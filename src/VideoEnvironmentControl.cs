using System;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.IO;
using System.Runtime.InteropServices;
using System.Threading.Tasks;
using System.Windows.Forms;
using FlyleafLib;
using FlyleafLib.Controls.WinForms;
using FlyleafLib.MediaPlayer;
using FlyleafLib.MediaFramework.MediaStream;

namespace LiteTube
{
    /// <summary>
    /// YouTube-Inspired Video Environment (▶ YouTube) with:
    /// - Authentic YouTube Dark Layout (#0F0F0F), Filter Chips (Tudo | Vídeos | Canais) & Infinite Scroll
    /// - Isolated Official Channel Page with 3 YouTube Tabs: Início ("home"), Vídeos ("videos"), Playlists ("playlists")
    /// - Watch Page with Player, Full-Width Red/Gray Buffer SeekBar, Watch Metadata Panel below player, and Compact Right Sidebar
    /// - FlyleafLib Direct3D 11 Native Hardware-Accelerated Streaming with Direct FFmpeg Demuxing
    /// </summary>
    public class VideoEnvironmentControl : UserControl
    {
        private readonly AppConfig _config;
        private readonly YouTubeService _ytService;
        private readonly AudioEngine _musicEngine;
        private readonly Form _parentForm;

        // Official YouTube Dark Palette (Preto Puro Neutro - Sem tom azulado)
        private readonly Color BgDark = Theme.Bg;
        private readonly Color BgPanel = Theme.Surface;
        private readonly Color BgCard = Theme.SurfaceHigh;
        private readonly Color YouTubeRed = Theme.Accent;
        private readonly Color AccentBlue = Theme.Blue;
        private readonly Color AccentGreen = Color.FromArgb(46, 204, 113);
        private readonly Color TextPrimary = Theme.TextMain;
        private readonly Color TextSecondary = Theme.TextDim;

        // YouTube Official Left Sidebar
        // YouTube Official Left Sidebar (Início | Inscrições | Você | Histórico)
        private Panel _sidebar;
        private Button _navHome;
        private Button _navSubscriptions;
        private Button _navYou;
        private Button _navHistory;
        private Button _navBackToPlayer;
        private Button _navOpenUrl;
        private Label _lblSidebarAccount;
        private string _activeNavSection = "home"; // "home", "subs", "you", "history", "search"

        // Main Center Panel & Top Search Filter Chips Bar
        private Panel _mainContentPanel;
        private Panel _topChipsBar;
        private Button _chipFilterAll;
        private Button _chipFilterVideos;
        private Button _chipFilterChannels;
        private Button _chipHideShorts;
        private Button _chipToggleSponsorBlock;
        private string _activeSearchChip = "all";
        private Label _lblBufferBadge;
        private List<SponsorSegment> _currentSponsorSegments = new List<SponsorSegment>();
        private string _lastSkippedSponsorUuid = null;

        // View 1: YouTube Home/Search Grid Catalog + YouTube Channel Page Header
        private Panel _catalogPanel;
        private Panel _channelBannerPanel;
        private Button _btnBackFromChannel;
        private PictureBox _picChannelAvatar;
        private Label _lblChannelBannerTitle;
        private Label _lblChannelBannerSub;
        private Button _btnChannelTabHome;
        private Button _btnChannelTabVideos;
        private Button _btnChannelTabPlaylists;
        private Panel _tabUnderlineIndicator;
        private Button _btnImportOpenedPlaylist;
        private Label _lblCatalogHeader;
        private MediaCardListControl _catalogGridCards;

        // View 2: YouTube Watch Page (Center Player + Watch Metadata Below + Right Compact Related Sidebar)
        private Panel _watchPagePanel;
        private Panel _rightRelatedPanel;
        private Label _lblRelatedHeader;
        private MediaCardListControl _relatedRowCards;

        // Video Stage (Inside Watch Page)
        private Panel _playerStagePanel;

        // FlyleafLib Native Player Engine & Unified Airspace-Safe WPF Overlay
        private Panel _videoCanvasContainer;
        private FlyleafHost _flyleafHost;
        private Player _player;
        private Config _playerConfig;
        private WpfVideoPlayerOverlay _wpfOverlay;

        private Panel _loadingOverlay;
        private Label _lblLoadingTitle;
        private Label _lblLoadingProgress;
        private Button _btnCancelLoad;

        // YouTube Watch Metadata Bar (Below Player Canvas: Video Title, Channel Avatar, Channel Name, Acessar Canal, Salvar)
        private Panel _watchMetaPanel;
        private Label _lblVideoTitle;
        private PictureBox _picWatchChannelAvatar;
        private Label _lblVideoChannel;
        private Label _lblVideoSubInfo;
        private Button _btnOpenCurrentChannel;
        private Button _btnSaveVideo;
        private Button _btnBackToCatalogFromWatch;

        // State for Feeds, Search & Infinite Scroll
        private List<TrackItem> _homeVideos = new List<TrackItem>();
        private List<TrackItem> _subscriptionsVideos = new List<TrackItem>();
        private List<TrackItem> _historyVideos = new List<TrackItem>();
        private List<TrackItem> _searchVideos = new List<TrackItem>();
        private string _searchQueryLabel = "";
        private string _searchContinuationToken = "";
        private bool _isSavedVideosView = false;

        // Channel State ("home", "videos", "playlists", "playlist_items")
        private bool _isChannelView = false;
        private string _activeChannelId = "";
        private string _activeChannelName = "";
        private string _activeChannelAvatarUrl = "";
        private string _activeChannelSubscribers = "";
        private string _activeChannelTab = "home";
        private string _channelContinuationToken = "";
        private List<TrackItem> _channelCurrentItems = new List<TrackItem>();
        private TrackItem _openedChannelPlaylist = null;
        private List<TrackItem> _openedPlaylistVideos = new List<TrackItem>();

        private bool _isLoadingMore = false;
        private bool _initialHomeLoaded = false;

        // Playback & Progressive Streaming State
        private TrackItem _currentVideo;
        private ProgressiveVideoSession _activeProgressiveSession;

        private bool _isPlaying = false;
        private bool _isSwitchingQuality = false;
        private bool _isLoading = false;
        private bool _isTheaterMode = false;
        private bool _isFullscreen = false;
        private FormWindowState _prevWindowState;
        private FormBorderStyle _prevBorderStyle;
        private DateTime _lastMouseActivityUtc = DateTime.UtcNow;

        public event Action<string> StatusNotification;
        public event Action<bool> FullscreenChanged;
        public event Action PlaylistImportedToLibrary;

        public VideoEnvironmentControl(AppConfig config, YouTubeService ytService, AudioEngine musicEngine, Form parentForm)
        {
            _config = config;
            _ytService = ytService;
            _musicEngine = musicEngine;
            _parentForm = parentForm;

            Dock = DockStyle.Fill;
            BackColor = BgDark;
            ForeColor = TextPrimary;
            Font = new Font("Segoe UI", 9.5f, FontStyle.Regular);
            DoubleBuffered = true;

            BuildUI();
            WireEvents();
            UpdateQualityButtons();
            UpdateVideoAudioUI();
        }

        public double VideoVolume { get { return _config.VideoVolume; } }
        public bool IsVideoMuted { get { return _config.IsVideoMuted; } }
        public bool IsVideoPlaying { get { return _isPlaying || (_player != null && _player.IsPlaying); } }
        public TrackItem CurrentVideo { get { return _currentVideo; } }
        public bool IsFullscreen { get { return _isFullscreen; } }
        public bool IsTheaterMode { get { return _isTheaterMode; } }
        public bool IsOverlayVisible { get { return _wpfOverlay != null && _wpfOverlay.IsOverlayVisible; } }

        private void BuildUI()
        {
            // ====================================================================
            // 1. YOUTUBE OFFICIAL LEFT SIDEBAR (INÍCIO | INSCRIÇÕES | VOCÊ | HISTÓRICO)
            // ====================================================================
            _sidebar = new Panel
            {
                Dock = DockStyle.Left,
                Width = 205,
                BackColor = Color.FromArgb(15, 15, 15),
                Padding = new Padding(8, 10, 8, 10)
            };

            int sideY = 10;
            _navHome = CreateYouTubeSidebarButton("Início", ref sideY);
            _navSubscriptions = CreateYouTubeSidebarButton("Inscrições", ref sideY);
            _navYou = CreateYouTubeSidebarButton("Você", ref sideY);
            _navHistory = CreateYouTubeSidebarButton("Histórico", ref sideY);

            sideY += 6;
            sideY += 6;
            var sideDivider = new Panel
            {
                Location = new Point(10, sideY),
                Size = new Size(184, 1),
                BackColor = Color.FromArgb(38, 38, 38)
            };
            _sidebar.Controls.Add(sideDivider);
            sideY += 10;

            _navBackToPlayer = CreateYouTubeSidebarButton("Assistindo", ref sideY);
            _navBackToPlayer.Visible = false;

            _navOpenUrl = CreateYouTubeSidebarButton("Abrir Link", ref sideY);

            _lblSidebarAccount = new Label
            {
                Text = GetAccountStatusLabel(),
                Font = new Font("Segoe UI Semibold", 8.2f, FontStyle.Bold),
                ForeColor = HasValidAuth() ? Color.FromArgb(0, 212, 130) : Theme.TextDim,
                Dock = DockStyle.Bottom,
                Height = 32,
                TextAlign = ContentAlignment.MiddleCenter,
                Cursor = Cursors.Hand,
                BackColor = Theme.Surface
            };
            _lblSidebarAccount.Click += (s, e) => ShowGoogleAuthDialog();
            _sidebar.Controls.Add(_lblSidebarAccount);

            // ====================================================================
            // 2. MAIN CENTER CONTENT & SEARCH FILTER CHIPS
            // ====================================================================
            _mainContentPanel = new Panel
            {
                Dock = DockStyle.Fill,
                BackColor = BgDark
            };

            _topChipsBar = new Panel
            {
                Dock = DockStyle.Top,
                Height = 44,
                BackColor = BgDark,
                Padding = new Padding(14, 6, 14, 6),
                Visible = false
            };

            _chipFilterAll = CreateChipButton("Tudo", 14, 6, 62, 32, true);
            _chipFilterVideos = CreateChipButton("Vídeos", 82, 6, 72, 32, false);
            _chipFilterChannels = CreateChipButton("Canais", 160, 6, 76, 32, false);
            _chipHideShorts = CreateChipButton(_config.HideShorts ? "🚫 Shorts: Ocultos" : "▶ Shorts: Visíveis", 244, 6, 134, 32, _config.HideShorts);
            _chipToggleSponsorBlock = CreateChipButton(_config.AutoSkipSponsors ? "🛡️ SponsorBlock: ON" : "🛡️ SponsorBlock: OFF", 384, 6, 150, 32, _config.AutoSkipSponsors);

            _lblBufferBadge = new Label
            {
                Text = "⚡ Streaming Direct3D 11 • FlyleafLib",
                Font = new Font("Segoe UI", 8.5f),
                ForeColor = TextSecondary,
                Location = new Point(540, 12),
                Size = new Size(350, 20),
                Anchor = AnchorStyles.Top | AnchorStyles.Right,
                TextAlign = ContentAlignment.MiddleRight,
                AutoEllipsis = true
            };

            _topChipsBar.Controls.AddRange(new Control[]
            {
                _chipFilterAll, _chipFilterVideos, _chipFilterChannels, _chipHideShorts, _chipToggleSponsorBlock, _lblBufferBadge
            });

            // ====================================================================
            // 3. VIEW 1: YOUTUBE CATALOG GRID + OFFICIAL CHANNEL HEADER
            // ====================================================================
            _catalogPanel = new Panel
            {
                Dock = DockStyle.Fill,
                BackColor = BgDark,
                Padding = new Padding(16, 4, 16, 10)
            };

            // YouTube-Inspired Channel Page Header (Avatar + Title + @handle/Subs + 3 Tabs: Início, Vídeos, Playlists)
            _channelBannerPanel = new Panel
            {
                Dock = DockStyle.Top,
                Height = 132,
                BackColor = BgDark,
                Visible = false
            };

            _btnBackFromChannel = CreateFlatButton("⬅ Voltar aos Resultados", 8, 10, 168, 30, BgCard);
            _btnBackFromChannel.Font = new Font("Segoe UI Semibold", 8.8f, FontStyle.Bold);

            _picChannelAvatar = new PictureBox
            {
                Location = new Point(190, 10),
                Size = new Size(68, 68),
                BackColor = BgDark
            };
            _picChannelAvatar.Paint += OnPaintChannelAvatar;

            _lblChannelBannerTitle = new Label
            {
                Text = "Canal do YouTube",
                Font = new Font("Segoe UI", 16f, FontStyle.Bold),
                ForeColor = TextPrimary,
                Location = new Point(272, 12),
                Size = new Size(560, 32),
                AutoEllipsis = true
            };

            _lblChannelBannerSub = new Label
            {
                Text = "@canal • Inscritos • Conteúdo oficial exclusivo deste canal",
                Font = new Font("Segoe UI", 9.2f),
                ForeColor = TextSecondary,
                Location = new Point(274, 46),
                Size = new Size(560, 22),
                AutoEllipsis = true
            };

            // 3 YouTube Channel Tabs: Início | Vídeos | Playlists
            int tabY = 88;
            _btnChannelTabHome = CreateChannelTabButton("Início", 14, tabY, 96);
            _btnChannelTabVideos = CreateChannelTabButton("Vídeos", 116, tabY, 96);
            _btnChannelTabPlaylists = CreateChannelTabButton("Playlists", 218, tabY, 106);

            _tabUnderlineIndicator = new Panel
            {
                Location = new Point(14, 124),
                Size = new Size(96, 3),
                BackColor = TextPrimary
            };

            var bottomDivider = new Panel
            {
                Dock = DockStyle.Bottom,
                Height = 1,
                BackColor = Color.FromArgb(45, 45, 45)
            };

            _btnImportOpenedPlaylist = CreateFlatButton("📥 Salvar Playlist na Biblioteca", 340, tabY + 2, 210, 32, YouTubeRed);
            _btnImportOpenedPlaylist.Font = new Font("Segoe UI Semibold", 9f, FontStyle.Bold);
            _btnImportOpenedPlaylist.Visible = false;

            _channelBannerPanel.Controls.AddRange(new Control[]
            {
                _btnBackFromChannel, _picChannelAvatar, _lblChannelBannerTitle, _lblChannelBannerSub,
                _btnChannelTabHome, _btnChannelTabVideos, _btnChannelTabPlaylists,
                _tabUnderlineIndicator, _btnImportOpenedPlaylist, bottomDivider
            });

            _lblCatalogHeader = new Label
            {
                Dock = DockStyle.Top,
                Height = 28,
                Text = "Pesquise vídeos ou canais no topo — clique no vídeo para assistir ou no nome do canal para explorar",
                Font = new Font("Segoe UI Semibold", 10f, FontStyle.Bold),
                ForeColor = TextSecondary,
                Padding = new Padding(4, 4, 0, 0)
            };

            _catalogGridCards = new MediaCardListControl
            {
                Dock = DockStyle.Fill,
                IsGridMode = true,
                IsMusicEnvironment = false,
                SingleClickActivates = true,
                AccentColor = YouTubeRed,
                IsFavoriteOrSavedCheck = id => _config.IsSavedVideo(id)
            };

            _catalogPanel.Controls.Add(_catalogGridCards);
            _catalogPanel.Controls.Add(_lblCatalogHeader);
            _catalogPanel.Controls.Add(_channelBannerPanel);

            // ====================================================================
            // 3. VIEW 2: YOUTUBE WATCH PAGE (PLAYER + METADATA BELOW + COMPACT RIGHT SIDEBAR)
            // ====================================================================
            _watchPagePanel = new Panel
            {
                Dock = DockStyle.Fill,
                BackColor = BgDark,
                Visible = false
            };

            // 3a. Right Related Videos Sidebar (YouTube Compact Watch Sidebar)
            _rightRelatedPanel = new Panel
            {
                Dock = DockStyle.Right,
                Width = 376,
                BackColor = BgDark,
                Padding = new Padding(4, 6, 12, 6)
            };

            _lblRelatedHeader = new Label
            {
                Dock = DockStyle.Top,
                Height = 28,
                Text = "  Próximos vídeos",
                Font = new Font("Segoe UI Semibold", 10.5f, FontStyle.Bold),
                ForeColor = TextPrimary,
                TextAlign = ContentAlignment.MiddleLeft
            };

            _relatedRowCards = new MediaCardListControl
            {
                Dock = DockStyle.Fill,
                IsGridMode = false,
                IsCompactSidebarMode = true,
                IsMusicEnvironment = false,
                SingleClickActivates = true,
                AccentColor = YouTubeRed,
                IsFavoriteOrSavedCheck = id => _config.IsSavedVideo(id)
            };

            _rightRelatedPanel.Controls.Add(_relatedRowCards);
            _rightRelatedPanel.Controls.Add(_lblRelatedHeader);

            // 3b. Center Watch Column (Video Canvas + Player Controls + YouTube Watch Metadata Below)
            _playerStagePanel = new Panel
            {
                Dock = DockStyle.Fill,
                BackColor = BgDark,
                Padding = new Padding(14, 6, 8, 8)
            };

            // YouTube Watch Metadata Panel (Docked at Bottom of Watch Column, below Player Controls)
            _watchMetaPanel = new Panel
            {
                Dock = DockStyle.Bottom,
                Height = 92,
                BackColor = Theme.Bg,
                Padding = new Padding(8, 6, 8, 4)
            };

            _lblVideoTitle = new Label
            {
                Text = "Selecione um vídeo para assistir",
                Font = new Font("Segoe UI Semibold", 13.5f, FontStyle.Bold),
                ForeColor = Theme.TextMain,
                Location = new Point(8, 6),
                Size = new Size(640, 28),
                AutoEllipsis = true
            };

            _picWatchChannelAvatar = new PictureBox
            {
                Location = new Point(8, 40),
                Size = new Size(42, 42),
                BackColor = Theme.Bg,
                Cursor = Cursors.Hand
            };
            _picWatchChannelAvatar.Paint += OnPaintWatchChannelAvatar;

            _lblVideoChannel = new Label
            {
                Text = "Canal do YouTube",
                Font = new Font("Segoe UI Semibold", 10.5f, FontStyle.Bold),
                ForeColor = Theme.TextMain,
                Location = new Point(58, 40),
                Size = new Size(260, 20),
                Cursor = Cursors.Hand,
                AutoEllipsis = true
            };

            _lblVideoSubInfo = new Label
            {
                Text = "Clique para acessar o canal oficial",
                Font = new Font("Segoe UI", 8.8f),
                ForeColor = Theme.TextDim,
                Location = new Point(58, 62),
                Size = new Size(260, 18),
                Cursor = Cursors.Hand,
                AutoEllipsis = true
            };

            _btnOpenCurrentChannel = CreateFlatButton("Acessar Canal", 328, 44, 120, 34, Theme.TextMain, 17, Color.Empty);
            _btnOpenCurrentChannel.ForeColor = Theme.Bg;
            _btnOpenCurrentChannel.Font = new Font("Segoe UI Semibold", 9f, FontStyle.Bold);

            var watchRightActions = new Panel
            {
                Size = new Size(215, 36),
                Location = new Point(460, 40),
                Anchor = AnchorStyles.Top | AnchorStyles.Right
            };

            _btnSaveVideo = CreateFlatButton("Salvar", 0, 2, 95, 34, Theme.SurfaceHigh, 17, Theme.Border);
            _btnSaveVideo.Font = new Font("Segoe UI Semibold", 9f, FontStyle.Bold);

            _btnBackToCatalogFromWatch = CreateFlatButton("Ver Grade", 105, 2, 105, 34, Theme.SurfaceHigh, 17, Theme.Border);
            _btnBackToCatalogFromWatch.Font = new Font("Segoe UI Semibold", 9f, FontStyle.Bold);

            watchRightActions.Controls.Add(_btnSaveVideo);
            watchRightActions.Controls.Add(_btnBackToCatalogFromWatch);

            _watchMetaPanel.Controls.AddRange(new Control[]
            {
                _lblVideoTitle, _picWatchChannelAvatar, _lblVideoChannel, _lblVideoSubInfo,
                _btnOpenCurrentChannel, watchRightActions
            });

            _watchMetaPanel.Resize += (s, e) =>
            {
                _lblVideoTitle.Width = Math.Max(200, _watchMetaPanel.Width - 16);
                watchRightActions.Left = Math.Max(435, _watchMetaPanel.Width - watchRightActions.Width - 8);
            };

            _videoCanvasContainer = new Panel
            {
                Dock = DockStyle.Fill,
                BackColor = Color.Black
            };

            _flyleafHost = new FlyleafHost
            {
                Dock = DockStyle.Fill,
                BackColor = Color.Black,
                KeyBindings = false
            };

            InitializeFlyleafPlayer();

            _wpfOverlay = new WpfVideoPlayerOverlay(_videoCanvasContainer, _parentForm);

            _loadingOverlay = new Panel
            {
                Size = new Size(440, 126),
                BackColor = Color.FromArgb(24, 24, 24),
                Visible = false
            };

            _lblLoadingTitle = new Label
            {
                Text = "Iniciando streaming FlyleafLib...",
                Font = new Font("Segoe UI Semibold", 10f, FontStyle.Bold),
                ForeColor = TextPrimary,
                Location = new Point(16, 14),
                Size = new Size(408, 24),
                TextAlign = ContentAlignment.MiddleCenter,
                AutoEllipsis = true
            };

            _lblLoadingProgress = new Label
            {
                Text = "Conectando diretamente aos fluxos de áudio e vídeo...",
                Font = new Font("Segoe UI", 9f),
                ForeColor = AccentBlue,
                Location = new Point(16, 44),
                Size = new Size(408, 24),
                TextAlign = ContentAlignment.MiddleCenter,
                AutoEllipsis = true
            };

            _btnCancelLoad = CreateFlatButton("✖ Cancelar", 165, 78, 110, 32, YouTubeRed);
            _loadingOverlay.Controls.AddRange(new Control[] { _lblLoadingTitle, _lblLoadingProgress, _btnCancelLoad });

            _videoCanvasContainer.Controls.Add(_loadingOverlay);
            _videoCanvasContainer.Controls.Add(_flyleafHost);
            _videoCanvasContainer.Resize += (s, e) =>
            {
                CenterLoadingOverlay();
            };

            _playerStagePanel.Controls.Add(_videoCanvasContainer);
            _playerStagePanel.Controls.Add(_watchMetaPanel);

            _watchPagePanel.Controls.Add(_playerStagePanel);
            _watchPagePanel.Controls.Add(_rightRelatedPanel);

            _mainContentPanel.Controls.Add(_catalogPanel);
            _mainContentPanel.Controls.Add(_watchPagePanel);
            _mainContentPanel.Controls.Add(_topChipsBar);

            Controls.Add(_mainContentPanel);
            Controls.Add(_sidebar);

            WireWpfOverlayEvents();

            _watchPagePanel.VisibleChanged += (s, e) =>
            {
                if (!_watchPagePanel.Visible && _wpfOverlay != null)
                {
                    _wpfOverlay.SetOverlayActive(false);
                }
                else if (_watchPagePanel.Visible && _wpfOverlay != null)
                {
                    _wpfOverlay.SetOverlayActive(true);
                }
            };

            VisibleChanged += (s, e) =>
            {
                if (!Visible && _wpfOverlay != null)
                {
                    _wpfOverlay.SetOverlayActive(false);
                }
                else if (Visible && _watchPagePanel != null && _watchPagePanel.Visible && _wpfOverlay != null)
                {
                    _wpfOverlay.SetOverlayActive(true);
                }
            };

            UpdateSavedVideosCount();
        }

