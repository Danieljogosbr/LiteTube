using System;
using System.IO;
using System.IO.Pipes;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Win32;

namespace LiteTube
{
    /// <summary>
    /// Servidor IPC local via Named Pipes ("LiteTube_IPC_Pipe") sem abertura de portas TCP
    /// e gerenciador do protocolo personalizado litetube:// no Registro do Windows (HKCU).
    /// Recebe comandos de instâncias secundárias do LiteTube (PING / PLAY|url / litetube://...) e despacha para a janela principal.
    /// </summary>
    public class ExternalLinkServer : IDisposable
    {
        public const string PipeName = "LiteTube_IPC_Pipe";
        public const string ProtocolScheme = "litetube";

        private static readonly Regex VideoIdOnlyRegex = new Regex("^[a-zA-Z0-9_-]{11}$", RegexOptions.Compiled);

        private readonly Action<string> _onPlayExternalUrl;
        private readonly Action _onRestoreWindow;
        private CancellationTokenSource _cts;
        private Task _listenTask;
        private volatile bool _isRunning;

        public bool IsRunning => _isRunning;

        public ExternalLinkServer(Action<string> onPlayExternalUrl, Action onRestoreWindow)
        {
            _onPlayExternalUrl = onPlayExternalUrl;
            _onRestoreWindow = onRestoreWindow;
        }

        public void Start()
        {
            if (_isRunning) return;

            _cts = new CancellationTokenSource();
            _isRunning = true;
            _listenTask = Task.Run(() => ListenLoopAsync(_cts.Token));
            Logger.Info("SERVER", "ExternalLinkServer ativo via NamedPipeServerStream (" + PipeName + ")");
        }

        private async Task ListenLoopAsync(CancellationToken token)
        {
            while (!token.IsCancellationRequested && _isRunning)
            {
                NamedPipeServerStream server = null;
                try
                {
                    server = new NamedPipeServerStream(
                        PipeName,
                        PipeDirection.InOut,
                        NamedPipeServerStream.MaxAllowedServerInstances,
                        PipeTransmissionMode.Byte,
                        PipeOptions.Asynchronous);

                    await server.WaitForConnectionAsync(token).ConfigureAwait(false);
                    _ = HandleClientAsync(server, token);
                }
                catch (OperationCanceledException)
                {
                    try { server?.Dispose(); } catch { }
                    break;
                }
                catch (Exception ex)
                {
                    try { server?.Dispose(); } catch { }
                    Logger.Warn("SERVER", "Erro no loop NamedPipeServerStream: " + ex.Message);
                    if (!token.IsCancellationRequested)
                    {
                        try { await Task.Delay(150, token).ConfigureAwait(false); } catch { break; }
                    }
                }
            }
        }

        private async Task HandleClientAsync(NamedPipeServerStream server, CancellationToken token)
        {
            try
            {
                using (server)
                using (var reader = new StreamReader(server, Encoding.UTF8, detectEncodingFromByteOrderMarks: false, bufferSize: 1024, leaveOpen: true))
                using (var writer = new StreamWriter(server, new UTF8Encoding(false), bufferSize: 1024, leaveOpen: true) { AutoFlush = true })
                {
                    string message = await reader.ReadLineAsync(token).ConfigureAwait(false);
                    if (string.IsNullOrWhiteSpace(message)) return;

                    string trimmed = message.Trim();

                    if (string.Equals(trimmed, "PING", StringComparison.OrdinalIgnoreCase))
                    {
                        await writer.WriteLineAsync("PONG").ConfigureAwait(false);
                        return;
                    }

                    if (trimmed.StartsWith("PLAY|", StringComparison.OrdinalIgnoreCase))
                    {
                        string rawPayload = trimmed.Substring(5).Trim();
                        string normalizedUrl = NormalizeExternalUrl(rawPayload);
                        if (!string.IsNullOrEmpty(normalizedUrl))
                        {
                            Logger.Info("SERVER", "Comando IPC PLAY recebido para: " + normalizedUrl);
                            await writer.WriteLineAsync("OK: Reproduzindo").ConfigureAwait(false);
                            DispatchPlaybackToUi(normalizedUrl);
                            return;
                        }
                    }
                    else if (trimmed.StartsWith("http://", StringComparison.OrdinalIgnoreCase) ||
                             trimmed.StartsWith("https://", StringComparison.OrdinalIgnoreCase) ||
                             trimmed.StartsWith(ProtocolScheme + ":", StringComparison.OrdinalIgnoreCase))
                    {
                        string normalizedUrl = NormalizeExternalUrl(trimmed);
                        if (!string.IsNullOrEmpty(normalizedUrl))
                        {
                            Logger.Info("SERVER", "URL IPC recebida diretamente para: " + normalizedUrl);
                            await writer.WriteLineAsync("OK: Reproduzindo").ConfigureAwait(false);
                            DispatchPlaybackToUi(normalizedUrl);
                            return;
                        }
                    }

                    await writer.WriteLineAsync("ERR: Comando invalido").ConfigureAwait(false);
                }
            }
            catch (OperationCanceledException) { }
            catch (Exception ex)
            {
                Logger.Warn("SERVER", "Erro ao processar mensagem NamedPipe: " + ex.Message);
            }
        }

