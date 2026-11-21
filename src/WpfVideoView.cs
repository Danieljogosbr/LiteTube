#nullable enable
using System;
using System.Collections.Generic;
using System.IO;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Documents;
using System.Windows.Input;
using System.Windows.Markup;
using System.Windows.Media;
using System.Windows.Shapes;
using FlyleafLib.Controls.WPF;

namespace LiteTube
{
    /// <summary>
    /// Camada 1 (UI WPF Pura) — Ambiente YouTube Vídeo.
    /// Zero WinForms, zero ElementHost, zero ThumbnailCache GDI+:
    /// usa FlyleafHost nativo WPF, WpfVideoPlayerOverlay, WpfMediaListControl e MemoryCachedWebImageLoader.
    /// </summary>
    public sealed class WpfVideoView : UserControl, IDisposable
    {
        private readonly AppConfig _config;
        private readonly YouTubeService _ytService;
        private readonly VideoPlaybackController _controller;

        // Layout & Navigation controls
        private ColumnDefinition _sidebarColumn = null!;
        private Border _sidebarPanel = null!;
        private Border _miniSidebarPanel = null!;
        private Button _navHome = null!;
        private Button _navSubscriptions = null!;
        private Button _navHistory = null!;
        private Button _navPlaylists = null!;
        private Button _navYou = null!;
        private TextBlock _txtNavYouLabel = null!;
        private Button _navBackToPlayer = null!;
        private Button _navOpenUrl = null!;
        private Button _miniNavHome = null!;
        private Button _miniNavSubs = null!;
        private Button _miniNavHistory = null!;
        private Button _miniNavPlaylists = null!;
        private Button _miniNavYou = null!;
        private Button _miniNavWatch = null!;
        private Button _btnBackToAccountPlaylists = null!;
        private Button _btnPlayOpenedPlaylist = null!;
        private bool _watchSidebarVisible = false;

        // Catalog & Channel controls
        private Grid _catalogViewGrid = null!;
        private Border _channelBannerPanel = null!;
        private Button _btnBackFromChannel = null!;
        private Ellipse _channelAvatarEllipse = null!;
        private TextBlock _lblChannelTitle = null!;
        private TextBlock _lblChannelSub = null!;
        private Button _btnImportOpenedPlaylist = null!;
        private Button _btnChannelTabHome = null!;
        private Button _btnChannelTabVideos = null!;
        private Button _btnChannelTabPlaylists = null!;
        private WrapPanel _searchChipsBar = null!;
        private Button _chipAll = null!;
        private Button _chipVideos = null!;
        private Button _chipChannels = null!;
        private Button _chipHideShorts = null!;
        private Button _chipSponsorBlock = null!;
        private TextBlock _lblCatalogHeader = null!;
        private ContentControl _catalogGridHost = null!;
        private WpfMediaListControl _catalogGrid = null!;

        // Watch Page controls
        private ScrollViewer _watchPageScroll = null!;
        private Grid _theaterStageContainer = null!;
        private Grid _watchTwoColumnGrid = null!;
        private Grid _normalStageContainer = null!;
        private Border _playerStageFrame = null!;
        private Grid _playerHostContainer = null!;
        private StackPanel _watchMetadataPanel = null!;
        private TextBlock _lblVideoTitle = null!;
        private TextBlock _lblVideoViewsDate = null!;
        private Ellipse _watchChannelAvatar = null!;
        private TextBlock _lblVideoChannel = null!;
        private TextBlock _lblVideoSubInfo = null!;
        private Button _btnOpenCurrentChannel = null!;
        private Button _btnToggleDescription = null!;
        private Border _descriptionContainer = null!;
        private TextBlock _txtVideoDescription = null!;
        private ContentControl _relatedGridHost = null!;
        private WpfMediaListControl _relatedGrid = null!;
        private Grid _fullscreenHostLayer = null!;

        // Native WPF FlyleafHost + Overlay + Loading layer
        private FlyleafHost _flyleafHost = null!;
        private WpfVideoPlayerOverlay _wpfOverlay = null!;
        private Border _loadingOverlay = null!;
        private TextBlock _lblLoadingTitle = null!;
        private TextBlock _lblLoadingProgress = null!;
        private Button _btnCancelLoad = null!;

        // View state
        private bool _sidebarExpanded = true;
        private bool _isFullscreen = false;
        private bool _isTheaterMode = false;
        private bool _initialHomeLoaded = false;
        private bool _isDescriptionExpanded = false;
        private bool _isChannelView = false;
        private bool _isLoadingMore = false;
        private int _navRequestSeq = 0;
        private string _activeNavSection = "home";
        private string _activeSearchChip = "all";
        private string _activeChannelId = "";
        private string _activeChannelName = "";
        private string _activeChannelTab = "home";
        private string _channelContinuationToken = "";
        private string _searchContinuationToken = "";
        private string _feedContinuationToken = "";
        private string _openedPlaylistContinuationToken = "";
        private string _currentSubscriberCountText = "";
        private string _lastDescriptionRaw = "";

        private TrackItem? _openedChannelPlaylist;
        private List<TrackItem> _homeVideos = new List<TrackItem>();
        private List<TrackItem> _subscriptionsVideos = new List<TrackItem>();
        private List<TrackItem> _historyVideos = new List<TrackItem>();
        private List<TrackItem> _accountPlaylists = new List<TrackItem>();
        private List<TrackItem> _searchVideos = new List<TrackItem>();
        private List<TrackItem> _channelCurrentItems = new List<TrackItem>();
        private List<TrackItem> _openedPlaylistVideos = new List<TrackItem>();
        private List<TrackItem> _relatedVideos = new List<TrackItem>();

        private CancellationTokenSource? _watchNextCts;
        private CancellationTokenSource? _metadataFetchCts;

        private static readonly Regex TimestampRegex = new Regex(
            @"(?<![0-9:])(?:(\d{1,2}):)?(\d{1,2}):(\d{2})(?![0-9:])",
            RegexOptions.Compiled);

        public event Action<string>? StatusNotification;
        public event Action<bool>? FullscreenChanged;
        public event Action? PlaylistImportedToLibrary;
        public event Action? AccountStatusMayHaveChanged;

        public VideoPlaybackController PlaybackController => _controller;
        public bool IsWatchPageVisible => _watchPageScroll != null && _watchPageScroll.Visibility == Visibility.Visible;
        public bool IsFullscreen => _isFullscreen;
        public bool IsOverlayVisible => _wpfOverlay != null && _wpfOverlay.IsOverlayVisible;

        public WpfVideoView(AppConfig config, YouTubeService ytService, AudioEngine musicEngine)
        {
            _config = config;
            _ytService = ytService;
            _controller = new VideoPlaybackController(config, musicEngine, Dispatcher);
            _controller.NextVideoFallbackProvider = GetFallbackNextVideo;

            LoadXaml();
            BuildMediaControls();
            WireEvents();
            WireControllerEvents();

            UpdateSidebarSelection();
            UpdateSavedVideosCount();
            UpdateFilterChipsVisuals();
        }

        private void LoadXaml()
        {
            string baseDir = AppDomain.CurrentDomain.BaseDirectory;
            string p1 = System.IO.Path.Combine(baseDir, "src", "WpfVideoView.xaml");
            string p2 = System.IO.Path.Combine(baseDir, "WpfVideoView.xaml");
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

            _sidebarColumn = (ColumnDefinition)root.FindName("SidebarColumn");
            _sidebarPanel = (Border)root.FindName("SidebarPanel");
            _miniSidebarPanel = (Border)root.FindName("MiniSidebarPanel");
            _navHome = (Button)root.FindName("NavHome");
            _navSubscriptions = (Button)root.FindName("NavSubscriptions");
            _navHistory = (Button)root.FindName("NavHistory");
            _navPlaylists = (Button)root.FindName("NavPlaylists");
            _navYou = (Button)root.FindName("NavYou");
            _txtNavYouLabel = (TextBlock)root.FindName("TxtNavYouLabel");
            _navBackToPlayer = (Button)root.FindName("NavBackToPlayer");
            _navOpenUrl = (Button)root.FindName("NavOpenUrl");
            _miniNavHome = (Button)root.FindName("MiniNavHome");
            _miniNavSubs = (Button)root.FindName("MiniNavSubs");
            _miniNavHistory = (Button)root.FindName("MiniNavHistory");
            _miniNavPlaylists = (Button)root.FindName("MiniNavPlaylists");
            _miniNavYou = (Button)root.FindName("MiniNavYou");
            _miniNavWatch = (Button)root.FindName("MiniNavWatch");
            _btnBackToAccountPlaylists = (Button)root.FindName("BtnBackToAccountPlaylists");
            _btnPlayOpenedPlaylist = (Button)root.FindName("BtnPlayOpenedPlaylist");

            _catalogViewGrid = (Grid)root.FindName("CatalogViewGrid");
            _channelBannerPanel = (Border)root.FindName("ChannelBannerPanel");
            _btnBackFromChannel = (Button)root.FindName("BtnBackFromChannel");
            _channelAvatarEllipse = (Ellipse)root.FindName("ChannelAvatarEllipse");
            _lblChannelTitle = (TextBlock)root.FindName("LblChannelTitle");
            _lblChannelSub = (TextBlock)root.FindName("LblChannelSub");
            _btnImportOpenedPlaylist = (Button)root.FindName("BtnImportOpenedPlaylist");
            _btnChannelTabHome = (Button)root.FindName("BtnChannelTabHome");
            _btnChannelTabVideos = (Button)root.FindName("BtnChannelTabVideos");
            _btnChannelTabPlaylists = (Button)root.FindName("BtnChannelTabPlaylists");
            _searchChipsBar = (WrapPanel)root.FindName("SearchChipsBar");
            _chipAll = (Button)root.FindName("ChipAll");
            _chipVideos = (Button)root.FindName("ChipVideos");
            _chipChannels = (Button)root.FindName("ChipChannels");
            _chipHideShorts = (Button)root.FindName("ChipHideShorts");
            _chipSponsorBlock = (Button)root.FindName("ChipSponsorBlock");
            _lblCatalogHeader = (TextBlock)root.FindName("LblCatalogHeader");
            _catalogGridHost = (ContentControl)root.FindName("CatalogGridHost");

            _watchPageScroll = (ScrollViewer)root.FindName("WatchPageScroll");
            _theaterStageContainer = (Grid)root.FindName("TheaterStageContainer");
            _watchTwoColumnGrid = (Grid)root.FindName("WatchTwoColumnGrid");
            _normalStageContainer = (Grid)root.FindName("NormalStageContainer");
            _playerStageFrame = (Border)root.FindName("PlayerStageFrame");
            _playerHostContainer = (Grid)root.FindName("PlayerHostContainer");
            _watchMetadataPanel = (StackPanel)root.FindName("WatchMetadataPanel");
            _lblVideoTitle = (TextBlock)root.FindName("LblVideoTitle");
            _lblVideoViewsDate = (TextBlock)root.FindName("LblVideoViewsDate");
            _watchChannelAvatar = (Ellipse)root.FindName("WatchChannelAvatar");
            _lblVideoChannel = (TextBlock)root.FindName("LblVideoChannel");
            _lblVideoSubInfo = (TextBlock)root.FindName("LblVideoSubInfo");
            _btnOpenCurrentChannel = (Button)root.FindName("BtnOpenCurrentChannel");
            _btnToggleDescription = (Button)root.FindName("BtnToggleDescription");
            _descriptionContainer = (Border)root.FindName("DescriptionContainer");
            _txtVideoDescription = (TextBlock)root.FindName("TxtVideoDescription");
            _relatedGridHost = (ContentControl)root.FindName("RelatedGridHost");
            _fullscreenHostLayer = (Grid)root.FindName("FullscreenHostLayer");
        }

