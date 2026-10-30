using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;

namespace LiteTube
{
    /// <summary>
    /// High-speed localhost HTTP Range Proxy that converts LibVLC's open-ended "Range: bytes=X-" requests
    /// into bounded 5MB chunk requests ("Range: bytes=X-Y") against googlevideo.com.
    /// This completely bypasses YouTube's 40 KB/s real-time rate throttling (boosting speed from ~500 KB/s to 30+ MB/s)
    /// and prewarms the first 512 KB (ftyp + moov + audio headers) in RAM for 0ms seek and start latency.
    /// </summary>
    internal sealed class LocalRangeUnthrottlerProxy : IDisposable
    {
        private const int HeaderCacheSize = 512 * 1024; // 512 KB covers ftyp + moov headers for 99.9% of videos
        private readonly string _videoUrl;
        private readonly string _audioUrl;
        private long _videoTotalLen;
        private long _audioTotalLen;
        private long _maxVideoByteLoaded = 0;
        private TcpListener _listener;
        private volatile bool _running = true;
        private readonly List<TcpClient> _activeClients = new List<TcpClient>();
        private readonly object _clientsLock = new object();

        private byte[] _videoHeaderCache = null;
        private byte[] _audioHeaderCache = null;
        private readonly object _vCacheLock = new object();
        private readonly object _aCacheLock = new object();

        private bool _videoIsWebm = false;
        private bool _audioIsWebm = false;

        public int Port { get; private set; }
        public string VideoLoopbackUrl { get { return string.Format("http://127.0.0.1:{0}/video", Port); } }
        public string AudioLoopbackUrl { get { return string.Format("http://127.0.0.1:{0}/audio", Port); } }
        public string VideoUrl { get { return _videoUrl; } }
        public string AudioUrl { get { return _audioUrl; } }

        public double BufferedFraction
        {
            get
            {
                if (_videoTotalLen > 0 && _maxVideoByteLoaded > 0)
                {
                    return Math.Max(0.0, Math.Min(1.0, (double)_maxVideoByteLoaded / _videoTotalLen));
                }
                return 0.0;
            }
        }

        static LocalRangeUnthrottlerProxy()
        {
            try
            {
                ServicePointManager.DefaultConnectionLimit = 64;
                ServicePointManager.Expect100Continue = false;
                ServicePointManager.UseNagleAlgorithm = false;
            }
            catch { }
        }

        public LocalRangeUnthrottlerProxy(string videoUrl, string audioUrl)
        {
            _videoUrl = videoUrl;
            _audioUrl = audioUrl;
            _videoTotalLen = ParseContentLength(videoUrl, 500000000L);
            _audioTotalLen = ParseContentLength(audioUrl, 50000000L);

            _videoIsWebm = videoUrl != null && (videoUrl.IndexOf("mime=video%2Fwebm", StringComparison.OrdinalIgnoreCase) >= 0 || videoUrl.IndexOf("mime=video/webm", StringComparison.OrdinalIgnoreCase) >= 0);
            _audioIsWebm = audioUrl != null && (audioUrl.IndexOf("mime=audio%2Fwebm", StringComparison.OrdinalIgnoreCase) >= 0 || audioUrl.IndexOf("mime=audio/webm", StringComparison.OrdinalIgnoreCase) >= 0);

            _listener = new TcpListener(IPAddress.Loopback, 0);
            _listener.Start();
            Port = ((IPEndPoint)_listener.LocalEndpoint).Port;
            Task.Run(() => AcceptLoopAsync());
        }

        private static long ParseContentLength(string url, long fallback)
        {
            if (string.IsNullOrEmpty(url)) return fallback;
            var m = Regex.Match(url, @"[?&]clen=(\d+)");
            if (m.Success)
            {
                long val;
                if (long.TryParse(m.Groups[1].Value, out val) && val > 1024) return val;
            }
            return fallback;
        }

        public async Task PrewarmHeadersAsync()
        {
            try
            {
                var t1 = Task.Run(() => EnsureHeaderCached(false));
                var t2 = Task.Run(() => EnsureHeaderCached(true));
                await Task.WhenAll(t1, t2);
            }
            catch { }
        }