        private void OnPaintChannelAvatar(object sender, PaintEventArgs e)
        {
            Graphics g = e.Graphics;
            g.SmoothingMode = SmoothingMode.AntiAlias;
            g.Clear(BgDark);

            Rectangle rect = new Rectangle(2, 2, 64, 64);
            Image avatar = null;
            if (!string.IsNullOrEmpty(_activeChannelId) || !string.IsNullOrEmpty(_activeChannelAvatarUrl))
            {
                string key = "ch_" + (!string.IsNullOrEmpty(_activeChannelId) ? _activeChannelId : _activeChannelName);
                avatar = ThumbnailCache.GetOrRequestThumbnail(key, _activeChannelAvatarUrl, () =>
                {
                    SafeInvoke(() => _picChannelAvatar.Invalidate());
                });
            }

            if (avatar != null)
            {
                using (GraphicsPath gp = new GraphicsPath())
                {
                    gp.AddEllipse(rect);
                    Region oldClip = g.Clip;
                    g.SetClip(gp);
                    g.DrawImage(avatar, rect);
                    g.Clip = oldClip;
                }
            }
            else
            {
                using (var br = new SolidBrush(Color.FromArgb(200, 30, 55)))
                {
                    g.FillEllipse(br, rect);
                }
                string initial = !string.IsNullOrEmpty(_activeChannelName) ? _activeChannelName.Substring(0, 1).ToUpper() : "C";
                using (var f = new Font("Segoe UI", 20f, FontStyle.Bold))
                {
                    TextRenderer.DrawText(g, initial, f, rect, Color.White,
                        TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter);
                }
            }
        }

        private void OnPaintWatchChannelAvatar(object sender, PaintEventArgs e)
        {
            Graphics g = e.Graphics;
            g.SmoothingMode = SmoothingMode.AntiAlias;
            g.InterpolationMode = InterpolationMode.HighQualityBicubic;
            g.Clear(Theme.Bg);

            Rectangle rect = new Rectangle(1, 1, 40, 40);
            Image avatar = null;
            if (_currentVideo != null && (!string.IsNullOrEmpty(_currentVideo.ChannelId) || !string.IsNullOrEmpty(_currentVideo.CustomThumbUrl)))
            {
                string key = "ch_" + (!string.IsNullOrEmpty(_currentVideo.ChannelId) ? _currentVideo.ChannelId : _currentVideo.Artist);
                avatar = ThumbnailCache.GetOrRequestThumbnail(key, _currentVideo.CustomThumbUrl, () =>
                {
                    SafeInvoke(() => _picWatchChannelAvatar.Invalidate());
                });
            }

            if (avatar != null)
            {
                using (var gp = new GraphicsPath())
                {
                    gp.AddEllipse(rect);
                    Region oldClip = g.Clip;
                    g.SetClip(gp);
                    g.DrawImage(avatar, rect);
                    g.Clip = oldClip;
                }
                using (var pen = new Pen(Theme.Border, 1.5f))
                {
                    g.DrawEllipse(pen, rect);
                }
            }
            else
            {
                string name = _currentVideo != null ? _currentVideo.Artist : "Y";
                string initial = !string.IsNullOrEmpty(name) ? name.Trim().Substring(0, 1).ToUpper() : "Y";
                int hash = Math.Abs((name ?? "").GetHashCode());
                Color[] colors = new Color[]
                {
                    Color.FromArgb(235, 45, 85),
                    Color.FromArgb(16, 163, 127),
                    Color.FromArgb(52, 130, 245),
                    Color.FromArgb(145, 71, 255),
                    Color.FromArgb(255, 130, 0)
                };
                Color c1 = colors[hash % colors.Length];
                Color c2 = Theme.Blend(c1, Color.Black, 0.35f);

                using (var lg = new LinearGradientBrush(rect, c1, c2, 45f))
                {
                    g.FillEllipse(lg, rect);
                }
                using (var pen = new Pen(Color.FromArgb(80, 255, 255, 255), 1.2f))
                {
                    g.DrawEllipse(pen, rect);
                }
                using (var f = new Font("Segoe UI Semibold", 13f, FontStyle.Bold))
                {
                    TextRenderer.DrawText(g, initial, f, rect, Color.White,
                        TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter);
                }
            }
        }

        private void CenterLoadingOverlay()
        {
            if (_loadingOverlay != null && _videoCanvasContainer != null)
            {
                _loadingOverlay.Left = Math.Max(10, (_videoCanvasContainer.Width - _loadingOverlay.Width) / 2);
                _loadingOverlay.Top = Math.Max(10, (_videoCanvasContainer.Height - _loadingOverlay.Height) / 2);
            }
        }

        private void InitializeFlyleafPlayer()
        {
            try
            {
                _playerConfig = new Config();
                _playerConfig.Video.VideoAcceleration = true;
                _playerConfig.Player.SeekAccurate = true;
                _playerConfig.Player.AutoPlay = true;

                // Buffer Dinâmico e Estabilidade FlyleafLib
                _playerConfig.Demuxer.BufferDuration = 30 * 1000 * 10000L; // 30s de buffer à frente
                _playerConfig.Player.MinBufferDuration = 1500 * 10000L;    // 1.5s buffer mínimo para iniciar reprodução sem congelamento
                _playerConfig.Demuxer.BufferPackets = 5000;
                _playerConfig.Demuxer.MaxAudioPackets = 5000;
                _playerConfig.Demuxer.IOStreamBufferSize = 2 * 1024 * 1024;
                _playerConfig.Demuxer.OpenTimeout = 15 * 1000 * 10000L;
                _playerConfig.Demuxer.ReadTimeout = 20 * 1000 * 10000L;
                _playerConfig.Demuxer.SeekTimeout = 15 * 1000 * 10000L;

                string ua = "Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/120.0.0.0 Safari/537.36";
                _playerConfig.Demuxer.FormatOpt["user_agent"] = ua;
                _playerConfig.Demuxer.FormatOpt["headers"] = "User-Agent: " + ua + "\r\n";
                _playerConfig.Demuxer.FormatOpt["buffer_size"] = "4194304";
                _playerConfig.Demuxer.FormatOpt["reconnect"] = "1";
                _playerConfig.Demuxer.FormatOpt["reconnect_streamed"] = "1";
                _playerConfig.Demuxer.FormatOpt["reconnect_delay_max"] = "5";

                _playerConfig.Demuxer.AudioFormatOpt["user_agent"] = ua;
                _playerConfig.Demuxer.AudioFormatOpt["headers"] = "User-Agent: " + ua + "\r\n";
                _playerConfig.Demuxer.AudioFormatOpt["buffer_size"] = "1048576";
                _playerConfig.Demuxer.AudioFormatOpt["reconnect"] = "1";
                _playerConfig.Demuxer.AudioFormatOpt["reconnect_streamed"] = "1";
                _playerConfig.Demuxer.AudioFormatOpt["reconnect_delay_max"] = "5";

                _player = new Player(_playerConfig);
                _flyleafHost.Player = _player;

                _player.PlaybackStopped += OnPlayerPlaybackStopped;
                _player.OpenCompleted += OnPlayerOpenCompleted;
            }
            catch (Exception ex)
            {
                Logger.Error("FLYLEAF", "Erro ao inicializar FlyleafHost/Player: " + ex.Message);
            }
        }

        private void OnPlayerOpenCompleted(object sender, OpenCompletedArgs e)
        {
            SafeInvoke(() =>
            {
                _isPlaying = _player != null && _player.IsPlaying;
                UpdatePlayPauseButton();

                if (_player != null && _player.Video != null && !string.IsNullOrEmpty(_player.Video.Codec))
                {
                    string hw = _player.Video.VideoAcceleration ? "HW D3D11" : "SW";
                    string vCodec = _player.Video.Codec.ToUpperInvariant();
                    string aCodec = (_player.Audio != null && !string.IsNullOrEmpty(_player.Audio.Codec))
                        ? _player.Audio.Codec.ToUpperInvariant()
                        : "AUDIO";
                    string runtimeCodec = string.Format("{0}/{1} ({2})", vCodec, aCodec, hw);
                    _lblBufferBadge.Text = string.Format("▶ Flyleaf D3D11 ({0})", runtimeCodec);
                    _lblVideoSubInfo.Text = string.Format("Qualidade {0}p ({1})  •  Flyleaf Direct3D 11  •  Volume: {2}%",
                        _config.VideoQualityHeight, runtimeCodec, (int)Math.Round(_config.VideoVolume * 100));

                    Logger.Info("FLYLEAF", string.Format("Fluxo aberto e validado pelo FFmpeg: Res={0}x{1}, Codec={2}, PixelFormat={3}, AceleraçãoHW={4}",
                        _player.Video.Width, _player.Video.Height, runtimeCodec, _player.Video.PixelFormat, _player.Video.VideoAcceleration));
                }

                if (_wpfOverlay != null)
                {
                    _wpfOverlay.UpdatePlaybackState(_isPlaying, CurrentVideoPositionSeconds, BufferedVideoDurationSeconds, TotalVideoDurationSeconds);
                }
            });
        }

