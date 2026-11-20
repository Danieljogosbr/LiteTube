using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Net.Http;
using System.Text;
using System.Text.Json;
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

        private const int MaxMetadataCacheEntries = 100;
        private static readonly ConcurrentDictionary<string, VideoItem> _metadataCache =
            new ConcurrentDictionary<string, VideoItem>(StringComparer.OrdinalIgnoreCase);
        private static readonly ConcurrentQueue<string> _metadataCacheOrder = new ConcurrentQueue<string>();

        private static readonly ConcurrentDictionary<string, (string VideoUrl, string AudioUrl, string Codec, DateTime ExpiresUtc)> _streamUrlCache =
            new ConcurrentDictionary<string, (string, string, string, DateTime)>(StringComparer.OrdinalIgnoreCase);
        private static string _cachedVisitorData = null;
        private static DateTime _visitorDataExpiresUtc = DateTime.MinValue;
        private static readonly SemaphoreSlim _visitorLock = new SemaphoreSlim(1, 1);
        private static long _fastPathPausedUntilTicks = 0;

        public static bool IsFastPathPaused()
        {
            long untilTicks = Interlocked.Read(ref _fastPathPausedUntilTicks);
            return untilTicks > 0 && DateTime.UtcNow.Ticks < untilTicks;
        }

        private static void PauseFastPath(TimeSpan duration, string reasonLabel)
        {
            long targetTicks = DateTime.UtcNow.Add(duration).Ticks;
            long prev = Interlocked.Exchange(ref _fastPathPausedUntilTicks, targetTicks);
            if (prev <= DateTime.UtcNow.Ticks)
            {
                Logger.Info("FASTPATH", string.Format("Circuit breaker ativado: Fast-Path ANDROID_VR pausado por {0:0} min ({1}) para evitar requisições repetidas.",
                    duration.TotalMinutes, reasonLabel));
            }
        }

        private static void StoreMetadataCache(string videoId, VideoItem item)
        {
            if (string.IsNullOrWhiteSpace(videoId) || item == null) return;
            bool isNew = !_metadataCache.ContainsKey(videoId);
            _metadataCache[videoId] = item;
            if (isNew)
            {
                _metadataCacheOrder.Enqueue(videoId);
                while (_metadataCache.Count > MaxMetadataCacheEntries && _metadataCacheOrder.TryDequeue(out string oldKey))
                {
                    _metadataCache.TryRemove(oldKey, out _);
                }
            }
        }

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
            using var sessionLinkedCts = CancellationTokenSource.CreateLinkedTokenSource(token, _sessionCts.Token);
            CancellationToken effectiveToken = sessionLinkedCts.Token;
            try
            {
                if (!string.IsNullOrEmpty(_resolvedVideoUrl))
                    return true;

                string vidId = _video != null ? _video.Id : "";
                Logger.Info("STREAM", string.Format("EnsureStreamUrlsResolvedAsync iniciando: vidId={0}, height={1}p",
                    !string.IsNullOrEmpty(vidId) ? vidId : "?", _height));

                // 0. Fast-path absoluto (0ms): Cache em RAM com TTL de 12 minutos
                string streamCacheKey = vidId + "|" + _height;
                if (!string.IsNullOrEmpty(vidId) &&
                    _streamUrlCache.TryGetValue(streamCacheKey, out var cachedStream) &&
                    DateTime.UtcNow < cachedStream.ExpiresUtc &&
                    !string.IsNullOrEmpty(cachedStream.VideoUrl))
                {
                    _resolvedVideoUrl = cachedStream.VideoUrl;
                    _resolvedAudioUrl = cachedStream.AudioUrl;
                    _activeCodecName = cachedStream.Codec;
                    Logger.Info("STREAM", string.Format("Cache Hit RAM (0ms) para VideoId={0} ({1}p • {2})", vidId, _height, _activeCodecName));
                    return true;
                }

                // 1. Fast-path HTTP/2 C# InnerTube ANDROID_VR (~150-300ms, sem spawn de yt-dlp/deno)
                // Ignorado automaticamente se o circuit breaker estiver ativo após LOGIN_REQUIRED / 429
                if (!string.IsNullOrEmpty(vidId) && !effectiveToken.IsCancellationRequested && !IsFastPathPaused())
                {
                    var fastResult = await TryResolveDirectInnerTubeStreamsAsync(vidId, _height, effectiveToken).ConfigureAwait(false);
                    if (fastResult.Success && !string.IsNullOrEmpty(fastResult.VideoUrl))
                    {
                        _resolvedVideoUrl = fastResult.VideoUrl;
                        _resolvedAudioUrl = fastResult.AudioUrl;
                        _activeCodecName = DetectCodecFromUrls(_resolvedVideoUrl, _resolvedAudioUrl);
                        StoreStreamCache(streamCacheKey, _resolvedVideoUrl, _resolvedAudioUrl, _activeCodecName);

                        int fVItag = ExtractItag(_resolvedVideoUrl);
                        int fAItag = ExtractItag(_resolvedAudioUrl);
                        Logger.Info("STREAM", string.Format("Fast-Path InnerTube C# obteve fluxos diretos em {0}ms: Video={1}p (itag={2}), Audio (itag={3}), Codec='{4}'",
                            sw.ElapsedMilliseconds, fastResult.ResolvedQuality, fVItag, fAItag > 0 ? fAItag.ToString() : "embutido", _activeCodecName));
                        return true;
                    }
                }

                string videoUrl = "https://www.youtube.com/watch?v=" + vidId;
                // O filtro `res:{0}` em -S utiliza min(width,height), evitando rejeitar Shorts verticais (1080x1920) por height>1080
                string formatArg = "bestvideo+bestaudio/best";
                string sortArg = string.Format("res:{0},vcodec:av01,vcodec:vp9.2,vcodec:vp9,vcodec:h264,fps,acodec:opus,acodec:m4a", _height);

                string denoPath = Path.Combine(_binDir, "deno.exe");
                string jsArg = File.Exists(denoPath) ? string.Format("--js-runtimes \"deno:{0}\" ", denoPath) : "";
                using var cookieScope = CookieVault.CreateYtDlpCookieScope(_cookiesFilePath, _browserForCookies);
                string cookieArg = cookieScope.Arg;
                string[] urls = null;

                // Tentativa 1 (Fallback yt-dlp): Cookies configurados do navegador/arquivo com skip=hls,translated_subs
                if (!effectiveToken.IsCancellationRequested)
                {
                    string attempt1Args = (!string.IsNullOrEmpty(cookieArg) ? cookieArg : "") + jsArg + "--extractor-args \"youtube:skip=hls,translated_subs\" ";
                    Logger.Info("STREAM", "Fallback yt-dlp (Tentativa 1): skip=hls,translated_subs e cookies...");
                    urls = await Task.Run(() => RunYtDlpGetUrls(videoUrl, formatArg, sortArg, attempt1Args, effectiveToken));
                }

                // Tentativa 2: Cliente Android (muda assinatura de bot)
                if ((urls == null || urls.Length == 0) && !_isVideoUnavailable && !effectiveToken.IsCancellationRequested)
                {
                    Logger.Warn("STREAM", "Tentativa 1 falhou. Tentativa 2: yt-dlp com cliente android...");
                    string attempt2Args = jsArg + "--extractor-args \"youtube:player_client=android;skip=hls,translated_subs\" ";
                    urls = await Task.Run(() => RunYtDlpGetUrls(videoUrl, formatArg, sortArg, attempt2Args, effectiveToken));
                }

                // Tentativa 3: Cliente iOS (excelente suporte para Live Streams HLS e formatos diretos)
                if ((urls == null || urls.Length == 0) && !_isVideoUnavailable && !effectiveToken.IsCancellationRequested)
                {
                    Logger.Warn("STREAM", "Tentativa 2 falhou. Tentativa 3: yt-dlp com cliente ios...");
                    string attempt3Args = jsArg + "--extractor-args \"youtube:player_client=ios;skip=translated_subs\" ";
                    urls = await Task.Run(() => RunYtDlpGetUrls(videoUrl, formatArg, sortArg, attempt3Args, effectiveToken));
                }

                // Tentativa 4: Cliente Web Padrão / Live Stream HLS (sem skip=hls para suportar manifestos live)
                if ((urls == null || urls.Length == 0) && !_isVideoUnavailable && !effectiveToken.IsCancellationRequested)
                {
                    Logger.Warn("STREAM", "Tentativa 3 falhou. Tentativa 4: yt-dlp fallback Web / Live Stream HLS...");
                    string liveFormatArg = "bestvideo+bestaudio/best";
                    string attempt4Args = (!string.IsNullOrEmpty(cookieArg) ? cookieArg : "") + jsArg + "--extractor-args \"youtube:skip=translated_subs\" ";
                    urls = await Task.Run(() => RunYtDlpGetUrls(videoUrl, liveFormatArg, sortArg, attempt4Args, effectiveToken));
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
                    if (!_isLiveStream && !string.IsNullOrEmpty(vidId))
                    {
                        StoreStreamCache(streamCacheKey, _resolvedVideoUrl, _resolvedAudioUrl, _activeCodecName);
                    }

                    int vItag = ExtractItag(_resolvedVideoUrl);
                    int aItag = ExtractItag(_resolvedAudioUrl);

                    Logger.Info("STREAM", string.Format("Fluxos diretos obtidos via yt-dlp em {0}ms: Video='{1}' (itag={2}), Audio='{3}' (itag={4}), Codec='{5}', Live={6}",
                        sw.ElapsedMilliseconds,
                        _resolvedVideoUrl.Substring(0, Math.Min(60, _resolvedVideoUrl.Length)) + "...",
                        vItag > 0 ? vItag.ToString() : "desconhecido",
                        _resolvedAudioUrl != null ? (_resolvedAudioUrl.Substring(0, Math.Min(60, _resolvedAudioUrl.Length)) + "...") : "embutido",
                        aItag > 0 ? aItag.ToString() : "desconhecido",
                        _activeCodecName,
                        _isLiveStream));

                    return true;
                }

                if (effectiveToken.IsCancellationRequested)
                {
                    Logger.Info("STREAM", string.Format("Resolução de stream cancelada (hover/navegação) após {0}ms: VideoId={1}", sw.ElapsedMilliseconds, vidId));
                    return false;
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

        private static void StoreStreamCache(string cacheKey, string videoUrl, string audioUrl, string codec)
        {
            if (string.IsNullOrEmpty(cacheKey) || string.IsNullOrEmpty(videoUrl)) return;
            DateTime now = DateTime.UtcNow;
            if (_streamUrlCache.Count >= 40)
            {
                foreach (var kv in _streamUrlCache)
                {
                    if (kv.Value.ExpiresUtc <= now)
                    {
                        _streamUrlCache.TryRemove(kv.Key, out _);
                    }
                }
                if (_streamUrlCache.Count >= 40)
                {
                    _streamUrlCache.Clear();
                }
            }
            _streamUrlCache[cacheKey] = (videoUrl, audioUrl, codec, now.AddMinutes(12));
        }

        private static async Task<string> GetOrFetchVisitorDataAsync(CancellationToken ct)
        {
            if (!string.IsNullOrEmpty(_cachedVisitorData) && DateTime.UtcNow < _visitorDataExpiresUtc)
                return _cachedVisitorData;

            await _visitorLock.WaitAsync(ct).ConfigureAwait(false);
            try
            {
                if (!string.IsNullOrEmpty(_cachedVisitorData) && DateTime.UtcNow < _visitorDataExpiresUtc)
                    return _cachedVisitorData;

                using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
                cts.CancelAfter(TimeSpan.FromMilliseconds(1500));

                using var req = new HttpRequestMessage(HttpMethod.Post, "https://www.youtube.com/youtubei/v1/visitor_id?prettyPrint=false")
                {
                    Content = new StringContent(
                        "{\"context\":{\"client\":{\"clientName\":\"ANDROID_VR\",\"clientVersion\":\"1.60.19\",\"deviceMake\":\"Oculus\",\"deviceModel\":\"Quest 3\"}}}",
                        Encoding.UTF8,
                        "application/json")
                };
                req.Headers.TryAddWithoutValidation("User-Agent", "com.google.android.apps.youtube.vr.oculus/1.60.19 (Linux; U; Android 12L; Quest 3 Build/SQ3A.220605.009.A1) gzip");

                using var resp = await HttpPipeline.Client.SendAsync(req, cts.Token).ConfigureAwait(false);
                if (resp.IsSuccessStatusCode)
                {
                    string json = await resp.Content.ReadAsStringAsync(cts.Token).ConfigureAwait(false);
                    using var doc = JsonDocument.Parse(json);
                    if (doc.RootElement.TryGetProperty("responseContext", out var rc) &&
                        rc.TryGetProperty("visitorData", out var vd))
                    {
                        string val = vd.GetString();
                        if (!string.IsNullOrEmpty(val))
                        {
                            _cachedVisitorData = val;
                            _visitorDataExpiresUtc = DateTime.UtcNow.AddHours(6);
                            return val;
                        }
                    }
                }
            }
            catch { }
            finally
            {
                _visitorLock.Release();
            }
            return null;
        }

        public static async Task PrewarmVisitorDataAsync()
        {
            if (IsFastPathPaused()) return;
            try
            {
                await GetOrFetchVisitorDataAsync(CancellationToken.None).ConfigureAwait(false);
            }
            catch { }
        }

        private static async Task<(bool Success, string VideoUrl, string AudioUrl, int ResolvedQuality)> TryResolveDirectInnerTubeStreamsAsync(
            string videoId, int targetHeight, CancellationToken token)
        {
            var fastSw = Stopwatch.StartNew();
            try
            {
                string visitorData = await GetOrFetchVisitorDataAsync(token).ConfigureAwait(false);

                using var cts = CancellationTokenSource.CreateLinkedTokenSource(token);
                cts.CancelAfter(TimeSpan.FromMilliseconds(1500));

                string payload = !string.IsNullOrEmpty(visitorData)
                    ? $"{{\"context\":{{\"client\":{{\"clientName\":\"ANDROID_VR\",\"clientVersion\":\"1.62.27\",\"deviceMake\":\"Oculus\",\"deviceModel\":\"Quest 3\",\"osName\":\"Android\",\"osVersion\":\"12L\",\"androidSdkVersion\":32,\"visitorData\":\"{visitorData}\",\"hl\":\"pt\",\"gl\":\"BR\"}}}},\"videoId\":\"{videoId}\"}}"
                    : $"{{\"context\":{{\"client\":{{\"clientName\":\"ANDROID_VR\",\"clientVersion\":\"1.62.27\",\"deviceMake\":\"Oculus\",\"deviceModel\":\"Quest 3\",\"osName\":\"Android\",\"osVersion\":\"12L\",\"androidSdkVersion\":32,\"hl\":\"pt\",\"gl\":\"BR\"}}}},\"videoId\":\"{videoId}\"}}";

                using var req = new HttpRequestMessage(HttpMethod.Post, "https://www.youtube.com/youtubei/v1/player?prettyPrint=false")
                {
                    Content = new StringContent(payload, Encoding.UTF8, "application/json")
                };
                req.Headers.TryAddWithoutValidation("User-Agent", "com.google.android.apps.youtube.vr.oculus/1.62.27 (Linux; U; Android 12L; Quest 3 Build/SQ3A.220605.009.A1) gzip");

                using var resp = await HttpPipeline.Client.SendAsync(req, cts.Token).ConfigureAwait(false);
                if (!resp.IsSuccessStatusCode)
                {
                    int code = (int)resp.StatusCode;
                    Logger.Info("FASTPATH", string.Format("InnerTube ANDROID_VR retornou HTTP {0} em {1}ms (Id={2}).", code, fastSw.ElapsedMilliseconds, videoId));
                    if (code == 429 || code == 403)
                    {
                        PauseFastPath(TimeSpan.FromMinutes(30), "HTTP " + code);
                    }
                    return (false, null, null, 0);
                }

                string json = await resp.Content.ReadAsStringAsync(cts.Token).ConfigureAwait(false);
                if (string.IsNullOrWhiteSpace(json))
                {
                    Logger.Info("FASTPATH", string.Format("InnerTube ANDROID_VR retornou JSON vazio em {0}ms (Id={1}).", fastSw.ElapsedMilliseconds, videoId));
                    return (false, null, null, 0);
                }

                using var doc = JsonDocument.Parse(json);
                var root = doc.RootElement;

                string playStatus = "MISSING";
                if (root.TryGetProperty("playabilityStatus", out var ps) &&
                    ps.TryGetProperty("status", out var st))
                {
                    playStatus = st.GetString() ?? "NULL";
                }
                if (!string.Equals(playStatus, "OK", StringComparison.OrdinalIgnoreCase))
                {
                    string reason = "";
                    if (ps.ValueKind == JsonValueKind.Object && ps.TryGetProperty("reason", out var rEl))
                        reason = rEl.GetString() ?? "";
                    Logger.Info("FASTPATH", string.Format("InnerTube ANDROID_VR recusado em {0}ms: Status={1} Reason='{2}' (Id={3}).", fastSw.ElapsedMilliseconds, playStatus, reason, videoId));
                    if (string.Equals(playStatus, "LOGIN_REQUIRED", StringComparison.OrdinalIgnoreCase))
                    {
                        PauseFastPath(TimeSpan.FromMinutes(30), "LOGIN_REQUIRED");
                    }
                    return (false, null, null, 0);
                }

                if (root.TryGetProperty("videoDetails", out var vd) &&
                    vd.TryGetProperty("isLive", out var isLiveEl) &&
                    isLiveEl.ValueKind == JsonValueKind.True)
                {
                    Logger.Info("FASTPATH", string.Format("Video é Live; encaminhando ao yt-dlp HLS ({0}ms, Id={1}).", fastSw.ElapsedMilliseconds, videoId));
                    return (false, null, null, 0);
                }

                if (!root.TryGetProperty("streamingData", out var sd) ||
                    !sd.TryGetProperty("adaptiveFormats", out var af) ||
                    af.ValueKind != JsonValueKind.Array)
                {
                    Logger.Info("FASTPATH", string.Format("InnerTube ANDROID_VR sem adaptiveFormats em {0}ms (Id={1}).", fastSw.ElapsedMilliseconds, videoId));
                    return (false, null, null, 0);
                }

                int maxAnyVideoQ = 0;
                int directUrlCount = 0;
                string bestVideoUrl = null;
                int bestVideoQ = 0;
                int bestVideoCodecRank = -1;
                int bestVideoFps = 0;
                long bestVideoBitrate = 0;

                string bestAudioUrl = null;
                int bestAudioRank = -1;
                long bestAudioBitrate = 0;

                foreach (var fmt in af.EnumerateArray())
                {
                    string mime = fmt.TryGetProperty("mimeType", out var mEl) ? (mEl.GetString() ?? "") : "";
                    string directUrl = fmt.TryGetProperty("url", out var uEl) ? uEl.GetString() : null;
                    if (!string.IsNullOrEmpty(directUrl)) directUrlCount++;
                    long bitrate = fmt.TryGetProperty("bitrate", out var bEl) && bEl.TryGetInt64(out long br) ? br : 0;

                    if (mime.StartsWith("video/", StringComparison.OrdinalIgnoreCase))
                    {
                        int w = fmt.TryGetProperty("width", out var wEl) && wEl.TryGetInt32(out int wi) ? wi : 0;
                        int h = fmt.TryGetProperty("height", out var hEl) && hEl.TryGetInt32(out int hi) ? hi : 0;
                        int effQ = ResolveEffectiveQualityHeight(w, h);
                        if (effQ > maxAnyVideoQ) maxAnyVideoQ = effQ;

                        if (string.IsNullOrEmpty(directUrl) || effQ <= 0 || effQ > targetHeight) continue;

                        int fps = fmt.TryGetProperty("fps", out var fEl) && fEl.TryGetInt32(out int fi) ? fi : 30;
                        int cRank = GetVideoCodecRank(mime);

                        bool better = effQ > bestVideoQ ||
                            (effQ == bestVideoQ && (cRank > bestVideoCodecRank ||
                            (cRank == bestVideoCodecRank && (fps > bestVideoFps ||
                            (fps == bestVideoFps && bitrate > bestVideoBitrate)))));

                        if (better)
                        {
                            bestVideoUrl = directUrl;
                            bestVideoQ = effQ;
                            bestVideoCodecRank = cRank;
                            bestVideoFps = fps;
                            bestVideoBitrate = bitrate;
                        }
                    }
                    else if (mime.StartsWith("audio/", StringComparison.OrdinalIgnoreCase) && !string.IsNullOrEmpty(directUrl))
                    {
                        int aRank = mime.IndexOf("opus", StringComparison.OrdinalIgnoreCase) >= 0 ? 2
                            : (mime.IndexOf("mp4a", StringComparison.OrdinalIgnoreCase) >= 0 ? 1 : 0);
                        if (aRank > bestAudioRank || (aRank == bestAudioRank && bitrate > bestAudioBitrate))
                        {
                            bestAudioUrl = directUrl;
                            bestAudioRank = aRank;
                            bestAudioBitrate = bitrate;
                        }
                    }
                }

                // Evita regressão silenciosa se qualidades altas exigirem cipher/yt-dlp
                int minAcceptable = targetHeight >= 720 ? Math.Min(targetHeight, Math.Max(720, maxAnyVideoQ)) : Math.Min(targetHeight, maxAnyVideoQ);
                if (!string.IsNullOrEmpty(bestVideoUrl) && !string.IsNullOrEmpty(bestAudioUrl) && bestVideoQ >= minAcceptable)
                {
                    return (true, bestVideoUrl, bestAudioUrl, bestVideoQ);
                }

                Logger.Info("FASTPATH", string.Format("InnerTube ANDROID_VR insuficiente em {0}ms: DirectUrls={1}, BestQ={2}p, MaxAnyQ={3}p, Target={4}p, HasAudio={5} (Id={6}).",
                    fastSw.ElapsedMilliseconds, directUrlCount, bestVideoQ, maxAnyVideoQ, targetHeight, !string.IsNullOrEmpty(bestAudioUrl), videoId));
            }
            catch (Exception ex)
            {
                if (!token.IsCancellationRequested)
                {
                    Logger.Info("FASTPATH", string.Format("Exceção Fast-Path em {0}ms: {1} (Id={2}).", fastSw.ElapsedMilliseconds, ex.Message, videoId));
                }
            }

            return (false, null, null, 0);
        }

        private static int GetVideoCodecRank(string mimeType)
        {
            if (string.IsNullOrEmpty(mimeType)) return 0;
            if (mimeType.IndexOf("av01", StringComparison.OrdinalIgnoreCase) >= 0) return 4;
            if (mimeType.IndexOf("vp09.02", StringComparison.OrdinalIgnoreCase) >= 0 ||
                mimeType.IndexOf("vp9.2", StringComparison.OrdinalIgnoreCase) >= 0) return 3;
            if (mimeType.IndexOf("vp09", StringComparison.OrdinalIgnoreCase) >= 0 ||
                mimeType.IndexOf("vp9", StringComparison.OrdinalIgnoreCase) >= 0) return 2;
            if (mimeType.IndexOf("avc1", StringComparison.OrdinalIgnoreCase) >= 0) return 1;
            return 0;
        }

        private bool HasConfiguredCookies()
        {
            return CookieVault.HasStoredCookies(_cookiesFilePath) ||
                (!string.IsNullOrEmpty(_browserForCookies) && _browserForCookies != "none");
        }

        /// <summary>
        /// Semáforo estrito global para pré-resolução em background (máximo 1 requisição ativa por vez, evitando HTTP 429).
        /// </summary>
        public static readonly SemaphoreSlim SharedPrefetchSemaphore = new SemaphoreSlim(1, 1);

        private string[] RunYtDlpGetUrls(string videoUrl, string formatArg, string sortArg, string extraArgs, CancellationToken token)
        {
            if (!File.Exists(_ytDlpPath))
            {
                _lastResolveError = "Executável yt-dlp.exe não encontrado em bin/.";
                return null;
            }

            try
            {
                string arguments = string.Format("--no-config --no-playlist --no-warnings -g -f \"{0}\" -S \"{1}\" --format-sort-force {2}\"{3}\"",
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
                                _isVideoUnavailable = !HasConfiguredCookies();
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

        public static async Task<VideoItem> FetchVideoMetadataAsync(
            string videoId,
            string binDir = null,
            string browserForCookies = null,
            string cookiesFilePath = null,
            CancellationToken token = default)
        {
            if (string.IsNullOrWhiteSpace(videoId)) return null;

            VideoItem cached;
            if (_metadataCache.TryGetValue(videoId, out cached) &&
                cached != null &&
                (!string.IsNullOrWhiteSpace(cached.Description) || cached.ViewCount > 0 || !string.IsNullOrWhiteSpace(cached.UploadDateRaw)))
            {
                return cached;
            }

            if (string.IsNullOrEmpty(binDir))
            {
                binDir = YtDlpResolver.GetYtDlpDirectory();
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

            var dummy = new TrackItem { Id = videoId, Title = "MetadataFetch" };
            using (var session = new ProgressiveVideoSession(dummy, 720, binDir, browserForCookies, cookiesFilePath))
            {
                // 1. Fast-path InnerTube /youtubei/v1/player (~150ms) + yt-dlp --dump-single-json fallback/enrichment
                VideoItem innerTubeMeta = await FetchInnerTubePlayerMetadataAsync(videoId, token).ConfigureAwait(false);
                if (innerTubeMeta != null &&
                    !string.IsNullOrWhiteSpace(innerTubeMeta.Description) &&
                    innerTubeMeta.ViewCount > 0 &&
                    !string.IsNullOrWhiteSpace(innerTubeMeta.UploadDateRaw))
                {
                    StoreMetadataCache(videoId, innerTubeMeta);
                    return innerTubeMeta;
                }

                // 2. Extração via yt-dlp (--dump-single-json --skip-download --no-playlist)
                string videoUrl = "https://www.youtube.com/watch?v=" + videoId;
                string denoPath = Path.Combine(session._binDir, "deno.exe");
                string jsArg = File.Exists(denoPath) ? string.Format("--js-runtimes \"deno:{0}\" ", denoPath) : "";
                using var cookieScope = CookieVault.CreateYtDlpCookieScope(session._cookiesFilePath, session._browserForCookies);
                string cookieArg = cookieScope.Arg;
                string extraArgs = (!string.IsNullOrEmpty(cookieArg) ? cookieArg : "") + jsArg;

                VideoItem ytDlpMeta = await Task.Run(() => session.RunYtDlpGetMetadata(videoUrl, extraArgs, videoId, token), token).ConfigureAwait(false);
                if (ytDlpMeta == null && !token.IsCancellationRequested)
                {
                    string androidArgs = jsArg + "--extractor-args \"youtube:player_client=android\" ";
                    ytDlpMeta = await Task.Run(() => session.RunYtDlpGetMetadata(videoUrl, androidArgs, videoId, token), token).ConfigureAwait(false);
                }

                if (ytDlpMeta != null)
                {
                    if (innerTubeMeta != null)
                    {
                        if (string.IsNullOrWhiteSpace(ytDlpMeta.Description)) ytDlpMeta.Description = innerTubeMeta.Description;
                        if (ytDlpMeta.ViewCount <= 0) ytDlpMeta.ViewCount = innerTubeMeta.ViewCount;
                        if (string.IsNullOrWhiteSpace(ytDlpMeta.UploadDateRaw)) ytDlpMeta.UploadDateRaw = innerTubeMeta.UploadDateRaw;
                        if (string.IsNullOrWhiteSpace(ytDlpMeta.Title)) ytDlpMeta.Title = innerTubeMeta.Title;
                        if (string.IsNullOrWhiteSpace(ytDlpMeta.Author)) ytDlpMeta.Author = innerTubeMeta.Author;
                        if ((ytDlpMeta.AvailableResolutions == null || ytDlpMeta.AvailableResolutions.Count == 0) &&
                            innerTubeMeta.AvailableResolutions != null && innerTubeMeta.AvailableResolutions.Count > 0)
                        {
                            ytDlpMeta.AvailableResolutions = new List<int>(innerTubeMeta.AvailableResolutions);
                        }
                    }
                    StoreMetadataCache(videoId, ytDlpMeta);
                    return ytDlpMeta;
                }

                if (innerTubeMeta != null)
                {
                    StoreMetadataCache(videoId, innerTubeMeta);
                    return innerTubeMeta;
                }
            }

            return null;
        }

        private VideoItem RunYtDlpGetMetadata(string videoUrl, string extraArgs, string fallbackVideoId, CancellationToken token)
        {
            if (!File.Exists(_ytDlpPath)) return null;

            try
            {
                string arguments = string.Format("--dump-single-json --skip-download --no-playlist --no-warnings {0}\"{1}\"",
                    extraArgs ?? "", videoUrl);

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

                var sbOut = new StringBuilder();
                using (var proc = new Process())
                {
                    proc.StartInfo = psi;
                    proc.OutputDataReceived += (s, e) =>
                    {
                        if (!string.IsNullOrEmpty(e.Data))
                        {
                            lock (sbOut) { sbOut.AppendLine(e.Data); }
                        }
                    };
                    proc.ErrorDataReceived += (s, e) => { };

                    if (!proc.Start()) return null;
                    proc.BeginOutputReadLine();
                    proc.BeginErrorReadLine();

                    var sw = Stopwatch.StartNew();
                    while (!proc.HasExited)
                    {
                        if (token.IsCancellationRequested || sw.ElapsedMilliseconds > 25000)
                        {
                            SafeKillProcess(proc);
                            return null;
                        }
                        Thread.Sleep(30);
                    }

                    string json;
                    lock (sbOut)
                    {
                        json = sbOut.ToString().Trim();
                    }

                    if (!string.IsNullOrWhiteSpace(json))
                    {
                        return ParseYtDlpMetadataJson(json, fallbackVideoId);
                    }
                }
            }
            catch (Exception ex)
            {
                Logger.Warn("METADATA", "RunYtDlpGetMetadata falhou: " + ex.Message);
            }

            return null;
        }

        private static async Task<VideoItem> FetchInnerTubePlayerMetadataAsync(string videoId, CancellationToken token)
        {
            try
            {
                string url = "https://www.youtube.com/youtubei/v1/player?prettyPrint=false";
                string payload = "{\"context\":{\"client\":{\"hl\":\"pt\",\"gl\":\"BR\",\"clientName\":\"WEB\",\"clientVersion\":\"2.20241101.01.00\"}},\"videoId\":\"" + videoId + "\"}";

                using (var req = new HttpRequestMessage(HttpMethod.Post, url))
                {
                    req.Content = new StringContent(payload, Encoding.UTF8, "application/json");
                    using (var resp = await HttpPipeline.Client.SendAsync(req, HttpCompletionOption.ResponseContentRead, token).ConfigureAwait(false))
                    {
                        if (!resp.IsSuccessStatusCode) return null;
                        string json = await resp.Content.ReadAsStringAsync(token).ConfigureAwait(false);
                        return ParseYtDlpMetadataJson(json, videoId);
                    }
                }
            }
            catch
            {
                return null;
            }
        }

        public static VideoItem ParseYtDlpMetadataJson(string json, string fallbackVideoId = null)
        {
            if (string.IsNullOrWhiteSpace(json)) return null;

            try
            {
                int firstBrace = json.IndexOf('{');
                if (firstBrace > 0) json = json.Substring(firstBrace);

                using (JsonDocument doc = JsonDocument.Parse(json))
                {
                    JsonElement root = doc.RootElement;
                    var item = new VideoItem
                    {
                        Id = fallbackVideoId ?? string.Empty
                    };

                    // 1. Campos diretos do yt-dlp (--dump-single-json)
                    if (root.TryGetProperty("id", out var idProp) && idProp.ValueKind == JsonValueKind.String)
                        item.Id = idProp.GetString() ?? item.Id;

                    if (root.TryGetProperty("title", out var titleProp) && titleProp.ValueKind == JsonValueKind.String)
                        item.Title = titleProp.GetString() ?? string.Empty;

                    if (root.TryGetProperty("uploader", out var upProp) && upProp.ValueKind == JsonValueKind.String)
                        item.Author = upProp.GetString() ?? string.Empty;
                    else if (root.TryGetProperty("channel", out var chProp) && chProp.ValueKind == JsonValueKind.String)
                        item.Author = chProp.GetString() ?? string.Empty;

                    if (root.TryGetProperty("description", out var descProp) && descProp.ValueKind == JsonValueKind.String)
                        item.Description = descProp.GetString() ?? string.Empty;

                    if (root.TryGetProperty("view_count", out var vcProp))
                    {
                        if (vcProp.ValueKind == JsonValueKind.Number && vcProp.TryGetInt64(out long vCount))
                            item.ViewCount = vCount;
                        else if (vcProp.ValueKind == JsonValueKind.String && long.TryParse(vcProp.GetString(), out long vCountStr))
                            item.ViewCount = vCountStr;
                    }

                    if (root.TryGetProperty("upload_date", out var udProp) && udProp.ValueKind == JsonValueKind.String)
                        item.UploadDateRaw = udProp.GetString() ?? string.Empty;
                    else if (root.TryGetProperty("release_date", out var rdProp) && rdProp.ValueKind == JsonValueKind.String)
                        item.UploadDateRaw = rdProp.GetString() ?? string.Empty;

                    if (root.TryGetProperty("duration", out var durProp))
                    {
                        if (durProp.ValueKind == JsonValueKind.Number && durProp.TryGetDouble(out double durVal))
                            item.DurationSeconds = (long)Math.Round(durVal);
                        else if (durProp.ValueKind == JsonValueKind.String &&
                                 double.TryParse(durProp.GetString(), System.Globalization.NumberStyles.Any, System.Globalization.CultureInfo.InvariantCulture, out double durStrVal))
                            item.DurationSeconds = (long)Math.Round(durStrVal);
                    }

                    if (root.TryGetProperty("thumbnail", out var thumbProp) && thumbProp.ValueKind == JsonValueKind.String)
                        item.ThumbnailUrl = thumbProp.GetString() ?? string.Empty;

                    // 2. Suporte complementar para resposta InnerTube (/youtubei/v1/player)
                    if (root.TryGetProperty("videoDetails", out var vd) && vd.ValueKind == JsonValueKind.Object)
                    {
                        if (string.IsNullOrEmpty(item.Id) && vd.TryGetProperty("videoId", out var vidIdProp) && vidIdProp.ValueKind == JsonValueKind.String)
                            item.Id = vidIdProp.GetString() ?? item.Id;

                        if (string.IsNullOrEmpty(item.Title) && vd.TryGetProperty("title", out var vdTitle) && vdTitle.ValueKind == JsonValueKind.String)
                            item.Title = vdTitle.GetString() ?? string.Empty;

                        if (string.IsNullOrEmpty(item.Author) && vd.TryGetProperty("author", out var vdAuthor) && vdAuthor.ValueKind == JsonValueKind.String)
                            item.Author = vdAuthor.GetString() ?? string.Empty;

                        if (string.IsNullOrEmpty(item.Description) && vd.TryGetProperty("shortDescription", out var vdDesc) && vdDesc.ValueKind == JsonValueKind.String)
                            item.Description = vdDesc.GetString() ?? string.Empty;

                        if (item.ViewCount <= 0 && vd.TryGetProperty("viewCount", out var vdViews))
                        {
                            if (vdViews.ValueKind == JsonValueKind.String && long.TryParse(vdViews.GetString(), out long parsedViews))
                                item.ViewCount = parsedViews;
                            else if (vdViews.ValueKind == JsonValueKind.Number && vdViews.TryGetInt64(out long parsedViewsNum))
                                item.ViewCount = parsedViewsNum;
                        }

                        if (item.DurationSeconds <= 0 && vd.TryGetProperty("lengthSeconds", out var vdLen))
                        {
                            if (vdLen.ValueKind == JsonValueKind.String &&
                                double.TryParse(vdLen.GetString(), System.Globalization.NumberStyles.Any, System.Globalization.CultureInfo.InvariantCulture, out double parsedLen))
                                item.DurationSeconds = (long)Math.Round(parsedLen);
                            else if (vdLen.ValueKind == JsonValueKind.Number && vdLen.TryGetDouble(out double parsedLenNum))
                                item.DurationSeconds = (long)Math.Round(parsedLenNum);
                        }
                    }

                    if (root.TryGetProperty("microformat", out var mf) &&
                        mf.ValueKind == JsonValueKind.Object &&
                        mf.TryGetProperty("playerMicroformatRenderer", out var pmf) &&
                        pmf.ValueKind == JsonValueKind.Object)
                    {
                        if (string.IsNullOrEmpty(item.UploadDateRaw))
                        {
                            if (pmf.TryGetProperty("uploadDate", out var mfUpload) && mfUpload.ValueKind == JsonValueKind.String)
                                item.UploadDateRaw = mfUpload.GetString() ?? string.Empty;
                            else if (pmf.TryGetProperty("publishDate", out var mfPub) && mfPub.ValueKind == JsonValueKind.String)
                                item.UploadDateRaw = mfPub.GetString() ?? string.Empty;
                        }

                        if (item.ViewCount <= 0 && pmf.TryGetProperty("viewCount", out var mfViews))
                        {
                            if (mfViews.ValueKind == JsonValueKind.String && long.TryParse(mfViews.GetString(), out long mfParsedViews))
                                item.ViewCount = mfParsedViews;
                        }

                        if (string.IsNullOrEmpty(item.Description) &&
                            pmf.TryGetProperty("description", out var mfDescObj) &&
                            mfDescObj.ValueKind == JsonValueKind.Object &&
                            mfDescObj.TryGetProperty("simpleText", out var mfDescText) &&
                            mfDescText.ValueKind == JsonValueKind.String)
                        {
                            item.Description = mfDescText.GetString() ?? string.Empty;
                        }
                    }

                    // 3. Extração das resoluções disponíveis (yt-dlp formats ou InnerTube streamingData)
                    var resolutionSet = new HashSet<int>();
                    CollectHeightsFromFormatArray(root, "formats", resolutionSet);
                    if (root.TryGetProperty("streamingData", out var sd) && sd.ValueKind == JsonValueKind.Object)
                    {
                        CollectHeightsFromFormatArray(sd, "formats", resolutionSet);
                        CollectHeightsFromFormatArray(sd, "adaptiveFormats", resolutionSet);
                    }
                    if (resolutionSet.Count > 0)
                    {
                        var sorted = new List<int>(resolutionSet);
                        sorted.Sort();
                        item.AvailableResolutions = sorted;
                    }

                    return item;
                }
            }
            catch (Exception ex)
            {
                Logger.Warn("METADATA", "ParseYtDlpMetadataJson falhou: " + ex.Message);
                return null;
            }
        }

        private static void CollectHeightsFromFormatArray(JsonElement parent, string propName, HashSet<int> heights)
        {
            if (!parent.TryGetProperty(propName, out var arr) || arr.ValueKind != JsonValueKind.Array)
                return;

            foreach (var fmt in arr.EnumerateArray())
            {
                if (fmt.ValueKind != JsonValueKind.Object) continue;
                if (fmt.TryGetProperty("vcodec", out var vc) && vc.ValueKind == JsonValueKind.String)
                {
                    if (string.Equals(vc.GetString(), "none", StringComparison.OrdinalIgnoreCase)) continue;
                }
                if (fmt.TryGetProperty("mimeType", out var mt) && mt.ValueKind == JsonValueKind.String)
                {
                    string m = mt.GetString() ?? "";
                    if (m.StartsWith("audio/", StringComparison.OrdinalIgnoreCase)) continue;
                }
                int w = 0;
                if (fmt.TryGetProperty("width", out var wProp) && wProp.ValueKind == JsonValueKind.Number)
                {
                    wProp.TryGetInt32(out w);
                }
                if (fmt.TryGetProperty("height", out var hProp) && hProp.ValueKind == JsonValueKind.Number && hProp.TryGetInt32(out int h))
                {
                    int normalized = ResolveEffectiveQualityHeight(w, h);
                    if (normalized >= 144 && normalized <= 4320)
                    {
                        heights.Add(normalized);
                    }
                }
            }
        }

        public static List<VideoChapter> ExtractChaptersFromDescription(string description, double totalDurationSeconds = 0.0)
        {
            var chapters = new List<VideoChapter>();
            if (string.IsNullOrWhiteSpace(description)) return chapters;

            var lineRx = new System.Text.RegularExpressions.Regex(
                @"^\s*(?:(\d{1,2}):)?(\d{1,2}):(\d{2})\s*[-–—:|]?\s*(.+?)\s*$",
                System.Text.RegularExpressions.RegexOptions.Multiline | System.Text.RegularExpressions.RegexOptions.CultureInvariant);
            var seenTimes = new HashSet<int>();

            foreach (System.Text.RegularExpressions.Match m in lineRx.Matches(description))
            {
                int hh = 0;
                int h = m.Groups[1].Success && int.TryParse(m.Groups[1].Value, out hh) ? hh : 0;
                int min = int.TryParse(m.Groups[2].Value, out int mm) ? mm : 0;
                int sec = int.TryParse(m.Groups[3].Value, out int ss) ? ss : 0;
                if (min >= 60 || sec >= 60) continue;

                int totalSec = h * 3600 + min * 60 + sec;
                if (totalDurationSeconds > 0 && totalSec >= totalDurationSeconds) continue;
                if (!seenTimes.Add(totalSec)) continue;

                string title = m.Groups[4].Value.Trim();
                if (string.IsNullOrWhiteSpace(title)) continue;

                chapters.Add(new VideoChapter { StartSeconds = totalSec, Title = title });
            }

            chapters.Sort((a, b) => a.StartSeconds.CompareTo(b.StartSeconds));
            return chapters.Count >= 2 ? chapters : new List<VideoChapter>();
        }

        public static int ResolveEffectiveQualityHeight(int width, int height)
        {
            if (width <= 0 && height <= 0) return 0;

            int effective;
            if (width > 0 && height > 0)
            {
                // Vídeo vertical (Shorts 9:16): qualidade comercial é dada pelo lado curto (ex: 1080x1920 => 1080p)
                if (height > width)
                {
                    effective = width;
                }
                else
                {
                    // Paisagem ou Ultrawide (ex: 1920x800 => 1080p, 1280x720 => 720p, 640x480 => 480p)
                    int widescreenEquiv = (int)Math.Round(width * 9.0 / 16.0);
                    effective = Math.Max(height, widescreenEquiv);
                }
            }
            else
            {
                effective = height;
            }

            if (effective < 144 || effective > 4320) return 0;
            return NormalizeStandardHeight(effective);
        }

        private static int NormalizeStandardHeight(int h)
        {
            int[] std = { 144, 240, 360, 480, 720, 1080, 1440, 2160 };
            int best = h;
            int minDiff = int.MaxValue;
            for (int i = 0; i < std.Length; i++)
            {
                int diff = Math.Abs(std[i] - h);
                if (diff < minDiff)
                {
                    minDiff = diff;
                    best = std[i];
                }
            }
            return minDiff <= 60 ? best : h;
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
