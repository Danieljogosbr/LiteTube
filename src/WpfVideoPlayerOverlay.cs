using System;
using System.Collections.Generic;
using System.IO;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
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

        [DllImport("user32.dll")]
        private static extern bool GetCursorPos(out POINT lpPoint);

        private readonly System.Windows.Forms.Control _targetControl;
        private readonly System.Windows.Forms.Form _ownerForm;
        private Window _overlayWindow;
        private FrameworkElement _rootContent;

        // UI Elements from WpfVideoPlayerOverlay.xaml
        private Grid _rootOverlayGrid;
        private Grid _controlsContainerGrid;
        private Border _toastNotificationBanner;
        private TextBlock _toastNotificationText;
        private Border _sponsorSkipBanner;
        private TextBlock _sponsorSkipBannerText;
        private Border _gearPopupPanel;

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
        private Border _btnSettingsGear;
        private Border _btnTheaterMode;
        private Border _btnFullscreen;

        private Button _btnGearQ144;
        private Button _btnGearQ240;
        private Button _btnGearQ360;
        private Button _btnGearQ480;
        private Button _btnGearQ720;
        private Button _btnGearQ1080;
        private Button _btnGearQ1440;
        private Button _btnGearQ2160;
        private Button _btnGearSpd075;
        private Button _btnGearSpd100;
        private Button _btnGearSpd125;
        private Button _btnGearSpd150;
        private Button _btnGearSpd200;
        private Button _btnGearAudioDialog;

        // State
        private bool _isPlaying = false;
        private bool _isMuted = false;
        private double _volume0To1 = 0.85;
        private double _currentSeconds = 0.0;
        private double _bufferedSeconds = 0.0;
        private double _totalSeconds = 0.0;
        private int _activeQualityHeight = 720;
        private double _activePlaybackSpeed = 1.0;

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
        private bool _isOverlayAttachedAndActive = false;
        private bool _isDisposed = false;

        private List<SponsorSegment> _sponsorSegments = new List<SponsorSegment>();
        private readonly DispatcherTimer _inactivityTimer;
        private readonly DispatcherTimer _sponsorBannerTimer;
        private readonly DispatcherTimer _toastTimer;
        private POINT _lastGlobalCursorPos;
        private DateTime _lastMouseMoveUtc = DateTime.UtcNow;

        // Events to bridge with VideoEnvironmentControl & FlyleafVideoControl
        public event Action PlayPauseRequested;
        public event Action NextVideoRequested;
        public event Action<double> OnUserSeekCommitted;
        public event Action<double> SeekRequested;
        public event Action MuteToggleRequested;
        public event Action<double> VolumeChanged;
        public event Action<int> QualityChanged;
        public event Action<double> SpeedChanged;
        public event Action TheaterToggleRequested;
        public event Action FullscreenToggleRequested;
        public event Action AudioDialogRequested;
        public event Action<System.Windows.Forms.Keys> ShortcutKeyPressed;

        public bool IsUserDraggingScrubber { get { return _isDraggingScrubber; } }
        public bool IsOverlayVisible { get { return _isOverlayAttachedAndActive && _overlayWindow != null && _overlayWindow.IsVisible; } }
        public FrameworkElement View { get { return _rootContent; } }

        public WpfVideoPlayerOverlay()
        {
            GetCursorPos(out _lastGlobalCursorPos);

            InitializeOverlayContent(false);
            WireOverlayEvents();

            _inactivityTimer = new DispatcherTimer
            {
                Interval = TimeSpan.FromMilliseconds(250)
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
        }

        public WpfVideoPlayerOverlay(System.Windows.Forms.Control targetVideoContainer, System.Windows.Forms.Form ownerForm)
        {
            _targetControl = targetVideoContainer;
            _ownerForm = ownerForm;

            GetCursorPos(out _lastGlobalCursorPos);

            InitializeOverlayContent(true);
            WireOverlayEvents();
            HookWinFormsLifecycleEvents();

            _inactivityTimer = new DispatcherTimer
            {
                Interval = TimeSpan.FromMilliseconds(250)
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
        }

        private void InitializeOverlayContent(bool createWindow)
        {
            if (createWindow)
            {
                _overlayWindow = new Window
                {
                    WindowStyle = WindowStyle.None,
                    AllowsTransparency = true,
                    Background = Brushes.Transparent,
                    ShowInTaskbar = false,
                    ResizeMode = ResizeMode.NoResize,
                    Topmost = false,
                    ShowActivated = false,
                    Width = 640,
                    Height = 360,
                    Left = -10000,
                    Top = -10000
                };
            }

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

            if (_overlayWindow != null)
            {
                _overlayWindow.Content = _rootContent;
            }

            _rootOverlayGrid = FindElement<Grid>("RootOverlayGrid");
            _controlsContainerGrid = FindElement<Grid>("ControlsContainerGrid");
            _toastNotificationBanner = FindElement<Border>("ToastNotificationBanner");
            _toastNotificationText = FindElement<TextBlock>("ToastNotificationText");
            _sponsorSkipBanner = FindElement<Border>("SponsorSkipBanner");
            _sponsorSkipBannerText = FindElement<TextBlock>("SponsorSkipBannerText");
            _gearPopupPanel = FindElement<Border>("GearPopupPanel");

            _scrubberHitArea = FindElement<Grid>("ScrubberHitArea");
            _scrubberBgTrack = FindElement<Border>("ScrubberBgTrack");
            _scrubberBufferBar = FindElement<Border>("ScrubberBufferBar");
            _scrubberHoverBar = FindElement<Border>("ScrubberHoverBar");
            _sponsorSegmentsCanvas = FindElement<Canvas>("SponsorSegmentsCanvas");
            _scrubberPlayedBar = FindElement<Border>("ScrubberPlayedBar");
            _scrubberThumbHead = FindElement<Ellipse>("ScrubberThumbHead");
            _scrubberTooltipBorder = FindElement<Border>("ScrubberTooltipBorder");
            _scrubberPreviewImageBorder = FindElement<Border>("ScrubberPreviewImageBorder");
            _scrubberPreviewImage = FindElement<System.Windows.Controls.Image>("ScrubberPreviewImage");
            _scrubberTooltipText = FindElement<TextBlock>("ScrubberTooltipText");

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
            _btnSettingsGear = FindElement<Border>("BtnSettingsGear");
            _btnTheaterMode = FindElement<Border>("BtnTheaterMode");
            _btnFullscreen = FindElement<Border>("BtnFullscreen");

            _btnGearQ144 = FindElement<Button>("BtnGearQ144");
            _btnGearQ240 = FindElement<Button>("BtnGearQ240");
            _btnGearQ360 = FindElement<Button>("BtnGearQ360");
            _btnGearQ480 = FindElement<Button>("BtnGearQ480");
            _btnGearQ720 = FindElement<Button>("BtnGearQ720");
            _btnGearQ1080 = FindElement<Button>("BtnGearQ1080");
            _btnGearQ1440 = FindElement<Button>("BtnGearQ1440");
            _btnGearQ2160 = FindElement<Button>("BtnGearQ2160");
            _btnGearSpd075 = FindElement<Button>("BtnGearSpd075");
            _btnGearSpd100 = FindElement<Button>("BtnGearSpd100");
            _btnGearSpd125 = FindElement<Button>("BtnGearSpd125");
            _btnGearSpd150 = FindElement<Button>("BtnGearSpd150");
            _btnGearSpd200 = FindElement<Button>("BtnGearSpd200");
            _btnGearAudioDialog = FindElement<Button>("BtnGearAudioDialog");
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
                _rootContent.PreviewMouseMove += (s, e) => RegisterUserActivity();
                _rootContent.PreviewKeyDown += OnOverlayPreviewKeyDown;
                _rootContent.MouseWheel += OnOverlayMouseWheel;
                _rootContent.SizeChanged += (s, e) => RefreshTimelineVisuals();
            }

            if (_overlayWindow != null)
            {
                _overlayWindow.PreviewMouseMove += (s, e) => RegisterUserActivity();
                _overlayWindow.PreviewKeyDown += OnOverlayPreviewKeyDown;
                _overlayWindow.MouseWheel += OnOverlayMouseWheel;
                _overlayWindow.SizeChanged += (s, e) => RefreshTimelineVisuals();
            }

            if (_rootOverlayGrid != null)
            {
                _rootOverlayGrid.MouseLeftButtonDown += (s, e) =>
                {
                    RegisterUserActivity();
                    if (e.OriginalSource == _rootOverlayGrid)
                    {
                        if (_gearPopupPanel != null && _gearPopupPanel.Visibility == Visibility.Visible)
                        {
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
                    RegisterUserActivity();
                    UpdateScrubberHoverAndDrag(e.GetPosition(_scrubberHitArea).X);
                };

                _scrubberHitArea.PreviewMouseUp += Scrubber_PreviewMouseUp;
            }

            // Transport Buttons
            WireButton(_btnPlayPause, () => { if (PlayPauseRequested != null) PlayPauseRequested(); });
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

            // Settings Gear, Quality Pill, Theater & Fullscreen
            WireButton(_btnSettingsGear, () => ToggleGearPopup());
            WireButton(_badgeQualityPill, () => ToggleGearPopup());
            WireButton(_btnTheaterMode, () => { if (TheaterToggleRequested != null) TheaterToggleRequested(); });
            WireButton(_btnFullscreen, () => { if (FullscreenToggleRequested != null) FullscreenToggleRequested(); });

            // Gear Menu Actions
            if (_btnGearQ144 != null) _btnGearQ144.Click += (s, e) => SelectQualityFromGear(144);
            if (_btnGearQ240 != null) _btnGearQ240.Click += (s, e) => SelectQualityFromGear(240);
            if (_btnGearQ360 != null) _btnGearQ360.Click += (s, e) => SelectQualityFromGear(360);
            if (_btnGearQ480 != null) _btnGearQ480.Click += (s, e) => SelectQualityFromGear(480);
            if (_btnGearQ720 != null) _btnGearQ720.Click += (s, e) => SelectQualityFromGear(720);
            if (_btnGearQ1080 != null) _btnGearQ1080.Click += (s, e) => SelectQualityFromGear(1080);
            if (_btnGearQ1440 != null) _btnGearQ1440.Click += (s, e) => SelectQualityFromGear(1440);
            if (_btnGearQ2160 != null) _btnGearQ2160.Click += (s, e) => SelectQualityFromGear(2160);

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

        private void HookWinFormsLifecycleEvents()
        {
            if (_targetControl != null)
            {
                _targetControl.Resize += (s, e) => SyncOverlayBounds();
                _targetControl.Move += (s, e) => SyncOverlayBounds();
                _targetControl.VisibleChanged += (s, e) => SyncOverlayBounds();
                _targetControl.HandleCreated += (s, e) => SyncOverlayBounds();

                System.Windows.Forms.Control parent = _targetControl.Parent;
                while (parent != null && parent != _ownerForm)
                {
                    parent.VisibleChanged += (s, e) => SyncOverlayBounds();
                    parent.Resize += (s, e) => SyncOverlayBounds();
                    parent = parent.Parent;
                }
            }

            if (_ownerForm != null)
            {
                _ownerForm.Move += (s, e) => SyncOverlayBounds();
                _ownerForm.Resize += (s, e) => SyncOverlayBounds();
                _ownerForm.LocationChanged += (s, e) => SyncOverlayBounds();
                _ownerForm.VisibleChanged += (s, e) => SyncOverlayBounds();
                _ownerForm.Activated += (s, e) => SyncOverlayBounds();
                _ownerForm.Deactivate += (s, e) =>
                {
                    if (_gearPopupPanel != null) _gearPopupPanel.Visibility = Visibility.Collapsed;
                };
            }
        }

        private bool IsTargetHierarchyVisible()
        {
            if (_targetControl == null || _ownerForm == null) return false;
            if (!_ownerForm.Visible || _ownerForm.WindowState == System.Windows.Forms.FormWindowState.Minimized)
                return false;

            System.Windows.Forms.Control cur = _targetControl;
            while (cur != null)
            {
                if (!cur.Visible || !cur.IsHandleCreated)
                    return false;
                if (cur == _ownerForm)
                    return true;
                cur = cur.Parent;
            }
            return _targetControl.Visible && _targetControl.IsHandleCreated;
        }

        public void SetOverlayActive(bool active)
        {
            _isOverlayAttachedAndActive = active;
            if (active)
            {
                if (_rootContent != null)
                {
                    _rootContent.Visibility = Visibility.Visible;
                }
                if (_rootOverlayGrid != null)
                {
                    _rootOverlayGrid.Visibility = Visibility.Visible;
                }
                if (_controlsContainerGrid != null)
                {
                    _controlsContainerGrid.Visibility = Visibility.Visible;
                    _controlsContainerGrid.Opacity = 1.0;
                }
                _inactivityTimer.Start();
                RegisterUserActivity();
                SyncOverlayBounds();
            }
            else
            {
                _inactivityTimer.Stop();
                _sponsorBannerTimer.Stop();
                if (_gearPopupPanel != null) _gearPopupPanel.Visibility = Visibility.Collapsed;
                if (_sponsorSkipBanner != null) _sponsorSkipBanner.Visibility = Visibility.Collapsed;
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
                if (_overlayWindow != null)
                {
                    _overlayWindow.Left = -10000;
                    _overlayWindow.Top = -10000;
                    if (_overlayWindow.IsVisible)
                    {
                        _overlayWindow.Hide();
                    }
                }
            }
        }

        public void SyncOverlayBounds()
        {
            if (_isDisposed)
                return;

            if (_overlayWindow == null)
            {
                RefreshTimelineVisuals();
                return;
            }

            if (_targetControl == null || _ownerForm == null)
                return;

            try
            {
                bool shouldShow = _isOverlayAttachedAndActive &&
                                  IsTargetHierarchyVisible() &&
                                  _targetControl.Width > 20 &&
                                  _targetControl.Height > 20;

                if (!shouldShow)
                {
                    _overlayWindow.Left = -10000;
                    _overlayWindow.Top = -10000;
                    if (_overlayWindow.IsVisible)
                    {
                        _overlayWindow.Hide();
                    }
                    return;
                }

                if (_rootOverlayGrid != null && _rootOverlayGrid.Visibility != Visibility.Visible)
                {
                    _rootOverlayGrid.Visibility = Visibility.Visible;
                }
                if (_controlsContainerGrid != null && _controlsContainerGrid.Visibility != Visibility.Visible)
                {
                    _controlsContainerGrid.Visibility = Visibility.Visible;
                    _controlsContainerGrid.Opacity = 1.0;
                }

                if (_ownerForm.IsHandleCreated)
                {
                    var helper = new System.Windows.Interop.WindowInteropHelper(_overlayWindow);
                    if (helper.Owner != _ownerForm.Handle)
                    {
                        helper.Owner = _ownerForm.Handle;
                    }
                }

                System.Drawing.Point screenPt = _targetControl.PointToScreen(System.Drawing.Point.Empty);
                int pixelW = _targetControl.Width;
                int pixelH = _targetControl.Height;

                PresentationSource source = PresentationSource.FromVisual(_overlayWindow);
                double dpiScaleX = 1.0;
                double dpiScaleY = 1.0;
                if (source != null && source.CompositionTarget != null)
                {
                    Matrix m = source.CompositionTarget.TransformFromDevice;
                    dpiScaleX = m.M11;
                    dpiScaleY = m.M22;
                }
                else
                {
                    using (var g = System.Drawing.Graphics.FromHwnd(IntPtr.Zero))
                    {
                        dpiScaleX = 96.0 / g.DpiX;
                        dpiScaleY = 96.0 / g.DpiY;
                    }
                }

                _overlayWindow.Left = screenPt.X * dpiScaleX;
                _overlayWindow.Top = screenPt.Y * dpiScaleY;
                _overlayWindow.Width = Math.Max(40, pixelW * dpiScaleX);
                _overlayWindow.Height = Math.Max(40, pixelH * dpiScaleY);

                if (!_overlayWindow.IsVisible)
                {
                    _overlayWindow.Show();
                }

                RefreshTimelineVisuals();
            }
            catch { }
        }

        // ====================================================================
        // AUTO-HIDE INACTIVITY (2.5 SECONDS + DOUBLEANIMATION)
        // ====================================================================

        public void RegisterUserActivity()
        {
            _lastMouseMoveUtc = DateTime.UtcNow;
            if (_controlsHiddenByInactivity || (_controlsContainerGrid != null && _controlsContainerGrid.Opacity < 0.99))
            {
                SetControlsVisibleAnimated(true);
            }
        }

        private void OnInactivityTimerTick(object sender, EventArgs e)
        {
            if (!_isOverlayAttachedAndActive)
                return;

            if (_overlayWindow != null)
            {
                if (!_overlayWindow.IsVisible) return;
                if (!IsTargetHierarchyVisible())
                {
                    SyncOverlayBounds();
                    return;
                }
            }
            else
            {
                if (_rootContent == null || _rootContent.Visibility != Visibility.Visible)
                    return;
            }

            POINT cur;
            if (GetCursorPos(out cur))
            {
                if (Math.Abs(cur.X - _lastGlobalCursorPos.X) > 2 || Math.Abs(cur.Y - _lastGlobalCursorPos.Y) > 2)
                {
                    _lastGlobalCursorPos = cur;
                    RegisterUserActivity();
                }
            }

            if (!_isPlaying || _isUserDraggingScrubber || _isDraggingVolume ||
                (_gearPopupPanel != null && _gearPopupPanel.Visibility == Visibility.Visible))
            {
                if (_controlsHiddenByInactivity)
                {
                    SetControlsVisibleAnimated(true);
                }
                return;
            }

            double idleSec = (DateTime.UtcNow - _lastMouseMoveUtc).TotalSeconds;
            if (idleSec >= 2.5 && !_controlsHiddenByInactivity)
            {
                SetControlsVisibleAnimated(false);
            }
        }

        private void SetControlsVisibleAnimated(bool visible)
        {
            _controlsHiddenByInactivity = !visible;
            if (_controlsContainerGrid == null) return;

            var anim = new DoubleAnimation
            {
                To = visible ? 1.0 : 0.0,
                Duration = TimeSpan.FromMilliseconds(visible ? 180 : 320),
                EasingFunction = new QuadraticEase { EasingMode = EasingMode.EaseOut }
            };
            _controlsContainerGrid.BeginAnimation(UIElement.OpacityProperty, anim);
            _controlsContainerGrid.IsHitTestVisible = visible;

            if (_rootOverlayGrid != null)
            {
                _rootOverlayGrid.Cursor = visible ? Cursors.Arrow : Cursors.None;
            }
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
                string timeLabel = YouTubeService.FormatSeconds(targetSec);
                string segName = GetHoveredSponsorCategoryLabel(targetSec);
                _scrubberTooltipText.Text = !string.IsNullOrEmpty(segName)
                    ? string.Format("{0} • {1}", timeLabel, segName)
                    : timeLabel;

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

        public void SetSponsorSegments(List<SponsorSegment> segments)
        {
            _sponsorSegments = segments ?? new List<SponsorSegment>();
            RenderSponsorSegmentsOnCanvas();
        }

        private void RenderSponsorSegmentsOnCanvas()
        {
            if (_sponsorSegmentsCanvas == null || _scrubberHitArea == null) return;
            _sponsorSegmentsCanvas.Children.Clear();

            if (_totalSeconds <= 0 || _sponsorSegments == null || _sponsorSegments.Count == 0)
                return;

            double trackW = _scrubberHitArea.ActualWidth;
            if (trackW <= 2) return;

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

                System.Drawing.Color c = seg.CategoryColor;
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

        private void RunOnWpfDispatcher(Action action)
        {
            if (action == null) return;
            var disp = _rootContent?.Dispatcher ?? _overlayWindow?.Dispatcher ?? System.Windows.Application.Current?.Dispatcher;
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
            _isPlaying = isPlaying;
            _totalSeconds = Math.Max(0.0, totalSec);

            // O timer do player apenas atualiza o valor visual se o usuário NÃO estiver arrastando a barra
            if (!_isUserDraggingScrubber)
            {
                _currentSeconds = Math.Max(0.0, currentSec);
            }
            _bufferedSeconds = Math.Max(_currentSeconds, bufferedSec);

            if (!isPlaying && _controlsHiddenByInactivity)
            {
                SetControlsVisibleAnimated(true);
            }

            if (_iconPlay != null && _iconPause != null)
            {
                _iconPlay.Visibility = isPlaying ? Visibility.Collapsed : Visibility.Visible;
                _iconPause.Visibility = isPlaying ? Visibility.Visible : Visibility.Collapsed;
            }

            RefreshTimelineVisuals();
        }

        private void RefreshTimelineVisuals()
        {
            if (_scrubberHitArea == null) return;

            double displayCur = _isUserDraggingScrubber ? _dragScrubTargetSeconds : _currentSeconds;
            if (_txtTimeDisplay != null)
            {
                _txtTimeDisplay.Text = string.Format("{0} / {1}",
                    YouTubeService.FormatSeconds(displayCur),
                    YouTubeService.FormatSeconds(_totalSeconds));
            }

            double trackW = _scrubberHitArea.ActualWidth;
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

        public void UpdateQualityAndSpeedVisuals(int qualityHeight, double speedRatio)
        {
            _activeQualityHeight = qualityHeight;
            _activePlaybackSpeed = speedRatio;

            if (_txtQualityBadge != null)
            {
                if (qualityHeight >= 2160)
                    _txtQualityBadge.Text = "4K";
                else if (qualityHeight >= 720)
                    _txtQualityBadge.Text = qualityHeight + "p HD";
                else
                    _txtQualityBadge.Text = qualityHeight + "p";
            }

            StyleGearButton(_btnGearQ144, qualityHeight <= 144);
            StyleGearButton(_btnGearQ240, qualityHeight == 240);
            StyleGearButton(_btnGearQ360, qualityHeight == 360);
            StyleGearButton(_btnGearQ480, qualityHeight == 480);
            StyleGearButton(_btnGearQ720, qualityHeight == 720);
            StyleGearButton(_btnGearQ1080, qualityHeight == 1080);
            StyleGearButton(_btnGearQ1440, qualityHeight == 1440);
            StyleGearButton(_btnGearQ2160, qualityHeight >= 2160);

            int spd = (int)Math.Round(speedRatio * 100);
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
                ? new SolidColorBrush(Color.FromRgb(255, 0, 0))
                : new SolidColorBrush(Color.FromRgb(40, 40, 40));
        }

        private void ToggleGearPopup()
        {
            if (_gearPopupPanel == null) return;
            _gearPopupPanel.Visibility = (_gearPopupPanel.Visibility == Visibility.Visible)
                ? Visibility.Collapsed
                : Visibility.Visible;
        }

        private void SelectQualityFromGear(int q)
        {
            if (_gearPopupPanel != null) _gearPopupPanel.Visibility = Visibility.Collapsed;
            UpdateQualityAndSpeedVisuals(q, _activePlaybackSpeed);
            if (QualityChanged != null) QualityChanged(q);
        }

        private void SelectSpeedFromGear(double spd)
        {
            if (_gearPopupPanel != null) _gearPopupPanel.Visibility = Visibility.Collapsed;
            UpdateQualityAndSpeedVisuals(_activeQualityHeight, spd);
            if (SpeedChanged != null) SpeedChanged(spd);
        }

        private void OnOverlayMouseWheel(object sender, MouseWheelEventArgs e)
        {
            RegisterUserActivity();
            double delta = e.Delta > 0 ? 0.05 : -0.05;
            double nextVol = Math.Max(0.0, Math.Min(1.0, Math.Round(_volume0To1 + delta, 2)));
            if (VolumeChanged != null) VolumeChanged(nextVol);
            e.Handled = true;
        }

        private void OnOverlayPreviewKeyDown(object sender, KeyEventArgs e)
        {
            RegisterUserActivity();
            System.Windows.Forms.Keys wfKey = (System.Windows.Forms.Keys)KeyInterop.VirtualKeyFromKey(e.Key);
            if (ShortcutKeyPressed != null)
            {
                ShortcutKeyPressed(wfKey);
                e.Handled = true;
            }
        }

        public void Dispose()
        {
            if (_isDisposed) return;
            _isDisposed = true;

            try { _inactivityTimer.Stop(); } catch { }
            try { _sponsorBannerTimer.Stop(); } catch { }

            if (_overlayWindow != null)
            {
                try { _overlayWindow.Close(); } catch { }
                _overlayWindow = null;
            }
        }
    }
}