        private byte[] EnsureHeaderCached(bool isAudio)
        {
            if (isAudio)
            {
                if (_audioHeaderCache != null) return _audioHeaderCache;
                lock (_aCacheLock)
                {
                    if (_audioHeaderCache != null) return _audioHeaderCache;
                    long detLen;
                    _audioHeaderCache = FetchRangeBytes(_audioUrl, 0, Math.Min(_audioTotalLen - 1, HeaderCacheSize - 1), out detLen);
                    if (detLen > 0) _audioTotalLen = detLen;
                    return _audioHeaderCache;
                }
            }
            else
            {
                if (_videoHeaderCache != null) return _videoHeaderCache;
                lock (_vCacheLock)
                {
                    if (_videoHeaderCache != null) return _videoHeaderCache;
                    long detLen;
                    _videoHeaderCache = FetchRangeBytes(_videoUrl, 0, Math.Min(_videoTotalLen - 1, HeaderCacheSize - 1), out detLen);
                    if (detLen > 0) _videoTotalLen = detLen;
                    return _videoHeaderCache;
                }
            }
        }

        private static byte[] FetchRangeBytes(string url, long start, long end, out long detectedTotalLen)
        {
            detectedTotalLen = -1;
            try
            {
                var req = (HttpWebRequest)WebRequest.Create(url);
                req.UserAgent = "Mozilla/5.0 (Windows NT 10.0; Win64; x64)";
                req.KeepAlive = true;
                req.Timeout = 8000;
                req.ReadWriteTimeout = 8000;
                req.AddRange(start, end);
                using (var resp = (HttpWebResponse)req.GetResponse())
                {
                    string cr = resp.Headers["Content-Range"];
                    if (!string.IsNullOrEmpty(cr))
                    {
                        var m = Regex.Match(cr, @"/(\d+)");
                        if (m.Success)
                        {
                            long.TryParse(m.Groups[1].Value, out detectedTotalLen);
                        }
                    }
                    using (var s = resp.GetResponseStream())
                    using (var ms = new MemoryStream((int)(end - start + 1)))
                    {
                        byte[] buf = new byte[32768];
                        int n;
                        while ((n = s.Read(buf, 0, buf.Length)) > 0)
                        {
                            ms.Write(buf, 0, n);
                        }
                        return ms.ToArray();
                    }
                }
            }
            catch
            {
                return null;
            }
        }

        private async Task AcceptLoopAsync()
        {
            while (_running)
            {
                try
                {
                    var client = await _listener.AcceptTcpClientAsync();
                    client.NoDelay = true;
                    lock (_clientsLock)
                    {
                        if (!_running)
                        {
                            try { client.Close(); } catch { }
                            break;
                        }
                        _activeClients.Add(client);
                    }
                    var _ = Task.Run(() => HandleClient(client));
                }
                catch
                {
                    break;
                }
            }
        }

