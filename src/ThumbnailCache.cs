using System;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.IO;
using System.Net;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Forms;

namespace LiteTube
{
    /// <summary>
    /// Gerenciador assíncrono de download e cache em duplo nível (RAM LRU + Disco)
    /// de miniaturas do YouTube (vídeos, canais e playlists).
    /// </summary>
    public static class ThumbnailCache
    {
        public const int TARGET_THUMB_WIDTH = 360;
        public const int TARGET_THUMB_HEIGHT = 202;

        private const int MAX_RAM_THUMBNAILS = 60;
        private static int _maxDiskSizeMB = 15;

        public static Func<string, bool> IsVisibleCheck;

        // Pipeline com pool de 10 conexões simultâneas para CDN de assets estáticos
        private static readonly SemaphoreSlim _downloadSemaphore = new SemaphoreSlim(10, 10);

        private static readonly Dictionary<string, Image> _memoryCache = new Dictionary<string, Image>(StringComparer.OrdinalIgnoreCase);
        private static readonly LinkedList<string> _lruOrder = new LinkedList<string>();
        private static readonly HashSet<string> _inFlight = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        private static readonly Dictionary<string, List<Action>> _pendingCallbacks = new Dictionary<string, List<Action>>(StringComparer.OrdinalIgnoreCase);
        private static readonly object _lock = new object();
        private static readonly string _thumbDir;
        public static string ThumbDir { get { return _thumbDir; } }

        public static string GetDiskCachePath(string idOrKey)
        {
            string safeId = SanitizeId(idOrKey);
            if (string.IsNullOrEmpty(safeId)) return null;
            return Path.Combine(_thumbDir, safeId + ".jpg");
        }

        public static bool HasDiskCache(string idOrKey)
        {
            string path = GetDiskCachePath(idOrKey);
            if (string.IsNullOrEmpty(path)) return false;
            try
            {
                var fi = new FileInfo(path);
                return fi.Exists && fi.Length > 200;
            }
            catch
            {
                return false;
            }
        }
        private static int _downloadsSinceLastDiskCleanup = 0;

        static ThumbnailCache()
        {
            _thumbDir = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "data", "thumbs");
            if (!Directory.Exists(_thumbDir))
            {
                try { Directory.CreateDirectory(_thumbDir); } catch { }
            }

            try
            {
                if (ServicePointManager.DefaultConnectionLimit < 32)
                {
                    ServicePointManager.DefaultConnectionLimit = 32;
                }
            }
            catch { }

            EnforceDiskLimitMB(_maxDiskSizeMB);
        }

        public static void SetMaxDiskLimitMB(int maxMB)
        {
            if (maxMB >= 5 && maxMB <= 200)
            {
                _maxDiskSizeMB = maxMB;
                EnforceDiskLimitMB(_maxDiskSizeMB);
            }
        }

        public static Image GetOrRequestThumbnail(TrackItem item, Action onLoadedCallback)
        {
            if (item == null) return null;
            return GetOrRequestThumbnail(item.Id, item.CustomThumbUrl, onLoadedCallback);
        }

        public static Image GetOrRequestThumbnail(string idOrKey, Action onLoadedCallback)
        {
            return GetOrRequestThumbnail(idOrKey, null, onLoadedCallback);
        }

