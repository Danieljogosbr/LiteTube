using System;
using System.Collections.Concurrent;
using System.IO;
using System.Net;
using System.Net.Http;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Media.Imaging;

namespace AsyncImageLoader.Loaders
{
    /// <summary>
    /// Carregador moderno de imagens web operando 100% em memória RAM (Memory Cache volátil / Zero Disco / Zero SSD wear).
    /// Possui Bounded Cache thread-safe limitado a 200 imagens com downsampling nativo,
    /// decodificação assíncrona fora da UI thread (Freeze) e fallback automático 404 para CDNs do YouTube.
    /// </summary>
    public class MemoryCachedWebImageLoader : IAsyncImageLoader
    {
        private static HttpClient HttpClient => LiteTube.HttpPipeline.Client;

        // Cache em RAM: chave = $"{url}_{decodePixelWidth}"
        private static readonly ConcurrentDictionary<string, BitmapSource> _memoryCache 
            = new ConcurrentDictionary<string, BitmapSource>(StringComparer.OrdinalIgnoreCase);

        // Fila para política de descarte FIFO/LRU quando ultrapassar o limite máximo
        private static readonly ConcurrentQueue<string> _cacheKeysOrder 
            = new ConcurrentQueue<string>();

        private const int MaxCacheEntries = 200;

        public MemoryCachedWebImageLoader(int maxRamItems = 200, string cacheFolder = null)
        {
            // Construtor mantido para compatibilidade total; não realiza I/O de disco
        }

        public Task<BitmapSource> ProvideImageAsync(string url)
        {
            return ProvideImageAsync(url, 320);
        }

        public async Task<BitmapSource> ProvideImageAsync(string url, int decodePixelWidth)
        {
            return await LoadImageAsync(url, decodePixelWidth, CancellationToken.None).ConfigureAwait(false);
        }

        public static async Task<BitmapSource> LoadImageAsync(string url, int decodePixelWidth, CancellationToken cancellationToken = default)
        {
            if (string.IsNullOrWhiteSpace(url)) return null;
            if (decodePixelWidth <= 0) decodePixelWidth = 320;

            string cacheKey = string.Format("{0}_{1}", url, decodePixelWidth);

            // 1. Hit em Memória RAM (Retorno imediato em 0 ms)
            BitmapSource cachedBitmap;
            if (_memoryCache.TryGetValue(cacheKey, out cachedBitmap))
            {
                return cachedBitmap;
            }

            // 2. Download via SocketsHttpHandler (HTTP/2) com suporte a cancelamento
            byte[] imageBytes = await DownloadBytesWithFallbackAsync(url, cancellationToken).ConfigureAwait(false);
            if (imageBytes == null || imageBytes.Length == 0) return null;

            // 3. Decodificação com Downsampling e Freeze fora da UI Thread
            var bitmap = DecodeAndFreeze(imageBytes, decodePixelWidth);
            if (bitmap == null) return null;

            // 4. Armazena na RAM e aplica limite máximo (FIFO/LRU Bounded Cache)
            if (_memoryCache.TryAdd(cacheKey, bitmap))
            {
                _cacheKeysOrder.Enqueue(cacheKey);
                while (_cacheKeysOrder.Count > MaxCacheEntries && _cacheKeysOrder.TryDequeue(out var oldKey))
                {
                    _memoryCache.TryRemove(oldKey, out _);
                }
            }

            return bitmap;
        }

        public static BitmapSource DecodeAndFreeze(byte[] bytes, int decodePixelWidth)
        {
            try
            {
                using (var stream = new MemoryStream(bytes))
                {
                    var bitmap = new BitmapImage();
                    bitmap.BeginInit();
                    bitmap.StreamSource = stream;
                    if (decodePixelWidth > 0)
                    {
                        bitmap.DecodePixelWidth = decodePixelWidth;
                    }
                    bitmap.CreateOptions = BitmapCreateOptions.IgnoreColorProfile;
                    bitmap.CacheOption = BitmapCacheOption.OnLoad;
                    bitmap.EndInit();
                    if (bitmap.CanFreeze)
                    {
                        bitmap.Freeze(); // Torna imutável e seguro para envio à UI Thread
                    }
                    return bitmap;
                }
            }
            catch
            {
                return null;
            }
        }

        private static async Task<byte[]> DownloadBytesWithFallbackAsync(string url, CancellationToken ct)
        {
            if (string.IsNullOrWhiteSpace(url)) return null;
            if (url.StartsWith("//")) url = "https:" + url;

            try
            {
                var response = await HttpClient.GetAsync(url, ct).ConfigureAwait(false);

                // Fallback 404: se hqdefault ou maxres falhar, tenta mqdefault automaticamente
                if (response.StatusCode == HttpStatusCode.NotFound)
                {
                    if (url.Contains("maxresdefault.jpg"))
                        return await DownloadBytesWithFallbackAsync(url.Replace("maxresdefault.jpg", "hqdefault.jpg"), ct).ConfigureAwait(false);
                    if (url.Contains("hqdefault.jpg"))
                        return await DownloadBytesWithFallbackAsync(url.Replace("hqdefault.jpg", "mqdefault.jpg"), ct).ConfigureAwait(false);

                    var idMatch = Regex.Match(url, @"/vi(?:_webp)?/([a-zA-Z0-9_-]{11})");
                    if (idMatch.Success)
                    {
                        string vidId = idMatch.Groups[1].Value;
                        string mqUrl = "https://i.ytimg.com/vi/" + vidId + "/mqdefault.jpg";
                        if (!url.Equals(mqUrl, StringComparison.OrdinalIgnoreCase))
                        {
                            return await DownloadBytesWithFallbackAsync(mqUrl, ct).ConfigureAwait(false);
                        }
                    }
                    return null;
                }

                response.EnsureSuccessStatusCode();
                return await response.Content.ReadAsByteArrayAsync(ct).ConfigureAwait(false);
            }
            catch
            {
                return null;
            }
        }

        public static void ClearMemoryCache()
        {
            _memoryCache.Clear();
            while (_cacheKeysOrder.TryDequeue(out _)) { }
        }

        public virtual void Dispose()
        {
            ClearMemoryCache();
        }
    }

    // Aliases retrocompatíveis
    [Obsolete("Substituído por MemoryCachedWebImageLoader (100% RAM, zero disco)")]
    public class DiskCachedWebImageLoader : MemoryCachedWebImageLoader
    {
        public DiskCachedWebImageLoader(string cacheFolder = null) : base(200, cacheFolder) { }
    }

    [Obsolete("Substituído por MemoryCachedWebImageLoader (100% RAM, zero disco)")]
    public class RamCachedWebImageLoader : MemoryCachedWebImageLoader
    {
        public RamCachedWebImageLoader(int maxRamItems = 200, string cacheFolder = null) : base(maxRamItems, cacheFolder) { }
    }
}

namespace LiteTube.Network
{
    public class MemoryCachedWebImageLoader : AsyncImageLoader.Loaders.MemoryCachedWebImageLoader
    {
        public MemoryCachedWebImageLoader(int maxRamItems = 200, string cacheFolder = null) : base(maxRamItems, cacheFolder) { }
    }

    public static class LiteHttpClient
    {
        public static HttpClient Instance => LiteTube.HttpPipeline.Client;
    }
}
