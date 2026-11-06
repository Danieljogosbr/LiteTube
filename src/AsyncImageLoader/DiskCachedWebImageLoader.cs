using System;
using System.IO;
using System.Net;
using System.Net.Http;
using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading.Tasks;
using System.Windows.Media.Imaging;

namespace AsyncImageLoader.Loaders
{
    public class DiskCachedWebImageLoader : IAsyncImageLoader
    {
        private static HttpClient HttpClient => LiteTube.HttpPipeline.Client;
        protected readonly string _cacheFolder;

        public DiskCachedWebImageLoader(string cacheFolder = null)
        {
            _cacheFolder = cacheFolder ?? Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "data", "thumbs");
            try
            {
                if (!Directory.Exists(_cacheFolder))
                {
                    Directory.CreateDirectory(_cacheFolder);
                }
            }
            catch { }
        }

        public virtual async Task<BitmapSource> ProvideImageAsync(string url)
        {
            if (string.IsNullOrWhiteSpace(url)) return null;

            // 1. Arquivo local direto
            if (File.Exists(url))
            {
                return LoadAndFreeze(url);
            }

            string diskPath = GetDiskCachePath(url);

            // 2. Cache em disco
            if (File.Exists(diskPath))
            {
                var cached = LoadAndFreeze(diskPath);
                if (cached != null) return cached;
            }

            // 3. Download da Web
            byte[] bytes = await DownloadBytesWithFallbackAsync(url).ConfigureAwait(false);
            if (bytes != null && bytes.Length > 200)
            {
                try
                {
                    File.WriteAllBytes(diskPath, bytes);
                }
                catch { }

                return DecodeAndFreeze(bytes);
            }

            return null;
        }

        protected virtual string GetDiskCachePath(string url)
        {
            // Extrai ID do YouTube se presente
            var m = Regex.Match(url, @"[?&]v=([a-zA-Z0-9_-]{11})|/vi(?:_webp)?/([a-zA-Z0-9_-]{11})");
            if (m.Success)
            {
                string id = !string.IsNullOrEmpty(m.Groups[1].Value) ? m.Groups[1].Value : m.Groups[2].Value;
                if (!string.IsNullOrEmpty(id))
                {
                    return Path.Combine(_cacheFolder, id + ".jpg");
                }
            }

            // Fallback para hash MD5 da URL
            using (var md5 = MD5.Create())
            {
                byte[] hash = md5.ComputeHash(Encoding.UTF8.GetBytes(url));
                StringBuilder sb = new StringBuilder();
                for (int i = 0; i < hash.Length; i++)
                    sb.Append(hash[i].ToString("x2"));
                return Path.Combine(_cacheFolder, sb.ToString() + ".jpg");
            }
        }

        private async Task<byte[]> DownloadBytesWithFallbackAsync(string initialUrl)
        {
            string url = initialUrl;
            if (url.StartsWith("//")) url = "https:" + url;

            // Tentativa 1: URL inicial
            try
            {
                var resp = await HttpClient.GetAsync(url).ConfigureAwait(false);
                if (resp.IsSuccessStatusCode)
                {
                    return await resp.Content.ReadAsByteArrayAsync().ConfigureAwait(false);
                }

                // Se 404 e for thumbnail do YouTube, tenta fallbacks
                if (resp.StatusCode == HttpStatusCode.NotFound)
                {
                    var idMatch = Regex.Match(url, @"/vi(?:_webp)?/([a-zA-Z0-9_-]{11})");
                    if (idMatch.Success)
                    {
                        string vidId = idMatch.Groups[1].Value;
                        string mqUrl = "https://i.ytimg.com/vi/" + vidId + "/mqdefault.jpg";
                        var respMq = await HttpClient.GetAsync(mqUrl).ConfigureAwait(false);
                        if (respMq.IsSuccessStatusCode)
                        {
                            return await respMq.Content.ReadAsByteArrayAsync().ConfigureAwait(false);
                        }

                        string hqUrl = "https://i.ytimg.com/vi/" + vidId + "/hqdefault.jpg";
                        var respHq = await HttpClient.GetAsync(hqUrl).ConfigureAwait(false);
                        if (respHq.IsSuccessStatusCode)
                        {
                            return await respHq.Content.ReadAsByteArrayAsync().ConfigureAwait(false);
                        }
                    }
                }
            }
            catch { }

            return null;
        }

        public static BitmapSource LoadAndFreeze(string filePath)
        {
            try
            {
                byte[] buffer;
                using (var fs = new FileStream(filePath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite))
                using (var ms = new MemoryStream((int)fs.Length))
                {
                    fs.CopyTo(ms);
                    buffer = ms.ToArray();
                }

                return DecodeAndFreeze(buffer);
            }
            catch
            {
                return null;
            }
        }

        public static BitmapSource DecodeAndFreeze(byte[] buffer)
        {
            try
            {
                var bitmap = new BitmapImage();
                bitmap.BeginInit();
                bitmap.CacheOption = BitmapCacheOption.OnLoad;
                bitmap.StreamSource = new MemoryStream(buffer);
                bitmap.DecodePixelWidth = 320; // Otimização de RAM
                bitmap.EndInit();

                if (bitmap.CanFreeze)
                    bitmap.Freeze();

                return bitmap;
            }
            catch
            {
                return null;
            }
        }

        public virtual void Dispose() { }
    }
}