        public static Image GetOrRequestThumbnail(string idOrKey, string customUrl, Action onLoadedCallback)
        {
            string safeId = SanitizeId(idOrKey);
            if (safeId == null) return null;

            lock (_lock)
            {
                Image cached;
                if (_memoryCache.TryGetValue(safeId, out cached))
                {
                    TouchLruUnsafe(safeId);
                    if (onLoadedCallback != null)
                    {
                        ThreadPool.QueueUserWorkItem(_ =>
                        {
                            try { onLoadedCallback(); } catch { }
                        });
                    }
                    return cached;
                }

                // Registra callback para quando a requisição em trânsito finalizar
                if (onLoadedCallback != null)
                {
                    List<Action> cbList;
                    if (!_pendingCallbacks.TryGetValue(safeId, out cbList))
                    {
                        cbList = new List<Action>();
                        _pendingCallbacks[safeId] = cbList;
                    }
                    cbList.Add(onLoadedCallback);
                }

                if (_inFlight.Contains(safeId))
                {
                    return null;
                }

                // Marca requisição em trânsito (sem limite que descarte cards da tela)
                _inFlight.Add(safeId);
            }

            string diskPath = Path.Combine(_thumbDir, safeId + ".jpg");

            // Processamento 100% assíncrono em segundo plano (nunca bloqueia thread de UI ou OnPaint)
            Task.Run(async () =>
            {
                try
                {
                    // 1. Tenta carregar do cache local em disco (Fonte de verdade persistente: zero requisições de rede)
                    if (File.Exists(diskPath))
                    {
                        try
                        {
                            var fi = new FileInfo(diskPath);
                            if (fi.Length > 200)
                            {
                                Image diskImg = LoadResizedThumbnail(diskPath, TARGET_THUMB_WIDTH, TARGET_THUMB_HEIGHT);
                                if (diskImg != null)
                                {
                                    try { File.SetLastWriteTimeUtc(diskPath, DateTime.UtcNow); } catch { }

                                    List<Action> cbs = null;
                                    lock (_lock)
                                    {
                                        StoreInMemoryLruUnsafe(safeId, diskImg);
                                        List<Action> list;
                                        if (_pendingCallbacks.TryGetValue(safeId, out list))
                                        {
                                            cbs = new List<Action>(list);
                                            _pendingCallbacks.Remove(safeId);
                                        }
                                    }

                                    DispatchCallbacks(cbs);
                                    return; // Retorna imediatamente do disco, 0 requisições de rede
                                }
                            }
                            else
                            {
                                try { File.Delete(diskPath); } catch { }
                            }
                        }
                        catch { }
                    }

                    // Se o arquivo existe no disco, nunca bate na rede (evita risco de bloqueio de IP)
                    if (File.Exists(diskPath))
                    {
                        try
                        {
                            var fi = new FileInfo(diskPath);
                            if (fi.Length > 200)
                            {
                                Thread.Sleep(20);
                                Image retryImg = LoadResizedThumbnail(diskPath, TARGET_THUMB_WIDTH, TARGET_THUMB_HEIGHT);
                                if (retryImg != null)
                                {
                                    try { File.SetLastWriteTimeUtc(diskPath, DateTime.UtcNow); } catch { }
                                    List<Action> cbs = null;
                                    lock (_lock)
                                    {
                                        StoreInMemoryLruUnsafe(safeId, retryImg);
                                        List<Action> list;
                                        if (_pendingCallbacks.TryGetValue(safeId, out list))
                                        {
                                            cbs = new List<Action>(list);
                                            _pendingCallbacks.Remove(safeId);
                                        }
                                    }
                                    DispatchCallbacks(cbs);
                                    return;
                                }
                            }
                        }
                        catch { }
                    }

                    // 2. Fila de conexões simultâneas (10 downloads simultâneos)
                    await _downloadSemaphore.WaitAsync().ConfigureAwait(false);
                    try
                    {
                        // 3. Download isolado por item com retentativa pontual
                        byte[] bytes = DownloadThumbnailBytes(safeId, customUrl);
                        if (bytes != null && bytes.Length > 500)
                        {
                            SaveThumbnailToDiskSafe(diskPath, bytes);

                            Image img = LoadResizedThumbnailFromBytes(bytes, TARGET_THUMB_WIDTH, TARGET_THUMB_HEIGHT);
                            if (img != null)
                            {
                                List<Action> cbs = null;
                                lock (_lock)
                                {
                                    StoreInMemoryLruUnsafe(safeId, img);
                                    _downloadsSinceLastDiskCleanup++;
                                    List<Action> list;
                                    if (_pendingCallbacks.TryGetValue(safeId, out list))
                                    {
                                        cbs = new List<Action>(list);
                                        _pendingCallbacks.Remove(safeId);
                                    }
                                }

                                // Notifica a interface imediatamente
                                DispatchCallbacks(cbs);

                                // Limpeza periódica do limite de disco a cada 25 downloads
                                if (_downloadsSinceLastDiskCleanup >= 25)
                                {
                                    _downloadsSinceLastDiskCleanup = 0;
                                    ThreadPool.QueueUserWorkItem(delegate { EnforceDiskLimitMB(_maxDiskSizeMB); });
                                }
                            }
                        }
                    }
                    finally
                    {
                        _downloadSemaphore.Release();
                    }
                }
                catch (Exception ex)
                {
                    Logger.Warn("THUMB", string.Format("Exceção ao processar miniatura {0}: {1}", safeId, ex.Message));
                }
                finally
                {
                    lock (_lock)
                    {
                        _inFlight.Remove(safeId);
                        _pendingCallbacks.Remove(safeId);
                    }
                }
            });

            return null;
        }