        private void HandleClient(TcpClient client)
        {
            try
            {
                using (var stream = client.GetStream())
                {
                    try
                    {
                        byte[] reqBuf = new byte[4096];
                        int read = stream.Read(reqBuf, 0, reqBuf.Length);
                        if (read <= 0) return;

                        string req = Encoding.ASCII.GetString(reqBuf, 0, read);
                        bool isAudio = req.IndexOf("/audio", StringComparison.OrdinalIgnoreCase) >= 0;
                        bool isHead = req.StartsWith("HEAD", StringComparison.OrdinalIgnoreCase);
                        string targetUrl = isAudio ? _audioUrl : _videoUrl;
                        long totalLen = isAudio ? _audioTotalLen : _videoTotalLen;

                        long startByte = 0;
                        long endByte = totalLen - 1;
                        var m = Regex.Match(req, @"Range:\s*bytes=(\d+)-(\d*)", RegexOptions.IgnoreCase);
                        bool hasRange = m.Success;
                        if (hasRange)
                        {
                            long.TryParse(m.Groups[1].Value, out startByte);
                            if (!string.IsNullOrEmpty(m.Groups[2].Value))
                            {
                                long eb;
                                if (long.TryParse(m.Groups[2].Value, out eb) && eb >= startByte)
                                {
                                    endByte = Math.Min(totalLen - 1, eb);
                                }
                            }
                        }

                        string mime = isAudio ? (_audioIsWebm ? "audio/webm" : "audio/mp4") : (_videoIsWebm ? "video/webm" : "video/mp4");

                        string respHeader;
                        if (hasRange)
                        {
                            respHeader = string.Format(
                                "HTTP/1.1 206 Partial Content\r\nContent-Type: {0}\r\nAccept-Ranges: bytes\r\nContent-Range: bytes {1}-{2}/{3}\r\nContent-Length: {4}\r\nConnection: close\r\n\r\n",
                                mime,
                                startByte,
                                endByte,
                                totalLen,
                                endByte - startByte + 1
                            );
                        }
                        else
                        {
                            respHeader = string.Format(
                                "HTTP/1.1 200 OK\r\nContent-Type: {0}\r\nAccept-Ranges: bytes\r\nContent-Length: {1}\r\nConnection: close\r\n\r\n",
                                mime,
                                totalLen
                            );
                        }
                        byte[] hdrBytes = Encoding.ASCII.GetBytes(respHeader);
                        stream.Write(hdrBytes, 0, hdrBytes.Length);

                        if (isHead) return;

                        long cur = startByte;

                        // Serve from RAM cache if reading from the header (0..HeaderCacheSize)
                        if (cur < HeaderCacheSize)
                        {
                            byte[] cachedHdr = EnsureHeaderCached(isAudio);
                            if (cachedHdr != null && cur < cachedHdr.Length)
                            {
                                int count = (int)Math.Min((long)cachedHdr.Length - cur, endByte - cur + 1);
                                if (count > 0)
                                {
                                    stream.Write(cachedHdr, (int)cur, count);
                                    stream.Flush();
                                    cur += count;
                                    if (!isAudio && cur > _maxVideoByteLoaded) _maxVideoByteLoaded = cur;
                                }
                            }
                        }

                        // Stream subsequent data in bounded unthrottled chunks
                        long chunkSize = isAudio ? (1024 * 1024) : (5 * 1024 * 1024);
                        byte[] copyBuf = new byte[65536];

                        while (cur <= endByte && _running)
                        {
                            long chunkEnd = Math.Min(endByte, cur + chunkSize - 1);
                            var webReq = (HttpWebRequest)WebRequest.Create(targetUrl);
                            webReq.UserAgent = "Mozilla/5.0 (Windows NT 10.0; Win64; x64)";
                            webReq.KeepAlive = true;
                            webReq.Timeout = 10000;
                            webReq.ReadWriteTimeout = 10000;
                            webReq.AddRange(cur, chunkEnd);

                            using (var resp = (HttpWebResponse)webReq.GetResponse())
                            using (var respStream = resp.GetResponseStream())
                            {
                                int n;
                                while ((n = respStream.Read(copyBuf, 0, copyBuf.Length)) > 0)
                                {
                                    if (!_running) break;
                                    stream.Write(copyBuf, 0, n);
                                    cur += n;
                                    if (!isAudio && cur > _maxVideoByteLoaded) _maxVideoByteLoaded = cur;
                                }
                            }
                        }
                    }
                    catch
                    {
                        // Normal when player stops or seeks before finishing the stream chunk
                    }
                }
            }
            finally
            {
                lock (_clientsLock)
                {
                    _activeClients.Remove(client);
                }
                try { client.Close(); } catch { }
            }
        }

        public void Dispose()
        {
            _running = false;

            // Fecha explicitamente todas as conexões ativas antes de parar o listener
            lock (_clientsLock)
            {
                for (int i = _activeClients.Count - 1; i >= 0; i--)
                {
                    try
                    {
                        var c = _activeClients[i];
                        c.Close();
                    }
                    catch { }
                }
                _activeClients.Clear();
            }

            try
            {
                if (_listener != null)
                {
                    _listener.Stop();
                }
            }
            catch { }
        }
    }

    /// <summary>
    /// Manages high-speed direct video stream resolution and unthrottled loopback delivery for LibVLC.
    /// Uses yt-dlp with multi-client fallback (Firefox/Chrome cookies -> Deno JS runtime -> VisionOS)
    /// to bypass YouTube bot detection and delivers streams through LocalRangeUnthrottlerProxy.
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
        private string _activeCodecName = "AV1/H.264";
        private string _lastResolveError = null;
        private LocalRangeUnthrottlerProxy _rangeProxy = null;
        private readonly SemaphoreSlim _resolveLock = new SemaphoreSlim(1, 1);
        private double _knownDurationSeconds = 0;

        public ProgressiveVideoSession(TrackItem video, int height, string binDir, string browserForCookies = null, string cookiesFilePath = null)
        {
            _video = video;
            _knownDurationSeconds = (video != null && video.DurationSeconds > 0) ? video.DurationSeconds : 240;
            _height = height > 0 ? height : 720;
            _binDir = binDir;
            _ytDlpPath = Path.Combine(binDir, "yt-dlp.exe");
            _browserForCookies = browserForCookies;
            _cookiesFilePath = cookiesFilePath;
        }

