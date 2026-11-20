using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Markup;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Shapes;
using System.Windows.Threading;

namespace LiteTube
{
    /// <summary>
    /// Sobreposição transparente em WPF ancorada ao container de vídeo do FlyleafLib (WinForms.FlyleafHost).
    /// Implementa a skin oficial do YouTube Web:
    /// - Scrubber expansível (3px -> 5px no hover) em #FF0000, buffer, segmentos coloridos do SponsorBlock e tooltip de tempo.
    /// - Prevenção de loops de seek via _isUserDraggingScrubber (emissão de Seek apenas em PreviewMouseUp).
    /// - Auto-hide de 2,5s com DoubleAnimation de opacidade e ocultação do cursor.
    /// - Ícones vetoriais Path: Play/Pause, Próximo, Volume expansível, Engrenagem, Modo Teatro e Tela Cheia.
    /// </summary>
    public class WpfVideoPlayerOverlay : IDisposable
    {
        [StructLayout(LayoutKind.Sequential)]
        private struct POINT
        {
            public int X;
            public int Y;
        }

        private const int GWL_EXSTYLE = -20;

        [DllImport("user32.dll")]
        private static extern bool GetCursorPos(out POINT lpPoint);

        private FrameworkElement _rootContent;

        // UI Elements from WpfVideoPlayerOverlay.xaml
        private Grid _rootOverlayGrid;
        private Grid _controlsContainerGrid;
        private Border _toastNotificationBanner;
        private TextBlock _toastNotificationText;
        private Border _centerOsdBadge;
        private TextBlock _centerOsdText;
        private Border _sponsorSkipBanner;
        private TextBlock _sponsorSkipBannerText;
        private Border _gearPopupPanel;

        // Autoplay Countdown Overlay (8s) elements
        private Grid _autoplayOverlay;
        private TextBlock _autoplayCountdownText;
        private System.Windows.Controls.Image _autoplayNextThumbnail;
        private TextBlock _autoplayNextTitle;
        private TextBlock _autoplayNextAuthor;
        private System.Windows.Controls.ProgressBar _autoplayProgressBar;
        private Button _btnAutoplayCancel;
        private Button _btnAutoplayPlayNow;

        // Scrubber elements
        private Grid _scrubberHitArea;
        private Border _scrubberBgTrack;
        private Border _scrubberBufferBar;
        private Border _scrubberHoverBar;
        private Canvas _sponsorSegmentsCanvas;
        private Border _scrubberPlayedBar;
        private Ellipse _scrubberThumbHead;
        private Border _scrubberTooltipBorder;
        private Border _scrubberPreviewImageBorder;
        private System.Windows.Controls.Image _scrubberPreviewImage;
        private TextBlock _scrubberTooltipText;
        private StoryboardData _activeStoryboard;
        private double _lastHoveredMouseX = 0.0;

        // Transport & Volume elements
        private Border _btnPlayPause;
        private System.Windows.Shapes.Path _iconPlay;
        private System.Windows.Shapes.Path _iconPause;
        private Border _btnNextVideo;
        private StackPanel _volumeHoverGroup;
        private Border _btnMuteSpeaker;
        private System.Windows.Shapes.Path _iconSpeakerActive;
        private System.Windows.Shapes.Path _iconSpeakerMuted;
        private Border _volumeSliderExpandContainer;
        private Grid _volumeSliderHitGrid;
        private Border _volumeSliderFillBar;
        private Ellipse _volumeSliderThumb;
        private TextBlock _txtTimeDisplay;

        // Right controls & Gear popup buttons
        private Border _badgeQualityPill;
        private TextBlock _txtQualityBadge;
        private Border _btnSpeedPill;
        private TextBlock _txtSpeedBadge;
        private Border _btnSettingsGear;
        private Border _btnFullscreen;
        private Canvas _chaptersCanvas;
        private TextBlock _scrubberChapterText;

        private UniformGrid _gearQualityGrid;
        private readonly Dictionary<int, Button> _gearQualityButtons = new Dictionary<int, Button>();
        private List<int> _availableQualities = new List<int> { 1080, 720, 480, 360 };
        private List<VideoChapter> _chapters = new List<VideoChapter>();
        private Button _btnGearSpd050;
        private Button _btnGearSpd075;
        private Button _btnGearSpd100;
        private Button _btnGearSpd125;
        private Button _btnGearSpd150;
        private Button _btnGearSpd200;
        private Button _btnGearAudioDialog;

        // State
        private bool _isPlaying = false;
        private bool _isLiveStream = false;
        private bool _isMuted = false;
        private double _volume0To1 = 0.85;
        private double _currentSeconds = 0.0;
        private double _bufferedSeconds = 0.0;
        private double _totalSeconds = 0.0;
        private int _activeQualityHeight = 720;
        private double _activePlaybackSpeed = 1.0;
        private string _activeCodecShort = "";

        private bool _isHoveringScrubber = false;
        private bool _isDraggingScrubber = false;
        private bool _isUserDraggingScrubber
        {
            get { return _isDraggingScrubber; }
            set { _isDraggingScrubber = value; }
        }
        private bool _isDraggingVolume = false;
        private double _dragScrubTargetSeconds = 0.0;
        private bool _controlsHiddenByInactivity = false;
        private bool _controlsTargetVisible = true;
        private bool _isOverlayAttachedAndActive = false;
        private bool _isDisposed = false;

        private string _lastDisplayedTimeText = "";
        private double _lastChaptersTrackW = -1;
        private double _lastChaptersTotalSec = -1;

        private List<SponsorSegment> _sponsorSegments = new List<SponsorSegment>();
        private readonly DispatcherTimer _inactivityTimer;
        private readonly DispatcherTimer _sponsorBannerTimer;
        private readonly DispatcherTimer _toastTimer;
        private readonly DispatcherTimer _osdHoldTimer;
        private readonly DispatcherTimer _autoplayTimer;
        private int _osdGeneration = 0;
        private int _autoplayTicksRemaining = 80;
        private POINT _lastGlobalCursorPos;
        private DateTime _lastMouseMoveUtc = DateTime.UtcNow;

        // Events to bridge with VideoEnvironmentControl & FlyleafVideoControl
        public event Action PlayPauseRequested;
        public event Action NextVideoRequested;
        public event Action AutoplayCancelled;
        public event Action<double> OnUserSeekCommitted;
        public event Action<double> SeekRequested;
        public event Action MuteToggleRequested;
        public event Action<double> VolumeChanged;
        public event Action<int> QualityChanged;
        public event Action<double> SpeedChanged;
        public event Action FullscreenToggleRequested;
        public event Action AudioDialogRequested;
        public event Action<Key> ShortcutKeyPressed;
        public event Action<int> UnhandledMouseWheel;

        public bool IsUserDraggingScrubber { get { return _isDraggingScrubber; } }
        public bool IsFullscreenMode { get; set; }
        public bool IsOverlayVisible { get { return _isOverlayAttachedAndActive && _rootContent != null && _rootContent.Visibility == Visibility.Visible; } }
        public FrameworkElement View { get { return _rootContent; } }

        public WpfVideoPlayerOverlay()
        {
            GetCursorPos(out _lastGlobalCursorPos);

            InitializeOverlayContent();
            WireOverlayEvents();

            _inactivityTimer = new DispatcherTimer
            {
                Interval = TimeSpan.FromMilliseconds(2500)
            };
            _inactivityTimer.Tick += OnInactivityTimerTick;

            _sponsorBannerTimer = new DispatcherTimer
            {
                Interval = TimeSpan.FromSeconds(3.0)
            };
            _sponsorBannerTimer.Tick += (s, e) =>
            {
                _sponsorBannerTimer.Stop();
                if (_sponsorSkipBanner != null)
                {
                    _sponsorSkipBanner.Visibility = Visibility.Collapsed;
                }
            };

            _toastTimer = new DispatcherTimer
            {
                Interval = TimeSpan.FromMilliseconds(2500)
            };
            _toastTimer.Tick += (s, e) =>
            {
                _toastTimer.Stop();
                if (_toastNotificationBanner != null)
                {
                    _toastNotificationBanner.Visibility = Visibility.Collapsed;
                }
            };

            _osdHoldTimer = new DispatcherTimer
            {
                Interval = TimeSpan.FromMilliseconds(350)
            };
            _osdHoldTimer.Tick += OnOsdHoldTimerTick;

            _autoplayTimer = new DispatcherTimer
            {
                Interval = TimeSpan.FromMilliseconds(100)
            };
            _autoplayTimer.Tick += OnAutoplayTimerTick;
        }

