using System;
using System.IO;

namespace LiteTube
{
    /// <summary>
    /// Centralized resolver for native FFmpeg libraries (FlyleafLib) and executable binaries (yt-dlp).
    /// Robustly scans application directories, deployment bundles, and PATH.
    /// </summary>
    public static class FFmpegResolver
    {
        private static string _cachedFFmpegDir;
        private static string _cachedFFmpegExePath;
        private static readonly object _lock = new object();

        /// <summary>
        /// Locates the directory containing native FFmpeg libraries (avcodec-*.dll, avformat-*.dll)
        /// required by FlyleafLib Direct3D 11 engine.
        /// </summary>
        public static string ResolveFFmpegDirectory()
        {
            if (_cachedFFmpegDir != null && Directory.Exists(_cachedFFmpegDir))
            {
                return _cachedFFmpegDir;
            }

            lock (_lock)
            {
                if (_cachedFFmpegDir != null && Directory.Exists(_cachedFFmpegDir))
                {
                    return _cachedFFmpegDir;
                }

                string baseDir = AppDomain.CurrentDomain.BaseDirectory;
                string appContextDir = AppContext.BaseDirectory;
                string currentDir = Directory.GetCurrentDirectory();

                string[] candidateDirs = new string[]
                {
                    Path.Combine(baseDir, "FFmpeg"),
                    Path.Combine(baseDir, "FFmpeg", "x64"),
                    Path.Combine(appContextDir, "FFmpeg"),
                    Path.Combine(appContextDir, "FFmpeg", "x64"),
                    Path.Combine(currentDir, "FFmpeg"),
                    Path.Combine(baseDir, "bin", "FFmpeg"),
                    Path.GetFullPath(Path.Combine(baseDir, "..", "..", "..", "..", "FFmpeg")),
                    Path.GetFullPath(Path.Combine(baseDir, "..", "FFmpeg")),
                    Path.Combine(baseDir, "Plugins")
                };

                foreach (string dir in candidateDirs)
                {
                    try
                    {
                        if (Directory.Exists(dir))
                        {
                            // Valida a presença de avcodec DLL no diretório
                            string[] avcodecs = Directory.GetFiles(dir, "avcodec*.dll");
                            if (avcodecs != null && avcodecs.Length > 0)
                            {
                                _cachedFFmpegDir = dir;
                                Logger.Info("FFMPEG", "Diretório nativo FFmpeg localizado com sucesso: " + dir);
                                return _cachedFFmpegDir;
                            }
                        }
                    }
                    catch { }
                }

                // Fallback canônico
                _cachedFFmpegDir = Path.Combine(baseDir, "FFmpeg");
                return _cachedFFmpegDir;
            }
        }

        /// <summary>
        /// Locates the ffmpeg.exe CLI executable if present on disk or in system PATH.
        /// Returns null if not installed (FlyleafLib uses native DLLs directly).
        /// </summary>
        public static string ResolveFFmpegExecutablePath()
        {
            if (_cachedFFmpegExePath != null && File.Exists(_cachedFFmpegExePath))
            {
                return _cachedFFmpegExePath;
            }

            lock (_lock)
            {
                if (_cachedFFmpegExePath != null && File.Exists(_cachedFFmpegExePath))
                {
                    return _cachedFFmpegExePath;
                }

                string baseDir = AppDomain.CurrentDomain.BaseDirectory;
                string binDir = Path.Combine(baseDir, "bin");
                string ffmpegDir = ResolveFFmpegDirectory();

                string[] candidateFiles = new string[]
                {
                    Path.Combine(binDir, "ffmpeg.exe"),
                    Path.Combine(ffmpegDir, "ffmpeg.exe"),
                    Path.Combine(baseDir, "ffmpeg.exe")
                };

                foreach (string path in candidateFiles)
                {
                    try
                    {
                        if (File.Exists(path))
                        {
                            _cachedFFmpegExePath = path;
                            return _cachedFFmpegExePath;
                        }
                    }
                    catch { }
                }

                // Procura no PATH do sistema
                try
                {
                    string pathEnv = Environment.GetEnvironmentVariable("PATH");
                    if (!string.IsNullOrEmpty(pathEnv))
                    {
                        foreach (string p in pathEnv.Split(Path.PathSeparator))
                        {
                            string candidate = Path.Combine(p.Trim(), "ffmpeg.exe");
                            if (File.Exists(candidate))
                            {
                                _cachedFFmpegExePath = candidate;
                                return _cachedFFmpegExePath;
                            }
                        }
                    }
                }
                catch { }

                return null;
            }
        }
    }
}