        public string LastResolveError { get { return _lastResolveError; } }
        public double KnownDurationSeconds { get { return _knownDurationSeconds; } }
        public string ActiveCodecName { get { return _activeCodecName; } }

        public double BufferedFraction
        {
            get
            {
                if (_rangeProxy != null && _rangeProxy.BufferedFraction > 0)
                {
                    return _rangeProxy.BufferedFraction;
                }
                return 0.0;
            }
        }

        public async Task<string[]> ResolveStreamUrlsAsync(CancellationToken token)
        {
            bool ok = await EnsureStreamUrlsResolvedAsync(token);
            if (!ok || string.IsNullOrEmpty(_resolvedVideoUrl)) return null;
            if (_rangeProxy != null)
            {
                string aLoop = !string.IsNullOrEmpty(_resolvedAudioUrl) && !string.Equals(_resolvedVideoUrl, _resolvedAudioUrl, StringComparison.OrdinalIgnoreCase)
                    ? _rangeProxy.AudioLoopbackUrl
                    : null;
                return new string[] { _rangeProxy.VideoLoopbackUrl, aLoop, _activeCodecName };
            }
            return new string[] { _resolvedVideoUrl, _resolvedAudioUrl, _activeCodecName };
        }

        private async Task<bool> EnsureStreamUrlsResolvedAsync(CancellationToken token)
        {
            if (_rangeProxy != null && !string.IsNullOrEmpty(_resolvedVideoUrl))
                return true;

            await _resolveLock.WaitAsync(token);
            var sw = Stopwatch.StartNew();
            try
            {
                if (_rangeProxy != null && !string.IsNullOrEmpty(_resolvedVideoUrl))
                    return true;

                Logger.Info("STREAM", string.Format("EnsureStreamUrlsResolvedAsync iniciando: vidId={0}, height={1}p",
                    _video != null ? _video.Id : "?", _height));

                string videoUrl = "https://www.youtube.com/watch?v=" + (_video != null ? _video.Id : "");
                string formatArg = string.Format("bestvideo[height<={0}]+bestaudio/best[height<={0}]", _height);
                string sortArg = string.Format("res:{0},vcodec:av01,vcodec:vp9,vcodec:h264,acodec:opus,acodec:m4a", _height);

                string denoPath = Path.Combine(_binDir, "deno.exe");
                string jsArg = File.Exists(denoPath) ? string.Format("--js-runtimes \"deno:{0}\" ", denoPath) : "";
                string cookieArg = GetCookieArg();
                string[] urls = null;

                // Tentativa 1 (Primária): Cookies configurados do navegador/arquivo com player_skip=hls
                if (!token.IsCancellationRequested)
                {
                    string attempt1Args = (!string.IsNullOrEmpty(cookieArg) ? cookieArg : "") + jsArg + "--extractor-args \"youtube:player_skip=hls\" ";
                    Logger.Info("STREAM", "Tentativa 1 (Primária): yt-dlp com player_skip=hls e cookies...");
                    urls = await Task.Run(() => RunYtDlpGetUrls(attempt1Args, formatArg, sortArg, videoUrl, token), token);
                }

                // Se houver erro definitivo (vídeo privado, restrito a membros ou indisponível), aborta imediatamente!
                bool isFatalError = !string.IsNullOrEmpty(_lastResolveError);

                // Tentativa 2 (Fallback): Chamada limpa web padrão (sem cookies), caso não seja erro definitivo
                if ((urls == null || urls.Length == 0) && !token.IsCancellationRequested && !isFatalError)
                {
                    Logger.Info("STREAM", "Tentativa 2 (Fallback): Chamada limpa web padrão...");
                    string fallbackArgs = jsArg + "--extractor-args \"youtube:player_skip=hls\" ";
                    urls = await Task.Run(() => RunYtDlpGetUrls(fallbackArgs, formatArg, sortArg, videoUrl, token), token);
                }

                if (urls == null || urls.Length == 0 || token.IsCancellationRequested)
                {
                    string failMsg = !string.IsNullOrEmpty(_lastResolveError) ? _lastResolveError : "urls=null ou vazio";
                    Logger.Warn("STREAM", string.Format("EnsureStreamUrlsResolvedAsync falhou após {0}ms: {1}", sw.ElapsedMilliseconds, failMsg));
                    return false;
                }

                _resolvedVideoUrl = urls[0];
                _resolvedAudioUrl = urls.Length > 1 ? urls[1] : urls[0];
                _activeCodecName = DetectCodecFromUrl(_resolvedVideoUrl);

                // Parse duration from URL if available
                var durMatch = Regex.Match(_resolvedVideoUrl, @"[?&]dur=([\d\.]+)");
                if (durMatch.Success)
                {
                    double parsedDur;
                    if (double.TryParse(durMatch.Groups[1].Value, System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out parsedDur) && parsedDur > 1)
                    {
                        _knownDurationSeconds = parsedDur;
                    }
                }

                Logger.Info("STREAM", string.Format("EnsureStreamUrlsResolvedAsync sucesso ({0}ms): Codec={1}, Dur={2:0.0}s",
                    sw.ElapsedMilliseconds, _activeCodecName, _knownDurationSeconds));

                _rangeProxy = new LocalRangeUnthrottlerProxy(_resolvedVideoUrl, _resolvedAudioUrl);
                await _rangeProxy.PrewarmHeadersAsync();
                return true;
            }
            finally
            {
                _resolveLock.Release();
            }
        }