        private void BuildMediaControls()
        {
            // Catálogo principal virtualizado WPF
            _catalogGrid = new WpfMediaListControl
            {
                IsGridMode = true,
                IsMusicEnvironment = false,
                SingleClickActivates = true,
                IsFavoriteOrSavedCheck = id => _config.IsSavedVideo(id)
            };
            _catalogGrid.SetAccentColor(255, 0, 51);
            _catalogGridHost.Content = _catalogGrid;

            // Barra lateral direita de recomendados (402px, cards compactos 168x94 estilo YouTube)
            _relatedGrid = new WpfMediaListControl
            {
                IsGridMode = false,
                IsCompactSidebarMode = true,
                IsEmbeddedWatchGrid = true,
                IsMusicEnvironment = false,
                SingleClickActivates = true,
                IsFavoriteOrSavedCheck = id => _config.IsSavedVideo(id)
            };
            _relatedGrid.SetAccentColor(255, 0, 51);
            _relatedGridHost.Content = _relatedGrid;

            // Overlay HUD WPF puro + Loading Overlay colocados dentro do FlyleafHost.Content (zero airspace!)
            _wpfOverlay = new WpfVideoPlayerOverlay();
            _wpfOverlay.UpdateAudioVisuals(_config.VideoVolume, _config.IsVideoMuted);
            _wpfOverlay.UpdateQualityAndSpeedVisuals(_config.VideoQualityHeight, 1.0);
            _wpfOverlay.SetOverlayActive(false);
            _isTheaterMode = _config.IsTheaterMode;
            ApplyTheaterModeLayout(_isTheaterMode);

            _loadingOverlay = new Border
            {
                Background = new SolidColorBrush(Color.FromArgb(225, 12, 12, 14)),
                Visibility = Visibility.Collapsed
            };
            var loadStack = new StackPanel
            {
                HorizontalAlignment = HorizontalAlignment.Center,
                VerticalAlignment = VerticalAlignment.Center,
                MaxWidth = 480
            };
            _lblLoadingTitle = new TextBlock
            {
                Text = "Carregando...",
                FontSize = 15,
                FontWeight = FontWeights.SemiBold,
                Foreground = new SolidColorBrush(Color.FromRgb(241, 241, 241)),
                TextAlignment = TextAlignment.Center,
                TextWrapping = TextWrapping.Wrap,
                Margin = new Thickness(0, 0, 0, 10)
            };
            var loadProg = new ProgressBar
            {
                Height = 3,
                Width = 260,
                IsIndeterminate = true,
                Background = new SolidColorBrush(Color.FromRgb(42, 42, 46)),
                Foreground = new SolidColorBrush(Color.FromRgb(255, 0, 51)),
                BorderThickness = new Thickness(0),
                Margin = new Thickness(0, 0, 0, 10)
            };
            _lblLoadingProgress = new TextBlock
            {
                Text = "Conectando ao YouTube...",
                FontSize = 12.5,
                Foreground = new SolidColorBrush(Color.FromRgb(170, 170, 170)),
                TextAlignment = TextAlignment.Center,
                Margin = new Thickness(0, 0, 0, 14)
            };
            _btnCancelLoad = new Button
            {
                Content = "Cancelar",
                Background = new SolidColorBrush(Color.FromRgb(38, 38, 42)),
                Foreground = new SolidColorBrush(Color.FromRgb(241, 241, 241)),
                Padding = new Thickness(18, 7, 18, 7),
                BorderThickness = new Thickness(0),
                HorizontalAlignment = HorizontalAlignment.Center,
                Cursor = Cursors.Hand
            };
            loadStack.Children.Add(_lblLoadingTitle);
            loadStack.Children.Add(loadProg);
            loadStack.Children.Add(_lblLoadingProgress);
            loadStack.Children.Add(_btnCancelLoad);
            _loadingOverlay.Child = loadStack;

            var overlayLayerRoot = new Grid { Background = Brushes.Transparent };
            overlayLayerRoot.Children.Add(_wpfOverlay.View);
            overlayLayerRoot.Children.Add(_loadingOverlay);

            // FlyleafHost nativo WPF (Direct3D 11 + overlay sincronizado sem airspace!)
            _flyleafHost = new FlyleafHost
            {
                Background = Brushes.Black,
                KeyBindings = AvailableWindows.None,
                ActivityTimeout = 0,
                Content = overlayLayerRoot
            };
            _playerHostContainer.Children.Add(_flyleafHost);
        }

        private void WireEvents()
        {
            SizeChanged += (s, e) => UpdateWatchStageDimensions();
            _watchPageScroll.SizeChanged += (s, e) => UpdateWatchStageDimensions();

            _navHome.Click += async (s, e) => await SwitchToNavSectionAsync("home");
            _miniNavHome.Click += async (s, e) => await SwitchToNavSectionAsync("home");
            _navSubscriptions.Click += async (s, e) => await SwitchToNavSectionAsync("subs");
            _miniNavSubs.Click += async (s, e) => await SwitchToNavSectionAsync("subs");
            _navHistory.Click += async (s, e) => await SwitchToNavSectionAsync("history");
            _miniNavHistory.Click += async (s, e) => await SwitchToNavSectionAsync("history");
            _navPlaylists.Click += async (s, e) => await SwitchToNavSectionAsync("playlists");
            _miniNavPlaylists.Click += async (s, e) => await SwitchToNavSectionAsync("playlists");
            _navYou.Click += async (s, e) => await SwitchToNavSectionAsync("you");
            _miniNavYou.Click += async (s, e) => await SwitchToNavSectionAsync("you");
            _navBackToPlayer.Click += (s, e) => ShowWatchView();
            _miniNavWatch.Click += (s, e) => ShowWatchView();
            _navOpenUrl.Click += (s, e) => PromptOpenVideoUrl();
            _btnBackToAccountPlaylists.Click += async (s, e) => await SwitchToNavSectionAsync("playlists", false);
            _btnPlayOpenedPlaylist.Click += (s, e) => PlayOpenedPlaylistAll();

            _btnBackFromChannel.Click += (s, e) => ExitChannelView();
            _btnChannelTabHome.Click += async (s, e) => await LoadChannelTabAsync("home");
            _btnChannelTabVideos.Click += async (s, e) => await LoadChannelTabAsync("videos");
            _btnChannelTabPlaylists.Click += async (s, e) => await LoadChannelTabAsync("playlists");
            _btnImportOpenedPlaylist.Click += (s, e) => ImportOpenedPlaylistToLibrary();

            _chipAll.Click += (s, e) => ApplySearchChip("all");
            _chipVideos.Click += (s, e) => ApplySearchChip("videos");
            _chipChannels.Click += (s, e) => ApplySearchChip("channels");
            _chipHideShorts.Click += (s, e) => ToggleHideShorts();
            _chipSponsorBlock.Click += (s, e) => ToggleSponsorBlock();

            _catalogGrid.ItemActivated += (idx, item) => HandleCardActivated(item);
            _catalogGrid.ChannelClicked += item => OpenChannelFromItem(item);
            _catalogGrid.FavoriteClicked += (idx, vid) => ToggleSaveVideo(vid);
            _catalogGrid.AddToQueueClicked += (idx, vid) => _controller.EnqueueNext(vid);
            _catalogGrid.LoadMoreRequested += async () => await LoadMoreAsync();

            _relatedGrid.ItemActivated += (idx, item) => HandleCardActivated(item);
            _relatedGrid.ChannelClicked += item => OpenChannelFromItem(item);
            _relatedGrid.FavoriteClicked += (idx, vid) => ToggleSaveVideo(vid);
            _relatedGrid.AddToQueueClicked += (idx, vid) => _controller.EnqueueNext(vid);
            _relatedGrid.LoadMoreRequested += async () => await LoadMoreAsync();
            _relatedGrid.WheelDeltaUnhandled += delta => ScrollWatchPageByDelta(delta);

            _flyleafHost.PreviewMouseWheel += (s, e) =>
            {
                if (!_isFullscreen)
                {
                    ScrollWatchPageByDelta(e.Delta);
                    e.Handled = true;
                }
            };
            _channelBannerPanel.PreviewMouseWheel += (s, e) =>
            {
                _catalogGrid.ScrollByDelta(e.Delta);
                e.Handled = true;
            };
            _lblCatalogHeader.PreviewMouseWheel += (s, e) =>
            {
                _catalogGrid.ScrollByDelta(e.Delta);
                e.Handled = true;
            };

            _watchChannelAvatar.MouseLeftButtonUp += (s, e) => OpenCurrentVideoChannel();
            _lblVideoChannel.MouseLeftButtonUp += (s, e) => OpenCurrentVideoChannel();
            _btnOpenCurrentChannel.Click += (s, e) => OpenCurrentVideoChannel();
            _btnToggleDescription.Click += (s, e) => ToggleDescriptionExpanded();
            _descriptionContainer.MouseLeftButtonUp += (s, e) =>
            {
                if (!_isDescriptionExpanded && !(e.OriginalSource is Hyperlink))
                {
                    ToggleDescriptionExpanded();
                }
            };
            _btnCancelLoad.Click += (s, e) => _controller.CancelLoad();

            // Eventos do HUD WPF
            _wpfOverlay.PlayPauseRequested += () => _controller.TogglePlayPause();
            _wpfOverlay.NextVideoRequested += () => PlayNextVideo();
            _wpfOverlay.SeekRequested += sec => _controller.SeekTo(sec);
            _wpfOverlay.OnUserSeekCommitted += sec => _controller.SeekTo(sec);
            _wpfOverlay.MuteToggleRequested += () => _controller.ToggleMute();
            _wpfOverlay.VolumeChanged += vol => _controller.SetVolume(vol);
            _wpfOverlay.QualityChanged += async q => await _controller.SwitchQualityAsync(q);
            _wpfOverlay.SpeedChanged += spd => _controller.SetSpeed(spd);
            _wpfOverlay.TheaterModeToggleRequested += () => ToggleTheaterMode(!_isTheaterMode);
            _wpfOverlay.FullscreenToggleRequested += () => ToggleFullscreen(!_isFullscreen);
            _wpfOverlay.ShortcutKeyPressed += HandlePlayerShortcutKey;
            _wpfOverlay.UnhandledMouseWheel += delta => ScrollWatchPageByDelta(delta);
        }

