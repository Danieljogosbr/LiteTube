using System;
using System.IO;
using System.Net;
using System.Net.Sockets;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading.Tasks;
using System.Windows.Forms;

namespace LiteTube
{
    /// <summary>
    /// Servidor HTTP local ultra-leve em segundo plano escutando na porta 48125 (http://127.0.0.1:48125/).
    /// Recebe comandos de reprodução de extensões do navegador, bookmarklets e scripts externos.
    /// Utiliza HttpListener com fallback automático para TcpListener em ambientes onde o driver http.sys não estiver disponível.
    /// </summary>
    public class ExternalLinkServer : IDisposable
    {
        private const string LISTENER_PREFIX = "http://127.0.0.1:48125/";
        private const int SERVER_PORT = 48125;
        private const int SW_RESTORE = 9;

        [DllImport("user32.dll")]
        private static extern bool SetForegroundWindow(IntPtr hWnd);

        [DllImport("user32.dll")]
        private static extern bool ShowWindowAsync(IntPtr hWnd, int nCmdShow);

        private readonly MainForm _mainForm;
        private HttpListener _httpListener;
        private TcpListener _tcpListener;
        private volatile bool _isRunning = false;

        public bool IsRunning
        {
            get
            {
                return _isRunning &&
                    ((_httpListener != null && _httpListener.IsListening) ||
                     (_tcpListener != null && _tcpListener.Server != null && _tcpListener.Server.IsBound));
            }
        }

        public ExternalLinkServer(MainForm mainForm)
        {
            _mainForm = mainForm;
        }

        /// <summary>
        /// Inicia o listener HTTP em segundo plano sem bloquear a thread de interface.
        /// Tenta primariamente HttpListener e, caso o sistema não suporte http.sys ou ocorra erro,
        /// realiza fallback transparente para TcpListener na porta 48125.
        /// </summary>
        public void Start()
        {
            if (_isRunning) return;
            _isRunning = true;

            Task.Run(() =>
            {
                // 1. Tentativa Primária: HttpListener nativo
                bool httpSysOk = false;
                try
                {
                    if (HttpListener.IsSupported)
                    {
                        _httpListener = new HttpListener();
                        _httpListener.Prefixes.Add(LISTENER_PREFIX);
                        _httpListener.Start();
                        httpSysOk = true;
                        Logger.Info("SERVER", "🌐 ExternalLinkServer ativo via HttpListener em " + LISTENER_PREFIX);

                        while (_isRunning && _httpListener != null && _httpListener.IsListening)
                        {
                            try
                            {
                                var context = _httpListener.GetContext();
                                Task.Run(() => HandleHttpContext(context));
                            }
                            catch (HttpListenerException)
                            {
                                break;
                            }
                            catch (ObjectDisposedException)
                            {
                                break;
                            }
                            catch (Exception ex)
                            {
                                if (!_isRunning) break;
                                Logger.Warn("SERVER", "Erro ao receber requisição HttpListener: " + ex.Message);
                            }
                        }
                        return;
                    }
                }
                catch (HttpListenerException ex)
                {
                    Logger.Warn("SERVER", "HttpListener indisponível ou porta restrita (" + ex.Message + "). Ativando fallback TcpListener...");
                }
                catch (Exception ex)
                {
                    Logger.Warn("SERVER", "HttpListener falhou (" + ex.Message + "). Ativando fallback TcpListener...");
                }

                // 2. Fallback Transparente: TcpListener HTTP Server (independente de http.sys)
                if (!httpSysOk && _isRunning)
                {
                    try
                    {
                        _tcpListener = new TcpListener(IPAddress.Loopback, SERVER_PORT);
                        _tcpListener.Start();
                        Logger.Info("SERVER", string.Format("🌐 ExternalLinkServer ativo via TcpListener em http://127.0.0.1:{0}/", SERVER_PORT));

                        while (_isRunning && _tcpListener != null)
                        {
                            try
                            {
                                var client = _tcpListener.AcceptTcpClient();
                                Task.Run(() => HandleTcpClient(client));
                            }
                            catch (SocketException)
                            {
                                break;
                            }
                            catch (ObjectDisposedException)
                            {
                                break;
                            }
                            catch (Exception ex)
                            {
                                if (!_isRunning) break;
                                Logger.Warn("SERVER", "Erro ao aceitar conexão TcpListener: " + ex.Message);
                            }
                        }
                    }
                    catch (Exception ex)
                    {
                        _isRunning = false;
                        Logger.Warn("SERVER", "Falha ao iniciar ExternalLinkServer via TcpListener: " + ex.Message);
                    }
                }
            });
        }