        private static byte[] DownloadThumbnailBytes(string safeId, string customUrl)
        {
            string url = customUrl;
            if (string.IsNullOrEmpty(url))
            {
                url = "https://i.ytimg.com/vi/" + safeId + "/mqdefault.jpg";
            }
            else if (url.StartsWith("//"))
            {
                url = "https:" + url;
            }

            int maxAttempts = 2;
            for (int attempt = 1; attempt <= maxAttempts; attempt++)
            {
                try
                {
                    using (var resp = HttpPipeline.Client.GetAsync(url).GetAwaiter().GetResult())
                    {
                        if (resp.IsSuccessStatusCode)
                        {
                            return resp.Content.ReadAsByteArrayAsync().GetAwaiter().GetResult();
                        }

                        int statusCode = (int)resp.StatusCode;
                        if (statusCode == 429)
                        {
                            Logger.Warn("THUMB", string.Format("CDN retornou HTTP 429 para '{0}'. Tentativa {1}/{2}.", safeId, attempt, maxAttempts));
                            if (attempt < maxAttempts)
                            {
                                Thread.Sleep(1200);
                                continue;
                            }
                        }
                        else if (statusCode == 404)
                        {
                            if (!url.Contains("mqdefault.jpg") && !url.Contains("hqdefault.jpg"))
                            {
                                url = "https://i.ytimg.com/vi/" + safeId + "/mqdefault.jpg";
                                continue;
                            }
                            else if (url.Contains("mqdefault.jpg"))
                            {
                                url = "https://i.ytimg.com/vi/" + safeId + "/hqdefault.jpg";
                                continue;
                            }
                            break;
                        }
                    }
                }
                catch (Exception ex)
                {
                    if (attempt < maxAttempts)
                    {
                        Thread.Sleep(400);
                        continue;
                    }
                    Logger.Warn("THUMB", string.Format("Erro no download da miniatura {0}: {1}", safeId, ex.Message));
                    break;
                }
            }

            return null;
        }

        private static void SaveThumbnailToDiskSafe(string diskPath, byte[] bytes)
        {
            if (bytes == null || bytes.Length == 0) return;
            try
            {
                string dir = Path.GetDirectoryName(diskPath);
                if (!Directory.Exists(dir))
                {
                    Directory.CreateDirectory(dir);
                }
                string tempPath = diskPath + "." + Guid.NewGuid().ToString("N") + ".tmp";
                File.WriteAllBytes(tempPath, bytes);
                if (File.Exists(diskPath))
                {
                    try { File.Delete(diskPath); } catch { }
                }
                File.Move(tempPath, diskPath);
            }
            catch
            {
            }
        }

        private static bool CheckVisibilitySafe(string safeId)
        {
            return true;
        }

        private static void DispatchCallbacks(List<Action> callbacks)
        {
            if (callbacks == null || callbacks.Count == 0) return;
            for (int i = 0; i < callbacks.Count; i++)
            {
                var cb = callbacks[i];
                if (cb != null)
                {
                    SafeDispatchCallback(cb);
                }
            }
        }