        private void OnPlayerPlaybackStopped(object sender, PlaybackStoppedArgs e)
        {
            SafeInvoke(() =>
            {
                if (_isSwitchingQuality) return;

                if (_player != null && _player.Status == Status.Ended)
                {
                    Logger.Info("FLYLEAF", "Fim do vídeo alcançado sem cortes prematuros.");
                    _isPlaying = false;
                    UpdatePlayPauseButton();
                    EmitStatus("🎬 Fim do vídeo.");
                    PlayNextVideo();
                }
            });
        }

        private async Task PlayStreamAsync(string videoUrl, string audioUrl, double durationSeconds, double startSeconds = 0.0)
        {
            if (_player == null) return;

            await Task.Run(() =>
            {
                try
                {
                    _player.Stop();

                    // 1. Abre a faixa de vídeo principal
                    _player.Open(videoUrl);

                    // 2. Acopla a faixa de áudio externa diretamente pelo motor FFmpeg com sincronização de PTS
                    if (!string.IsNullOrEmpty(audioUrl) && !string.Equals(videoUrl, audioUrl, StringComparison.OrdinalIgnoreCase))
                    {
                        var extAudio = new ExternalAudioStream
                        {
                            Url = audioUrl,
                            UserAgent = "Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/120.0.0.0 Safari/537.36"
                        };
                        _player.Open(extAudio, resync: true, defaultAudio: true);
                    }

                    if (startSeconds > 0.0)
                    {
                        _player.SeekAccurate((int)(startSeconds * 1000.0));
                    }

                    _player.Play();
                }
                catch (Exception ex)
                {
                    Logger.Error("FLYLEAF", "Erro ao abrir fluxos de reprodução: " + ex.Message);
                }
            });
        }

        private void WireWpfOverlayEvents()
        {
            if (_wpfOverlay == null) return;

            _wpfOverlay.PlayPauseRequested += () => SafeInvoke(() => TogglePlayPause());
            _wpfOverlay.NextVideoRequested += () => SafeInvoke(() => PlayNextVideo());
            _wpfOverlay.OnUserSeekCommitted += targetSec => SafeInvoke(() => SeekToSeconds(targetSec));
            _wpfOverlay.SeekRequested += targetSec => SafeInvoke(() => SeekToSeconds(targetSec));
            _wpfOverlay.MuteToggleRequested += () => SafeInvoke(() => ToggleVideoMute());
            _wpfOverlay.VolumeChanged += vol0To1 => SafeInvoke(() =>
            {
                _config.VideoVolume = Math.Max(0.0, Math.Min(1.0, Math.Round(vol0To1, 2)));
                if (_config.VideoVolume > 0 && _config.IsVideoMuted) _config.IsVideoMuted = false;
                ApplyVideoAudioSettings();
                _config.Save();
            });
            _wpfOverlay.QualityChanged += q => SafeInvoke(() => ChangeVideoQuality(q));
            _wpfOverlay.SpeedChanged += spd => SafeInvoke(() =>
            {
                if (_player != null) _player.Speed = spd;
                EmitStatus(string.Format("⚡ Velocidade do vídeo: {0:0.00}x", spd));
            });
            _wpfOverlay.TheaterToggleRequested += () => SafeInvoke(() => ToggleTheaterMode(!_isTheaterMode));
            _wpfOverlay.FullscreenToggleRequested += () => SafeInvoke(() => ToggleFullscreen(!_isFullscreen));
            _wpfOverlay.AudioDialogRequested += () => SafeInvoke(() => ShowVideoAudioRegulatorDialog());
            _wpfOverlay.ShortcutKeyPressed += key => SafeInvoke(() => HandleShortcutKey(key));
        }

        private void WireEvents()
        {
            // Sidebar Navigation (Início | Inscrições | Você | Histórico)
            _navHome.Click += async (s, e) => await SwitchToNavSectionAsync("home");
            _navSubscriptions.Click += async (s, e) => await SwitchToNavSectionAsync("subs");
            _navYou.Click += async (s, e) => await SwitchToNavSectionAsync("you");
            _navHistory.Click += async (s, e) => await SwitchToNavSectionAsync("history");
            _navBackToPlayer.Click += (s, e) => ShowWatchView();
            _navOpenUrl.Click += (s, e) => PromptOpenVideoUrl();

            // Search Filter Chips ("all", "videos", "channels", "hide_shorts", "sponsor_block")
            _chipFilterAll.Click += (s, e) => ApplySearchChipFilter("all");
            _chipFilterVideos.Click += (s, e) => ApplySearchChipFilter("videos");
            _chipFilterChannels.Click += (s, e) => ApplySearchChipFilter("channels");
            _chipHideShorts.Click += (s, e) => ToggleHideShorts();
            _chipToggleSponsorBlock.Click += (s, e) => ToggleSponsorBlock();

            // YouTube Channel Header Controls (Início | Vídeos | Playlists)
            _btnBackFromChannel.Click += (s, e) => ExitChannelViewToSearch();
            _btnChannelTabHome.Click += async (s, e) => await LoadChannelTabAsync("home");
            _btnChannelTabVideos.Click += async (s, e) => await LoadChannelTabAsync("videos");
            _btnChannelTabPlaylists.Click += async (s, e) => await LoadChannelTabAsync("playlists");
            _btnImportOpenedPlaylist.Click += (s, e) => ImportOpenedChannelPlaylistToLibrary();

            // Catalog Grid & Related Cards Events
            _catalogGridCards.ItemActivated += (idx, item) => HandleCardActivated(item);
            _catalogGridCards.ChannelClicked += item => OpenChannelFromItem(item);
            _catalogGridCards.FavoriteClicked += (idx, vid) => ToggleSaveVideo(vid);
            _catalogGridCards.LoadMoreRequested += async () => await LoadMoreVideosAsync();

            _relatedRowCards.ItemActivated += (idx, item) => HandleCardActivated(item);
            _relatedRowCards.ChannelClicked += item => OpenChannelFromItem(item);
            _relatedRowCards.FavoriteClicked += (idx, vid) => ToggleSaveVideo(vid);
            _relatedRowCards.LoadMoreRequested += async () => await LoadMoreVideosAsync();

            // Watch Metadata Bar Controls
            _picWatchChannelAvatar.Click += (s, e) => { if (_currentVideo != null) OpenChannelFromItem(_currentVideo); };
            _lblVideoChannel.Click += (s, e) => { if (_currentVideo != null) OpenChannelFromItem(_currentVideo); };
            _lblVideoSubInfo.Click += (s, e) => { if (_currentVideo != null) OpenChannelFromItem(_currentVideo); };
            _btnOpenCurrentChannel.Click += (s, e) => { if (_currentVideo != null) OpenChannelFromItem(_currentVideo); };
            _btnSaveVideo.Click += (s, e) => { if (_currentVideo != null) ToggleSaveVideo(_currentVideo); };
            _btnBackToCatalogFromWatch.Click += (s, e) => ReturnToCatalog();

            _btnCancelLoad.Click += (s, e) =>
            {
                StopProgressiveSession();
                _loadingOverlay.Visible = false;
                _isLoading = false;
                UpdatePlayPauseButton();
                EmitStatus("Carregamento do vídeo cancelado.");
            };
        }

        private void HandleCardActivated(TrackItem item)
        {
            if (item == null) return;
            if (item.IsChannel)
            {
                OpenChannelFromItem(item);
                return;
            }
            if (item.IsPlaylistCard)
            {
                OpenChannelPlaylistItemsAsync(item);
                return;
            }
            PlayVideo(item);
        }

        // ====================================================================
        // SEARCH FILTER CHIPS ("all", "videos", "channels")
        // ====================================================================

        private void ApplySearchChipFilter(string chip)
        {
            _activeSearchChip = chip ?? "all";
            UpdateChipButtonsUI();

            if (_isChannelView)
            {
                ExitChannelViewToSearch();
                return;
            }

            RefreshFilteredSearchCatalog(true);
        }

        private void ToggleHideShorts()
        {
            _config.HideShorts = !_config.HideShorts;
            _config.Save();
            _chipHideShorts.Text = _config.HideShorts ? "🚫 Shorts: Ocultos" : "▶ Shorts: Visíveis";
            StyleChip(_chipHideShorts, _config.HideShorts);
            RefreshCurrentViewShortsFilter();
            EmitStatus(_config.HideShorts ? "🚫 Filtro de Shorts ativo: Shorts removidos do catálogo" : "▶ Shorts agora visíveis no catálogo");
        }

        private void ToggleSponsorBlock()
        {
            _config.AutoSkipSponsors = !_config.AutoSkipSponsors;
            _config.Save();
            _chipToggleSponsorBlock.Text = _config.AutoSkipSponsors ? "🛡️ SponsorBlock: ON" : "🛡️ SponsorBlock: OFF";
            StyleChip(_chipToggleSponsorBlock, _config.AutoSkipSponsors);
            EmitStatus(_config.AutoSkipSponsors ? "🛡️ SponsorBlock ativado: Pulo automático de patrocínios ligado" : "🛡️ SponsorBlock desativado");
        }

        private bool HasValidAuth()
        {
            if (!string.IsNullOrEmpty(_config.CookiesFilePath) && File.Exists(_config.CookiesFilePath)) return true;
            if (!string.IsNullOrEmpty(_config.BrowserForCookies) && _config.BrowserForCookies != "none") return true;
            return false;
        }

        private string GetAccountStatusLabel()
        {
            if (!string.IsNullOrEmpty(_config.CookiesFilePath) && File.Exists(_config.CookiesFilePath))
            {
                return "● Autenticado (Autônomo)";
            }
            if (!string.IsNullOrEmpty(_config.BrowserForCookies) && _config.BrowserForCookies != "none")
            {
                return "● YouTube (" + _config.BrowserForCookies.ToUpper() + ")";
            }
            return "🔑 Conectar Conta Google";
        }

        public void RefreshSidebarAccountStatus()
        {
            if (_lblSidebarAccount != null)
            {
                _lblSidebarAccount.Text = GetAccountStatusLabel();
                _lblSidebarAccount.ForeColor = HasValidAuth() ? Color.FromArgb(0, 212, 130) : Theme.TextDim;
            }
        }

        public void ShowGoogleAuthDialog()
        {
            using (var dlg = new GoogleAuthDialog(_config))
            {
                dlg.AuthChanged += () =>
                {
                    _lblSidebarAccount.Text = GetAccountStatusLabel();
                    _lblSidebarAccount.ForeColor = HasValidAuth() ? Color.FromArgb(0, 212, 130) : Theme.TextDim;
                    _homeVideos.Clear();
                    _subscriptionsVideos.Clear();
                    _historyVideos.Clear();
                    if (_activeNavSection == "home" || _activeNavSection == "subs" || _activeNavSection == "history")
                    {
                        var _ = SwitchToNavSectionAsync(_activeNavSection, true);
                    }
                    EmitStatus("Conta Google atualizada! Feeds recarregados.");
                };
                dlg.ShowDialog(this);
            }
        }

        private List<TrackItem> FilterItemsForDisplay(List<TrackItem> rawItems)
        {
            if (rawItems == null) return new List<TrackItem>();
            if (!_config.HideShorts) return rawItems;

            var filtered = new List<TrackItem>(rawItems.Count);
            for (int i = 0; i < rawItems.Count; i++)
            {
                var item = rawItems[i];
                if (item != null && !item.IsShort)
                {
                    filtered.Add(item);
                }
            }
            return filtered;
        }

        private void RefreshCurrentViewShortsFilter()
        {
            if (_isChannelView)
            {
                _catalogGridCards.SetItems(FilterItemsForDisplay(_channelCurrentItems), false);
            }
            else if (_activeNavSection == "search")
            {
                RefreshFilteredSearchCatalog(false);
            }
            else if (_activeNavSection == "home")
            {
                _catalogGridCards.SetItems(FilterItemsForDisplay(_homeVideos), false);
            }
            else if (_activeNavSection == "subs")
            {
                _catalogGridCards.SetItems(FilterItemsForDisplay(_subscriptionsVideos), false);
            }
            else if (_activeNavSection == "history")
            {
                _catalogGridCards.SetItems(FilterItemsForDisplay(_historyVideos), false);
            }
            else if (_activeNavSection == "you")
            {
                _catalogGridCards.SetItems(FilterItemsForDisplay(_config.SavedVideos), false);
            }
        }

        private void UpdateChipButtonsUI()
        {
            bool showChips = !_isChannelView;
            _topChipsBar.Visible = showChips;
            if (showChips)
            {
                bool isSearch = (_activeNavSection == "search");
                _chipFilterAll.Visible = isSearch;
                _chipFilterVideos.Visible = isSearch;
                _chipFilterChannels.Visible = isSearch;

                if (isSearch)
                {
                    StyleChip(_chipFilterAll, _activeSearchChip == "all");
                    StyleChip(_chipFilterVideos, _activeSearchChip == "videos");
                    StyleChip(_chipFilterChannels, _activeSearchChip == "channels");
                    _chipHideShorts.Location = new Point(244, 6);
                    _chipToggleSponsorBlock.Location = new Point(384, 6);
                }
                else
                {
                    _chipHideShorts.Location = new Point(14, 6);
                    _chipToggleSponsorBlock.Location = new Point(154, 6);
                }

                _chipHideShorts.Text = _config.HideShorts ? "🚫 Shorts: Ocultos" : "▶ Shorts: Visíveis";
                StyleChip(_chipHideShorts, _config.HideShorts);

                _chipToggleSponsorBlock.Text = _config.AutoSkipSponsors ? "🛡️ SponsorBlock: ON" : "🛡️ SponsorBlock: OFF";
                StyleChip(_chipToggleSponsorBlock, _config.AutoSkipSponsors);
            }
        }

        private void StyleChip(Button btn, bool active)
        {
            if (btn == null) return;
            btn.BackColor = active ? TextPrimary : BgCard;
            btn.ForeColor = active ? BgDark : TextPrimary;
        }

        private List<TrackItem> GetFilteredSearchItems()
        {
            if (_searchVideos == null) return new List<TrackItem>();
            if (_activeSearchChip == "videos")
            {
                var vids = _searchVideos.FindAll(x => !x.IsChannel && !x.IsPlaylistCard);
                return _config.HideShorts ? vids.FindAll(x => !x.IsShort) : vids;
            }
            if (_activeSearchChip == "channels")
            {
                var channels = _searchVideos.FindAll(x => x.IsChannel);
                // If YouTube returned videos from channels that didn't have a separate channelRenderer card,
                // synthesize distinct channel cards from the video results so the "Canais" tab always shows them cleanly!
                var seenChannelKeys = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                foreach (var c in channels)
                {
                    if (!string.IsNullOrEmpty(c.ChannelId)) seenChannelKeys.Add(c.ChannelId);
                    if (!string.IsNullOrEmpty(c.Title)) seenChannelKeys.Add(c.Title);
                }

                foreach (var v in _searchVideos)
                {
                    if (v.IsChannel || v.IsPlaylistCard) continue;
                    string chName = (v.Artist ?? "").Trim();
                    string chId = (v.ChannelId ?? "").Trim();
                    if (string.IsNullOrEmpty(chName) || chName == "YouTube") continue;

                    string key = !string.IsNullOrEmpty(chId) ? chId : chName;
                    if (seenChannelKeys.Contains(key) || seenChannelKeys.Contains(chName)) continue;
                    seenChannelKeys.Add(key);
                    seenChannelKeys.Add(chName);

                    channels.Add(new TrackItem
                    {
                        Id = !string.IsNullOrEmpty(chId) ? chId : ("channel:" + chName),
                        Title = chName,
                        Artist = "Canal Oficial • Clique para ver Início, Vídeos e Playlists",
                        Duration = "CANAL",
                        ItemType = "channel",
                        ChannelId = chId,
                        SubscriberCountText = "Canal encontrado nos vídeos da busca"
                    });
                }
                return channels;
            }
            return _config.HideShorts ? _searchVideos.FindAll(x => !x.IsShort) : _searchVideos;
        }