        private void ScrollWatchPageByDelta(int delta)
        {
            if (_isFullscreen || _watchPageScroll == null || _watchPageScroll.Visibility != Visibility.Visible)
                return;

            double step = -(delta / 120.0) * 90.0;
            double next = Math.Max(0.0, Math.Min(_watchPageScroll.ScrollableHeight, _watchPageScroll.VerticalOffset + step));
            _watchPageScroll.ScrollToVerticalOffset(next);
        }

        private void WireControllerEvents()
        {
            _controller.PlayerCreated += player =>
            {
                _flyleafHost.Player = player;
            };

            _controller.VideoStarted += video =>
            {
                _navBackToPlayer.Visibility = Visibility.Visible;
                _catalogGrid.PlayingVideoId = video.Id;
                _relatedGrid.PlayingVideoId = video.Id;

                _wpfOverlay.CancelAutoplayCountdown();
                _wpfOverlay.SetChapters(null);
                _wpfOverlay.SetAvailableQualities(video.AvailableResolutions);
                _wpfOverlay.SetOverlayActive(true);

                _lblVideoTitle.Text = video.Title;
                _lblVideoViewsDate.Text = FormatViewsAndDate(video);
                _lblVideoChannel.Text = !string.IsNullOrEmpty(video.Artist) ? video.Artist : "Canal do YouTube";
                _currentSubscriberCountText = "";
                _lblVideoSubInfo.Text = string.Format("Carregando canal  •  {0}p", _config.VideoQualityHeight);
                _isDescriptionExpanded = false;
                _txtVideoDescription.MaxHeight = 42.0;
                _txtVideoDescription.Inlines.Clear();
                _txtVideoDescription.Inlines.Add(new Run("Carregando descrição..."));
                _descriptionContainer.Cursor = Cursors.Hand;
                _lastDescriptionRaw = "";
                _btnToggleDescription.Content = "...mais";

                LoadWatchChannelAvatarAsync(video);
                StartBackgroundVideoMetadataFetch(video);

                bool isExistingPlaylistTrack = _openedPlaylistVideos.Exists(x => x.Id == video.Id);
                LoadRelatedVideosAsync(video.Id, !isExistingPlaylistTrack);
            };

            _controller.LoadingStateChanged += (loading, title, progress) =>
            {
                _lblLoadingTitle.Text = title;
                _lblLoadingProgress.Text = progress;
                _loadingOverlay.Visibility = loading ? Visibility.Visible : Visibility.Collapsed;
            };

            _controller.PlaybackProgressUpdated += (isPlaying, cur, buf, total) =>
            {
                if (!_wpfOverlay.IsUserDraggingScrubber)
                {
                    _wpfOverlay.UpdatePlaybackState(isPlaying, cur, buf, total);
                }
            };

            _controller.MediaMetricsUpdated += (actualQ, codec) =>
            {
                _wpfOverlay.UpdateQualityAndSpeedVisuals(actualQ, _controller.Player != null ? _controller.Player.Speed : 1.0);
                UpdateChannelSubInfoLine();
            };

            _controller.AudioSettingsUpdated += (vol, muted) =>
            {
                _wpfOverlay.UpdateAudioVisuals(vol, muted);
            };

            _controller.LiveStreamModeChanged += isLive =>
            {
                _wpfOverlay.SetLiveStreamMode(isLive);
            };

            _controller.StoryboardLoaded += sb =>
            {
                _wpfOverlay.SetVideoStoryboard(sb);
            };

            _controller.SponsorSegmentsLoaded += segs =>
            {
                _wpfOverlay.SetSponsorSegments(segs);
            };

            _controller.SponsorSkipped += (label, skipTo) =>
            {
                _wpfOverlay.ShowSponsorSkipToast(label);
            };

            _controller.VideoEndedWithNextCandidate += next =>
            {
                _wpfOverlay.StartAutoplayCountdown(next);
            };

            _controller.StatusNotification += msg => StatusNotification?.Invoke(msg);
            _controller.ToastRequested += msg => _wpfOverlay.ShowToast(msg);

            _controller.PlaybackError += (errMsg, isUnavailable) =>
            {
                if (isUnavailable)
                {
                    string? failedId = _controller.CurrentVideo?.Id;
                    _ = Task.Run(async () =>
                    {
                        await Task.Delay(3000).ConfigureAwait(false);
                        await Dispatcher.InvokeAsync(() =>
                        {
                            if (_controller.CurrentVideo != null && _controller.CurrentVideo.Id == failedId && !_controller.IsPlaying)
                            {
                                var next = _controller.GetNextVideoCandidate(true);
                                if (next != null)
                                {
                                    _wpfOverlay.ShowToast("⏭️ Avançando para o próximo vídeo disponível...");
                                    PlayVideo(next);
                                }
                            }
                        });
                    });
                }
            };
        }

        // =========================================================================
        // PUBLIC NAVIGATION & PLAYBACK API
        // =========================================================================

        public void ToggleSidebar()
        {
            if (_isFullscreen) return;
            if (IsWatchPageVisible)
            {
                _watchSidebarVisible = !_watchSidebarVisible;
            }
            else
            {
                _sidebarExpanded = !_sidebarExpanded;
            }
            ApplySidebarLayout();
            UpdateWatchStageDimensions();
        }

        private void ApplySidebarLayout()
        {
            if (_isFullscreen)
            {
                _sidebarColumn.Width = new GridLength(0);
                _sidebarPanel.Visibility = Visibility.Collapsed;
                _miniSidebarPanel.Visibility = Visibility.Collapsed;
                return;
            }

            if (IsWatchPageVisible)
            {
                _sidebarColumn.Width = _watchSidebarVisible ? new GridLength(216) : new GridLength(0);
                _sidebarPanel.Visibility = _watchSidebarVisible ? Visibility.Visible : Visibility.Collapsed;
                _miniSidebarPanel.Visibility = Visibility.Collapsed;
                return;
            }

            // No Catálogo: alterna entre Barra Completa (216px) e Mini-Barra (72px), igual ao YouTube!
            _sidebarColumn.Width = _sidebarExpanded ? new GridLength(216) : new GridLength(72);
            _sidebarPanel.Visibility = _sidebarExpanded ? Visibility.Visible : Visibility.Collapsed;
            _miniSidebarPanel.Visibility = _sidebarExpanded ? Visibility.Collapsed : Visibility.Visible;
        }

        private void PlayOpenedPlaylistAll()
        {
            if (_openedPlaylistVideos == null || _openedPlaylistVideos.Count == 0) return;
            _controller.UpNextQueue.Clear();
            for (int i = 1; i < _openedPlaylistVideos.Count; i++)
            {
                _controller.UpNextQueue.Add(_openedPlaylistVideos[i]);
            }
            PlayVideo(_openedPlaylistVideos[0]);
            StatusNotification?.Invoke(string.Format("Reproduzindo playlist ({0} vídeos na fila).", _openedPlaylistVideos.Count));
        }

        public async Task EnsureInitialFeedLoadedAsync()
        {
            if (_initialHomeLoaded) return;
            _initialHomeLoaded = true;
            await SwitchToNavSectionAsync("home");
        }

