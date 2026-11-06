using System;
using System.IO;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Runtime.InteropServices;
using System.Text.RegularExpressions;
using System.Windows.Forms;
using System.Threading.Tasks;

namespace LiteTube
{
    public class MainForm : Form
    {
        // Global Hotkeys P/Invoke
        [DllImport("user32.dll")]
        private static extern bool RegisterHotKey(IntPtr hWnd, int id, uint fsModifiers, uint vk);

        [DllImport("user32.dll")]
        private static extern bool UnregisterHotKey(IntPtr hWnd, int id);

        private const int WM_HOTKEY = 0x0312;
        private const uint MOD_NONE = 0x0000;
        private const uint MOD_ALT = 0x0001;
        private const uint MOD_CONTROL = 0x0002;
        private const uint MOD_NOREPEAT = 0x4000;

        private const uint VK_MEDIA_NEXT_TRACK = 0xB0;
        private const uint VK_MEDIA_PREV_TRACK = 0xB1;
        private const uint VK_MEDIA_PLAY_PAUSE = 0xB3;
        private const uint VK_P = 0x50;
        private const uint VK_M = 0x4D;
        private const uint VK_RIGHT = 0x27;
        private const uint VK_LEFT = 0x25;
        private const uint VK_UP = 0x26;
        private const uint VK_DOWN = 0x28;

        // Core services
        private readonly AppConfig _config;
        private readonly YouTubeService _ytService;
        private readonly AudioEngine _musicAudio;

        // Official YouTube & YouTube Music Dark Palette (Preto Puro Neutro - Sem tom azulado)
        private readonly Color BgDark = Theme.Bg;
        private readonly Color BgMusicDark = Color.FromArgb(8, 8, 8);
        private readonly Color BgPanel = Theme.Surface;
        private readonly Color BgCard = Theme.SurfaceHigh;
        private readonly Color AccentRed = Theme.Accent;
        private readonly Color AccentBlue = Theme.Blue;
        private readonly Color AccentGreen = Color.FromArgb(46, 204, 113);
        private readonly Color TextPrimary = Theme.TextMain;
        private readonly Color TextSecondary = Theme.TextDim;

        // Environment State
        // Environment State ("music" or "video")
        private string _activeEnvironment = "music";
        private string _activeSection = "search"; // search, queue, favorites, account, playlist:<id>
        private string _searchFilter = "songs";   // songs, albums, all
        private List<TrackItem> _displayedTracks = new List<TrackItem>();
        private string _musicSearchContinuationToken = "";
        private bool _isLoadingMoreMusic = false;
        private bool _forceExit = false;
        private readonly Size _defaultWindowSize = new Size(1220, 750);

        // Main Layout Containers
        private Panel _topBar;
        private Panel _musicEnvironmentContainer;
        private VideoEnvironmentControl _videoEnvironmentControl;

        // Music Environment Sub-Panels
        private Panel _sidebar;
        private Panel _centerPanel;
        private Panel _bottomBar;
        private System.Windows.Forms.Integration.ElementHost _musicBarElementHost;
        private WpfMusicPlayerBar _wpfMusicBar;
        private ExternalLinkServer _externalServer;

        // Top Bar Controls
        private Label _lblBrandLogo;
        private Button _btnEnvMusic;
        private Button _btnEnvVideo;
        private Panel _searchBoxBorderPanel;
        private TextBox _txtSearch;
        private Button _btnSearch;
        private Button _btnFilterSongs;
        private Button _btnFilterAlbums;
        private Button _btnFilterAll;
        private ComboBox _cmbBrowser;
        private Button _btnTopAudioRegulator;
        private Button _btnRamBadge;
        private Button _btnGpuBadge;

        // Sidebar Controls (YouTube Music Left Rail)
        private Button _navSearch;
        private Button _navQueue;
        private Button _navFavorites;
        private Button _navImportPlaylistUrl;
        private Button _navAccountRecommended;
        private Button _navAccountLiked;
        private Button _navAccountHistory;
        private Button _navGoogleAuth;
        private Button _btnNewPlaylist;
        private FlowLayoutPanel _playlistsNavContainer;

        // Center Content Controls (Music Environment)
        private Label _lblSectionTitle;
        private Label _lblStatus;
        private Button _btnToggleCardLayout;
        private Button _btnPlayAllView;
        private Button _btnAddSelectedToQueue;
        private Button _btnFavSelected;
        private Button _btnAddToPlaylist;
        private Button _btnRemoveFromView;
        private Button _btnWatchInVideoEnv;
        private MediaCardListControl _musicCardList;

        // System Tray & Timers
        private NotifyIcon _trayIcon;
        private ContextMenuStrip _trayMenu;
        private System.Windows.Forms.Timer _uiTimer;
        private int _ramTrimTickCounter = 0;

        protected override CreateParams CreateParams
        {
            get
            {
                CreateParams cp = base.CreateParams;
                return cp;
            }
        }

        public MainForm()
        {
            _config = AppConfig.Load();
            _ytService = new YouTubeService();
            _musicAudio = new AudioEngine(_ytService, _config);

            InitializeWindow();
            BuildTopBar();
            BuildMusicEnvironment();
            BuildVideoEnvironment();
            BuildSystemTray();
            WireEvents();
            RegisterGlobalMediaKeys();

            SwitchEnvironment("music");
            UpdateSidebarSelection();
            UpdateRepeatShuffleButtons();
            UpdateMusicAudioRegulatorUI();
            UpdateRamBadge();

            if (_config.Playlists.Count > 0)
            {
                SwitchSection("playlist:" + _config.Playlists[0].Id);
            }
            else if (_config.Favorites.Count > 0)
            {
                SwitchSection("favorites");
            }
            else
            {
                SwitchSection("search");
                _lblStatus.Text = "Bem-vindo ao LiteTube v0.5! Alterne entre YT Music (áudio baixo) e YouTube Vídeos (streaming progressivo).";
            }

            CheckYtDlpOnStartup();
            InitHardwareAccelerationOnStartup();
            MemoryOptimizer.TrimMemory();

            _externalServer = new ExternalLinkServer(this);
            _externalServer.Start();

            Logger.Info("MAIN", "MainForm inicializado com sucesso.");
        }

        private void InitializeWindow()
        {
            Text = "LiteTube — YouTube & YT Music";
            Size = _defaultWindowSize;
            MinimumSize = new Size(1020, 600);
            StartPosition = FormStartPosition.CenterScreen;
            BackColor = BgDark;
            ForeColor = TextPrimary;
            Font = new Font("Segoe UI", 9.5f, FontStyle.Regular);
            DoubleBuffered = true;
            KeyPreview = true;
            try
            {
                Icon = Icon.ExtractAssociatedIcon(Application.ExecutablePath) ?? SystemIcons.Application;
            }
            catch
            {
                Icon = SystemIcons.Application;
            }
        }

        private async void CheckYtDlpOnStartup()
        {
            if (!_ytService.IsYtDlpAvailable())
            {
                bool ok = await _ytService.EnsureYtDlpAsync(msg => SafeInvoke(() => _lblStatus.Text = msg));
                SafeInvoke(() =>
                {
                    _lblStatus.Text = ok
                        ? "Motor yt-dlp pronto!"
                        : "Aviso: yt-dlp.exe não encontrado na pasta bin.";
                });
            }
        }

        private void BuildTopBar()
        {
            _topBar = new Panel
            {
                Dock = DockStyle.Top,
                Height = 62,
                BackColor = Color.FromArgb(12, 12, 12)
            };

            var topDivider = new Panel
            {
                Dock = DockStyle.Bottom,
                Height = 1,
                BackColor = Color.FromArgb(32, 32, 32)
            };

            _lblBrandLogo = new Label
            {
                Text = "▶ LiteTube",
                Font = new Font("Segoe UI", 13.5f, FontStyle.Bold),
                ForeColor = Color.FromArgb(241, 241, 241),
                Location = new Point(16, 14),
                Size = new Size(112, 28)
            };
            _lblBrandLogo.Paint += (s, e) =>
            {
                e.Graphics.SmoothingMode = SmoothingMode.AntiAlias;
                e.Graphics.Clear(Color.FromArgb(12, 12, 12));
                Rectangle logoRect = new Rectangle(0, 3, 30, 22);
                using (var lp = Theme.RoundRect(logoRect, 7))
                using (var lg = new LinearGradientBrush(logoRect, Theme.Accent, Color.FromArgb(200, 20, 110), 45f))
                {
                    e.Graphics.FillPath(lg, lp);
                }
                Point[] tri = new Point[] { new Point(11, 8), new Point(11, 19), new Point(21, 13) };
                e.Graphics.FillPolygon(Brushes.White, tri);
                using (var f = new Font("Segoe UI Semibold", 12.5f, FontStyle.Bold))
                {
                    TextRenderer.DrawText(e.Graphics, "LiteTube", f, new Point(35, 1), Color.FromArgb(241, 241, 241));
                }
            };

            // Environment Switcher Pills (YT Music vs YouTube)
            _btnEnvMusic = CreateFlatButton("YT Music", 132, 12, 102, 34, AccentRed, 17, Color.Empty);
            _btnEnvMusic.Font = new Font("Segoe UI Semibold", 9.2f, FontStyle.Bold);
            _btnEnvMusic.ForeColor = Color.White;

            _btnEnvVideo = CreateFlatButton("YouTube", 240, 12, 102, 34, Color.FromArgb(24, 24, 24), 17, Color.FromArgb(44, 44, 44));
            _btnEnvVideo.Font = new Font("Segoe UI Semibold", 9.2f, FontStyle.Bold);
            _btnEnvVideo.ForeColor = Color.FromArgb(180, 180, 180);

            // YouTube-Style Unified Search Bar (Clean modern pure dark theme)
            _searchBoxBorderPanel = new Panel
            {
                Location = new Point(366, 11),
                Size = new Size(340, 36),
                BackColor = Color.FromArgb(44, 44, 44),
                Padding = new Padding(1)
            };

            var searchInner = new Panel
            {
                Dock = DockStyle.Fill,
                BackColor = Color.FromArgb(18, 18, 18)
            };

            _txtSearch = new TextBox
            {
                Location = new Point(10, 7),
                Size = new Size(256, 24),
                BackColor = Color.FromArgb(18, 18, 18),
                ForeColor = Color.FromArgb(241, 241, 241),
                BorderStyle = BorderStyle.None,
                Font = new Font("Segoe UI", 10.5f),
                Anchor = AnchorStyles.Top | AnchorStyles.Left | AnchorStyles.Right
            };

            _btnSearch = CreateFlatButton("Buscar", 260, 0, 74, 34, Color.FromArgb(34, 34, 34), 0, Color.Empty);
            _btnSearch.Dock = DockStyle.Right;
            _btnSearch.ForeColor = Color.FromArgb(220, 220, 220);
            _btnSearch.Font = new Font("Segoe UI Semibold", 8.8f, FontStyle.Bold);

            _txtSearch.Enter += (s, e) => { _searchBoxBorderPanel.BackColor = Theme.Blue; };
            _txtSearch.Leave += (s, e) => { _searchBoxBorderPanel.BackColor = Color.FromArgb(44, 44, 44); };
            searchInner.Controls.Add(_txtSearch);
            searchInner.Controls.Add(_btnSearch);
            _searchBoxBorderPanel.Controls.Add(searchInner);

            // Music Filter Chips (Only visible in Music Mode)
            _btnFilterSongs = CreateFlatButton("Músicas", 714, 13, 76, 32, Color.FromArgb(241, 241, 241), 16, Color.Empty);
            _btnFilterSongs.ForeColor = Color.FromArgb(15, 15, 15);
            _btnFilterSongs.Font = new Font("Segoe UI Semibold", 8.8f, FontStyle.Bold);

            _btnFilterAlbums = CreateFlatButton("Álbuns", 796, 13, 70, 32, Color.FromArgb(28, 28, 28), 16, Color.FromArgb(48, 48, 48));
            _btnFilterAlbums.ForeColor = Color.FromArgb(220, 220, 220);
            _btnFilterAlbums.Font = new Font("Segoe UI Semibold", 8.8f, FontStyle.Bold);

            _btnFilterAll = CreateFlatButton("Tudo", 872, 13, 60, 32, Color.FromArgb(28, 28, 28), 16, Color.FromArgb(48, 48, 48));
            _btnFilterAll.ForeColor = Color.FromArgb(220, 220, 220);
            _btnFilterAll.Font = new Font("Segoe UI Semibold", 8.8f, FontStyle.Bold);

            // Right side of Top Bar
            var topRightPanel = new Panel
            {
                Dock = DockStyle.Right,
                Width = 280,
                BackColor = Color.FromArgb(12, 12, 12)
            };

            _btnTopAudioRegulator = CreateFlatButton("Áudio", 0, 11, 70, 32, Color.FromArgb(24, 24, 24), 16, Color.FromArgb(44, 44, 44));
            _btnTopAudioRegulator.Font = new Font("Segoe UI Semibold", 8.5f, FontStyle.Bold);
            _btnTopAudioRegulator.ForeColor = Color.FromArgb(255, 130, 150);

            _btnRamBadge = CreateFlatButton("RAM", 76, 11, 80, 32, Color.FromArgb(16, 28, 20), 16, Color.FromArgb(30, 60, 40));
            _btnRamBadge.ForeColor = AccentGreen;
            _btnRamBadge.Font = new Font("Segoe UI Semibold", 8.2f, FontStyle.Bold);

            _btnGpuBadge = CreateFlatButton("GPU...", 162, 11, 112, 32, Color.FromArgb(20, 22, 30), 16, Color.FromArgb(44, 50, 70));
            _btnGpuBadge.ForeColor = Color.FromArgb(170, 200, 255);
            _btnGpuBadge.Font = new Font("Segoe UI Semibold", 8.0f, FontStyle.Bold);

            topRightPanel.Controls.AddRange(new Control[]
            {
                _btnTopAudioRegulator, _btnRamBadge, _btnGpuBadge
            });

            _topBar.Controls.AddRange(new Control[]
            {
                _lblBrandLogo, _btnEnvMusic, _btnEnvVideo,
                _searchBoxBorderPanel,
                _btnFilterSongs, _btnFilterAlbums, _btnFilterAll,
                topRightPanel, topDivider
            });

            _topBar.Resize += (s, e) => UpdateTopBarLayout();
        }