        private void RefreshFilteredSearchCatalog(bool resetScroll)
        {
            var filtered = GetFilteredSearchItems();
            bool hasMore = !string.IsNullOrEmpty(_searchContinuationToken) && _activeSearchChip != "channels";
            _catalogGridCards.HasMoreItems = hasMore;
            _catalogGridCards.SetItems(filtered, resetScroll);

            if (string.IsNullOrEmpty(_searchQueryLabel))
            {
                _lblCatalogHeader.Text = "Pesquise vídeos ou canais na barra superior";
            }
            else if (_activeSearchChip == "channels")
            {
                _lblCatalogHeader.Text = string.Format("📺 Canais encontrados para \"{0}\" ({1} canais — clique em um canal para ver Início, Vídeos e Playlists exclusivos dele)",
                    _searchQueryLabel, filtered.Count);
            }
            else if (_activeSearchChip == "videos")
            {
                _lblCatalogHeader.Text = string.Format("🎬 Vídeos encontrados para \"{0}\" ({1} vídeos)",
                    _searchQueryLabel, filtered.Count);
            }
            else
            {
                _lblCatalogHeader.Text = string.Format("Resultados para \"{0}\" ({1} itens — use os filtros Tudo | Vídeos | Canais no topo)",
                    _searchQueryLabel, filtered.Count);
            }
        }

        // ====================================================================
        // OFFICIAL YOUTUBE CHANNEL PAGE (INÍCIO | VÍDEOS | PLAYLISTS)
        // ====================================================================

        public async void OpenChannelFromItem(TrackItem item)
        {
            if (item == null) return;
            if (_isFullscreen) ToggleFullscreen(false);

            string chId = item.ChannelId;
            string chName = item.IsChannel ? item.Title : item.Artist;
            if (string.IsNullOrWhiteSpace(chName)) chName = "Canal do YouTube";

            _isChannelView = true;
            _isSavedVideosView = false;
            _activeChannelId = chId ?? "";
            _activeChannelName = chName;
            _activeChannelAvatarUrl = item.CustomThumbUrl ?? "";
            _activeChannelSubscribers = item.SubscriberCountText ?? "";
            _openedChannelPlaylist = null;

            _homeVideos.Clear();
            _subscriptionsVideos.Clear();
            _historyVideos.Clear();
            _searchVideos.Clear();
            _openedPlaylistVideos.Clear();
            if (!_watchPagePanel.Visible) _relatedRowCards.SetItems(new List<TrackItem>());
            ThumbnailCache.ClearMemoryCache();
            MemoryOptimizer.TrimMemory();
            ThumbnailCache.IsVisibleCheck = (id) => _catalogGridCards.IsItemVisible(id);

            UpdateChipButtonsUI();
            if (_wpfOverlay != null) _wpfOverlay.SetOverlayActive(false);
            _watchPagePanel.Visible = false;
            _catalogPanel.Visible = true;
            _catalogPanel.BringToFront();

            // Start on "home" (Início) tab just like YouTube!
            await LoadChannelTabAsync("home");
        }

        private void UpdateChannelTabIndicator(string tab)
        {
            _btnChannelTabHome.ForeColor = (tab == "home") ? TextPrimary : TextSecondary;
            _btnChannelTabVideos.ForeColor = (tab == "videos") ? TextPrimary : TextSecondary;
            _btnChannelTabPlaylists.ForeColor = (tab == "playlists" || tab == "playlist_items") ? TextPrimary : TextSecondary;

            Button activeBtn = _btnChannelTabHome;
            if (tab == "videos") activeBtn = _btnChannelTabVideos;
            else if (tab == "playlists" || tab == "playlist_items") activeBtn = _btnChannelTabPlaylists;

            _tabUnderlineIndicator.Left = activeBtn.Left;
            _tabUnderlineIndicator.Width = activeBtn.Width;
        }

        private async Task LoadChannelTabAsync(string tab)
        {
            _activeChannelTab = tab;
            _openedChannelPlaylist = null;
            _btnImportOpenedPlaylist.Visible = false;

            _channelBannerPanel.Visible = true;
            UpdateChannelTabIndicator(tab);

            _lblChannelBannerTitle.Text = _activeChannelName;
            string tabPt = tab == "home" ? "Início (Destaques)" : (tab == "videos" ? "Vídeos Enviados" : "Playlists Criadas");
            _lblChannelBannerSub.Text = "Carregando " + tabPt + " exclusivamente deste canal...";
            _picChannelAvatar.Invalidate();

            EmitStatus(string.Format("📺 Carregando aba '{0}' do canal '{1}'...", tabPt, _activeChannelName));

            ChannelPageData pageData = await _ytService.FetchChannelPageAsync(_activeChannelId, _activeChannelName, tab);

            if (!string.IsNullOrEmpty(pageData.ChannelId))
                _activeChannelId = pageData.ChannelId;
            if (!string.IsNullOrEmpty(pageData.ChannelName))
                _activeChannelName = pageData.ChannelName;
            if (!string.IsNullOrEmpty(pageData.AvatarUrl))
                _activeChannelAvatarUrl = pageData.AvatarUrl;
            if (!string.IsNullOrEmpty(pageData.SubscriberText))
                _activeChannelSubscribers = pageData.SubscriberText;

            _channelContinuationToken = pageData.ContinuationToken ?? "";
            _channelCurrentItems = pageData.Items ?? new List<TrackItem>();

            _lblChannelBannerTitle.Text = _activeChannelName;
            _lblChannelBannerSub.Text = !string.IsNullOrEmpty(_activeChannelSubscribers)
                ? string.Format("{0}  •  Conteúdo 100% oficial de {1}", _activeChannelSubscribers, _activeChannelName)
                : string.Format("Canal Oficial do YouTube  •  Conteúdo exclusivo de {0}", _activeChannelName);
            _picChannelAvatar.Invalidate();

            if (tab == "home")
            {
                _lblCatalogHeader.Text = string.Format("🏠 Início — Envios recentes e destaques exclusivos de {0} ({1} vídeos)",
                    _activeChannelName, _channelCurrentItems.Count);
            }
            else if (tab == "videos")
            {
                _lblCatalogHeader.Text = string.Format("🎬 Todos os vídeos enviados por {0} ({1} carregados — role para baixo para carregar mais)",
                    _activeChannelName, _channelCurrentItems.Count);
            }
            else
            {
                _lblCatalogHeader.Text = string.Format("📂 Playlists oficiais de {0} ({1} encontradas — clique em uma playlist para ver os vídeos)",
                    _activeChannelName, _channelCurrentItems.Count);
            }

            bool hasMore = !string.IsNullOrEmpty(_channelContinuationToken);
            _catalogGridCards.HasMoreItems = hasMore;
            _catalogGridCards.SetItems(_channelCurrentItems, true);

            if (tab != "playlists" && _watchPagePanel.Visible)
            {
                _relatedRowCards.HasMoreItems = hasMore;
                _relatedRowCards.SetItems(_channelCurrentItems, true);
            }
        }

        private async void OpenChannelPlaylistItemsAsync(TrackItem playlistCard)
        {
            if (playlistCard == null || string.IsNullOrEmpty(playlistCard.PlaylistId)) return;

            _activeChannelTab = "playlist_items";
            _openedChannelPlaylist = playlistCard;
            _channelContinuationToken = "";
            UpdateChannelTabIndicator("playlists");

            _lblCatalogHeader.Text = "⏳ Carregando vídeos da playlist \"" + playlistCard.Title + "\"...";
            EmitStatus("📂 Abrindo playlist: " + playlistCard.Title);

            var res = await _ytService.FetchUrlOrPlaylistAsync(playlistCard.PlaylistId, _config, false);
            _openedPlaylistVideos = res.Item1 ?? new List<TrackItem>();

            for (int i = 0; i < _openedPlaylistVideos.Count; i++)
            {
                if (string.IsNullOrEmpty(_openedPlaylistVideos[i].ChannelId))
                    _openedPlaylistVideos[i].ChannelId = _activeChannelId;
            }

            _btnImportOpenedPlaylist.Visible = _openedPlaylistVideos.Count > 0;
            _btnImportOpenedPlaylist.Text = string.Format("📥 Salvar Playlist ({0})", _openedPlaylistVideos.Count);

            _lblCatalogHeader.Text = string.Format("📂 Playlist de {0}: \"{1}\" ({2} vídeos)",
                _activeChannelName, playlistCard.Title, _openedPlaylistVideos.Count);

            _catalogGridCards.HasMoreItems = false;
            _catalogGridCards.SetItems(_openedPlaylistVideos, true);
            if (_watchPagePanel.Visible)
            {
                _relatedRowCards.HasMoreItems = false;
                _relatedRowCards.SetItems(_openedPlaylistVideos, true);
            }

            EmitStatus(string.Format("📂 Playlist '{0}' carregada com {1} vídeos.", playlistCard.Title, _openedPlaylistVideos.Count));
        }

        private void ImportOpenedChannelPlaylistToLibrary()
        {
            if (_openedChannelPlaylist == null || _openedPlaylistVideos == null || _openedPlaylistVideos.Count == 0)
                return;

            var newPl = new UserPlaylist
            {
                Name = _openedChannelPlaylist.Title,
                RemoteUrl = "https://www.youtube.com/playlist?list=" + _openedChannelPlaylist.PlaylistId,
                Tracks = new List<TrackItem>(_openedPlaylistVideos)
            };

            _config.Playlists.Add(newPl);
            _config.Save();

            if (PlaylistImportedToLibrary != null)
            {
                PlaylistImportedToLibrary();
            }

            EmitStatus(string.Format("✅ Playlist '{0}' ({1} faixas) salva na sua biblioteca!", newPl.Name, newPl.Tracks.Count));
            MessageBox.Show(
                this,
                string.Format("A playlist \"{0}\" ({1} itens) foi salva com sucesso nas suas Playlists do LiteTube!",
                    newPl.Name, newPl.Tracks.Count),
                "Playlist Salva",
                MessageBoxButtons.OK,
                MessageBoxIcon.Information
            );
        }

        private void ExitChannelViewToSearch()
        {
            _isChannelView = false;
            _channelBannerPanel.Visible = false;
            _btnImportOpenedPlaylist.Visible = false;
            if (_wpfOverlay != null) _wpfOverlay.SetOverlayActive(false);
            _watchPagePanel.Visible = false;
            _catalogPanel.Visible = true;
            _catalogPanel.BringToFront();
            UpdateChipButtonsUI();
            RefreshFilteredSearchCatalog(false);
        }

        // ====================================================================
        // INFINITE SCROLL (SEARCH & CHANNEL UPLOADS)
        // ====================================================================

        private async Task LoadMoreVideosAsync()
        {
            if (_isLoadingMore || _isSavedVideosView) return;

            // 1. Channel View Infinite Scroll
            if (_isChannelView)
            {
                if (string.IsNullOrEmpty(_channelContinuationToken))
                {
                    _catalogGridCards.HasMoreItems = false;
                    _relatedRowCards.HasMoreItems = false;
                    return;
                }

                _isLoadingMore = true;
                _catalogGridCards.IsLoadingMore = true;
                _relatedRowCards.IsLoadingMore = true;
                EmitStatus("⏳ Carregando mais vídeos do canal...");

                try
                {
                    var nextBatch = await _ytService.FetchChannelContinuationAsync(_channelContinuationToken, _activeChannelId, _activeChannelName);
                    _channelContinuationToken = nextBatch.ContinuationToken ?? "";
                    bool hasNext = !string.IsNullOrEmpty(nextBatch.ContinuationToken);
                    _catalogGridCards.HasMoreItems = hasNext;
                    _relatedRowCards.HasMoreItems = hasNext;

                    if (nextBatch.Items != null && nextBatch.Items.Count > 0)
                    {
                        _channelCurrentItems.AddRange(nextBatch.Items);
                        int addedGrid = _catalogGridCards.AppendItems(nextBatch.Items);
                        if (_watchPagePanel.Visible) _relatedRowCards.AppendItems(nextBatch.Items);
                        _lblCatalogHeader.Text = string.Format("🎬 Vídeos enviados por {0} ({1} vídeos carregados)",
                            _activeChannelName, _channelCurrentItems.Count);
                        EmitStatus(string.Format("➕ Mais {0} vídeos de {1} carregados!", addedGrid, _activeChannelName));
                    }
                }
                catch { }
                finally
                {
                    _isLoadingMore = false;
                    _catalogGridCards.IsLoadingMore = false;
                    _relatedRowCards.IsLoadingMore = false;
                }
                return;
            }

            // 2. Search Results Infinite Scroll
            if (_activeNavSection == "search" || !string.IsNullOrEmpty(_searchContinuationToken))
            {
                if (string.IsNullOrEmpty(_searchContinuationToken))
                {
                    _catalogGridCards.HasMoreItems = false;
                    _relatedRowCards.HasMoreItems = false;
                    return;
                }

                _isLoadingMore = true;
                _catalogGridCards.IsLoadingMore = true;
                _relatedRowCards.IsLoadingMore = true;
                EmitStatus("⏳ Carregando mais resultados...");

                try
                {
                    var nextBatch = await _ytService.FetchSearchContinuationAsync(_searchContinuationToken, true);
                    _searchContinuationToken = nextBatch.ContinuationToken ?? "";
                    bool hasNext = !string.IsNullOrEmpty(nextBatch.ContinuationToken);
                    _catalogGridCards.HasMoreItems = hasNext;
                    _relatedRowCards.HasMoreItems = hasNext;

                    if (nextBatch.Items != null && nextBatch.Items.Count > 0)
                    {
                        _searchVideos.AddRange(nextBatch.Items);
                        if (_searchVideos.Count > 350)
                        {
                            _searchVideos.RemoveRange(0, _searchVideos.Count - 350);
                        }

                        var itemsToAppend = nextBatch.Items;
                        if (_activeSearchChip == "videos")
                            itemsToAppend = nextBatch.Items.FindAll(x => !x.IsChannel && !x.IsPlaylistCard);
                        else if (_activeSearchChip == "channels")
                            itemsToAppend = nextBatch.Items.FindAll(x => x.IsChannel);

                        int addedGrid = _catalogGridCards.AppendItems(itemsToAppend);
                        if (_watchPagePanel.Visible) _relatedRowCards.AppendItems(nextBatch.Items.FindAll(x => !x.IsChannel));
                        EmitStatus(string.Format("➕ Mais {0} resultados carregados!", addedGrid));
                    }
                }
                catch { }
                finally
                {
                    _isLoadingMore = false;
                    _catalogGridCards.IsLoadingMore = false;
                    _relatedRowCards.IsLoadingMore = false;
                }
                return;
            }

            // 3. Home / Subscriptions / History Feeds Infinite Scroll
            string feedUrl = null;
            List<TrackItem> targetList = null;
            string sectionLabel = "";

            if (_activeNavSection == "home")
            {
                feedUrl = "https://www.youtube.com/";
                targetList = _homeVideos;
                sectionLabel = "recomendados";
            }
            else if (_activeNavSection == "subs")
            {
                feedUrl = "https://www.youtube.com/feed/subscriptions";
                targetList = _subscriptionsVideos;
                sectionLabel = "inscrições";
            }
            else if (_activeNavSection == "history")
            {
                feedUrl = "https://www.youtube.com/feed/history";
                targetList = _historyVideos;
                sectionLabel = "histórico";
            }

            if (!string.IsNullOrEmpty(feedUrl) && targetList != null)
            {
                _isLoadingMore = true;
                _catalogGridCards.IsLoadingMore = true;
                _relatedRowCards.IsLoadingMore = true;
                EmitStatus(string.Format("⏳ Carregando mais vídeos de {0}...", sectionLabel));

                try
                {
                    int startIndex = targetList.Count + 1;
                    var res = await _ytService.FetchUrlOrPlaylistAsync(feedUrl, _config, true, startIndex, 50);

                    if (res.Item1 != null && res.Item1.Count > 0)
                    {
                        targetList.AddRange(res.Item1);
                        var filtered = FilterItemsForDisplay(res.Item1);
                        int addedGrid = _catalogGridCards.AppendItems(filtered);
                        if (_watchPagePanel.Visible) _relatedRowCards.AppendItems(filtered);
                        _catalogGridCards.HasMoreItems = (res.Item1.Count >= 15);
                        if (_watchPagePanel.Visible) _relatedRowCards.HasMoreItems = (res.Item1.Count >= 15);
                        EmitStatus(string.Format("➕ Mais {0} vídeos adicionados ao feed ({1} no total)!", addedGrid, targetList.Count));
                    }
                    else
                    {
                        _catalogGridCards.HasMoreItems = false;
                        _relatedRowCards.HasMoreItems = false;
                        EmitStatus("Fim dos vídeos disponíveis para este feed.");
                    }
                }
                catch { }
                finally
                {
                    _isLoadingMore = false;
                    _catalogGridCards.IsLoadingMore = false;
                    _relatedRowCards.IsLoadingMore = false;
                }
            }
        }

