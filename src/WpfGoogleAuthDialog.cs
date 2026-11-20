#nullable enable
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Text;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Markup;
using System.Windows.Media;
using Microsoft.Web.WebView2.Core;
using Microsoft.Web.WebView2.Wpf;
using Microsoft.Win32;

namespace LiteTube
{
    /// <summary>
    /// Camada 4 (UI) — Janela WPF pura para autenticação segura da Conta Google (WebView2 WPF + DPAPI).
    /// </summary>
    public sealed class WpfGoogleAuthDialog : Window
    {
        private readonly AppConfig _config;
        private readonly string _cookiesPath;

        private TextBlock _lblStatusTitle = null!;
        private TextBlock _lblStatusDetail = null!;
        private ProgressBar _progressBar = null!;
        private Button _btnWebLogin = null!;
        private Button _btnExtractFirefox = null!;
        private Button _btnExtractChrome = null!;
        private Button _btnExtractEdge = null!;
        private Button _btnExtractBrave = null!;
        private Button _btnPasteClipboard = null!;
        private Button _btnImportCookies = null!;
        private Button _btnClearAuth = null!;
        private Button _btnClose = null!;

        public event Action? AuthChanged;

        public WpfGoogleAuthDialog(AppConfig config, Window? owner = null)
        {
            _config = config;
            _cookiesPath = CookieVault.GetVaultFilePath();

            string? dataDir = Path.GetDirectoryName(_cookiesPath);
            if (!string.IsNullOrEmpty(dataDir) && !Directory.Exists(dataDir))
                Directory.CreateDirectory(dataDir);

            _config.CookiesFilePath = CookieVault.MigrateLegacyFileIfPresent(_config.CookiesFilePath);

            Title = "LiteTube — Conta Google";
            Width = 520;
            Height = 470;
            MinWidth = 480;
            MinHeight = 430;
            ResizeMode = ResizeMode.NoResize;
            WindowStartupLocation = owner != null ? WindowStartupLocation.CenterOwner : WindowStartupLocation.CenterScreen;
            Background = new SolidColorBrush(Color.FromRgb(15, 15, 15));
            if (owner != null) Owner = owner;
            SourceInitialized += (s, e) => WpfMainWindow.ApplyDarkTitleBar(this);

            LoadXaml();
            WireEvents();
            RefreshStatusAsync();
        }

        private void LoadXaml()
        {
            string baseDir = AppDomain.CurrentDomain.BaseDirectory;
            string p1 = Path.Combine(baseDir, "src", "WpfGoogleAuthDialog.xaml");
            string p2 = Path.Combine(baseDir, "WpfGoogleAuthDialog.xaml");
            string? xamlPath = File.Exists(p1) ? p1 : (File.Exists(p2) ? p2 : null);

            FrameworkElement root;
            if (xamlPath != null)
            {
                using var fs = new FileStream(xamlPath, FileMode.Open, FileAccess.Read, FileShare.Read);
                root = (FrameworkElement)XamlReader.Load(fs);
            }
            else
            {
                root = new Grid();
            }

            Content = root;

            _lblStatusTitle = (TextBlock)root.FindName("LblStatusTitle");
            _lblStatusDetail = (TextBlock)root.FindName("LblStatusDetail");
            _progressBar = (ProgressBar)root.FindName("ProgressBar");
            _btnWebLogin = (Button)root.FindName("BtnWebLogin");
            _btnExtractFirefox = (Button)root.FindName("BtnExtractFirefox");
            _btnExtractChrome = (Button)root.FindName("BtnExtractChrome");
            _btnExtractEdge = (Button)root.FindName("BtnExtractEdge");
            _btnExtractBrave = (Button)root.FindName("BtnExtractBrave");
            _btnPasteClipboard = (Button)root.FindName("BtnPasteClipboard");
            _btnImportCookies = (Button)root.FindName("BtnImportCookies");
            _btnClearAuth = (Button)root.FindName("BtnClearAuth");
            _btnClose = (Button)root.FindName("BtnClose");
        }

