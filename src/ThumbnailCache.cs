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
    /// Gerenciador de miniaturas operando 100% em memória RAM (Memory Cache volátil / Zero Disco).
    /// Mantém compatibilidade com a interface WinForms enquanto descarta qualquer gravação em SSD/HDD.
    /// </summary>
    public static class ThumbnailCache
    {
        public const int TARGET_THUMB_WIDTH = 360;
        public const int TARGET_THUMB_HEIGHT = 202;

        private const int MAX_RAM_THUMBNAILS = 120;
        private static int _maxDiskSizeMB = 0; // Desativado: zero disco

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
            // 100% Memória RAM: não utiliza caminhos de arquivo em disco
            return null;
        }

        public static bool HasDiskCache(string idOrKey)
        {
            // 100% Memória RAM: zero leitura de disco
            return false;
        }

        static ThumbnailCache()
        {
            _thumbDir = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "data", "thumbs");
            
            // Rotina única no arranque para deletar a pasta legada data/thumbs e liberar espaço no disco
            try
            {
                if (Directory.Exists(_thumbDir))
                {
                    Directory.Delete(_thumbDir, true);
                    Logger.Info("THUMB", "Pasta legada data/thumbs removida com sucesso. ThumbnailCache operando 100% em RAM.");
                }
            }
            catch (Exception ex)
            {
                Logger.Warn("THUMB", "Aviso ao limpar pasta legada data/thumbs: " + ex.Message);
            }
        }

        public static void SetMaxDiskLimitMB(int maxMB)
        {
            // Desativado: operação 100% em memória RAM
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

                // Marca requisição em trânsito
                _inFlight.Add(safeId);
            }

            // Processamento 100% assíncrono em RAM (nunca toca no disco nem bloqueia a UI thread)
            Task.Run(async () =>
            {
                try
                {
                    await _downloadSemaphore.WaitAsync().ConfigureAwait(false);
                    try
                    {
                        byte[] bytes = await DownloadThumbnailBytesAsync(safeId, customUrl).ConfigureAwait(false);
                        if (bytes != null && bytes.Length > 200)
                        {
                            Image img = LoadResizedThumbnailFromBytes(bytes, TARGET_THUMB_WIDTH, TARGET_THUMB_HEIGHT);
                            if (img != null)
                            {
                                List<Action> cbs = null;
                                lock (_lock)
                                {
                                    StoreInMemoryLruUnsafe(safeId, img);
                                    List<Action> list;
                                    if (_pendingCallbacks.TryGetValue(safeId, out list))
                                    {
                                        cbs = new List<Action>(list);
                                        _pendingCallbacks.Remove(safeId);
                                    }
                                }

                                // Notifica a interface imediatamente
                                DispatchCallbacks(cbs);
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
                    Logger.Warn("THUMB", string.Format("Exceção ao processar miniatura {0} em RAM: {1}", safeId, ex.Message));
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

        private static async Task<byte[]> DownloadThumbnailBytesAsync(string safeId, string customUrl)
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
                    using (var resp = await HttpPipeline.Client.GetAsync(url).ConfigureAwait(false))
                    {
                        if (resp.IsSuccessStatusCode)
                        {
                            return await resp.Content.ReadAsByteArrayAsync().ConfigureAwait(false);
                        }

                        int statusCode = (int)resp.StatusCode;
                        if (statusCode == 429)
                        {
                            Logger.Warn("THUMB", string.Format("CDN retornou HTTP 429 para '{0}'. Tentativa {1}/{2}.", safeId, attempt, maxAttempts));
                            if (attempt < maxAttempts)
                            {
                                await Task.Delay(1000).ConfigureAwait(false);
                                continue;
                            }
                        }
                        else if (statusCode == 404)
                        {
                            if (url.Contains("maxresdefault.jpg"))
                            {
                                url = "https://i.ytimg.com/vi/" + safeId + "/hqdefault.jpg";
                                continue;
                            }
                            if (url.Contains("hqdefault.jpg"))
                            {
                                url = "https://i.ytimg.com/vi/" + safeId + "/mqdefault.jpg";
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
                        await Task.Delay(300).ConfigureAwait(false);
                        continue;
                    }
                    Logger.Warn("THUMB", string.Format("Erro no download da miniatura {0}: {1}", safeId, ex.Message));
                    break;
                }
            }

            return null;
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
                string oldestKey = _lruOrder.First.Value;
                _lruOrder.RemoveFirst();
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
            // Operação 100% em memória RAM: 0.0 MB de disco
            return 0.0;
        }

        public static void ClearDiskAndMemoryCache()
        {
            ClearMemoryCache();
            try
            {
                if (Directory.Exists(_thumbDir))
                {
                    Directory.Delete(_thumbDir, true);
                }
            }
            catch { }
        }

        public static void EnforceDiskLimitMB(int maxMB)
        {
            // Remove a pasta de disco se existir para manter zero gravação
            try
            {
                if (Directory.Exists(_thumbDir))
                {
                    Directory.Delete(_thumbDir, true);
                }
            }
            catch { }
        }
    }
}
