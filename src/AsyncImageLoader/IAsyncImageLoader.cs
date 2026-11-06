using System;
using System.Threading.Tasks;
using System.Windows.Media.Imaging;

namespace AsyncImageLoader
{
    public interface IAsyncImageLoader : IDisposable
    {
        Task<BitmapSource> ProvideImageAsync(string url);
    }
}