        private void WireEvents()
        {
            _btnWebLogin.Click += (s, e) => OpenWebView2Login();
            _btnExtractFirefox.Click += async (s, e) => await ExtractFromBrowserAsync("firefox");
            _btnExtractChrome.Click += async (s, e) => await ExtractFromBrowserAsync("chrome");
            _btnExtractEdge.Click += async (s, e) => await ExtractFromBrowserAsync("edge");
            _btnExtractBrave.Click += async (s, e) => await ExtractFromBrowserAsync("brave");
            _btnPasteClipboard.Click += (s, e) => ImportFromClipboard();
            _btnImportCookies.Click += (s, e) => ImportFromFile();
            _btnClearAuth.Click += (s, e) => ClearAuth();
            _btnClose.Click += (s, e) => Close();
        }

        private async void RefreshStatusAsync()
        {
            bool hasVault = CookieVault.HasStoredCookies(_config.CookiesFilePath);
            if (!hasVault && (string.IsNullOrEmpty(_config.BrowserForCookies) || _config.BrowserForCookies == "none"))
            {
                _lblStatusTitle.Text = "○ Nenhuma conta conectada";
                _lblStatusTitle.Foreground = new SolidColorBrush(Color.FromRgb(170, 170, 170));
                _lblStatusDetail.Text = "Faça login para carregar seu Início, Inscrições, Histórico e Playlists do YouTube.";
                return;
            }

            if (GoogleSessionService.Shared.IsSessionExpired)
            {
                _lblStatusTitle.Text = "⚠️ Sessão expirada — reconecte sua conta";
                _lblStatusTitle.Foreground = new SolidColorBrush(Color.FromRgb(250, 204, 21));
                _lblStatusDetail.Text = "O YouTube pediu nova autenticação. Use o Login Integrado abaixo para renovar.";
                return;
            }

            _lblStatusTitle.Text = "● Conta Google Conectada (Cofre DPAPI)";
            _lblStatusTitle.Foreground = new SolidColorBrush(Color.FromRgb(0, 212, 130));
            _lblStatusDetail.Text = "Sessão protegida com criptografia do Windows. Verificando perfil...";

            var profile = await GoogleSessionService.Shared.GetAccountProfileAsync(_config, false);
            if (IsLoaded)
            {
                if (profile.IsAuthenticated && !string.IsNullOrWhiteSpace(profile.DisplayName))
                {
                    string handle = !string.IsNullOrWhiteSpace(profile.Handle) ? (" (" + profile.Handle + ")") : "";
                    _lblStatusTitle.Text = "● Conectado como " + profile.DisplayName + handle;
                    _lblStatusDetail.Text = "Cofre DPAPI ativo • Início, Inscrições, Histórico e Playlists sincronizados.";
                }
                else if (profile.SessionExpired)
                {
                    _lblStatusTitle.Text = "⚠️ Sessão expirada — reconecte sua conta";
                    _lblStatusTitle.Foreground = new SolidColorBrush(Color.FromRgb(250, 204, 21));
                    _lblStatusDetail.Text = "Os cookies salvos expiraram no Google. Faça login novamente.";
                }
                else
                {
                    _lblStatusDetail.Text = "Cofre DPAPI pronto e ativo.";
                }
            }
        }

        private void SetBusy(bool busy, string? detail = null)
        {
            _btnWebLogin.IsEnabled = !busy;
            _btnExtractFirefox.IsEnabled = !busy;
            _btnExtractChrome.IsEnabled = !busy;
            _btnExtractEdge.IsEnabled = !busy;
            _btnExtractBrave.IsEnabled = !busy;
            _btnPasteClipboard.IsEnabled = !busy;
            _btnImportCookies.IsEnabled = !busy;
            _btnClearAuth.IsEnabled = !busy;
            _progressBar.Visibility = busy ? Visibility.Visible : Visibility.Collapsed;
            if (!string.IsNullOrEmpty(detail))
                _lblStatusDetail.Text = detail;
        }

        private void SaveAndActivateCookies(string rawNetscapeText)
        {
            string clean = CookieVault.SanitizeNetscapeContent(rawNetscapeText);
            CookieVault.SaveProtectedCookies(_cookiesPath, clean);
            _config.CookiesFilePath = _cookiesPath;
            _config.BrowserForCookies = "none";
            _config.Save();

            GoogleSessionService.Shared.InvalidateCache();
            YouTubeAccountFeedService.Shared.InvalidateCache();
            RefreshStatusAsync();
            AuthChanged?.Invoke();
        }