        public void OnAuthChanged()
        {
            _homeVideos.Clear();
            _subscriptionsVideos.Clear();
            _historyVideos.Clear();
            _accountPlaylists.Clear();
            _feedContinuationToken = "";

            if (_activeNavSection == "home" || _activeNavSection == "subs" ||
                _activeNavSection == "history" || _activeNavSection == "playlists")
            {
                _ = SwitchToNavSectionAsync(_activeNavSection, true);
            }
        }

        public async Task RefreshCurrentPageAsync()
        {
            if (IsWatchPageVisible && _controller.CurrentVideo != null)
            {
                StatusNotification?.Invoke("🔄 Recarregando vídeos relacionados...");
                LoadRelatedVideosAsync(_controller.CurrentVideo.Id, true);
                return;
            }
            if (_isChannelView && !string.IsNullOrEmpty(_activeChannelId))
            {
                await LoadChannelTabAsync(_activeChannelTab);
                return;
            }
            await SwitchToNavSectionAsync(_activeNavSection, true);
        }

        public async Task SearchAsync(string query)
        {
            if (string.IsNullOrWhiteSpace(query)) return;

            int reqSeq = ++_navRequestSeq;
            if (_isFullscreen) ToggleFullscreen(false);
            ExitWatchPage();

            _activeNavSection = "search";
            _isChannelView = false;
            _channelBannerPanel.Visibility = Visibility.Collapsed;
            _searchChipsBar.Visibility = Visibility.Visible;
            _activeSearchChip = "all";
            _searchContinuationToken = "";
            UpdateSidebarSelection();
            UpdateFilterChipsVisuals();

            _lblCatalogHeader.Text = string.Format("⏳ Buscando \"{0}\" no YouTube...", query.Trim());
            _catalogGrid.IsLoadingSkeleton = true;
            _catalogGrid.HasMoreItems = false;

            try
            {
                var page = await _ytService.SearchPageAsync(query.Trim(), "video", _config);
                if (reqSeq != _navRequestSeq) return;
                _searchVideos = page.Items ?? new List<TrackItem>();
                _searchContinuationToken = page.ContinuationToken ?? "";

                _lblCatalogHeader.Text = string.Format("🔍 Resultados para \"{0}\" ({1} itens)", query.Trim(), _searchVideos.Count);
                _catalogGrid.HasMoreItems = !string.IsNullOrEmpty(_searchContinuationToken);
                ApplySearchChip("all");
                StatusNotification?.Invoke(string.Format("🔍 {0} resultados encontrados para \"{1}\".", _searchVideos.Count, query.Trim()));
            }
            catch (Exception ex)
            {
                if (reqSeq == _navRequestSeq)
                    _lblCatalogHeader.Text = "⚠️ Erro na busca: " + ex.Message;
            }
            finally
            {
                if (reqSeq == _navRequestSeq) _catalogGrid.IsLoadingSkeleton = false;
            }
        }

        public async Task SwitchToNavSectionAsync(string section, bool forceRefresh = false)
        {
            int reqSeq = ++_navRequestSeq;
            if (_isFullscreen) ToggleFullscreen(false);
            if (IsWatchPageVisible)
            {
                _wpfOverlay.SetOverlayActive(false);
                _controller.Stop();
            }

            _activeNavSection = section ?? "home";
            _isChannelView = false;
            _channelBannerPanel.Visibility = Visibility.Collapsed;
            _searchChipsBar.Visibility = (_activeNavSection == "search") ? Visibility.Visible : Visibility.Collapsed;
            _btnBackToAccountPlaylists.Visibility = Visibility.Collapsed;
            _btnPlayOpenedPlaylist.Visibility = Visibility.Collapsed;
            _openedChannelPlaylist = null;
            _openedPlaylistContinuationToken = "";
            _feedContinuationToken = "";

            _watchPageScroll.Visibility = Visibility.Collapsed;
            _catalogViewGrid.Visibility = Visibility.Visible;
            ApplySidebarLayout();
            UpdateSidebarSelection();

            if (_activeNavSection == "home")
            {
                _lblCatalogHeader.Text = "⏳ Carregando seus vídeos recomendados...";
                _catalogGrid.IsLoadingSkeleton = true;
                try
                {
                    var page = await YouTubeAccountFeedService.Shared.FetchHomeFeedAsync(_config, forceRefresh);
                    if (reqSeq != _navRequestSeq) return;
                    if ((page == null || page.Items.Count == 0) && (page == null || !page.SessionExpired))
                    {
                        var fb = await _ytService.FetchUrlOrPlaylistAsync("https://www.youtube.com/", _config, true, 1, 50);
                        if (reqSeq != _navRequestSeq) return;
                        if (fb.Item1 != null && fb.Item1.Count > 0)
                            page = new AccountFeedPage { Items = fb.Item1, UsedAccountAuth = GoogleSessionService.Shared.HasStoredSession(_config) };
                    }

                    AccountStatusMayHaveChanged?.Invoke();
                    if (page != null && page.Items.Count > 0)
                    {
                        _homeVideos = page.Items;
                        _feedContinuationToken = page.ContinuationToken ?? "";
                        _lblCatalogHeader.Text = string.Format("🏠 Início • {0} vídeos recomendados", _homeVideos.Count);
                        _catalogGrid.HasMoreItems = !string.IsNullOrEmpty(_feedContinuationToken);
                        _catalogGrid.SetItems(FilterItemsForDisplay(_homeVideos), true);
                        StatusNotification?.Invoke(string.Format("🏠 {0} vídeos recomendados carregados!", _homeVideos.Count));
                    }
                    else
                    {
                        string msg = page?.StatusMessage ?? "Não foi possível carregar os recomendados.";
                        _lblCatalogHeader.Text = "⚠️ " + msg;
                        _catalogGrid.SetItems(new List<TrackItem>(), true);
                    }
                }
                finally
                {
                    if (reqSeq == _navRequestSeq) _catalogGrid.IsLoadingSkeleton = false;
                }
            }
            else if (_activeNavSection == "subs")
            {
                _lblCatalogHeader.Text = "⏳ Carregando inscrições da sua conta...";
                _catalogGrid.IsLoadingSkeleton = true;
                try
                {
                    var page = await YouTubeAccountFeedService.Shared.FetchSubscriptionsFeedAsync(_config, forceRefresh);
                    if (reqSeq != _navRequestSeq) return;
                    AccountStatusMayHaveChanged?.Invoke();
                    if (page.Items.Count > 0)
                    {
                        _subscriptionsVideos = page.Items;
                        _feedContinuationToken = page.ContinuationToken ?? "";
                        _lblCatalogHeader.Text = string.Format("🔔 Inscrições • {0} vídeos recentes dos seus canais", _subscriptionsVideos.Count);
                        _catalogGrid.HasMoreItems = !string.IsNullOrEmpty(_feedContinuationToken);
                        _catalogGrid.SetItems(FilterItemsForDisplay(_subscriptionsVideos), true);
                        StatusNotification?.Invoke(string.Format("🔔 {0} vídeos de canais inscritos carregados!", _subscriptionsVideos.Count));
                    }
                    else
                    {
                        string msg = !string.IsNullOrEmpty(page.StatusMessage) ? page.StatusMessage : "Conecte sua Conta Google para ver suas inscrições.";
                        _lblCatalogHeader.Text = "⚠️ " + msg;
                        _catalogGrid.HasMoreItems = false;
                        _catalogGrid.SetItems(new List<TrackItem>(), true);
                    }
                }
                finally
                {
                    if (reqSeq == _navRequestSeq) _catalogGrid.IsLoadingSkeleton = false;
                }
            }
            else if (_activeNavSection == "history")
            {
                _lblCatalogHeader.Text = "⏳ Sincronizando histórico local e da conta Google...";
                _catalogGrid.IsLoadingSkeleton = true;
                try
                {
                    var page = await YouTubeAccountFeedService.Shared.FetchHistoryFeedAsync(_config, forceRefresh);
                    if (reqSeq != _navRequestSeq) return;
                    AccountStatusMayHaveChanged?.Invoke();
                    _historyVideos = page.Items;
                    _feedContinuationToken = page.ContinuationToken ?? "";
                    _catalogGrid.HasMoreItems = !string.IsNullOrEmpty(_feedContinuationToken);
                    _catalogGrid.SetItems(FilterItemsForDisplay(_historyVideos), true);

                    if (_historyVideos.Count > 0)
                    {
                        string src = page.UsedAccountAuth ? "Conta Google + Local" : "Local";
                        _lblCatalogHeader.Text = string.Format("🕒 Histórico ({0}) • {1} vídeos assistidos recentemente", src, _historyVideos.Count);
                        StatusNotification?.Invoke(string.Format("🕒 Histórico ({0}): {1} vídeo(s).", src, _historyVideos.Count));
                    }
                    else
                    {
                        _lblCatalogHeader.Text = "🕒 Histórico • Nenhum vídeo assistido recentemente ainda";
                    }
                }
                finally
                {
                    if (reqSeq == _navRequestSeq) _catalogGrid.IsLoadingSkeleton = false;
                }
            }
            else if (_activeNavSection == "playlists")
            {
                _lblCatalogHeader.Text = "⏳ Carregando suas playlists da conta Google...";
                _catalogGrid.IsLoadingSkeleton = true;
                try
                {
                    var page = await YouTubeAccountFeedService.Shared.FetchAccountPlaylistsAsync(_config, forceRefresh);
                    if (reqSeq != _navRequestSeq) return;
                    AccountStatusMayHaveChanged?.Invoke();
                    if (page.Items.Count > 0)
                    {
                        _accountPlaylists = page.Items;
                        _feedContinuationToken = page.ContinuationToken ?? "";
                        _lblCatalogHeader.Text = string.Format("📑 Suas Playlists • {0} playlist(s) da conta Google", _accountPlaylists.Count);
                        _catalogGrid.HasMoreItems = !string.IsNullOrEmpty(_feedContinuationToken);
                        _catalogGrid.SetItems(_accountPlaylists, true);
                        StatusNotification?.Invoke(string.Format("📑 {0} playlist(s) carregadas!", _accountPlaylists.Count));
                    }
                    else
                    {
                        string msg = !string.IsNullOrEmpty(page.StatusMessage) ? page.StatusMessage : "Nenhuma playlist encontrada na conta conectada.";
                        _lblCatalogHeader.Text = "📑 Playlists • " + msg;
                        _catalogGrid.HasMoreItems = false;
                        _catalogGrid.SetItems(new List<TrackItem>(), true);
                    }
                }
                finally
                {
                    if (reqSeq == _navRequestSeq) _catalogGrid.IsLoadingSkeleton = false;
                }
            }
            else if (_activeNavSection == "you")
            {
                _catalogGrid.IsLoadingSkeleton = false;
                _lblCatalogHeader.Text = string.Format("👤 Você • Seus Vídeos Salvos ({0} vídeos)", _config.SavedVideos.Count);
                _catalogGrid.HasMoreItems = false;
                _catalogGrid.SetItems(FilterItemsForDisplay(_config.SavedVideos), true);
            }
        }

