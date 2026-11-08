using System;
using System.Net;
using System.Windows;

namespace LiteTube
{
    public class App : Application
    {
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