        private void InitializeOverlayContent()
        {
            string baseDir = AppDomain.CurrentDomain.BaseDirectory;
            string p1 = System.IO.Path.Combine(baseDir, "src", "WpfVideoPlayerOverlay.xaml");
            string p2 = System.IO.Path.Combine(baseDir, "WpfVideoPlayerOverlay.xaml");
            string xamlPath = File.Exists(p1) ? p1 : (File.Exists(p2) ? p2 : null);

            if (xamlPath != null)
            {
                using (var fs = new FileStream(xamlPath, FileMode.Open, FileAccess.Read, FileShare.Read))
                {
                    _rootContent = (FrameworkElement)XamlReader.Load(fs);
                }
            }
            else
            {
                _rootContent = new Grid { Background = new SolidColorBrush(Color.FromArgb(1, 0, 0, 0)) };
            }

            _rootOverlayGrid = FindElement<Grid>("RootOverlayGrid");
            _controlsContainerGrid = FindElement<Grid>("ControlsContainerGrid");
            _toastNotificationBanner = FindElement<Border>("ToastNotificationBanner");
            _toastNotificationText = FindElement<TextBlock>("ToastNotificationText");
            _centerOsdBadge = FindElement<Border>("CenterOsdBadge");
            _centerOsdText = FindElement<TextBlock>("CenterOsdText");
            _sponsorSkipBanner = FindElement<Border>("SponsorSkipBanner");
            _sponsorSkipBannerText = FindElement<TextBlock>("SponsorSkipBannerText");
            _gearPopupPanel = FindElement<Border>("GearPopupPanel");

            _autoplayOverlay = FindElement<Grid>("AutoplayOverlay");
            _autoplayCountdownText = FindElement<TextBlock>("AutoplayCountdownText");
            _autoplayNextThumbnail = FindElement<System.Windows.Controls.Image>("AutoplayNextThumbnail");
            _autoplayNextTitle = FindElement<TextBlock>("AutoplayNextTitle");
            _autoplayNextAuthor = FindElement<TextBlock>("AutoplayNextAuthor");
            _autoplayProgressBar = FindElement<System.Windows.Controls.ProgressBar>("AutoplayProgressBar");
            _btnAutoplayCancel = FindElement<Button>("BtnAutoplayCancel");
            _btnAutoplayPlayNow = FindElement<Button>("BtnAutoplayPlayNow");

            _scrubberHitArea = FindElement<Grid>("ScrubberHitArea");
            _scrubberBgTrack = FindElement<Border>("ScrubberBgTrack");
            _scrubberBufferBar = FindElement<Border>("ScrubberBufferBar");
            _scrubberHoverBar = FindElement<Border>("ScrubberHoverBar");
            _sponsorSegmentsCanvas = FindElement<Canvas>("SponsorSegmentsCanvas");
            _chaptersCanvas = FindElement<Canvas>("ChaptersCanvas");
            _scrubberPlayedBar = FindElement<Border>("ScrubberPlayedBar");
            _scrubberThumbHead = FindElement<Ellipse>("ScrubberThumbHead");
            _scrubberTooltipBorder = FindElement<Border>("ScrubberTooltipBorder");
            _scrubberPreviewImageBorder = FindElement<Border>("ScrubberPreviewImageBorder");
            _scrubberPreviewImage = FindElement<System.Windows.Controls.Image>("ScrubberPreviewImage");
            _scrubberTooltipText = FindElement<TextBlock>("ScrubberTooltipText");
            _scrubberChapterText = FindElement<TextBlock>("ScrubberChapterText");

            _btnPlayPause = FindElement<Border>("BtnPlayPause");
            _iconPlay = FindElement<System.Windows.Shapes.Path>("IconPlay");
            _iconPause = FindElement<System.Windows.Shapes.Path>("IconPause");
            _btnNextVideo = FindElement<Border>("BtnNextVideo");
            _volumeHoverGroup = FindElement<StackPanel>("VolumeHoverGroup");
            _btnMuteSpeaker = FindElement<Border>("BtnMuteSpeaker");
            _iconSpeakerActive = FindElement<System.Windows.Shapes.Path>("IconSpeakerActive");
            _iconSpeakerMuted = FindElement<System.Windows.Shapes.Path>("IconSpeakerMuted");
            _volumeSliderExpandContainer = FindElement<Border>("VolumeSliderExpandContainer");
            _volumeSliderHitGrid = FindElement<Grid>("VolumeSliderHitGrid");
            _volumeSliderFillBar = FindElement<Border>("VolumeSliderFillBar");
            _volumeSliderThumb = FindElement<Ellipse>("VolumeSliderThumb");
            _txtTimeDisplay = FindElement<TextBlock>("TxtTimeDisplay");

            _badgeQualityPill = FindElement<Border>("BadgeQualityPill");
            _txtQualityBadge = FindElement<TextBlock>("TxtQualityBadge");
            _btnSpeedPill = FindElement<Border>("BtnSpeedPill");
            _txtSpeedBadge = FindElement<TextBlock>("TxtSpeedBadge");
            _btnSettingsGear = FindElement<Border>("BtnSettingsGear");
            _btnFullscreen = FindElement<Border>("BtnFullscreen");

            _gearQualityGrid = FindElement<UniformGrid>("GearQualityGrid");
            _btnGearSpd050 = FindElement<Button>("BtnGearSpd050");
            _btnGearSpd075 = FindElement<Button>("BtnGearSpd075");
            _btnGearSpd100 = FindElement<Button>("BtnGearSpd100");
            _btnGearSpd125 = FindElement<Button>("BtnGearSpd125");
            _btnGearSpd150 = FindElement<Button>("BtnGearSpd150");
            _btnGearSpd200 = FindElement<Button>("BtnGearSpd200");
            _btnGearAudioDialog = FindElement<Button>("BtnGearAudioDialog");
            RebuildQualityButtons(_availableQualities);
        }

        private T FindElement<T>(string name) where T : class
        {
            if (_rootContent == null) return null;
            return _rootContent.FindName(name) as T;
        }

        private void WireButton(Border btn, Action action)
        {
            if (btn == null) return;
            btn.PreviewMouseLeftButtonDown += (s, e) =>
            {
                RegisterUserActivity();
                if (action != null) action();
                e.Handled = true;
            };
            btn.MouseLeftButtonUp += (s, e) =>
            {
                e.Handled = true;
            };
        }

