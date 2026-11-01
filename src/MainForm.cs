using System;
using System.IO;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Runtime.InteropServices;
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
        private readonly Color BgHover = Color.FromArgb(36, 36, 36);
        private readonly Color BgYTMusicPlayerBar = Color.FromArgb(14, 14, 14);
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
        private bool _isMiniPlayer = false;
        private bool _isDraggingSeek = false;
        private bool _isDraggingVolumeBar = false;
        private bool _isHoveringMusicVolBar = false;
        private bool _forceExit = false;
        private Size _normalSize = new Size(1220, 750);
        private Point _normalLocation;

        // Main Layout Containers
        private Panel _topBar;
        private Panel _musicEnvironmentContainer;
        private VideoEnvironmentControl _videoEnvironmentControl;
        private Panel _miniPlayerPanel;

        // Music Environment Sub-Panels
        private Panel _sidebar;
        private Panel _centerPanel;
        private Panel _bottomBar;

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
        private Button _btnMiniMode;
        private Button _btnHideToTray;

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

        // Bottom Player Controls (YouTube Music Style Bar)
        private PictureBox _musicCustomSeekBar;
        private PictureBox _nowPlayingThumb;
        private Label _lblNowTitle;
        private Label _lblNowArtist;
        private Button _btnNowFavorite;
        private Button _btnShuffle;
        private Button _btnPrev;
        private Button _btnPlayPause;
        private Button _btnNext;
        private Button _btnRepeat;
        private Label _lblCurrentTime;
        private Label _lblTotalTime;

        // Bottom Music Audio Regulator Controls
        private Button _btnMute;
        private PictureBox _volCustomBar;
        private Button _btnOpenAudioRegulator;

        // Mini-Player Controls
        private Label _lblMiniTitle;
        private Label _lblMiniArtist;
        private Label _lblMiniTime;
        private Label _lblMiniRam;
        private Button _btnMiniPrev;
        private Button _btnMiniPlayPause;
        private Button _btnMiniNext;
        private Button _btnMiniFav;
        private Button _btnMiniMute;
        private Button _btnMiniVolDown;
        private Label _lblMiniVol;
        private Button _btnMiniVolUp;
        private Button _btnMiniAudioReg;
        private Button _btnMiniExpand;
        private Button _btnMiniTray;

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
            BuildMiniPlayerPanel();
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
                _lblStatus.Text = "Bem-vindo ao LiteTube! Alterne entre YT Music (áudio baixo) e YouTube Vídeos (streaming progressivo).";
            }

            CheckYtDlpOnStartup();
            InitHardwareAccelerationOnStartup();
            MemoryOptimizer.TrimMemory();
            Logger.Info("MAIN", "MainForm inicializado com sucesso.");
        }

        private void InitializeWindow()
        {
            Text = "LiteTube — YouTube & YT Music";
            Size = _normalSize;
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
                Width = 420,
                BackColor = Color.FromArgb(12, 12, 12)
            };

            _btnTopAudioRegulator = CreateFlatButton("Áudio", 0, 11, 70, 32, Color.FromArgb(24, 24, 24), 16, Color.FromArgb(44, 44, 44));
            _btnTopAudioRegulator.Font = new Font("Segoe UI Semibold", 8.5f, FontStyle.Bold);
            _btnTopAudioRegulator.ForeColor = Color.FromArgb(255, 130, 150);

            _btnRamBadge = CreateFlatButton("RAM", 76, 11, 80, 32, Color.FromArgb(16, 28, 20), 16, Color.FromArgb(30, 60, 40));
            _btnRamBadge.ForeColor = AccentGreen;
            _btnRamBadge.Font = new Font("Segoe UI Semibold", 8.2f, FontStyle.Bold);

            _btnGpuBadge = CreateFlatButton("GPU...", 162, 11, 112, 32, Color.FromArgb(20, 22, 30), 16, Color.FromArgb(40, 50, 70));
            _btnGpuBadge.ForeColor = Color.FromArgb(170, 200, 255);
            _btnGpuBadge.Font = new Font("Segoe UI Semibold", 8.0f, FontStyle.Bold);

            _btnMiniMode = CreateFlatButton("Mini", 280, 11, 54, 32, Color.FromArgb(24, 24, 24), 16, Color.FromArgb(44, 44, 44));
            _btnMiniMode.ForeColor = Color.FromArgb(210, 210, 210);
            _btnHideToTray = CreateFlatButton("Bandeja", 340, 11, 72, 32, Color.FromArgb(24, 24, 24), 16, Color.FromArgb(44, 44, 44));
            _btnHideToTray.ForeColor = Color.FromArgb(210, 210, 210);

            topRightPanel.Controls.AddRange(new Control[]
            {
                _btnTopAudioRegulator, _btnRamBadge, _btnGpuBadge, _btnMiniMode, _btnHideToTray
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
            int rightBoundary = _topBar.Width - 430; // 420px is topRightPanel.Width + 10px margin
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
            // 1. BOTTOM YOUTUBE MUSIC PLAYER BAR (#212121 + Top-Edge Red SeekBar)
            // ====================================================================
            _bottomBar = new Panel
            {
                Dock = DockStyle.Bottom,
                Height = 86,
                BackColor = BgYTMusicPlayerBar
            };

            // Full-width top-edge seek bar just like YouTube Music!
            _musicCustomSeekBar = new PictureBox
            {
                Dock = DockStyle.Top,
                Height = 14,
                BackColor = BgYTMusicPlayerBar,
                Cursor = Cursors.Hand
            };

            var bottomContentRow = new Panel
            {
                Dock = DockStyle.Fill,
                BackColor = BgYTMusicPlayerBar,
                Padding = new Padding(12, 2, 12, 4)
            };

            // Left: Transport Controls + Time (YouTube Music puts Play/Next/Prev + Time on the left or center)
            var nowPlayingPanel = new Panel
            {
                Dock = DockStyle.Left,
                Width = 340,
                BackColor = BgYTMusicPlayerBar
            };

            _nowPlayingThumb = new PictureBox
            {
                Location = new Point(4, 8),
                Size = new Size(52, 52),
                BackColor = BgCard,
                SizeMode = PictureBoxSizeMode.Normal
            };
            _nowPlayingThumb.Paint += OnPaintNowPlayingThumb;

            _lblNowTitle = new Label
            {
                Text = "Nenhuma música tocando",
                Font = new Font("Segoe UI Semibold", 10f, FontStyle.Bold),
                ForeColor = TextPrimary,
                Location = new Point(66, 10),
                Size = new Size(224, 22),
                AutoEllipsis = true
            };

            _lblNowArtist = new Label
            {
                Text = "YouTube Music • Áudio Independente",
                Font = new Font("Segoe UI", 8.8f, FontStyle.Regular),
                ForeColor = TextSecondary,
                Location = new Point(66, 34),
                Size = new Size(224, 20),
                AutoEllipsis = true
            };

            _btnNowFavorite = CreateFlatButton("\u2665", 296, 16, 36, 36, Color.Transparent, 18, Color.Empty);
            _btnNowFavorite.Font = new Font("Segoe UI", 12f, FontStyle.Bold);

            nowPlayingPanel.Controls.AddRange(new Control[]
            {
                _nowPlayingThumb, _lblNowTitle, _lblNowArtist, _btnNowFavorite
            });

            // Right panel: Dedicated Low-Volume Music Audio Regulator
            var audioRegPanel = new Panel
            {
                Dock = DockStyle.Right,
                Width = 285,
                BackColor = BgYTMusicPlayerBar
            };

            var lblAudioHeader = new Label
            {
                Text = "VOLUME DA MÚSICA",
                Font = new Font("Segoe UI", 7.2f, FontStyle.Bold),
                ForeColor = Color.FromArgb(255, 120, 140),
                Location = new Point(6, 2),
                Size = new Size(270, 14)
            };

            _btnMute = CreateFlatButton("Som", 6, 20, 50, 34, Theme.SurfaceHigh, 17, Theme.Border);
            _btnMute.Font = new Font("Segoe UI Semibold", 8.5f, FontStyle.Bold);

            _volCustomBar = new PictureBox
            {
                Location = new Point(60, 20),
                Size = new Size(110, 34),
                BackColor = BgYTMusicPlayerBar,
                Cursor = Cursors.Hand
            };

            _btnOpenAudioRegulator = CreateFlatButton("Áudio", 176, 20, 68, 34, Color.FromArgb(32, 20, 24), 17, Color.FromArgb(60, 30, 40));
            _btnOpenAudioRegulator.ForeColor = Color.FromArgb(255, 135, 155);
            _btnOpenAudioRegulator.Font = new Font("Segoe UI Semibold", 8.5f, FontStyle.Bold);

            audioRegPanel.Controls.AddRange(new Control[]
            {
                lblAudioHeader, _btnMute, _volCustomBar, _btnOpenAudioRegulator
            });

            // Center panel: Transport Controls & Time
            var transportContainer = new Panel
            {
                Dock = DockStyle.Fill,
                BackColor = BgYTMusicPlayerBar
            };

            var transportInner = new Panel
            {
                Size = new Size(410, 62),
                Location = new Point(10, 4)
            };

            _lblCurrentTime = new Label
            {
                Text = "0:00",
                ForeColor = Theme.TextDim,
                Font = new Font("Segoe UI Semibold", 8.8f),
                Location = new Point(4, 18),
                Size = new Size(46, 22),
                TextAlign = ContentAlignment.MiddleRight
            };

            _btnShuffle = CreateFlatButton("Aleatório", 54, 12, 72, 36, Theme.SurfaceHigh, 18, Theme.Border);
            _btnShuffle.Font = new Font("Segoe UI Semibold", 8.2f, FontStyle.Bold);

            _btnPrev = CreateFlatButton("\u23EE", 132, 12, 44, 36, Theme.SurfaceHigh, 18, Theme.Border);

            _btnPlayPause = CreateFlatButton("\u25B6", 182, 9, 56, 42, AccentRed, 21, Color.Empty);
            _btnPlayPause.Font = new Font("Segoe UI", 12.5f, FontStyle.Bold);
            _btnPlayPause.ForeColor = Color.White;

            _btnNext = CreateFlatButton("\u23ED", 244, 12, 44, 36, Theme.SurfaceHigh, 18, Theme.Border);

            _btnRepeat = CreateFlatButton("Repetir", 294, 12, 64, 36, Theme.SurfaceHigh, 18, Theme.Border);
            _btnRepeat.Font = new Font("Segoe UI Semibold", 8.2f, FontStyle.Bold);

            _lblTotalTime = new Label
            {
                Text = "0:00",
                ForeColor = Theme.TextDim,
                Font = new Font("Segoe UI Semibold", 8.8f),
                Location = new Point(362, 18),
                Size = new Size(46, 22),
                TextAlign = ContentAlignment.MiddleLeft
            };

            transportInner.Controls.AddRange(new Control[]
            {
                _lblCurrentTime, _btnShuffle, _btnPrev, _btnPlayPause, _btnNext, _btnRepeat, _lblTotalTime
            });

            transportContainer.Controls.Add(transportInner);
            transportContainer.Resize += (s, e) =>
            {
                transportInner.Left = Math.Max(0, (transportContainer.Width - transportInner.Width) / 2);
            };

            bottomContentRow.Controls.Add(transportContainer);
            bottomContentRow.Controls.Add(nowPlayingPanel);
            bottomContentRow.Controls.Add(audioRegPanel);

            _bottomBar.Controls.Add(bottomContentRow);
            _bottomBar.Controls.Add(_musicCustomSeekBar);

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
                _topBar.Visible = !isFull && !_isMiniPlayer;
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

            if (_nowPlayingThumb != null)
            {
                _nowPlayingThumb.Image = null;
            }

            bool isMusic = (env == "music");

            if (!isMusic && _videoEnvironmentControl.IsFullscreen)
            {
                _topBar.Visible = false;
            }
            else if (!_isMiniPlayer)
            {
                _topBar.Visible = true;
            }

            _musicEnvironmentContainer.Visible = isMusic && !_isMiniPlayer;
            _videoEnvironmentControl.Visible = !isMusic && !_isMiniPlayer;

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

            if (_nowPlayingThumb != null && _activeEnvironment == "video")
            {
                _nowPlayingThumb.Image = null;
                _nowPlayingThumb.Invalidate();
            }

            ThumbnailCache.ClearMemoryCache();
            UpdateTopBarLayout();
            MemoryOptimizer.TrimMemory();
            GC.Collect(2, GCCollectionMode.Optimized);
            UpdateRamBadge();
        }

        private void BuildMiniPlayerPanel()
        {
            _miniPlayerPanel = new Panel
            {
                Dock = DockStyle.Fill,
                BackColor = Color.FromArgb(12, 12, 12),
                Visible = false,
                Padding = new Padding(10)
            };

            _lblMiniTitle = new Label
            {
                Text = "Nenhuma música tocando",
                Font = new Font("Segoe UI Semibold", 10f, FontStyle.Bold),
                ForeColor = TextPrimary,
                Location = new Point(12, 8),
                Size = new Size(280, 22),
                AutoEllipsis = true
            };

            _lblMiniArtist = new Label
            {
                Text = "LiteTube Mini-Player (YT Music)",
                Font = new Font("Segoe UI", 8.5f),
                ForeColor = TextSecondary,
                Location = new Point(12, 30),
                Size = new Size(200, 18),
                AutoEllipsis = true
            };

            _lblMiniTime = new Label
            {
                Text = "0:00 / 0:00",
                Font = new Font("Segoe UI", 8.5f),
                ForeColor = AccentRed,
                Location = new Point(215, 30),
                Size = new Size(100, 18),
                TextAlign = ContentAlignment.MiddleRight
            };

            _lblMiniRam = new Label
            {
                Text = "⚡ -- MB",
                Font = new Font("Segoe UI", 8f, FontStyle.Bold),
                ForeColor = AccentGreen,
                Location = new Point(320, 10),
                Size = new Size(80, 18),
                TextAlign = ContentAlignment.MiddleRight
            };

            _btnMiniPrev = CreateFlatButton("⏮", 12, 54, 44, 30, BgCard);
            _btnMiniPlayPause = CreateFlatButton("▶", 60, 54, 54, 30, AccentRed);
            _btnMiniNext = CreateFlatButton("⏭", 118, 54, 44, 30, BgCard);
            _btnMiniFav = CreateFlatButton("♡", 166, 54, 38, 30, BgCard);
            _btnMiniExpand = CreateFlatButton("🗖 Expandir", 210, 54, 92, 30, BgCard);
            _btnMiniTray = CreateFlatButton("⬇ Bandeja", 306, 54, 88, 30, BgCard);

            _btnMiniMute = CreateFlatButton("🔊", 12, 90, 40, 28, BgCard);
            _btnMiniVolDown = CreateFlatButton("➖", 56, 90, 38, 28, BgCard);
            _lblMiniVol = new Label
            {
                Text = "Vol: 12%",
                Font = new Font("Segoe UI Semibold", 8.8f, FontStyle.Bold),
                ForeColor = TextPrimary,
                BackColor = BgCard,
                Location = new Point(98, 90),
                Size = new Size(92, 28),
                TextAlign = ContentAlignment.MiddleCenter
            };
            _btnMiniVolUp = CreateFlatButton("➕", 194, 90, 38, 28, BgCard);
            _btnMiniAudioReg = CreateFlatButton("🎛️ Áudio Música", 238, 90, 156, 28, Color.FromArgb(40, 25, 32));
            _btnMiniAudioReg.ForeColor = Color.FromArgb(255, 130, 150);

            _miniPlayerPanel.Controls.AddRange(new Control[]
            {
                _lblMiniTitle, _lblMiniArtist, _lblMiniTime, _lblMiniRam,
                _btnMiniPrev, _btnMiniPlayPause, _btnMiniNext, _btnMiniFav,
                _btnMiniExpand, _btnMiniTray,
                _btnMiniMute, _btnMiniVolDown, _lblMiniVol, _btnMiniVolUp, _btnMiniAudioReg
            });

            Controls.Add(_miniPlayerPanel);
        }

        private void BuildSystemTray()
        {
            _trayMenu = new ContextMenuStrip();
            _trayMenu.Items.Add("Abrir YT Music", null, (s, e) => { RestoreFromTray(); SwitchEnvironment("music"); });
            _trayMenu.Items.Add("\u25B6 Abrir YouTube Vídeos", null, (s, e) => { RestoreFromTray(); SwitchEnvironment("video"); });
            _trayMenu.Items.Add("Alternar Mini-Player", null, (s, e) => { RestoreFromTray(); ToggleMiniPlayerMode(!_isMiniPlayer); });
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

            _btnMiniMode.Click += (s, e) => ToggleMiniPlayerMode(true);
            _btnMiniExpand.Click += (s, e) => ToggleMiniPlayerMode(false);
            _btnHideToTray.Click += (s, e) => HideToSystemTray();
            _btnMiniTray.Click += (s, e) => HideToSystemTray();

            // Sidebar Navigation
            _navSearch.Click += (s, e) => SwitchSection("search");
            _navQueue.Click += (s, e) => SwitchSection("queue");
            _navFavorites.Click += (s, e) => SwitchSection("favorites");

            _cmbBrowser.SelectedIndexChanged += (s, e) =>
            {
                _config.BrowserForCookies = _cmbBrowser.SelectedItem.ToString();
                _config.Save();
            };

            _navAccountRecommended.Click += async (s, e) =>
            {
                _activeSection = "account_rec";
                await LoadBrowserPlaylistAsync("https://www.youtube.com/", "✨ Recomendados do YouTube (" + GetAccountAuthLabel() + ")", true);
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

            // Music Transport Controls
            _btnPlayPause.Click += (s, e) => _musicAudio.TogglePlayPause();
            _btnMiniPlayPause.Click += (s, e) => _musicAudio.TogglePlayPause();
            _btnNext.Click += (s, e) => _musicAudio.PlayNext();
            _btnMiniNext.Click += (s, e) => _musicAudio.PlayNext();
            _btnPrev.Click += (s, e) => _musicAudio.PlayPrevious();
            _btnMiniPrev.Click += (s, e) => _musicAudio.PlayPrevious();

            _btnNowFavorite.Click += (s, e) => ToggleCurrentTrackFavorite();
            _btnMiniFav.Click += (s, e) => ToggleCurrentTrackFavorite();

            _btnShuffle.Click += (s, e) =>
            {
                _config.Shuffle = !_config.Shuffle;
                _config.Save();
                UpdateRepeatShuffleButtons();
            };

            _btnRepeat.Click += (s, e) =>
            {
                _config.Repeat = (_config.Repeat + 1) % 3;
                _config.Save();
                UpdateRepeatShuffleButtons();
            };

            // Custom Smooth Top-Edge Seek Bar for YouTube Music
            _musicCustomSeekBar.Paint += OnPaintMusicSeekBar;
            _musicCustomSeekBar.MouseDown += (s, e) =>
            {
                _isDraggingSeek = true;
                SeekMusicFromMouseX(e.X);
            };
            _musicCustomSeekBar.MouseMove += (s, e) =>
            {
                if (_isDraggingSeek) SeekMusicFromMouseX(e.X);
            };
            _musicCustomSeekBar.MouseUp += (s, e) =>
            {
                _isDraggingSeek = false;
            };

            // Music Audio Regulator Controls
            _btnMute.Click += (s, e) => _musicAudio.ToggleMute();
            _btnMiniMute.Click += (s, e) => _musicAudio.ToggleMute();

            _btnMiniVolDown.Click += (s, e) => _musicAudio.ChangeVolumeBy(-0.02);

            _btnMiniVolUp.Click += (s, e) => _musicAudio.ChangeVolumeBy(0.02);

            _btnOpenAudioRegulator.Click += (s, e) => ShowMusicAudioRegulatorDialog();
            _btnMiniAudioReg.Click += (s, e) => ShowMusicAudioRegulatorDialog();

            _volCustomBar.Paint += OnPaintMusicVolumeBar;
            _volCustomBar.MouseEnter += (s, e) => { _isHoveringMusicVolBar = true; _volCustomBar.Invalidate(); };
            _volCustomBar.MouseLeave += (s, e) => { _isHoveringMusicVolBar = false; _volCustomBar.Invalidate(); };
            _volCustomBar.MouseDown += (s, e) =>
            {
                _isDraggingVolumeBar = true;
                SetMusicVolumeFromBarMouse(e.X);
            };
            _volCustomBar.MouseMove += (s, e) =>
            {
                if (_isDraggingVolumeBar) SetMusicVolumeFromBarMouse(e.X);
            };
            _volCustomBar.MouseUp += (s, e) =>
            {
                if (_isDraggingVolumeBar)
                {
                    _isDraggingVolumeBar = false;
                    _config.Save();
                }
            };

            _bottomBar.MouseWheel += OnMusicVolumeMouseWheel;
            _volCustomBar.MouseWheel += OnMusicVolumeMouseWheel;
            _miniPlayerPanel.MouseWheel += OnMusicVolumeMouseWheel;

            // AudioEngine callbacks
            _musicAudio.TrackChanged += t => SafeInvoke(() => OnTrackChanged(t));
            _musicAudio.PlaybackStateChanged += (playing, loading) => SafeInvoke(() => OnPlaybackStateChanged(playing, loading));
            _musicAudio.StatusMessage += msg => SafeInvoke(() => _lblStatus.Text = msg);
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
                await LoadBrowserPlaylistAsync("https://www.youtube.com/", "🎵 Recomendados do YouTube (" + GetAccountAuthLabel() + ")", true);
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

        private void SeekMusicFromMouseX(int mouseX)
        {
            double tot = _musicAudio.TotalDurationSeconds;
            if (tot <= 0) return;
            double ratio = Math.Max(0.0, Math.Min(1.0, (double)mouseX / Math.Max(1, _musicCustomSeekBar.Width)));
            _musicAudio.SeekToSeconds(ratio * tot);
            _musicCustomSeekBar.Invalidate();
        }

        private void OnPaintMusicSeekBar(object sender, PaintEventArgs e)
        {
            Graphics g = e.Graphics;
            g.SmoothingMode = SmoothingMode.AntiAlias;
            int w = _musicCustomSeekBar.Width;
            int h = _musicCustomSeekBar.Height;

            g.Clear(BgYTMusicPlayerBar);

            const int trackH = 5;
            int trackY = (h - trackH) / 2;
            using (var tp = Theme.RoundRect(new Rectangle(0, trackY, w, trackH), 2))
            using (var trackBrush = new SolidBrush(Color.FromArgb(52, 52, 68)))
            {
                g.FillPath(trackBrush, tp);
            }

            double tot = _musicAudio.TotalDurationSeconds;
            double cur = _musicAudio.CurrentPositionSeconds;

            if (tot > 0)
            {
                double ratio = Math.Max(0.0, Math.Min(1.0, cur / tot));
                int fillW = (int)Math.Round(ratio * w);
                if (fillW > 3)
                {
                    Rectangle fr = new Rectangle(0, trackY, fillW, trackH);
                    using (var fp = Theme.RoundRect(fr, 2))
                    using (var fillBrush = new LinearGradientBrush(fr, Theme.Accent, Theme.AccentSoft, 0f))
                    {
                        g.FillPath(fillBrush, fp);
                    }
                }

                int cx = Math.Max(6, Math.Min(w - 6, fillW));
                using (var haloBrush = new SolidBrush(Color.FromArgb(55, Theme.Accent)))
                {
                    g.FillEllipse(haloBrush, cx - 8, h / 2 - 8, 16, 16);
                }
                using (var thumbBrush = new SolidBrush(Color.White))
                {
                    g.FillEllipse(thumbBrush, cx - 5, h / 2 - 5, 10, 10);
                }
            }
        }

        private void OnMusicVolumeMouseWheel(object sender, MouseEventArgs e)
        {
            double delta = e.Delta > 0 ? 0.02 : -0.02;
            _musicAudio.ChangeVolumeBy(delta);
        }

        private void SetMusicVolumeFromBarMouse(int mouseX)
        {
            int padX = 10;
            int trackW = Math.Max(10, _volCustomBar.Width - padX * 2);
            double ratio = Math.Max(0.0, Math.Min(1.0, (double)(mouseX - padX) / trackW));
            _musicAudio.Volume = Math.Round(ratio, 2);
            if (_musicAudio.Volume > 0 && _musicAudio.IsMuted) _musicAudio.IsMuted = false;
            UpdateMusicAudioRegulatorUI();
        }

        private void OnPaintNowPlayingThumb(object sender, PaintEventArgs e)
        {
            Graphics g = e.Graphics;
            g.SmoothingMode = SmoothingMode.AntiAlias;
            g.InterpolationMode = InterpolationMode.HighQualityBicubic;
            g.Clear(BgYTMusicPlayerBar);

            Rectangle rect = new Rectangle(0, 0, _nowPlayingThumb.Width - 1, _nowPlayingThumb.Height - 1);
            using (var path = Theme.RoundRect(rect, 8))
            {
                bool drewImage = false;
                Image currentImg = _nowPlayingThumb.Image;
                if (currentImg != null)
                {
                    try
                    {
                        if (currentImg.Width > 0 && currentImg.Height > 0)
                        {
                            Region oldClip = g.Clip;
                            g.SetClip(path);
                            g.DrawImage(currentImg, rect);
                            g.Clip = oldClip;
                            drewImage = true;
                        }
                    }
                    catch (ArgumentException)
                    {
                        _nowPlayingThumb.Image = null;
                        drewImage = false;
                    }
                    catch
                    {
                        _nowPlayingThumb.Image = null;
                        drewImage = false;
                    }
                }

                if (!drewImage)
                {
                    using (var br = new SolidBrush(Theme.SurfaceHigh))
                    {
                        g.FillPath(br, path);
                    }
                    using (var f = new Font("Segoe UI", 16f))
                    {
                        TextRenderer.DrawText(g, "\u266B", f, rect, Theme.TextDim,
                            TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter);
                    }
                }
                using (var pen = new Pen(Theme.Border))
                {
                    g.DrawPath(pen, path);
                }
            }
        }

        private void OnPaintMusicVolumeBar(object sender, PaintEventArgs e)
        {
            Theme.DrawModernVolumeBar(
                e.Graphics,
                new Rectangle(0, 0, _volCustomBar.Width, _volCustomBar.Height),
                _musicAudio.Volume,
                _musicAudio.IsMuted,
                false,
                AccentRed,
                _isDraggingVolumeBar,
                _isHoveringMusicVolBar,
                "VOLUME"
            );
        }

        private void UpdateMusicAudioRegulatorUI()
        {
            int pct = (int)Math.Round(_musicAudio.Volume * 100);
            if (_btnMute != null)
            {
                _btnMute.Text = _musicAudio.IsMuted ? "Mudo" : "Som";
                _btnMute.ForeColor = _musicAudio.IsMuted ? Theme.TextDim : Color.White;
                _btnMute.BackColor = _musicAudio.IsMuted ? Color.FromArgb(40, 20, 25) : Theme.SurfaceHigh;
            }
            if (_lblMiniVol != null)
            {
                _lblMiniVol.Text = _musicAudio.IsMuted ? "MUTADO" : string.Format("Mús: {0}%", pct);
            }
            if (_volCustomBar != null)
            {
                _volCustomBar.Invalidate();
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

        private string FormatBalanceLabel(double bal)
        {
            int pct = (int)Math.Round(bal * 100);
            if (pct == 0) return "🎧 Balanço de Música: Centro (L 50% / R 50%)";
            if (pct < 0) return string.Format("🎧 Balanço de Música: {0}% Esquerda (L)", Math.Abs(pct));
            return string.Format("🎧 Balanço de Música: {0}% Direita (R)", pct);
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
            _btnNowFavorite.Text = fav ? "❤️" : "♡";
            _btnNowFavorite.ForeColor = fav ? AccentRed : TextPrimary;
            _btnMiniFav.Text = fav ? "❤️" : "♡";
            _btnMiniFav.ForeColor = fav ? AccentRed : TextPrimary;
        }

        private void OnTrackChanged(TrackItem track)
        {
            if (track == null)
            {
                _lblNowTitle.Text = "Nenhuma música tocando";
                _lblNowArtist.Text = "YouTube Music • Áudio Independente";
                _lblMiniTitle.Text = "Nenhuma música tocando";
                _lblMiniArtist.Text = "LiteTube Mini-Player";
                _nowPlayingThumb.Image = null;
                _trayIcon.Text = "LiteTube — Parado";
            }
            else
            {
                _lblNowTitle.Text = track.Title;
                _lblNowArtist.Text = track.Artist;
                _lblMiniTitle.Text = track.Title;
                _lblMiniArtist.Text = track.Artist;

                try
                {
                    _nowPlayingThumb.Image = null;
                    Image thumb = ThumbnailCache.GetOrRequestThumbnail(track.Id, () =>
                    {
                        SafeInvoke(() =>
                        {
                            try
                            {
                                if (_musicAudio.CurrentTrack != null && _musicAudio.CurrentTrack.Id == track.Id)
                                {
                                    Image loadedImg = ThumbnailCache.GetOrRequestThumbnail(track.Id, null);
                                    if (loadedImg != null && loadedImg.Width > 0 && loadedImg.Height > 0)
                                    {
                                        _nowPlayingThumb.Image = loadedImg;
                                        _nowPlayingThumb.Invalidate();
                                    }
                                }
                            }
                            catch { }
                        });
                    });

                    if (thumb != null && thumb.Width > 0 && thumb.Height > 0)
                    {
                        _nowPlayingThumb.Image = thumb;
                    }
                    _nowPlayingThumb.Invalidate();
                }
                catch
                {
                    _nowPlayingThumb.Image = null;
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
            string icon = isLoading ? "..." : (isPlaying ? "\u23F8" : "\u25B6");
            _btnPlayPause.Text = icon;
            _btnMiniPlayPause.Text = icon;
        }

        private void OnUiTimerTick(object sender, EventArgs e)
        {
            if (_musicAudio.IsPlaying)
            {
                double cur = _musicAudio.CurrentPositionSeconds;
                double tot = _musicAudio.TotalDurationSeconds;

                _lblCurrentTime.Text = YouTubeService.FormatSeconds(cur);
                _lblTotalTime.Text = YouTubeService.FormatSeconds(tot);
                _lblMiniTime.Text = _lblCurrentTime.Text + " / " + _lblTotalTime.Text;
                _musicCustomSeekBar.Invalidate();
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
            _lblMiniRam.Text = string.Format("⚡ {0:0.0} MB", mb);
        }

        private void InitHardwareAccelerationOnStartup()
        {
            VlcPlayer.EnsureInitializedAsync();

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
                            Logger.Info("STARTUP", string.Format("🚀 LiteTube pronto com aceleração LibVLC Direct3D na GPU: {0}", primary.Name));
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
            _btnGpuBadge.Text = "⚡ VLC Direct3D";
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
            sb.AppendLine("⚡ PIPELINE DE VÍDEO HARDWARE & DIRECT3D (LITETUBE VLC)");
            sb.AppendLine("==========================================================");
            sb.AppendLine();
            sb.AppendLine("🚀 MOTOR DE VÍDEO ATIVO: LibVLC 3.x (Direct3D 11 HWND Native)");
            sb.AppendLine("• Decodificação por Hardware: Ativa (--avcodec-hw=any)");
            sb.AppendLine("• Codecs Nativos na GPU: H.264 (AVC), VP9, AV1 (Sem recodificação)");
            sb.AppendLine("• Renderizador: Direct3D 11 / D3D11VA via HWND Nativo do Windows");
            sb.AppendLine("• Latência de Stream: Loopback Range Unthrottler (Zero Buffering)");
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

        private void ToggleMiniPlayerMode(bool enableMini)
        {
            if (_isMiniPlayer == enableMini) return;
            _isMiniPlayer = enableMini;

            if (enableMini)
            {
                if (_videoEnvironmentControl.IsFullscreen)
                {
                    _videoEnvironmentControl.ToggleFullscreen(false);
                }

                _normalSize = Size;
                _normalLocation = Location;

                _topBar.Visible = false;
                _musicEnvironmentContainer.Visible = false;
                _videoEnvironmentControl.Visible = false;
                _miniPlayerPanel.Visible = true;
                _miniPlayerPanel.BringToFront();

                MinimumSize = new Size(415, 165);
                Size = new Size(415, 165);
                FormBorderStyle = FormBorderStyle.FixedToolWindow;
                TopMost = _config.AlwaysOnTopMiniPlayer;
            }
            else
            {
                TopMost = false;
                FormBorderStyle = FormBorderStyle.Sizable;
                _miniPlayerPanel.Visible = false;
                _topBar.Visible = true;

                MinimumSize = new Size(1020, 600);
                Size = _normalSize;
                if (_normalLocation != Point.Empty) Location = _normalLocation;

                SwitchEnvironment(_activeEnvironment);
            }

            MemoryOptimizer.TrimMemory();
            UpdateRamBadge();
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
            var mbShuff = _btnShuffle as ModernButton;
            if (mbShuff != null)
            {
                mbShuff.BackColor = _config.Shuffle ? AccentRed : Theme.SurfaceHigh;
                mbShuff.ForeColor = _config.Shuffle ? Color.White : TextPrimary;
                mbShuff.BorderColor = _config.Shuffle ? Color.Empty : Theme.Border;
                mbShuff.Invalidate();
            }

            var mbRep = _btnRepeat as ModernButton;
            if (mbRep != null)
            {
                RepeatMode mode = (RepeatMode)_config.Repeat;
                if (mode == RepeatMode.None)
                {
                    mbRep.Text = "Repetir";
                    mbRep.BackColor = Theme.SurfaceHigh;
                    mbRep.ForeColor = TextPrimary;
                    mbRep.BorderColor = Theme.Border;
                }
                else if (mode == RepeatMode.RepeatAll)
                {
                    mbRep.Text = "Rep. Tudo";
                    mbRep.BackColor = AccentRed;
                    mbRep.ForeColor = Color.White;
                    mbRep.BorderColor = Color.Empty;
                }
                else
                {
                    mbRep.Text = "Rep. 1";
                    mbRep.BackColor = AccentRed;
                    mbRep.ForeColor = Color.White;
                    mbRep.BorderColor = Color.Empty;
                }
                mbRep.Invalidate();
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
                bool controlVideo = (_activeEnvironment == "video" && Visible && !_isMiniPlayer);

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

        private void OnMainFormClosing(object sender, FormClosingEventArgs e)
        {
            if (!_forceExit && e.CloseReason == CloseReason.UserClosing && _config.MinimizeToTrayOnClose)
            {
                e.Cancel = true;
                HideToSystemTray();
                return;
            }

            _uiTimer.Stop();
            UnregisterGlobalMediaKeys();
            _trayIcon.Visible = false;
            _trayIcon.Dispose();
            if (_videoEnvironmentControl != null) _videoEnvironmentControl.StopVideo();
            _musicAudio.Dispose();
            _config.Save();
        }

        private void SafeInvoke(Action action)
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