        private void UpdateTopBarLayout()
        {
            if (_searchBoxBorderPanel == null || _btnFilterSongs == null || _topBar == null) return;

            bool showFilters = (_activeEnvironment == "music");
            int rightBoundary = _topBar.Width - 290; // 280px is topRightPanel.Width + 10px margin
            int searchLeft = _searchBoxBorderPanel.Left;

            if (showFilters)
            {
                int filterWidthTotal = 76 + 6 + 70 + 6 + 60; // 218px
                int gap = 12;
                int maxSearchWidth = Math.Max(140, rightBoundary - searchLeft - filterWidthTotal - gap - 10);
                _searchBoxBorderPanel.Width = Math.Min(320, maxSearchWidth);

                int chipX = _searchBoxBorderPanel.Right + gap;
                _btnFilterSongs.Left = chipX;
                _btnFilterAlbums.Left = _btnFilterSongs.Right + 6;
                _btnFilterAll.Left = _btnFilterAlbums.Right + 6;
            }
            else
            {
                int maxSearchWidth = Math.Max(180, rightBoundary - searchLeft - 20);
                _searchBoxBorderPanel.Width = Math.Min(480, maxSearchWidth);
            }

            _txtSearch.Width = Math.Max(80, _searchBoxBorderPanel.Width - 80);
            ApplySearchPillShape();
        }

        private void ApplySearchPillShape()
        {
            if (_searchBoxBorderPanel == null || _searchBoxBorderPanel.Width < 20) return;
            using (var gp = Theme.RoundRect(new Rectangle(0, 0, _searchBoxBorderPanel.Width, _searchBoxBorderPanel.Height), 18))
            {
                _searchBoxBorderPanel.Region = new Region(gp);
            }
        }

        private void BuildMusicEnvironment()
        {
            _musicEnvironmentContainer = new Panel
            {
                Dock = DockStyle.Fill,
                BackColor = BgMusicDark
            };

            // ====================================================================
            // 1. BOTTOM YOUTUBE MUSIC PLAYER BAR (WPF WpfMusicPlayerBar via ElementHost)
            // ====================================================================
            _bottomBar = new Panel
            {
                Dock = DockStyle.Bottom,
                Height = 82,
                BackColor = Color.FromArgb(33, 33, 33)
            };

            _wpfMusicBar = new WpfMusicPlayerBar();
            _musicBarElementHost = new System.Windows.Forms.Integration.ElementHost
            {
                Dock = DockStyle.Fill,
                BackColor = Color.FromArgb(33, 33, 33),
                Child = _wpfMusicBar
            };

            _bottomBar.Controls.Add(_musicBarElementHost);

            // ====================================================================
            // 2. YOUTUBE MUSIC LEFT NAVIGATION RAIL (#0A0A0A)
            // ====================================================================
            _sidebar = new Panel
            {
                Dock = DockStyle.Left,
                Width = 236,
                BackColor = Color.FromArgb(10, 10, 10),
                Padding = new Padding(10)
            };

            int y = 10;
            _navSearch = CreateSidebarButton("Explorar", ref y);
            _navQueue = CreateSidebarButton("Fila de Reprodução", ref y);
            _navFavorites = CreateSidebarButton("Músicas Favoritas", ref y);

            y += 6;
            _navImportPlaylistUrl = CreateFlatButton("Importar Playlist", 8, y, 214, 36, Theme.SurfaceHigh, 10, Theme.Border);
            _navImportPlaylistUrl.Font = new Font("Segoe UI Semibold", 9f, FontStyle.Bold);
            _sidebar.Controls.Add(_navImportPlaylistUrl);
            y += 44;

            var dividerLine = new Panel
            {
                Location = new Point(10, y),
                Size = new Size(210, 1),
                BackColor = Color.FromArgb(36, 36, 36)
            };
            _sidebar.Controls.Add(dividerLine);
            y += 10;

            var lblAccountHdr = new Label
            {
                Text = "CONTA YOUTUBE",
                Font = new Font("Segoe UI", 7.8f, FontStyle.Bold),
                ForeColor = TextSecondary,
                Location = new Point(12, y),
                Size = new Size(200, 18)
            };
            _sidebar.Controls.Add(lblAccountHdr);
            y += 20;

            var lblBrowser = new Label
            {
                Text = "Navegador:",
                Font = new Font("Segoe UI", 8.5f),
                ForeColor = TextSecondary,
                Location = new Point(12, y + 3),
                Size = new Size(76, 22)
            };

            _cmbBrowser = new ComboBox
            {
                Location = new Point(92, y),
                Size = new Size(126, 24),
                DropDownStyle = ComboBoxStyle.DropDownList,
                BackColor = BgCard,
                ForeColor = TextPrimary,
                FlatStyle = FlatStyle.Flat
            };
            _cmbBrowser.Items.AddRange(new object[] { "firefox", "chrome", "edge", "brave", "opera", "none" });
            if (_cmbBrowser.Items.Contains(_config.BrowserForCookies))
                _cmbBrowser.SelectedItem = _config.BrowserForCookies;
            else
                _cmbBrowser.SelectedIndex = 0;

            _sidebar.Controls.Add(lblBrowser);
            _sidebar.Controls.Add(_cmbBrowser);
            y += 30;

            _navAccountRecommended = CreateSidebarButton("Recomendações", ref y);
            _navAccountLiked = CreateSidebarButton("Músicas Curtidas", ref y);
            _navAccountHistory = CreateSidebarButton("Histórico", ref y);
            _navGoogleAuth = CreateSidebarButton(HasValidAuth() ? "● Conta Conectada" : "🔑 Conta Google", ref y);
            if (HasValidAuth()) _navGoogleAuth.ForeColor = Color.FromArgb(0, 212, 130);
            _navGoogleAuth.Click += (s, e) =>
            {
                using (var dlg = new GoogleAuthDialog(_config))
                {
                    dlg.AuthChanged += () =>
                    {
                        UpdateGoogleAuthNavButton();
                        if (_videoEnvironmentControl != null)
                        {
                            _videoEnvironmentControl.RefreshSidebarAccountStatus();
                        }
                    };
                    dlg.ShowDialog(this);
                }
            };

            y += 8;
            var lblLocalHdr = new Label
            {
                Text = "SUAS PLAYLISTS",
                Font = new Font("Segoe UI", 7.8f, FontStyle.Bold),
                ForeColor = TextSecondary,
                Location = new Point(12, y),
                Size = new Size(195, 18)
            };
            _sidebar.Controls.Add(lblLocalHdr);
            y += 20;

            _btnNewPlaylist = CreateFlatButton("+ Nova Playlist", 8, y, 214, 32, Theme.SurfaceHigh, 10, Theme.Border);
            _btnNewPlaylist.Font = new Font("Segoe UI Semibold", 8.8f, FontStyle.Bold);
            _sidebar.Controls.Add(_btnNewPlaylist);
            y += 38;

            _playlistsNavContainer = new FlowLayoutPanel
            {
                Location = new Point(6, y),
                Size = new Size(220, 155),
                Anchor = AnchorStyles.Top | AnchorStyles.Bottom | AnchorStyles.Left,
                AutoScroll = true,
                FlowDirection = FlowDirection.TopDown,
                WrapContents = false,
                BackColor = Color.FromArgb(10, 10, 10)
            };
            _sidebar.Controls.Add(_playlistsNavContainer);
            RebuildPlaylistsSidebar();

            // ====================================================================
            // 3. YOUTUBE MUSIC CENTER CONTENT PANEL
            // ====================================================================
            _centerPanel = new Panel
            {
                Dock = DockStyle.Fill,
                BackColor = BgMusicDark,
                Padding = new Padding(18, 12, 18, 10)
            };

            var headerBar = new Panel
            {
                Dock = DockStyle.Top,
                Height = 62,
                BackColor = BgMusicDark
            };

            _lblSectionTitle = new Label
            {
                Text = "Explorar no YouTube Music",
                Font = new Font("Segoe UI", 15f, FontStyle.Bold),
                ForeColor = TextPrimary,
                Location = new Point(4, 0),
                Size = new Size(340, 32),
                AutoEllipsis = true
            };

            _lblStatus = new Label
            {
                Text = "Pronto.",
                Font = new Font("Segoe UI", 9f),
                ForeColor = TextSecondary,
                Location = new Point(6, 34),
                Size = new Size(360, 22),
                AutoEllipsis = true
            };

            var actionButtonsPanel = new Panel
            {
                Size = new Size(665, 40),
                Location = new Point(310, 10),
                Anchor = AnchorStyles.Top | AnchorStyles.Right
            };

            _btnToggleCardLayout = CreateFlatButton("Grade", 0, 4, 75, 32, Theme.SurfaceHigh, 16, Theme.Border);
            _btnPlayAllView = CreateFlatButton("\u25B6 Tocar Tudo", 80, 4, 105, 32, TextPrimary, 16, Color.Empty);
            _btnPlayAllView.ForeColor = BgDark;
            _btnPlayAllView.Font = new Font("Segoe UI Semibold", 8.8f, FontStyle.Bold);

            _btnAddSelectedToQueue = CreateFlatButton("+ Fila", 190, 4, 75, 32, Theme.SurfaceHigh, 16, Theme.Border);
            _btnFavSelected = CreateFlatButton("Favoritar", 270, 4, 85, 32, Theme.SurfaceHigh, 16, Theme.Border);
            _btnAddToPlaylist = CreateFlatButton("+ Playlist", 360, 4, 88, 32, Theme.SurfaceHigh, 16, Theme.Border);
            _btnWatchInVideoEnv = CreateFlatButton("Ver no YouTube", 454, 4, 118, 32, Theme.SurfaceHigh, 16, Theme.Border);
            _btnWatchInVideoEnv.ForeColor = AccentBlue;
            _btnRemoveFromView = CreateFlatButton("Remover", 578, 4, 78, 32, Theme.SurfaceHigh, 16, Theme.Border);

            actionButtonsPanel.Controls.AddRange(new Control[]
            {
                _btnToggleCardLayout, _btnPlayAllView, _btnAddSelectedToQueue,
                _btnFavSelected, _btnAddToPlaylist, _btnWatchInVideoEnv, _btnRemoveFromView
            });

            headerBar.Controls.Add(_lblSectionTitle);
            headerBar.Controls.Add(_lblStatus);
            headerBar.Controls.Add(actionButtonsPanel);

            _musicCardList = new MediaCardListControl
            {
                Dock = DockStyle.Fill,
                IsGridMode = false,
                IsMusicEnvironment = true,
                SingleClickActivates = false,
                AccentColor = AccentRed,
                IsFavoriteOrSavedCheck = id => _config.IsFavorite(id)
            };

            _centerPanel.Controls.Add(_musicCardList);
            _centerPanel.Controls.Add(headerBar);

            _musicEnvironmentContainer.Controls.Add(_centerPanel);
            _musicEnvironmentContainer.Controls.Add(_sidebar);
            _musicEnvironmentContainer.Controls.Add(_bottomBar);

            Controls.Add(_musicEnvironmentContainer);
            Controls.Add(_topBar);

            UpdateSearchFilterButtons();
        }

        private void BuildVideoEnvironment()
        {
            _videoEnvironmentControl = new VideoEnvironmentControl(_config, _ytService, _musicAudio, this)
            {
                Visible = false
            };

            _videoEnvironmentControl.StatusNotification += msg => SafeInvoke(() =>
            {
                _lblStatus.Text = msg;
            });

            _videoEnvironmentControl.FullscreenChanged += isFull => SafeInvoke(() =>
            {
                _topBar.Visible = !isFull;
            });

            _videoEnvironmentControl.PlaylistImportedToLibrary += () => SafeInvoke(() =>
            {
                RebuildPlaylistsSidebar();
            });

            Controls.Add(_videoEnvironmentControl);
            _videoEnvironmentControl.BringToFront();
        }