        public void PlayVideo(TrackItem video, double overrideStartSeconds = 0.0)
        {
            if (video == null || string.IsNullOrEmpty(video.Id)) return;
            if (video.IsChannel) { OpenChannelFromItem(video); return; }
            if (video.IsPlaylistCard) { OpenPlaylistItemsAsync(video); return; }

            ShowWatchView();
            _ = _controller.PlayVideoAsync(video, overrideStartSeconds);
        }

        public void PlayVideoByUrl(string url)
        {
            if (string.IsNullOrWhiteSpace(url)) return;
            string normalized = ExternalLinkServer.NormalizeExternalUrl(url);
            var idMatch = Regex.Match(normalized, @"(?:[?&]v=|youtu\.be/|/shorts/|/live/|/embed/)([a-zA-Z0-9_-]{11})");
            string vidId = idMatch.Success ? idMatch.Groups[1].Value : (normalized.Length == 11 ? normalized : "");
            double startSec = ExternalLinkServer.ExtractStartSecondsFromUrl(normalized);
            if (!string.IsNullOrEmpty(vidId))
            {
                PlayVideo(new TrackItem
                {
                    Id = vidId,
                    Title = "Carregando vídeo do YouTube...",
                    Artist = "YouTube",
                    CustomThumbUrl = "https://i.ytimg.com/vi/" + vidId + "/hqdefault.jpg"
                }, startSec);
            }
        }

        public void ShowWatchView()
        {
            _catalogViewGrid.Visibility = Visibility.Collapsed;
            _watchPageScroll.Visibility = Visibility.Visible;
            _watchPageScroll.ScrollToTop();
            _watchSidebarVisible = false;
            ApplySidebarLayout();
            _wpfOverlay.SetOverlayActive(true);
            UpdateSidebarSelection();
            UpdateWatchStageDimensions();
        }

        public void ExitWatchPage()
        {
            if (_isFullscreen) ToggleFullscreen(false);
            _wpfOverlay.SetOverlayActive(false);
            _controller.PauseIfPlaying();
            _watchPageScroll.Visibility = Visibility.Collapsed;
            _catalogViewGrid.Visibility = Visibility.Visible;
            ApplySidebarLayout();
            UpdateSidebarSelection();
        }

        public void ToggleTheaterMode(bool enable)
        {
            if (_isFullscreen)
            {
                ToggleFullscreen(false);
            }
            if (_isTheaterMode == enable) return;
            _isTheaterMode = enable;
            _config.IsTheaterMode = enable;
            _config.SaveDebounced();

            ApplyTheaterModeLayout(enable);
            UpdateWatchStageDimensions();
            _wpfOverlay.ShowOsd(enable ? "Modo Teatro" : "Modo Padrão");
            _wpfOverlay.ForceImmediateGpuRender();
        }

        private void ApplyTheaterModeLayout(bool theater)
        {
            if (_playerStageFrame == null || _theaterStageContainer == null || _normalStageContainer == null)
                return;

            _wpfOverlay.IsTheaterMode = theater;

            // Move o PlayerStageFrame entre o palco full-bleed (Teatro) e a coluna esquerda (Padrão)
            // sem recriar o FlyleafHost e sem interromper 1 único frame do vídeo
            if (_theaterStageContainer.Children.Contains(_playerStageFrame))
                _theaterStageContainer.Children.Remove(_playerStageFrame);
            if (_normalStageContainer.Children.Contains(_playerStageFrame))
                _normalStageContainer.Children.Remove(_playerStageFrame);

            if (theater)
            {
                _playerStageFrame.CornerRadius = new CornerRadius(0);
                _theaterStageContainer.Children.Add(_playerStageFrame);
                _theaterStageContainer.Visibility = Visibility.Visible;
                _normalStageContainer.Visibility = Visibility.Collapsed;
                if (_watchTwoColumnGrid != null)
                    _watchTwoColumnGrid.Margin = new Thickness(24, 14, 24, 32);
            }
            else
            {
                _playerStageFrame.CornerRadius = new CornerRadius(12);
                _normalStageContainer.Children.Add(_playerStageFrame);
                _normalStageContainer.Visibility = Visibility.Visible;
                _theaterStageContainer.Visibility = Visibility.Collapsed;
                if (_watchTwoColumnGrid != null)
                    _watchTwoColumnGrid.Margin = new Thickness(24, 12, 24, 32);
            }
        }

        public void ToggleFullscreen(bool enable)
        {
            if (_isFullscreen == enable) return;
            _isFullscreen = enable;
            _wpfOverlay.IsFullscreenMode = enable;

            if (enable)
            {
                _sidebarColumn.Width = new GridLength(0);
                _sidebarPanel.Visibility = Visibility.Collapsed;
                _watchPageScroll.Visibility = Visibility.Collapsed;
                _catalogViewGrid.Visibility = Visibility.Collapsed;

                if (_playerHostContainer.Children.Contains(_flyleafHost))
                {
                    _playerHostContainer.Children.Remove(_flyleafHost);
                }
                _fullscreenHostLayer.Children.Clear();
                _fullscreenHostLayer.Children.Add(_flyleafHost);
                _fullscreenHostLayer.Visibility = Visibility.Visible;
            }
            else
            {
                _fullscreenHostLayer.Visibility = Visibility.Collapsed;
                if (_fullscreenHostLayer.Children.Contains(_flyleafHost))
                {
                    _fullscreenHostLayer.Children.Remove(_flyleafHost);
                }
                _playerHostContainer.Children.Clear();
                _playerHostContainer.Children.Add(_flyleafHost);

                _watchPageScroll.Visibility = Visibility.Visible;
                ApplySidebarLayout();
                UpdateWatchStageDimensions();
            }

            FullscreenChanged?.Invoke(enable);
            _wpfOverlay.ForceImmediateGpuRender();
        }

        public void HandlePlayerShortcutKey(Key key)
        {
            if (!IsWatchPageVisible && !_isFullscreen) return;

            switch (key)
            {
                case Key.Space:
                case Key.K:
                    _controller.TogglePlayPause();
                    break;
                case Key.J:
                    _controller.SeekRelative(-10);
                    _wpfOverlay.ShowOsd("⏪ -10s");
                    break;
                case Key.L:
                    _controller.SeekRelative(10);
                    _wpfOverlay.ShowOsd("⏩ +10s");
                    break;
                case Key.Left:
                    _controller.SeekRelative(-5);
                    _wpfOverlay.ShowOsd("⏪ -5s");
                    break;
                case Key.Right:
                    _controller.SeekRelative(5);
                    _wpfOverlay.ShowOsd("⏩ +5s");
                    break;
                case Key.M:
                    _controller.ToggleMute();
                    _wpfOverlay.ShowOsd(_config.IsVideoMuted ? "🔇 Mudo" : "🔊 Som");
                    break;
                case Key.T:
                    ToggleTheaterMode(!_isTheaterMode);
                    break;
                case Key.F:
                    ToggleFullscreen(!_isFullscreen);
                    break;
                case Key.Escape:
                    if (_isFullscreen) ToggleFullscreen(false);
                    break;
                case Key.Up:
                    _controller.ChangeVolumeBy(0.05);
                    _wpfOverlay.ShowOsd(string.Format("🔊 {0}%", (int)Math.Round(_config.VideoVolume * 100)));
                    break;
                case Key.Down:
                    _controller.ChangeVolumeBy(-0.05);
                    _wpfOverlay.ShowOsd(string.Format("🔉 {0}%", (int)Math.Round(_config.VideoVolume * 100)));
                    break;
            }
        }

        // =========================================================================
        // CHANNELS, PLAYLISTS, RELATED & METADATA
        // =========================================================================

        public async void OpenChannelFromItem(TrackItem item)
        {
            if (item == null) return;
            string chId = item.ChannelId ?? "";
            string chName = item.IsChannel ? item.Title : item.Artist;
            if (string.IsNullOrWhiteSpace(chId) && string.IsNullOrWhiteSpace(chName)) return;

            if (_isFullscreen) ToggleFullscreen(false);
            ExitWatchPage();

            _isChannelView = true;
            _activeChannelId = chId;
            _activeChannelName = chName;
            _activeChannelTab = "home";
            _channelBannerPanel.Visibility = Visibility.Visible;
            _searchChipsBar.Visibility = Visibility.Collapsed;
            _btnImportOpenedPlaylist.Visibility = Visibility.Collapsed;
            _lblChannelTitle.Text = chName;
            _lblChannelSub.Text = "Carregando canal...";

            await LoadChannelTabAsync("home");
        }