        private void WireOverlayEvents()
        {
            if (_rootContent != null)
            {
                _rootContent.PreviewMouseMove += (s, e) => OnPassiveMouseMove();
                _rootContent.PreviewKeyDown += OnOverlayPreviewKeyDown;
                _rootContent.PreviewMouseWheel += OnOverlayMouseWheel;
                _rootContent.SizeChanged += (s, e) => RefreshTimelineVisuals();
            }

            if (_rootOverlayGrid != null)
            {
                _rootOverlayGrid.MouseLeave += (s, e) => RestoreCursor();
                _rootOverlayGrid.MouseLeftButtonDown += (s, e) =>
                {
                    RegisterUserActivity();
                    if (e.OriginalSource == _rootOverlayGrid)
                    {
                        if (_gearPopupPanel != null && _gearPopupPanel.Visibility == Visibility.Visible)
                        {
                            _gearPopupPanel.IsHitTestVisible = false;
                            _gearPopupPanel.Visibility = Visibility.Collapsed;
                            e.Handled = true;
                            return;
                        }

                        if (e.ClickCount == 2)
                        {
                            if (FullscreenToggleRequested != null) FullscreenToggleRequested();
                        }
                        else if (e.ClickCount == 1)
                        {
                            if (PlayPauseRequested != null) PlayPauseRequested();
                        }
                        e.Handled = true;
                    }
                };
            }

            // Scrubber Bar Hover & Drag (Seek emitido estritamente em PreviewMouseUp)
            if (_scrubberHitArea != null)
            {
                _scrubberHitArea.MouseEnter += (s, e) =>
                {
                    _isHoveringScrubber = true;
                    AnimateScrubberHeight(true);
                };

                _scrubberHitArea.MouseLeave += (s, e) =>
                {
                    _isHoveringScrubber = false;
                    if (!_isDraggingScrubber)
                    {
                        AnimateScrubberHeight(false);
                        if (_scrubberTooltipBorder != null) _scrubberTooltipBorder.Visibility = Visibility.Collapsed;
                        if (_scrubberHoverBar != null) _scrubberHoverBar.Visibility = Visibility.Collapsed;
                    }
                };

                _scrubberHitArea.PreviewMouseLeftButtonDown += Scrubber_PreviewMouseDown;

                _scrubberHitArea.PreviewMouseMove += (s, e) =>
                {
                    if (_isDraggingScrubber || UpdateGlobalCursorIfMoved(1))
                    {
                        RegisterUserActivity();
                        UpdateScrubberHoverAndDrag(e.GetPosition(_scrubberHitArea).X);
                    }
                };

                _scrubberHitArea.PreviewMouseUp += Scrubber_PreviewMouseUp;
            }

            // Transport Buttons
            WireButton(_btnPlayPause, () => { if (PlayPauseRequested != null) PlayPauseRequested(); });

            // Autoplay Countdown Buttons
            if (_btnAutoplayCancel != null)
            {
                _btnAutoplayCancel.Click += BtnAutoplayCancel_Click;
            }
            if (_btnAutoplayPlayNow != null)
            {
                _btnAutoplayPlayNow.Click += BtnAutoplayPlayNow_Click;
            }
            WireButton(_btnNextVideo, () => { if (NextVideoRequested != null) NextVideoRequested(); });

            // Expandable Volume Slider on Speaker Hover
            if (_volumeHoverGroup != null)
            {
                _volumeHoverGroup.MouseEnter += (s, e) => ExpandVolumeSlider(true);
                _volumeHoverGroup.MouseLeave += (s, e) =>
                {
                    if (!_isDraggingVolume) ExpandVolumeSlider(false);
                };
            }

            WireButton(_btnMuteSpeaker, () => { if (MuteToggleRequested != null) MuteToggleRequested(); });

            if (_volumeSliderHitGrid != null)
            {
                _volumeSliderHitGrid.PreviewMouseLeftButtonDown += (s, e) =>
                {
                    RegisterUserActivity();
                    _isDraggingVolume = true;
                    _volumeSliderHitGrid.CaptureMouse();
                    SetVolumeFromSliderMouseX(e.GetPosition(_volumeSliderHitGrid).X);
                    e.Handled = true;
                };

                _volumeSliderHitGrid.PreviewMouseMove += (s, e) =>
                {
                    if (_isDraggingVolume)
                    {
                        RegisterUserActivity();
                        SetVolumeFromSliderMouseX(e.GetPosition(_volumeSliderHitGrid).X);
                        e.Handled = true;
                    }
                };

                _volumeSliderHitGrid.PreviewMouseUp += (s, e) =>
                {
                    if (_isDraggingVolume)
                    {
                        _isDraggingVolume = false;
                        _volumeSliderHitGrid.ReleaseMouseCapture();
                        if (_volumeHoverGroup != null && !_volumeHoverGroup.IsMouseOver)
                        {
                            ExpandVolumeSlider(false);
                        }
                        e.Handled = true;
                    }
                };
            }

            // Settings Gear, Quality Pill, Speed Pill & Fullscreen
            WireButton(_btnSettingsGear, () => ToggleGearPopup());
            WireButton(_badgeQualityPill, () => ToggleGearPopup());
            WireButton(_btnSpeedPill, () => ToggleGearPopup());
            WireButton(_btnFullscreen, () => { if (FullscreenToggleRequested != null) FullscreenToggleRequested(); });

            // Gear Menu Actions
            if (_btnGearSpd050 != null) _btnGearSpd050.Click += (s, e) => SelectSpeedFromGear(0.50);
            if (_btnGearSpd075 != null) _btnGearSpd075.Click += (s, e) => SelectSpeedFromGear(0.75);
            if (_btnGearSpd100 != null) _btnGearSpd100.Click += (s, e) => SelectSpeedFromGear(1.00);
            if (_btnGearSpd125 != null) _btnGearSpd125.Click += (s, e) => SelectSpeedFromGear(1.25);
            if (_btnGearSpd150 != null) _btnGearSpd150.Click += (s, e) => SelectSpeedFromGear(1.50);
            if (_btnGearSpd200 != null) _btnGearSpd200.Click += (s, e) => SelectSpeedFromGear(2.00);

            if (_btnGearAudioDialog != null)
            {
                _btnGearAudioDialog.Click += (s, e) =>
                {
                    if (_gearPopupPanel != null) _gearPopupPanel.Visibility = Visibility.Collapsed;
                    if (AudioDialogRequested != null) AudioDialogRequested();
                };
            }
        }

        public void SetOverlayActive(bool active)
        {
            _isOverlayAttachedAndActive = active;
            if (active)
            {
                _controlsHiddenByInactivity = false;
                _controlsTargetVisible = true;
                if (_rootContent != null)
                {
                    _rootContent.Visibility = Visibility.Visible;
                }
                if (_rootOverlayGrid != null)
                {
                    _rootOverlayGrid.Visibility = Visibility.Visible;
                    _rootOverlayGrid.Cursor = Cursors.Arrow;
                }
                if (_controlsContainerGrid != null)
                {
                    _controlsContainerGrid.BeginAnimation(UIElement.OpacityProperty, null);
                    _controlsContainerGrid.Visibility = Visibility.Visible;
                    _controlsContainerGrid.Opacity = 1.0;
                    _controlsContainerGrid.IsHitTestVisible = true;
                }
                _inactivityTimer.Start();
                RegisterUserActivity();
                ForceImmediateGpuRender();
            }
            else
            {
                _inactivityTimer.Stop();
                _sponsorBannerTimer.Stop();
                _osdHoldTimer.Stop();
                RestoreCursor();
                if (_gearPopupPanel != null) _gearPopupPanel.Visibility = Visibility.Collapsed;
                if (_sponsorSkipBanner != null) _sponsorSkipBanner.Visibility = Visibility.Collapsed;
                if (_centerOsdBadge != null)
                {
                    _centerOsdBadge.BeginAnimation(UIElement.OpacityProperty, null);
                    _centerOsdBadge.Opacity = 0.0;
                    _centerOsdBadge.Visibility = Visibility.Collapsed;
                }
                if (_scrubberTooltipBorder != null) _scrubberTooltipBorder.Visibility = Visibility.Collapsed;
                if (_scrubberHoverBar != null) _scrubberHoverBar.Visibility = Visibility.Collapsed;
                if (_controlsContainerGrid != null)
                {
                    _controlsContainerGrid.BeginAnimation(UIElement.OpacityProperty, null);
                    _controlsContainerGrid.Opacity = 0.0;
                    _controlsContainerGrid.Visibility = Visibility.Collapsed;
                }
                if (_rootOverlayGrid != null)
                {
                    _rootOverlayGrid.Cursor = Cursors.Arrow;
                    _rootOverlayGrid.Visibility = Visibility.Collapsed;
                }
                if (_rootContent != null)
                {
                    _rootContent.Visibility = Visibility.Collapsed;
                }
            }
        }

