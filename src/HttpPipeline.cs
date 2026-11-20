#nullable enable
using System;
using System.Collections.Generic;
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
    /// Provides HTTP/2 multiplexing, balanced connection pooling (16 connections per host),
    /// Brotli/Gzip/Deflate decompression, TLS 1.2 / TLS 1.3 negotiation,
    /// lightweight API concurrency throttling, and automatic backoff on 429/503 responses.
    /// </summary>
    public static class HttpPipeline
    {
        private static readonly SocketsHttpHandler _handler;
        private static readonly HttpClient _client;
        private static readonly SemaphoreSlim _apiConcurrencyGate = new SemaphoreSlim(8, 8);
        private static long _rateLimitUntilTicks = 0;

        public const string DefaultUserAgent = "Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/125.0.0.0 Safari/537.36";

        static HttpPipeline()
        {
            _handler = new SocketsHttpHandler
            {
                // Desativa proxy herdado do sistema para evitar erros WSAEACCES (10013) em portas locais bloqueadas
                UseProxy = false,
                Proxy = null,
                // Pool equilibrado com HTTP/2 multiplexing (reduz consumo de RAM mantendo alta vazão)
                MaxConnectionsPerServer = 16,
                PooledConnectionLifetime = TimeSpan.FromMinutes(10),
                PooledConnectionIdleTimeout = TimeSpan.FromMinutes(2),
                KeepAlivePingDelay = TimeSpan.FromSeconds(30),
                KeepAlivePingTimeout = TimeSpan.FromSeconds(5),
                KeepAlivePingPolicy = HttpKeepAlivePingPolicy.WithActiveRequests,

                // Descompressão nativa de alta performance (GZip, Deflate e Brotli via SIMD)
                AutomaticDecompression = DecompressionMethods.All,

                // Suporte a HTTP/2 Multiplexing
                EnableMultipleHttp2Connections = true,

                // Desativa cookies automáticos gerenciados pelo handler (LiteTube gerencia cookies via CookieVault)
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

        private static async Task WaitIfRateLimitedAsync(CancellationToken ct)
        {
            long untilTicks = Interlocked.Read(ref _rateLimitUntilTicks);
            if (untilTicks <= 0) return;

            long nowTicks = DateTime.UtcNow.Ticks;
            if (untilTicks > nowTicks)
            {
                TimeSpan remaining = TimeSpan.FromTicks(untilTicks - nowTicks);
                if (remaining > TimeSpan.FromSeconds(15))
                {
                    remaining = TimeSpan.FromSeconds(15);
                }
                await Task.Delay(remaining, ct).ConfigureAwait(false);
            }
        }

        private static TimeSpan ComputeBackoffDelay(HttpResponseMessage resp, int attempt)
        {
            if (resp.Headers.RetryAfter != null)
            {
                if (resp.Headers.RetryAfter.Delta.HasValue)
                {
                    double sec = Math.Max(1.0, Math.Min(15.0, resp.Headers.RetryAfter.Delta.Value.TotalSeconds));
                    return TimeSpan.FromSeconds(sec);
                }
                if (resp.Headers.RetryAfter.Date.HasValue)
                {
                    double sec = (resp.Headers.RetryAfter.Date.Value.UtcDateTime - DateTime.UtcNow).TotalSeconds;
                    if (sec > 0.5)
                    {
                        return TimeSpan.FromSeconds(Math.Min(15.0, sec));
                    }
                }
            }

            // Backoff exponencial conservador: 1.0s na 1ª tentativa, 2.5s na 2ª tentativa
            return attempt == 0 ? TimeSpan.FromSeconds(1.0) : TimeSpan.FromSeconds(2.5);
        }

        private static void RecordRateLimitWindow(TimeSpan delay)
        {
            long targetTicks = DateTime.UtcNow.Add(delay).Ticks;
            long current;
            do
            {
                current = Interlocked.Read(ref _rateLimitUntilTicks);
                if (current >= targetTicks) return;
            } while (Interlocked.CompareExchange(ref _rateLimitUntilTicks, targetTicks, current) != current);
        }

        private static bool IsTransientRateLimitStatus(HttpStatusCode code)
        {
            return code == (HttpStatusCode)429 || code == HttpStatusCode.ServiceUnavailable;
        }

        public static async Task<string> PostJsonAsync(string url, string jsonPayload, string? referer = null, IDictionary<string, string>? customHeaders = null, CancellationToken ct = default)
        {
            await _apiConcurrencyGate.WaitAsync(ct).ConfigureAwait(false);
            try
            {
                const int maxAttempts = 3;
                for (int attempt = 0; attempt < maxAttempts; attempt++)
                {
                    await WaitIfRateLimitedAsync(ct).ConfigureAwait(false);

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
                            if (IsTransientRateLimitStatus(resp.StatusCode) && attempt < maxAttempts - 1)
                            {
                                TimeSpan delay = ComputeBackoffDelay(resp, attempt);
                                RecordRateLimitWindow(delay);
                                Logger.Warn("HTTP", string.Format("HTTP {0} em POST; aplicando backoff de {1:0.0}s (tentativa {2}/{3}).",
                                    (int)resp.StatusCode, delay.TotalSeconds, attempt + 1, maxAttempts));
                                continue;
                            }

                            resp.EnsureSuccessStatusCode();
                            return await resp.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
                        }
                    }
                }

                throw new HttpRequestException("Limite de tentativas atingido em PostJsonAsync.");
            }
            finally
            {
                _apiConcurrencyGate.Release();
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
                        return await resp.Content.ReadAsByteArrayAsync(ct).ConfigureAwait(false);
                    }
                }
            }
            catch
            {
                return null;
            }
            return null;
        }

        public static async Task<string?> GetStringAsync(string url, string? referer = null, CancellationToken ct = default)
        {
            await _apiConcurrencyGate.WaitAsync(ct).ConfigureAwait(false);
            try
            {
                const int maxAttempts = 3;
                for (int attempt = 0; attempt < maxAttempts; attempt++)
                {
                    await WaitIfRateLimitedAsync(ct).ConfigureAwait(false);

                    using (var req = new HttpRequestMessage(HttpMethod.Get, url))
                    {
                        if (!string.IsNullOrEmpty(referer))
                        {
                            req.Headers.Referrer = new Uri(referer);
                        }

                        using (var resp = await _client.SendAsync(req, HttpCompletionOption.ResponseHeadersRead, ct).ConfigureAwait(false))
                        {
                            if (IsTransientRateLimitStatus(resp.StatusCode) && attempt < maxAttempts - 1)
                            {
                                TimeSpan delay = ComputeBackoffDelay(resp, attempt);
                                RecordRateLimitWindow(delay);
                                Logger.Warn("HTTP", string.Format("HTTP {0} em GET; aplicando backoff de {1:0.0}s (tentativa {2}/{3}).",
                                    (int)resp.StatusCode, delay.TotalSeconds, attempt + 1, maxAttempts));
                                continue;
                            }

                            if (resp.IsSuccessStatusCode)
                            {
                                return await resp.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
                            }
                            return null;
                        }
                    }
                }
                return null;
            }
            finally
            {
                _apiConcurrencyGate.Release();
            }
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