        private void OpenWebView2Login()
        {
            string tempProfileDir = Path.Combine(Path.GetTempPath(), "LiteTube_WV2_" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(tempProfileDir);

            var webWin = new Window
            {
                Title = "Login Google / YouTube — LiteTube",
                Width = 980,
                Height = 720,
                WindowStartupLocation = WindowStartupLocation.CenterOwner,
                Owner = this,
                Background = new SolidColorBrush(Color.FromRgb(15, 15, 15))
            };
            webWin.SourceInitialized += (s, e) => WpfMainWindow.ApplyDarkTitleBar(webWin);

            var rootGrid = new Grid();
            rootGrid.RowDefinitions.Add(new RowDefinition { Height = new GridLength(52) });
            rootGrid.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });

            var topBar = new Border
            {
                Background = new SolidColorBrush(Color.FromRgb(24, 24, 27)),
                Padding = new Thickness(16, 8, 16, 8)
            };
            var topDock = new DockPanel();
            var btnFinish = new Button
            {
                Content = "✓  Concluir e Salvar Sessão",
                Background = new SolidColorBrush(Color.FromRgb(62, 166, 255)),
                Foreground = new SolidColorBrush(Color.FromRgb(15, 15, 15)),
                FontWeight = FontWeights.Bold,
                Padding = new Thickness(16, 6, 16, 6),
                BorderThickness = new Thickness(0),
                IsEnabled = false
            };
            DockPanel.SetDock(btnFinish, Dock.Right);
            var lblHint = new TextBlock
            {
                Text = "Inicializando Microsoft Edge WebView2 isolado...",
                Foreground = new SolidColorBrush(Color.FromRgb(241, 241, 241)),
                VerticalAlignment = VerticalAlignment.Center,
                FontSize = 13
            };
            topDock.Children.Add(btnFinish);
            topDock.Children.Add(lblHint);
            topBar.Child = topDock;
            Grid.SetRow(topBar, 0);

            var webView = new WebView2();
            Grid.SetRow(webView, 1);

            rootGrid.Children.Add(topBar);
            rootGrid.Children.Add(webView);
            webWin.Content = rootGrid;

            bool savedOk = false;

            webWin.Loaded += async (s, e) =>
            {
                try
                {
                    var env = await CoreWebView2Environment.CreateAsync(userDataFolder: tempProfileDir);
                    await webView.EnsureCoreWebView2Async(env);
                    if (!webWin.IsLoaded || webView.CoreWebView2 == null) return;

                    lblHint.Text = "Faça login na sua conta Google/YouTube e depois clique em 'Concluir e Salvar Sessão'.";
                    btnFinish.IsEnabled = true;

                    webView.CoreWebView2.Navigate(
                        "https://accounts.google.com/ServiceLogin?service=youtube&continue=https%3A%2F%2Fwww.youtube.com%2Fsignin%3Faction_handle_signin%3Dtrue");
                }
                catch (Exception ex)
                {
                    Logger.Error("AUTH", "Falha ao inicializar WebView2 WPF: " + ex.Message, ex);
                    MessageBox.Show(webWin, "Não foi possível abrir o WebView2:\n" + ex.Message, "WebView2 Indisponível", MessageBoxButton.OK, MessageBoxImage.Warning);
                    webWin.Close();
                }
            };