        public void ForceImmediateGpuRender()
        {
            if (_isDisposed) return;
            try
            {
                RunOnWpfDispatcher(() =>
                {
                    try
                    {
                        RefreshTimelineVisuals();
                        if (_rootContent != null)
                        {
                            _rootContent.InvalidateVisual();
                            _rootContent.UpdateLayout();
                        }
                        if (_controlsContainerGrid != null)
                        {
                            _controlsContainerGrid.InvalidateVisual();
                        }
                    }
                    catch { }
                });
            }
            catch { }
        }

        // ====================================================================
        // AUTO-HIDE INACTIVITY (2.5 SECONDS + GPU OPACITY ANIMATION)
        // ====================================================================

        private void RestoreCursor()
        {
            if (Mouse.OverrideCursor != null)
            {
                Mouse.OverrideCursor = null;
            }
            if (_rootOverlayGrid != null && _rootOverlayGrid.Cursor != Cursors.Arrow)
            {
                _rootOverlayGrid.Cursor = Cursors.Arrow;
            }
        }

        private bool IsCursorInsidePlayer()
        {
            return _rootOverlayGrid != null && _rootOverlayGrid.IsMouseOver;
        }

        private bool UpdateGlobalCursorIfMoved(int minDeltaPx = 2)
        {
            if (!GetCursorPos(out POINT cur))
                return true;

            int dx = Math.Abs(cur.X - _lastGlobalCursorPos.X);
            int dy = Math.Abs(cur.Y - _lastGlobalCursorPos.Y);
            if (dx < minDeltaPx && dy < minDeltaPx)
                return false;

            _lastGlobalCursorPos = cur;
            return true;
        }

        private void OnPassiveMouseMove()
        {
            if (UpdateGlobalCursorIfMoved(2))
            {
                RegisterUserActivity();
            }
        }

        public void RegisterUserActivity()
        {
            if (GetCursorPos(out POINT cur))
            {
                _lastGlobalCursorPos = cur;
            }
            _lastMouseMoveUtc = DateTime.UtcNow;
            RestoreCursor();
            if (!_controlsTargetVisible || _controlsHiddenByInactivity ||
                (_controlsContainerGrid != null && _controlsContainerGrid.Visibility != Visibility.Visible))
            {
                SetControlsVisibleAnimated(true);
            }

            _inactivityTimer.Stop();
            if (_isOverlayAttachedAndActive && _isPlaying && !_isUserDraggingScrubber && !_isDraggingVolume &&
                (_gearPopupPanel == null || _gearPopupPanel.Visibility != Visibility.Visible))
            {
                _inactivityTimer.Start();
            }
        }

        private void OnInactivityTimerTick(object sender, EventArgs e)
        {
            _inactivityTimer.Stop();

            if (!_isOverlayAttachedAndActive)
            {
                RestoreCursor();
                return;
            }

            if (_rootContent == null || _rootContent.Visibility != Visibility.Visible)
            {
                RestoreCursor();
                return;
            }

            if (!_isPlaying || _isUserDraggingScrubber || _isDraggingVolume ||
                (_gearPopupPanel != null && _gearPopupPanel.Visibility == Visibility.Visible) ||
                (_autoplayOverlay != null && _autoplayOverlay.Visibility == Visibility.Visible))
            {
                if (_controlsHiddenByInactivity || !_controlsTargetVisible)
                {
                    SetControlsVisibleAnimated(true);
                }
                RestoreCursor();
                if (_isPlaying)
                {
                    _inactivityTimer.Start();
                }
                return;
            }

            SetControlsVisibleAnimated(false);
        }

        private void SetControlsVisibleAnimated(bool visible)
        {
            if (_controlsTargetVisible == visible && _controlsHiddenByInactivity == !visible)
            {
                if (visible && _controlsContainerGrid != null && _controlsContainerGrid.Visibility == Visibility.Visible)
                    return;
                if (!visible && _controlsContainerGrid != null && _controlsContainerGrid.Visibility == Visibility.Collapsed)
                    return;
            }

            _controlsTargetVisible = visible;
            _controlsHiddenByInactivity = !visible;
            if (_controlsContainerGrid == null) return;

            bool allowHideCursor = !visible && _isPlaying &&
                                   (_gearPopupPanel == null || _gearPopupPanel.Visibility != Visibility.Visible) &&
                                   IsCursorInsidePlayer();

            if (visible)
            {
                RestoreCursor();
                _controlsContainerGrid.Visibility = Visibility.Visible;
                _controlsContainerGrid.IsHitTestVisible = true;
            }
            else
            {
                _isHoveringScrubber = false;
                if (_scrubberTooltipBorder != null) _scrubberTooltipBorder.Visibility = Visibility.Collapsed;
                if (_scrubberHoverBar != null) _scrubberHoverBar.Visibility = Visibility.Collapsed;
                if (!_isDraggingVolume) ExpandVolumeSlider(false);

                _controlsContainerGrid.IsHitTestVisible = false;
                if (allowHideCursor)
                {
                    Mouse.OverrideCursor = Cursors.None;
                    if (_rootOverlayGrid != null) _rootOverlayGrid.Cursor = Cursors.None;
                }
                else
                {
                    RestoreCursor();
                }
            }

            var anim = new DoubleAnimation
            {
                To = visible ? 1.0 : 0.0,
                Duration = TimeSpan.FromMilliseconds(120),
                EasingFunction = new QuadraticEase { EasingMode = EasingMode.EaseOut }
            };
            anim.Completed += (s, e) =>
            {
                if (!visible && _controlsHiddenByInactivity && !_controlsTargetVisible && _controlsContainerGrid != null)
                {
                    _controlsContainerGrid.Visibility = Visibility.Collapsed;
                }
            };
            _controlsContainerGrid.BeginAnimation(UIElement.OpacityProperty, anim);
        }

        // ====================================================================
        // SCRUBBER, SPONSORBLOCK TIMELINE SEGMENTS & TOOLTIP
        // ====================================================================

        private void Scrubber_PreviewMouseDown(object sender, MouseButtonEventArgs e)
        {
            RegisterUserActivity();
            _isDraggingScrubber = true;
            if (_scrubberHitArea != null)
            {
                _scrubberHitArea.CaptureMouse();
                UpdateScrubberHoverAndDrag(e.GetPosition(_scrubberHitArea).X);
            }
            e.Handled = true;
        }

        private void Scrubber_PreviewMouseUp(object sender, MouseButtonEventArgs e)
        {
            if (_isDraggingScrubber)
            {
                _isDraggingScrubber = false;
                if (_scrubberHitArea != null)
                {
                    _scrubberHitArea.ReleaseMouseCapture();
                    UpdateScrubberHoverAndDrag(e.GetPosition(_scrubberHitArea).X);
                }
                double targetSeconds = _dragScrubTargetSeconds;
                _currentSeconds = targetSeconds;
                if (!_isHoveringScrubber)
                {
                    AnimateScrubberHeight(false);
                    if (_scrubberTooltipBorder != null) _scrubberTooltipBorder.Visibility = Visibility.Collapsed;
                    if (_scrubberHoverBar != null) _scrubberHoverBar.Visibility = Visibility.Collapsed;
                }
                if (OnUserSeekCommitted != null) OnUserSeekCommitted(targetSeconds);
                else if (SeekRequested != null) SeekRequested(targetSeconds);
                e.Handled = true;
            }
        }

