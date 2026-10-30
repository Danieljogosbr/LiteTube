using System;
using System.IO;
using System.Text;
using System.Threading;

namespace LiteTube
{
    public static class Logger
    {
        private static readonly object _lock = new object();
        private static string _logsDir;
        private static string _logFilePath;
        private static string _latestLogFilePath;
        private static bool _initialized = false;

        public static string LogsDirectory { get { return _logsDir; } }
        public static string LogFilePath { get { return _logFilePath; } }
        public static string LatestLogFilePath { get { return _latestLogFilePath; } }

        public static void Init(string customDir = null)
        {
            lock (_lock)
            {
                if (_initialized) return;

                try
                {
                    string baseDir = AppDomain.CurrentDomain.BaseDirectory;
                    _logsDir = !string.IsNullOrEmpty(customDir)
                        ? customDir
                        : Path.Combine(baseDir, "logs");

                    if (!Directory.Exists(_logsDir))
                    {
                        Directory.CreateDirectory(_logsDir);
                    }

                    _logFilePath = Path.Combine(_logsDir, string.Format("litetube_{0:yyyy-MM-dd}.log", DateTime.Now));
                    _latestLogFilePath = Path.Combine(_logsDir, "litetube_latest.log");

                    try
                    {
                        // Overwrite litetube_latest.log so it always shows ONLY the current run
                        File.WriteAllText(_latestLogFilePath, "", Encoding.UTF8);
                    }
                    catch { }

                    // Check if daily file exceeds 15 MB; if so, roll it
                    if (File.Exists(_logFilePath) && new FileInfo(_logFilePath).Length > 15 * 1024 * 1024)
                    {
                        string rolled = Path.Combine(_logsDir, string.Format("litetube_{0:yyyy-MM-dd_HHmmss}.log", DateTime.Now));
                        try { File.Move(_logFilePath, rolled); } catch { }
                    }

                    _initialized = true;

                    WriteEntry("LOGGER", "INFO", "================================================================================");
                    WriteEntry("LOGGER", "INFO", string.Format("LiteTube iniciado em {0:yyyy-MM-dd HH:mm:ss.fff}", DateTime.Now));
                    WriteEntry("LOGGER", "INFO", string.Format("Diretório base: {0}", baseDir));
                    WriteEntry("LOGGER", "INFO", string.Format("Diretório de logs: {0}", _logsDir));
                    WriteEntry("LOGGER", "INFO", string.Format("OS: {0} ({1})", Environment.OSVersion, Environment.Is64BitOperatingSystem ? "64-bit" : "32-bit"));
                    WriteEntry("LOGGER", "INFO", string.Format("CLR: {0}", Environment.Version));
                    WriteEntry("LOGGER", "INFO", "================================================================================");
                }
                catch (Exception ex)
                {
                    // Fallback to console if file system fails
                    Console.WriteLine("[Logger Init Error]: " + ex.Message);
                }
            }
        }

        public static void Info(string category, string message)
        {
            WriteEntry(category, "INFO", message);
        }

        public static void Warn(string category, string message)
        {
            WriteEntry(category, "WARN", message);
        }

        public static void Error(string category, string message, Exception ex = null)
        {
            string fullMsg = message;
            if (ex != null)
            {
                fullMsg += "\r\nExceção: " + ex.GetType().FullName + ": " + ex.Message + "\r\nStack: " + ex.StackTrace;
            }
            WriteEntry(category, "ERROR", fullMsg);
        }

        public static void Debug(string category, string message)
        {
#if DEBUG
            WriteEntry(category, "DEBUG", message);
#else
            WriteEntry(category, "DEBUG", message);
#endif
        }

        private static void WriteEntry(string category, string level, string message)
        {
            if (!_initialized)
            {
                Init();
            }

            if (string.IsNullOrEmpty(_logFilePath)) return;

            string line = string.Format("[{0:yyyy-MM-dd HH:mm:ss.fff}] [{1,-5}] [{2,-16}] {3}\r\n",
                DateTime.Now, level, category, message);

            lock (_lock)
            {
                try
                {
                    File.AppendAllText(_logFilePath, line, Encoding.UTF8);
                }
                catch { }

                try
                {
                    if (!string.IsNullOrEmpty(_latestLogFilePath))
                    {
                        File.AppendAllText(_latestLogFilePath, line, Encoding.UTF8);
                    }
                }
                catch { }
            }
        }
    }
}
