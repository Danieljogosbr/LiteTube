using System;
using System.Diagnostics;
using System.IO;
using System.Net;
using System.Runtime.InteropServices;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Forms;

namespace LiteTube
{
    internal static class Program
    {
        private static Mutex _singleInstanceMutex;

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
                    string libPath = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "lib", asmName + ".dll");
                    if (File.Exists(libPath))
                    {
                        try { return System.Reflection.Assembly.LoadFrom(libPath); }
                        catch { return System.Reflection.Assembly.Load(File.ReadAllBytes(libPath)); }
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
            Logger.Info("APP", "Iniciando LiteTube...");

            // Inicializa e destrava o pool de rede global (32 conexões simultâneas, TLS 1.2 / 1.3, sem Nagle)
            App.InitializeNetworkPool();
            if (System.Windows.Application.Current == null)
            {
                new App { ShutdownMode = System.Windows.ShutdownMode.OnExplicitShutdown };
            }

            string backupDir = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "backup", "pre_wpf_redesign");
            if (Directory.Exists(backupDir))
            {
                Logger.Info("BACKUP", "Backup preventivo (backup/pre_wpf_redesign) verificado e confirmado com sucesso em: " + backupDir);
            }

            Application.SetUnhandledExceptionMode(UnhandledExceptionMode.CatchException);
            Application.ThreadException += (s, e) => HandleException(e.Exception, false);
            AppDomain.CurrentDomain.UnhandledException += (s, e) => HandleException(e.ExceptionObject as Exception, true);
            TaskScheduler.UnobservedTaskException += (s, e) =>
            {
                e.SetObserved();
                LogException(e.Exception);
            };

            bool createdNew;
            _singleInstanceMutex = new Mutex(true, "LiteTube_SingleInstance_Mutex_v2", out createdNew);
            if (!createdNew)
            {
                Logger.Warn("APP", "Instância duplicada detectada. Restaurando janela existente e repassando argumentos.");
                try
                {
                    string[] cliArgs = Environment.GetCommandLineArgs();
                    if (cliArgs.Length > 1 && !string.IsNullOrWhiteSpace(cliArgs[1]) && !cliArgs[1].StartsWith("--"))
                    {
                        try
                        {
                            HttpPipeline.Client.GetStringAsync("http://127.0.0.1:48125/play?url=" + Uri.EscapeDataString(cliArgs[1].Trim())).GetAwaiter().GetResult();
                        }
                        catch { }
                    }

                    Process current = Process.GetCurrentProcess();
                    foreach (Process proc in Process.GetProcessesByName(current.ProcessName))
                    {
                        if (proc.Id != current.Id && proc.MainWindowHandle != IntPtr.Zero)
                        {
                            ShowWindowAsync(proc.MainWindowHandle, SW_RESTORE);
                            SetForegroundWindow(proc.MainWindowHandle);
                            break;
                        }
                    }
                }
                catch { }
                return;
            }

            Application.EnableVisualStyles();
            Application.SetCompatibleTextRenderingDefault(false);
            MemoryOptimizer.TrimMemory();

            try
            {
                string oldExe = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "LiteTube.old.exe");
                if (File.Exists(oldExe)) File.Delete(oldExe);
            }
            catch { }

            InitializeFlyleafEngine();

            Logger.Info("APP", "Iniciando loop da interface gráfica (MainForm)...");
            var mainForm = new MainForm();
            string[] args = Environment.GetCommandLineArgs();
            for (int i = 1; i < args.Length; i++)
            {
                if (string.Equals(args[i], "--self-test", StringComparison.OrdinalIgnoreCase))
                {
                    mainForm.Shown += async (s, e) =>
                    {
                        await mainForm.RunSelfTestProtocolAsync();
                    };
                    break;
                }
            }
            Application.Run(mainForm);
            Logger.Info("APP", "LiteTube finalizado com sucesso.");
            GC.KeepAlive(_singleInstanceMutex);
        }

        private static void InitializeFlyleafEngine()
        {
            try
            {
                string ffmpegPath = FFmpegResolver.ResolveFFmpegDirectory();
                string pluginsPath = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "Plugins");

                FlyleafLib.Engine.Start(new FlyleafLib.EngineConfig()
                {
                    UIRefreshInterval = 30,
                    PluginsPath = Directory.Exists(pluginsPath) ? pluginsPath : ffmpegPath,
                    FFmpegPath = ffmpegPath
                });
                Logger.Info("FLYLEAF", "Motor FlyleafLib / Direct3D 11 inicializado com sucesso apontando para: " + ffmpegPath);
            }
            catch (Exception ex)
            {
                Logger.Error("FLYLEAF", "Falha ao inicializar o motor FlyleafLib", ex);
            }
        }

        private static void HandleException(Exception ex, bool isFatal)
        {
            LogException(ex);
            if (isFatal)
            {
                MessageBox.Show(
                    "Ocorreu um erro inesperado no LiteTube:\n\n" + (ex != null ? ex.Message : "Erro desconhecido") +
                    "\n\nOs detalhes foram gravados em 'data\\crash.log'. O aplicativo precisará ser reiniciado.",
                    "LiteTube - Falha Inesperada",
                    MessageBoxButtons.OK,
                    MessageBoxIcon.Error
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
                    DateTime.Now, ex.GetType().FullName, ex.ToString());
                File.AppendAllText(crashLog, entry);
            }
            catch { }
        }
    }
}