        private void AnimateScrubberHeight(bool expanded)
        {
            double targetH = expanded ? 5.0 : 3.0;
            CornerRadius cr = new CornerRadius(targetH / 2.0);

            if (_scrubberBgTrack != null) { _scrubberBgTrack.Height = targetH; _scrubberBgTrack.CornerRadius = cr; }
            if (_scrubberBufferBar != null) { _scrubberBufferBar.Height = targetH; _scrubberBufferBar.CornerRadius = cr; }
            if (_scrubberHoverBar != null) { _scrubberHoverBar.Height = targetH; _scrubberHoverBar.CornerRadius = cr; }
            if (_sponsorSegmentsCanvas != null) { _sponsorSegmentsCanvas.Height = targetH; }
            if (_scrubberPlayedBar != null) { _scrubberPlayedBar.Height = targetH; _scrubberPlayedBar.CornerRadius = cr; }

            if (_scrubberThumbHead != null)
            {
                _scrubberThumbHead.Visibility = expanded ? Visibility.Visible : Visibility.Collapsed;
            }

            RenderSponsorSegmentsOnCanvas();
            RenderChaptersOnCanvas();
        }

        private void UpdateScrubberHoverAndDrag(double mouseX)
        {
            if (_scrubberHitArea == null) return;
            _lastHoveredMouseX = mouseX;
            double trackW = Math.Max(1.0, _scrubberHitArea.ActualWidth);
            double clampedX = Math.Max(0.0, Math.Min(trackW, mouseX));
            double ratio = clampedX / trackW;

            if (_scrubberHoverBar != null)
            {
                _scrubberHoverBar.Visibility = Visibility.Visible;
                _scrubberHoverBar.Width = clampedX;
            }

            double targetSec = _totalSeconds > 0 ? ratio * _totalSeconds : 0.0;
            if (_isUserDraggingScrubber)
            {
                _dragScrubTargetSeconds = targetSec;
                RefreshTimelineVisuals();
            }

            if (_scrubberTooltipBorder != null && _scrubberTooltipText != null)
            {
                if (_isLiveStream)
                {
                    _scrubberTooltipText.Text = "🔴 AO VIVO";
                    if (_scrubberPreviewImageBorder != null)
                    {
                        _scrubberPreviewImageBorder.Visibility = Visibility.Collapsed;
                    }
                }
                else
                {
                    string timeLabel = YouTubeService.FormatSeconds(targetSec);
                    string segName = GetHoveredSponsorCategoryLabel(targetSec);
                    _scrubberTooltipText.Text = !string.IsNullOrEmpty(segName)
                        ? string.Format("{0} • {1}", timeLabel, segName)
                        : timeLabel;

                    if (_scrubberChapterText != null)
                    {
                        string chapterTitle = FindChapterTitleAt(targetSec);
                        if (!string.IsNullOrWhiteSpace(chapterTitle))
                        {
                            _scrubberChapterText.Text = chapterTitle;
                            _scrubberChapterText.Visibility = Visibility.Visible;
                        }
                        else
                        {
                            _scrubberChapterText.Visibility = Visibility.Collapsed;
                        }
                    }

                    if (_scrubberPreviewImageBorder != null && _scrubberPreviewImage != null)
                    {
                        var previewBmp = _activeStoryboard != null
                            ? _activeStoryboard.TryGetFrame(targetSec, _totalSeconds, OnStoryboardSheetReady)
                            : null;

                        if (previewBmp != null)
                        {
                            _scrubberPreviewImage.Source = previewBmp;
                            _scrubberPreviewImageBorder.Visibility = Visibility.Visible;
                        }
                        else
                        {
                            _scrubberPreviewImageBorder.Visibility = Visibility.Collapsed;
                        }
                    }
                }

                _scrubberTooltipBorder.Visibility = Visibility.Visible;
                _scrubberTooltipBorder.UpdateLayout();

                double tipW = _scrubberTooltipBorder.ActualWidth > 0 ? _scrubberTooltipBorder.ActualWidth : 48.0;
                double leftPos = 14.0 + clampedX - (tipW / 2.0);
                double maxLeft = Math.Max(14.0, (_controlsContainerGrid != null ? _controlsContainerGrid.ActualWidth : trackW) - tipW - 14.0);
                Canvas.SetLeft(_scrubberTooltipBorder, Math.Max(14.0, Math.Min(maxLeft, leftPos)));
            }
        }

        private void OnStoryboardSheetReady()
        {
            if (_isDisposed || _rootContent == null) return;
            try
            {
                _rootContent.Dispatcher.BeginInvoke(new Action(() =>
                {
                    if ((_isHoveringScrubber || _isDraggingScrubber) &&
                        _scrubberTooltipBorder != null &&
                        _scrubberTooltipBorder.Visibility == Visibility.Visible)
                    {
                        UpdateScrubberHoverAndDrag(_lastHoveredMouseX);
                    }
                }), DispatcherPriority.Render);
            }
            catch { }
        }

        public void SetVideoStoryboard(StoryboardData storyboard)
        {
            _activeStoryboard = storyboard;
            if (_rootContent != null)
            {
                _rootContent.Dispatcher.BeginInvoke(new Action(() =>
                {
                    if (storyboard == null)
                    {
                        if (_scrubberPreviewImage != null) _scrubberPreviewImage.Source = null;
                        if (_scrubberPreviewImageBorder != null) _scrubberPreviewImageBorder.Visibility = Visibility.Collapsed;
                    }
                    else if ((_isHoveringScrubber || _isDraggingScrubber) &&
                             _scrubberTooltipBorder != null &&
                             _scrubberTooltipBorder.Visibility == Visibility.Visible)
                    {
                        UpdateScrubberHoverAndDrag(_lastHoveredMouseX);
                    }
                }));
            }
        }

        private string GetHoveredSponsorCategoryLabel(double sec)
        {
            if (_sponsorSegments == null || _sponsorSegments.Count == 0) return null;
            for (int i = 0; i < _sponsorSegments.Count; i++)
            {
                var seg = _sponsorSegments[i];
                if (seg != null && sec >= seg.Start && sec <= seg.End)
                {
                    return seg.CategoryLabel;
                }
            }
            return null;
        }

        private double _lastSponsorTrackW = -1;

        public void SetSponsorSegments(List<SponsorSegment> segments)
        {
            _sponsorSegments = segments ?? new List<SponsorSegment>();
            _lastSponsorTrackW = -1;
            RenderSponsorSegmentsOnCanvas(true);
        }

        private void RenderSponsorSegmentsOnCanvas(bool force = false)
        {
            if (_sponsorSegmentsCanvas == null || _scrubberHitArea == null) return;
            
            double trackW = _scrubberHitArea.ActualWidth;
            if (trackW <= 2) return;

            if (!force && Math.Abs(trackW - _lastSponsorTrackW) < 1.0)
                return;

            _lastSponsorTrackW = trackW;
            _sponsorSegmentsCanvas.Children.Clear();

            if (_totalSeconds <= 0 || _sponsorSegments == null || _sponsorSegments.Count == 0)
                return;

            double trackH = _sponsorSegmentsCanvas.Height > 0 ? _sponsorSegmentsCanvas.Height : 3.0;

            for (int i = 0; i < _sponsorSegments.Count; i++)
            {
                var seg = _sponsorSegments[i];
                if (seg == null || seg.End <= seg.Start || seg.Start >= _totalSeconds)
                    continue;

                double startRatio = Math.Max(0.0, Math.Min(1.0, seg.Start / _totalSeconds));
                double endRatio = Math.Max(0.0, Math.Min(1.0, Math.Min(_totalSeconds, seg.End) / _totalSeconds));
                double sx = startRatio * trackW;
                double segW = Math.Max(3.0, (endRatio - startRatio) * trackW);

                Color c = seg.CategoryColor;
                var rect = new Rectangle
                {
                    Width = segW,
                    Height = trackH,
                    Fill = new SolidColorBrush(Color.FromArgb(230, c.R, c.G, c.B)),
                    IsHitTestVisible = false
                };
                Canvas.SetLeft(rect, sx);
                Canvas.SetTop(rect, 0);
                _sponsorSegmentsCanvas.Children.Add(rect);
            }
        }

        public void SetChapters(IEnumerable<VideoChapter> chapters)
        {
            RunOnWpfDispatcher(() =>
            {
                _chapters = chapters != null ? chapters.ToList() : new List<VideoChapter>();
                _lastChaptersTrackW = -1;
                RenderChaptersOnCanvas(true);
            });
        }

