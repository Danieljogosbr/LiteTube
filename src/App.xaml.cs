using System;
using System.IO;
using System.Net;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Markup;
using System.Windows.Media;
using System.Xml;

namespace LiteTube
{
    public class App : Application
    {
        public App()
        {
            LoadGlobalWinUiResources();
        }

        private void LoadGlobalWinUiResources()
        {
            try
            {
                string baseDir = AppDomain.CurrentDomain.BaseDirectory;
                string p1 = Path.Combine(baseDir, "src", "App.xaml");
                string p2 = Path.Combine(baseDir, "App.xaml");
                string xamlPath = File.Exists(p1) ? p1 : (File.Exists(p2) ? p2 : null);

                if (xamlPath != null)
                {
                    var doc = new XmlDocument();
                    doc.Load(xamlPath);
                    var nsmgr = new XmlNamespaceManager(doc.NameTable);
                    nsmgr.AddNamespace("p", "http://schemas.microsoft.com/winfx/2006/xaml/presentation");
                    var rdNode = doc.SelectSingleNode("//p:ResourceDictionary", nsmgr);
                    if (rdNode != null)
                    {
                        using (var sr = new StringReader(rdNode.OuterXml))
                        using (var xr = XmlReader.Create(sr))
                        {
                            var rd = XamlReader.Load(xr) as ResourceDictionary;
                            if (rd != null)
                            {
                                this.Resources.MergedDictionaries.Add(rd);
                                return;
                            }
                        }
                    }
                }
            }
            catch { }

            // Fallback programático garantindo disponibilidade imediata da paleta WinUI 3
            try
            {
                AddFrozenBrush("WinUiWindowBgBrush", 15, 15, 17);
                AddFrozenBrush("WinUiSurfaceElevatedBrush", 24, 24, 27);
                AddFrozenBrush("WinUiHoverHighlightBrush", 34, 34, 38);
                AddFrozenBrush("WinUiBorderBrush", 39, 39, 42);
                AddFrozenBrush("WinUiTextPrimaryBrush", 244, 244, 245);
                AddFrozenBrush("WinUiTextSecondaryBrush", 161, 161, 170);
                AddFrozenBrush("WinUiAccentBrush", 62, 166, 255);
                this.Resources["WinUiFontFamily"] = new FontFamily("Segoe UI Variable Text, Segoe UI");
            }
            catch { }
        }

        private void AddFrozenBrush(string key, byte r, byte g, byte b)
        {
            var brush = new SolidColorBrush(Color.FromRgb(r, g, b));
            brush.Freeze();
            this.Resources[key] = brush;
        }

        public static void InitializeNetworkPool()
        {
            // 1. Garante inicialização do pipeline moderno SocketsHttpHandler (.NET 8 LTS)
            _ = HttpPipeline.Client;

            // 2. Configura ServicePointManager como compatibilidade suplementar
            try
            {
                ServicePointManager.DefaultConnectionLimit = 64;
                ServicePointManager.Expect100Continue = false;
                ServicePointManager.UseNagleAlgorithm = false;
                ServicePointManager.SecurityProtocol = SecurityProtocolType.Tls12 | SecurityProtocolType.Tls13;
            }
            catch { }
        }

        protected override void OnStartup(StartupEventArgs e)
        {
            base.OnStartup(e);
            InitializeNetworkPool();
            PrewarmBackgroundServices();
        }

        private static bool _prewarmed = false;
        private static readonly object _prewarmLock = new object();

        /// <summary>
        /// Safely executes background pre-warming routines (yt-dlp Standby Cache and TLS 1.3 network pool)
        /// without blocking UI rendering or main thread startup.
        /// </summary>
        public static void PrewarmBackgroundServices()
        {
            lock (_prewarmLock)
            {
                if (_prewarmed) return;
                _prewarmed = true;
            }

            System.Threading.Tasks.Task.Run(async () =>
            {
                try
                {
                    // 1. Pré-aquecimento do executável yt-dlp (carrega binário Python na memória do Windows)
                    var ytTask = YtDlpResolver.PrewarmAsync();

                    // 2. Pré-aquecimento de conexões de rede e handshake TLS 1.3
                    var netTask = HttpPipeline.PrewarmEndpointsAsync();

                    await System.Threading.Tasks.Task.WhenAll(ytTask, netTask).ConfigureAwait(false);
                    Logger.Info("PREWARM", "⚡ Rotinas de pré-aquecimento seguro de cold start concluídas com êxito.");
                }
                catch (Exception ex)
                {
                    Logger.Warn("PREWARM", "Aviso seguro durante pré-aquecimento em background: " + ex.Message);
                }
            });
        }
    }
}