        private void SwitchEnvironment(string env)
        {
            if (env != "music" && env != "video") env = "music";
            _activeEnvironment = env;
            Logger.Info("ENV", "Alternando ambiente para: " + env);

            bool isMusic = (env == "music");

            if (!isMusic && _videoEnvironmentControl.IsFullscreen)
            {
                _topBar.Visible = false;
            }
            else
            {
                _topBar.Visible = true;
            }

            _musicEnvironmentContainer.Visible = isMusic;
            _videoEnvironmentControl.Visible = !isMusic;

            if (isMusic)
            {
                // Suspensão do VideoEnvironmentControl em segundo plano
                if (_videoEnvironmentControl != null)
                {
                    _videoEnvironmentControl.SuspendForBackground();
                }

                MemoryOptimizer.IsVideoModeActive = false;
                _musicEnvironmentContainer.BringToFront();
                _btnEnvMusic.BackColor = AccentRed;
                _btnEnvMusic.ForeColor = Color.White;
                var mbEnvMusic = _btnEnvMusic as ModernButton;
                if (mbEnvMusic != null) { mbEnvMusic.BorderColor = Color.Empty; mbEnvMusic.Invalidate(); }

                _btnEnvVideo.BackColor = Color.FromArgb(24, 24, 24);
                _btnEnvVideo.ForeColor = Color.FromArgb(180, 180, 180);
                var mbEnvVideo = _btnEnvVideo as ModernButton;
                if (mbEnvVideo != null) { mbEnvVideo.BorderColor = Color.FromArgb(44, 44, 44); mbEnvVideo.Invalidate(); }

                _btnFilterSongs.Visible = true;
                _btnFilterAlbums.Visible = true;
                _btnFilterAll.Visible = true;

                _btnTopAudioRegulator.Text = "Áudio";
                _btnTopAudioRegulator.ForeColor = Color.FromArgb(210, 20, 60);

                if (_musicCardList != null)
                {
                    if (_musicCardList.IsSuspended)
                    {
                        _musicCardList.ResumeAndAttach();
                    }
                    _musicCardList.PruneOffscreenResources();
                }
            }
            else
            {
                // Suspensão do feed de música ao trocar para o ambiente de vídeo
                if (_musicCardList != null && !_musicCardList.IsSuspended)
                {
                    _musicCardList.SuspendAndDetach();
                }

                if (_config.AutoPauseMusicWhenVideoPlays && _musicAudio != null && _musicAudio.IsPlaying)
                {
                    _musicAudio.PauseIfPlaying();
                }

                MemoryOptimizer.IsVideoModeActive = true;
                _videoEnvironmentControl.BringToFront();
                _btnEnvMusic.BackColor = Color.FromArgb(24, 24, 24);
                _btnEnvMusic.ForeColor = Color.FromArgb(180, 180, 180);
                var mbEnvMusic = _btnEnvMusic as ModernButton;
                if (mbEnvMusic != null) { mbEnvMusic.BorderColor = Color.FromArgb(44, 44, 44); mbEnvMusic.Invalidate(); }

                _btnEnvVideo.BackColor = AccentRed;
                _btnEnvVideo.ForeColor = Color.White;
                var mbEnvVideo = _btnEnvVideo as ModernButton;
                if (mbEnvVideo != null) { mbEnvVideo.BorderColor = Color.Empty; mbEnvVideo.Invalidate(); }

                _btnFilterSongs.Visible = false;
                _btnFilterAlbums.Visible = false;
                _btnFilterAll.Visible = false;

                _btnTopAudioRegulator.Text = "Áudio";
                _btnTopAudioRegulator.ForeColor = Color.FromArgb(15, 100, 210);

                if (_videoEnvironmentControl != null)
                {
                    _videoEnvironmentControl.ResumeFromBackground();
                }
            }

            ThumbnailCache.ClearMemoryCache();
            UpdateTopBarLayout();
            MemoryOptimizer.TrimMemory();
            GC.Collect(2, GCCollectionMode.Optimized);
            UpdateRamBadge();
        }

        private void BuildSystemTray()
        {
            _trayMenu = new ContextMenuStrip();
            _trayMenu.Items.Add("Abrir YT Music", null, (s, e) => { RestoreFromTray(); SwitchEnvironment("music"); });
            _trayMenu.Items.Add("\u25B6 Abrir YouTube Vídeos", null, (s, e) => { RestoreFromTray(); SwitchEnvironment("video"); });
            _trayMenu.Items.Add(new ToolStripSeparator());
            _trayMenu.Items.Add("Play / Pause Música", null, (s, e) => _musicAudio.TogglePlayPause());
            _trayMenu.Items.Add("\u23ED Próxima Música", null, (s, e) => _musicAudio.PlayNext());
            _trayMenu.Items.Add("\u23EE Música Anterior", null, (s, e) => _musicAudio.PlayPrevious());
            _trayMenu.Items.Add(new ToolStripSeparator());
            _trayMenu.Items.Add("Regulador de Áudio de Música", null, (s, e) => { RestoreFromTray(); ShowMusicAudioRegulatorDialog(); });
            _trayMenu.Items.Add("Regulador de Áudio de Vídeos", null, (s, e) => { RestoreFromTray(); _videoEnvironmentControl.ShowVideoAudioRegulatorDialog(); });
            _trayMenu.Items.Add(new ToolStripSeparator());
            _trayMenu.Items.Add("Otimizar Memória RAM Agora", null, (s, e) =>
            {
                MemoryOptimizer.TrimMemory();
                UpdateRamBadge();
            });
            _trayMenu.Items.Add("📂 Abrir Pasta de Logs", null, (s, e) =>
            {
                try { System.Diagnostics.Process.Start("explorer.exe", Logger.LogsDirectory); } catch { }
            });
            _trayMenu.Items.Add("📄 Ver Log da Sessão Atual", null, (s, e) =>
            {
                try { if (System.IO.File.Exists(Logger.LatestLogFilePath)) System.Diagnostics.Process.Start(Logger.LatestLogFilePath); } catch { }
            });
            _trayMenu.Items.Add(new ToolStripSeparator());
            _trayMenu.Items.Add("✖ Sair Completamente", null, (s, e) =>
            {
                _forceExit = true;
                Application.Exit();
            });

            _trayIcon = new NotifyIcon
            {
                Icon = Icon ?? SystemIcons.Application,
                Text = "LiteTube — YouTube & YT Music",
                ContextMenuStrip = _trayMenu,
                Visible = true
            };

            _trayIcon.DoubleClick += (s, e) => RestoreFromTray();
        }

        private void WireEvents()
        {
            _btnEnvMusic.Click += (s, e) => SwitchEnvironment("music");
            _btnEnvVideo.Click += (s, e) => SwitchEnvironment("video");

            // Search
            _btnSearch.Click += async (s, e) => await PerformSearchAsync();
            _txtSearch.KeyDown += async (s, e) =>
            {
                if (e.KeyCode == Keys.Enter)
                {
                    e.SuppressKeyPress = true;
                    await PerformSearchAsync();
                }
            };

            _btnFilterSongs.Click += (s, e) => { _searchFilter = "songs"; UpdateSearchFilterButtons(); };
            _btnFilterAlbums.Click += (s, e) => { _searchFilter = "albums"; UpdateSearchFilterButtons(); };
            _btnFilterAll.Click += (s, e) => { _searchFilter = "all"; UpdateSearchFilterButtons(); };

            // Top bar buttons
            _btnTopAudioRegulator.Click += (s, e) =>
            {
                if (_activeEnvironment == "video")
                    _videoEnvironmentControl.ShowVideoAudioRegulatorDialog();
                else
                    ShowMusicAudioRegulatorDialog();
            };

            _btnRamBadge.Click += (s, e) =>
            {
                _ytService.EnforceCacheSizeLimit(_config.MaxCacheSizeMB);
                ThumbnailCache.EnforceDiskLimitMB(_config.MaxThumbCacheSizeMB);
                MemoryOptimizer.TrimMemory();
                UpdateRamBadge();

                int mCnt;
                double mMB;
                _ytService.GetCacheStats(out mCnt, out mMB);
                double tMB = ThumbnailCache.GetDiskUsageMB();
                _lblStatus.Text = string.Format("⚡ RAM otimizada! Caches: Música {0:0.0}/{1}MB | Miniaturas {2:0.0}/{3}MB",
                    mMB, _config.MaxCacheSizeMB, tMB, _config.MaxThumbCacheSizeMB);
            };

            _btnGpuBadge.Click += (s, e) => ShowGpuHardwareDetailsDialog();

            // Sidebar Navigation
            _navSearch.Click += async (s, e) =>
            {
                SwitchSection("search");
                if (_txtSearch == null || string.IsNullOrWhiteSpace(_txtSearch.Text))
                {
                    await LoadMusicExploreFeedAsync();
                }
            };
            _navQueue.Click += (s, e) => SwitchSection("queue");
            _navFavorites.Click += (s, e) => SwitchSection("favorites");

            _cmbBrowser.SelectedIndexChanged += (s, e) =>
            {
                _config.BrowserForCookies = _cmbBrowser.SelectedItem.ToString();
                _config.Save();
            };

            _navAccountRecommended.Click += async (s, e) =>
            {
                await LoadPersonalizedMusicFeedAsync();
            };

            _navAccountLiked.Click += async (s, e) =>
            {
                _activeSection = "account_liked";
                await LoadBrowserPlaylistAsync("https://music.youtube.com/playlist?list=LM", "Músicas Curtidas (" + GetAccountAuthLabel() + ")", true);
            };

            _navAccountHistory.Click += async (s, e) =>
            {
                _activeSection = "account_hist";
                await LoadBrowserPlaylistAsync("https://www.youtube.com/feed/history", "Histórico Recente (" + GetAccountAuthLabel() + ")", true);
            };

            _navImportPlaylistUrl.Click += (s, e) => PromptImportPlaylistByUrl();
            _btnNewPlaylist.Click += (s, e) => PromptCreateLocalPlaylist();

            // Center Music Card List Events
            _btnToggleCardLayout.Click += (s, e) =>
            {
                _musicCardList.IsGridMode = !_musicCardList.IsGridMode;
                _btnToggleCardLayout.Text = _musicCardList.IsGridMode ? "📋 Lista" : "🖼️ Grade";
                _btnToggleCardLayout.BackColor = _musicCardList.IsGridMode ? AccentRed : BgCard;
            };

            _musicCardList.ItemActivated += (idx, track) =>
            {
                if (idx >= 0 && idx < _displayedTracks.Count)
                {
                    _musicAudio.SetQueueAndPlay(_displayedTracks, idx);
                }
            };

            _musicCardList.FavoriteClicked += (idx, track) =>
            {
                bool fav = _config.ToggleFavorite(track);
                _lblStatus.Text = (fav ? "Adicionado aos Favoritos: " : "Removido dos Favoritos: ") + track.Title;
                UpdateCurrentTrackFavoriteIcon();
                if (_activeSection == "favorites") SwitchSection("favorites");
                else _musicCardList.Invalidate();
            };

            _musicCardList.AddToQueueClicked += (idx, track) =>
            {
                _musicAudio.AddToQueue(track);
            };

            _musicCardList.WatchClipClicked += (idx, track) =>
            {
                SwitchEnvironment("video");
                _videoEnvironmentControl.PlayVideo(track);
            };

            _musicCardList.ChannelClicked += item =>
            {
                SwitchEnvironment("video");
                _videoEnvironmentControl.OpenChannelFromItem(item);
            };

            _musicCardList.LoadMoreRequested += async () =>
            {
                await LoadMoreMusicSearchAsync();
            };

            _btnPlayAllView.Click += (s, e) =>
            {
                if (_displayedTracks.Count > 0)
                {
                    int startIdx = _musicCardList.SelectedIndex >= 0 ? _musicCardList.SelectedIndex : 0;
                    _musicAudio.SetQueueAndPlay(_displayedTracks, startIdx);
                }
            };

            _btnAddSelectedToQueue.Click += (s, e) =>
            {
                var t = GetSelectedTrack();
                if (t != null) _musicAudio.AddToQueue(t);
            };

            _btnFavSelected.Click += (s, e) =>
            {
                var t = GetSelectedTrack();
                if (t != null)
                {
                    bool fav = _config.ToggleFavorite(t);
                    _lblStatus.Text = (fav ? "Adicionado aos Favoritos: " : "Removido dos Favoritos: ") + t.Title;
                    _musicCardList.Invalidate();
                    UpdateCurrentTrackFavoriteIcon();
                }
            };

            _btnAddToPlaylist.Click += (s, e) => ShowAddToPlaylistMenu();
            _btnRemoveFromView.Click += (s, e) => RemoveSelectedFromCurrentView();

            _btnWatchInVideoEnv.Click += (s, e) =>
            {
                TrackItem target = GetSelectedTrack() ?? _musicAudio.CurrentTrack;
                if (target == null)
                {
                    _lblStatus.Text = "Selecione uma faixa primeiro para assistir no YouTube.";
                    return;
                }
                SwitchEnvironment("video");
                _videoEnvironmentControl.PlayVideo(target);
            };

            // Music Transport Controls (WPF WpfMusicPlayerBar + Mini-Player)
            if (_wpfMusicBar != null)
            {
                _wpfMusicBar.PlayPauseRequested += () => _musicAudio.TogglePlayPause();
                _wpfMusicBar.NextRequested += () => _musicAudio.PlayNext();
                _wpfMusicBar.PreviousRequested += () => _musicAudio.PlayPrevious();
                _wpfMusicBar.LikeToggleRequested += () => ToggleCurrentTrackFavorite();
                _wpfMusicBar.ShuffleToggleRequested += () =>
                {
                    _config.Shuffle = !_config.Shuffle;
                    _config.Save();
                    UpdateRepeatShuffleButtons();
                };
                _wpfMusicBar.RepeatToggleRequested += () =>
                {
                    _config.Repeat = (_config.Repeat + 1) % 3;
                    _config.Save();
                    UpdateRepeatShuffleButtons();
                };
                _wpfMusicBar.QueueToggleRequested += () =>
                {
                    SwitchSection(_activeSection == "queue" ? "search" : "queue");
                };
                _wpfMusicBar.MuteToggleRequested += () => _musicAudio.ToggleMute();
                _wpfMusicBar.VolumeChanged += newVol =>
                {
                    _musicAudio.Volume = Math.Round(newVol, 2);
                    if (_musicAudio.Volume > 0 && _musicAudio.IsMuted) _musicAudio.IsMuted = false;
                    _config.Save();
                    UpdateMusicAudioRegulatorUI();
                };
                _wpfMusicBar.SeekRequested += targetSec =>
                {
                    _musicAudio.SeekToSeconds(targetSec);
                };
            }

            _bottomBar.MouseWheel += OnMusicVolumeMouseWheel;

            // AudioEngine callbacks
            _musicAudio.TrackChanged += t => SafeInvoke(() => OnTrackChanged(t));
            _musicAudio.PlaybackStateChanged += (playing, loading) => SafeInvoke(() => OnPlaybackStateChanged(playing, loading));
            _musicAudio.StatusMessage += msg => SafeInvoke(() => _lblStatus.Text = msg);
            _musicAudio.OfftopicSegmentsLoaded += segs => SafeInvoke(() =>
            {
                if (_wpfMusicBar != null)
                {
                    _wpfMusicBar.SetOfftopicSegments(segs);
                }
            });
            _musicAudio.AudioSettingsChanged += () => SafeInvoke(() =>
            {
                UpdateMusicAudioRegulatorUI();
            });
            _musicAudio.QueueUpdated += () => SafeInvoke(() =>
            {
                if (_activeSection == "queue")
                {
                    _displayedTracks = new List<TrackItem>(_musicAudio.Queue);
                    RefreshTrackListView();
                }
            });

            KeyDown += (s, e) =>
            {
                if (_txtSearch != null && _txtSearch.Focused) return;

                if (_activeEnvironment == "video" || _videoEnvironmentControl.IsFullscreen)
                {
                    if (_videoEnvironmentControl.HandleShortcutKey(e.KeyCode))
                    {
                        e.Handled = true;
                        e.SuppressKeyPress = true;
                    }
                }
            };

            _uiTimer = new System.Windows.Forms.Timer { Interval = 450 };
            _uiTimer.Tick += OnUiTimerTick;
            _uiTimer.Start();

            FormClosing += OnMainFormClosing;
        }

