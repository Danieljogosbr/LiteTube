#nullable enable
using System;
using System.Collections.Generic;
using System.IO;
using System.Net;
using System.Net.Http;
using System.Net.Security;
using System.Security.Authentication;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace LiteTube
{
    /// <summary>
    /// Modern managed HTTP pipeline for .NET 8 powered by native SocketsHttpHandler.
    /// Provides full support for HTTP/2, HTTP/3 (QUIC), connection pooling (64 connections per host),
    /// Brotli/Gzip/Deflate decompression, and TLS 1.2 / TLS 1.3 negotiation.
    /// </summary>
    public static class HttpPipeline
    {
        private static readonly SocketsHttpHandler _handler;
        private static readonly HttpClient _client;

        public const string DefaultUserAgent = "Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/125.0.0.0 Safari/537.36";

        static HttpPipeline()
        {
            _handler = new SocketsHttpHandler
            {
                // Pool de conexões otimizado para streaming e alta concorrência de miniaturas
                MaxConnectionsPerServer = 64,
                PooledConnectionLifetime = TimeSpan.FromMinutes(15),
                PooledConnectionIdleTimeout = TimeSpan.FromMinutes(2),
                KeepAlivePingDelay = TimeSpan.FromSeconds(30),
                KeepAlivePingTimeout = TimeSpan.FromSeconds(5),
                KeepAlivePingPolicy = HttpKeepAlivePingPolicy.Always,

                // Descompressão nativa de alta performance (GZip, Deflate e Brotli via SIMD)
                AutomaticDecompression = DecompressionMethods.All,

                // Suporte avançado a HTTP/2 Multiplexing com múltiplos canais
                EnableMultipleHttp2Connections = true,

                // Desativa cookies automáticos gerenciados pelo handler (LiteTube gerencia cookies Netscape)
                UseCookies = false,
                AllowAutoRedirect = true,
                MaxAutomaticRedirections = 5,

                // Negociação estrita de segurança TLS 1.2 e TLS 1.3
                SslOptions = new SslClientAuthenticationOptions
                {
                    EnabledSslProtocols = SslProtocols.Tls12 | SslProtocols.Tls13
                }
            };

            _client = new HttpClient(_handler)
            {
                Timeout = TimeSpan.FromSeconds(15),
                DefaultRequestVersion = HttpVersion.Version20,
                DefaultVersionPolicy = HttpVersionPolicy.RequestVersionOrLower
            };

            _client.DefaultRequestHeaders.UserAgent.ParseAdd(DefaultUserAgent);
            _client.DefaultRequestHeaders.AcceptLanguage.ParseAdd("pt-BR,pt;q=0.9,en-US;q=0.8,en;q=0.7");
        }

        public static HttpClient Client => _client;
        public static SocketsHttpHandler Handler => _handler;

        public static async Task<string> PostJsonAsync(string url, string jsonPayload, string? referer = null, IDictionary<string, string>? customHeaders = null, CancellationToken ct = default)
        {
            using (var req = new HttpRequestMessage(HttpMethod.Post, url))
            {
                req.Content = new StringContent(jsonPayload, Encoding.UTF8, "application/json");

                if (!string.IsNullOrEmpty(referer))
                {
                    req.Headers.Referrer = new Uri(referer);
                }

                if (customHeaders != null)
                {
                    foreach (var kv in customHeaders)
                    {
                        req.Headers.TryAddWithoutValidation(kv.Key, kv.Value);
                    }
                }

                using (var resp = await _client.SendAsync(req, HttpCompletionOption.ResponseHeadersRead, ct).ConfigureAwait(false))
                {
                    resp.EnsureSuccessStatusCode();
                    return await resp.Content.ReadAsStringAsync().ConfigureAwait(false);
                }
            }
        }

        public static string PostJson(string url, string jsonPayload, string? referer = null, IDictionary<string, string>? customHeaders = null)
        {
            return PostJsonAsync(url, jsonPayload, referer, customHeaders).GetAwaiter().GetResult();
        }

        public static async Task<byte[]?> GetByteArrayAsync(string url, CancellationToken ct = default)
        {
            try
            {
                using (var resp = await _client.GetAsync(url, HttpCompletionOption.ResponseHeadersRead, ct).ConfigureAwait(false))
                {
                    if (resp.IsSuccessStatusCode)
                    {
                        return await resp.Content.ReadAsByteArrayAsync().ConfigureAwait(false);
                    }
                }
            }
            catch
            {
                return null;
            }
            return null;
        }

        public static byte[]? GetByteArray(string url)
        {
            return GetByteArrayAsync(url).GetAwaiter().GetResult();
        }

        public static async Task<string?> GetStringAsync(string url, string? referer = null, CancellationToken ct = default)
        {
            using (var req = new HttpRequestMessage(HttpMethod.Get, url))
            {
                if (!string.IsNullOrEmpty(referer))
                {
                    req.Headers.Referrer = new Uri(referer);
                }

                using (var resp = await _client.SendAsync(req, HttpCompletionOption.ResponseHeadersRead, ct).ConfigureAwait(false))
                {
                    if (resp.IsSuccessStatusCode)
                    {
                        return await resp.Content.ReadAsStringAsync().ConfigureAwait(false);
                    }
                }
            }
            return null;
        }

        /// <summary>
        /// Pre-warms DNS resolution, TCP sockets, and TLS 1.3 handshakes in background
        /// for key YouTube and CDN endpoints (i.ytimg.com, www.youtube.com, music.youtube.com).
        /// </summary>
        public static async Task PrewarmEndpointsAsync(CancellationToken ct = default)
        {
            string[] endpoints = new string[]
            {
                "https://www.youtube.com/generate_204",
                "https://i.ytimg.com/generate_204",
                "https://music.youtube.com/generate_204"
            };

            var tasks = new List<Task>();
            foreach (string url in endpoints)
            {
                tasks.Add(Task.Run(async () =>
                {
                    try
                    {
                        using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
                        cts.CancelAfter(TimeSpan.FromSeconds(3));
                        using var req = new HttpRequestMessage(HttpMethod.Get, url);
                        using var resp = await _client.SendAsync(req, HttpCompletionOption.ResponseHeadersRead, cts.Token).ConfigureAwait(false);
                    }
                    catch { }
                }, ct));
            }

            await Task.WhenAll(tasks).ConfigureAwait(false);
        }
    }
}