        /// <summary>
        /// Registra o protocolo personalizado litetube:// em HKCU\Software\Classes\litetube
        /// sem exigir privilégios administrativos ou abertura de portas TCP.
        /// </summary>
        public static void EnsureProtocolRegistered()
        {
            try
            {
                string exePath = Environment.ProcessPath;
                if (string.IsNullOrWhiteSpace(exePath) ||
                    !exePath.EndsWith(".exe", StringComparison.OrdinalIgnoreCase) ||
                    !File.Exists(exePath))
                {
                    return;
                }

                string expectedCommand = "\"" + exePath + "\" \"%1\"";
                string expectedIcon = "\"" + exePath + "\",0";
                string rootSubKey = @"Software\Classes\" + ProtocolScheme;

                using (var existingCmd = Registry.CurrentUser.OpenSubKey(rootSubKey + @"\shell\open\command", writable: false))
                {
                    string currentCmd = existingCmd?.GetValue(null) as string;
                    if (string.Equals(currentCmd, expectedCommand, StringComparison.OrdinalIgnoreCase))
                    {
                        return;
                    }
                }

                using (var key = Registry.CurrentUser.CreateSubKey(rootSubKey, writable: true))
                {
                    if (key == null) return;
                    key.SetValue(null, "URL:LiteTube Protocol");
                    key.SetValue("URL Protocol", string.Empty);

                    using (var iconKey = key.CreateSubKey("DefaultIcon", writable: true))
                    {
                        iconKey?.SetValue(null, expectedIcon);
                    }

                    using (var cmdKey = key.CreateSubKey(@"shell\open\command", writable: true))
                    {
                        cmdKey?.SetValue(null, expectedCommand);
                    }
                }

                Logger.Info("PROTOCOL", "Protocolo " + ProtocolScheme + ":// registrado em HKCU para: " + exePath);
            }
            catch (Exception ex)
            {
                Logger.Warn("PROTOCOL", "Não foi possível registrar protocolo " + ProtocolScheme + ":// no Registro: " + ex.Message);
            }
        }

        /// <summary>
        /// Converte argumentos externos (litetube://play?url=..., litetube://https://..., URL direta ou Video ID)
        /// para uma URL HTTP/HTTPS reproduzível ou ID de vídeo de 11 caracteres.
        /// </summary>
        public static string NormalizeExternalUrl(string rawInput)
        {
            if (string.IsNullOrWhiteSpace(rawInput)) return string.Empty;

            string candidate = rawInput.Trim().Trim('"');

            if (candidate.StartsWith(ProtocolScheme + ":", StringComparison.OrdinalIgnoreCase))
            {
                string rest = candidate.Substring(ProtocolScheme.Length + 1);
                if (rest.StartsWith("//", StringComparison.Ordinal))
                {
                    rest = rest.Substring(2);
                }

                // Procura parametro ?url= ou &url= preservando case dos IDs de vídeo
                int qIdx = rest.IndexOf('?');
                if (qIdx >= 0)
                {
                    string queryPart = rest.Substring(qIdx + 1);
                    int urlParamIdx = queryPart.IndexOf("url=", StringComparison.OrdinalIgnoreCase);
                    if (urlParamIdx >= 0)
                    {
                        string encodedTarget = queryPart.Substring(urlParamIdx + 4);
                        candidate = Uri.UnescapeDataString(encodedTarget).Trim();
                    }
                    else
                    {
                        candidate = Uri.UnescapeDataString(rest).Trim();
                    }
                }
                else
                {
                    if (rest.StartsWith("play/", StringComparison.OrdinalIgnoreCase))
                    {
                        rest = rest.Substring(5);
                    }
                    candidate = Uri.UnescapeDataString(rest).Trim();
                }

                if (!string.IsNullOrEmpty(candidate) &&
                    !candidate.StartsWith("http://", StringComparison.OrdinalIgnoreCase) &&
                    !candidate.StartsWith("https://", StringComparison.OrdinalIgnoreCase) &&
                    !VideoIdOnlyRegex.IsMatch(candidate))
                {
                    candidate = "https://" + candidate.TrimStart('/');
                }
            }

            if (candidate.StartsWith("http://", StringComparison.OrdinalIgnoreCase) ||
                candidate.StartsWith("https://", StringComparison.OrdinalIgnoreCase) ||
                VideoIdOnlyRegex.IsMatch(candidate))
            {
                return candidate;
            }

            return string.Empty;
        }

