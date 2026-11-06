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
        }
    }
}