            btnFinish.Click += async (s, e) =>
            {
                if (webView.CoreWebView2 == null) return;
                btnFinish.IsEnabled = false;
                lblHint.Text = "Salvando cookies com criptografia Windows DPAPI...";

                try
                {
                    var dedup = new Dictionary<string, CoreWebView2Cookie>(StringComparer.OrdinalIgnoreCase);
                    string[] urls =
                    {
                        "https://www.youtube.com/",
                        "https://music.youtube.com/",
                        "https://accounts.google.com/",
                        "https://www.google.com/"
                    };

                    foreach (string u in urls)
                    {
                        var list = await webView.CoreWebView2.CookieManager.GetCookiesAsync(u);
                        if (list == null) continue;
                        foreach (var c in list)
                        {
                            if (c == null || string.IsNullOrWhiteSpace(c.Name)) continue;
                            string dom = string.IsNullOrWhiteSpace(c.Domain) ? ".youtube.com" : c.Domain;
                            string path = string.IsNullOrWhiteSpace(c.Path) ? "/" : c.Path;
                            dedup[$"{dom}|{path}|{c.Name}"] = c;
                        }
                    }

                    if (dedup.Count == 0)
                    {
                        MessageBox.Show(webWin, "Nenhum cookie do YouTube foi capturado ainda. Conclua o login primeiro.", "Aviso", MessageBoxButton.OK, MessageBoxImage.Warning);
                        btnFinish.IsEnabled = true;
                        lblHint.Text = "Faça login na sua conta Google/YouTube e depois clique em 'Concluir e Salvar Sessão'.";
                        return;
                    }

                    var sb = new StringBuilder();
                    sb.AppendLine("# Netscape HTTP Cookie File");
                    long fallbackExp = DateTimeOffset.UtcNow.AddYears(1).ToUnixTimeSeconds();

                    foreach (var c in dedup.Values)
                    {
                        string domain = string.IsNullOrWhiteSpace(c.Domain) ? ".youtube.com" : c.Domain.Trim();
                        string includeSub = domain.StartsWith(".") ? "TRUE" : "FALSE";
                        string path = string.IsNullOrWhiteSpace(c.Path) ? "/" : c.Path;
                        string secure = c.IsSecure ? "TRUE" : "FALSE";
                        long exp = (!c.IsSession && c.Expires > DateTime.UtcNow)
                            ? new DateTimeOffset(c.Expires.ToUniversalTime()).ToUnixTimeSeconds()
                            : fallbackExp;

                        sb.AppendLine($"{domain}\t{includeSub}\t{path}\t{secure}\t{exp}\t{c.Name}\t{c.Value}");
                    }

                    SaveAndActivateCookies(sb.ToString());
                    savedOk = true;
                    Logger.Info("AUTH", $"Sessão WebView2 WPF salva no cofre DPAPI ({dedup.Count} cookies).");
                    webWin.Close();
                }
                catch (Exception ex)
                {
                    Logger.Error("AUTH", "Erro ao capturar cookies do WebView2 WPF: " + ex.Message, ex);
                    MessageBox.Show(webWin, "Erro ao capturar cookies: " + ex.Message, "Erro", MessageBoxButton.OK, MessageBoxImage.Error);
                    btnFinish.IsEnabled = true;
                }
            };

            webWin.Closed += (s, e) =>
            {
                try { webView.Dispose(); } catch { }
                _ = Task.Run(async () =>
                {
                    for (int i = 0; i < 6; i++)
                    {
                        try
                        {
                            if (!Directory.Exists(tempProfileDir)) break;
                            Directory.Delete(tempProfileDir, true);
                            break;
                        }
                        catch
                        {
                            await Task.Delay(350).ConfigureAwait(false);
                        }
                    }
                });
            };

            webWin.ShowDialog();
            if (savedOk)
            {
                MessageBox.Show(this, "Conta Google conectada com criptografia Windows DPAPI!", "Conectado", MessageBoxButton.OK, MessageBoxImage.Information);
            }
        }