        private static void SafeDispatchCallback(Action callback)
        {
            if (callback == null) return;
            try
            {
                Control control = callback.Target as Control;
                if (control == null && callback.Target != null)
                {
                    var fields = callback.Target.GetType().GetFields(
                        System.Reflection.BindingFlags.Public |
                        System.Reflection.BindingFlags.NonPublic |
                        System.Reflection.BindingFlags.Instance);
                    for (int i = 0; i < fields.Length; i++)
                    {
                        if (typeof(Control).IsAssignableFrom(fields[i].FieldType))
                        {
                            control = fields[i].GetValue(callback.Target) as Control;
                            if (control != null) break;
                        }
                    }
                }

                if (control != null)
                {
                    if (control.IsDisposed) return;
                    if (control.IsHandleCreated && control.InvokeRequired)
                    {
                        try
                        {
                            control.BeginInvoke(callback);
                            return;
                        }
                        catch { }
                    }
                }

                callback();
            }
            catch (Exception ex)
            {
                Logger.Warn("THUMB", "Erro ao despachar callback de miniatura: " + ex.Message);
            }
        }

        public static string SanitizeId(string idOrKey)
        {
            if (string.IsNullOrWhiteSpace(idOrKey)) return null;
            bool needsClean = false;
            for (int i = 0; i < idOrKey.Length; i++)
            {
                char c = idOrKey[i];
                if (!((c >= 'a' && c <= 'z') || (c >= 'A' && c <= 'Z') || (c >= '0' && c <= '9') || c == '_' || c == '-'))
                {
                    needsClean = true;
                    break;
                }
            }
            string safeId = needsClean ? Regex.Replace(idOrKey, "[^a-zA-Z0-9_-]", "") : idOrKey;
            if (string.IsNullOrEmpty(safeId)) return null;
            return safeId.Length > 48 ? safeId.Substring(0, 48) : safeId;
        }

        public static void PruneOffscreenExcept(ICollection<string> keepKeys)
        {
            if (keepKeys == null) return;
            var toDispose = new List<Image>();

            lock (_lock)
            {
                // Não purga cards desnecessariamente se estivermos dentro da cota de RAM (evita thrashing no scroll)
                if (_memoryCache.Count <= MAX_RAM_THUMBNAILS) return;

                var node = _lruOrder.First;
                while (node != null && _memoryCache.Count > MAX_RAM_THUMBNAILS)
                {
                    var next = node.Next;
                    string key = node.Value;
                    bool isProtected = key.StartsWith("ch_", StringComparison.OrdinalIgnoreCase);
                    if (!isProtected && !keepKeys.Contains(key))
                    {
                        Image oldImg;
                        if (_memoryCache.TryGetValue(key, out oldImg))
                        {
                            _memoryCache.Remove(key);
                            if (oldImg != null) toDispose.Add(oldImg);
                        }
                        _lruOrder.Remove(node);
                    }
                    node = next;
                }
            }

            for (int i = 0; i < toDispose.Count; i++)
            {
                try { toDispose[i].Dispose(); } catch { }
            }
        }

        private static void TouchLruUnsafe(string key)
        {
            if (_lruOrder.Last != null && string.Equals(_lruOrder.Last.Value, key, StringComparison.OrdinalIgnoreCase))
                return;
            _lruOrder.Remove(key);
            _lruOrder.AddLast(key);
        }

        private static void StoreInMemoryLruUnsafe(string key, Image img)
        {
            if (string.IsNullOrEmpty(key) || img == null) return;

            if (_memoryCache.ContainsKey(key))
            {
                Image existing = _memoryCache[key];
                _memoryCache[key] = img;
                TouchLruUnsafe(key);
                if (existing != null && existing != img)
                {
                    try { existing.Dispose(); } catch { }
                }
                return;
            }

            while (_memoryCache.Count >= MAX_RAM_THUMBNAILS && _lruOrder.First != null)
            {
                // Procura o item mais antigo que NÃO esteja atualmente visível na tela
                LinkedListNode<string> targetNode = null;
                var curr = _lruOrder.First;
                while (curr != null)
                {
                    string candidate = curr.Value;
                    if (!candidate.StartsWith("ch_", StringComparison.OrdinalIgnoreCase) && !CheckVisibilitySafe(candidate))
                    {
                        targetNode = curr;
                        break;
                    }
                    curr = curr.Next;
                }

                if (targetNode == null) targetNode = _lruOrder.First;

                string oldestKey = targetNode.Value;
                _lruOrder.Remove(targetNode);
                Image oldImg;
                if (_memoryCache.TryGetValue(oldestKey, out oldImg))
                {
                    _memoryCache.Remove(oldestKey);
                    if (oldImg != null)
                    {
                        try { oldImg.Dispose(); } catch { }
                    }
                }
            }

            _memoryCache[key] = img;
            _lruOrder.AddLast(key);
        }

