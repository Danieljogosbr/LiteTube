using System;

namespace AsyncImageLoader
{
    public static class ImageLoaderBase
    {
        private static IAsyncImageLoader _defaultLoader;
        private static readonly object _lock = new object();

        public static IAsyncImageLoader DefaultLoader
        {
            get
            {
                if (_defaultLoader == null)
                {
                    lock (_lock)
                    {
                        if (_defaultLoader == null)
                        {
                            _defaultLoader = new Loaders.MemoryCachedWebImageLoader();
                        }
                    }
                }
                return _defaultLoader;
            }
            set
            {
                lock (_lock)
                {
                    _defaultLoader = value;
                }
            }
        }
    }
}