        private async Task ExtractFromBrowserAsync(string browser)
        {
            string ytDlp = YtDlpResolver.GetYtDlpBinaryPath();
            if (!File.Exists(ytDlp))
            {
                MessageBox.Show(this, "bin\\yt-dlp.exe não encontrado.", "Aviso", MessageBoxButton.OK, MessageBoxImage.Warning);
                return;
            }

            SetBusy(true, $"Extraindo sessão do {browser.ToUpper()} para o cofre DPAPI...");
            string tempOut = Path.Combine(Path.GetTempPath(), "litetube_ck_" + Guid.NewGuid().ToString("N") + ".txt");

            try
            {
                string errOut = await Task.Run(() =>
                {
                    var psi = new ProcessStartInfo
                    {
                        FileName = ytDlp,
                        Arguments = $"--no-config --cookies-from-browser {browser} --cookies \"{tempOut}\" --skip-download --no-warnings \"https://www.youtube.com/\"",
                        UseShellExecute = false,
                        CreateNoWindow = true,
                        RedirectStandardError = true,
                        RedirectStandardOutput = true
                    };
                    using var p = Process.Start(psi);
                    if (p == null) return "Falha ao iniciar yt-dlp.";
                    string stderr = p.StandardError.ReadToEnd();
                    p.WaitForExit(20000);
                    return stderr;
                });

                if (File.Exists(tempOut) && new FileInfo(tempOut).Length > 100)
                {
                    string raw = File.ReadAllText(tempOut, Encoding.UTF8);
                    SaveAndActivateCookies(raw);
                    MessageBox.Show(this, $"Sessão do {browser.ToUpper()} salva no cofre DPAPI!", "Conectado", MessageBoxButton.OK, MessageBoxImage.Information);
                }
                else
                {
                    string hint = errOut.IndexOf("Could not copy", StringComparison.OrdinalIgnoreCase) >= 0
                        ? $"Feche o {browser.ToUpper()} por 3 segundos e tente novamente, ou use o Login Integrado (WebView2)."
                        : "Não foi possível extrair os cookies desse navegador. Use o Login Integrado (WebView2).";
                    MessageBox.Show(this, hint, "Extração de Cookies", MessageBoxButton.OK, MessageBoxImage.Warning);
                }
            }
            catch (Exception ex)
            {
                MessageBox.Show(this, "Erro ao extrair cookies: " + ex.Message, "Erro", MessageBoxButton.OK, MessageBoxImage.Error);
            }
            finally
            {
                try { if (File.Exists(tempOut)) File.Delete(tempOut); } catch { }
                SetBusy(false);
            }
        }

        private void ImportFromClipboard()
        {
            try
            {
                string text = Clipboard.GetText();
                if (!CookieVault.LooksLikeNetscapeCookies(text))
                {
                    MessageBox.Show(this, "O conteúdo da área de transferência não parece um arquivo Netscape cookies.txt válido.", "Aviso", MessageBoxButton.OK, MessageBoxImage.Warning);
                    return;
                }
                SaveAndActivateCookies(text);
                MessageBox.Show(this, "Cookies colados e protegidos com Windows DPAPI!", "Conectado", MessageBoxButton.OK, MessageBoxImage.Information);
            }
            catch (Exception ex)
            {
                MessageBox.Show(this, "Erro ao ler área de transferência: " + ex.Message, "Erro", MessageBoxButton.OK, MessageBoxImage.Error);
            }
        }

        private void ImportFromFile()
        {
            var ofd = new OpenFileDialog
            {
                Title = "Selecionar arquivo cookies.txt (formato Netscape)",
                Filter = "Arquivos de Cookies (*.txt)|*.txt|Todos os arquivos (*.*)|*.*"
            };
            if (ofd.ShowDialog(this) != true) return;

            try
            {
                string raw = File.ReadAllText(ofd.FileName, Encoding.UTF8);
                if (!CookieVault.LooksLikeNetscapeCookies(raw))
                {
                    MessageBox.Show(this, "O arquivo selecionado não está no formato Netscape cookies.txt.", "Aviso", MessageBoxButton.OK, MessageBoxImage.Warning);
                    return;
                }
                SaveAndActivateCookies(raw);
                MessageBox.Show(this, "Arquivo importado e salvo com criptografia Windows DPAPI!", "Conectado", MessageBoxButton.OK, MessageBoxImage.Information);
            }
            catch (Exception ex)
            {
                MessageBox.Show(this, "Erro ao importar arquivo: " + ex.Message, "Erro", MessageBoxButton.OK, MessageBoxImage.Error);
            }
        }

        private void ClearAuth()
        {
            _config.CookiesFilePath = "";
            _config.BrowserForCookies = "none";
            _config.Save();

            try { if (File.Exists(_cookiesPath)) File.Delete(_cookiesPath); } catch { }
            try
            {
                string legacy = CookieVault.GetLegacyPlaintextFilePath();
                if (File.Exists(legacy)) File.Delete(legacy);
            }
            catch { }

            GoogleSessionService.Shared.InvalidateCache();
            YouTubeAccountFeedService.Shared.InvalidateCache();
            RefreshStatusAsync();
            AuthChanged?.Invoke();
            MessageBox.Show(this, "Conta desconectada e cofre de cookies removido.", "Desconectado", MessageBoxButton.OK, MessageBoxImage.Information);
        }
    }
}