        private string FindChapterTitleAt(double seconds)
        {
            if (_chapters == null || _chapters.Count == 0) return string.Empty;
            string current = string.Empty;
            for (int i = 0; i < _chapters.Count; i++)
            {
                if (_chapters[i].StartSeconds <= seconds + 0.5)
                    current = _chapters[i].Title;
                else
                    break;
            }
            return current ?? string.Empty;
        }

        private void RenderChaptersOnCanvas(bool force = false)
        {
            if (_chaptersCanvas == null || _scrubberHitArea == null) return;
            double totalWidth = _scrubberHitArea.ActualWidth;
            if (totalWidth <= 0) return;

            if (!force && Math.Abs(totalWidth - _lastChaptersTrackW) < 1.0 && Math.Abs(_totalSeconds - _lastChaptersTotalSec) < 0.5)
                return;

            _lastChaptersTrackW = totalWidth;
            _lastChaptersTotalSec = _totalSeconds;
            _chaptersCanvas.Children.Clear();
            if (_totalSeconds <= 0 || _chapters == null || _chapters.Count <= 1)
                return;

            for (int i = 0; i < _chapters.Count; i++)
            {
                double start = _chapters[i].StartSeconds;
                if (start <= 1.0 || start >= _totalSeconds - 1.0) continue;
                double left = (start / _totalSeconds) * totalWidth;
                var tick = new System.Windows.Shapes.Rectangle
                {
                    Width = 2.0,
                    Height = 6.0,
                    Fill = new SolidColorBrush(Color.FromArgb(230, 15, 15, 17)),
                    IsHitTestVisible = false
                };
                Canvas.SetLeft(tick, Math.Max(0, left - 1.0));
                Canvas.SetTop(tick, -1.5);
                _chaptersCanvas.Children.Add(tick);
            }
        }

        private void RunOnWpfDispatcher(Action action)
        {
            if (action == null) return;
            var disp = _rootContent?.Dispatcher ?? System.Windows.Application.Current?.Dispatcher;
            if (disp == null || disp.CheckAccess())
            {
                action();
            }
            else
            {
                disp.BeginInvoke(action);
            }
        }

        public void ShowSponsorSkipToast(string categoryLabel)
        {
            RunOnWpfDispatcher(() =>
            {
                if (_sponsorSkipBanner == null || _sponsorSkipBannerText == null) return;

                _sponsorSkipBannerText.Text = string.IsNullOrWhiteSpace(categoryLabel)
                    ? "Patrocínio pulado"
                    : string.Format("{0} pulado", categoryLabel);

                _sponsorSkipBanner.Visibility = Visibility.Visible;
                _sponsorBannerTimer.Stop();
                _sponsorBannerTimer.Start();
            });
        }

        public void ShowToast(string message)
        {
            if (string.IsNullOrWhiteSpace(message)) return;

            RunOnWpfDispatcher(() =>
            {
                if (_toastNotificationBanner != null && _toastNotificationText != null)
                {
                    _toastNotificationText.Text = message;
                    _toastNotificationBanner.Visibility = Visibility.Visible;
                    _toastTimer.Stop();
                    _toastTimer.Start();
                }
                else if (_sponsorSkipBanner != null && _sponsorSkipBannerText != null)
                {
                    _sponsorSkipBannerText.Text = message;
                    _sponsorSkipBanner.Visibility = Visibility.Visible;
                    _sponsorBannerTimer.Stop();
                    _sponsorBannerTimer.Start();
                }
            });
        }

        public void ShowOsd(string text)
        {
            if (string.IsNullOrWhiteSpace(text)) return;

            RunOnWpfDispatcher(() =>
            {
                if (_centerOsdBadge == null || _centerOsdText == null) return;

                _osdGeneration++;
                _centerOsdText.Text = text;
                _centerOsdBadge.BeginAnimation(UIElement.OpacityProperty, null);
                _centerOsdBadge.Opacity = 1.0;
                _centerOsdBadge.Visibility = Visibility.Visible;

                _osdHoldTimer.Stop();
                _osdHoldTimer.Start();
            });
        }

        private void OnOsdHoldTimerTick(object sender, EventArgs e)
        {
            _osdHoldTimer.Stop();
            if (_centerOsdBadge == null) return;

            int gen = _osdGeneration;
            var fadeOut = new DoubleAnimation
            {
                From = _centerOsdBadge.Opacity,
                To = 0.0,
                Duration = TimeSpan.FromMilliseconds(150),
                EasingFunction = new QuadraticEase { EasingMode = EasingMode.EaseOut }
            };
            fadeOut.Completed += (s, args) =>
            {
                if (_osdGeneration == gen && _centerOsdBadge != null)
                {
                    _centerOsdBadge.Visibility = Visibility.Collapsed;
                }
            };
            _centerOsdBadge.BeginAnimation(UIElement.OpacityProperty, fadeOut);
        }

        // ====================================================================
        // VOLUME EXPANDABLE SLIDER & PLAYBACK STATE UPDATES
        // ====================================================================

        private void ExpandVolumeSlider(bool expand)
        {
            if (_volumeSliderExpandContainer == null) return;
            var anim = new DoubleAnimation
            {
                To = expand ? 78.0 : 0.0,
                Duration = TimeSpan.FromMilliseconds(160),
                EasingFunction = new QuadraticEase { EasingMode = EasingMode.EaseOut }
            };
            _volumeSliderExpandContainer.BeginAnimation(FrameworkElement.WidthProperty, anim);
        }

        private void SetVolumeFromSliderMouseX(double mouseX)
        {
            if (_volumeSliderHitGrid == null) return;
            double trackW = Math.Max(10.0, _volumeSliderHitGrid.ActualWidth > 0 ? _volumeSliderHitGrid.ActualWidth : 68.0);
            double ratio = Math.Max(0.0, Math.Min(1.0, mouseX / trackW));
            _volume0To1 = Math.Round(ratio, 2);
            _isMuted = (_volume0To1 <= 0.001);
            UpdateAudioVisuals(_volume0To1, _isMuted);
            if (VolumeChanged != null) VolumeChanged(_volume0To1);
        }

        public void UpdatePlaybackState(bool isPlaying, double currentSec, double bufferedSec, double totalSec)
        {
            RunOnWpfDispatcher(() =>
            {
                bool wasPlaying = _isPlaying;
                _isPlaying = isPlaying;
                _totalSeconds = Math.Max(0.0, totalSec);

                // O timer do player apenas atualiza o valor visual se o usuário NÃO estiver arrastando a barra
                if (!_isUserDraggingScrubber)
                {
                    _currentSeconds = Math.Max(0.0, currentSec);
                }
                _bufferedSeconds = Math.Max(_currentSeconds, bufferedSec);

                if (!isPlaying)
                {
                    _inactivityTimer.Stop();
                    RestoreCursor();
                    if (_controlsHiddenByInactivity)
                    {
                        SetControlsVisibleAnimated(true);
                    }
                }
                else if (!wasPlaying && _isOverlayAttachedAndActive && !_inactivityTimer.IsEnabled &&
                         (_gearPopupPanel == null || _gearPopupPanel.Visibility != Visibility.Visible))
                {
                    _lastMouseMoveUtc = DateTime.UtcNow;
                    _inactivityTimer.Start();
                }

                if (_iconPlay != null && _iconPause != null)
                {
                    _iconPlay.Visibility = isPlaying ? Visibility.Collapsed : Visibility.Visible;
                    _iconPause.Visibility = isPlaying ? Visibility.Visible : Visibility.Collapsed;
                }

                RefreshTimelineVisuals();
            });
        }

        public void SetLiveStreamMode(bool isLive)
        {
            _isLiveStream = isLive;
            RunOnWpfDispatcher(() => RefreshTimelineVisuals());
        }

