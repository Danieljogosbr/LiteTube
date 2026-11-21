using System;
using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;

namespace LiteTube
{
    internal static class Program
    {
        private static Mutex _singleInstanceMutex;
        public static readonly Stopwatch StartupStopwatch = Stopwatch.StartNew();
        public static Task EngineReadyTask { get; private set; } = Task.CompletedTask;

        [DllImport("shell32.dll", SetLastError = true)]
        private static extern int SetCurrentProcessExplicitAppUserModelID([MarshalAs(UnmanagedType.LPWStr)] string AppID);

        [DllImport("user32.dll")]
        private static extern bool SetForegroundWindow(IntPtr hWnd);

        [DllImport("user32.dll")]
        private static extern bool ShowWindowAsync(IntPtr hWnd, int nCmdShow);

        private const int SW_RESTORE = 9;

        [STAThread]
        private static void Main()
        {
            try { SetCurrentProcessExplicitAppUserModelID("LiteTube"); } catch { }

            AppDomain.CurrentDomain.AssemblyResolve += (s, resolveArgs) =>
            {
                try
                {
                    string asmName = new System.Reflection.AssemblyName(resolveArgs.Name).Name;
                    if (asmName == "AsyncImageLoader.WPF" || asmName == "AsyncImageLoader" || asmName == "LiteTube")
                    {
                        return typeof(Program).Assembly;
                    }
                    string rootPath = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, asmName + ".dll");
                    if (File.Exists(rootPath))
                    {
                        try { return System.Reflection.Assembly.LoadFrom(rootPath); }
                        catch { return System.Reflection.Assembly.Load(File.ReadAllBytes(rootPath)); }
                    }
                }
                catch { }
                return null;
            };

            Logger.Init();
            Logger.Info("APP", "Iniciando LiteTube (Arquitetura 100% WPF Puro)...");

            int orphanCookiesCleaned = CookieVault.CleanupOrphanedTempCookieFiles();
            if (orphanCookiesCleaned > 0)
            {
                Logger.Info("AUTH", string.Format("Removidos {0} arquivo(s) temporário(s) órfão(s) de cookies do %TEMP%.", orphanCookiesCleaned));
            }

            App.InitializeNetworkPool();

            AppDomain.CurrentDomain.UnhandledException += (s, e) => HandleException(e.ExceptionObject as Exception, true);
            TaskScheduler.UnobservedTaskException += (s, e) =>
            {
                e.SetObserved();
                LogException(e.Exception);
            };

            bool isSelfTest = false;
            string[] checkArgs = Environment.GetCommandLineArgs();
            for (int i = 1; i < checkArgs.Length; i++)
            {
                if (string.Equals(checkArgs[i], "--self-test", StringComparison.OrdinalIgnoreCase))
                {
                    isSelfTest = true;
                    break;
                }
            }
            _isSelfTestMode = isSelfTest;

            string startupExternalUrl = FindExternalUrlArg(checkArgs);

            bool createdNew;
            _singleInstanceMutex = new Mutex(true, "LiteTube_SingleInstance_Mutex_v2", out createdNew);
            if (!createdNew && !isSelfTest)
            {
                if (!string.IsNullOrEmpty(startupExternalUrl))
                {
                    try
                    {
                        ExternalLinkServer.SendCommand("PLAY|" + startupExternalUrl, 2500);
                    }
                    catch { }
                }

                try
                {
                    Process current = Process.GetCurrentProcess();
                    foreach (Process p in Process.GetProcessesByName(current.ProcessName))
                    {
                        if (p.Id != current.Id && p.MainWindowHandle != IntPtr.Zero)
                        {
                            ShowWindowAsync(p.MainWindowHandle, SW_RESTORE);
                            SetForegroundWindow(p.MainWindowHandle);
                            break;
                        }
                    }
                }
                catch { }
                return;
            }

            ExternalLinkServer.EnsureProtocolRegistered();