        private static Image LoadResizedThumbnail(string path, int targetW, int targetH)
        {
            try
            {
                byte[] bytes = File.ReadAllBytes(path);
                return LoadResizedThumbnailFromBytes(bytes, targetW, targetH);
            }
            catch
            {
                return null;
            }
        }

        private static Image LoadResizedThumbnailFromBytes(byte[] bytes, int targetW, int targetH)
        {
            if (bytes == null || bytes.Length == 0) return null;
            try
            {
                using (var ms = new MemoryStream(bytes))
                using (var src = Image.FromStream(ms))
                {
                    var bmp = new Bitmap(targetW, targetH);
                    using (var g = Graphics.FromImage(bmp))
                    {
                        g.InterpolationMode = InterpolationMode.HighQualityBilinear;
                        g.SmoothingMode = SmoothingMode.HighSpeed;
                        g.PixelOffsetMode = PixelOffsetMode.HighSpeed;
                        g.DrawImage(src, 0, 0, targetW, targetH);
                    }
                    return bmp;
                }
            }
            catch
            {
                return null;
            }
        }

        public static void ClearMemoryCache()
        {
            var toDispose = new List<Image>();
            lock (_lock)
            {
                foreach (var img in _memoryCache.Values)
                {
                    if (img != null) toDispose.Add(img);
                }
                _memoryCache.Clear();
                _lruOrder.Clear();
                _inFlight.Clear();
                _pendingCallbacks.Clear();
            }
            for (int i = 0; i < toDispose.Count; i++)
            {
                try { toDispose[i].Dispose(); } catch { }
            }
        }

        public static double GetDiskUsageMB()
        {
            try
            {
                var dir = new DirectoryInfo(_thumbDir);
                if (!dir.Exists) return 0.0;
                long total = 0;
                foreach (var f in dir.GetFiles("*.jpg"))
                {
                    total += f.Length;
                }
                return Math.Round(total / (1024.0 * 1024.0), 1);
            }
            catch
            {
                return 0.0;
            }
        }

        public static void ClearDiskAndMemoryCache()
        {
            ClearMemoryCache();
            try
            {
                var dir = new DirectoryInfo(_thumbDir);
                if (!dir.Exists) return;
                foreach (var f in dir.GetFiles("*.jpg"))
                {
                    try { f.Delete(); } catch { }
                }
                foreach (var f in dir.GetFiles("*.tmp"))
                {
                    try { f.Delete(); } catch { }
                }
            }
            catch { }
        }

        public static void EnforceDiskLimitMB(int maxMB)
        {
            try
            {
                var dir = new DirectoryInfo(_thumbDir);
                if (!dir.Exists) return;

                try
                {
                    foreach (var tmp in dir.GetFiles("*.tmp"))
                    {
                        if (DateTime.UtcNow - tmp.LastWriteTimeUtc > TimeSpan.FromMinutes(10))
                        {
                            try { tmp.Delete(); } catch { }
                        }
                    }
                }
                catch { }

                FileInfo[] files = dir.GetFiles("*.jpg");
                long maxBytes = (long)Math.Max(5, maxMB) * 1024L * 1024L;
                long totalBytes = 0;
                for (int i = 0; i < files.Length; i++)
                {
                    totalBytes += files[i].Length;
                }

                if (totalBytes <= maxBytes && files.Length <= 800) return;

                Array.Sort(files, (a, b) => a.LastWriteTimeUtc.CompareTo(b.LastWriteTimeUtc));

                long targetBytes = (long)(maxBytes * 0.75);
                for (int i = 0; i < files.Length && (totalBytes > targetBytes || (files.Length - i) > 650); i++)
                {
                    long len = files[i].Length;
                    try
                    {
                        files[i].Delete();
                        totalBytes -= len;
                    }
                    catch { }
                }
            }
            catch { }
        }
    }
}