        private async Task LoadChannelTabAsync(string tab)
        {
            _activeChannelTab = tab;
            _btnImportOpenedPlaylist.Visibility = Visibility.Collapsed;
            UpdateChannelTabsVisual(tab);

            _lblCatalogHeader.Text = string.Format("⏳ Carregando {0} de {1}...", tab, _activeChannelName);
            _catalogGrid.IsLoadingSkeleton = true;

            try
            {
                var data = await _ytService.FetchChannelPageAsync(_activeChannelId, _activeChannelName, tab);
                if (!string.IsNullOrEmpty(data.ChannelId)) _activeChannelId = data.ChannelId;
                if (!string.IsNullOrEmpty(data.ChannelName)) _activeChannelName = data.ChannelName;

                _lblChannelTitle.Text = _activeChannelName;
                _lblChannelSub.Text = !string.IsNullOrEmpty(data.SubscriberText) ? data.SubscriberText : "Canal do YouTube";
                LoadEllipseAvatarAsync(_channelAvatarEllipse, data.AvatarUrl, 128);

                _channelCurrentItems = data.Items ?? new List<TrackItem>();
                _channelContinuationToken = data.ContinuationToken ?? "";

                _lblCatalogHeader.Text = string.Format("🎬 {0} — {1} ({2} itens)",
                    _activeChannelName,
                    tab == "playlists" ? "Playlists" : (tab == "videos" ? "Vídeos" : "Início"),
                    _channelCurrentItems.Count);

                _catalogGrid.HasMoreItems = !string.IsNullOrEmpty(_channelContinuationToken);
                _catalogGrid.SetItems(tab == "playlists" ? _channelCurrentItems : FilterItemsForDisplay(_channelCurrentItems), true);
            }
            finally
            {
                _catalogGrid.IsLoadingSkeleton = false;
            }
        }

        private async void OpenPlaylistItemsAsync(TrackItem playlistCard)
        {
            if (playlistCard == null || string.IsNullOrEmpty(playlistCard.PlaylistId)) return;

            if (IsWatchPageVisible) ExitWatchPage();

            _openedChannelPlaylist = playlistCard;
            _openedPlaylistContinuationToken = "";
            _lblCatalogHeader.Text = "⏳ Carregando vídeos da playlist \"" + playlistCard.Title + "\"...";
            _catalogGrid.IsLoadingSkeleton = true;

            try
            {
                var page = await YouTubeAccountFeedService.Shared.FetchPlaylistItemsAsync(playlistCard.PlaylistId, _config);
                if (page.Items.Count > 0)
                {
                    _openedPlaylistVideos = page.Items;
                    _openedPlaylistContinuationToken = page.ContinuationToken ?? "";
                }
                else
                {
                    var fb = await _ytService.FetchUrlOrPlaylistAsync(playlistCard.PlaylistId, _config, false);
                    _openedPlaylistVideos = fb.Item1 ?? new List<TrackItem>();
                }
            }
            finally
            {
                _catalogGrid.IsLoadingSkeleton = false;
            }

            if (_isChannelView)
            {
                _btnBackToAccountPlaylists.Visibility = Visibility.Collapsed;
                _btnPlayOpenedPlaylist.Visibility = _openedPlaylistVideos.Count > 0 ? Visibility.Visible : Visibility.Collapsed;
                _btnImportOpenedPlaylist.Visibility = _openedPlaylistVideos.Count > 0 ? Visibility.Visible : Visibility.Collapsed;
                _btnImportOpenedPlaylist.Content = string.Format("Salvar Playlist ({0})", _openedPlaylistVideos.Count);
                _lblCatalogHeader.Text = string.Format("Playlist de {0}: \"{1}\" ({2} vídeos)",
                    _activeChannelName, playlistCard.Title, _openedPlaylistVideos.Count);
            }
            else
            {
                _btnBackToAccountPlaylists.Visibility = Visibility.Visible;
                _btnPlayOpenedPlaylist.Visibility = _openedPlaylistVideos.Count > 0 ? Visibility.Visible : Visibility.Collapsed;
                _lblCatalogHeader.Text = string.Format("Playlist: \"{0}\" ({1} vídeos)",
                    playlistCard.Title, _openedPlaylistVideos.Count);
            }

            _catalogGrid.HasMoreItems = !string.IsNullOrEmpty(_openedPlaylistContinuationToken);
            _catalogGrid.SetItems(_openedPlaylistVideos, true);
            StatusNotification?.Invoke(string.Format("📂 Playlist '{0}' carregada com {1} vídeos.", playlistCard.Title, _openedPlaylistVideos.Count));
        }

        private void ImportOpenedPlaylistToLibrary()
        {
            if (_openedChannelPlaylist == null || _openedPlaylistVideos.Count == 0) return;
            _config.Playlists.Add(new UserPlaylist
            {
                Name = _openedChannelPlaylist.Title,
                RemoteUrl = "https://www.youtube.com/playlist?list=" + _openedChannelPlaylist.PlaylistId,
                Tracks = new List<TrackItem>(_openedPlaylistVideos)
            });
            _config.Save();
            PlaylistImportedToLibrary?.Invoke();
            StatusNotification?.Invoke("✅ Playlist salva na sua biblioteca!");
        }

        private void ExitChannelView()
        {
            _isChannelView = false;
            _channelBannerPanel.Visibility = Visibility.Collapsed;
            _ = SwitchToNavSectionAsync(_activeNavSection == "search" ? "home" : _activeNavSection, false);
        }

        private void LoadRelatedVideosAsync(string videoId, bool updateGrid)
        {
            try { _watchNextCts?.Cancel(); } catch { }
            _watchNextCts = new CancellationTokenSource();
            var ct = _watchNextCts.Token;

            if (updateGrid)
            {
                _relatedGrid.IsLoadingSkeleton = true;
            }

            _ = Task.Run(async () =>
            {
                try
                {
                    var details = await _ytService.FetchWatchNextDetailsAsync(videoId, _config, 36).ConfigureAwait(false);
                    if (ct.IsCancellationRequested) return;

                    await Dispatcher.InvokeAsync(() =>
                    {
                        if (ct.IsCancellationRequested || _controller.CurrentVideo == null || _controller.CurrentVideo.Id != videoId)
                            return;

                        if (!string.IsNullOrWhiteSpace(details.SubscriberCountText))
                        {
                            _currentSubscriberCountText = details.SubscriberCountText;
                            UpdateChannelSubInfoLine();
                        }

                        if (updateGrid && details.RelatedVideos != null && details.RelatedVideos.Count > 0)
                        {
                            _relatedVideos = FilterItemsForDisplay(details.RelatedVideos.FindAll(x => x.Id != videoId));
                            _relatedGrid.SetItems(_relatedVideos, true);
                            UpdateWatchStageDimensions();
                        }
                    });
                }
                catch { }
                finally
                {
                    await Dispatcher.InvokeAsync(() =>
                    {
                        if (!ct.IsCancellationRequested) _relatedGrid.IsLoadingSkeleton = false;
                    });
                }
            }, ct);
        }

        private void StartBackgroundVideoMetadataFetch(TrackItem video)
        {
            try { _metadataFetchCts?.Cancel(); } catch { }
            _metadataFetchCts = new CancellationTokenSource();
            var ct = _metadataFetchCts.Token;
            string vidId = video.Id;

            _ = Task.Run(async () =>
            {
                try
                {
                    var meta = await ProgressiveVideoSession.FetchVideoMetadataAsync(
                        vidId, YtDlpResolver.GetYtDlpDirectory(), _config.BrowserForCookies, _config.CookiesFilePath, ct).ConfigureAwait(false);
                    if (ct.IsCancellationRequested || meta == null) return;

                    await Dispatcher.InvokeAsync(() =>
                    {
                        if (ct.IsCancellationRequested || _controller.CurrentVideo == null || _controller.CurrentVideo.Id != vidId)
                            return;

                        if (!string.IsNullOrWhiteSpace(meta.Title))
                        {
                            _controller.CurrentVideo.Title = meta.Title;
                            _lblVideoTitle.Text = meta.Title;
                        }
                        if (meta.ViewCount > 0 || !string.IsNullOrWhiteSpace(meta.UploadDateRaw))
                        {
                            string views = meta.ViewCount > 0 ? meta.FormattedViews : _controller.CurrentVideo.FormattedViews;
                            string date = !string.IsNullOrWhiteSpace(meta.FormattedUploadDate) ? meta.FormattedUploadDate : _controller.CurrentVideo.FormattedUploadDate;
                            _lblVideoViewsDate.Text = !string.IsNullOrWhiteSpace(date) ? (views + "  •  " + date) : views;
                        }
                        if (meta.AvailableResolutions != null && meta.AvailableResolutions.Count > 0)
                        {
                            _wpfOverlay.SetAvailableQualities(meta.AvailableResolutions);
                        }
                        if (!string.IsNullOrWhiteSpace(meta.Description))
                        {
                            _lastDescriptionRaw = meta.Description;
                            RenderDescriptionWithTimestamps(_lastDescriptionRaw);

                            var chapters = ProgressiveVideoSession.ExtractChaptersFromDescription(meta.Description, _controller.GetTotalSeconds());
                            if (chapters != null && chapters.Count > 0)
                            {
                                _wpfOverlay.SetChapters(chapters);
                            }
                        }
                    });
                }
                catch { }
            }, ct);
        }

