using System;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Forms;

namespace LiteTube
{
    internal static class Program
    {
        private static Mutex _singleInstanceMutex;

        [STAThread]
        private static void Main()
        {
            Logger.Init();
            Logger.Info("APP", "Iniciando LiteTube...");

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
                Logger.Warn("APP", "Instância duplicada detectada. Encerrando.");
                MessageBox.Show(
                    "O LiteTube já está aberto (verifique a bandeja do sistema perto do relógio)!",
                    "LiteTube",
                    MessageBoxButtons.OK,
                    MessageBoxIcon.Information
                );
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

            Logger.Info("APP", "Iniciando loop da interface gráfica (MainForm)...");
            Application.Run(new MainForm());
            Logger.Info("APP", "LiteTube finalizado com sucesso.");
            GC.KeepAlive(_singleInstanceMutex);
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
