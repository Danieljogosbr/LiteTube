#nullable enable
using System;
using System.Collections.Generic;
using System.IO;
using System.Runtime.InteropServices;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Markup;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;

namespace LiteTube
{
    /// <summary>
    /// Casca Principal 100% WPF do LiteTube (Zero WinForms / Zero GDI+).
    /// Orquestra o cabeçalho estilo YouTube, alternância YouTube Vídeo / YT Music, busca,
    /// conta Google, indicador de RAM real, links externos litetube:// e o protocolo --self-test.
    /// </summary>
    public sealed class WpfMainWindow : Window
    {
        [DllImport("user32.dll")]
        private static extern bool SetForegroundWindow(IntPtr hWnd);

        [DllImport("user32.dll")]
        private static extern bool ShowWindowAsync(IntPtr hWnd, int nCmdShow);

        [DllImport("dwmapi.dll")]
        private static extern int DwmSetWindowAttribute(IntPtr hwnd, int attr, ref int attrValue, int attrSize);

        private const int SW_RESTORE = 9;
        private const int DWMWA_USE_IMMERSIVE_DARK_MODE_BEFORE_20H1 = 19;
        private const int DWMWA_USE_IMMERSIVE_DARK_MODE = 20;
        private const int DWMWA_CAPTION_COLOR = 35;
        private const int DWMWA_TEXT_COLOR = 36;

        public static void ApplyDarkTitleBar(Window window)
        {
            if (window == null) return;
            try
            {
                IntPtr hwnd = new WindowInteropHelper(window).Handle;
                if (hwnd == IntPtr.Zero) return;

                int dark = 1;
                DwmSetWindowAttribute(hwnd, DWMWA_USE_IMMERSIVE_DARK_MODE, ref dark, sizeof(int));
                DwmSetWindowAttribute(hwnd, DWMWA_USE_IMMERSIVE_DARK_MODE_BEFORE_20H1, ref dark, sizeof(int));

                // COLORREF 0x00BBGGRR para #0F0F0F (fundo exato do LiteTube) e texto #F1F1F1
                int captionColor = 0x000F0F0F;
                int textColor = 0x00F1F1F1;
                DwmSetWindowAttribute(hwnd, DWMWA_CAPTION_COLOR, ref captionColor, sizeof(int));
                DwmSetWindowAttribute(hwnd, DWMWA_TEXT_COLOR, ref textColor, sizeof(int));
            }
            catch { }
        }

        private readonly AppConfig _config;
        private readonly YouTubeService _ytService;
        private readonly AudioEngine _musicAudio;
        private readonly bool _isSelfTest;
        private readonly string? _startupExternalUrl;

        private WpfVideoView _videoView = null!;
        private WpfMusicView _musicView = null!;
        private ExternalLinkServer? _externalServer;
        private DispatcherTimer _ramTimer = null!;

        // Header & status elements
        private RowDefinition _topBarRow = null!;
        private RowDefinition _statusBarRow = null!;
        private Grid _topHeaderBar = null!;
        private Grid _bottomStatusBar = null!;
        private Button _btnHamburger = null!;
        private StackPanel _brandClickArea = null!;
        private Button _btnEnvVideo = null!;
        private Button _btnEnvMusic = null!;
        private TextBlock _txtSearchPlaceholder = null!;
        private TextBox _txtSearchBox = null!;
        private Button _btnClearSearch = null!;
        private Button _btnSearchGo = null!;
        private Popup _searchHistoryPopup = null!;
        private StackPanel _searchHistoryItemsPanel = null!;
        private Button _btnRefresh = null!;
        private TextBlock _lblRamUsage = null!;
        private Button _btnGoogleAccount = null!;
        private System.Windows.Shapes.Ellipse _accountAvatarEllipse = null!;
        private TextBlock _txtGoogleAccountName = null!;
        private ContentControl _videoHost = null!;
        private ContentControl _musicHost = null!;
        private TextBlock _lblStatusText = null!;
        private TextBlock _lblGpuStatus = null!;

        private string _activeEnvironment = "video";
        private WindowState _prevWindowState = WindowState.Normal;
        private WindowStyle _prevWindowStyle = WindowStyle.SingleBorderWindow;
        private ResizeMode _prevResizeMode = ResizeMode.CanResize;
        private int _ramTrimCounter = 0;