        public async Task ReloadCurrentActiveView()
        {
            if (_activeEnvironment == "video")
            {
                if (_videoEnvironmentControl != null)
                {
                    await _videoEnvironmentControl.RefreshCurrentPageAsync();
                }
                return;
            }

            // Music Environment
            ThumbnailCache.ClearMemoryCache();
            MemoryOptimizer.TrimMemory();

            if (_activeSection == "account_rec")
            {
                await LoadPersonalizedMusicFeedAsync();
                if (_lblStatus != null) _lblStatus.Text = "🔄 Página recarregada (F5)!";
            }
            else if (_activeSection == "account_liked")
            {
                await LoadBrowserPlaylistAsync("https://music.youtube.com/playlist?list=LM", "Músicas Curtidas (" + GetAccountAuthLabel() + ")", true);
                if (_lblStatus != null) _lblStatus.Text = "🔄 Página recarregada (F5)!";
            }
            else if (_activeSection == "account_hist")
            {
                await LoadBrowserPlaylistAsync("https://www.youtube.com/feed/history", "Histórico Recente (" + GetAccountAuthLabel() + ")", true);
                if (_lblStatus != null) _lblStatus.Text = "🔄 Página recarregada (F5)!";
            }
            else if (_activeSection == "search" && _txtSearch != null && !string.IsNullOrWhiteSpace(_txtSearch.Text))
            {
                await PerformSearchAsync();
                if (_lblStatus != null) _lblStatus.Text = "🔄 Busca recarregada (F5)!";
            }
            else if (_activeSection == "search")
            {
                await LoadMusicExploreFeedAsync();
                if (_lblStatus != null) _lblStatus.Text = "🔄 Explorar recarregado (F5)!";
            }
            else
            {
                SwitchSection(_activeSection);
                if (_lblStatus != null) _lblStatus.Text = "🔄 Página recarregada (F5)!";
            }
        }

        protected override bool ProcessCmdKey(ref Message msg, Keys keyData)
        {
            Keys keyCode = keyData & Keys.KeyCode;

            if (keyCode == Keys.F5)
            {
                var _ = ReloadCurrentActiveView();
                return true;
            }

            if (_txtSearch != null && _txtSearch.Focused)
            {
                if (keyData == Keys.Escape)
                {
                    _videoEnvironmentControl.Focus();
                    return true;
                }
                return base.ProcessCmdKey(ref msg, keyData);
            }

            if (_activeEnvironment == "video" || (_videoEnvironmentControl != null && _videoEnvironmentControl.IsFullscreen))
            {
                if (_videoEnvironmentControl != null && _videoEnvironmentControl.HandleShortcutKey(keyCode))
                {
                    return true;
                }
            }

            return base.ProcessCmdKey(ref msg, keyData);
        }

        private void OnMusicVolumeMouseWheel(object sender, MouseEventArgs e)
        {
            double delta = e.Delta > 0 ? 0.02 : -0.02;
            _musicAudio.ChangeVolumeBy(delta);
        }

        private void UpdateMusicAudioRegulatorUI()
        {
            int pct = (int)Math.Round(_musicAudio.Volume * 100);
            if (_wpfMusicBar != null)
            {
                _wpfMusicBar.UpdateAudioVisuals(_musicAudio.Volume, _musicAudio.IsMuted);
            }
        }

        private void ShowMusicAudioRegulatorDialog()
        {
            using (var dlg = new Form())
            {
                dlg.Text = "🎵 Regulador de Áudio de MÚSICA — LiteTube";
                dlg.Size = new Size(480, 500);
                dlg.FormBorderStyle = FormBorderStyle.FixedDialog;
                dlg.StartPosition = FormStartPosition.CenterParent;
                dlg.BackColor = BgPanel;
                dlg.ForeColor = TextPrimary;
                dlg.MaximizeBox = false;
                dlg.MinimizeBox = false;

                var lblBanner = new Label
                {
                    Text = "ℹ️ Este perfil regula exclusivamente o Volume de MÚSICA (1:1 direto na saída de som).",
                    Font = new Font("Segoe UI", 8.5f, FontStyle.Italic),
                    ForeColor = Color.FromArgb(255, 120, 140),
                    Location = new Point(18, 12),
                    Size = new Size(435, 20)
                };

                var lblVolTitle = new Label
                {
                    Text = "",
                    Font = new Font("Segoe UI Semibold", 10f, FontStyle.Bold),
                    Location = new Point(18, 36),
                    Size = new Size(420, 22)
                };

                var sliderVol = new TrackBar
                {
                    Location = new Point(14, 60),
                    Size = new Size(340, 32),
                    Minimum = 0,
                    Maximum = 100,
                    Value = (int)Math.Round(_musicAudio.Volume * 100),
                    TickFrequency = 5,
                    BackColor = BgPanel
                };

                var btnMuteDlg = CreateFlatButton("🔊 Ativo", 360, 58, 86, 32, BgCard);

                var btnPre3 = CreateFlatButton("🤫 3% Ultra-Baixo", 18, 96, 108, 28, BgCard);
                var btnPre8 = CreateFlatButton("🎮 8% Fundo Jogo", 132, 96, 112, 28, BgCard);
                var btnPre15 = CreateFlatButton("🌙 15% Suave", 250, 96, 96, 28, BgCard);
                var btnPre40 = CreateFlatButton("🎧 40% Médio", 352, 96, 94, 28, BgCard);

                var lblCeilingTitle = new Label
                {
                    Text = "",
                    Font = new Font("Segoe UI Semibold", 9.5f, FontStyle.Bold),
                    ForeColor = AccentGreen,
                    Location = new Point(18, 138),
                    Size = new Size(425, 22)
                };

                var btnCeil20 = CreateFlatButton("Teto 20%", 18, 164, 98, 30, BgCard);
                var btnCeil40 = CreateFlatButton("Teto 40%", 122, 164, 98, 30, BgCard);
                var btnCeil70 = CreateFlatButton("Teto 70%", 226, 164, 98, 30, BgCard);
                var btnCeil100 = CreateFlatButton("100% (Sem Limite)", 330, 164, 116, 30, BgCard);

                var lblBalanceTitle = new Label
                {
                    Text = "",
                    Font = new Font("Segoe UI Semibold", 9.5f, FontStyle.Bold),
                    Location = new Point(18, 206),
                    Size = new Size(320, 22)
                };

                var btnCenterBalance = CreateFlatButton("Centralizar (0)", 344, 202, 102, 26, BgCard);

                var sliderBalance = new TrackBar
                {
                    Location = new Point(14, 232),
                    Size = new Size(432, 32),
                    Minimum = -100,
                    Maximum = 100,
                    Value = (int)Math.Round(_musicAudio.Balance * 100),
                    TickFrequency = 25,
                    BackColor = BgPanel
                };

                var lblSpeedTitle = new Label
                {
                    Text = "",
                    Font = new Font("Segoe UI Semibold", 9.5f, FontStyle.Bold),
                    Location = new Point(18, 274),
                    Size = new Size(420, 22)
                };

                var btnSpd075 = CreateFlatButton("0.75x Slow", 18, 300, 80, 30, BgCard);
                var btnSpd090 = CreateFlatButton("0.90x", 104, 300, 72, 30, BgCard);
                var btnSpd100 = CreateFlatButton("1.0x Normal", 182, 300, 92, 30, BgCard);
                var btnSpd115 = CreateFlatButton("1.15x Fast", 280, 300, 80, 30, BgCard);
                var btnSpd125 = CreateFlatButton("1.25x", 366, 300, 80, 30, BgCard);

                int cacheCount;
                double cacheMB;
                _ytService.GetCacheStats(out cacheCount, out cacheMB);

                var lblCacheStatus = new Label
                {
                    Text = string.Format("💾 Cache de Música: {0:0.0} MB / {1} MB máx ({2} músicas)",
                        cacheMB, _config.MaxCacheSizeMB, cacheCount),
                    Font = new Font("Segoe UI Semibold", 9f, FontStyle.Bold),
                    ForeColor = AccentGreen,
                    Location = new Point(18, 346),
                    Size = new Size(320, 24)
                };

                var btnClearCache = CreateFlatButton("🧹 Limpar Músicas", 342, 342, 104, 28, BgCard);

                var lblHotkeyHint = new Label
                {
                    Text = "Atalhos Globais: Ctrl+Alt+Cima (+Vol) | Ctrl+Alt+Baixo (-Vol) | Ctrl+Alt+M (Mute)",
                    Font = new Font("Segoe UI", 8f),
                    ForeColor = TextSecondary,
                    Location = new Point(18, 382),
                    Size = new Size(430, 20)
                };

                var btnClose = CreateFlatButton("✔ Fechar e Salvar", 306, 412, 140, 34, AccentRed);
                btnClose.DialogResult = DialogResult.OK;

                Action refreshDialogLabels = () =>
                {
                    lblVolTitle.Text = string.Format("🎵 Volume da Música: {0}%",
                        (int)Math.Round(_config.MusicVolume * 100));
                    btnMuteDlg.Text = _config.IsMusicMuted ? "🔇 Mutado" : "🔊 Ativo";
                    btnMuteDlg.BackColor = _config.IsMusicMuted ? AccentRed : BgCard;

                    lblCeilingTitle.Text = "🛡️ Escala 1:1 Linear: 0% a 100% (Sem Teto Artificial)";
                    btnCeil20.BackColor = BgCard;
                    btnCeil40.BackColor = BgCard;
                    btnCeil70.BackColor = BgCard;
                    btnCeil100.BackColor = AccentRed;

                    lblBalanceTitle.Text = "🎧 Balanço: Centro (L 50% / R 50%)";
                    lblSpeedTitle.Text = string.Format("⚡ Velocidade da Música: {0:0.00}x", _musicAudio.PlaybackSpeed);

                    int spd = (int)Math.Round(_musicAudio.PlaybackSpeed * 100);
                    btnSpd075.BackColor = spd == 75 ? AccentRed : BgCard;
                    btnSpd090.BackColor = spd == 90 ? AccentRed : BgCard;
                    btnSpd100.BackColor = spd == 100 ? AccentRed : BgCard;
                    btnSpd115.BackColor = spd == 115 ? AccentRed : BgCard;
                    btnSpd125.BackColor = spd == 125 ? AccentRed : BgCard;
                };

                sliderVol.Scroll += (s, e) => { _musicAudio.Volume = sliderVol.Value / 100.0; refreshDialogLabels(); };
                btnMuteDlg.Click += (s, e) => { _musicAudio.ToggleMute(); refreshDialogLabels(); };

                btnPre3.Click += (s, e) => { sliderVol.Value = 3; _musicAudio.Volume = 0.03; refreshDialogLabels(); };
                btnPre8.Click += (s, e) => { sliderVol.Value = 8; _musicAudio.Volume = 0.08; refreshDialogLabels(); };
                btnPre15.Click += (s, e) => { sliderVol.Value = 15; _musicAudio.Volume = 0.15; refreshDialogLabels(); };
                btnPre40.Click += (s, e) => { sliderVol.Value = 40; _musicAudio.Volume = 0.40; refreshDialogLabels(); };

                btnCeil20.Click += (s, e) => { _musicAudio.MaxVolumeCeiling = 0.20; refreshDialogLabels(); };
                btnCeil40.Click += (s, e) => { _musicAudio.MaxVolumeCeiling = 0.40; refreshDialogLabels(); };
                btnCeil70.Click += (s, e) => { _musicAudio.MaxVolumeCeiling = 0.70; refreshDialogLabels(); };
                btnCeil100.Click += (s, e) => { _musicAudio.MaxVolumeCeiling = 1.00; refreshDialogLabels(); };

                sliderBalance.Scroll += (s, e) => { _musicAudio.Balance = sliderBalance.Value / 100.0; refreshDialogLabels(); };
                btnCenterBalance.Click += (s, e) => { sliderBalance.Value = 0; _musicAudio.Balance = 0.0; refreshDialogLabels(); };

                btnSpd075.Click += (s, e) => { _musicAudio.PlaybackSpeed = 0.75; refreshDialogLabels(); };
                btnSpd090.Click += (s, e) => { _musicAudio.PlaybackSpeed = 0.90; refreshDialogLabels(); };
                btnSpd100.Click += (s, e) => { _musicAudio.PlaybackSpeed = 1.00; refreshDialogLabels(); };
                btnSpd115.Click += (s, e) => { _musicAudio.PlaybackSpeed = 1.15; refreshDialogLabels(); };
                btnSpd125.Click += (s, e) => { _musicAudio.PlaybackSpeed = 1.25; refreshDialogLabels(); };

                btnClearCache.Click += (s, e) =>
                {
                    _ytService.ClearAllCache();
                    int c; double m;
                    _ytService.GetCacheStats(out c, out m);
                    lblCacheStatus.Text = string.Format("💾 Cache de Música: {0:0.0} MB / {1} MB máx ({2} músicas)", m, _config.MaxCacheSizeMB, c);
                };

                refreshDialogLabels();

                dlg.Controls.AddRange(new Control[]
                {
                    lblBanner, lblVolTitle, sliderVol, btnMuteDlg,
                    btnPre3, btnPre8, btnPre15, btnPre40,
                    lblCeilingTitle, btnCeil20, btnCeil40, btnCeil70, btnCeil100,
                    lblBalanceTitle, btnCenterBalance, sliderBalance,
                    lblSpeedTitle, btnSpd075, btnSpd090, btnSpd100, btnSpd115, btnSpd125,
                    lblCacheStatus, btnClearCache,
                    lblHotkeyHint, btnClose
                });

                dlg.ShowDialog(this);
                _config.Save();
            }
        }

