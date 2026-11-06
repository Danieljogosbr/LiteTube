using System;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace AsyncImageLoader
{
    public static class ImageBrushLoader
    {
        public static readonly DependencyProperty SourceProperty =
            DependencyProperty.RegisterAttached("Source", typeof(string), typeof(ImageBrushLoader),
                new PropertyMetadata(null, OnSourceChanged));

        private static readonly DependencyProperty CurrentCtsProperty =
            DependencyProperty.RegisterAttached("CurrentCts", typeof(CancellationTokenSource), typeof(ImageBrushLoader),
                new PropertyMetadata(null));

        public static string GetSource(ImageBrush element)
        {
            if (element == null) return null;
            return (string)element.GetValue(SourceProperty);
        }

        public static void SetSource(ImageBrush element, string value)
        {
            if (element == null) return;
            element.SetValue(SourceProperty, value);
        }

        private static async void OnSourceChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
        {
            var brush = d as ImageBrush;
            if (brush == null) return;

            var oldCts = (CancellationTokenSource)brush.GetValue(CurrentCtsProperty);
            if (oldCts != null)
            {
                oldCts.Cancel();
                oldCts.Dispose();
                brush.SetValue(CurrentCtsProperty, null);
            }

            string url = e.NewValue as string;
            if (string.IsNullOrWhiteSpace(url))
            {
                brush.ImageSource = null;
                return;
            }

            var loader = ImageLoader.GetAsyncImageLoader(brush) ?? ImageLoaderBase.DefaultLoader;
            if (loader == null) return;

            var cts = new CancellationTokenSource();
            brush.SetValue(CurrentCtsProperty, cts);

            try
            {
                BitmapSource bitmap = await loader.ProvideImageAsync(url).ConfigureAwait(false);

                if (!cts.IsCancellationRequested)
                {
                    var op = brush.Dispatcher.BeginInvoke(new Action(() =>
                    {
                        if (!cts.IsCancellationRequested && GetSource(brush) == url)
                        {
                            brush.ImageSource = bitmap;
                        }
                    }));
                }
            }
            catch { }
        }
    }
}