        private void RenderDescriptionWithTimestamps(string rawDescription)
        {
            _txtVideoDescription.Inlines.Clear();
            if (string.IsNullOrWhiteSpace(rawDescription))
            {
                _txtVideoDescription.Inlines.Add(new Run("Sem descrição disponível."));
                return;
            }

            int lastPos = 0;
            foreach (Match m in TimestampRegex.Matches(rawDescription))
            {
                if (m.Index > lastPos)
                {
                    _txtVideoDescription.Inlines.Add(new Run(rawDescription.Substring(lastPos, m.Index - lastPos)));
                }

                double targetSec = ParseTimestampSeconds(m.Value);
                var link = new Hyperlink(new Run(m.Value))
                {
                    Foreground = new SolidColorBrush(Color.FromRgb(62, 166, 255)),
                    TextDecorations = null
                };
                link.Click += (s, e) => _controller.SeekTo(targetSec);
                _txtVideoDescription.Inlines.Add(link);
                lastPos = m.Index + m.Length;
            }

            if (lastPos < rawDescription.Length)
            {
                _txtVideoDescription.Inlines.Add(new Run(rawDescription.Substring(lastPos)));
            }
        }

        private static double ParseTimestampSeconds(string ts)
        {
            string[] parts = ts.Split(':');
            if (parts.Length == 2 && int.TryParse(parts[0], out int m) && int.TryParse(parts[1], out int s))
                return m * 60 + s;
            if (parts.Length == 3 && int.TryParse(parts[0], out int h) && int.TryParse(parts[1], out int mm) && int.TryParse(parts[2], out int ss))
                return h * 3600 + mm * 60 + ss;
            return 0;
        }

        private void ToggleDescriptionExpanded()
        {
            _isDescriptionExpanded = !_isDescriptionExpanded;
            _txtVideoDescription.MaxHeight = _isDescriptionExpanded ? double.PositiveInfinity : 42.0;
            _descriptionContainer.Cursor = _isDescriptionExpanded ? Cursors.Arrow : Cursors.Hand;
            _btnToggleDescription.Content = _isDescriptionExpanded ? "Mostrar menos" : "...mais";
            if (_isDescriptionExpanded && _txtVideoDescription.Inlines.Count == 0)
            {
                RenderDescriptionWithTimestamps(_lastDescriptionRaw);
            }
        }

        private async Task LoadMoreAsync()
        {
            if (_isLoadingMore) return;

            // 1. Channel pagination
            if (_isChannelView && !string.IsNullOrEmpty(_channelContinuationToken))
            {
                _isLoadingMore = true;
                _catalogGrid.IsLoadingMore = true;
                try
                {
                    var next = await _ytService.FetchChannelContinuationAsync(_channelContinuationToken, _activeChannelId, _activeChannelName);
                    _channelContinuationToken = next.ContinuationToken ?? "";
                    _catalogGrid.HasMoreItems = !string.IsNullOrEmpty(_channelContinuationToken);
                    if (next.Items != null && next.Items.Count > 0)
                    {
                        _channelCurrentItems.AddRange(next.Items);
                        _catalogGrid.AppendItems(FilterItemsForDisplay(next.Items));
                    }
                }
                finally
                {
                    _isLoadingMore = false;
                    _catalogGrid.IsLoadingMore = false;
                }
                return;
            }

            // 2. Search pagination
            if (_activeNavSection == "search" && !string.IsNullOrEmpty(_searchContinuationToken))
            {
                _isLoadingMore = true;
                _catalogGrid.IsLoadingMore = true;
                try
                {
                    var next = await _ytService.FetchSearchContinuationAsync(_searchContinuationToken, true);
                    _searchContinuationToken = next.ContinuationToken ?? "";
                    _catalogGrid.HasMoreItems = !string.IsNullOrEmpty(_searchContinuationToken);
                    if (next.Items != null && next.Items.Count > 0)
                    {
                        _searchVideos.AddRange(next.Items);
                        _catalogGrid.AppendItems(FilterSearchItems(next.Items));
                    }
                }
                finally
                {
                    _isLoadingMore = false;
                    _catalogGrid.IsLoadingMore = false;
                }
                return;
            }

            // 3. Opened Playlist pagination
            if (!string.IsNullOrEmpty(_openedPlaylistContinuationToken) && _openedChannelPlaylist != null)
            {
                _isLoadingMore = true;
                _catalogGrid.IsLoadingMore = true;
                try
                {
                    var next = await YouTubeAccountFeedService.Shared.FetchPlaylistItemsAsync(_openedChannelPlaylist.PlaylistId, _config, _openedPlaylistContinuationToken);
                    _openedPlaylistContinuationToken = next.ContinuationToken ?? "";
                    _catalogGrid.HasMoreItems = !string.IsNullOrEmpty(_openedPlaylistContinuationToken);
                    if (next.Items.Count > 0)
                    {
                        _openedPlaylistVideos.AddRange(next.Items);
                        _catalogGrid.AppendItems(next.Items);
                    }
                }
                finally
                {
                    _isLoadingMore = false;
                    _catalogGrid.IsLoadingMore = false;
                }
                return;
            }

            // 4. Account Feeds pagination
            if (!string.IsNullOrEmpty(_feedContinuationToken))
            {
                _isLoadingMore = true;
                _catalogGrid.IsLoadingMore = true;
                try
                {
                    AccountFeedPage? next = _activeNavSection switch
                    {
                        "home" => await YouTubeAccountFeedService.Shared.FetchHomeFeedAsync(_config, true, _feedContinuationToken),
                        "subs" => await YouTubeAccountFeedService.Shared.FetchSubscriptionsFeedAsync(_config, true, _feedContinuationToken),
                        "history" => await YouTubeAccountFeedService.Shared.FetchHistoryFeedAsync(_config, true, _feedContinuationToken),
                        "playlists" => await YouTubeAccountFeedService.Shared.FetchAccountPlaylistsAsync(_config, true, _feedContinuationToken),
                        _ => null
                    };

                    _feedContinuationToken = next?.ContinuationToken ?? "";
                    _catalogGrid.HasMoreItems = !string.IsNullOrEmpty(_feedContinuationToken);
                    if (next != null && next.Items.Count > 0)
                    {
                        var items = _activeNavSection == "playlists" ? next.Items : FilterItemsForDisplay(next.Items);
                        _catalogGrid.AppendItems(items);
                    }
                }
                finally
                {
                    _isLoadingMore = false;
                    _catalogGrid.IsLoadingMore = false;
                }
            }
        }

        // =========================================================================
        // UI HELPERS
        // =========================================================================

        private void UpdateWatchStageDimensions()
        {
            if (_isFullscreen || _watchPageScroll.Visibility != Visibility.Visible) return;

            double viewportW = Math.Max(640, _watchPageScroll.ActualWidth);
            double viewportH = Math.Max(420, _watchPageScroll.ActualHeight);

            if (_isTheaterMode)
            {
                // Fórmula oficial YouTube Theater Mode (#full-bleed-container):
                // height: 56.25vw (16:9), max-height: calc(100vh - 169px) -> viewportH já desconta 56px do topo -> viewportH - 113px
                double idealH = Math.Round(viewportW * 9.0 / 16.0);
                double maxTheaterH = Math.Max(420, viewportH - 113);
                _playerStageFrame.Height = Math.Min(idealH, maxTheaterH);
            }
            else
            {
                // Layout oficial 2 colunas YouTube: MaxWidth 1754px, margens 24px+24px, gap 24px, sidebar direita 402px
                double contentW = Math.Min(1754, Math.Max(640, viewportW - 48));
                double primaryW = Math.Max(460, contentW - 402 - 24);
                double idealH = Math.Round(primaryW * 9.0 / 16.0);
                double maxNormalH = Math.Max(340, viewportH - 150);
                _playerStageFrame.Height = Math.Min(idealH, maxNormalH);
            }

            if (_relatedGrid != null)
            {
                double embH = _relatedGrid.ComputeEmbeddedGridHeight(402);
                _relatedGrid.Height = Math.Max(320, embH);
            }
        }

        private void HandleCardActivated(TrackItem item)
        {
            if (item == null) return;
            if (item.IsChannel) OpenChannelFromItem(item);
            else if (item.IsPlaylistCard) OpenPlaylistItemsAsync(item);
            else PlayVideo(item);
        }

        private void PlayNextVideo()
        {
            var next = _controller.GetNextVideoCandidate(true);
            if (next != null) PlayVideo(next);
            else _wpfOverlay.ShowToast("Sem próximo vídeo na fila.");
        }

        private TrackItem? GetFallbackNextVideo()
        {
            string curId = _controller.CurrentVideo?.Id ?? "";
            if (_openedPlaylistVideos.Count > 0)
            {
                int idx = _openedPlaylistVideos.FindIndex(x => x.Id == curId);
                if (idx >= 0 && idx + 1 < _openedPlaylistVideos.Count)
                    return _openedPlaylistVideos[idx + 1];
            }
            return _relatedVideos.Find(x => !x.IsChannel && !x.IsPlaylistCard && x.Id != curId);
        }

        private void OpenCurrentVideoChannel()
        {
            if (_controller.CurrentVideo != null)
                OpenChannelFromItem(_controller.CurrentVideo);
        }

        private void ToggleSaveVideo(TrackItem vid)
        {
            if (vid == null || vid.IsChannel || vid.IsPlaylistCard) return;
            bool saved = _config.ToggleSavedVideo(vid);
            UpdateSavedVideosCount();
            _catalogGrid.RefreshVisuals();
            _relatedGrid.RefreshVisuals();
            StatusNotification?.Invoke((saved ? "📌 Vídeo salvo: " : "Removido dos salvos: ") + vid.Title);
        }

        private void UpdateSavedVideosCount()
        {
            if (_txtNavYouLabel != null)
            {
                _txtNavYouLabel.Text = _config.SavedVideos.Count > 0
                    ? string.Format("Você ({0})", _config.SavedVideos.Count)
                    : "Você";
            }
        }