        private Button CreateYouTubeSidebarButton(string text, ref int y)
        {
            var btn = new ModernButton
            {
                Text = text,
                Location = new Point(8, y),
                Size = new Size(189, 40),
                FlatStyle = FlatStyle.Flat,
                BackColor = Color.Transparent,
                ForeColor = TextSecondary,
                TextAlign = ContentAlignment.MiddleLeft,
                Font = new Font("Segoe UI Semibold", 9.5f),
                CornerRadius = 12,
                ShowActiveIndicator = true,
                ActiveAccentColor = YouTubeRed,
                ActiveColor = Theme.SurfaceHigh,
                Cursor = Cursors.Hand
            };
            _sidebar.Controls.Add(btn);
            y += 44;
            return btn;
        }

        private void UpdateSidebarSelection()
        {
            StyleSidebarNavButton(_navHome, _activeNavSection == "home");
            StyleSidebarNavButton(_navSubscriptions, _activeNavSection == "subs");
            StyleSidebarNavButton(_navYou, _activeNavSection == "you");
            StyleSidebarNavButton(_navHistory, _activeNavSection == "history");

            if (_navBackToPlayer != null)
            {
                _navBackToPlayer.Visible = (_currentVideo != null);
                StyleSidebarNavButton(_navBackToPlayer, _watchPagePanel != null && _watchPagePanel.Visible);
            }
        }

        private void StyleSidebarNavButton(Button btn, bool active)
        {
            var mb = btn as ModernButton;
            if (mb != null)
            {
                mb.IsActive = active;
                mb.ForeColor = active ? TextPrimary : TextSecondary;
                mb.Font = new Font("Segoe UI Semibold", 9.5f, active ? FontStyle.Bold : FontStyle.Regular);
                mb.Invalidate();
            }
        }

        public async Task SwitchToNavSectionAsync(string section, bool forceRefresh = false)
        {
            if (_isFullscreen) ToggleFullscreen(false);

            if (_watchPagePanel != null && _watchPagePanel.Visible)
            {
                if (_wpfOverlay != null) _wpfOverlay.SetOverlayActive(false);
                StopVideo();
                if (_relatedRowCards != null) _relatedRowCards.ClearItems(false);
            }

            _activeNavSection = section ?? "home";
            _isChannelView = false;
            _isSavedVideosView = false;
            _channelBannerPanel.Visible = false;

            if (_wpfOverlay != null) _wpfOverlay.SetOverlayActive(false);
            if (_wpfOverlay != null) _wpfOverlay.SetOverlayActive(false);
            _catalogPanel.Visible = true;
            _watchPagePanel.Visible = false;
            _catalogPanel.BringToFront();

            UpdateSidebarSelection();
            UpdateChipButtonsUI();

            if (_activeNavSection == "home")
            {
                _subscriptionsVideos.Clear();
                _historyVideos.Clear();
                _channelCurrentItems.Clear();
                _searchVideos.Clear();
                _openedPlaylistVideos.Clear();
                if (!_watchPagePanel.Visible) _relatedRowCards.SetItems(new List<TrackItem>());

                _lblCatalogHeader.Text = "🏠 Início • Vídeos recomendados para você";
                if (!forceRefresh && _homeVideos.Count > 0)
                {
                    _catalogGridCards.HasMoreItems = (_homeVideos.Count >= 15);
                    _catalogGridCards.SetItems(FilterItemsForDisplay(_homeVideos), false);
                    return;
                }

                ThumbnailCache.ClearMemoryCache();
                MemoryOptimizer.TrimMemory();

                string bName = GetAccountStatusLabel();

                _lblCatalogHeader.Text = string.Format("⏳ Carregando seus vídeos recomendados do YouTube ({0})...", bName);
                _catalogGridCards.IsLoadingMore = true;
                EmitStatus("Sincronizando página inicial / recomendados...");

                var res = await _ytService.FetchUrlOrPlaylistAsync("https://www.youtube.com/", _config, true, 1, 60);
                _catalogGridCards.IsLoadingMore = false;

                if (res.Item1 != null && res.Item1.Count > 0)
                {
                    _homeVideos = res.Item1;
                    _lblCatalogHeader.Text = string.Format("🏠 Início • {0} vídeos recomendados para você ({1})",
                        _homeVideos.Count, bName);
                    _catalogGridCards.HasMoreItems = (_homeVideos.Count >= 15);
                    _catalogGridCards.SetItems(FilterItemsForDisplay(_homeVideos), true);
                    EmitStatus(string.Format("🏠 {0} vídeos recomendados carregados!", _homeVideos.Count));
                }
                else
                {
                    _lblCatalogHeader.Text = "⚠️ Não foi possível carregar os recomendados. " + (res.Item2 ?? "");
                    EmitStatus(res.Item2 ?? "Erro ao carregar recomendados.");
                    if (res.Item2 != null && res.Item2.Contains("bloqueia os cookies"))
                    {
                        MessageBox.Show(this, res.Item2, "Conta YouTube", MessageBoxButtons.OK, MessageBoxIcon.Information);
                    }
                }
            }
            else if (_activeNavSection == "subs")
            {
                _homeVideos.Clear();
                _historyVideos.Clear();
                _channelCurrentItems.Clear();
                _searchVideos.Clear();
                _openedPlaylistVideos.Clear();
                if (!_watchPagePanel.Visible) _relatedRowCards.SetItems(new List<TrackItem>());

                _lblCatalogHeader.Text = "🔔 Inscrições • Envios mais recentes dos seus canais";
                if (!forceRefresh && _subscriptionsVideos.Count > 0)
                {
                    _catalogGridCards.HasMoreItems = (_subscriptionsVideos.Count >= 15);
                    _catalogGridCards.SetItems(FilterItemsForDisplay(_subscriptionsVideos), false);
                    return;
                }

                ThumbnailCache.ClearMemoryCache();
                MemoryOptimizer.TrimMemory();

                string bName = GetAccountStatusLabel();

                _lblCatalogHeader.Text = string.Format("⏳ Carregando inscrições da sua conta ({0})...", bName);
                _catalogGridCards.IsLoadingMore = true;
                EmitStatus("Sincronizando feed de inscrições do YouTube...");

                var res = await _ytService.FetchUrlOrPlaylistAsync("https://www.youtube.com/feed/subscriptions", _config, true, 1, 60);
                _catalogGridCards.IsLoadingMore = false;

                if (res.Item1 != null && res.Item1.Count > 0)
                {
                    _subscriptionsVideos = res.Item1;
                    _lblCatalogHeader.Text = string.Format("🔔 Inscrições • {0} vídeos recentes dos seus canais ({1})",
                        _subscriptionsVideos.Count, bName);
                    _catalogGridCards.HasMoreItems = (_subscriptionsVideos.Count >= 15);
                    _catalogGridCards.SetItems(FilterItemsForDisplay(_subscriptionsVideos), true);
                    EmitStatus(string.Format("🔔 {0} vídeos de canais inscritos carregados!", _subscriptionsVideos.Count));
                }
                else
                {
                    _lblCatalogHeader.Text = "⚠️ Nenhuma inscrição encontrada. " + (res.Item2 ?? "");
                    EmitStatus(res.Item2 ?? "Erro ao carregar inscrições.");
                }
            }
            else if (_activeNavSection == "you")
            {
                _isSavedVideosView = true;
                _homeVideos.Clear();
                _subscriptionsVideos.Clear();
                _historyVideos.Clear();
                _channelCurrentItems.Clear();
                _searchVideos.Clear();
                _openedPlaylistVideos.Clear();
                if (!_watchPagePanel.Visible) _relatedRowCards.SetItems(new List<TrackItem>());

                _lblCatalogHeader.Text = string.Format("👤 Você • Seus Vídeos Salvos ({0} vídeos)", _config.SavedVideos.Count);
                _catalogGridCards.HasMoreItems = false;
                _catalogGridCards.SetItems(FilterItemsForDisplay(_config.SavedVideos), false);
            }
            else if (_activeNavSection == "history")
            {
                _homeVideos.Clear();
                _subscriptionsVideos.Clear();
                _channelCurrentItems.Clear();
                _searchVideos.Clear();
                _openedPlaylistVideos.Clear();
                if (!_watchPagePanel.Visible) _relatedRowCards.SetItems(new List<TrackItem>());

                _lblCatalogHeader.Text = "🕒 Histórico • Vídeos assistidos recentemente";
                if (!forceRefresh && _historyVideos.Count > 0)
                {
                    _catalogGridCards.HasMoreItems = (_historyVideos.Count >= 15);
                    _catalogGridCards.SetItems(FilterItemsForDisplay(_historyVideos), false);
                    return;
                }

                ThumbnailCache.ClearMemoryCache();
                MemoryOptimizer.TrimMemory();

                string bName = GetAccountStatusLabel();

                _lblCatalogHeader.Text = string.Format("⏳ Carregando histórico da sua conta ({0})...", bName);
                _catalogGridCards.IsLoadingMore = true;
                EmitStatus("Sincronizando histórico do YouTube...");

                var res = await _ytService.FetchUrlOrPlaylistAsync("https://www.youtube.com/feed/history", _config, true, 1, 60);
                _catalogGridCards.IsLoadingMore = false;

                if (res.Item1 != null && res.Item1.Count > 0)
                {
                    _historyVideos = res.Item1;
                    _lblCatalogHeader.Text = string.Format("🕒 Histórico • {0} vídeos recentes ({1})",
                        _historyVideos.Count, bName);
                    _catalogGridCards.HasMoreItems = (_historyVideos.Count >= 15);
                    _catalogGridCards.SetItems(FilterItemsForDisplay(_historyVideos), true);
                    EmitStatus(string.Format("🕒 {0} vídeos do histórico carregados!", _historyVideos.Count));
                }
                else
                {
                    _lblCatalogHeader.Text = "⚠️ Nenhum vídeo no histórico encontrado. " + (res.Item2 ?? "");
                    EmitStatus(res.Item2 ?? "Erro ao carregar histórico.");
                }
            }
        }

        public async Task RefreshCurrentPageAsync()
        {
            if (_watchPagePanel != null && _watchPagePanel.Visible)
            {
                if (_currentVideo != null)
                {
                    EmitStatus(string.Format("🔄 Recarregando vídeos relacionados para '{0}'...", _currentVideo.Title));
                    ThumbnailCache.ClearMemoryCache();
                    MemoryOptimizer.TrimMemory();
                    var related = await _ytService.FetchRelatedVideosAsync(_currentVideo.Id, _config, 35);
                    if (related != null && related.Count > 0)
                    {
                        var filtered = FilterItemsForDisplay(related.FindAll(x => x.Id != _currentVideo.Id));
                        _relatedRowCards.ScrollToTop();
                        _relatedRowCards.SetItems(filtered, true);
                        _relatedRowCards.PlayingVideoId = _currentVideo.Id;
                        ThumbnailCache.IsVisibleCheck = (id) => _relatedRowCards.IsItemVisible(id);
                        _relatedRowCards.PruneOffscreenResources();
                        EmitStatus(string.Format("🔄 {0} próximos vídeos recarregados (F5)!", filtered.Count));
                    }
                    else
                    {
                        EmitStatus("🔄 Página do vídeo atualizada (F5)!");
                    }
                }
                return;
            }

            if (_isChannelView && !string.IsNullOrEmpty(_activeChannelId))
            {
                EmitStatus(string.Format("🔄 Recarregando canal {0}...", _activeChannelName));
                _channelCurrentItems.Clear();
                _channelContinuationToken = "";
                _catalogGridCards.ScrollToTop();
                _catalogGridCards.SetItems(new List<TrackItem>(), true);
                ThumbnailCache.ClearMemoryCache();
                MemoryOptimizer.TrimMemory();
                await LoadChannelTabAsync(_activeChannelTab ?? "videos");
                EmitStatus(string.Format("🔄 Canal {0} recarregado (F5)!", _activeChannelName));
                return;
            }

            if (_activeNavSection == "search" && !string.IsNullOrWhiteSpace(_searchQueryLabel))
            {
                EmitStatus("🔄 Recarregando busca...");
                _searchVideos.Clear();
                _searchContinuationToken = "";
                _catalogGridCards.ScrollToTop();
                _catalogGridCards.SetItems(new List<TrackItem>(), true);
                ThumbnailCache.ClearMemoryCache();
                MemoryOptimizer.TrimMemory();
                var searchPage = await _ytService.SearchPageAsync(_searchQueryLabel, "all", _config);
                SetSearchResults(searchPage, _searchQueryLabel);
                EmitStatus("🔄 Busca recarregada (F5)!");
                return;
            }

            if (_activeNavSection == "home" || _activeNavSection == "subs" || _activeNavSection == "history")
            {
                // Limpa os vídeos da aba ativa (inclusive os que foram acumulados no scroll infinito)
                if (_activeNavSection == "home") _homeVideos.Clear();
                else if (_activeNavSection == "subs") _subscriptionsVideos.Clear();
                else if (_activeNavSection == "history") _historyVideos.Clear();

                // Volta para o topo e limpa os cards do grid imediatamente
                _catalogGridCards.ScrollToTop();
                _catalogGridCards.SetItems(new List<TrackItem>(), true);

                ThumbnailCache.ClearMemoryCache();
                MemoryOptimizer.TrimMemory();

                await SwitchToNavSectionAsync(_activeNavSection, forceRefresh: true);
                EmitStatus("🔄 Página recarregada (F5)!");
                return;
            }

            if (_activeNavSection == "you")
            {
                _catalogGridCards.ScrollToTop();
                ThumbnailCache.ClearMemoryCache();
                MemoryOptimizer.TrimMemory();
                _catalogGridCards.SetItems(FilterItemsForDisplay(_config.SavedVideos), true);
                EmitStatus("🔄 Vídeos Salvos recarregados (F5)!");
                return;
            }
        }

        public void ExitWatchPage()
        {
            ReturnToCatalog();
        }

        private void ReturnToCatalog()
        {
            if (_wpfOverlay != null) _wpfOverlay.SetOverlayActive(false);

            // 1. Para o player e finaliza streaming
            StopVideo();

            // 2. Limpa completamente os cards de recomendação e miniaturas da barra lateral
            if (_relatedRowCards != null)
            {
                _relatedRowCards.ClearItems(false);
            }

            // 3. Libera os metadados do vídeo anterior
            _currentVideo = null;
            if (_lblVideoTitle != null) _lblVideoTitle.Text = "";
            if (_lblVideoChannel != null) _lblVideoChannel.Text = "";
            if (_lblVideoSubInfo != null) _lblVideoSubInfo.Text = "";
            if (_picWatchChannelAvatar != null && _picWatchChannelAvatar.Image != null)
            {
                _picWatchChannelAvatar.Image.Dispose();
                _picWatchChannelAvatar.Image = null;
            }
            _currentSponsorSegments = new List<SponsorSegment>();
            _lastSkippedSponsorUuid = null;
            if (_wpfOverlay != null) _wpfOverlay.SetSponsorSegments(_currentSponsorSegments);

            // 4. Alterna visibilidade para o catálogo
            _watchPagePanel.Visible = false;
            _catalogPanel.Visible = true;
            _catalogPanel.BringToFront();
            _sidebar.Visible = true;
            UpdateSidebarSelection();
            UpdateChipButtonsUI();

            // 5. Restaura instantaneamente o catálogo inicial com o scroll preservado
            if (_catalogGridCards != null)
            {
                _catalogGridCards.ResumeAndAttach();
                ThumbnailCache.IsVisibleCheck = (id) => _catalogGridCards.IsItemVisible(id);
                _catalogGridCards.PruneOffscreenResources();
            }

            // 6. Limpeza e trim de memória
            ThumbnailCache.ClearMemoryCache();
            MemoryOptimizer.TrimMemory();
            GC.Collect(2, GCCollectionMode.Optimized);
        }

