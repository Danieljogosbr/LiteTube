using System;
using System.Collections.Concurrent;
using System.IO;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;

namespace LiteTube
{
    public static class Logger
    {
        private static readonly object _initLock = new object();
        private static readonly object _ioLock = new object();
        private static readonly ConcurrentQueue<string> _queue = new ConcurrentQueue<string>();
        private static readonly AutoResetEvent _signal = new AutoResetEvent(false);
        private const int MaxQueuedLines = 4096;

        private static string _logsDir;
        private static string _logFilePath;
        private static string _latestLogFilePath;
        private static bool _initialized = false;
        private static int _queuedCount = 0;
        private static Thread _writerThread;

        private static readonly Regex _rxSapisidHash = new Regex(
            @"SAPISIDHASH\s+[^\s""';,]+",
            RegexOptions.Compiled | RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);

        private static readonly Regex _rxSensitiveHeaders = new Regex(
            @"(?<=\b(?:Authorization|Cookie|Set-Cookie)\s*:\s*)[^\r\n]+",
            RegexOptions.Compiled | RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);

        private static readonly Regex _rxSensitiveCookies = new Regex(
            @"\b(__Secure-3PAPISID|__Secure-1PAPISID|__Secure-3PSID|__Secure-1PSID|__Secure-3PSIDCC|__Secure-1PSIDCC|SAPISID|APISID|HSID|SSID|SID|LOGIN_INFO)\s*=\s*[^;\s""'&\r\n]+",
            RegexOptions.Compiled | RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);

        public static string LogsDirectory { get { return _logsDir; } }
        public static string LogFilePath { get { return _logFilePath; } }
        public static string LatestLogFilePath { get { return _latestLogFilePath; } }

        public static void Init(string customDir = null)
        {
            lock (_initLock)
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

                    _writerThread = new Thread(WriterLoop)
                    {
                        IsBackground = true,
                        Name = "LiteTube_AsyncLogger",
                        Priority = ThreadPriority.BelowNormal
                    };
                    _writerThread.Start();

                    AppDomain.CurrentDomain.ProcessExit += (s, e) => Flush();

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
                    Console.WriteLine("[Logger Init Error]: " + ex.Message);
                }
            }
        }

        /// <summary>
        /// Mascara automaticamente tokens, cabeçalhos de autorização e cookies sensíveis da conta Google
        /// antes que qualquer mensagem seja persistida em disco.
        /// </summary>
        public static string SanitizeSensitiveData(string message)
        {
            if (string.IsNullOrEmpty(message)) return string.Empty;

            string safe = message;
            if (safe.IndexOf("SAPISIDHASH", StringComparison.OrdinalIgnoreCase) >= 0)
            {
                safe = _rxSapisidHash.Replace(safe, "SAPISIDHASH [REDACTED]");
            }
            if (safe.IndexOf("Authorization", StringComparison.OrdinalIgnoreCase) >= 0 ||
                safe.IndexOf("Cookie", StringComparison.OrdinalIgnoreCase) >= 0)
            {
                safe = _rxSensitiveHeaders.Replace(safe, "[REDACTED]");
            }
            if (safe.IndexOf("SID", StringComparison.OrdinalIgnoreCase) >= 0 ||
                safe.IndexOf("LOGIN_INFO", StringComparison.OrdinalIgnoreCase) >= 0)
            {
                safe = _rxSensitiveCookies.Replace(safe, "$1=[REDACTED]");
            }
            return safe;
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
            string fullMsg = message ?? string.Empty;
            if (ex != null)
            {
                fullMsg += "\r\nExceção: " + ex.GetType().FullName + ": " + ex.Message + "\r\nStack: " + ex.StackTrace;
            }
            WriteEntry(category, "ERROR", fullMsg);
        }

        public static void Flush()
        {
            if (!_initialized) return;
            DrainQueueToDisk();
        }

        private static void WriteEntry(string category, string level, string message)
        {
            if (!_initialized)
            {
                Init();
            }

            if (string.IsNullOrEmpty(_logFilePath)) return;

            string safeMessage = SanitizeSensitiveData(message);
            string line = string.Format("[{0:yyyy-MM-dd HH:mm:ss.fff}] [{1,-5}] [{2,-16}] {3}\r\n",
                DateTime.Now, level, category, safeMessage);

            if (Interlocked.Increment(ref _queuedCount) <= MaxQueuedLines)
            {
                _queue.Enqueue(line);
                try { _signal.Set(); } catch { }
            }
            else
            {
                Interlocked.Decrement(ref _queuedCount);
            }
        }

        private static void WriterLoop()
        {
            while (true)
            {
                try
                {
                    _signal.WaitOne(250);
                    DrainQueueToDisk();
                }
                catch { }
            }
        }

        private static void DrainQueueToDisk()
        {
            if (_queue.IsEmpty || string.IsNullOrEmpty(_logFilePath)) return;

            var sb = new StringBuilder(1024);
            while (_queue.TryDequeue(out string line))
            {
                Interlocked.Decrement(ref _queuedCount);
                sb.Append(line);
                if (sb.Length >= 65536) break;
            }

            if (sb.Length == 0) return;
            string batch = sb.ToString();

            lock (_ioLock)
            {
                try
                {
                    File.AppendAllText(_logFilePath, batch, Encoding.UTF8);
                }
                catch { }

                try
                {
                    if (!string.IsNullOrEmpty(_latestLogFilePath))
                    {
                        File.AppendAllText(_latestLogFilePath, batch, Encoding.UTF8);
                    }
                }
                catch { }
            }
        }
    }
}