            // CRÍTICO: Instancia App (System.Windows.Application) ANTES de FlyleafLib.Engine.StartAsync,
            // pois o motor FlyleafLib cria/acessa Application.Current internamente ao inicializar.
            var app = (Application.Current as App) ?? new App();
            app.ShutdownMode = ShutdownMode.OnMainWindowClose;
            app.DispatcherUnhandledException += (s, e) =>
            {
                LogException(e.Exception);
                e.Handled = true;
            };

            EngineReadyTask = StartFlyleafEngineNonBlocking();
            App.PrewarmBackgroundServices();

            Logger.Info("APP", string.Format("Iniciando loop da interface WPF (WpfMainWindow em {0}ms)...",
                StartupStopwatch.ElapsedMilliseconds));

            var mainWindow = new WpfMainWindow(isSelfTest, startupExternalUrl);
            app.Run(mainWindow);

            CookieVault.CleanupOrphanedTempCookieFiles();
            Logger.Info("APP", "LiteTube finalizado com sucesso.");
            Logger.Flush();
            GC.KeepAlive(_singleInstanceMutex);
        }

        private static string FindExternalUrlArg(string[] args)
        {
            if (args == null || args.Length <= 1) return null;
            for (int i = 1; i < args.Length; i++)
            {
                string arg = args[i];
                if (!string.IsNullOrWhiteSpace(arg) && !arg.StartsWith("--", StringComparison.Ordinal))
                {
                    return arg.Trim();
                }
            }
            return null;
        }

        private static Task StartFlyleafEngineNonBlocking()
        {
            try
            {
                string ffmpegPath = FFmpegResolver.ResolveFFmpegDirectory();

                FlyleafLib.Engine.StartAsync(new FlyleafLib.EngineConfig()
                {
                    UIRefresh = true,
                    UIRefreshInterval = 50,
                    UICurTimePerSecond = false,
                    PluginsPath = ffmpegPath,
                    FFmpegPath = ffmpegPath
                });

                return Task.Run(async () =>
                {
                    var swWait = Stopwatch.StartNew();
                    while (!FlyleafLib.Engine.IsLoaded && swWait.ElapsedMilliseconds < 15000)
                    {
                        await Task.Delay(15).ConfigureAwait(false);
                    }
                    Logger.Info("FLYLEAF", string.Format("Motor FlyleafLib / Direct3D 11 inicializado em {0}ms (StartAsync em background | IsLoaded={1}) apontando para: {2}",
                        StartupStopwatch.ElapsedMilliseconds, FlyleafLib.Engine.IsLoaded, ffmpegPath));
                });
            }
            catch (Exception ex)
            {
                Logger.Error("FLYLEAF", "Falha ao inicializar o motor FlyleafLib", ex);
                return Task.CompletedTask;
            }
        }

        private static bool _isSelfTestMode = false;

        private static void HandleException(Exception ex, bool isFatal)
        {
            LogException(ex);
            Logger.Flush();
            if (isFatal && !_isSelfTestMode)
            {
                MessageBox.Show(
                    "Ocorreu um erro inesperado no LiteTube:\n\n" + (ex != null ? ex.Message : "Erro desconhecido") +
                    "\n\nOs detalhes foram gravados em 'data\\crash.log'. O aplicativo precisará ser reiniciado.",
                    "LiteTube - Falha Inesperada",
                    MessageBoxButton.OK,
                    MessageBoxImage.Error
                );
            }
        }

        private static void LogException(Exception ex)
        {
            if (ex == null) return;
            try
            {
                Logger.Error("CRASH", "Exceção não tratada capturada", ex);

                string dataDir = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "data");
                if (!Directory.Exists(dataDir)) Directory.CreateDirectory(dataDir);
                string crashLog = Path.Combine(dataDir, "crash.log");
                string entry = string.Format("[{0:yyyy-MM-dd HH:mm:ss}] {1}\r\n{2}\r\n----------------------------------------\r\n",
                    DateTime.Now, ex.GetType().FullName, Logger.SanitizeSensitiveData(ex.ToString()));
                File.AppendAllText(crashLog, entry);
            }
            catch { }
        }
    }
}