        private void ShowWatchView()
        {
            // Suspende e descarrega a grade do catálogo inicial para liberar RAM e texturas GPU
            if (_catalogGridCards != null && !_catalogGridCards.IsSuspended)
            {
                _catalogGridCards.SuspendAndDetach();
            }

            _catalogPanel.Visible = false;
            _watchPagePanel.Visible = true;
            _watchPagePanel.BringToFront();

            _topChipsBar.Visible = false;
            _sidebar.Visible = !_isFullscreen && !_isTheaterMode;
            UpdateSidebarSelection();

            if (_wpfOverlay != null)
            {
                _wpfOverlay.UpdateAudioVisuals(_config.VideoVolume, _config.IsVideoMuted);
                _wpfOverlay.UpdateQualityAndSpeedVisuals(_config.VideoQualityHeight, _player != null ? _player.Speed : 1.0);
                _wpfOverlay.SetOverlayActive(true);
            }

            ThumbnailCache.ClearMemoryCache();
            ThumbnailCache.IsVisibleCheck = (id) => _relatedRowCards.IsItemVisible(id);
            _relatedRowCards.PruneOffscreenResources();

            MemoryOptimizer.TrimMemory();
        }

        public void SuspendForBackground()
        {
            if (_wpfOverlay != null) _wpfOverlay.SetOverlayActive(false);
            PauseIfPlaying();

            if (_catalogGridCards != null && !_catalogGridCards.IsSuspended)
            {
                _catalogGridCards.SuspendAndDetach();
            }
            if (_relatedRowCards != null && !_relatedRowCards.IsSuspended)
            {
                _relatedRowCards.SuspendAndDetach();
            }

            ThumbnailCache.ClearMemoryCache();
            MemoryOptimizer.TrimMemory();
            GC.Collect(2, GCCollectionMode.Optimized);
        }

        public void ResumeFromBackground()
        {
            if (!_initialHomeLoaded)
            {
                _initialHomeLoaded = true;
                var _ = SwitchToNavSectionAsync("home");
            }

            if (_watchPagePanel != null && _watchPagePanel.Visible)
            {
                if (_relatedRowCards != null && _relatedRowCards.IsSuspended)
                {
                    _relatedRowCards.ResumeAndAttach();
                }
                if (_wpfOverlay != null) _wpfOverlay.SetOverlayActive(true);
                ThumbnailCache.IsVisibleCheck = (id) => _relatedRowCards.IsItemVisible(id);
                _relatedRowCards.PruneOffscreenResources();
            }
            else
            {
                if (_wpfOverlay != null) _wpfOverlay.SetOverlayActive(false);
                if (_catalogGridCards != null && _catalogGridCards.IsSuspended)
                {
                    _catalogGridCards.ResumeAndAttach();
                }
                ThumbnailCache.IsVisibleCheck = (id) => _catalogGridCards.IsItemVisible(id);
                _catalogGridCards.PruneOffscreenResources();
            }

            UpdateVideoAudioUI();
        }

        public void SetSearchResults(SearchResultPage page, string queryLabel)
        {
            _initialHomeLoaded = true;
            if (_isFullscreen) ToggleFullscreen(false);

            _activeNavSection = "search";
            _isChannelView = false;
            _isSavedVideosView = false;
            _channelBannerPanel.Visible = false;
            _activeSearchChip = "all";

            if (_wpfOverlay != null) _wpfOverlay.SetOverlayActive(false);
            _catalogPanel.Visible = true;
            _watchPagePanel.Visible = false;
            _catalogPanel.BringToFront();

            _homeVideos.Clear();
            _subscriptionsVideos.Clear();
            _historyVideos.Clear();
            _channelCurrentItems.Clear();
            _openedPlaylistVideos.Clear();
            if (!_watchPagePanel.Visible) _relatedRowCards.SetItems(new List<TrackItem>());
            ThumbnailCache.ClearMemoryCache();
            MemoryOptimizer.TrimMemory();
            ThumbnailCache.IsVisibleCheck = (id) => _catalogGridCards.IsItemVisible(id);

            _searchQueryLabel = queryLabel ?? "";
            _searchVideos = (page != null && page.Items != null) ? page.Items : new List<TrackItem>();
            _searchContinuationToken = (page != null) ? (page.ContinuationToken ?? "") : "";

            UpdateSidebarSelection();
            UpdateChipButtonsUI();
            RefreshFilteredSearchCatalog(true);

            EmitStatus(string.Format("🎬 {0} resultados encontrados para '{1}'.", _searchVideos.Count, _searchQueryLabel));
        }

        public void SetSearchResults(List<TrackItem> videos, string queryLabel)
        {
            var page = new SearchResultPage { Items = videos ?? new List<TrackItem>(), Query = queryLabel ?? "" };
            SetSearchResults(page, queryLabel);
        }

        private void ToggleSaveVideo(TrackItem video)
        {
            if (video == null || video.IsChannel || video.IsPlaylistCard) return;
            bool saved = _config.ToggleSavedVideo(video);
            UpdateSavedVideosCount();
            UpdateSaveVideoButton();
            _catalogGridCards.Invalidate();
            _relatedRowCards.Invalidate();
            EmitStatus((saved ? "📌 Vídeo salvo: " : "Removido dos salvos: ") + video.Title);
        }

        private void UpdateSavedVideosCount()
        {
            if (_navYou != null)
            {
                _navYou.Text = string.Format("👤   Você ({0})", _config.SavedVideos.Count);
            }
        }

        private void UpdateSaveVideoButton()
        {
            bool isSaved = _currentVideo != null && _config.IsSavedVideo(_currentVideo.Id);
            _btnSaveVideo.Text = isSaved ? "\u2713 Salvo" : "Salvar";
            var mb = _btnSaveVideo as ModernButton;
            if (mb != null)
            {
                mb.BackColor = isSaved ? Theme.Blue : Theme.SurfaceHigh;
                mb.ForeColor = isSaved ? Color.White : TextPrimary;
                mb.BorderColor = isSaved ? Color.Empty : Theme.Border;
                mb.Invalidate();
            }
        }

        public async void PlayVideo(TrackItem video)
        {
            if (video == null || string.IsNullOrEmpty(video.Id)) return;
            Logger.Info("WATCH", string.Format("PlayVideo chamado: Id={0}, Title='{1}', Dur={2:0.0}s, Res={3}p",
                video.Id, video.Title, video.DurationSeconds, _config.VideoQualityHeight));

            if (video.IsChannel)
            {
                OpenChannelFromItem(video);
                return;
            }
            if (video.IsPlaylistCard)
            {
                OpenChannelPlaylistItemsAsync(video);
                return;
            }

            ShowWatchView();
            StopProgressiveSession();

            ThumbnailCache.EnforceDiskLimitMB(_config.MaxThumbCacheSizeMB);

            if (_config.AutoPauseMusicWhenVideoPlays && _musicEngine != null && _musicEngine.IsPlaying)
            {
                _musicEngine.PauseIfPlaying();
            }

            _currentVideo = video;
            _currentSponsorSegments = new List<SponsorSegment>();
            _lastSkippedSponsorUuid = null;
            if (_wpfOverlay != null)
            {
                _wpfOverlay.SetSponsorSegments(_currentSponsorSegments);
                _wpfOverlay.UpdatePlaybackState(false, 0.0, 0.0, video.DurationSeconds);
            }
            _catalogGridCards.PlayingVideoId = video.Id;
            _relatedRowCards.PlayingVideoId = video.Id;

            // Load YouTube Related / Recommended Videos in Watch Sidebar
            bool isExistingPlaylistTrack = (_openedPlaylistVideos != null && _openedPlaylistVideos.Count > 0 &&
                                            _openedPlaylistVideos.Exists(x => x.Id == video.Id));
            if (!isExistingPlaylistTrack)
            {
                _relatedRowCards.SetItems(new List<TrackItem>(), true);
                LoadRelatedVideosForPlayingVideoAsync(video.Id);
            }

            if (_player != null)
            {
                _player.Stop();
            }

            _isPlaying = false;
            _isLoading = true;
            UpdatePlayPauseButton();
            UpdateSaveVideoButton();

            _lblVideoTitle.Text = video.Title;
            _lblVideoChannel.Text = !string.IsNullOrEmpty(video.Artist) ? video.Artist : "Canal do YouTube";
            _lblVideoSubInfo.Text = string.Format("Qualidade {0}p  •  Volume: {1}%",
                _config.VideoQualityHeight, (int)Math.Round(_config.VideoVolume * 100));
            _picWatchChannelAvatar.Invalidate();

            CenterLoadingOverlay();
            _lblLoadingTitle.Text = video.Title;
            _lblLoadingProgress.Text = "Conectando ao YouTube (FlyleafLib Direct3D 11)...";
            _loadingOverlay.Visible = true;
            _loadingOverlay.BringToFront();

            string baseDir = AppDomain.CurrentDomain.BaseDirectory;
            string binDir = Path.Combine(baseDir, "bin");

            var session = new ProgressiveVideoSession(video, _config.VideoQualityHeight, binDir, _config.BrowserForCookies, _config.CookiesFilePath);
            _activeProgressiveSession = session;

            using (var cts = new System.Threading.CancellationTokenSource(TimeSpan.FromSeconds(30)))
            {
                var streamUrls = await session.ResolveStreamUrlsAsync(cts.Token);
                if (_activeProgressiveSession != session) return;

                _loadingOverlay.Visible = false;
                _isLoading = false;

                if (streamUrls == null || string.IsNullOrEmpty(streamUrls[0]))
                {
                    UpdatePlayPauseButton();
                    string errMsg = !string.IsNullOrEmpty(session.LastResolveError)
                        ? session.LastResolveError
                        : "Não foi possível obter os fluxos de streaming deste vídeo.";
                    _lblBufferBadge.Text = "❌ " + errMsg;
                    _lblLoadingProgress.Text = "❌ " + errMsg;
                    EmitStatus("❌ " + errMsg);
                    return;
                }

                string vUrl = streamUrls[0];
                string aUrl = streamUrls.Length > 1 ? streamUrls[1] : null;
                string codec = streamUrls.Length > 2 ? streamUrls[2] : "Direct";

                ApplyVideoAudioSettings();
                await PlayStreamAsync(vUrl, aUrl, video.DurationSeconds, 0.0);

                _isPlaying = true;
                UpdatePlayPauseButton();
                _lblBufferBadge.Text = string.Format("▶ Flyleaf D3D11 ({0})", codec);
                _lblVideoSubInfo.Text = string.Format("Qualidade {0}p ({1})  •  Flyleaf Direct3D 11  •  Volume: {2}%",
                    _config.VideoQualityHeight, codec, (int)Math.Round(_config.VideoVolume * 100));
                EmitStatus(string.Format("▶ Assistindo ({0}p • Flyleaf): {1}", _config.VideoQualityHeight, video.Title));

                if (_config.EnableSponsorBlock)
                {
                    var _ = Task.Run(async () =>
                    {
                        try
                        {
                            var segs = await SponsorBlockService.GetVideoSegmentsAsync(video.Id);
                            SafeInvoke(() =>
                            {
                                if (_currentVideo != null && _currentVideo.Id == video.Id && segs != null)
                                {
                                    _currentSponsorSegments = segs;
                                    if (_wpfOverlay != null)
                                    {
                                        _wpfOverlay.SetSponsorSegments(_currentSponsorSegments);
                                    }
                                    if (_currentSponsorSegments.Count > 0)
                                    {
                                        _lblBufferBadge.Text = string.Format("🛡️ {0} trecho(s) mapeados (SponsorBlock)", _currentSponsorSegments.Count);
                                        Logger.Info("SPONSORBLOCK", string.Format("SponsorBlock: {0} trechos carregados para {1}", _currentSponsorSegments.Count, video.Title));
                                    }
                                }
                            });
                        }
                        catch { }
                    });
                }
            }
        }

        private void StopProgressiveSession()
        {
            if (_activeProgressiveSession != null)
            {
                _activeProgressiveSession.Dispose();
                _activeProgressiveSession = null;
            }
        }

        private void LoadRelatedVideosForPlayingVideoAsync(string videoId)
        {
            if (string.IsNullOrWhiteSpace(videoId)) return;
            string targetId = videoId;

            Task.Run(async () =>
            {
                try
                {
                    var related = await _ytService.FetchRelatedVideosAsync(targetId, _config, 35);
                    SafeInvoke(() =>
                    {
                        if (_currentVideo != null && _currentVideo.Id == targetId && _watchPagePanel != null && _watchPagePanel.Visible)
                        {
                            var filtered = FilterItemsForDisplay(related);
                            _relatedRowCards.ScrollToTop();
                            _relatedRowCards.SetItems(filtered, true);
                            _relatedRowCards.PlayingVideoId = targetId;
                            ThumbnailCache.IsVisibleCheck = (id) => _relatedRowCards.IsItemVisible(id);
                            _relatedRowCards.PruneOffscreenResources();
                        }
                    });
                }
                catch (Exception ex)
                {
                    Logger.Warn("WATCH", "Falha ao carregar próximos vídeos: " + ex.Message);
                }
            });
        }

        // ====================================================================
        // THEATER MODE, FULLSCREEN & SHORTCUTS
        // ====================================================================

        public void ToggleTheaterMode(bool enableTheater)
        {
            if (_isFullscreen && enableTheater)
            {
                ToggleFullscreen(false);
            }

            _isTheaterMode = enableTheater;
            _rightRelatedPanel.Visible = !_isTheaterMode && !_isFullscreen;
            _sidebar.Visible = !_isTheaterMode && !_isFullscreen;

            if (_wpfOverlay != null) _wpfOverlay.SyncOverlayBounds();

            EmitStatus(_isTheaterMode
                ? "🎬 Modo Teatro ativado (barra lateral oculta). Pressione 'T' para restaurar."
                : "🎬 Modo Padrão restaurado.");
        }

        public void ToggleFullscreen(bool enableFullscreen)
        {
            if (_isFullscreen == enableFullscreen) return;

            _isFullscreen = enableFullscreen;
            _lastMouseActivityUtc = DateTime.UtcNow;

            if (_isFullscreen)
            {
                if (!_watchPagePanel.Visible)
                {
                    ShowWatchView();
                }

                _prevWindowState = _parentForm.WindowState;
                _prevBorderStyle = _parentForm.FormBorderStyle;

                _sidebar.Visible = false;
                _rightRelatedPanel.Visible = false;
                _watchMetaPanel.Visible = false;
                _playerStagePanel.Padding = Padding.Empty;

                if (_parentForm.WindowState == FormWindowState.Maximized)
                {
                    _parentForm.WindowState = FormWindowState.Normal;
                }
                _parentForm.FormBorderStyle = FormBorderStyle.None;
                _parentForm.WindowState = FormWindowState.Maximized;
            }
            else
            {
                _parentForm.FormBorderStyle = _prevBorderStyle == FormBorderStyle.None ? FormBorderStyle.Sizable : _prevBorderStyle;
                _parentForm.WindowState = _prevWindowState == FormWindowState.Minimized ? FormWindowState.Normal : _prevWindowState;

                _playerStagePanel.Padding = new Padding(14, 6, 8, 8);
                _watchMetaPanel.Visible = true;
                _sidebar.Visible = !_isTheaterMode;
                _rightRelatedPanel.Visible = !_isTheaterMode;
            }

            if (FullscreenChanged != null) FullscreenChanged(_isFullscreen);
            if (_wpfOverlay != null) _wpfOverlay.SyncOverlayBounds();
        }