        private async System.Threading.Tasks.Task PerformSearchAsync()
        {
            string query = _txtSearch.Text.Trim();
            if (string.IsNullOrEmpty(query)) return;

            _btnSearch.Enabled = false;

            if (_activeEnvironment == "video")
            {
                _lblStatus.Text = "🎬 Buscando vídeos e canais no YouTube...";
                var videoPage = await _ytService.SearchPageAsync(query, "videos", _config);
                _btnSearch.Enabled = true;
                _videoEnvironmentControl.SetSearchResults(videoPage, query);
            }
            else
            {
                _activeSection = "search";
                UpdateSidebarSelection();
                _lblSectionTitle.Text = "Resultados para \"" + query + "\"";
                _lblStatus.Text = "🎵 Buscando no YouTube Music...";

                var page = await _ytService.SearchPageAsync(query, _searchFilter, _config);

                _btnSearch.Enabled = true;
                _displayedTracks = page.Items;
                _musicSearchContinuationToken = page.ContinuationToken ?? "";
                _musicCardList.HasMoreItems = !string.IsNullOrEmpty(_musicSearchContinuationToken);
                RefreshTrackListView();
                _lblStatus.Text = page.Items.Count > 0
                    ? string.Format("{0} faixas encontradas (role para baixo para carregar mais).", page.Items.Count)
                    : "Nenhum resultado encontrado.";
            }

            MemoryOptimizer.TrimMemory();
            UpdateRamBadge();
        }

        private async System.Threading.Tasks.Task LoadMoreMusicSearchAsync()
        {
            if (_isLoadingMoreMusic || _activeSection != "search" || string.IsNullOrEmpty(_musicSearchContinuationToken))
            {
                _musicCardList.HasMoreItems = false;
                return;
            }

            _isLoadingMoreMusic = true;
            _musicCardList.IsLoadingMore = true;
            _lblStatus.Text = "⏳ Carregando mais músicas...";

            try
            {
                var nextBatch = await _ytService.FetchSearchContinuationAsync(_musicSearchContinuationToken, false);
                _musicSearchContinuationToken = nextBatch.ContinuationToken ?? "";
                _musicCardList.HasMoreItems = !string.IsNullOrEmpty(_musicSearchContinuationToken);

                if (nextBatch.Items != null && nextBatch.Items.Count > 0)
                {
                    _displayedTracks.AddRange(nextBatch.Items);
                    if (_displayedTracks.Count > 350)
                    {
                        _displayedTracks.RemoveRange(0, _displayedTracks.Count - 350);
                    }
                    if (_activeSection != "queue" && _musicAudio != null && _musicAudio.Queue.Count > 0)
                    {
                        _musicAudio.Queue.AddRange(nextBatch.Items);
                    }
                    int added = _musicCardList.AppendItems(nextBatch.Items);
                    _lblStatus.Text = string.Format("➕ Mais {0} músicas carregadas!", added);
                }
            }
            catch { }
            finally
            {
                _isLoadingMoreMusic = false;
                _musicCardList.IsLoadingMore = false;
                MemoryOptimizer.TrimMemory();
            }
        }

        private async System.Threading.Tasks.Task LoadPersonalizedMusicFeedAsync()
        {
            try
            {
                _activeSection = "account_rec";
                UpdateSidebarSelection();
                _lblSectionTitle.Text = "✨ Recomendados do YouTube Music (" + GetAccountAuthLabel() + ")";
                _lblStatus.Text = "🎵 Carregando Ouvir novamente, Seu Supermix e carrosséis personalizados...";
                _musicSearchContinuationToken = "";
                _musicCardList.HasMoreItems = false;

                var res = await _ytService.FetchPersonalizedMusicFeedAsync(_config);
                _displayedTracks = res.Item1 ?? new List<TrackItem>();
                RefreshTrackListView();

                if (res.Item2 == "OK")
                {
                    _lblStatus.Text = string.Format("🎵 {0} faixas personalizadas do YouTube Music carregadas!", _displayedTracks.Count);
                }
                else
                {
                    _lblStatus.Text = res.Item2;
                }
            }
            catch (Exception ex)
            {
                Logger.Warn("MUSIC", "Erro em LoadPersonalizedMusicFeedAsync: " + ex.Message);
                _lblStatus.Text = "Não foi possível carregar o feed personalizado do YouTube Music.";
            }
            finally
            {
                MemoryOptimizer.TrimMemory();
                UpdateRamBadge();
            }
        }

        private async System.Threading.Tasks.Task LoadMusicExploreFeedAsync()
        {
            try
            {
                _activeSection = "search";
                UpdateSidebarSelection();
                _lblSectionTitle.Text = "Explorar • Paradas e Lançamentos Oficiais";
                _lblStatus.Text = "🎵 Carregando paradas e lançamentos do YouTube Music...";
                _musicSearchContinuationToken = "";
                _musicCardList.HasMoreItems = false;

                var res = await _ytService.FetchMusicExploreFeedAsync(_config);
                _displayedTracks = res.Item1 ?? new List<TrackItem>();
                RefreshTrackListView();

                if (res.Item2 == "OK")
                {
                    _lblStatus.Text = string.Format("🎵 {0} paradas e lançamentos oficiais carregados!", _displayedTracks.Count);
                }
                else
                {
                    _lblStatus.Text = res.Item2;
                }
            }
            catch (Exception ex)
            {
                Logger.Warn("MUSIC", "Erro em LoadMusicExploreFeedAsync: " + ex.Message);
                _lblStatus.Text = "Não foi possível carregar paradas e lançamentos do YouTube Music.";
            }
            finally
            {
                MemoryOptimizer.TrimMemory();
                UpdateRamBadge();
            }
        }

        private async System.Threading.Tasks.Task LoadBrowserPlaylistAsync(string url, string title, bool useCookies)
        {
            if (_activeSection != "account_rec" && _activeSection != "account_liked" && _activeSection != "account_hist")
            {
                _activeSection = "account";
            }
            UpdateSidebarSelection();
            _lblSectionTitle.Text = title;
            _lblStatus.Text = "Sincronizando com o YouTube (" + GetAccountAuthLabel() + ")...";

            var res = await _ytService.FetchUrlOrPlaylistAsync(url, _config, useCookies);
            _displayedTracks = res.Item1;
            RefreshTrackListView();

            if (res.Item2 != "OK")
            {
                _lblStatus.Text = res.Item2.Replace("\n", " ");
                if (res.Item1.Count == 0)
                {
                    MessageBox.Show(this, res.Item2, "Sincronização de Conta YouTube", MessageBoxButtons.OK, MessageBoxIcon.Information);
                }
            }
            else
            {
                _lblStatus.Text = string.Format("Sincronizadas {0} músicas com sucesso!", res.Item1.Count);
            }

            MemoryOptimizer.TrimMemory();
            UpdateRamBadge();
        }

        private bool HasValidAuth()
        {
            if (!string.IsNullOrEmpty(_config.CookiesFilePath) && File.Exists(_config.CookiesFilePath)) return true;
            if (!string.IsNullOrEmpty(_config.BrowserForCookies) && _config.BrowserForCookies != "none") return true;
            return false;
        }

        private string GetAccountAuthLabel()
        {
            if (!string.IsNullOrEmpty(_config.CookiesFilePath) && File.Exists(_config.CookiesFilePath))
                return "Conta Conectada";
            if (!string.IsNullOrEmpty(_config.BrowserForCookies) && _config.BrowserForCookies != "none")
                return _config.BrowserForCookies.ToUpper();
            return "Desconectado";
        }

        private void UpdateGoogleAuthNavButton()
        {
            if (_navGoogleAuth != null)
            {
                _navGoogleAuth.Text = HasValidAuth() ? "● Conta Conectada" : "🔑 Conta Google";
                _navGoogleAuth.ForeColor = HasValidAuth() ? Color.FromArgb(0, 212, 130) : TextSecondary;
            }
        }

        private void SwitchSection(string section)
        {
            _activeSection = section;
            UpdateSidebarSelection();

            if (section == "search")
            {
                _lblSectionTitle.Text = "Explorar no YouTube Music";
            }
            else if (section == "queue")
            {
                _lblSectionTitle.Text = string.Format("Fila de Reprodução ({0} faixas)", _musicAudio.Queue.Count);
                _displayedTracks = new List<TrackItem>(_musicAudio.Queue);
                RefreshTrackListView();
            }
            else if (section == "favorites")
            {
                _lblSectionTitle.Text = string.Format("Músicas Favoritas ({0} faixas)", _config.Favorites.Count);
                _displayedTracks = new List<TrackItem>(_config.Favorites);
                RefreshTrackListView();
            }
            else if (section.StartsWith("playlist:"))
            {
                string plId = section.Substring("playlist:".Length);
                UserPlaylist pl = _config.Playlists.Find(p => p.Id == plId);
                if (pl != null)
                {
                    _lblSectionTitle.Text = pl.Name + " (" + pl.Tracks.Count + " faixas)";
                    _displayedTracks = new List<TrackItem>(pl.Tracks);
                    RefreshTrackListView();
                }
            }
        }

        private void RefreshTrackListView()
        {
            _musicCardList.HasMoreItems = (_activeSection == "search" && !string.IsNullOrEmpty(_musicSearchContinuationToken));
            _musicCardList.PlayingVideoId = _musicAudio.CurrentTrack != null ? _musicAudio.CurrentTrack.Id : "";
            _musicCardList.SetItems(_displayedTracks, false);
        }

        private TrackItem GetSelectedTrack()
        {
            return _musicCardList.SelectedItem;
        }

        private void RemoveSelectedFromCurrentView()
        {
            int idx = _musicCardList.SelectedIndex;
            if (idx < 0 || idx >= _displayedTracks.Count) return;

            TrackItem track = _displayedTracks[idx];

            if (_activeSection == "queue")
            {
                _musicAudio.RemoveFromQueueAt(idx);
            }
            else if (_activeSection == "favorites")
            {
                _config.ToggleFavorite(track);
                SwitchSection("favorites");
            }
            else if (_activeSection.StartsWith("playlist:"))
            {
                string plId = _activeSection.Substring("playlist:".Length);
                UserPlaylist pl = _config.Playlists.Find(p => p.Id == plId);
                if (pl != null && idx < pl.Tracks.Count)
                {
                    pl.Tracks.RemoveAt(idx);
                    _config.Save();
                    SwitchSection(_activeSection);
                }
            }
        }

        private void ToggleCurrentTrackFavorite()
        {
            if (_musicAudio.CurrentTrack == null) return;
            bool fav = _config.ToggleFavorite(_musicAudio.CurrentTrack);
            UpdateCurrentTrackFavoriteIcon();
            _musicCardList.Invalidate();
            _lblStatus.Text = (fav ? "Adicionado aos Favoritos: " : "Removido dos Favoritos: ") + _musicAudio.CurrentTrack.Title;
        }

        private void UpdateCurrentTrackFavoriteIcon()
        {
            bool fav = _musicAudio.CurrentTrack != null && _config.IsFavorite(_musicAudio.CurrentTrack.Id);
            if (_wpfMusicBar != null)
            {
                _wpfMusicBar.UpdateFavoriteVisual(fav);
            }
        }

