using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace LiteTube
{
    /// <summary>
    /// Gerencia a resolução de fluxos diretos de vídeo e áudio do YouTube para o motor FlyleafLib (FFmpeg / Direct3D 11).
    /// Utiliza o yt-dlp com estratégia de fallback multi-cliente (Cookies -> Deno JS Runtime -> VisionOS)
    /// para obter as URLs diretas de googlevideo.com, sem depender de intermediários ou proxies de loopback local.
    /// </summary>
    public class ProgressiveVideoSession : IDisposable
    {
        private readonly TrackItem _video;
        private readonly int _height;
        private readonly string _binDir;
        private readonly string _ytDlpPath;
        private readonly string _browserForCookies;
        private readonly string _cookiesFilePath;

        private readonly CancellationTokenSource _sessionCts = new CancellationTokenSource();
        private string _resolvedVideoUrl = null;
        private string _resolvedAudioUrl = null;
        private string _activeCodecName = "Direct3D 11 / FFmpeg";
        private string _lastResolveError = null;
        private bool _isLiveStream = false;
        private bool _isVideoUnavailable = false;
        private readonly SemaphoreSlim _resolveLock = new SemaphoreSlim(1, 1);
        private double _knownDurationSeconds = 0;

        public ProgressiveVideoSession(TrackItem video, int height, string binDir, string browserForCookies = null, string cookiesFilePath = null)
        {
            _video = video;
            _knownDurationSeconds = (video != null && video.DurationSeconds > 0) ? video.DurationSeconds : 240;
            _height = height > 0 ? height : 720;
            _ytDlpPath = (!string.IsNullOrEmpty(binDir) && File.Exists(Path.Combine(binDir, "yt-dlp.exe")))
                ? Path.Combine(binDir, "yt-dlp.exe")
                : YtDlpResolver.GetYtDlpBinaryPath();
            _binDir = Path.GetDirectoryName(_ytDlpPath) ?? binDir;
            _browserForCookies = browserForCookies;
            _cookiesFilePath = cookiesFilePath;
        }

        public string LastResolveError { get { return _lastResolveError; } }
        public double KnownDurationSeconds { get { return _knownDurationSeconds; } }
        public string ActiveCodecName { get { return _activeCodecName; } }
        public bool IsLiveStream { get { return _isLiveStream; } }
        public bool IsVideoUnavailable { get { return _isVideoUnavailable; } }

        public async Task<string[]> ResolveStreamUrlsAsync(CancellationToken token)
        {
            bool ok = await EnsureStreamUrlsResolvedAsync(token);
            if (!ok || string.IsNullOrEmpty(_resolvedVideoUrl)) return null;

            return new string[] { _resolvedVideoUrl, _resolvedAudioUrl, _activeCodecName };
        }

        public static (string videoUrl, string audioUrl) ResolveStreamUrls(string videoId, int targetHeight)
        {
            return ResolveStreamUrls(videoId, targetHeight, null, null, null);
        }

        public static (string videoUrl, string audioUrl) ResolveStreamUrls(string videoId, int targetHeight, string binDir, string browserForCookies = null, string cookiesFilePath = null)
        {
            if (string.IsNullOrEmpty(videoId)) return (null, null);

            if (string.IsNullOrEmpty(binDir))
            {
                binDir = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "bin");
            }

            if (string.IsNullOrEmpty(browserForCookies) && string.IsNullOrEmpty(cookiesFilePath))
            {
                try
                {
                    var cfg = AppConfig.Load();
                    if (cfg != null)
                    {
                        browserForCookies = cfg.BrowserForCookies;
                        cookiesFilePath = cfg.CookiesFilePath;
                    }
                }
                catch { }
            }

            var dummyItem = new TrackItem { Id = videoId, Title = "QualitySwitch" };
            using (var session = new ProgressiveVideoSession(dummyItem, targetHeight, binDir, browserForCookies, cookiesFilePath))
            {
                using (var cts = new CancellationTokenSource(TimeSpan.FromSeconds(30)))
                {
                    var urls = session.ResolveStreamUrlsAsync(cts.Token).GetAwaiter().GetResult();
                    if (urls != null && urls.Length > 0 && !string.IsNullOrEmpty(urls[0]))
                    {
                        string v = urls[0];
                        string a = urls.Length > 1 ? urls[1] : null;
                        return (v, a);
                    }
                }
            }

            return (null, null);
        }

        public static async Task<(string videoUrl, string audioUrl)> ResolveStreamUrlsAsync(string videoId, int targetHeight, string binDir = null, string browserForCookies = null, string cookiesFilePath = null, CancellationToken token = default)
        {
            if (string.IsNullOrEmpty(videoId)) return (null, null);

            if (string.IsNullOrEmpty(binDir))
            {
                binDir = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "bin");
            }

            if (string.IsNullOrEmpty(browserForCookies) && string.IsNullOrEmpty(cookiesFilePath))
            {
                try
                {
                    var cfg = AppConfig.Load();
                    if (cfg != null)
                    {
                        browserForCookies = cfg.BrowserForCookies;
                        cookiesFilePath = cfg.CookiesFilePath;
                    }
                }
                catch { }
            }

            var dummyItem = new TrackItem { Id = videoId, Title = "QualitySwitch" };
            using (var session = new ProgressiveVideoSession(dummyItem, targetHeight, binDir, browserForCookies, cookiesFilePath))
            {
                using (var linkedCts = CancellationTokenSource.CreateLinkedTokenSource(token))
                {
                    linkedCts.CancelAfter(TimeSpan.FromSeconds(30));
                    var urls = await session.ResolveStreamUrlsAsync(linkedCts.Token);
                    if (urls != null && urls.Length > 0 && !string.IsNullOrEmpty(urls[0]))
                    {
                        string v = urls[0];
                        string a = urls.Length > 1 ? urls[1] : null;
                        return (v, a);
                    }
                }
            }

            return (null, null);
        }

        private async Task<bool> EnsureStreamUrlsResolvedAsync(CancellationToken token)
        {
            if (!string.IsNullOrEmpty(_resolvedVideoUrl))
                return true;

            await _resolveLock.WaitAsync(token);
            var sw = Stopwatch.StartNew();
            try
            {
                if (!string.IsNullOrEmpty(_resolvedVideoUrl))
                    return true;

                Logger.Info("STREAM", string.Format("EnsureStreamUrlsResolvedAsync iniciando: vidId={0}, height={1}p",
                    _video != null ? _video.Id : "?", _height));

                string videoUrl = "https://www.youtube.com/watch?v=" + (_video != null ? _video.Id : "");
                string formatArg = string.Format("bestvideo[height<={0}]+bestaudio/best[height<={0}]", _height);
                string sortArg = string.Format("res:{0},vcodec:av01,vcodec:vp9.2,vcodec:vp9,vcodec:h264,acodec:opus,acodec:m4a", _height);

                string denoPath = Path.Combine(_binDir, "deno.exe");
                string jsArg = File.Exists(denoPath) ? string.Format("--js-runtimes \"deno:{0}\" ", denoPath) : "";
                string cookieArg = GetCookieArg();
                string[] urls = null;

                // Tentativa 1 (Primária): Cookies configurados do navegador/arquivo com player_skip=hls
                if (!token.IsCancellationRequested)
                {
                    string attempt1Args = (!string.IsNullOrEmpty(cookieArg) ? cookieArg : "") + jsArg + "--extractor-args \"youtube:player_skip=hls\" ";
                    Logger.Info("STREAM", "Tentativa 1 (Primária): yt-dlp com player_skip=hls e cookies...");
                    urls = await Task.Run(() => RunYtDlpGetUrls(videoUrl, formatArg, sortArg, attempt1Args, token));
                }

                // Tentativa 2: Cliente Android (muda assinatura de bot)
                if ((urls == null || urls.Length == 0) && !_isVideoUnavailable && !token.IsCancellationRequested)
                {
                    Logger.Warn("STREAM", "Tentativa 1 falhou. Tentativa 2: yt-dlp com cliente android...");
                    string attempt2Args = jsArg + "--extractor-args \"youtube:player_client=android\" ";
                    urls = await Task.Run(() => RunYtDlpGetUrls(videoUrl, formatArg, sortArg, attempt2Args, token));
                }

                // Tentativa 3: Cliente iOS (excelente suporte para Live Streams HLS e formatos diretos)
                if ((urls == null || urls.Length == 0) && !_isVideoUnavailable && !token.IsCancellationRequested)
                {
                    Logger.Warn("STREAM", "Tentativa 2 falhou. Tentativa 3: yt-dlp com cliente ios...");
                    string attempt3Args = jsArg + "--extractor-args \"youtube:player_client=ios\" ";
                    urls = await Task.Run(() => RunYtDlpGetUrls(videoUrl, formatArg, sortArg, attempt3Args, token));
                }

                // Tentativa 4: Cliente Web Padrão / Live Stream HLS (sem player_skip=hls)
                if ((urls == null || urls.Length == 0) && !_isVideoUnavailable && !token.IsCancellationRequested)
                {
                    Logger.Warn("STREAM", "Tentativa 3 falhou. Tentativa 4: yt-dlp fallback Web / Live Stream HLS...");
                    string liveFormatArg = string.Format("bestvideo[height<={0}]+bestaudio/best[height<={0}]/best", _height);
                    string attempt4Args = (!string.IsNullOrEmpty(cookieArg) ? cookieArg : "") + jsArg;
                    urls = await Task.Run(() => RunYtDlpGetUrls(videoUrl, liveFormatArg, sortArg, attempt4Args, token));
                }

                if (urls != null && urls.Length > 0 && !string.IsNullOrEmpty(urls[0]))
                {
                    _resolvedVideoUrl = urls[0];
                    _resolvedAudioUrl = urls.Length > 1 ? urls[1] : null;

                    if (_resolvedVideoUrl.IndexOf(".m3u8", StringComparison.OrdinalIgnoreCase) >= 0 ||
                        _resolvedVideoUrl.IndexOf("manifest.googlevideo.com", StringComparison.OrdinalIgnoreCase) >= 0)
                    {
                        _isLiveStream = true;
                    }

                    _activeCodecName = DetectCodecFromUrls(_resolvedVideoUrl, _resolvedAudioUrl);
                    int vItag = ExtractItag(_resolvedVideoUrl);
                    int aItag = ExtractItag(_resolvedAudioUrl);

                    Logger.Info("STREAM", string.Format("Fluxos diretos obtidos em {0}ms: Video='{1}' (itag={2}), Audio='{3}' (itag={4}), Codec='{5}', Live={6}",
                        sw.ElapsedMilliseconds,
                        _resolvedVideoUrl.Substring(0, Math.Min(60, _resolvedVideoUrl.Length)) + "...",
                        vItag > 0 ? vItag.ToString() : "desconhecido",
                        _resolvedAudioUrl != null ? (_resolvedAudioUrl.Substring(0, Math.Min(60, _resolvedAudioUrl.Length)) + "...") : "embutido",
                        aItag > 0 ? aItag.ToString() : "desconhecido",
                        _activeCodecName,
                        _isLiveStream));

                    return true;
                }

                if (!_isVideoUnavailable)
                {
                    var _ = YtDlpResolver.EnsureUpdatedInBackgroundAsync(force: true);
                }

                Logger.Error("STREAM", string.Format("Falha definitiva ao obter URLs com yt-dlp após {0}ms (Unavailable={1}).", sw.ElapsedMilliseconds, _isVideoUnavailable));
                return false;
            }
            finally
            {
                _resolveLock.Release();
            }
        }

        private string GetCookieArg()
        {
            if (!string.IsNullOrEmpty(_cookiesFilePath) && File.Exists(_cookiesFilePath))
            {
                return string.Format("--cookies \"{0}\" ", _cookiesFilePath);
            }
            if (!string.IsNullOrEmpty(_browserForCookies) && _browserForCookies != "none")
            {
                return string.Format("--cookies-from-browser {0} ", _browserForCookies);
            }
            return "";
        }

        private string[] RunYtDlpGetUrls(string videoUrl, string formatArg, string sortArg, string extraArgs, CancellationToken token)
        {
            if (!File.Exists(_ytDlpPath))
            {
                _lastResolveError = "Executável yt-dlp.exe não encontrado em bin/.";
                return null;
            }

            try
            {
                string arguments = string.Format("-g -f \"{0}\" -S \"{1}\" --format-sort-force {2}\"{3}\"",
                    formatArg, sortArg, extraArgs ?? "", videoUrl);

                var psi = new ProcessStartInfo
                {
                    FileName = _ytDlpPath,
                    Arguments = arguments,
                    UseShellExecute = false,
                    RedirectStandardOutput = true,
                    RedirectStandardError = true,
                    CreateNoWindow = true,
                    StandardOutputEncoding = Encoding.UTF8
                };

                var lines = new List<string>();
                var errLines = new List<string>();

                using (var proc = new Process())
                {
                    proc.StartInfo = psi;
                    proc.OutputDataReceived += (s, e) =>
                    {
                        if (!string.IsNullOrEmpty(e.Data))
                        {
                            string l = e.Data.Trim();
                            if (l.StartsWith("http", StringComparison.OrdinalIgnoreCase))
                            {
                                lock (lines) { lines.Add(l); }
                            }
                        }
                    };
                    proc.ErrorDataReceived += (s, e) =>
                    {
                        if (!string.IsNullOrEmpty(e.Data))
                        {
                            lock (errLines) { errLines.Add(e.Data.Trim()); }
                        }
                    };

                    if (!proc.Start()) return null;
                    proc.BeginOutputReadLine();
                    proc.BeginErrorReadLine();

                    var sw = Stopwatch.StartNew();
                    while (!proc.HasExited)
                    {
                        if (token.IsCancellationRequested || sw.ElapsedMilliseconds > 30000)
                        {
                            SafeKillProcess(proc);
                            return null;
                        }
                        Thread.Sleep(30);
                    }

                    lock (lines)
                    {
                        if (lines.Count > 0) return lines.ToArray();
                    }
                    lock (errLines)
                    {
                        if (errLines.Count > 0)
                        {
                            string errJoined = string.Join(" | ", errLines);
                            Logger.Warn("YTDLP", "yt-dlp stderr: " + errJoined);
                            if (errJoined.IndexOf("Join this channel", StringComparison.OrdinalIgnoreCase) >= 0 ||
                                errJoined.IndexOf("members-only", StringComparison.OrdinalIgnoreCase) >= 0)
                            {
                                _lastResolveError = "Vídeo exclusivo para membros do canal (requer assinatura no YouTube).";
                                _isVideoUnavailable = true;
                            }
                            else if (errJoined.IndexOf("confirm your age", StringComparison.OrdinalIgnoreCase) >= 0 ||
                                     errJoined.IndexOf("age-restricted", StringComparison.OrdinalIgnoreCase) >= 0)
                            {
                                _lastResolveError = "Vídeo com restrição de idade no YouTube.";
                                _isVideoUnavailable = string.IsNullOrEmpty(GetCookieArg());
                            }
                            else if (errJoined.IndexOf("Private video", StringComparison.OrdinalIgnoreCase) >= 0)
                            {
                                _lastResolveError = "Este vídeo é privado no YouTube.";
                                _isVideoUnavailable = true;
                            }
                            else if (errJoined.IndexOf("Premieres in", StringComparison.OrdinalIgnoreCase) >= 0 ||
                                     errJoined.IndexOf("live event will begin", StringComparison.OrdinalIgnoreCase) >= 0)
                            {
                                _lastResolveError = "Esta estreia ou transmissão ao vivo ainda não começou.";
                                _isVideoUnavailable = true;
                            }
                            else if (errJoined.IndexOf("not available in your country", StringComparison.OrdinalIgnoreCase) >= 0)
                            {
                                _lastResolveError = "Este vídeo não está disponível no seu país.";
                                _isVideoUnavailable = true;
                            }
                            else if (errJoined.IndexOf("Video unavailable", StringComparison.OrdinalIgnoreCase) >= 0 ||
                                     errJoined.IndexOf("This video has been removed", StringComparison.OrdinalIgnoreCase) >= 0 ||
                                     errJoined.IndexOf("This video is not available", StringComparison.OrdinalIgnoreCase) >= 0)
                            {
                                _lastResolveError = "Vídeo indisponível ou removido do YouTube.";
                                _isVideoUnavailable = true;
                            }
                        }
                    }
                    return null;
                }
            }
            catch (Exception ex)
            {
                Logger.Error("YTDLP", "RunYtDlpGetUrls exceção", ex);
                return null;
            }
        }

        public static string DetectCodecFromUrls(string videoUrl, string audioUrl)
        {
            if (string.IsNullOrEmpty(videoUrl)) return "Direct3D 11 / FFmpeg";

            if (videoUrl.IndexOf(".m3u8", StringComparison.OrdinalIgnoreCase) >= 0 ||
                videoUrl.IndexOf("manifest.googlevideo.com", StringComparison.OrdinalIgnoreCase) >= 0)
            {
                return "LIVE HLS (Direct3D 11)";
            }

            int videoItag = ExtractItag(videoUrl);
            int audioItag = ExtractItag(audioUrl);

            string videoCodec;
            // Cascata estrita de prioridade de codecs de vídeo: AV1 > VP9.2 > VP9 > H.264
            if (IsAv1Itag(videoItag) || videoUrl.IndexOf("av01", StringComparison.OrdinalIgnoreCase) >= 0)
            {
                videoCodec = "AV1";
            }
            else if (IsVp92Itag(videoItag) || videoUrl.IndexOf("vp09.02", StringComparison.OrdinalIgnoreCase) >= 0 || videoUrl.IndexOf("vp9.2", StringComparison.OrdinalIgnoreCase) >= 0)
            {
                videoCodec = "VP9.2 HDR";
            }
            else if (IsVp9Itag(videoItag) || videoUrl.IndexOf("vp09", StringComparison.OrdinalIgnoreCase) >= 0 || videoUrl.IndexOf("vp9", StringComparison.OrdinalIgnoreCase) >= 0 || videoUrl.IndexOf("mime=video%2Fwebm", StringComparison.OrdinalIgnoreCase) >= 0 || videoUrl.IndexOf("mime=video/webm", StringComparison.OrdinalIgnoreCase) >= 0)
            {
                videoCodec = "VP9";
            }
            else if (IsH264Itag(videoItag) || videoUrl.IndexOf("avc1", StringComparison.OrdinalIgnoreCase) >= 0 || videoUrl.IndexOf("h264", StringComparison.OrdinalIgnoreCase) >= 0 || videoUrl.IndexOf("mime=video%2Fmp4", StringComparison.OrdinalIgnoreCase) >= 0 || videoUrl.IndexOf("mime=video/mp4", StringComparison.OrdinalIgnoreCase) >= 0)
            {
                videoCodec = "H.264";
            }
            else
            {
                videoCodec = "Direct3D 11";
            }

            string audioCodec = "AAC";
            if (audioItag == 249 || audioItag == 250 || audioItag == 251 ||
                (!string.IsNullOrEmpty(audioUrl) && (audioUrl.IndexOf("opus", StringComparison.OrdinalIgnoreCase) >= 0 || audioUrl.IndexOf("mime=audio%2Fwebm", StringComparison.OrdinalIgnoreCase) >= 0 || audioUrl.IndexOf("mime=audio/webm", StringComparison.OrdinalIgnoreCase) >= 0)))
            {
                audioCodec = "Opus";
            }
            else if (audioItag == 139 || audioItag == 140 || audioItag == 141 ||
                (!string.IsNullOrEmpty(audioUrl) && (audioUrl.IndexOf("mp4a", StringComparison.OrdinalIgnoreCase) >= 0 || audioUrl.IndexOf("aac", StringComparison.OrdinalIgnoreCase) >= 0 || audioUrl.IndexOf("mime=audio%2Fmp4", StringComparison.OrdinalIgnoreCase) >= 0 || audioUrl.IndexOf("mime=audio/mp4", StringComparison.OrdinalIgnoreCase) >= 0)))
            {
                audioCodec = "AAC";
            }

            return string.Format("{0}/{1} (Direct3D 11)", videoCodec, audioCodec);
        }

        public static int ExtractItag(string url)
        {
            if (string.IsNullOrEmpty(url)) return 0;
            try
            {
                var match = System.Text.RegularExpressions.Regex.Match(url, @"[?&]itag=(\d+)");
                if (match.Success)
                {
                    int itag;
                    if (int.TryParse(match.Groups[1].Value, out itag))
                        return itag;
                }
            }
            catch { }
            return 0;
        }

        public static bool IsAv1Itag(int itag)
        {
            // YouTube AV1: 394 (144p) até 402 (4320p), 571 (8K), 694-702 (AV1 Live)
            return (itag >= 394 && itag <= 402) || itag == 571 || (itag >= 694 && itag <= 702);
        }

        public static bool IsVp92Itag(int itag)
        {
            // YouTube VP9.2 Profile 2 HDR: 330 (144p HDR) até 337 (2160p HDR)
            return itag >= 330 && itag <= 337;
        }

        public static bool IsVp9Itag(int itag)
        {
            // YouTube VP9 Profile 0
            return itag == 278 || (itag >= 242 && itag <= 248) || itag == 271 || itag == 313 ||
                   itag == 302 || itag == 303 || itag == 308 || itag == 315 || itag == 272;
        }

        public static bool IsH264Itag(int itag)
        {
            // YouTube H.264 / AVC
            return itag == 160 || (itag >= 133 && itag <= 137) || itag == 264 || itag == 266 ||
                   itag == 298 || itag == 299 || itag == 18 || itag == 22 || itag == 37 || itag == 38;
        }

        private static void SafeKillProcess(Process p)
        {
            try
            {
                if (p != null && !p.HasExited)
                {
                    p.Kill();
                }
            }
            catch { }
        }

        public void Dispose()
        {
            _sessionCts.Cancel();
            _sessionCts.Dispose();
        }
    }
}