        public bool HandleShortcutKey(Keys keyCode)
        {
            if (keyCode == Keys.F5)
            {
                var _ = RefreshCurrentPageAsync();
                return true;
            }
            if (keyCode == Keys.Escape)
            {
                if (_isFullscreen)
                {
                    ToggleFullscreen(false);
                    return true;
                }
                if (_isTheaterMode)
                {
                    ToggleTheaterMode(false);
                    return true;
                }
            }
            else if (keyCode == Keys.F11 || keyCode == Keys.F)
            {
                if (_watchPagePanel.Visible)
                {
                    ToggleFullscreen(!_isFullscreen);
                    return true;
                }
            }
            else if (keyCode == Keys.T)
            {
                if (_watchPagePanel.Visible)
                {
                    ToggleTheaterMode(!_isTheaterMode);
                    return true;
                }
            }
            else if (keyCode == Keys.Space || keyCode == Keys.K || keyCode == Keys.MediaPlayPause)
            {
                if (_watchPagePanel.Visible && _currentVideo != null)
                {
                    TogglePlayPause();
                    return true;
                }
            }
            else if (keyCode == Keys.Up || keyCode == Keys.VolumeUp)
            {
                if (_watchPagePanel.Visible)
                {
                    ChangeVideoVolumeBy(0.05);
                    return true;
                }
            }
            else if (keyCode == Keys.Down || keyCode == Keys.VolumeDown)
            {
                if (_watchPagePanel.Visible)
                {
                    ChangeVideoVolumeBy(-0.05);
                    return true;
                }
            }
            else if (keyCode == Keys.M || keyCode == Keys.VolumeMute)
            {
                if (_watchPagePanel.Visible)
                {
                    ToggleVideoMute();
                    return true;
                }
            }
            else if (keyCode == Keys.Left)
            {
                if (_watchPagePanel.Visible && _currentVideo != null)
                {
                    SeekRelative(-5.0);
                    return true;
                }
            }
            else if (keyCode == Keys.Right)
            {
                if (_watchPagePanel.Visible && _currentVideo != null)
                {
                    SeekRelative(5.0);
                    return true;
                }
            }
            else if (keyCode == Keys.J)
            {
                if (_watchPagePanel.Visible && _currentVideo != null)
                {
                    SeekRelative(-10.0);
                    return true;
                }
            }
            else if (keyCode == Keys.L)
            {
                if (_watchPagePanel.Visible && _currentVideo != null)
                {
                    SeekRelative(10.0);
                    return true;
                }
            }
            else if (keyCode == Keys.D0 || keyCode == Keys.NumPad0 || keyCode == Keys.Home)
            {
                if (_watchPagePanel.Visible && _currentVideo != null) { SeekToPercentage(0.0); return true; }
            }
            else if (keyCode == Keys.D1 || keyCode == Keys.NumPad1)
            {
                if (_watchPagePanel.Visible && _currentVideo != null) { SeekToPercentage(0.10); return true; }
            }
            else if (keyCode == Keys.D2 || keyCode == Keys.NumPad2)
            {
                if (_watchPagePanel.Visible && _currentVideo != null) { SeekToPercentage(0.20); return true; }
            }
            else if (keyCode == Keys.D3 || keyCode == Keys.NumPad3)
            {
                if (_watchPagePanel.Visible && _currentVideo != null) { SeekToPercentage(0.30); return true; }
            }
            else if (keyCode == Keys.D4 || keyCode == Keys.NumPad4)
            {
                if (_watchPagePanel.Visible && _currentVideo != null) { SeekToPercentage(0.40); return true; }
            }
            else if (keyCode == Keys.D5 || keyCode == Keys.NumPad5)
            {
                if (_watchPagePanel.Visible && _currentVideo != null) { SeekToPercentage(0.50); return true; }
            }
            else if (keyCode == Keys.D6 || keyCode == Keys.NumPad6)
            {
                if (_watchPagePanel.Visible && _currentVideo != null) { SeekToPercentage(0.60); return true; }
            }
            else if (keyCode == Keys.D7 || keyCode == Keys.NumPad7)
            {
                if (_watchPagePanel.Visible && _currentVideo != null) { SeekToPercentage(0.70); return true; }
            }
            else if (keyCode == Keys.D8 || keyCode == Keys.NumPad8)
            {
                if (_watchPagePanel.Visible && _currentVideo != null) { SeekToPercentage(0.80); return true; }
            }
            else if (keyCode == Keys.D9 || keyCode == Keys.NumPad9)
            {
                if (_watchPagePanel.Visible && _currentVideo != null) { SeekToPercentage(0.90); return true; }
            }
            else if (keyCode == Keys.End)
            {
                if (_watchPagePanel.Visible && _currentVideo != null) { SeekToPercentage(0.99); return true; }
            }
            else if (keyCode == Keys.MediaStop)
            {
                if (_watchPagePanel.Visible)
                {
                    StopVideo();
                    return true;
                }
            }
            return false;
        }

        public void SeekToPercentage(double fraction)
        {
            double tot = TotalVideoDurationSeconds;
            if (tot > 0)
            {
                double target = tot * Math.Max(0.0, Math.Min(0.99, fraction));
                SeekToSeconds(target);
                EmitStatus(string.Format("⏩ Pular para {0}% ({1})", (int)Math.Round(fraction * 100), YouTubeService.FormatSeconds(target)));
            }
        }

        public void TogglePlayPause()
        {
            if (_isLoading) return;
            if (_currentVideo == null || _player == null) return;

            try
            {
                if (_config.AutoPauseMusicWhenVideoPlays && _musicEngine != null && _musicEngine.IsPlaying)
                {
                    _musicEngine.PauseIfPlaying();
                }

                if (_player.IsPlaying)
                {
                    _player.Pause();
                }
                else
                {
                    if (_player.Status == Status.Ended)
                    {
                        _player.SeekAccurate(0);
                    }
                    _player.Play();
                }
                _isPlaying = _player.IsPlaying;
                _lastMouseActivityUtc = DateTime.UtcNow;
                UpdatePlayPauseButton();

                EmitStatus(_isPlaying
                    ? "▶ Assistindo: " + _currentVideo.Title
                    : "⏸ Vídeo pausado: " + _currentVideo.Title);
            }
            catch { }
        }

        public void StopVideo()
        {
            StopProgressiveSession();
            try
            {
                if (_player != null) _player.Stop();
            }
            catch { }
            _isPlaying = false;
            _isLoading = false;
            _loadingOverlay.Visible = false;
            UpdatePlayPauseButton();
        }

        public void ShutdownOverlay()
        {
            StopVideo();
            try
            {
                if (_player != null)
                {
                    _player.PlaybackStopped -= OnPlayerPlaybackStopped;
                    _player.OpenCompleted -= OnPlayerOpenCompleted;
                    _player.Stop();
                    _player.Dispose();
                    _player = null;
                }
                if (_flyleafHost != null)
                {
                    _flyleafHost.Dispose();
                    _flyleafHost = null;
                }
                if (_wpfOverlay != null)
                {
                    _wpfOverlay.Dispose();
                    _wpfOverlay = null;
                }
            }
            catch { }
        }

        public void PauseIfPlaying()
        {
            if (_isPlaying && _player != null)
            {
                try
                {
                    _player.Pause();
                    _isPlaying = false;
                    UpdatePlayPauseButton();
                }
                catch { }
            }
        }

        private async void ChangeVideoQuality(int targetHeight)
        {
            if (_config.VideoQualityHeight == targetHeight || _isSwitchingQuality || _currentVideo == null)
                return;

            _isSwitchingQuality = true;
            int previousHeight = _config.VideoQualityHeight;
            _config.VideoQualityHeight = targetHeight;
            _config.Save();
            UpdateQualityButtons();

            string qLabel = targetHeight >= 2160 ? "4K" : string.Format("{0}p", targetHeight);
            _wpfOverlay?.ShowToast(string.Format("Alternando para {0}...", qLabel));

            try
            {
                string videoId = _currentVideo.Id;

                // 1. O vídeo atual continua tocando a partir do buffer na GPU/RAM.
                // 2. Resolve dinamicamente as URLs da nova resolução em segundo plano.
                var (newVideoUrl, newAudioUrl) = await Task.Run(() =>
                {
                    return ProgressiveVideoSession.ResolveStreamUrls(videoId, targetHeight, null, _config.BrowserForCookies, _config.CookiesFilePath);
                });

                if (_currentVideo == null || _currentVideo.Id != videoId)
                    return;

                if (string.IsNullOrWhiteSpace(newVideoUrl))
                    throw new InvalidOperationException(string.Format("Não foi possível obter o fluxo para {0}.", qLabel));

                // 3. Captura precisa do timestamp atual da mídia (em ticks)
                long currentTicks = _player != null ? _player.CurTime : 0;
                bool wasPlaying = _isPlaying;

                // 4. Efetua a troca atômica sem resetar o marcador de reprodução
                await SwitchQualityStreamAtomicAsync(newVideoUrl, newAudioUrl, currentTicks, wasPlaying);

                _wpfOverlay?.ShowToast(string.Format("Qualidade: {0}", qLabel));
                EmitStatus(string.Format("▶ Resolução alternada com sucesso para {0} ({1})", qLabel, _currentVideo.Title));
            }
            catch (Exception ex)
            {
                _config.VideoQualityHeight = previousHeight;
                UpdateQualityButtons();
                EmitStatus(string.Format("Erro ao mudar resolução: {0}", ex.Message));
                _wpfOverlay?.ShowToast(string.Format("Falha ao mudar para {0}", qLabel));
            }
            finally
            {
                _isSwitchingQuality = false;
            }
        }

        private async Task SwitchQualityStreamAtomicAsync(string videoUrl, string audioUrl, long currentTicks, bool wasPlaying)
        {
            if (_player == null) return;

            await Task.Run(() =>
            {
                try
                {
                    _player.Pause();

                    // 1. Abre a nova faixa de vídeo na resolução solicitada
                    _player.Open(videoUrl);

                    // 2. Acopla o áudio sincronizado se for fluxo separado
                    if (!string.IsNullOrEmpty(audioUrl) && !string.Equals(videoUrl, audioUrl, StringComparison.OrdinalIgnoreCase))
                    {
                        var extAudio = new ExternalAudioStream
                        {
                            Url = audioUrl,
                            UserAgent = "Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/120.0.0.0 Safari/537.36"
                        };
                        _player.Open(extAudio, resync: true, defaultAudio: true);
                    }

                    // 3. Restaura o marcador de tempo da reprodução sem retroceder ao início
                    if (currentTicks > 0)
                    {
                        int targetMs = (int)(currentTicks / TimeSpan.TicksPerMillisecond);
                        _player.SeekAccurate(targetMs);
                    }

                    // 4. Retoma a reprodução contínua caso estivesse tocando
                    if (wasPlaying)
                    {
                        _player.Play();
                    }
                }
                catch (Exception ex)
                {
                    Logger.Error("FLYLEAF", "Falha na troca atômica de resolução: " + ex.Message, ex);
                    throw;
                }
            });

            SafeInvoke(() =>
            {
                _isPlaying = wasPlaying;
                UpdatePlayPauseButton();

                if (_player != null && _player.Video != null && !string.IsNullOrEmpty(_player.Video.Codec))
                {
                    string hw = _player.Video.VideoAcceleration ? "HW D3D11" : "SW";
                    string vCodec = _player.Video.Codec.ToUpperInvariant();
                    string aCodec = (_player.Audio != null && !string.IsNullOrEmpty(_player.Audio.Codec))
                        ? _player.Audio.Codec.ToUpperInvariant()
                        : "AUDIO";
                    string runtimeCodec = string.Format("{0}/{1} ({2})", vCodec, aCodec, hw);
                    _lblBufferBadge.Text = string.Format("▶ Flyleaf D3D11 ({0})", runtimeCodec);
                    _lblVideoSubInfo.Text = string.Format("Qualidade {0}p ({1})  •  Flyleaf Direct3D 11  •  Volume: {2}%",
                        _config.VideoQualityHeight, runtimeCodec, (int)Math.Round(_config.VideoVolume * 100));
                }

                if (_wpfOverlay != null)
                {
                    double curSec = TimeSpan.FromTicks(currentTicks).TotalSeconds;
                    _wpfOverlay.UpdatePlaybackState(_isPlaying, curSec, BufferedVideoDurationSeconds, TotalVideoDurationSeconds);
                }
            });
        }

        private void UpdateQualityButtons()
        {
            int q = _config.VideoQualityHeight;
            if (_wpfOverlay != null)
            {
                _wpfOverlay.UpdateQualityAndSpeedVisuals(q, _player != null ? _player.Speed : 1.0);
            }
        }

        public double CurrentVideoPositionSeconds
        {
            get
            {
                if (_player != null && _player.CurTime > 0) return TimeSpan.FromTicks(_player.CurTime).TotalSeconds;
                return 0.0;
            }
        }

        public double BufferedVideoDurationSeconds
        {
            get
            {
                if (_player != null && _player.BufferedDuration > 0) return TimeSpan.FromTicks(_player.BufferedDuration).TotalSeconds;
                return Math.Min(TotalVideoDurationSeconds, CurrentVideoPositionSeconds + 20.0);
            }
        }

        public double TotalVideoDurationSeconds
        {
            get
            {
                if (_player != null && _player.Duration > 0)
                    return TimeSpan.FromTicks(_player.Duration).TotalSeconds;
                if (_currentVideo != null && _currentVideo.DurationSeconds > 0)
                    return _currentVideo.DurationSeconds;
                return 0.0;
            }
        }

        public void SeekToSeconds(double seconds)
        {
            if (_player == null) return;
            try
            {
                double dur = TotalVideoDurationSeconds;
                if (dur > 0)
                {
                    seconds = Math.Max(0.0, Math.Min(dur - 0.2, seconds));
                }
                int targetMs = (int)Math.Round(seconds * 1000.0);
                Logger.Info("SEEK", string.Format("Flyleaf SeekAccurate para {0:0.0}s ({1}ms)", seconds, targetMs));
                _player.SeekAccurate(targetMs);
                _lastMouseActivityUtc = DateTime.UtcNow;
                UpdatePlayPauseButton();
            }
            catch (Exception ex)
            {
                EmitStatus("Erro no seek Flyleaf: " + ex.Message);
            }
        }

        public void SeekRelative(double deltaSeconds)
        {
            SeekToSeconds(CurrentVideoPositionSeconds + deltaSeconds);
        }

        public void UpdateTimerTick()
        {
            if (_currentVideo == null || _player == null || _wpfOverlay == null) return;

            double cur = CurrentVideoPositionSeconds;
            double tot = TotalVideoDurationSeconds;
            double buf = BufferedVideoDurationSeconds;
            bool playing = _player.IsPlaying;

            if (_player.Status == Status.Ended && tot > 0)
            {
                cur = tot;
            }

            _wpfOverlay.UpdatePlaybackState(playing, cur, buf, tot);

            // Auto-Skip do SponsorBlock
            if (_config.EnableSponsorBlock && _config.AutoSkipSponsors && playing && !_wpfOverlay.IsUserDraggingScrubber && _currentSponsorSegments != null && _currentSponsorSegments.Count > 0)
            {
                double skipTo;
                SponsorSegment matched;
                if (SponsorBlockService.CheckAndGetSkipTarget(cur, _currentSponsorSegments, out skipTo, out matched))
                {
                    if (matched != null && _lastSkippedSponsorUuid != matched.UUID)
                    {
                        _lastSkippedSponsorUuid = matched.UUID;
                        double targetSec = tot > 0 ? Math.Min(tot, skipTo) : skipTo;
                        SeekToSeconds(targetSec);
                        _wpfOverlay.ShowSponsorSkipToast(matched.CategoryLabel);
                        string msg = string.Format("⏩ [SponsorBlock] {0} pulado ({1:0.0}s -> {2:0.0}s)",
                            matched.CategoryLabel, matched.Start, matched.End);
                        EmitStatus(msg);
                        _lblBufferBadge.Text = string.Format("⏩ {0} pulado!", matched.CategoryLabel);
                    }
                }
            }

            _isPlaying = playing;
        }

        // ====================================================================
        // INDEPENDENT VIDEO AUDIO PROFILE LOGIC
        // ====================================================================

        public void ApplyVideoAudioSettings()
        {
            try
            {
                if (_player != null && _player.Audio != null)
                {
                    int target = (int)Math.Round(Math.Max(0.0, Math.Min(1.0, _config.VideoVolume)) * 100.0);
                    _player.Audio.Volume = target;
                    _player.Audio.Mute = _config.IsVideoMuted;
                }
            }
            catch { }
            UpdateVideoAudioUI();
        }

        public void ToggleVideoMute()
        {
            _config.IsVideoMuted = !_config.IsVideoMuted;
            ApplyVideoAudioSettings();
            _config.Save();
            EmitStatus(_config.IsVideoMuted
                ? "🎬 Áudio de vídeo mutado (🔇)"
                : string.Format("🎬 Volume do Vídeo: {0}%", (int)Math.Round(_config.VideoVolume * 100)));
        }