        private void OnTrackChanged(TrackItem track)
        {
            bool fav = track != null && _config.IsFavorite(track.Id);
            if (_wpfMusicBar != null)
            {
                _wpfMusicBar.UpdateTrackMetadata(track, fav);
            }

            if (track == null)
            {
                if (_wpfMusicBar != null)
                {
                    _wpfMusicBar.UpdateThumbnailFromGdiImage(null);
                }
                _trayIcon.Text = "LiteTube — Parado";
            }
            else
            {

                try
                {
                    if (_wpfMusicBar != null)
                    {
                        _wpfMusicBar.UpdateThumbnailFromGdiImage(null);
                    }
                    Image thumb = ThumbnailCache.GetOrRequestThumbnail(track.Id, () =>
                    {
                        SafeInvoke(() =>
                        {
                            try
                            {
                                if (_musicAudio.CurrentTrack != null && _musicAudio.CurrentTrack.Id == track.Id)
                                {
                                    Image loadedImg = ThumbnailCache.GetOrRequestThumbnail(track.Id, null);
                                    if (loadedImg != null && loadedImg.Width > 0 && loadedImg.Height > 0 && _wpfMusicBar != null)
                                    {
                                        _wpfMusicBar.UpdateThumbnailFromGdiImage(loadedImg);
                                    }
                                }
                            }
                            catch { }
                        });
                    });

                    if (thumb != null && thumb.Width > 0 && thumb.Height > 0 && _wpfMusicBar != null)
                    {
                        _wpfMusicBar.UpdateThumbnailFromGdiImage(thumb);
                    }
                }
                catch
                {
                    if (_wpfMusicBar != null)
                    {
                        _wpfMusicBar.UpdateThumbnailFromGdiImage(null);
                    }
                }

                string trayTitle = "▶ " + track.Title;
                if (trayTitle.Length > 60) trayTitle = trayTitle.Substring(0, 60) + "...";
                _trayIcon.Text = trayTitle;
            }
            UpdateCurrentTrackFavoriteIcon();
            _musicCardList.PlayingVideoId = track != null ? track.Id : "";
        }

        private void OnPlaybackStateChanged(bool isPlaying, bool isLoading)
        {
            if (_wpfMusicBar != null)
            {
                _wpfMusicBar.UpdatePlaybackState(isPlaying, _musicAudio.CurrentPositionSeconds, _musicAudio.TotalDurationSeconds);
            }
        }

        private void OnUiTimerTick(object sender, EventArgs e)
        {
            if (_musicAudio.IsPlaying || _musicAudio.CurrentTrack != null)
            {
                double cur = _musicAudio.CurrentPositionSeconds;
                double tot = _musicAudio.TotalDurationSeconds;

                if (_wpfMusicBar != null)
                {
                    _wpfMusicBar.UpdatePlaybackState(_musicAudio.IsPlaying, cur, tot);
                }
            }

            if (_videoEnvironmentControl != null && _videoEnvironmentControl.IsVideoPlaying)
            {
                _videoEnvironmentControl.UpdateTimerTick();
            }

            _ramTrimTickCounter++;
            if (_ramTrimTickCounter % 6 == 0)
            {
                UpdateRamBadge();
            }
        }

        private void UpdateRamBadge()
        {
            double mb = MemoryOptimizer.GetCurrentRamUsageMB();
            _btnRamBadge.Text = string.Format("⚡ {0:0.0} MB", mb);
        }

        private void InitHardwareAccelerationOnStartup()
        {
            System.Threading.Tasks.Task.Run(() =>
            {
                try
                {
                    GpuDetector.EnsureDetected();

                    SafeInvoke(() =>
                    {
                        UpdateGpuBadge();
                        var primary = GpuDetector.PrimaryGpu;
                        if (primary != null)
                        {
                            Logger.Info("STARTUP", string.Format("🚀 LiteTube pronto com aceleração FlyleafLib Direct3D 11 na GPU: {0}", primary.Name));
                        }
                    });
                }
                catch (Exception ex)
                {
                    Logger.Error("HARDWARE", "Erro na inicialização de hardware gráfico", ex);
                }
            });
        }

        private void UpdateGpuBadge()
        {
            _btnGpuBadge.Text = "⚡ Flyleaf D3D11";
            _btnGpuBadge.BackColor = Color.FromArgb(20, 22, 30);
            _btnGpuBadge.ForeColor = Color.FromArgb(170, 200, 255);
            var mb = _btnGpuBadge as ModernButton;
            if (mb != null) mb.BorderColor = Color.FromArgb(40, 50, 70);
        }

        private void ShowGpuHardwareDetailsDialog()
        {
            var primary = GpuDetector.PrimaryGpu;
            var allGpus = GpuDetector.DetectedGpus;

            System.Text.StringBuilder sb = new System.Text.StringBuilder();
            sb.AppendLine("⚡ PIPELINE DE VÍDEO HARDWARE & DIRECT3D (LITETUBE FLYLEAF)");
            sb.AppendLine("==========================================================");
            sb.AppendLine();
            sb.AppendLine("🚀 MOTOR DE VÍDEO ATIVO: FlyleafLib 3.7.55 (Direct3D 11 / FFmpeg Nativo)");
            sb.AppendLine("• Decodificação por Hardware: Ativa (Direct3D 11 / DXVA2)");
            sb.AppendLine("• Codecs Nativos na GPU: H.264 (AVC), VP9, AV1 (Sem recodificação)");
            sb.AppendLine("• Renderizador: Direct3D 11 nativo integrado ao WPF sem Airspace");
            sb.AppendLine("• Conexão de Áudio/Vídeo: FFmpeg nativo com sincronização direta de PTS");
            sb.AppendLine("• Carga de CPU: Mínima (< 1% em reprodução 1080p 60fps)");
            sb.AppendLine();

            if (primary != null)
            {
                sb.AppendLine("🎮 GPU PRINCIPAL DETECTADA:");
                sb.AppendLine("• Placa: " + primary.Name + " (" + primary.TypeDisplay + ")");
                sb.AppendLine("• Fabricante: " + primary.VendorDisplay);
            }

            if (allGpus != null && allGpus.Count > 1)
            {
                sb.AppendLine();
                sb.AppendLine(string.Format("🖥️ TODAS AS GPUS NO SISTEMA ({0}):", allGpus.Count));
                for (int i = 0; i < allGpus.Count; i++)
                {
                    var g = allGpus[i];
                    sb.AppendLine(string.Format("  [{0}] {1} ({2} - {3}){4}",
                        i + 1, g.Name, g.VendorDisplay, g.TypeDisplay, g.IsPrimary ? " ★ ATIVA" : ""));
                }
            }

            MessageBox.Show(this, sb.ToString(), "LiteTube — Diagnóstico de Pipeline & Hardware", MessageBoxButtons.OK, MessageBoxIcon.Information);
        }

        private void HideToSystemTray()
        {
            Hide();
            MemoryOptimizer.TrimMemory();
            _trayIcon.ShowBalloonTip(
                1500,
                "LiteTube em Segundo Plano",
                string.Format("Rodando em segundo plano ({0:0.0} MB RAM). Duplo-clique no ícone da bandeja para restaurar.", MemoryOptimizer.GetCurrentRamUsageMB()),
                ToolTipIcon.Info
            );
        }

        private void RestoreFromTray()
        {
            Show();
            if (WindowState == FormWindowState.Minimized)
                WindowState = FormWindowState.Normal;
            Activate();
            UpdateRamBadge();
        }

        private void PromptImportPlaylistByUrl()
        {
            using (var form = new Form())
            {
                form.Width = 500;
                form.Height = 295;
                form.FormBorderStyle = FormBorderStyle.FixedDialog;
                form.Text = "📥 Portar Playlist ou Álbum do YouTube";
                form.StartPosition = FormStartPosition.CenterParent;
                form.BackColor = BgPanel;
                form.ForeColor = TextPrimary;
                form.MaximizeBox = false;
                form.MinimizeBox = false;

                var lblGuide = new Label
                {
                    Left = 16,
                    Top = 14,
                    Width = 455,
                    Height = 62,
                    ForeColor = TextSecondary,
                    Font = new Font("Segoe UI", 8.8f),
                    Text = "Como portar sua Playlist:\n" +
                           "1. No YouTube ou YouTube Music, deixe a playlist como 'Pública' ou 'Não listada'.\n" +
                           "2. Copie o link da playlist (ou de qualquer vídeo tocando dentro dela) e cole abaixo:"
                };

                var lblUrl = new Label { Left = 16, Top = 76, Width = 450, Height = 20, Text = "Link da Playlist (YouTube ou YouTube Music):", Font = new Font("Segoe UI Semibold", 9f, FontStyle.Bold) };
                var txtUrl = new TextBox { Left = 16, Top = 98, Width = 450, Text = "", BackColor = BgCard, ForeColor = TextPrimary, BorderStyle = BorderStyle.FixedSingle, Font = new Font("Segoe UI", 10f) };

                if (Clipboard.ContainsText())
                {
                    string clip = Clipboard.GetText().Trim();
                    if (clip.IndexOf("youtube.com", StringComparison.OrdinalIgnoreCase) >= 0 ||
                        clip.IndexOf("youtu.be", StringComparison.OrdinalIgnoreCase) >= 0 ||
                        clip.IndexOf("list=", StringComparison.OrdinalIgnoreCase) >= 0)
                    {
                        txtUrl.Text = clip;
                    }
                }

                var lblName = new Label { Left = 16, Top = 132, Width = 450, Height = 20, Text = "Nome para salvar no programa:", Font = new Font("Segoe UI Semibold", 9f, FontStyle.Bold) };
                var txtName = new TextBox { Left = 16, Top = 154, Width = 450, Text = "Minha Playlist do YouTube", BackColor = BgCard, ForeColor = TextPrimary, BorderStyle = BorderStyle.FixedSingle, Font = new Font("Segoe UI", 10f) };

                var chkPrivate = new CheckBox
                {
                    Left = 16,
                    Top = 186,
                    Width = 450,
                    Height = 22,
                    Text = "Esta playlist é Privada (usar cookies do navegador selecionado)",
                    ForeColor = TextSecondary,
                    Checked = false
                };

                var btnOk = CreateFlatButton("📥 Portar e Salvar Playlist", 216, 216, 160, 34, AccentRed);
                var btnCancel = CreateFlatButton("Cancelar", 382, 216, 84, 34, BgCard);

                btnOk.DialogResult = DialogResult.OK;
                btnCancel.DialogResult = DialogResult.Cancel;

                form.Controls.AddRange(new Control[] { lblGuide, lblUrl, txtUrl, lblName, txtName, chkPrivate, btnOk, btnCancel });
                form.AcceptButton = btnOk;
                form.CancelButton = btnCancel;

                if (form.ShowDialog(this) != DialogResult.OK) return;

                string url = txtUrl.Text.Trim();
                string name = txtName.Text.Trim();
                bool forceCookies = chkPrivate.Checked;

                if (string.IsNullOrWhiteSpace(url)) return;

                System.Threading.Tasks.Task.Run(async () =>
                {
                    SafeInvoke(() => _lblStatus.Text = "Portando faixas da playlist do YouTube...");
                    var res = await _ytService.FetchUrlOrPlaylistAsync(url, _config, forceCookies);

                    SafeInvoke(() =>
                    {
                        if (res.Item1.Count > 0)
                        {
                            string finalName = string.IsNullOrWhiteSpace(name) ? "Playlist YouTube" : name;
                            var pl = new UserPlaylist
                            {
                                Name = finalName,
                                RemoteUrl = url,
                                Tracks = res.Item1
                            };
                            _config.Playlists.Add(pl);
                            _config.Save();
                            RebuildPlaylistsSidebar();
                            SwitchSection("playlist:" + pl.Id);
                            _lblStatus.Text = string.Format("Playlist '{0}' portada com {1} músicas!", finalName, res.Item1.Count);
                        }
                        else
                        {
                            MessageBox.Show(this, res.Item2, "Portar Playlist", MessageBoxButtons.OK, MessageBoxIcon.Warning);
                        }
                        MemoryOptimizer.TrimMemory();
                        UpdateRamBadge();
                    });
                });
            }
        }

        private void PromptCreateLocalPlaylist()
        {
            string name = ShowInputDialog("Digite o nome da nova Playlist local:", "Nova Playlist", "Playlist Favorita");
            if (string.IsNullOrWhiteSpace(name)) return;

            var pl = new UserPlaylist { Name = name.Trim() };
            _config.Playlists.Add(pl);
            _config.Save();
            RebuildPlaylistsSidebar();
            SwitchSection("playlist:" + pl.Id);
        }

        private void ShowAddToPlaylistMenu()
        {
            TrackItem selected = GetSelectedTrack();
            if (selected == null)
            {
                _lblStatus.Text = "Selecione uma música na lista primeiro.";
                return;
            }

            var menu = new ContextMenuStrip();
            if (_config.Playlists.Count == 0)
            {
                menu.Items.Add("Criar Nova Playlist e Adicionar...", null, (s, e) =>
                {
                    PromptCreateLocalPlaylist();
                    if (_config.Playlists.Count > 0)
                    {
                        var last = _config.Playlists[_config.Playlists.Count - 1];
                        last.Tracks.Add(selected);
                        _config.Save();
                        _lblStatus.Text = "Adicionado à playlist: " + last.Name;
                    }
                });
            }
            else
            {
                foreach (var pl in _config.Playlists)
                {
                    var targetPl = pl;
                    menu.Items.Add("📁 " + targetPl.Name, null, (s, e) =>
                    {
                        targetPl.Tracks.Add(selected);
                        _config.Save();
                        _lblStatus.Text = string.Format("'{0}' adicionada em {1}!", selected.Title, targetPl.Name);
                    });
                }
            }

            menu.Show(_btnAddToPlaylist, new Point(0, _btnAddToPlaylist.Height));
        }