        public WpfMainWindow(bool isSelfTest = false, string? startupExternalUrl = null)
        {
            _isSelfTest = isSelfTest;
            _startupExternalUrl = startupExternalUrl;

            _config = AppConfig.Load();
            _ytService = new YouTubeService();
            _musicAudio = new AudioEngine(_ytService, _config);

            Title = "LiteTube — YouTube & YT Music";
            Width = 1340;
            Height = 840;
            MinWidth = 940;
            MinHeight = 620;
            WindowStartupLocation = WindowStartupLocation.CenterScreen;
            Background = new SolidColorBrush(Color.FromRgb(15, 15, 15));

            TryLoadWindowIcon();
            LoadXaml();
            BuildEnvironments();
            WireEvents();

            SourceInitialized += (s, e) => ApplyDarkTitleBar(this);
            Loaded += OnWindowLoaded;
            Closing += OnWindowClosing;
            PreviewKeyDown += OnWindowPreviewKeyDown;

            _ramTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(2.5) };
            _ramTimer.Tick += OnRamTimerTick;
            _ramTimer.Start();
        }

        private void TryLoadWindowIcon()
        {
            try
            {
                string icoPath = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "app.ico");
                if (File.Exists(icoPath))
                {
                    Icon = BitmapFrame.Create(new Uri(icoPath, UriKind.Absolute));
                }
            }
            catch { }
        }

        private void LoadXaml()
        {
            string baseDir = AppDomain.CurrentDomain.BaseDirectory;
            string p1 = Path.Combine(baseDir, "src", "WpfMainWindow.xaml");
            string p2 = Path.Combine(baseDir, "WpfMainWindow.xaml");
            string? xamlPath = File.Exists(p1) ? p1 : (File.Exists(p2) ? p2 : null);

            FrameworkElement root;
            if (xamlPath != null)
            {
                using var fs = new FileStream(xamlPath, FileMode.Open, FileAccess.Read, FileShare.Read);
                root = (FrameworkElement)XamlReader.Load(fs);
            }
            else
            {
                root = new Grid { Background = new SolidColorBrush(Color.FromRgb(15, 15, 15)) };
            }

            Content = root;

            _topBarRow = (RowDefinition)root.FindName("TopBarRow");
            _statusBarRow = (RowDefinition)root.FindName("StatusBarRow");
            _topHeaderBar = (Grid)root.FindName("TopHeaderBar");
            _bottomStatusBar = (Grid)root.FindName("BottomStatusBar");
            _btnHamburger = (Button)root.FindName("BtnHamburger");
            _brandClickArea = (StackPanel)root.FindName("BrandClickArea");
            _btnEnvVideo = (Button)root.FindName("BtnEnvVideo");
            _btnEnvMusic = (Button)root.FindName("BtnEnvMusic");
            _txtSearchPlaceholder = (TextBlock)root.FindName("TxtSearchPlaceholder");
            _txtSearchBox = (TextBox)root.FindName("TxtSearchBox");
            _btnClearSearch = (Button)root.FindName("BtnClearSearch");
            _btnSearchGo = (Button)root.FindName("BtnSearchGo");
            _searchHistoryPopup = (Popup)root.FindName("SearchHistoryPopup");
            _searchHistoryItemsPanel = (StackPanel)root.FindName("SearchHistoryItemsPanel");
            _btnRefresh = (Button)root.FindName("BtnRefresh");
            _lblRamUsage = (TextBlock)root.FindName("LblRamUsage");
            _btnGoogleAccount = (Button)root.FindName("BtnGoogleAccount");
            _accountAvatarEllipse = (System.Windows.Shapes.Ellipse)root.FindName("AccountAvatarEllipse");
            _txtGoogleAccountName = (TextBlock)root.FindName("TxtGoogleAccountName");
            _videoHost = (ContentControl)root.FindName("VideoEnvironmentHost");
            _musicHost = (ContentControl)root.FindName("MusicEnvironmentHost");
            _lblStatusText = (TextBlock)root.FindName("LblStatusText");
            _lblGpuStatus = (TextBlock)root.FindName("LblGpuStatus");
        }

        private void BuildEnvironments()
        {
            _videoView = new WpfVideoView(_config, _ytService, _musicAudio);
            _videoHost.Content = _videoView;

            _musicView = new WpfMusicView(_config, _ytService, _musicAudio);
            _musicHost.Content = _musicView;

            _externalServer = new ExternalLinkServer(
                url => Dispatcher.BeginInvoke(new Action(() => PlayExternalUrl(url))),
                () => Dispatcher.BeginInvoke(new Action(RestoreAndFocusWindow)));
            _externalServer.Start();
        }

        private void WireEvents()
        {
            _btnHamburger.Click += (s, e) =>
            {
                if (_activeEnvironment == "video") _videoView.ToggleSidebar();
                else _musicView.ToggleSidebar();
            };

            _brandClickArea.MouseLeftButtonUp += async (s, e) =>
            {
                SwitchEnvironment("video");
                await _videoView.SwitchToNavSectionAsync("home");
            };

            _btnEnvVideo.Click += (s, e) => SwitchEnvironment("video");
            _btnEnvMusic.Click += (s, e) => SwitchEnvironment("music");

            _txtSearchBox.GotFocus += (s, e) => ShowSearchHistoryDropdown();
            _txtSearchBox.PreviewMouseLeftButtonDown += (s, e) =>
            {
                if (_txtSearchBox.IsFocused) ShowSearchHistoryDropdown();
            };
            _txtSearchBox.TextChanged += (s, e) =>
            {
                bool hasText = !string.IsNullOrEmpty(_txtSearchBox.Text);
                _txtSearchPlaceholder.Visibility = hasText ? Visibility.Collapsed : Visibility.Visible;
                _btnClearSearch.Visibility = hasText ? Visibility.Visible : Visibility.Collapsed;
                if (_txtSearchBox.IsFocused) ShowSearchHistoryDropdown();
            };
            _txtSearchBox.KeyDown += async (s, e) =>
            {
                if (e.Key == Key.Enter)
                {
                    e.Handled = true;
                    _searchHistoryPopup.IsOpen = false;
                    await ExecuteSearchAsync();
                }
                else if (e.Key == Key.Escape)
                {
                    _searchHistoryPopup.IsOpen = false;
                }
            };
            _btnClearSearch.Click += (s, e) =>
            {
                _txtSearchBox.Text = "";
                _txtSearchBox.Focus();
            };
            _btnSearchGo.Click += async (s, e) => await ExecuteSearchAsync();
            _btnRefresh.Click += async (s, e) =>
            {
                if (_activeEnvironment == "video") await _videoView.RefreshCurrentPageAsync();
                else await _musicView.RefreshCurrentAsync();
            };

            _btnGoogleAccount.Click += (s, e) => OpenGoogleAuthDialog();

            _videoView.StatusNotification += msg => _lblStatusText.Text = msg;
            _videoView.FullscreenChanged += OnVideoFullscreenChanged;
            _videoView.PlaylistImportedToLibrary += () => _musicView.RefreshLocalPlaylistsList();
            _videoView.AccountStatusMayHaveChanged += () => RefreshGoogleAccountButtonAsync(false);

            _musicView.StatusNotification += msg => _lblStatusText.Text = msg;
            _musicView.AccountStatusMayHaveChanged += () => RefreshGoogleAccountButtonAsync(false);
            _musicView.WatchClipRequested += track =>
            {
                SwitchEnvironment("video");
                _videoView.PlayVideo(track);
            };
            _musicView.ChannelClicked += item =>
            {
                SwitchEnvironment("video");
                _videoView.OpenChannelFromItem(item);
            };
        }

        private async void OnWindowLoaded(object sender, RoutedEventArgs e)
        {
            Logger.Info("MAIN", "WpfMainWindow inicializado com sucesso (100% WPF puro, zero WinForms).");
            Logger.Info("STARTUP", string.Format("WpfMainWindow pronto e visível em {0}ms (boot não-bloqueante).",
                Program.StartupStopwatch.ElapsedMilliseconds));

            UpdateRamLabel();
            RefreshGoogleAccountButtonAsync(false);

            // Detecção de GPU em background
            _ = Task.Run(() =>
            {
                var gpu = GpuDetector.PrimaryGpu;
                string gpuName = gpu != null && !string.IsNullOrWhiteSpace(gpu.Name) ? gpu.Name : "Direct3D 11";
                _ = Dispatcher.BeginInvoke(new Action(() =>
                {
                    _lblGpuStatus.Text = "Flyleaf D3D11 • " + gpuName;
                    Logger.Info("STARTUP", "🚀 LiteTube WPF puro pronto com aceleração FlyleafLib Direct3D 11 na GPU: " + gpuName);
                }));
            });

            if (_isSelfTest)
            {
                SwitchEnvironment("video", loadFeed: false);
                await RunSelfTestProtocolAsync();
                return;
            }

            if (!string.IsNullOrWhiteSpace(_startupExternalUrl))
            {
                SwitchEnvironment("video", loadFeed: false);
                PlayExternalUrl(_startupExternalUrl);
            }
            else
            {
                SwitchEnvironment("video", loadFeed: true);
            }
        }

        private void OnWindowClosing(object? sender, System.ComponentModel.CancelEventArgs e)
        {
            try { _ramTimer.Stop(); } catch { }
            try { _externalServer?.Stop(); } catch { }
            try { _videoView.Dispose(); } catch { }
            try { _musicView.Dispose(); } catch { }
            try { _musicAudio.Dispose(); } catch { }
        }

        private void OnWindowPreviewKeyDown(object sender, KeyEventArgs e)
        {
            if (e.Key == Key.K && (Keyboard.Modifiers & ModifierKeys.Control) != 0)
            {
                _txtSearchBox.Focus();
                _txtSearchBox.SelectAll();
                e.Handled = true;
                return;
            }

            if (Keyboard.FocusedElement is TextBoxBase)
                return;

            if (_activeEnvironment == "video" && (_videoView.IsWatchPageVisible || _videoView.IsFullscreen))
            {
                Key key = e.Key == Key.System ? e.SystemKey : e.Key;
                if (key == Key.Space || key == Key.K || key == Key.J || key == Key.L ||
                    key == Key.Left || key == Key.Right || key == Key.M || key == Key.F ||
                    key == Key.Escape || key == Key.Up || key == Key.Down)
                {
                    _videoView.HandlePlayerShortcutKey(key);
                    e.Handled = true;
                }
            }
        }

        private void SwitchEnvironment(string env, bool loadFeed = true)
        {
            _activeEnvironment = env == "music" ? "music" : "video";
            Logger.Info("ENV", "Alternando ambiente para: " + _activeEnvironment);

            bool isVideo = _activeEnvironment == "video";
            MemoryOptimizer.IsVideoModeActive = isVideo;

            _videoHost.Visibility = isVideo ? Visibility.Visible : Visibility.Collapsed;
            _musicHost.Visibility = isVideo ? Visibility.Collapsed : Visibility.Visible;

            StyleEnvButton(_btnEnvVideo, isVideo);
            StyleEnvButton(_btnEnvMusic, !isVideo);

            if (loadFeed)
            {
                if (isVideo) _ = _videoView.EnsureInitialFeedLoadedAsync();
                else _ = _musicView.EnsureInitialFeedLoadedAsync();
            }
        }

        private static void StyleEnvButton(Button btn, bool active)
        {
            btn.Background = active ? new SolidColorBrush(Color.FromRgb(62, 166, 255)) : Brushes.Transparent;
            btn.Foreground = active ? new SolidColorBrush(Color.FromRgb(15, 15, 15)) : new SolidColorBrush(Color.FromRgb(185, 185, 192));
        }

        private void RecordSearchHistory(string query)
        {
            if (string.IsNullOrWhiteSpace(query)) return;
            string clean = query.Trim();
            _config.SearchHistory.RemoveAll(x => string.Equals(x, clean, StringComparison.OrdinalIgnoreCase));
            _config.SearchHistory.Insert(0, clean);
            if (_config.SearchHistory.Count > 20)
                _config.SearchHistory.RemoveRange(20, _config.SearchHistory.Count - 20);
            _config.Save();
        }

        private void ShowSearchHistoryDropdown()
        {
            if (_searchHistoryPopup == null || _searchHistoryItemsPanel == null) return;
            if (_config.SearchHistory == null || _config.SearchHistory.Count == 0)
            {
                _searchHistoryPopup.IsOpen = false;
                return;
            }

            string filter = _txtSearchBox.Text?.Trim() ?? "";
            _searchHistoryItemsPanel.Children.Clear();

            int shown = 0;
            for (int i = 0; i < _config.SearchHistory.Count && shown < 8; i++)
            {
                string term = _config.SearchHistory[i];
                if (string.IsNullOrWhiteSpace(term)) continue;
                if (!string.IsNullOrEmpty(filter) && term.IndexOf(filter, StringComparison.OrdinalIgnoreCase) < 0)
                    continue;

                string captured = term;
                var row = new Grid { Margin = new Thickness(0, 1, 0, 1) };
                row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
                row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

                var btnTerm = new Button
                {
                    Content = "🕒   " + captured,
                    Background = Brushes.Transparent,
                    Foreground = new SolidColorBrush(Color.FromRgb(235, 235, 240)),
                    HorizontalContentAlignment = HorizontalAlignment.Left,
                    Padding = new Thickness(10, 7, 10, 7),
                    BorderThickness = new Thickness(0),
                    FontSize = 13,
                    Cursor = Cursors.Hand
                };
                btnTerm.Click += async (s, e) =>
                {
                    _txtSearchBox.Text = captured;
                    _searchHistoryPopup.IsOpen = false;
                    await ExecuteSearchAsync();
                };
                Grid.SetColumn(btnTerm, 0);

                var btnRemove = new Button
                {
                    Content = "✕",
                    Width = 28,
                    Background = Brushes.Transparent,
                    Foreground = new SolidColorBrush(Color.FromRgb(130, 130, 140)),
                    BorderThickness = new Thickness(0),
                    FontSize = 11,
                    Cursor = Cursors.Hand,
                    ToolTip = "Remover do histórico"
                };
                btnRemove.Click += (s, e) =>
                {
                    _config.SearchHistory.RemoveAll(x => string.Equals(x, captured, StringComparison.OrdinalIgnoreCase));
                    _config.Save();
                    ShowSearchHistoryDropdown();
                };
                Grid.SetColumn(btnRemove, 1);

                row.Children.Add(btnTerm);
                row.Children.Add(btnRemove);
                _searchHistoryItemsPanel.Children.Add(row);
                shown++;
            }

            _searchHistoryPopup.IsOpen = shown > 0;
        }

        private async Task ExecuteSearchAsync()
        {
            string q = _txtSearchBox.Text?.Trim() ?? "";
            if (string.IsNullOrEmpty(q)) return;
            _searchHistoryPopup.IsOpen = false;

            if (q.IndexOf("youtube.com/watch", StringComparison.OrdinalIgnoreCase) >= 0 ||
                q.IndexOf("youtu.be/", StringComparison.OrdinalIgnoreCase) >= 0)
            {
                SwitchEnvironment("video", loadFeed: false);
                _videoView.PlayVideoByUrl(q);
                return;
            }

            RecordSearchHistory(q);

            if (_activeEnvironment == "video")
                await _videoView.SearchAsync(q);
            else
                await _musicView.SearchAsync(q);
        }

        private void OpenGoogleAuthDialog()
        {
            var dlg = new WpfGoogleAuthDialog(_config, this);
            dlg.AuthChanged += () =>
            {
                RefreshGoogleAccountButtonAsync(true);
                _videoView.OnAuthChanged();
                _musicView.OnAuthChanged();
                _lblStatusText.Text = "Conta Google atualizada! Feeds e playlists sincronizados.";
            };
            dlg.ShowDialog();
        }

        private async void RefreshGoogleAccountButtonAsync(bool forceProfileFetch)
        {
            if (GoogleSessionService.Shared.IsSessionExpired)
            {
                _txtGoogleAccountName.Text = "Sessão Expirada";
                _txtGoogleAccountName.Foreground = new SolidColorBrush(Color.FromRgb(250, 204, 21));
                _accountAvatarEllipse.Fill = new SolidColorBrush(Color.FromRgb(180, 120, 20));
                return;
            }

            if (!GoogleSessionService.Shared.HasStoredSession(_config))
            {
                _txtGoogleAccountName.Text = "Conta Google";
                _txtGoogleAccountName.Foreground = new SolidColorBrush(Color.FromRgb(241, 241, 241));
                _accountAvatarEllipse.Fill = new SolidColorBrush(Color.FromRgb(46, 46, 52));
                return;
            }

            _txtGoogleAccountName.Text = "Conectado";
            _txtGoogleAccountName.Foreground = new SolidColorBrush(Color.FromRgb(0, 212, 130));

            if (_isSelfTest) return;

            var profile = await GoogleSessionService.Shared.GetAccountProfileAsync(_config, forceProfileFetch);
            if (IsLoaded)
            {
                if (profile.SessionExpired)
                {
                    _txtGoogleAccountName.Text = "Sessão Expirada";
                    _txtGoogleAccountName.Foreground = new SolidColorBrush(Color.FromRgb(250, 204, 21));
                }
                else if (profile.IsAuthenticated && !string.IsNullOrWhiteSpace(profile.DisplayName))
                {
                    string shortName = profile.DisplayName.Length > 18
                        ? profile.DisplayName.Substring(0, 17) + "…"
                        : profile.DisplayName;
                    _txtGoogleAccountName.Text = shortName;
                    _txtGoogleAccountName.Foreground = new SolidColorBrush(Color.FromRgb(241, 241, 241));

                    if (!string.IsNullOrWhiteSpace(profile.AvatarUrl))
                    {
                        try
                        {
                            var bmp = await AsyncImageLoader.Loaders.MemoryCachedWebImageLoader.LoadImageAsync(profile.AvatarUrl, 64);
                            if (bmp != null)
                            {
                                _accountAvatarEllipse.Fill = new ImageBrush(bmp) { Stretch = Stretch.UniformToFill };
                            }
                        }
                        catch { }
                    }
                }
            }
        }

        private void OnVideoFullscreenChanged(bool isFullscreen)
        {
            if (isFullscreen)
            {
                _prevWindowState = WindowState;
                _prevWindowStyle = WindowStyle;
                _prevResizeMode = ResizeMode;

                _topBarRow.Height = new GridLength(0);
                _statusBarRow.Height = new GridLength(0);
                _topHeaderBar.Visibility = Visibility.Collapsed;
                _bottomStatusBar.Visibility = Visibility.Collapsed;

                WindowStyle = WindowStyle.None;
                ResizeMode = ResizeMode.NoResize;
                WindowState = WindowState.Normal;
                WindowState = WindowState.Maximized;
            }
            else
            {
                _topBarRow.Height = new GridLength(56);
                _statusBarRow.Height = new GridLength(24);
                _topHeaderBar.Visibility = Visibility.Visible;
                _bottomStatusBar.Visibility = Visibility.Visible;

                WindowStyle = _prevWindowStyle;
                ResizeMode = _prevResizeMode;
                WindowState = _prevWindowState;
            }
        }

        public void PlayExternalUrl(string rawUrl)
        {
            if (string.IsNullOrWhiteSpace(rawUrl)) return;
            SwitchEnvironment("video", loadFeed: false);
            _videoView.PlayVideoByUrl(rawUrl);
        }

        public void RestoreAndFocusWindow()
        {
            try
            {
                if (!IsVisible) Show();
                if (WindowState == WindowState.Minimized) WindowState = WindowState.Normal;
                IntPtr hwnd = new WindowInteropHelper(this).Handle;
                if (hwnd != IntPtr.Zero)
                {
                    ShowWindowAsync(hwnd, SW_RESTORE);
                    SetForegroundWindow(hwnd);
                }
                Activate();
            }
            catch { }
        }

        private void OnRamTimerTick(object? sender, EventArgs e)
        {
            UpdateRamLabel();
            _ramTrimCounter++;
            if (_ramTrimCounter >= 24)
            {
                _ramTrimCounter = 0;
                MemoryOptimizer.TrimMemory();
            }
        }

        private void UpdateRamLabel()
        {
            double mb = MemoryOptimizer.GetCurrentRamUsageMB();
            _lblRamUsage.Text = string.Format("⚡ {0:0.0} MB", mb);
        }

        // =========================================================================
        // PROTOCOLO AUTOMATIZADO --self-test (100% WPF Puro)
        // =========================================================================

        private async Task RunSelfTestProtocolAsync()
        {
            try
            {
                Logger.Info("SELFTEST", "Iniciando validação automatizada (Arquitetura 100% WPF Puro)...");

                await Program.EngineReadyTask;
                await _videoView.PlaybackController.EnsurePlayerInitializedAsync();

                // 1. YouTube Music feeds + parser
                var exploreRes = await _ytService.FetchMusicExploreFeedAsync(_config);
                Logger.Info("SELFTEST", string.Format("Fluxo Explorar (FEmusic_explore) validado: {0} itens carregados.", exploreRes.Item1?.Count ?? 0));

                var homeRes = await _ytService.FetchPersonalizedMusicFeedAsync(_config);
                Logger.Info("SELFTEST", string.Format("Fluxo Recomendados (FEmusic_home) validado: {0} itens carregados.", homeRes.Item1?.Count ?? 0));

                string mockCarouselJson = "{\"musicCarouselShelfRenderer\":{\"header\":{\"musicCarouselShelfBasicHeaderRenderer\":{\"title\":{\"runs\":[{\"text\":\"Ouvir novamente\"}]}}},\"contents\":[{\"musicTwoRowItemRenderer\":{\"title\":{\"runs\":[{\"text\":\"Bohemian Rhapsody\"}]},\"subtitle\":{\"runs\":[{\"text\":\"Queen\"}]},\"navigationEndpoint\":{\"watchEndpoint\":{\"videoId\":\"fJ9rUzIMcZQ\"}}}}]}}";
                var parsedCarousel = _ytService.ParseMusicCarouselShelves(mockCarouselJson, new HashSet<string>(StringComparer.OrdinalIgnoreCase));
                Logger.Info("SELFTEST", string.Format("Parser musicCarouselShelfRenderer validado: {0} faixa(s) extraída(s).", parsedCarousel.Count));

                // 2. SponsorBlock
                var mockSegs = new List<SponsorSegment>
                {
                    new SponsorSegment { Category = "sponsor", ActionType = "skip", Start = 10, End = 42, UUID = "u1" },
                    new SponsorSegment { Category = "music_offtopic", ActionType = "skip", Start = 180, End = 210, UUID = "u2" }
                };
                bool skip1 = SponsorBlockService.CheckAndGetSkipTarget(15, mockSegs, out double to1);
                bool skip2 = SponsorBlockService.CheckAndGetSkipTarget(185, mockSegs, out double to2);
                Logger.Info("SELFTEST", string.Format("SponsorBlockService validado: SkipSponsor->{0:0.0}s ({1}) | SkipMusicOfftopic->{2:0.0}s ({3}).", to1, skip1, to2, skip2));

                // 3. FlyleafLib + VideoPlaybackController + Pure WPF
                bool flyleafLoaded = FlyleafLib.Engine.IsLoaded;
                bool demuxerTuned = _videoView.PlaybackController.HasLowLatencyDemuxerTuning;
                bool prefetchGate = ProgressiveVideoSession.SharedPrefetchSemaphore.CurrentCount == 1;
                bool overlayHidden = !_videoView.IsOverlayVisible;
                Logger.Info("SELFTEST", string.Format("Motor FlyleafLib WPF + VideoPlaybackController validados: EngineLoaded={0} | DemuxerTuned={1} | PrefetchGate={2} | OverlayHiddenOnCatalog={3}.",
                    flyleafLoaded, demuxerTuned, prefetchGate, overlayHidden));

                // 4. Independência de Volume Vídeo vs Música
                double prevVid = _config.VideoVolume;
                double prevMus = _config.MusicVolume;
                _videoView.PlaybackController.ChangeVolumeBy(0.05);
                bool independentVol = Math.Abs(_config.MusicVolume - prevMus) < 0.0001;
                _videoView.PlaybackController.SetVolume(prevVid);
                Logger.Info("SELFTEST", string.Format("Independência de Volume Vídeo/Música validada: Independente={0} | VideoVol={1:0.00} | MusicVol={2:0.00}.",
                    independentVol, _config.VideoVolume, _config.MusicVolume));

                // 5. Codec Cascade
                bool cascadeOk = ProgressiveVideoSession.DetectCodecFromUrls("https://g.com/vp?itag=398", "https://g.com/vp?itag=251") == "AV1/Opus (Direct3D 11)" &&
                                 ProgressiveVideoSession.DetectCodecFromUrls("https://g.com/vp?itag=136", "https://g.com/vp?itag=140") == "H.264/AAC (Direct3D 11)";
                Logger.Info("SELFTEST", "Prioridade de Codecs em Cascata validada: CascadeOk=" + cascadeOk);

                // 6. HttpPipeline + AsyncImageLoader + LogSanitizer + WPF Lists
                bool netOk = HttpPipeline.Client != null && HttpPipeline.Handler.MaxConnectionsPerServer == 16;
                bool loaderOk = AsyncImageLoader.ImageLoaderBase.DefaultLoader != null;
                bool musicListOk = _musicView.TrackList != null && _musicView.TrackList.IsVisualTreeReady;
                string redacted = Logger.SanitizeSensitiveData("Authorization: SAPISIDHASH 123_abc | Cookie: SAPISID=secret123; HSID=xyz");
                bool redactOk = !redacted.Contains("secret123") && !redacted.Contains("123_abc");
                Logger.Info("SELFTEST", string.Format("Pipeline HTTP (16 conexões + backoff), AsyncImageLoader, LogSanitizer e WpfMediaListControl validados: NetOk={0} | LoaderOk={1} | MusicListOk={2} | LogRedactionOk={3}.",
                    netOk, loaderOk, musicListOk, redactOk));

                // 7. DPAPI Vault + ExternalLinkServer IPC
                string enc = CookieVault.ProtectText("test_cookie_payload");
                bool vaultOk = CookieVault.UnprotectText(enc) == "test_cookie_payload";
                string pingResp = ExternalLinkServer.SendCommand("PING", 1500);
                string playResp = ExternalLinkServer.SendCommand("PLAY|litetube://play?url=https%3A%2F%2Fwww.youtube.com%2Fwatch%3Fv%3DmockId12345", 1500);
                Logger.Info("SELFTEST", string.Format("Segurança & IPC validados: VaultOk={0} | PingOk={1} | PlayOk={2}.",
                    vaultOk, pingResp == "PONG", playResp.IndexOf("OK", StringComparison.OrdinalIgnoreCase) >= 0));

                // 8. Etapa 2 + Etapa 3 (GoogleSessionService + YouTubeAccountFeedService + Pure WPF RAM)
                string? sapisid = GoogleSessionService.BuildSapisidHashHeader("SAPISID=mock_val_123", "https://www.youtube.com");
                bool sapisidOk = !string.IsNullOrEmpty(sapisid) && sapisid.StartsWith("SAPISIDHASH ", StringComparison.Ordinal);

                var parsedPls = YouTubeAccountFeedService.ParsePlaylistsJson(
                    "{\"items\":[{\"playlistRenderer\":{\"playlistId\":\"WL\",\"title\":{\"simpleText\":\"Assistir mais tarde\"}}},{\"playlistRenderer\":{\"playlistId\":\"LL\",\"title\":{\"simpleText\":\"Liked videos\"}}},{\"lockupViewModel\":{\"contentType\":\"LOCKUP_CONTENT_TYPE_PLAYLIST\",\"contentId\":\"PL1234567890ABCDEF\",\"metadata\":{\"lockupMetadataViewModel\":{\"title\":{\"content\":\"Minha Playlist Real\"}}}}}]}",
                    true);
                bool playlistsOk = parsedPls.Items.Count == 1 && parsedPls.Items[0].PlaylistId == "PL1234567890ABCDEF";

                var parsedMusicPls = YouTubeAccountFeedService.ParseMusicPlaylistsJson(
                    "{\"items\":[" +
                    "{\"musicTwoRowItemRenderer\":{\"title\":{\"runs\":[{\"text\":\"Músicas marcadas com Gostei\"}]},\"navigationEndpoint\":{\"browseEndpoint\":{\"browseId\":\"VLLM\"}}}}," +
                    "{\"musicTwoRowItemRenderer\":{\"title\":{\"runs\":[{\"text\":\"Episódios para mais tarde\"}]},\"navigationEndpoint\":{\"browseEndpoint\":{\"browseId\":\"VLSE\"}}}}," +
                    "{\"musicTwoRowItemRenderer\":{\"title\":{\"runs\":[{\"text\":\"Rock Favoritas\"}]},\"subtitle\":{\"runs\":[{\"text\":\"Playlist • 42 músicas\"}]},\"navigationEndpoint\":{\"browseEndpoint\":{\"browseId\":\"VLPLMusicReal123456\"}}}}" +
                    "]}", true);
                bool musicPlsOk = parsedMusicPls.Items.Count == 1 && parsedMusicPls.Items[0].PlaylistId == "PLMusicReal123456";

                var parsedMusicTracks = YouTubeAccountFeedService.ParseMusicPlaylistItemsJson(
                    "{\"contents\":{\"singleColumnBrowseResultsRenderer\":{\"tabs\":[{\"tabRenderer\":{\"content\":{\"sectionListRenderer\":{\"contents\":[" +
                    "{\"musicPlaylistShelfRenderer\":{\"contents\":[{\"musicResponsiveListItemRenderer\":{\"playlistItemData\":{\"videoId\":\"fJ9rUzIMcZQ\"},\"flexColumns\":[{\"musicResponsiveListItemFlexColumnRenderer\":{\"text\":{\"runs\":[{\"text\":\"Bohemian Rhapsody\"}]}}},{\"musicResponsiveListItemFlexColumnRenderer\":{\"text\":{\"runs\":[{\"text\":\"Queen\"}]}}}],\"fixedColumns\":[{\"musicResponsiveListItemFixedColumnRenderer\":{\"text\":{\"runs\":[{\"text\":\"5:55\"}]}}}]}}]}}," +
                    "{\"musicShelfRenderer\":{\"title\":{\"runs\":[{\"text\":\"Sugestões\"}]},\"contents\":[{\"musicResponsiveListItemRenderer\":{\"playlistItemData\":{\"videoId\":\"dQw4w9WgXcQ\"},\"flexColumns\":[{\"musicResponsiveListItemFlexColumnRenderer\":{\"text\":{\"runs\":[{\"text\":\"Recomendada Fora da Playlist\"}]}}}]}}]}}" +
                    "],\"continuations\":[{\"nextContinuationData\":{\"continuation\":\"OUTER_SUGGESTIONS_TOKEN\"}}]}}}}]}}}",
                    true);
                bool musicTracksOk = parsedMusicTracks.Items.Count == 1 &&
                                     parsedMusicTracks.Items[0].Id == "fJ9rUzIMcZQ" &&
                                     parsedMusicTracks.Items[0].Duration == "5:55" &&
                                     string.IsNullOrEmpty(parsedMusicTracks.ContinuationToken);

                double ramMb = MemoryOptimizer.GetCurrentRamUsageMB();
                Logger.Info("SELFTEST", string.Format("Etapa 4 (WPF Puro + YT Music Playlists) validada: SapisidOk={0} | VideoPlaylistsOnlyOk={1} | MusicPlaylistsOnlyOk={2} | MusicTracksOk={3} | RamSnapshot={4:0.0}MB.",
                    sapisidOk, playlistsOk, musicPlsOk, musicTracksOk, ramMb));

                Logger.Info("SELFTEST", "Validação automatizada concluída com 0 exceções.");
            }
            catch (Exception ex)
            {
                Logger.Error("SELFTEST", "Falha durante o protocolo de auto-teste WPF", ex);
            }
            finally
            {
                Close();
            }
        }
    }
}
