using System;
using System.Diagnostics;
using System.Drawing;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;
using System.Windows.Forms;
using Microsoft.Win32;

namespace LiteTube
{
    public class GoogleAuthDialog : Form
    {
        [DllImport("wininet.dll", SetLastError = true, CharSet = CharSet.Auto)]
        private static extern bool InternetGetCookieEx(
            string url,
            string cookieName,
            StringBuilder cookieData,
            ref int size,
            int flags,
            IntPtr pReserved);

        private const int INTERNET_COOKIE_HTTPONLY = 0x00002000;

        private readonly AppConfig _config;
        private readonly string _cookiesPath;
        private readonly string _binDir;
        private readonly string _ytDlpPath;

        private Label _lblStatus;
        private Label _lblCookieInfo;
        private Button _btnFirefox;
        private Button _btnChrome;
        private Button _btnFile;
        private Button _btnWebLogin;
        private Button _btnClear;
        private Button _btnClose;

        public event Action AuthChanged;

        public GoogleAuthDialog(AppConfig config)
        {
            _config = config;
            string dataDir = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "data");
            if (!Directory.Exists(dataDir)) Directory.CreateDirectory(dataDir);
            _cookiesPath = Path.Combine(dataDir, "cookies.txt");

            _binDir = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "bin");
            _ytDlpPath = Path.Combine(_binDir, "yt-dlp.exe");