        #region HttpListener Request Handler
        private void HandleHttpContext(HttpListenerContext context)
        {
            try
            {
                var request = context.Request;
                var response = context.Response;

                // Cabeçalhos CORS irrestritos para compatibilidade com extensões e bookmarklets
                response.Headers["Access-Control-Allow-Origin"] = "*";
                response.Headers["Access-Control-Allow-Methods"] = "GET, POST, OPTIONS";
                response.Headers["Access-Control-Allow-Headers"] = "*";

                if (string.Equals(request.HttpMethod, "OPTIONS", StringComparison.OrdinalIgnoreCase))
                {
                    response.StatusCode = 200;
                    response.ContentLength64 = 0;
                    response.Close();
                    return;
                }

                string rawPath = request.Url != null ? request.Url.AbsolutePath : "";

                // Rota /ping
                if (string.Equals(rawPath, "/ping", StringComparison.OrdinalIgnoreCase))
                {
                    byte[] pongBytes = Encoding.UTF8.GetBytes("pong");
                    response.ContentType = "text/plain; charset=utf-8";
                    response.StatusCode = 200;
                    response.ContentLength64 = pongBytes.Length;
                    response.OutputStream.Write(pongBytes, 0, pongBytes.Length);
                    response.Close();
                    return;
                }

                // Rota /play?url={encodedUrl}
                if (string.Equals(rawPath, "/play", StringComparison.OrdinalIgnoreCase))
                {
                    string urlParam = request.QueryString["url"];

                    // Fallback para corpo POST ou query string direta
                    if (string.IsNullOrEmpty(urlParam) && request.HasEntityBody)
                    {
                        using (var reader = new StreamReader(request.InputStream, request.ContentEncoding))
                        {
                            string body = reader.ReadToEnd();
                            urlParam = ExtractUrlFromQueryStringOrBody(body);
                        }
                    }

                    if (string.IsNullOrEmpty(urlParam) && request.Url != null && !string.IsNullOrEmpty(request.Url.Query))
                    {
                        urlParam = ExtractUrlFromQueryStringOrBody(request.Url.Query);
                    }

                    if (!string.IsNullOrEmpty(urlParam))
                    {
                        string decodedUrl = Uri.UnescapeDataString(urlParam).Trim();
                        Logger.Info("SERVER", "📥 Comando /play recebido para: " + decodedUrl);

                        byte[] okBytes = Encoding.UTF8.GetBytes("OK: Reproduzindo");
                        response.ContentType = "text/plain; charset=utf-8";
                        response.StatusCode = 200;
                        response.ContentLength64 = okBytes.Length;
                        response.OutputStream.Write(okBytes, 0, okBytes.Length);
                        response.Close();

                        DispatchPlaybackToUi(decodedUrl);
                        return;
                    }
                    else
                    {
                        byte[] errBytes = Encoding.UTF8.GetBytes("Erro: Parâmetro 'url' ausente");
                        response.ContentType = "text/plain; charset=utf-8";
                        response.StatusCode = 400;
                        response.ContentLength64 = errBytes.Length;
                        response.OutputStream.Write(errBytes, 0, errBytes.Length);
                        response.Close();
                        return;
                    }
                }

                response.StatusCode = 404;
                response.Close();
            }
            catch (Exception ex)
            {
                Logger.Warn("SERVER", "Erro ao processar requisição HttpListener: " + ex.Message);
            }
        }
        #endregion

        #region TcpListener HTTP Request Handler (Fallback Engine)
        private void HandleTcpClient(TcpClient client)
        {
            try
            {
                using (client)
                using (var stream = client.GetStream())
                {
                    var reader = new StreamReader(stream, Encoding.UTF8);
                    string requestLine = reader.ReadLine();
                    if (string.IsNullOrEmpty(requestLine)) return;

                    string[] parts = requestLine.Split(' ');
                    if (parts.Length < 2) return;

                    string method = parts[0].ToUpperInvariant();
                    string rawUrl = parts[1];

                    // Lê cabeçalhos HTTP
                    int contentLength = 0;
                    string line;
                    while (!string.IsNullOrEmpty(line = reader.ReadLine()))
                    {
                        if (line.StartsWith("Content-Length:", StringComparison.OrdinalIgnoreCase))
                        {
                            int.TryParse(line.Substring(15).Trim(), out contentLength);
                        }
                    }

                    // Lê corpo se houver
                    string body = "";
                    if (contentLength > 0)
                    {
                        char[] buf = new char[contentLength];
                        int readTotal = 0;
                        while (readTotal < contentLength)
                        {
                            int r = reader.Read(buf, readTotal, contentLength - readTotal);
                            if (r <= 0) break;
                            readTotal += r;
                        }
                        body = new string(buf, 0, readTotal);
                    }

                    // Preflight CORS
                    if (method == "OPTIONS")
                    {
                        SendTcpHttpResponse(stream, 200, "OK", "text/plain", new byte[0]);
                        return;
                    }

                    string path = rawUrl;
                    int qIdx = rawUrl.IndexOf('?');
                    string query = "";
                    if (qIdx >= 0)
                    {
                        path = rawUrl.Substring(0, qIdx);
                        query = rawUrl.Substring(qIdx);
                    }

                    // Rota /ping
                    if (string.Equals(path, "/ping", StringComparison.OrdinalIgnoreCase))
                    {
                        byte[] pong = Encoding.UTF8.GetBytes("pong");
                        SendTcpHttpResponse(stream, 200, "OK", "text/plain; charset=utf-8", pong);
                        return;
                    }

                    // Rota /play
                    if (string.Equals(path, "/play", StringComparison.OrdinalIgnoreCase))
                    {
                        string urlParam = ExtractUrlFromQueryStringOrBody(query);
                        if (string.IsNullOrEmpty(urlParam) && !string.IsNullOrEmpty(body))
                        {
                            urlParam = ExtractUrlFromQueryStringOrBody(body);
                        }

                        if (!string.IsNullOrEmpty(urlParam))
                        {
                            string decodedUrl = Uri.UnescapeDataString(urlParam).Trim();
                            Logger.Info("SERVER", "📥 Comando /play recebido via TcpListener para: " + decodedUrl);

                            byte[] ok = Encoding.UTF8.GetBytes("OK: Reproduzindo");
                            SendTcpHttpResponse(stream, 200, "OK", "text/plain; charset=utf-8", ok);

                            DispatchPlaybackToUi(decodedUrl);
                            return;
                        }
                        else
                        {
                            byte[] err = Encoding.UTF8.GetBytes("Erro: Parâmetro 'url' ausente");
                            SendTcpHttpResponse(stream, 400, "Bad Request", "text/plain; charset=utf-8", err);
                            return;
                        }
                    }

                    SendTcpHttpResponse(stream, 404, "Not Found", "text/plain", new byte[0]);
                }
            }
            catch (Exception ex)
            {
                Logger.Warn("SERVER", "Erro ao tratar requisição TcpListener: " + ex.Message);
            }
        }