        private void ApplySearchChip(string chip)
        {
            _activeSearchChip = chip;
            UpdateFilterChipsVisuals();
            _catalogGrid.SetItems(FilterSearchItems(_searchVideos), true);
        }

        private List<TrackItem> FilterSearchItems(List<TrackItem> raw)
        {
            var list = FilterItemsForDisplay(raw);
            if (_activeSearchChip == "videos") return list.FindAll(x => !x.IsChannel && !x.IsPlaylistCard);
            if (_activeSearchChip == "channels") return list.FindAll(x => x.IsChannel);
            return list;
        }

        private List<TrackItem> FilterItemsForDisplay(List<TrackItem> raw)
        {
            if (raw == null) return new List<TrackItem>();
            if (!_config.HideShorts) return raw;
            return raw.FindAll(x => x != null && !x.IsShort);
        }

        private void ToggleHideShorts()
        {
            _config.HideShorts = !_config.HideShorts;
            _config.Save();
            UpdateFilterChipsVisuals();
            if (_activeNavSection == "search") ApplySearchChip(_activeSearchChip);
        }

        private void ToggleSponsorBlock()
        {
            _config.AutoSkipSponsors = !_config.AutoSkipSponsors;
            _config.Save();
            UpdateFilterChipsVisuals();
        }

        private void UpdateFilterChipsVisuals()
        {
            StyleChip(_chipAll, _activeSearchChip == "all");
            StyleChip(_chipVideos, _activeSearchChip == "videos");
            StyleChip(_chipChannels, _activeSearchChip == "channels");
            _chipHideShorts.Content = _config.HideShorts ? "🚫 Sem Shorts: ON" : "▶ Shorts: ON";
            StyleChip(_chipHideShorts, _config.HideShorts);
            _chipSponsorBlock.Content = _config.AutoSkipSponsors ? "🛡️ SponsorBlock: ON" : "🛡️ SponsorBlock: OFF";
            StyleChip(_chipSponsorBlock, _config.AutoSkipSponsors);
        }

        private static void StyleChip(Button btn, bool active)
        {
            btn.Background = new SolidColorBrush(active ? Color.FromRgb(241, 241, 241) : Color.FromArgb(30, 255, 255, 255));
            btn.Foreground = new SolidColorBrush(active ? Color.FromRgb(15, 15, 15) : Color.FromRgb(241, 241, 241));
        }

        private void UpdateSidebarSelection()
        {
            bool watchActive = IsWatchPageVisible;
            StyleNavButton(_navHome, !watchActive && _activeNavSection == "home");
            StyleNavButton(_miniNavHome, !watchActive && _activeNavSection == "home");
            StyleNavButton(_navSubscriptions, !watchActive && _activeNavSection == "subs");
            StyleNavButton(_miniNavSubs, !watchActive && _activeNavSection == "subs");
            StyleNavButton(_navHistory, !watchActive && _activeNavSection == "history");
            StyleNavButton(_miniNavHistory, !watchActive && _activeNavSection == "history");
            StyleNavButton(_navPlaylists, !watchActive && _activeNavSection == "playlists");
            StyleNavButton(_miniNavPlaylists, !watchActive && _activeNavSection == "playlists");
            StyleNavButton(_navYou, !watchActive && _activeNavSection == "you");
            StyleNavButton(_miniNavYou, !watchActive && _activeNavSection == "you");

            bool hasActiveVid = _controller.CurrentVideo != null;
            _navBackToPlayer.Visibility = hasActiveVid ? Visibility.Visible : Visibility.Collapsed;
            _miniNavWatch.Visibility = hasActiveVid ? Visibility.Visible : Visibility.Collapsed;
            StyleNavButton(_navBackToPlayer, watchActive);
            StyleNavButton(_miniNavWatch, watchActive);
        }

        private static void StyleNavButton(Button btn, bool active)
        {
            btn.Background = active ? new SolidColorBrush(Color.FromRgb(34, 34, 38)) : Brushes.Transparent;
            btn.Foreground = active ? new SolidColorBrush(Color.FromRgb(241, 241, 241)) : new SolidColorBrush(Color.FromRgb(190, 190, 195));
            btn.FontWeight = active ? FontWeights.Bold : FontWeights.Normal;
        }

        private void UpdateChannelTabsVisual(string activeTab)
        {
            StyleTab(_btnChannelTabHome, activeTab == "home");
            StyleTab(_btnChannelTabVideos, activeTab == "videos");
            StyleTab(_btnChannelTabPlaylists, activeTab == "playlists");
        }

        private static void StyleTab(Button btn, bool active)
        {
            btn.Foreground = new SolidColorBrush(active ? Color.FromRgb(241, 241, 241) : Color.FromRgb(170, 170, 170));
            btn.BorderBrush = active ? new SolidColorBrush(Color.FromRgb(241, 241, 241)) : Brushes.Transparent;
        }

        private void UpdateChannelSubInfoLine()
        {
            int q = _controller.ActualQualityHeight;
            string qLabel = q >= 2160 ? "4K" : string.Format("{0}p", q);
            _lblVideoSubInfo.Text = !string.IsNullOrEmpty(_currentSubscriberCountText)
                ? string.Format("{0}  •  {1}", _currentSubscriberCountText, qLabel)
                : string.Format("{0}  •  {1}", qLabel, _controller.ActiveCodecLabel);
        }

        private static string FormatViewsAndDate(TrackItem video)
        {
            if (!string.IsNullOrWhiteSpace(video.FormattedUploadDate))
                return string.Format("{0}  •  {1}", video.FormattedViews, video.FormattedUploadDate);
            return video.FormattedViews ?? "";
        }

        private void LoadWatchChannelAvatarAsync(TrackItem video)
        {
            string url = video.EffectiveChannelAvatarUrl;
            if (!string.IsNullOrEmpty(url))
            {
                LoadEllipseAvatarAsync(_watchChannelAvatar, url, 96);
                return;
            }

            _watchChannelAvatar.Fill = new SolidColorBrush(Color.FromRgb(42, 42, 46));
            if (!string.IsNullOrEmpty(video.ChannelId) || !string.IsNullOrEmpty(video.Artist))
            {
                string vidId = video.Id;
                _ = Task.Run(async () =>
                {
                    string resolved = await YouTubeService.ResolveChannelAvatarUrlStaticAsync(video.ChannelId, video.Artist).ConfigureAwait(false);
                    if (!string.IsNullOrEmpty(resolved))
                    {
                        video.ChannelAvatarUrl = resolved;
                        await Dispatcher.InvokeAsync(() =>
                        {
                            if (_controller.CurrentVideo != null && _controller.CurrentVideo.Id == vidId)
                                LoadEllipseAvatarAsync(_watchChannelAvatar, resolved, 96);
                        });
                    }
                });
            }
        }

        private async void LoadEllipseAvatarAsync(Ellipse target, string? url, int decodePx)
        {
            if (string.IsNullOrWhiteSpace(url))
            {
                target.Fill = new SolidColorBrush(Color.FromRgb(42, 42, 46));
                return;
            }
            try
            {
                var bmp = await AsyncImageLoader.Loaders.MemoryCachedWebImageLoader.LoadImageAsync(url, decodePx);
                if (bmp != null)
                {
                    target.Fill = new ImageBrush(bmp) { Stretch = Stretch.UniformToFill };
                }
            }
            catch { }
        }

        private void PromptOpenVideoUrl()
        {
            var dlg = new Window
            {
                Title = "Abrir Link do YouTube",
                Width = 460,
                Height = 160,
                ResizeMode = ResizeMode.NoResize,
                WindowStartupLocation = WindowStartupLocation.CenterOwner,
                Owner = Window.GetWindow(this),
                Background = new SolidColorBrush(Color.FromRgb(18, 18, 20))
            };
            dlg.SourceInitialized += (s, e) => WpfMainWindow.ApplyDarkTitleBar(dlg);
            var stack = new StackPanel { Margin = new Thickness(18) };
            stack.Children.Add(new TextBlock { Text = "Cole o link do vídeo ou playlist do YouTube:", Foreground = new SolidColorBrush(Color.FromRgb(241, 241, 241)), Margin = new Thickness(0, 0, 0, 8) });
            var tb = new TextBox
            {
                Background = new SolidColorBrush(Color.FromRgb(30, 30, 34)),
                Foreground = Brushes.White,
                CaretBrush = Brushes.White,
                BorderBrush = new SolidColorBrush(Color.FromRgb(56, 56, 62)),
                Padding = new Thickness(8, 6, 8, 6),
                FontSize = 13
            };
            stack.Children.Add(tb);
            var btn = new Button
            {
                Content = "Abrir",
                HorizontalAlignment = HorizontalAlignment.Right,
                Margin = new Thickness(0, 12, 0, 0),
                Padding = new Thickness(18, 6, 18, 6),
                Background = new SolidColorBrush(Color.FromRgb(255, 0, 51)),
                Foreground = Brushes.White,
                BorderThickness = new Thickness(0)
            };
            btn.Click += (s, e) => { dlg.DialogResult = true; dlg.Close(); };
            stack.Children.Add(btn);
            dlg.Content = stack;
            tb.Focus();

            if (dlg.ShowDialog() == true && !string.IsNullOrWhiteSpace(tb.Text))
            {
                PlayVideoByUrl(tb.Text.Trim());
            }
        }

        public void Dispose()
        {
            try { _watchNextCts?.Cancel(); } catch { }
            try { _metadataFetchCts?.Cancel(); } catch { }
            try { _controller.Dispose(); } catch { }
            try { _wpfOverlay.Dispose(); } catch { }
            try { _flyleafHost.Dispose(); } catch { }
        }
    }
}
