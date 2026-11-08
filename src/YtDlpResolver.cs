using System;
using System.Diagnostics;
using System.IO;
using System.Threading.Tasks;

namespace LiteTube
{
    /// <summary>
    /// Centralized resolver and lifecycle manager for yt-dlp executable.
    /// Manages path resolution, availability checks, and background pre-warming for instant cold start.
    /// </summary>
    public static class YtDlpResolver
    {
        private static string _cachedBinaryPath;
        private static readonly object _lock = new object();
        private static bool _isPrewarmed = false;

        /// <summary>
        /// Locates the yt-dlp.exe executable across standard project and runtime paths.
        /// </summary>
        public static string GetYtDlpBinaryPath()
        {
            if (_cachedBinaryPath != null && File.Exists(_cachedBinaryPath))
            {
                return _cachedBinaryPath;
            }

            lock (_lock)
            {
                if (_cachedBinaryPath != null && File.Exists(_cachedBinaryPath))
                {
                    return _cachedBinaryPath;
                }

                string baseDir = AppDomain.CurrentDomain.BaseDirectory;
                string appContextDir = AppContext.BaseDirectory;
                string currentDir = Directory.GetCurrentDirectory();

                string[] candidatePaths = new string[]
                {
                    Path.Combine(baseDir, "bin", "yt-dlp.exe"),
                    Path.Combine(baseDir, "yt-dlp.exe"),
                    Path.Combine(appContextDir, "bin", "yt-dlp.exe"),
                    Path.Combine(appContextDir, "yt-dlp.exe"),
                    Path.Combine(currentDir, "bin", "yt-dlp.exe"),
                    Path.Combine(currentDir, "yt-dlp.exe"),
                    Path.GetFullPath(Path.Combine(baseDir, "..", "..", "..", "..", "bin", "yt-dlp.exe"))
                };

                foreach (string path in candidatePaths)
                {
                    try
                    {
                        if (File.Exists(path))
                        {
                            _cachedBinaryPath = path;
                            return _cachedBinaryPath;
                        }
                    }
                    catch { }
                }

                // Fallback padrão relativo
                _cachedBinaryPath = Path.Combine(baseDir, "bin", "yt-dlp.exe");
                return _cachedBinaryPath;
            }
        }

        /// <summary>
        /// Gets the folder where yt-dlp.exe resides.
        /// </summary>
        public static string GetYtDlpDirectory()
        {
            string binPath = GetYtDlpBinaryPath();
            return Path.GetDirectoryName(binPath) ?? Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "bin");
        }

        /// <summary>
        /// Checks whether the yt-dlp binary is present on disk.
        /// </summary>
        public static bool IsAvailable()
        {
            string path = GetYtDlpBinaryPath();
            return !string.IsNullOrEmpty(path) && File.Exists(path);
        }

        /// <summary>
        /// Executes a fast, non-blocking background check (--version) to warm up Windows Standby Cache
        /// and Python runtime libraries from disk into RAM, eliminating first-playback cold start lag.
        /// </summary>
        public static Task PrewarmAsync()
        {
            if (_isPrewarmed) return Task.CompletedTask;

            return Task.Run(() =>
            {
                try
                {
                    string path = GetYtDlpBinaryPath();
                    if (!File.Exists(path))
                    {
                        Logger.Warn("PREWARM", "yt-dlp não encontrado para pré-aquecimento: " + path);
                        return;
                    }

                    var sw = Stopwatch.StartNew();
                    using var proc = new Process
                    {
                        StartInfo = new ProcessStartInfo
                        {
                            FileName = path,
                            Arguments = "--version",
                            UseShellExecute = false,
                            CreateNoWindow = true,
                            RedirectStandardOutput = true,
                            RedirectStandardError = true
                        }
                    };

                    proc.Start();
                    bool exited = proc.WaitForExit(3000);
                    sw.Stop();

                    if (exited)
                    {
                        _isPrewarmed = true;
                        string ver = proc.StandardOutput.ReadToEnd().Trim();
                        Logger.Info("PREWARM", string.Format("⚡ yt-dlp ({0}) pré-aquecido no Standby Cache em {1}ms.", ver, sw.ElapsedMilliseconds));
                    }
                    else
                    {
                        try { proc.Kill(); } catch { }
                        Logger.Warn("PREWARM", "Timeout ao pré-aquecer yt-dlp (processo finalizado com segurança).");
                    }
                }
                catch (Exception ex)
                {
                    Logger.Warn("PREWARM", "Falha segura no pré-aquecimento do yt-dlp: " + ex.Message);
                }
            });
        }
    }
}