        public void StartAutoplayCountdown(TrackItem nextVideo)
        {
            if (nextVideo == null) return;

            RunOnWpfDispatcher(() =>
            {
                if (_autoplayOverlay == null)
                {
                    if (NextVideoRequested != null) NextVideoRequested();
                    return;
                }

                _autoplayTicksRemaining = 80;
                if (_autoplayCountdownText != null) _autoplayCountdownText.Text = "8";
                if (_autoplayProgressBar != null) _autoplayProgressBar.Value = 80;
                if (_autoplayNextTitle != null) _autoplayNextTitle.Text = !string.IsNullOrWhiteSpace(nextVideo.Title) ? nextVideo.Title : "Próximo Vídeo";
                if (_autoplayNextAuthor != null) _autoplayNextAuthor.Text = !string.IsNullOrWhiteSpace(nextVideo.Artist) ? nextVideo.Artist : "Canal do YouTube";
                if (_autoplayNextThumbnail != null) _autoplayNextThumbnail.Source = null;

                _autoplayOverlay.IsHitTestVisible = true;
                _autoplayOverlay.Visibility = Visibility.Visible;
                SetControlsVisibleAnimated(true);

                string thumbUrl = !string.IsNullOrWhiteSpace(nextVideo.ThumbnailUrl)
                    ? nextVideo.ThumbnailUrl
                    : (!string.IsNullOrWhiteSpace(nextVideo.Id) ? "https://i.ytimg.com/vi/" + nextVideo.Id + "/mqdefault.jpg" : "");

                if (!string.IsNullOrEmpty(thumbUrl))
                {
                    string targetVideoId = nextVideo.Id;
                    System.Threading.Tasks.Task.Run(async () =>
                    {
                        try
                        {
                            byte[] bytes = await HttpPipeline.GetByteArrayAsync(thumbUrl).ConfigureAwait(false);
                            if (bytes != null && bytes.Length > 0)
                            {
                                var bmp = StoryboardService.DecodeFrozenBitmap(bytes, 280);
                                if (bmp != null)
                                {
                                    RunOnWpfDispatcher(() =>
                                    {
                                        if (_autoplayNextThumbnail != null && _autoplayOverlay != null && _autoplayOverlay.Visibility == Visibility.Visible)
                                        {
                                            _autoplayNextThumbnail.Source = bmp;
                                        }
                                    });
                                }
                            }
                        }
                        catch { }
                    });
                }

                _autoplayTimer.Stop();
                _autoplayTimer.Start();
            });
        }

        public void CancelAutoplayCountdown()
        {
            RunOnWpfDispatcher(() =>
            {
                _autoplayTimer.Stop();
                if (_autoplayOverlay != null)
                {
                    _autoplayOverlay.IsHitTestVisible = false;
                    _autoplayOverlay.Visibility = Visibility.Collapsed;
                }
            });
        }

        private void OnAutoplayTimerTick(object sender, EventArgs e)
        {
            _autoplayTicksRemaining--;
            if (_autoplayProgressBar != null)
            {
                _autoplayProgressBar.Value = Math.Max(0, _autoplayTicksRemaining);
            }
            if (_autoplayCountdownText != null)
            {
                int secsLeft = (int)Math.Ceiling(Math.Max(0, _autoplayTicksRemaining) / 10.0);
                _autoplayCountdownText.Text = secsLeft.ToString();
            }

            if (_autoplayTicksRemaining <= 0)
            {
                _autoplayTimer.Stop();
                if (_autoplayOverlay != null)
                {
                    _autoplayOverlay.IsHitTestVisible = false;
                    _autoplayOverlay.Visibility = Visibility.Collapsed;
                }
                if (NextVideoRequested != null) NextVideoRequested();
            }
        }

        private void BtnAutoplayCancel_Click(object sender, RoutedEventArgs e)
        {
            RegisterUserActivity();
            CancelAutoplayCountdown();
            if (AutoplayCancelled != null) AutoplayCancelled();
        }

        private void BtnAutoplayPlayNow_Click(object sender, RoutedEventArgs e)
        {
            RegisterUserActivity();
            CancelAutoplayCountdown();
            if (NextVideoRequested != null) NextVideoRequested();
        }

        private void RefreshTimelineVisuals()
        {
            if (_scrubberHitArea == null) return;

            double trackW = _scrubberHitArea.ActualWidth;

            if (_isLiveStream)
            {
                if (_txtTimeDisplay != null && _lastDisplayedTimeText != "🔴 AO VIVO")
                {
                    _lastDisplayedTimeText = "🔴 AO VIVO";
                    _txtTimeDisplay.Text = _lastDisplayedTimeText;
                }
                if (_scrubberPlayedBar != null) _scrubberPlayedBar.Width = Math.Max(0.0, trackW);
                if (_scrubberBufferBar != null) _scrubberBufferBar.Width = Math.Max(0.0, trackW);
                if (_scrubberThumbHead != null && trackW > 1.0)
                {
                    Canvas.SetLeft(_scrubberThumbHead, Math.Max(-6.5, trackW - 6.5));
                }
                return;
            }

            double displayCur = _isUserDraggingScrubber ? _dragScrubTargetSeconds : _currentSeconds;
            if (_txtTimeDisplay != null)
            {
                string nextTimeText = string.Format("{0} / {1}",
                    YouTubeService.FormatSeconds(displayCur),
                    YouTubeService.FormatSeconds(_totalSeconds));
                if (_lastDisplayedTimeText != nextTimeText)
                {
                    _lastDisplayedTimeText = nextTimeText;
                    _txtTimeDisplay.Text = nextTimeText;
                }
            }

            if (trackW <= 1.0 || _totalSeconds <= 0.0)
            {
                if (_scrubberPlayedBar != null) _scrubberPlayedBar.Width = 0;
                if (_scrubberBufferBar != null) _scrubberBufferBar.Width = 0;
                if (_scrubberThumbHead != null) Canvas.SetLeft(_scrubberThumbHead, -6.5);
                return;
            }

            double curRatio = Math.Max(0.0, Math.Min(1.0, displayCur / _totalSeconds));
            double bufRatio = Math.Max(0.0, Math.Min(1.0, _bufferedSeconds / _totalSeconds));

            double curPx = curRatio * trackW;
            double bufPx = bufRatio * trackW;

            if (_scrubberPlayedBar != null) _scrubberPlayedBar.Width = curPx;
            if (_scrubberBufferBar != null) _scrubberBufferBar.Width = bufPx;
            if (_scrubberThumbHead != null)
            {
                Canvas.SetLeft(_scrubberThumbHead, Math.Max(-6.5, Math.Min(trackW - 6.5, curPx - 6.5)));
            }

            RenderSponsorSegmentsOnCanvas();
            RenderChaptersOnCanvas();
        }

        public void UpdateAudioVisuals(double volume0To1, bool isMuted)
        {
            _volume0To1 = Math.Max(0.0, Math.Min(1.0, volume0To1));
            _isMuted = isMuted;

            bool effectiveMute = isMuted || _volume0To1 <= 0.005;
            if (_iconSpeakerActive != null && _iconSpeakerMuted != null)
            {
                _iconSpeakerActive.Visibility = effectiveMute ? Visibility.Collapsed : Visibility.Visible;
                _iconSpeakerMuted.Visibility = effectiveMute ? Visibility.Visible : Visibility.Collapsed;
            }

            double maxBarW = 68.0;
            double fillW = effectiveMute ? 0.0 : (_volume0To1 * maxBarW);
            if (_volumeSliderFillBar != null)
            {
                _volumeSliderFillBar.Width = fillW;
            }
            if (_volumeSliderThumb != null)
            {
                Canvas.SetLeft(_volumeSliderThumb, Math.Max(0.0, Math.Min(maxBarW - 12.0, fillW - 6.0)));
            }
        }

