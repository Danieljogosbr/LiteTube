using System;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media.Imaging;

namespace AsyncImageLoader
{
    public static class ImageLoader
    {
        public static readonly DependencyProperty AsyncImageLoaderProperty =
            DependencyProperty.RegisterAttached("AsyncImageLoader", typeof(IAsyncImageLoader), typeof(ImageLoader),
                new FrameworkPropertyMetadata(null, FrameworkPropertyMetadataOptions.Inherits));

        public static readonly DependencyProperty SourceProperty =
            DependencyProperty.RegisterAttached("Source", typeof(string), typeof(ImageLoader),
                new PropertyMetadata(null, OnSourceChanged));

        public static readonly DependencyProperty IsLoadingProperty =
            DependencyProperty.RegisterAttached("IsLoading", typeof(bool), typeof(ImageLoader),
                new PropertyMetadata(false));

        private static readonly DependencyProperty CurrentCtsProperty =
            DependencyProperty.RegisterAttached("CurrentCts", typeof(CancellationTokenSource), typeof(ImageLoader),
                new PropertyMetadata(null));

        public static IAsyncImageLoader GetAsyncImageLoader(DependencyObject element)
        {
            if (element == null) return null;
            return (IAsyncImageLoader)element.GetValue(AsyncImageLoaderProperty);
        }

        public static void SetAsyncImageLoader(DependencyObject element, IAsyncImageLoader value)
        {
            if (element == null) return;
            element.SetValue(AsyncImageLoaderProperty, value);
        }

        public static string GetSource(Image element)
        {
            if (element == null) return null;
            return (string)element.GetValue(SourceProperty);
        }

        public static void SetSource(Image element, string value)
        {
            if (element == null) return;
            element.SetValue(SourceProperty, value);
        }

        public static bool GetIsLoading(Image element)
        {
            if (element == null) return false;
            return (bool)element.GetValue(IsLoadingProperty);
        }

        public static void SetIsLoading(Image element, bool value)
        {
            if (element == null) return;
            element.SetValue(IsLoadingProperty, value);
        }

        private static async void OnSourceChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
        {
            var image = d as Image;
            if (image == null) return;

            var oldCts = (CancellationTokenSource)image.GetValue(CurrentCtsProperty);
            if (oldCts != null)
            {
                oldCts.Cancel();
                oldCts.Dispose();
                image.SetValue(CurrentCtsProperty, null);
            }

            string url = e.NewValue as string;
            if (string.IsNullOrWhiteSpace(url))
            {
                image.Source = null;
                SetIsLoading(image, false);
                return;
            }

            var loader = GetAsyncImageLoader(image) ?? ImageLoaderBase.DefaultLoader;
            if (loader == null) return;

            var cts = new CancellationTokenSource();
            image.SetValue(CurrentCtsProperty, cts);
            SetIsLoading(image, true);

            try
            {
                BitmapSource bitmap = await loader.ProvideImageAsync(url).ConfigureAwait(false);

                if (!cts.IsCancellationRequested)
                {
                    var op = image.Dispatcher.BeginInvoke(new Action(() =>
                    {
                        if (!cts.IsCancellationRequested && GetSource(image) == url)
                        {
                            image.Source = bitmap;
                            SetIsLoading(image, false);
                        }
                    }));
                }
            }
            catch
            {
                if (!cts.IsCancellationRequested)
                {
                    var op = image.Dispatcher.BeginInvoke(new Action(() =>
                    {
                        if (!cts.IsCancellationRequested && GetSource(image) == url)
                        {
                            SetIsLoading(image, false);
                        }
                    }));
                }
            }
        }
    }
}