        /// <summary>
        /// Extrai o instante inicial em segundos (t=872s, t=1m30s, t=1h2m3s, start=120) de uma URL do YouTube.
        /// </summary>
        public static double ExtractStartSecondsFromUrl(string url)
        {
            if (string.IsNullOrWhiteSpace(url)) return 0;

            var match = Regex.Match(url, @"[?&#](?:t|time_continue|start)=([0-9hmsHMS]+)", RegexOptions.IgnoreCase);
            if (!match.Success) return 0;

            string token = match.Groups[1].Value.Trim().ToLowerInvariant();
            if (string.IsNullOrEmpty(token)) return 0;

            if (double.TryParse(token.TrimEnd('s'), System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out double plainSec))
            {
                return plainSec > 0 ? plainSec : 0;
            }

            double total = 0;
            var hMatch = Regex.Match(token, @"(\d+)h");
            var mMatch = Regex.Match(token, @"(\d+)m");
            var sMatch = Regex.Match(token, @"(\d+)s");
            if (hMatch.Success && int.TryParse(hMatch.Groups[1].Value, out int h)) total += h * 3600;
            if (mMatch.Success && int.TryParse(mMatch.Groups[1].Value, out int m)) total += m * 60;
            if (sMatch.Success && int.TryParse(sMatch.Groups[1].Value, out int s)) total += s;
            return total > 0 ? total : 0;
        }

        /// <summary>
        /// Envia um comando síncrono para a instância ativa do LiteTube via NamedPipeClientStream.
        /// </summary>
        public static string SendCommand(string command, int timeoutMs = 1500)
        {
            if (string.IsNullOrWhiteSpace(command)) return string.Empty;

            using var client = new NamedPipeClientStream(".", PipeName, PipeDirection.InOut, PipeOptions.Asynchronous);
            client.Connect(timeoutMs);

            using var writer = new StreamWriter(client, new UTF8Encoding(false), bufferSize: 1024, leaveOpen: true) { AutoFlush = true };
            using var reader = new StreamReader(client, Encoding.UTF8, detectEncodingFromByteOrderMarks: false, bufferSize: 1024, leaveOpen: true);

            writer.WriteLine(command.Replace("\r", "").Replace("\n", "").Trim());
            string response = reader.ReadLine();
            return response ?? string.Empty;
        }

        private void DispatchPlaybackToUi(string decodedUrl)
        {
            try
            {
                _onRestoreWindow?.Invoke();
                _onPlayExternalUrl?.Invoke(decodedUrl);
            }
            catch (Exception ex)
            {
                Logger.Warn("SERVER", "Erro ao despachar link externo para UI: " + ex.Message);
            }
        }

        public void RestoreAndFocusWindow()
        {
            try
            {
                _onRestoreWindow?.Invoke();
            }
            catch (Exception ex)
            {
                Logger.Warn("SERVER", "Erro ao restaurar foco da janela: " + ex.Message);
            }
        }

        public void Stop()
        {
            _isRunning = false;
            try
            {
                _cts?.Cancel();
            }
            catch (Exception ex)
            {
                Logger.Warn("SERVER", "Erro ao encerrar NamedPipeServerStream: " + ex.Message);
            }
            finally
            {
                try { _cts?.Dispose(); } catch { }
                _cts = null;
            }
        }

        public void Dispose()
        {
            Stop();
        }
    }
}