        private void SendTcpHttpResponse(NetworkStream stream, int statusCode, string statusMsg, string contentType, byte[] body)
        {
            try
            {
                string header = string.Format(
                    "HTTP/1.1 {0} {1}\r\n" +
                    "Access-Control-Allow-Origin: *\r\n" +
                    "Access-Control-Allow-Methods: GET, POST, OPTIONS\r\n" +
                    "Access-Control-Allow-Headers: *\r\n" +
                    "Content-Type: {2}\r\n" +
                    "Content-Length: {3}\r\n" +
                    "Connection: close\r\n\r\n",
                    statusCode, statusMsg, contentType, body != null ? body.Length : 0);

                byte[] headerBytes = Encoding.UTF8.GetBytes(header);
                stream.Write(headerBytes, 0, headerBytes.Length);
                if (body != null && body.Length > 0)
                {
                    stream.Write(body, 0, body.Length);
                }
                stream.Flush();
            }
            catch { }
        }
        #endregion

        private static string ExtractUrlFromQueryStringOrBody(string input)
        {
            if (string.IsNullOrEmpty(input)) return null;
            if (input.StartsWith("?")) input = input.Substring(1);

            var m = Regex.Match(input, @"(?:^|&)url=([^&]+)");
            if (m.Success)
            {
                return m.Groups[1].Value;
            }

            if (input.StartsWith("http://", StringComparison.OrdinalIgnoreCase) ||
                input.StartsWith("https://", StringComparison.OrdinalIgnoreCase))
            {
                return input.Trim();
            }

            return null;
        }

        private void DispatchPlaybackToUi(string decodedUrl)
        {
            if (_mainForm != null && !_mainForm.IsDisposed)
            {
                _mainForm.SafeInvoke(() =>
                {
                    RestoreAndFocusWindow();
                    _mainForm.PlayExternalUrl(decodedUrl);
                });
            }
        }

        /// <summary>
        /// Restaura a janela do LiteTube caso esteja na bandeja do sistema ou minimizada, e coloca-a em primeiro plano.
        /// </summary>
        public void RestoreAndFocusWindow()
        {
            try
            {
                if (_mainForm == null || _mainForm.IsDisposed) return;

                if (!_mainForm.Visible)
                {
                    _mainForm.Show();
                }

                if (_mainForm.WindowState == FormWindowState.Minimized)
                {
                    _mainForm.WindowState = FormWindowState.Normal;
                }

                ShowWindowAsync(_mainForm.Handle, SW_RESTORE);
                SetForegroundWindow(_mainForm.Handle);

                _mainForm.Activate();
                _mainForm.BringToFront();
            }
            catch (Exception ex)
            {
                Logger.Warn("SERVER", "Erro ao restaurar foco da janela: " + ex.Message);
            }
        }

        /// <summary>
        /// Encerra e descarta o listener HTTP liberando a porta 48125.
        /// </summary>
        public void Stop()
        {
            _isRunning = false;
            try
            {
                if (_httpListener != null)
                {
                    if (_httpListener.IsListening)
                    {
                        _httpListener.Stop();
                    }
                    _httpListener.Close();
                }
            }
            catch (Exception ex)
            {
                Logger.Warn("SERVER", "Erro ao encerrar HttpListener: " + ex.Message);
            }
            finally
            {
                _httpListener = null;
            }

            try
            {
                if (_tcpListener != null)
                {
                    _tcpListener.Stop();
                }
            }
            catch (Exception ex)
            {
                Logger.Warn("SERVER", "Erro ao encerrar TcpListener: " + ex.Message);
            }
            finally
            {
                _tcpListener = null;
            }
        }

        public void Dispose()
        {
            Stop();
        }
    }
}