        public void SetAvailableQualities(IEnumerable<int> resolutions)
        {
            RunOnWpfDispatcher(() =>
            {
                var list = resolutions != null
                    ? resolutions.Where(h => h >= 144).Distinct().OrderByDescending(h => h).ToList()
                    : new List<int>();

                if (list.Count == 0)
                {
                    list = new List<int> { 1080, 720, 480, 360 };
                }
                else if (_activeQualityHeight >= 144 && !list.Contains(_activeQualityHeight))
                {
                    list.Add(_activeQualityHeight);
                    list = list.OrderByDescending(h => h).ToList();
                }

                _availableQualities = list;
                RebuildQualityButtons(_availableQualities);
                UpdateQualityAndSpeedVisuals(_activeQualityHeight, _activePlaybackSpeed);
            });
        }

        private void RebuildQualityButtons(List<int> resolutions)
        {
            if (_gearQualityGrid == null) return;

            _gearQualityGrid.Children.Clear();
            _gearQualityButtons.Clear();

            var list = (resolutions != null && resolutions.Count > 0)
                ? resolutions
                : new List<int> { 1080, 720, 480, 360 };

            _gearQualityGrid.Columns = list.Count <= 3 ? Math.Max(1, list.Count) : 4;

            var style = _rootContent != null ? _rootContent.TryFindResource("GearOptionBtnStyle") as Style : null;
            foreach (int h in list)
            {
                int targetHeight = h;
                var btn = new Button
                {
                    Content = h >= 2160 ? "4K" : (h + "p"),
                    Tag = targetHeight,
                    Margin = new Thickness(2),
                    Padding = new Thickness(4, 5, 4, 5),
                    FontFamily = new FontFamily("Segoe UI Variable Text, Segoe UI"),
                    FontSize = 11.5,
                    Cursor = Cursors.Hand
                };
                if (style != null)
                {
                    btn.Style = style;
                }
                else
                {
                    btn.Foreground = new SolidColorBrush(Color.FromRgb(0xF4, 0xF4, 0xF5));
                    btn.Background = new SolidColorBrush(Color.FromRgb(0x22, 0x22, 0x26));
                    btn.BorderBrush = new SolidColorBrush(Color.FromRgb(0x27, 0x27, 0x2A));
                    btn.BorderThickness = new Thickness(1);
                }
                btn.Click += (s, e) => SelectQualityFromGear(targetHeight);

                _gearQualityGrid.Children.Add(btn);
                _gearQualityButtons[targetHeight] = btn;
            }
        }

        public void UpdateQualityAndSpeedVisuals(int qualityHeight, double speedRatio)
        {
            _activeQualityHeight = qualityHeight;
            _activePlaybackSpeed = speedRatio;

            if (_txtQualityBadge != null)
            {
                string resStr = qualityHeight >= 2160
                    ? "4K"
                    : (qualityHeight >= 720 ? qualityHeight + "p HD" : qualityHeight + "p");
                _txtQualityBadge.Text = !string.IsNullOrEmpty(_activeCodecShort)
                    ? resStr + " • " + _activeCodecShort
                    : resStr;
            }

            if (_txtSpeedBadge != null)
            {
                _txtSpeedBadge.Text = string.Format(System.Globalization.CultureInfo.InvariantCulture, "{0:0.##}x", speedRatio);
            }

            foreach (var kv in _gearQualityButtons)
            {
                StyleGearButton(kv.Value, kv.Key == qualityHeight);
            }

            int spd = (int)Math.Round(speedRatio * 100);
            StyleGearButton(_btnGearSpd050, spd == 50);
            StyleGearButton(_btnGearSpd075, spd == 75);
            StyleGearButton(_btnGearSpd100, spd == 100);
            StyleGearButton(_btnGearSpd125, spd == 125);
            StyleGearButton(_btnGearSpd150, spd == 150);
            StyleGearButton(_btnGearSpd200, spd == 200);
        }

        private void StyleGearButton(Button btn, bool active)
        {
            if (btn == null) return;
            btn.Background = active
                ? new SolidColorBrush(Color.FromRgb(0x3E, 0xA6, 0xFF))
                : new SolidColorBrush(Color.FromRgb(0x22, 0x22, 0x26));
            btn.Foreground = active
                ? new SolidColorBrush(Color.FromRgb(0x0F, 0x0F, 0x11))
                : new SolidColorBrush(Color.FromRgb(0xF4, 0xF4, 0xF5));
            btn.FontWeight = active ? FontWeights.SemiBold : FontWeights.Normal;
        }

        private void ToggleGearPopup()
        {
            if (_gearPopupPanel == null) return;
            bool opening = _gearPopupPanel.Visibility != Visibility.Visible;
            _gearPopupPanel.IsHitTestVisible = opening;
            _gearPopupPanel.Visibility = opening ? Visibility.Visible : Visibility.Collapsed;
            if (opening)
            {
                RestoreCursor();
                _inactivityTimer.Stop();
            }
            else if (_isPlaying && _isOverlayAttachedAndActive)
            {
                _lastMouseMoveUtc = DateTime.UtcNow;
                _inactivityTimer.Start();
            }
        }

        private void SelectQualityFromGear(int q)
        {
            if (_gearPopupPanel != null)
            {
                _gearPopupPanel.IsHitTestVisible = false;
                _gearPopupPanel.Visibility = Visibility.Collapsed;
            }
            UpdateQualityAndSpeedVisuals(q, _activePlaybackSpeed);
            if (QualityChanged != null) QualityChanged(q);
        }

        private void SelectSpeedFromGear(double spd)
        {
            if (_gearPopupPanel != null)
            {
                _gearPopupPanel.IsHitTestVisible = false;
                _gearPopupPanel.Visibility = Visibility.Collapsed;
            }
            UpdateQualityAndSpeedVisuals(_activeQualityHeight, spd);
            if (SpeedChanged != null) SpeedChanged(spd);
        }

        private void OnOverlayMouseWheel(object sender, MouseWheelEventArgs e)
        {
            if (_isDisposed || !_isOverlayAttachedAndActive || _rootContent == null || _rootContent.Visibility != Visibility.Visible)
                return;
            if (_autoplayOverlay != null && _autoplayOverlay.Visibility == Visibility.Visible)
                return;
            if (_gearPopupPanel != null && _gearPopupPanel.Visibility == Visibility.Visible && _gearPopupPanel.IsMouseOver)
                return;

            // Ajusta volume com a rodinha do mouse em Tela Cheia ou sobre o controle de volume;
            // fora de tela cheia, repassa o scroll para a página Watch rolar até os recomendados!
            bool overVolume = _volumeHoverGroup != null && _volumeHoverGroup.IsMouseOver;
            if (!IsFullscreenMode && !overVolume)
            {
                UnhandledMouseWheel?.Invoke(e.Delta);
                e.Handled = true;
                return;
            }

            RegisterUserActivity();
            double delta = e.Delta > 0 ? 0.05 : -0.05;
            double nextVol = Math.Max(0.0, Math.Min(1.0, Math.Round(_volume0To1 + delta, 2)));
            if (VolumeChanged != null) VolumeChanged(nextVol);
            e.Handled = true;
        }

        private void OnOverlayPreviewKeyDown(object sender, KeyEventArgs e)
        {
            if ((Keyboard.Modifiers & (ModifierKeys.Control | ModifierKeys.Alt | ModifierKeys.Windows)) != ModifierKeys.None)
                return;
            if (Keyboard.FocusedElement is TextBoxBase)
                return;

            RegisterUserActivity();
            Key key = e.Key == Key.System ? e.SystemKey : e.Key;
            if (ShortcutKeyPressed != null)
            {
                ShortcutKeyPressed(key);
                e.Handled = true;
            }
        }

        public void Dispose()
        {
            if (_isDisposed) return;
            _isDisposed = true;

            try { RestoreCursor(); } catch { }
            try { _inactivityTimer.Stop(); } catch { }
            try { _sponsorBannerTimer.Stop(); } catch { }
            try { _toastTimer.Stop(); } catch { }
            try { _osdHoldTimer.Stop(); } catch { }
            try { _autoplayTimer.Stop(); } catch { }
        }
    }
}
