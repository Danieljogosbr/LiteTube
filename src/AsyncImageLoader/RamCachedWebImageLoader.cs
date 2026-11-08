using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Threading.Tasks;
using System.Windows.Media.Imaging;

namespace AsyncImageLoader.Loaders
{
    public class RamCachedWebImageLoader : DiskCachedWebImageLoader
    {
        private readonly ConcurrentDictionary<string, BitmapSource> _memoryCache =
            new ConcurrentDictionary<string, BitmapSource>(StringComparer.OrdinalIgnoreCase);

        private readonly LinkedList<string> _lruList = new LinkedList<string>();
        private readonly object _lruLock = new object();
        private readonly int _maxRamItems;

        public RamCachedWebImageLoader(int maxRamItems = 120, string cacheFolder = null)
            : base(cacheFolder)
        {
            _maxRamItems = maxRamItems;
        }

        public override Task<BitmapSource> ProvideImageAsync(string url)
        {
            return ProvideImageAsync(url, 320);
        }

        public override async Task<BitmapSource> ProvideImageAsync(string url, int decodePixelWidth)
        {
            if (string.IsNullOrWhiteSpace(url)) return null;

            string cacheKey = decodePixelWidth > 0 ? string.Format("{0}#w={1}", url, decodePixelWidth) : url;

            // 1. Verificação instantânea em RAM
            BitmapSource cached;
            if (_memoryCache.TryGetValue(cacheKey, out cached))
            {
                TouchLru(cacheKey);
                return cached;
            }

            // 2. Carregamento de disco ou download com downsampling nativo
            var bitmap = await base.ProvideImageAsync(url, decodePixelWidth).ConfigureAwait(false);
            if (bitmap != null)
            {
                StoreInRam(cacheKey, bitmap);
            }

            return bitmap;
        }

        private void StoreInRam(string url, BitmapSource bitmap)
        {
            _memoryCache[url] = bitmap;
            lock (_lruLock)
            {
                _lruList.Remove(url);
                _lruList.AddFirst(url);

                while (_lruList.Count > _maxRamItems)
                {
                    var last = _lruList.Last;
                    if (last != null)
                    {
                        _lruList.RemoveLast();
                        BitmapSource removed;
                        _memoryCache.TryRemove(last.Value, out removed);
                    }
                }
            }
        }

        private void TouchLru(string url)
        {
            lock (_lruLock)
            {
                var node = _lruList.Find(url);
                if (node != null)
                {
                    _lruList.Remove(node);
                    _lruList.AddFirst(node);
                }
            }
        }

        public void ClearMemoryCache()
        {
            lock (_lruLock)
            {
                _memoryCache.Clear();
                _lruList.Clear();
            }
        }
    }
}