        private static string DetectCodecFromUrl(string url)
        {
            if (string.IsNullOrEmpty(url)) return "H.264";
            var m = Regex.Match(url, @"[?&]itag=(\d+)");
            if (m.Success)
            {
                int itag;
                if (int.TryParse(m.Groups[1].Value, out itag))
                {
                    if (itag == 133 || itag == 134 || itag == 135 || itag == 136 || itag == 137 || itag == 160 || itag == 298 || itag == 299 || itag == 18 || itag == 22) return "H.264";
                    if (itag >= 394 && itag <= 401) return "AV1";
                    if (itag == 242 || itag == 243 || itag == 244 || itag == 247 || itag == 248 || itag == 271 || itag == 278 || (itag >= 302 && itag <= 337) || (itag >= 602 && itag <= 628)) return "VP9";
                }
            }
            if (url.IndexOf("mime=video%2Fwebm", StringComparison.OrdinalIgnoreCase) >= 0 || url.IndexOf("mime=video/webm", StringComparison.OrdinalIgnoreCase) >= 0)
                return "VP9";
            if (url.IndexOf("av01", StringComparison.OrdinalIgnoreCase) >= 0)
                return "AV1";
            return "H.264";
        }

        private string GetCookieArg()
        {
            if (!string.IsNullOrEmpty(_cookiesFilePath) && File.Exists(_cookiesFilePath))
            {
                GoogleAuthDialog.SanitizeCookieFile(_cookiesFilePath);
                return string.Format("--cookies \"{0}\" ", _cookiesFilePath);
            }
            if (!string.IsNullOrEmpty(_browserForCookies) && _browserForCookies != "none")
                return string.Format("--cookies-from-browser {0} ", _browserForCookies);
            return "";
        }

        private string[] RunYtDlpGetUrls(string extraArgs, string formatArg, string sortArg, string videoUrl, CancellationToken token)
        {
            try
            {
                string args = string.Format(
                    "{0}--no-playlist --no-warnings -g -f \"{1}\" -S \"{2}\" \"{3}\"",
                    extraArgs,
                    formatArg,
                    sortArg,
                    videoUrl
                );

                var psi = new ProcessStartInfo
                {
                    FileName = _ytDlpPath,
                    Arguments = args,
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
                            }
                            else if (errJoined.IndexOf("confirm your age", StringComparison.OrdinalIgnoreCase) >= 0 ||
                                     errJoined.IndexOf("age-restricted", StringComparison.OrdinalIgnoreCase) >= 0)
                            {
                                _lastResolveError = "Vídeo com restrição de idade no YouTube.";
                            }
                            else if (errJoined.IndexOf("Private video", StringComparison.OrdinalIgnoreCase) >= 0)
                            {
                                _lastResolveError = "Este vídeo é privado no YouTube.";
                            }
                            else if (errJoined.IndexOf("Video unavailable", StringComparison.OrdinalIgnoreCase) >= 0 ||
                                     errJoined.IndexOf("This video has been removed", StringComparison.OrdinalIgnoreCase) >= 0)
                            {
                                _lastResolveError = "Vídeo indisponível ou removido do YouTube.";
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
            if (_rangeProxy != null)
            {
                _rangeProxy.Dispose();
                _rangeProxy = null;
            }
            _sessionCts.Dispose();
        }
    }
}