            SetBrowserEmulation();
            InitializeComponent();
            UpdateStatusUI();
        }

        public static void SanitizeCookieFile(string path)
        {
            try
            {
                if (string.IsNullOrEmpty(path) || !File.Exists(path)) return;
                byte[] bytes = File.ReadAllBytes(path);
                if (bytes.Length >= 3 && bytes[0] == 0xEF && bytes[1] == 0xBB && bytes[2] == 0xBF)
                {
                    byte[] clean = new byte[bytes.Length - 3];
                    Buffer.BlockCopy(bytes, 3, clean, 0, clean.Length);
                    File.WriteAllBytes(path, clean);
                }
            }
            catch { }
        }

        private static void SetBrowserEmulation()
        {
            try
            {
                using (var key = Registry.CurrentUser.CreateSubKey(@"Software\Microsoft\Internet Explorer\Main\FeatureControl\FEATURE_BROWSER_EMULATION"))
                {
                    if (key != null)
                    {
                        string appName = Process.GetCurrentProcess().ProcessName + ".exe";
                        key.SetValue(appName, 11001, RegistryValueKind.DWord);
                    }
                }
            }
            catch { }
        }

        private void InitializeComponent()
        {
            Text = "LiteTube — Autenticação & Conta Google (Modo Autônomo)";
            Size = new Size(680, 520);
            StartPosition = FormStartPosition.CenterParent;
            FormBorderStyle = FormBorderStyle.FixedDialog;
            MaximizeBox = false;
            MinimizeBox = false;
            BackColor = Theme.Bg;
            ForeColor = Theme.TextMain;
            Font = new Font("Segoe UI", 9.5f, FontStyle.Regular);
            try
            {
                Icon = Icon.ExtractAssociatedIcon(Application.ExecutablePath) ?? SystemIcons.Application;
            }
            catch { }

            var topPanel = new Panel
            {
                Dock = DockStyle.Top,
                Height = 84,
                BackColor = Theme.Surface,
                Padding = new Padding(20, 14, 20, 10)
            };

            var lblTitle = new Label
            {
                Text = "🔑 Conexão com Conta Google (Sem Navegador)",
                Font = new Font("Segoe UI Semibold", 13f, FontStyle.Bold),
                ForeColor = Theme.TextMain,
                AutoSize = true,
                Location = new Point(18, 12)
            };

            var lblSub = new Label
            {
                Text = "Torne o LiteTube 100% independente salvando seus cookies de autenticação no arquivo local.",
                Font = new Font("Segoe UI", 8.8f),
                ForeColor = Theme.TextDim,
                AutoSize = true,
                Location = new Point(20, 44)
            };

            topPanel.Controls.Add(lblTitle);
            topPanel.Controls.Add(lblSub);

            var contentPanel = new Panel
            {
                Dock = DockStyle.Fill,
                Padding = new Padding(24, 16, 24, 16),
                BackColor = Theme.Bg
            };

            var grpStatus = new Panel
            {
                Location = new Point(24, 16),
                Size = new Size(616, 85),
                BackColor = Theme.SurfaceHigh
            };

            _lblStatus = new Label
            {
                Location = new Point(16, 12),
                Size = new Size(584, 28),
                Font = new Font("Segoe UI Semibold", 10.5f, FontStyle.Bold),
                ForeColor = Color.FromArgb(0, 212, 130),
                Text = "● Status: Verificando..."
            };

            _lblCookieInfo = new Label
            {
                Location = new Point(16, 44),
                Size = new Size(584, 30),
                Font = new Font("Segoe UI", 8.6f),
                ForeColor = Theme.TextDim,
                Text = "Carregando..."
            };

            grpStatus.Controls.Add(_lblStatus);
            grpStatus.Controls.Add(_lblCookieInfo);

            int btnY = 118;
            int btnW = 296;
            int btnH = 46;

            _btnFirefox = CreateActionButton("🦊 1-Clique: Extrair do Firefox", 24, btnY, btnW, btnH, Color.FromArgb(220, 90, 30));
            _btnFirefox.Click += (s, e) => ExportCookiesFromBrowser("firefox");

            _btnChrome = CreateActionButton("🌐 1-Clique: Extrair do Chrome", 344, btnY, btnW, btnH, Color.FromArgb(40, 120, 220));
            _btnChrome.Click += (s, e) => ExportCookiesFromBrowser("chrome");

            btnY += 56;
            _btnWebLogin = CreateActionButton("🔑 Login Direto no Google (Janela)", 24, btnY, btnW, btnH, Theme.Accent);
            _btnWebLogin.Click += (s, e) => OpenWebLoginWindow();

            _btnFile = CreateActionButton("📁 Importar Arquivo cookies.txt", 344, btnY, btnW, btnH, Color.FromArgb(45, 45, 60));
            _btnFile.Click += (s, e) => BrowseCookiesFile();

            btnY += 64;
            _btnClear = new Button
            {
                Text = "🗑️ Desconectar / Remover Cookies Locais",
                Location = new Point(24, btnY),
                Size = new Size(320, 36),
                FlatStyle = FlatStyle.Flat,
                BackColor = Color.FromArgb(32, 20, 20),
                ForeColor = Color.FromArgb(255, 110, 110),
                Cursor = Cursors.Hand,
                Font = new Font("Segoe UI", 8.8f)
            };
            _btnClear.FlatAppearance.BorderSize = 1;
            _btnClear.FlatAppearance.BorderColor = Color.FromArgb(70, 30, 30);
            _btnClear.Click += (s, e) => ClearAuth();

            _btnClose = new Button
            {
                Text = "Concluir",
                Location = new Point(510, btnY),
                Size = new Size(130, 36),
                FlatStyle = FlatStyle.Flat,
                BackColor = Theme.SurfaceHigh,
                ForeColor = Theme.TextMain,
                Cursor = Cursors.Hand,
                Font = new Font("Segoe UI Semibold", 9.5f, FontStyle.Bold)
            };
            _btnClose.FlatAppearance.BorderSize = 0;
            _btnClose.Click += (s, e) => Close();

            contentPanel.Controls.Add(grpStatus);
            contentPanel.Controls.Add(_btnFirefox);
            contentPanel.Controls.Add(_btnChrome);
            contentPanel.Controls.Add(_btnWebLogin);
            contentPanel.Controls.Add(_btnFile);
            contentPanel.Controls.Add(_btnClear);
            contentPanel.Controls.Add(_btnClose);

            Controls.Add(contentPanel);
            Controls.Add(topPanel);
        }

        private Button CreateActionButton(string text, int x, int y, int w, int h, Color color)
        {
            var btn = new Button
            {
                Text = text,
                Location = new Point(x, y),
                Size = new Size(w, h),
                FlatStyle = FlatStyle.Flat,
                BackColor = color,
                ForeColor = Color.White,
                Cursor = Cursors.Hand,
                Font = new Font("Segoe UI Semibold", 9.6f, FontStyle.Bold)
            };
            btn.FlatAppearance.BorderSize = 0;
            return btn;
        }

        private void UpdateStatusUI()
        {
            bool hasCustomFile = !string.IsNullOrEmpty(_config.CookiesFilePath) && File.Exists(_config.CookiesFilePath);
            if (hasCustomFile)
            {
                var fi = new FileInfo(_config.CookiesFilePath);
                _lblStatus.Text = "✅ Modo Autônomo Ativo: Conta Google Conectada";
                _lblStatus.ForeColor = Color.FromArgb(0, 212, 130);
                _lblCookieInfo.Text = string.Format("Arquivo local: {0} ({1:0.0} KB • Modificado: {2:dd/MM/yyyy HH:mm})",
                    Path.GetFileName(_config.CookiesFilePath), fi.Length / 1024.0, fi.LastWriteTime);
            }
            else if (!string.IsNullOrEmpty(_config.BrowserForCookies) && _config.BrowserForCookies != "none")
            {
                _lblStatus.Text = string.Format("🦊 Modo Conectado via Navegador: {0}", _config.BrowserForCookies.ToUpper());
                _lblStatus.ForeColor = Color.FromArgb(255, 180, 0);
                _lblCookieInfo.Text = "O LiteTube lê os cookies diretamente do navegador informado.";
            }
            else
            {
                _lblStatus.Text = "⚠️ Nenhuma conta conectada (Modo Anônimo)";
                _lblStatus.ForeColor = Color.FromArgb(200, 200, 200);
                _lblCookieInfo.Text = "Conecte sua conta para acessar histórico, inscrições e evitar restrições de idade.";
            }
        }

        private void ExportCookiesFromBrowser(string browser)
        {
            if (!File.Exists(_ytDlpPath))
            {
                MessageBox.Show(this, "O yt-dlp.exe não foi encontrado na pasta bin.", "Erro", MessageBoxButtons.OK, MessageBoxIcon.Error);
                return;
            }

            _lblStatus.Text = string.Format("⏳ Extraindo cookies do {0}...", browser);
            _lblStatus.ForeColor = Color.FromArgb(255, 200, 0);
            Refresh();

            try
            {
                var psi = new ProcessStartInfo
                {
                    FileName = _ytDlpPath,
                    Arguments = string.Format("--cookies-from-browser {0} --cookies \"{1}\" --skip-download \"https://www.youtube.com/\"", browser, _cookiesPath),
                    UseShellExecute = false,
                    CreateNoWindow = true,
                    RedirectStandardOutput = true,
                    RedirectStandardError = true
                };

                using (var p = Process.Start(psi))
                {
                    p.WaitForExit(15000);
                }

                if (File.Exists(_cookiesPath) && new FileInfo(_cookiesPath).Length > 200)
                {
                    SanitizeCookieFile(_cookiesPath);
                    _config.CookiesFilePath = _cookiesPath;
                    _config.BrowserForCookies = "none";
                    _config.Save();

                    UpdateStatusUI();
                    if (AuthChanged != null) AuthChanged();
                    MessageBox.Show(this, string.Format("✅ Cookies do {0} importados com sucesso!\nO LiteTube agora é totalmente autônomo.", browser.ToUpper()),
                        "Sucesso", MessageBoxButtons.OK, MessageBoxIcon.Information);
                }
                else
                {
                    MessageBox.Show(this, string.Format("Não foi possível extrair os cookies do {0}. Verifique se o navegador está fechado ou se você está logado na sua conta Google nele.", browser),
                        "Aviso", MessageBoxButtons.OK, MessageBoxIcon.Warning);
                    UpdateStatusUI();
                }
            }
            catch (Exception ex)
            {
                MessageBox.Show(this, "Erro ao extrair cookies: " + ex.Message, "Erro", MessageBoxButtons.OK, MessageBoxIcon.Error);
                UpdateStatusUI();
            }
        }

        private void BrowseCookiesFile()
        {
            using (var ofd = new System.Windows.Forms.OpenFileDialog())
            {
                ofd.Filter = "Arquivos de Cookies (*.txt)|*.txt|Todos os Arquivos (*.*)|*.*";
                ofd.Title = "Selecione o arquivo de cookies exportado";
                if (ofd.ShowDialog(this) == DialogResult.OK)
                {
                    try
                    {
                        File.Copy(ofd.FileName, _cookiesPath, true);
                        SanitizeCookieFile(_cookiesPath);
                        _config.CookiesFilePath = _cookiesPath;
                        _config.BrowserForCookies = "none";
                        _config.Save();

                        UpdateStatusUI();
                        if (AuthChanged != null) AuthChanged();
                        MessageBox.Show(this, "✅ Arquivo de cookies configurado com sucesso!", "Sucesso", MessageBoxButtons.OK, MessageBoxIcon.Information);
                    }
                    catch (Exception ex)
                    {
                        MessageBox.Show(this, "Erro ao carregar arquivo: " + ex.Message, "Erro", MessageBoxButtons.OK, MessageBoxIcon.Error);
                    }
                }
            }
        }

        private void OpenWebLoginWindow()
        {
            var loginForm = new Form
            {
                Text = "Google Login Seguro — LiteTube",
                Size = new Size(540, 680),
                StartPosition = FormStartPosition.CenterParent,
                FormBorderStyle = FormBorderStyle.Sizable,
                BackColor = Theme.Bg
            };

            var wb = new WebBrowser
            {
                Dock = DockStyle.Fill,
                ScriptErrorsSuppressed = true
            };

            var topBar = new Panel
            {
                Dock = DockStyle.Top,
                Height = 36,
                BackColor = Theme.Surface
            };

            var lblHelp = new Label
            {
                Text = "Faça login com sua conta. Assim que terminar e entrar no YouTube, a janela fechará automaticamente.",
                Dock = DockStyle.Fill,
                TextAlign = ContentAlignment.MiddleCenter,
                Font = new Font("Segoe UI", 8.2f),
                ForeColor = Theme.TextDim
            };
            topBar.Controls.Add(lblHelp);

            wb.Navigated += (s, e) =>
            {
                if (e.Url != null && e.Url.Host.IndexOf("youtube.com", StringComparison.OrdinalIgnoreCase) >= 0)
                {
                    int size = 8192;
                    StringBuilder sb = new StringBuilder(size);
                    if (!InternetGetCookieEx("https://www.youtube.com/", null, sb, ref size, INTERNET_COOKIE_HTTPONLY, IntPtr.Zero))
                    {
                        if (size > 0)
                        {
                            sb = new StringBuilder(size);
                            InternetGetCookieEx("https://www.youtube.com/", null, sb, ref size, INTERNET_COOKIE_HTTPONLY, IntPtr.Zero);
                        }
                    }

                    string cookieStr = sb.ToString();
                    if (!string.IsNullOrEmpty(cookieStr) && (cookieStr.Contains("SAPISID") || cookieStr.Contains("SSID") || cookieStr.Contains("LOGIN_INFO")))
                    {
                        try
                        {
                            var netscape = new StringBuilder();
                            netscape.AppendLine("# Netscape HTTP Cookie File");
                            netscape.AppendLine("# Gerado de forma autônoma pelo LiteTube");
                            string[] pairs = cookieStr.Split(new char[] { ';' }, StringSplitOptions.RemoveEmptyEntries);
                            long exp = 2147483647; // Year 2038
                            foreach (var p in pairs)
                            {
                                int eq = p.IndexOf('=');
                                if (eq > 0)
                                {
                                    string k = p.Substring(0, eq).Trim();
                                    string v = p.Substring(eq + 1).Trim();
                                    netscape.AppendFormat(".youtube.com\tTRUE\t/\tTRUE\t{0}\t{1}\t{2}\r\n", exp, k, v);
                                }
                            }

                            File.WriteAllText(_cookiesPath, netscape.ToString(), new UTF8Encoding(false));
                            SanitizeCookieFile(_cookiesPath);
                            _config.CookiesFilePath = _cookiesPath;
                            _config.BrowserForCookies = "none";
                            _config.Save();

                            UpdateStatusUI();
                            if (AuthChanged != null) AuthChanged();

                            loginForm.Close();
                            MessageBox.Show(this, "✅ Login no Google realizado com sucesso!\nO LiteTube agora é autônomo.", "Sucesso", MessageBoxButtons.OK, MessageBoxIcon.Information);
                        }
                        catch { }
                    }
                }
            };

            loginForm.Controls.Add(wb);
            loginForm.Controls.Add(topBar);

            wb.Navigate("https://accounts.google.com/ServiceLogin?service=youtube&passive=true&continue=https://www.youtube.com/");
            loginForm.ShowDialog(this);
        }

        private void ClearAuth()
        {
            try
            {
                if (File.Exists(_cookiesPath)) File.Delete(_cookiesPath);
                _config.CookiesFilePath = "";
                _config.BrowserForCookies = "none";
                _config.Save();

                UpdateStatusUI();
                if (AuthChanged != null) AuthChanged();
                MessageBox.Show(this, "Conta desconectada e cookies removidos.", "Sucesso", MessageBoxButtons.OK, MessageBoxIcon.Information);
            }
            catch (Exception ex)
            {
                MessageBox.Show(this, "Erro: " + ex.Message, "Erro", MessageBoxButtons.OK, MessageBoxIcon.Error);
            }
        }
    }
}