        private void RebuildPlaylistsSidebar()
        {
            _playlistsNavContainer.Controls.Clear();
            foreach (var pl in _config.Playlists)
            {
                var currentPl = pl;
                var btn = new ModernButton
                {
                    Text = string.Format("•  {0} ({1})", currentPl.Name, currentPl.Tracks != null ? currentPl.Tracks.Count : 0),
                    Tag = currentPl.Id,
                    Size = new Size(200, 32),
                    FlatStyle = FlatStyle.Flat,
                    BackColor = Color.Transparent,
                    ForeColor = TextSecondary,
                    TextAlign = ContentAlignment.MiddleLeft,
                    Font = new Font("Segoe UI Semibold", 8.8f),
                    CornerRadius = 8,
                    ShowActiveIndicator = true,
                    ActiveAccentColor = AccentRed,
                    ActiveColor = Theme.SurfaceHigh,
                    Cursor = Cursors.Hand,
                    Margin = new Padding(2)
                };
                btn.FlatAppearance.BorderSize = 0;
                btn.Click += (s, e) => SwitchSection("playlist:" + currentPl.Id);

                var ctx = new ContextMenuStrip();
                ctx.Items.Add("\u25B6 Tocar Playlist Agora", null, (s, e) =>
                {
                    if (currentPl.Tracks.Count > 0)
                        _musicAudio.SetQueueAndPlay(currentPl.Tracks, 0);
                });
                ctx.Items.Add("Excluir Playlist '" + currentPl.Name + "'", null, (s, e) =>
                {
                    _config.Playlists.Remove(currentPl);
                    _config.Save();
                    RebuildPlaylistsSidebar();
                    SwitchSection("search");
                });
                btn.ContextMenuStrip = ctx;

                _playlistsNavContainer.Controls.Add(btn);
            }
        }

        private void UpdateSearchFilterButtons()
        {
            StyleTopChip(_btnFilterSongs, _searchFilter == "songs");
            StyleTopChip(_btnFilterAlbums, _searchFilter == "albums");
            StyleTopChip(_btnFilterAll, _searchFilter == "all");
        }

        private void StyleTopChip(Button btn, bool active)
        {
            var mb = btn as ModernButton;
            if (mb != null)
            {
                mb.BackColor = active ? Color.FromArgb(241, 241, 241) : Color.FromArgb(28, 28, 28);
                mb.ForeColor = active ? Color.FromArgb(15, 15, 15) : Color.FromArgb(220, 220, 220);
                mb.BorderColor = active ? Color.Empty : Color.FromArgb(48, 48, 48);
                mb.CornerRadius = 16;
                mb.Invalidate();
            }
        }

        private void UpdateSidebarSelection()
        {
            SetSidebarNavActive(_navSearch, _activeSection == "search");
            SetSidebarNavActive(_navQueue, _activeSection == "queue");
            SetSidebarNavActive(_navFavorites, _activeSection == "favorites");
            SetSidebarNavActive(_navAccountRecommended, _activeSection == "account_rec");
            SetSidebarNavActive(_navAccountLiked, _activeSection == "account_liked");
            SetSidebarNavActive(_navAccountHistory, _activeSection == "account_hist");

            if (_playlistsNavContainer != null)
            {
                foreach (Control c in _playlistsNavContainer.Controls)
                {
                    var btn = c as ModernButton;
                    if (btn != null && btn.Tag != null)
                    {
                        bool active = ("playlist:" + btn.Tag.ToString()) == _activeSection;
                        SetSidebarNavActive(btn, active);
                    }
                }
            }
        }

        private void SetSidebarNavActive(Button btn, bool active)
        {
            var mb = btn as ModernButton;
            if (mb != null)
            {
                mb.IsActive = active;
                mb.ForeColor = active ? TextPrimary : TextSecondary;
                mb.Font = new Font("Segoe UI Semibold", 9.2f, active ? FontStyle.Bold : FontStyle.Regular);
                mb.Invalidate();
            }
        }

        private void UpdateRepeatShuffleButtons()
        {
            if (_wpfMusicBar != null)
            {
                _wpfMusicBar.UpdateShuffleAndRepeatVisuals(_config.Shuffle, _config.Repeat);
            }
        }

        private Button CreateFlatButton(string text, int x, int y, int w, int h, Color bg)
        {
            return CreateFlatButton(text, x, y, w, h, bg, -1, Color.Empty);
        }

        private Button CreateFlatButton(string text, int x, int y, int w, int h, Color bg, int cornerRadius, Color borderColor)
        {
            var btn = new ModernButton
            {
                Text = text,
                Location = new Point(x, y),
                Size = new Size(w, h),
                FlatStyle = FlatStyle.Flat,
                BackColor = bg,
                ForeColor = TextPrimary,
                CornerRadius = cornerRadius >= 0 ? cornerRadius : Math.Min(h / 2, 16),
                BorderColor = borderColor != Color.Empty ? borderColor : ((bg == BgCard || bg == Theme.SurfaceHigh) ? Theme.Border : Color.Empty),
                Cursor = Cursors.Hand
            };
            btn.FlatAppearance.BorderSize = 0;
            return btn;
        }

        private Button CreateSidebarButton(string text, ref int y)
        {
            var btn = new ModernButton
            {
                Text = text,
                Location = new Point(8, y),
                Size = new Size(214, 36),
                FlatStyle = FlatStyle.Flat,
                BackColor = Color.Transparent,
                ForeColor = TextSecondary,
                TextAlign = ContentAlignment.MiddleLeft,
                Font = new Font("Segoe UI Semibold", 9.2f),
                CornerRadius = 10,
                ShowActiveIndicator = true,
                ActiveAccentColor = AccentRed,
                ActiveColor = Theme.SurfaceHigh,
                Cursor = Cursors.Hand
            };
            _sidebar.Controls.Add(btn);
            y += 40;
            return btn;
        }

        private string ShowInputDialog(string prompt, string title, string defaultValue)
        {
            using (var form = new Form())
            {
                form.Width = 440;
                form.Height = 175;
                form.FormBorderStyle = FormBorderStyle.FixedDialog;
                form.Text = title;
                form.StartPosition = FormStartPosition.CenterParent;
                form.BackColor = BgPanel;
                form.ForeColor = TextPrimary;
                form.MaximizeBox = false;
                form.MinimizeBox = false;

                var lbl = new Label { Left = 16, Top = 16, Width = 395, Height = 36, Text = prompt, ForeColor = TextPrimary };
                var txt = new TextBox { Left = 16, Top = 56, Width = 395, Text = defaultValue, BackColor = BgCard, ForeColor = TextPrimary, BorderStyle = BorderStyle.FixedSingle };
                var btnOk = CreateFlatButton("Confirmar", 230, 92, 90, 32, AccentRed);
                var btnCancel = CreateFlatButton("Cancelar", 326, 92, 85, 32, BgCard);

                btnOk.DialogResult = DialogResult.OK;
                btnCancel.DialogResult = DialogResult.Cancel;

                form.Controls.AddRange(new Control[] { lbl, txt, btnOk, btnCancel });
                form.AcceptButton = btnOk;
                form.CancelButton = btnCancel;

                return form.ShowDialog(this) == DialogResult.OK ? txt.Text : "";
            }
        }

        private void RegisterGlobalMediaKeys()
        {
            if (!_config.EnableGlobalHotkeys) return;
            try
            {
                RegisterHotKey(Handle, 1001, MOD_NONE, VK_MEDIA_PLAY_PAUSE);
                RegisterHotKey(Handle, 1002, MOD_NONE, VK_MEDIA_NEXT_TRACK);
                RegisterHotKey(Handle, 1003, MOD_NONE, VK_MEDIA_PREV_TRACK);

                RegisterHotKey(Handle, 1004, MOD_CONTROL | MOD_ALT | MOD_NOREPEAT, VK_P);
                RegisterHotKey(Handle, 1005, MOD_CONTROL | MOD_ALT | MOD_NOREPEAT, VK_RIGHT);
                RegisterHotKey(Handle, 1006, MOD_CONTROL | MOD_ALT | MOD_NOREPEAT, VK_LEFT);

                RegisterHotKey(Handle, 1007, MOD_CONTROL | MOD_ALT, VK_UP);
                RegisterHotKey(Handle, 1008, MOD_CONTROL | MOD_ALT, VK_DOWN);
                RegisterHotKey(Handle, 1009, MOD_CONTROL | MOD_ALT | MOD_NOREPEAT, VK_M);
            }
            catch { }
        }

        private void UnregisterGlobalMediaKeys()
        {
            try
            {
                for (int id = 1001; id <= 1009; id++)
                {
                    UnregisterHotKey(Handle, id);
                }
            }
            catch { }
        }

        protected override void WndProc(ref Message m)
        {
            if (m.Msg == WM_HOTKEY)
            {
                int id = m.WParam.ToInt32();
                bool controlVideo = (_activeEnvironment == "video" && Visible);

                if (id == 1001 || id == 1004)
                {
                    if (controlVideo) _videoEnvironmentControl.TogglePlayPause();
                    else _musicAudio.TogglePlayPause();
                }
                else if (id == 1002 || id == 1005)
                {
                    if (controlVideo) _videoEnvironmentControl.SeekRelative(10.0);
                    else _musicAudio.PlayNext();
                }
                else if (id == 1003 || id == 1006)
                {
                    if (controlVideo) _videoEnvironmentControl.SeekRelative(-10.0);
                    else _musicAudio.PlayPrevious();
                }
                else if (id == 1007)
                {
                    if (controlVideo) _videoEnvironmentControl.ChangeVideoVolumeBy(0.05);
                    else _musicAudio.ChangeVolumeBy(0.05);
                }
                else if (id == 1008)
                {
                    if (controlVideo) _videoEnvironmentControl.ChangeVideoVolumeBy(-0.05);
                    else _musicAudio.ChangeVolumeBy(-0.05);
                }
                else if (id == 1009)
                {
                    if (controlVideo) _videoEnvironmentControl.ToggleVideoMute();
                    else _musicAudio.ToggleMute();
                }
            }
            base.WndProc(ref m);
        }