        public void ChangeVideoVolumeBy(double delta)
        {
            double v = Math.Max(0.0, Math.Min(1.0, Math.Round(_config.VideoVolume + delta, 2)));
            _config.VideoVolume = v;
            if (v > 0.0 && _config.IsVideoMuted) _config.IsVideoMuted = false;
            ApplyVideoAudioSettings();
            _config.Save();
            EmitStatus(string.Format("🎬 Volume do Vídeo: {0}%", (int)Math.Round(_config.VideoVolume * 100)));
        }

        public void UpdateVideoAudioUI()
        {
            int pct = (int)Math.Round(_config.VideoVolume * 100);
            if (_wpfOverlay != null)
            {
                _wpfOverlay.UpdateAudioVisuals(_config.VideoVolume, _config.IsVideoMuted);
            }
            if (_currentVideo != null && _lblVideoSubInfo != null)
            {
                _lblVideoSubInfo.Text = string.Format("Qualidade {0}p • Volume: {1}%",
                    _config.VideoQualityHeight, pct);
            }
        }

        public void ShowVideoAudioRegulatorDialog()
        {
            using (var dlg = new Form())
            {
                dlg.Text = "Regulador de Áudio & Cache de Vídeos — LiteTube";
                dlg.Size = new Size(490, 535);
                dlg.FormBorderStyle = FormBorderStyle.FixedDialog;
                dlg.StartPosition = FormStartPosition.CenterParent;
                dlg.BackColor = BgPanel;
                dlg.ForeColor = TextPrimary;
                dlg.MaximizeBox = false;
                dlg.MinimizeBox = false;

                var lblBanner = new Label
                {
                    Text = "Este perfil regula exclusivamente o Volume de VÍDEOS (1:1 direto na saída de som).",
                    Font = new Font("Segoe UI", 8.5f, FontStyle.Italic),
                    ForeColor = AccentBlue,
                    Location = new Point(18, 12),
                    Size = new Size(445, 20)
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
                    Value = (int)Math.Round(_config.VideoVolume * 100),
                    TickFrequency = 10,
                    BackColor = BgPanel
                };

                var btnMuteDlg = CreateFlatButton("🔊 Ativo", 360, 58, 92, 32, BgCard);

                var btnPre40 = CreateFlatButton("🔉 40% Médio", 18, 96, 100, 28, BgCard);
                var btnPre70 = CreateFlatButton("🎬 70% Cinema", 124, 96, 104, 28, BgCard);
                var btnPre85 = CreateFlatButton("🔊 85% Padrão", 234, 96, 104, 28, BgCard);
                var btnPre100 = CreateFlatButton("🔥 100% Máx", 344, 96, 108, 28, BgCard);

                var lblCeilingTitle = new Label
                {
                    Text = "",
                    Font = new Font("Segoe UI Semibold", 9.5f, FontStyle.Bold),
                    ForeColor = AccentGreen,
                    Location = new Point(18, 138),
                    Size = new Size(425, 22)
                };

                var btnCeil50 = CreateFlatButton("Teto 50%", 18, 164, 135, 30, BgCard);
                var btnCeil75 = CreateFlatButton("Teto 75%", 160, 164, 135, 30, BgCard);
                var btnCeil100 = CreateFlatButton("100% (Sem Limite)", 302, 164, 150, 30, BgCard);

                var lblBalanceTitle = new Label
                {
                    Text = "",
                    Font = new Font("Segoe UI Semibold", 9.5f, FontStyle.Bold),
                    Location = new Point(18, 206),
                    Size = new Size(320, 22)
                };

                var btnCenterBalance = CreateFlatButton("Centralizar (0)", 344, 202, 108, 26, BgCard);

                var sliderBalance = new TrackBar
                {
                    Location = new Point(14, 232),
                    Size = new Size(438, 32),
                    Minimum = -100,
                    Maximum = 100,
                    Value = 0,
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

                var btnSpd075 = CreateFlatButton("0.75x", 18, 300, 78, 30, BgCard);
                var btnSpd100 = CreateFlatButton("1.0x Normal", 102, 300, 96, 30, BgCard);
                var btnSpd125 = CreateFlatButton("1.25x", 204, 300, 76, 30, BgCard);
                var btnSpd150 = CreateFlatButton("1.50x", 286, 300, 76, 30, BgCard);
                var btnSpd200 = CreateFlatButton("2.00x", 368, 300, 84, 30, BgCard);

                var chkAutoPauseMusic = new CheckBox
                {
                    Text = "Pausar automaticamente a Música quando eu der Play em um Vídeo",
                    Checked = _config.AutoPauseMusicWhenVideoPlays,
                    ForeColor = TextPrimary,
                    Location = new Point(18, 342),
                    Size = new Size(440, 24)
                };

                double thumbCacheMB = ThumbnailCache.GetDiskUsageMB();

                var lblVidCache = new Label
                {
                    Text = string.Format("🖼️ Miniaturas em Disco: {0:0.0}/{1} MB  |  🚀 Streaming: Loopback Direto (RAM)",
                        thumbCacheMB, _config.MaxThumbCacheSizeMB),
                    Font = new Font("Segoe UI Semibold", 8.8f, FontStyle.Bold),
                    ForeColor = AccentBlue,
                    Location = new Point(18, 376),
                    Size = new Size(330, 32)
                };

                var btnClearVidCache = CreateFlatButton("🧹 Limpar Thumbs", 352, 374, 104, 30, BgCard);

                var btnClose = CreateFlatButton("✔ Fechar e Salvar", 312, 435, 144, 36, AccentBlue);
                btnClose.DialogResult = DialogResult.OK;

                Action refreshLabels = () =>
                {
                    lblVolTitle.Text = string.Format("🎬 Volume do Vídeo: {0}%",
                        (int)Math.Round(_config.VideoVolume * 100));
                    btnMuteDlg.Text = _config.IsVideoMuted ? "🔇 Mutado" : "🔊 Ativo";
                    btnMuteDlg.BackColor = _config.IsVideoMuted ? YouTubeRed : BgCard;

                    lblCeilingTitle.Text = "🛡️ Escala 1:1 Linear: 0% a 100% (Sem Teto Artificial)";
                    btnCeil50.BackColor = BgCard;
                    btnCeil75.BackColor = BgCard;
                    btnCeil100.BackColor = AccentBlue;

                    lblBalanceTitle.Text = "🎧 Balanço: Centro (L 50% / R 50%)";

                    float currentSpeed = (float)(_player != null ? _player.Speed : 1.0);
                    lblSpeedTitle.Text = string.Format("⚡ Velocidade do Vídeo: {0:0.00}x", currentSpeed);
                    int spd = (int)Math.Round(currentSpeed * 100);
                    btnSpd075.BackColor = spd == 75 ? AccentBlue : BgCard;
                    btnSpd100.BackColor = spd == 100 ? AccentBlue : BgCard;
                    btnSpd125.BackColor = spd == 125 ? AccentBlue : BgCard;
                    btnSpd150.BackColor = spd == 150 ? AccentBlue : BgCard;
                    btnSpd200.BackColor = spd == 200 ? AccentBlue : BgCard;
                };

                sliderVol.Scroll += (s, e) => {
                    _config.VideoVolume = sliderVol.Value / 100.0;
                    if (_config.VideoVolume > 0) _config.IsVideoMuted = false;
                    ApplyVideoAudioSettings();
                    refreshLabels();
                };
                btnMuteDlg.Click += (s, e) => { ToggleVideoMute(); refreshLabels(); };

                btnPre40.Click += (s, e) => { sliderVol.Value = 40; _config.VideoVolume = 0.40; if (_config.IsVideoMuted) _config.IsVideoMuted = false; ApplyVideoAudioSettings(); refreshLabels(); };
                btnPre70.Click += (s, e) => { sliderVol.Value = 70; _config.VideoVolume = 0.70; if (_config.IsVideoMuted) _config.IsVideoMuted = false; ApplyVideoAudioSettings(); refreshLabels(); };
                btnPre85.Click += (s, e) => { sliderVol.Value = 85; _config.VideoVolume = 0.85; if (_config.IsVideoMuted) _config.IsVideoMuted = false; ApplyVideoAudioSettings(); refreshLabels(); };
                btnPre100.Click += (s, e) => { sliderVol.Value = 100; _config.VideoVolume = 1.00; if (_config.IsVideoMuted) _config.IsVideoMuted = false; ApplyVideoAudioSettings(); refreshLabels(); };

                btnCeil50.Click += (s, e) => { refreshLabels(); };
                btnCeil75.Click += (s, e) => { refreshLabels(); };
                btnCeil100.Click += (s, e) => { refreshLabels(); };

                sliderBalance.Scroll += (s, e) => { refreshLabels(); };
                btnCenterBalance.Click += (s, e) => { sliderBalance.Value = 0; refreshLabels(); };

                btnSpd075.Click += (s, e) => { if (_player != null) _player.Speed = 0.75; refreshLabels(); };
                btnSpd100.Click += (s, e) => { if (_player != null) _player.Speed = 1.00; refreshLabels(); };
                btnSpd125.Click += (s, e) => { if (_player != null) _player.Speed = 1.25; refreshLabels(); };
                btnSpd150.Click += (s, e) => { if (_player != null) _player.Speed = 1.50; refreshLabels(); };
                btnSpd200.Click += (s, e) => { if (_player != null) _player.Speed = 2.00; refreshLabels(); };

                chkAutoPauseMusic.CheckedChanged += (s, e) => { _config.AutoPauseMusicWhenVideoPlays = chkAutoPauseMusic.Checked; };

                btnClearVidCache.Click += (s, e) =>
                {
                    ThumbnailCache.ClearDiskAndMemoryCache();
                    lblVidCache.Text = string.Format("🖼️ Miniaturas em Disco: {0:0.0}/{1} MB  |  🚀 Streaming: Loopback Direto (RAM)",
                        ThumbnailCache.GetDiskUsageMB(), _config.MaxThumbCacheSizeMB);
                };

                refreshLabels();

                dlg.Controls.AddRange(new Control[]
                {
                    lblBanner, lblVolTitle, sliderVol, btnMuteDlg,
                    btnPre40, btnPre70, btnPre85, btnPre100,
                    lblCeilingTitle, btnCeil50, btnCeil75, btnCeil100,
                    lblBalanceTitle, btnCenterBalance, sliderBalance,
                    lblSpeedTitle, btnSpd075, btnSpd100, btnSpd125, btnSpd150, btnSpd200,
                    chkAutoPauseMusic, lblVidCache, btnClearVidCache, btnClose
                });

                dlg.ShowDialog(this);
                _config.Save();
            }
        }

        private void PromptOpenVideoUrl()
        {
            using (var form = new Form())
            {
                form.Width = 460;
                form.Height = 180;
                form.FormBorderStyle = FormBorderStyle.FixedDialog;
                form.Text = "🔗 Abrir Link de Vídeo do YouTube";
                form.StartPosition = FormStartPosition.CenterParent;
                form.BackColor = BgPanel;
                form.ForeColor = TextPrimary;
                form.MaximizeBox = false;
                form.MinimizeBox = false;

                var lbl = new Label { Left = 16, Top = 16, Width = 415, Height = 30, Text = "Cole o link do vídeo do YouTube abaixo para assisti-lo:" };
                var txt = new TextBox { Left = 16, Top = 50, Width = 415, BackColor = BgCard, ForeColor = TextPrimary, BorderStyle = BorderStyle.FixedSingle };

                if (Clipboard.ContainsText())
                {
                    string clip = Clipboard.GetText().Trim();
                    if (clip.IndexOf("youtube.com", StringComparison.OrdinalIgnoreCase) >= 0 ||
                        clip.IndexOf("youtu.be", StringComparison.OrdinalIgnoreCase) >= 0)
                    {
                        txt.Text = clip;
                    }
                }

                var btnOk = CreateFlatButton("▶ Assistir Vídeo", 220, 92, 120, 32, YouTubeRed);
                var btnCancel = CreateFlatButton("Cancelar", 346, 92, 85, 32, BgCard);
                btnOk.DialogResult = DialogResult.OK;
                btnCancel.DialogResult = DialogResult.Cancel;

                form.Controls.AddRange(new Control[] { lbl, txt, btnOk, btnCancel });
                form.AcceptButton = btnOk;
                form.CancelButton = btnCancel;

                if (form.ShowDialog(this) == DialogResult.OK && !string.IsNullOrWhiteSpace(txt.Text))
                {
                    string url = txt.Text.Trim();
                    PlayUrlOrPlaylist(url);
                }
            }
        }

        public void UpdatePlayingVideoMetadata(string title, string artist, string thumbUrl = null)
        {
            if (_currentVideo != null)
            {
                if (!string.IsNullOrEmpty(title)) _currentVideo.Title = title;
                if (!string.IsNullOrEmpty(artist)) _currentVideo.Artist = artist;
                if (!string.IsNullOrEmpty(thumbUrl)) _currentVideo.CustomThumbUrl = thumbUrl;
            }
            if (_lblVideoTitle != null && !string.IsNullOrEmpty(title))
            {
                _lblVideoTitle.Text = title;
            }
            if (_lblVideoChannel != null && !string.IsNullOrEmpty(artist))
            {
                _lblVideoChannel.Text = artist;
            }
            if (_picWatchChannelAvatar != null)
            {
                _picWatchChannelAvatar.Invalidate();
            }
        }

        public void PlayUrlOrPlaylist(string url)
        {
            if (string.IsNullOrWhiteSpace(url)) return;
            string target = url.Trim();
            Task.Run(async () =>
            {
                SafeInvoke(() => EmitStatus("Buscando informações do link externo..."));
                var res = await _ytService.FetchUrlOrPlaylistAsync(target, _config, false);
                SafeInvoke(() =>
                {
                    if (res != null && res.Item1 != null && res.Item1.Count > 0)
                    {
                        SetSearchResults(res.Item1, "Link Externo");
                        PlayVideo(res.Item1[0]);
                    }
                    else
                    {
                        EmitStatus("Não foi possível carregar o vídeo desse link.");
                    }
                });
            });
        }

        private void UpdatePlayPauseButton()
        {
            if (_wpfOverlay != null)
            {
                _wpfOverlay.UpdatePlaybackState(_isPlaying, CurrentVideoPositionSeconds, BufferedVideoDurationSeconds, TotalVideoDurationSeconds);
            }
        }

        private void PlayNextVideo()
        {
            if (_openedPlaylistVideos != null && _openedPlaylistVideos.Count > 0 && _currentVideo != null)
            {
                int idx = _openedPlaylistVideos.FindIndex(x => x.Id == _currentVideo.Id);
                if (idx >= 0 && idx + 1 < _openedPlaylistVideos.Count)
                {
                    PlayVideo(_openedPlaylistVideos[idx + 1]);
                    return;
                }
            }

            if (_relatedRowCards != null && _relatedRowCards.Items != null && _relatedRowCards.Items.Count > 0)
            {
                var next = _relatedRowCards.Items.Find(x => x.Id != (_currentVideo != null ? _currentVideo.Id : ""));
                if (next != null)
                {
                    PlayVideo(next);
                    return;
                }
            }
        }

        private Button CreateChipButton(string text, int x, int y, int w, int h, bool active)
        {
            var btn = new ModernButton
            {
                Text = text,
                Location = new Point(x, y),
                Size = new Size(w, h),
                FlatStyle = FlatStyle.Flat,
                BackColor = active ? TextPrimary : Theme.SurfaceHigh,
                ForeColor = active ? BgDark : TextPrimary,
                Font = new Font("Segoe UI Semibold", 9f, FontStyle.Bold),
                CornerRadius = 16,
                BorderColor = active ? Color.Empty : Theme.Border,
                Cursor = Cursors.Hand
            };
            btn.FlatAppearance.BorderSize = 0;
            return btn;
        }

        private Button CreateChannelTabButton(string text, int x, int y, int w)
        {
            var btn = new ModernButton
            {
                Text = text,
                Location = new Point(x, y),
                Size = new Size(w, 34),
                FlatStyle = FlatStyle.Flat,
                BackColor = Color.Transparent,
                ForeColor = TextSecondary,
                Font = new Font("Segoe UI Semibold", 10f, FontStyle.Bold),
                CornerRadius = 10,
                Cursor = Cursors.Hand
            };
            btn.FlatAppearance.BorderSize = 0;
            return btn;
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

        private void EmitStatus(string msg)
        {
            if (StatusNotification != null) StatusNotification(msg);
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