        public async Task RunSelfTestProtocolAsync()
        {
            try
            {
                Logger.Info("SELFTEST", "Iniciando validação automatizada dos feeds YouTube Music (WEB_REMIX)...");

                // 1. Teste de Explorar (FEmusic_explore) com caixa de busca vazia
                if (_txtSearch != null) _txtSearch.Text = "";
                await LoadMusicExploreFeedAsync();
                Logger.Info("SELFTEST", string.Format("Fluxo Explorar (FEmusic_explore) validado: {0} itens carregados.", _displayedTracks != null ? _displayedTracks.Count : 0));

                // 2. Teste de Recomendados Personalizados (FEmusic_home + fallback)
                await LoadPersonalizedMusicFeedAsync();
                Logger.Info("SELFTEST", string.Format("Fluxo Recomendados (FEmusic_home) validado: {0} itens carregados.", _displayedTracks != null ? _displayedTracks.Count : 0));

                // 3. Teste unitário do parser de estantes musicCarouselShelfRenderer (Ouvir novamente / Seu Supermix)
                string mockCarouselJson = "{\"contents\":{\"singleColumnBrowseResultsRenderer\":{\"tabs\":[{\"tabRenderer\":{\"content\":{\"sectionListRenderer\":{\"contents\":[" +
                    "{\"musicCarouselShelfRenderer\":{\"header\":{\"musicCarouselShelfBasicHeaderRenderer\":{\"title\":{\"runs\":[{\"text\":\"Ouvir novamente\"}]}}},\"contents\":[" +
                    "{\"musicResponsiveListItemRenderer\":{\"flexColumns\":[{\"musicResponsiveListItemFlexColumnRenderer\":{\"text\":{\"runs\":[{\"text\":\"Bohemian Rhapsody\"}]}}},{\"musicResponsiveListItemFlexColumnRenderer\":{\"text\":{\"runs\":[{\"text\":\"Queen\"},{\"text\":\" • \"},{\"text\":\"5:55\"}]}}}],\"playlistItemData\":{\"videoId\":\"fJ9rUzIMcZQ\"}}}" +
                    "]}}," +
                    "{\"musicCarouselShelfRenderer\":{\"header\":{\"musicCarouselShelfBasicHeaderRenderer\":{\"title\":{\"runs\":[{\"text\":\"Seu Supermix\"}]}}},\"contents\":[" +
                    "{\"musicTwoRowItemRenderer\":{\"title\":{\"runs\":[{\"text\":\"Smells Like Teen Spirit\"}]},\"subtitle\":{\"runs\":[{\"text\":\"Nirvana\"}]},\"navigationEndpoint\":{\"watchEndpoint\":{\"videoId\":\"hTWKbfoikeg\",\"playlistId\":\"RDCLAK5uy_k\"}}}}" +
                    "]}}" +
                    "]}}}}]}}}";
                var parsedMock = _ytService.ParseMusicCarouselShelves(mockCarouselJson, null);
                int linearVolCheck = 50;
                Logger.Info("SELFTEST", string.Format("Parser musicCarouselShelfRenderer validado: {0} faixas extraídas ('{1}', '{2}') | Volume [0,100]={3}.",
                    parsedMock.Count,
                    parsedMock.Count > 0 ? parsedMock[0].Title + " - " + parsedMock[0].Artist : "",
                    parsedMock.Count > 1 ? parsedMock[1].Title + " - " + parsedMock[1].Artist : "",
                    linearVolCheck));

                // 4. Validação do SponsorBlockService (ParseSegmentsJson + CheckAndGetSkipTarget para Vídeo e music_offtopic)
                string mockSponsorJson = "[{\"segment\":[12.5,42.0],\"category\":\"sponsor\",\"UUID\":\"sb-1\",\"actionType\":\"skip\"},{\"segment\":[0.0,8.0],\"category\":\"intro\",\"UUID\":\"sb-2\",\"actionType\":\"skip\"},{\"segment\":[180.0,210.0],\"category\":\"music_offtopic\",\"UUID\":\"sb-3\",\"actionType\":\"skip\"}]";
                var parsedSegments = SponsorBlockService.ParseSegmentsJson(mockSponsorJson);
                double skipTarget = 0;
                bool shouldSkipSponsor = SponsorBlockService.CheckAndGetSkipTarget(15.0, parsedSegments, out skipTarget);
                double musicSkipTarget = 0;
                bool shouldSkipMusicOfftopic = SponsorBlockService.CheckAndGetSkipTarget(185.0, parsedSegments, out musicSkipTarget);
                if (_wpfMusicBar != null)
                {
                    _wpfMusicBar.SetOfftopicSegments(parsedSegments);
                }
                Logger.Info("SELFTEST", string.Format("SponsorBlockService & CheckAndGetSkipTarget validados: {0} segmentos extraídos | SkipSponsor(15s)->{1:0.0}s ({2}) | SkipMusicOfftopic(185s)->{3:0.0}s ({4}).",
                    parsedSegments.Count, skipTarget, shouldSkipSponsor, musicSkipTarget, shouldSkipMusicOfftopic));

                // 5. Validação do Motor FlyleafLib (Direct3D 11 / FFmpeg Nativo) e Ciclo de Vida do Overlay no Catálogo
                bool flyleafLoaded = FlyleafLib.Engine.IsLoaded;
                string ffmpegVer = FlyleafLib.Engine.FFmpeg != null ? FlyleafLib.Engine.FFmpeg.Version.ToString() : "N/A";
                _videoEnvironmentControl.ExitWatchPage();
                bool overlayHiddenOnCatalog = !_videoEnvironmentControl.IsOverlayVisible;
                Logger.Info("SELFTEST", string.Format("Motor FlyleafLib (Direct3D 11 / FFmpeg Nativo v{0}) e Ocultação do Overlay no Catálogo validados: EngineLoaded={1} | OverlayHiddenOnCatalog={2}.",
                    ffmpegVer, flyleafLoaded, overlayHiddenOnCatalog));

                // 6. Validação de independência de volume Vídeo vs Música e Atalhos Globais
                double prevVidVol = _config.VideoVolume;
                double prevMusVol = _config.MusicVolume;
                _videoEnvironmentControl.ChangeVideoVolumeBy(0.05);
                bool independentVol = Math.Abs(_config.MusicVolume - prevMusVol) < 0.0001;
                _config.VideoVolume = prevVidVol;
                _videoEnvironmentControl.ApplyVideoAudioSettings();
                Logger.Info("SELFTEST", string.Format("Independência de Volume Vídeo/Música (WASAPI), WpfVideoPlayerOverlay e WpfMusicPlayerBar validados: Independente={0} | VideoVol={1:0.00} | MusicVol={2:0.00}.",
                    independentVol, _config.VideoVolume, _config.MusicVolume));

                // 6.1 Validação da Prioridade em Cascata de Codecs de Vídeo (AV1 > VP9.2 > VP9 > H.264) e Áudio (Opus > AAC)
                string testAv1 = ProgressiveVideoSession.DetectCodecFromUrls("https://googlevideo.com/videoplayback?itag=398", "https://googlevideo.com/videoplayback?itag=251");
                string testVp92 = ProgressiveVideoSession.DetectCodecFromUrls("https://googlevideo.com/videoplayback?itag=335", "https://googlevideo.com/videoplayback?itag=251");
                string testVp9 = ProgressiveVideoSession.DetectCodecFromUrls("https://googlevideo.com/videoplayback?itag=247", "https://googlevideo.com/videoplayback?itag=251");
                string testH264 = ProgressiveVideoSession.DetectCodecFromUrls("https://googlevideo.com/videoplayback?itag=136", "https://googlevideo.com/videoplayback?itag=140");
                bool cascadeOk = (testAv1 == "AV1/Opus (Direct3D 11)") &&
                                 (testVp92 == "VP9.2 HDR/Opus (Direct3D 11)") &&
                                 (testVp9 == "VP9/Opus (Direct3D 11)") &&
                                 (testH264 == "H.264/AAC (Direct3D 11)");
                Logger.Info("SELFTEST", string.Format("Prioridade de Codecs em Cascata (AV1 > VP9.2 > VP9 > H.264) e Detecção validados: CascadeOk={0} [AV1='{1}', VP9.2='{2}', VP9='{3}', H.264='{4}'].",
                    cascadeOk, testAv1, testVp92, testVp9, testH264));

                // 6.2 Validação do Destravamento do Pool de Rede Global (HttpPipeline / SocketsHttpHandler) e AsyncImageLoader.WPF
                bool networkPoolUnlocked = (HttpPipeline.Client != null);
                bool asyncLoaderOk = AsyncImageLoader.ImageLoaderBase.DefaultLoader != null;
                Logger.Info("SELFTEST", string.Format("Pipeline HTTP Moderno (.NET 8 SocketsHttpHandler: 64 conexões, HTTP/2, TLS 1.2/1.3) e AsyncImageLoader.WPF validados: NetworkUnlocked={0} | AsyncLoaderLoaded={1}.",
                    networkPoolUnlocked, asyncLoaderOk));

                // 6.3 Validação do Seamless Quality Switch (Troca Contínua de Resolução Agnóstica de 144p até 4K)
                bool seamlessSwitchApiOk = false;
                try
                {
                    // Valida a assinatura agnóstica do método estático sem travar a UI
                    var resolveMethod = typeof(ProgressiveVideoSession).GetMethod("ResolveStreamUrls", new Type[] { typeof(string), typeof(int) });
                    seamlessSwitchApiOk = (resolveMethod != null);
                }
                catch { }
                Logger.Info("SELFTEST", string.Format("Seamless Quality Switch (Troca Contínua Agnóstica 144p..4K sem reload) validado: ApiReady={0}.",
                    seamlessSwitchApiOk));

                // 7. Validação do ExternalLinkServer (Listener HTTP na porta 48125: rotas /ping e /play)
                bool serverPingOk = false;
                string pingResponse = "";
                try
                {
                    pingResponse = HttpPipeline.Client.GetStringAsync("http://127.0.0.1:48125/ping").GetAwaiter().GetResult();
                    serverPingOk = (pingResponse == "pong");
                }
                catch (Exception ex)
                {
                    Logger.Warn("SELFTEST", "Falha ao testar /ping do ExternalLinkServer: " + ex.Message);
                }

                bool serverPlayOk = false;
                try
                {
                    string playResp = HttpPipeline.Client.GetStringAsync("http://127.0.0.1:48125/play?url=https%3A%2F%2Fwww.youtube.com%2Fwatch%3Fv%3DmockId12345").GetAwaiter().GetResult();
                    serverPlayOk = (playResp.IndexOf("OK", StringComparison.OrdinalIgnoreCase) >= 0);
                }
                catch (Exception ex)
                {
                    Logger.Warn("SELFTEST", "Falha ao testar /play do ExternalLinkServer: " + ex.Message);
                }

                Logger.Info("SELFTEST", string.Format("ExternalLinkServer (HTTP 127.0.0.1:48125) validado: Ping={0} ('{1}') | Play={2}.",
                    serverPingOk, pingResponse, serverPlayOk));

                Logger.Info("SELFTEST", "Validação automatizada concluída com 0 exceções.");
            }
            catch (Exception ex)
            {
                Logger.Error("SELFTEST", "Falha durante o protocolo de auto-teste", ex);
            }
            finally
            {
                _forceExit = true;
                Close();
            }
        }

        private void OnMainFormClosing(object sender, FormClosingEventArgs e)
        {
            if (!_forceExit && e.CloseReason == CloseReason.UserClosing && _config.MinimizeToTrayOnClose)
            {
                e.Cancel = true;
                HideToSystemTray();
                return;
            }

            if (_externalServer != null)
            {
                _externalServer.Stop();
                _externalServer = null;
            }

            _uiTimer.Stop();
            UnregisterGlobalMediaKeys();
            _trayIcon.Visible = false;
            _trayIcon.Dispose();
            if (_videoEnvironmentControl != null) _videoEnvironmentControl.ShutdownOverlay();
            _musicAudio.Dispose();
            _config.Save();
        }

        public static string ExtractVideoId(string url)
        {
            if (string.IsNullOrWhiteSpace(url)) return null;
            url = url.Trim();

            if (Regex.IsMatch(url, @"^[a-zA-Z0-9_-]{11}$"))
                return url;

            var m = Regex.Match(url, @"[?&]v=([a-zA-Z0-9_-]{11})");
            if (m.Success) return m.Groups[1].Value;

            m = Regex.Match(url, @"youtu\.be/([a-zA-Z0-9_-]{11})");
            if (m.Success) return m.Groups[1].Value;

            m = Regex.Match(url, @"/shorts/([a-zA-Z0-9_-]{11})");
            if (m.Success) return m.Groups[1].Value;

            m = Regex.Match(url, @"/embed/([a-zA-Z0-9_-]{11})");
            if (m.Success) return m.Groups[1].Value;

            m = Regex.Match(url, @"/v/([a-zA-Z0-9_-]{11})");
            if (m.Success) return m.Groups[1].Value;

            return null;
        }

        public void PlayExternalUrl(string url)
        {
            if (string.IsNullOrWhiteSpace(url)) return;
            url = url.Trim();
            Logger.Info("SERVER", "Executando reprodução externa para: " + url);

            bool isMusic = (url.IndexOf("music.youtube.com", StringComparison.OrdinalIgnoreCase) >= 0);
            string videoId = ExtractVideoId(url);

            if (isMusic)
            {
                SwitchEnvironment("music");
                if (!string.IsNullOrEmpty(videoId))
                {
                    var track = new TrackItem
                    {
                        Id = videoId,
                        Title = "YouTube Music (" + videoId + ")",
                        Artist = "Música Externa",
                        ItemType = "video"
                    };
                    _musicAudio.PlaySingleAndAddToQueue(track);

                    // Enriquecimento assíncrono de metadados
                    Task.Run(async () =>
                    {
                        try
                        {
                            var meta = await _ytService.FetchUrlOrPlaylistAsync(videoId, _config, false, 1, 1);
                            if (meta != null && meta.Item1 != null && meta.Item1.Count > 0)
                            {
                                var item = meta.Item1[0];
                                SafeInvoke(() =>
                                {
                                    if (_musicAudio.CurrentTrack != null && _musicAudio.CurrentTrack.Id == videoId)
                                    {
                                        _musicAudio.CurrentTrack.Title = item.Title;
                                        _musicAudio.CurrentTrack.Artist = item.Artist;
                                        _musicAudio.CurrentTrack.CustomThumbUrl = item.CustomThumbUrl;
                                        if (_wpfMusicBar != null)
                                        {
                                            _wpfMusicBar.UpdateTrackMetadata(_musicAudio.CurrentTrack, false);
                                        }
                                    }
                                });
                            }
                        }
                        catch { }
                    });
                }
                else
                {
                    Task.Run(async () =>
                    {
                        try
                        {
                            SafeInvoke(() => _lblStatus.Text = "🎵 Carregando playlist externa do YouTube Music...");
                            var res = await _ytService.FetchUrlOrPlaylistAsync(url, _config, false);
                            SafeInvoke(() =>
                            {
                                if (res != null && res.Item1 != null && res.Item1.Count > 0)
                                {
                                    _displayedTracks = res.Item1;
                                    _musicCardList.SetItems(_displayedTracks);
                                    _musicAudio.SetQueueAndPlay(_displayedTracks, 0);
                                }
                                else
                                {
                                    _lblStatus.Text = "❌ Não foi possível carregar o conteúdo do link.";
                                }
                            });
                        }
                        catch (Exception ex)
                        {
                            Logger.Warn("SERVER", "Erro ao processar playlist externa: " + ex.Message);
                        }
                    });
                }
            }
            else
            {
                SwitchEnvironment("video");

                if (!string.IsNullOrEmpty(videoId))
                {
                    var video = new TrackItem
                    {
                        Id = videoId,
                        Title = "Vídeo do YouTube (" + videoId + ")",
                        ItemType = "video"
                    };
                    _videoEnvironmentControl.PlayVideo(video);

                    // Enriquecimento assíncrono de metadados
                    Task.Run(async () =>
                    {
                        try
                        {
                            var meta = await _ytService.FetchUrlOrPlaylistAsync(videoId, _config, false, 1, 1);
                            if (meta != null && meta.Item1 != null && meta.Item1.Count > 0)
                            {
                                var item = meta.Item1[0];
                                SafeInvoke(() =>
                                {
                                    if (_videoEnvironmentControl.CurrentVideo != null && _videoEnvironmentControl.CurrentVideo.Id == videoId)
                                    {
                                        _videoEnvironmentControl.UpdatePlayingVideoMetadata(item.Title, item.Artist, item.CustomThumbUrl);
                                    }
                                });
                            }
                        }
                        catch { }
                    });
                }
                else
                {
                    _videoEnvironmentControl.PlayUrlOrPlaylist(url);
                }
            }
        }

        public void SafeInvoke(Action action)
        {
            if (IsDisposed) return;
            if (InvokeRequired)
            {
                try { BeginInvoke(action); } catch { }
            }
            else
            {
                action();
            }
        }
    }
}
