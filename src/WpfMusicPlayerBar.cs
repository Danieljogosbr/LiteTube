using System;
using System.Collections.Generic;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Markup;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Media.Imaging;
using System.Windows.Shapes;

namespace LiteTube
{
    /// <summary>
    /// Barra inferior do player de música em WPF com skin fiel ao YouTube Music:
    /// - Scrubber de borda superior (2px -> 4px no hover) em #FF0000 e segmentos music_offtopic em azul/cinza (#3EA6FF).
    /// - Esquerda: Miniatura 48x48 arredondada, Título e Artista com CharacterEllipsis, botão Curtir.
    /// - Centro: Shuffle, Anterior, Play/Pause circular branco, Próxima e Repetir (Desligado/Todas/Uma).
    /// - Direita: Tempo decorrido/total, controle de som com slider expansível linear 1:1 e botão de Fila.
    /// </summary>
    public class WpfMusicPlayerBar : UserControl
    {
        private FrameworkElement _rootContent;

        // Scrubber elements
        private Grid _scrubberHitArea;
        private Border _scrubberBgTrack;
        private Canvas _offtopicSegmentsCanvas;
        private Border _scrubberPlayedBar;
        private Ellipse _scrubberThumb;

        // Left metadata elements
        private Rectangle _thumbImageRect;
        private ImageBrush _thumbImageBrush;
        private TextBlock _txtTrackTitle;
        private TextBlock _txtTrackArtist;
        private Border _btnLikeTrack;
        private System.Windows.Shapes.Path _iconHeart;

        // Center transport elements
        private Border _btnShuffle;
        private System.Windows.Shapes.Path _iconShuffle;
        private Border _btnPrevTrack;
        private Border _btnPlayPauseCircle;
        private System.Windows.Shapes.Path _iconMusicPlay;
        private System.Windows.Shapes.Path _iconMusicPause;
        private Border _btnNextTrack;
        private Border _btnRepeat;
        private System.Windows.Shapes.Path _iconRepeat;
        private Border _badgeRepeatOne;

        // Right actions & volume elements
        private TextBlock _txtMusicTime;
        private StackPanel _musicVolumeHoverGroup;
        private Border _musicVolumeExpandContainer;
        private Grid _musicVolumeHitGrid;
        private Border _musicVolumeFillBar;
        private Ellipse _musicVolumeThumb;
        private Border _btnMusicMute;
        private System.Windows.Shapes.Path _iconMusicSpeakerActive;
        private System.Windows.Shapes.Path _iconMusicSpeakerMuted;
        private Border _btnQueueToggle;

        // State
        private bool _isPlaying = false;
        private bool _isHoveringScrubber = false;
        private bool _isUserDraggingScrubber = false;
        private bool _isDraggingVolume = false;
        private double _currentSeconds = 0.0;
        private double _totalSeconds = 0.0;
        private double _dragScrubTargetSeconds = 0.0;
        private double _volume0To1 = 0.12;
        private bool _isMuted = false;
        private List<SponsorSegment> _offtopicSegments = new List<SponsorSegment>();

        // Events
        public event Action PlayPauseRequested;
        public event Action NextRequested;
        public event Action PreviousRequested;
        public event Action ShuffleToggleRequested;
        public event Action RepeatToggleRequested;
        public event Action LikeToggleRequested;
        public event Action QueueToggleRequested;
        public event Action MuteToggleRequested;
        public event Action<double> VolumeChanged;
        public event Action<double> SeekRequested;

        public bool IsUserDraggingScrubber { get { return _isUserDraggingScrubber; } }

        public WpfMusicPlayerBar()
        {
            LoadXamlTemplate();
            WireEvents();
        }

        private void LoadXamlTemplate()
        {
            string baseDir = AppDomain.CurrentDomain.BaseDirectory;
            string p1 = System.IO.Path.Combine(baseDir, "src", "WpfMusicPlayerBar.xaml");
            string p2 = System.IO.Path.Combine(baseDir, "WpfMusicPlayerBar.xaml");
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
                _rootContent = new Grid { Background = new SolidColorBrush(Color.FromRgb(24, 24, 24)) };
            }

            Content = _rootContent;

            _scrubberHitArea = FindElement<Grid>("MusicScrubberHitArea");
            _scrubberBgTrack = FindElement<Border>("MusicScrubberBgTrack");
            _offtopicSegmentsCanvas = FindElement<Canvas>("MusicOfftopicSegmentsCanvas");
            _scrubberPlayedBar = FindElement<Border>("MusicScrubberPlayedBar");
            _scrubberThumb = FindElement<Ellipse>("MusicScrubberThumb");

            _thumbImageRect = FindElement<Rectangle>("ThumbImageRect");
            _thumbImageBrush = FindElement<ImageBrush>("ThumbImageBrush");
            _txtTrackTitle = FindElement<TextBlock>("TxtTrackTitle");
            _txtTrackArtist = FindElement<TextBlock>("TxtTrackArtist");
            _btnLikeTrack = FindElement<Border>("BtnLikeTrack");
            _iconHeart = FindElement<System.Windows.Shapes.Path>("IconHeart");

            _btnShuffle = FindElement<Border>("BtnShuffle");
            _iconShuffle = FindElement<System.Windows.Shapes.Path>("IconShuffle");
            _btnPrevTrack = FindElement<Border>("BtnPrevTrack");
            _btnPlayPauseCircle = FindElement<Border>("BtnPlayPauseCircle");
            _iconMusicPlay = FindElement<System.Windows.Shapes.Path>("IconMusicPlay");
            _iconMusicPause = FindElement<System.Windows.Shapes.Path>("IconMusicPause");
            _btnNextTrack = FindElement<Border>("BtnNextTrack");
            _btnRepeat = FindElement<Border>("BtnRepeat");
            _iconRepeat = FindElement<System.Windows.Shapes.Path>("IconRepeat");
            _badgeRepeatOne = FindElement<Border>("BadgeRepeatOne");

            _txtMusicTime = FindElement<TextBlock>("TxtMusicTime");
            _musicVolumeHoverGroup = FindElement<StackPanel>("MusicVolumeHoverGroup");
            _musicVolumeExpandContainer = FindElement<Border>("MusicVolumeExpandContainer");
            _musicVolumeHitGrid = FindElement<Grid>("MusicVolumeHitGrid");
            _musicVolumeFillBar = FindElement<Border>("MusicVolumeFillBar");
            _musicVolumeThumb = FindElement<Ellipse>("MusicVolumeThumb");
            _btnMusicMute = FindElement<Border>("BtnMusicMute");
            _iconMusicSpeakerActive = FindElement<System.Windows.Shapes.Path>("IconMusicSpeakerActive");
            _iconMusicSpeakerMuted = FindElement<System.Windows.Shapes.Path>("IconMusicSpeakerMuted");
            _btnQueueToggle = FindElement<Border>("BtnQueueToggle");
        }

        private T FindElement<T>(string name) where T : class
        {
            if (_rootContent == null) return null;
            return _rootContent.FindName(name) as T;
        }

        private void WireEvents()
        {
            SizeChanged += (s, e) => RefreshTimelineVisuals();
            MouseWheel += OnBarMouseWheel;

            // Top-Edge Scrubber (2px -> 4px on hover, Seek emitted only on PreviewMouseUp)
            if (_scrubberHitArea != null)
            {
                _scrubberHitArea.MouseEnter += (s, e) =>
                {
                    _isHoveringScrubber = true;
                    SetScrubberExpanded(true);
                };

                _scrubberHitArea.MouseLeave += (s, e) =>
                {
                    _isHoveringScrubber = false;
                    if (!_isUserDraggingScrubber)
                    {
                        SetScrubberExpanded(false);
                    }
                };

                _scrubberHitArea.PreviewMouseLeftButtonDown += (s, e) =>
                {
                    _isUserDraggingScrubber = true;
                    _scrubberHitArea.CaptureMouse();
                    UpdateScrubberDragPosition(e.GetPosition(_scrubberHitArea).X);
                    e.Handled = true;
                };

                _scrubberHitArea.PreviewMouseMove += (s, e) =>
                {
                    if (_isUserDraggingScrubber)
                    {
                        UpdateScrubberDragPosition(e.GetPosition(_scrubberHitArea).X);
                    }
                };

                _scrubberHitArea.PreviewMouseUp += (s, e) =>
                {
                    if (_isUserDraggingScrubber)
                    {
                        _isUserDraggingScrubber = false;
                        _scrubberHitArea.ReleaseMouseCapture();
                        _currentSeconds = _dragScrubTargetSeconds;
                        if (!_isHoveringScrubber)
                        {
                            SetScrubberExpanded(false);
                        }
                        if (SeekRequested != null) SeekRequested(_dragScrubTargetSeconds);
                        e.Handled = true;
                    }
                };
            }

            if (_btnLikeTrack != null)
            {
                _btnLikeTrack.MouseLeftButtonUp += (s, e) =>
                {
                    if (LikeToggleRequested != null) LikeToggleRequested();
                    e.Handled = true;
                };
            }

            if (_btnShuffle != null)
            {
                _btnShuffle.MouseLeftButtonUp += (s, e) =>
                {
                    if (ShuffleToggleRequested != null) ShuffleToggleRequested();
                    e.Handled = true;
                };
            }

            if (_btnPrevTrack != null)
            {
                _btnPrevTrack.MouseLeftButtonUp += (s, e) =>
                {
                    if (PreviousRequested != null) PreviousRequested();
                    e.Handled = true;
                };
            }

            if (_btnPlayPauseCircle != null)
            {
                _btnPlayPauseCircle.MouseLeftButtonUp += (s, e) =>
                {
                    if (PlayPauseRequested != null) PlayPauseRequested();
                    e.Handled = true;
                };
            }

            if (_btnNextTrack != null)
            {
                _btnNextTrack.MouseLeftButtonUp += (s, e) =>
                {
                    if (NextRequested != null) NextRequested();
                    e.Handled = true;
                };
            }

            if (_btnRepeat != null)
            {
                _btnRepeat.MouseLeftButtonUp += (s, e) =>
                {
                    if (RepeatToggleRequested != null) RepeatToggleRequested();
                    e.Handled = true;
                };
            }

            if (_musicVolumeHoverGroup != null)
            {
                _musicVolumeHoverGroup.MouseEnter += (s, e) => AnimateVolumeExpand(true);
                _musicVolumeHoverGroup.MouseLeave += (s, e) =>
                {
                    if (!_isDraggingVolume) AnimateVolumeExpand(true); // Keeps comfortable slider width on YT Music bar
                };
            }

            if (_btnMusicMute != null)
            {
                _btnMusicMute.MouseLeftButtonUp += (s, e) =>
                {
                    if (MuteToggleRequested != null) MuteToggleRequested();
                    e.Handled = true;
                };
            }

            if (_musicVolumeHitGrid != null)
            {
                _musicVolumeHitGrid.PreviewMouseLeftButtonDown += (s, e) =>
                {
                    _isDraggingVolume = true;
                    _musicVolumeHitGrid.CaptureMouse();
                    SetMusicVolumeFromMouseX(e.GetPosition(_musicVolumeHitGrid).X);
                    e.Handled = true;
                };

                _musicVolumeHitGrid.PreviewMouseMove += (s, e) =>
                {
                    if (_isDraggingVolume)
                    {
                        SetMusicVolumeFromMouseX(e.GetPosition(_musicVolumeHitGrid).X);
                    }
                };

                _musicVolumeHitGrid.PreviewMouseUp += (s, e) =>
                {
                    if (_isDraggingVolume)
                    {
                        _isDraggingVolume = false;
                        _musicVolumeHitGrid.ReleaseMouseCapture();
                        e.Handled = true;
                    }
                };
            }

            if (_btnQueueToggle != null)
            {
                _btnQueueToggle.MouseLeftButtonUp += (s, e) =>
                {
                    if (QueueToggleRequested != null) QueueToggleRequested();
                    e.Handled = true;
                };
            }
        }

        private void SetScrubberExpanded(bool expanded)
        {
            double h = expanded ? 4.0 : 2.0;
            if (_scrubberBgTrack != null) _scrubberBgTrack.Height = h;
            if (_offtopicSegmentsCanvas != null) _offtopicSegmentsCanvas.Height = h;
            if (_scrubberPlayedBar != null) _scrubberPlayedBar.Height = h;
            if (_scrubberThumb != null)
            {
                _scrubberThumb.Visibility = expanded ? Visibility.Visible : Visibility.Collapsed;
            }
            RenderOfftopicSegments();
        }

        private void AnimateVolumeExpand(bool expand)
        {
            if (_musicVolumeExpandContainer == null) return;
            var anim = new DoubleAnimation
            {
                To = expand ? 76.0 : 0.0,
                Duration = TimeSpan.FromMilliseconds(150),
                EasingFunction = new QuadraticEase { EasingMode = EasingMode.EaseOut }
            };
            _musicVolumeExpandContainer.BeginAnimation(FrameworkElement.WidthProperty, anim);
        }

        private void UpdateScrubberDragPosition(double mouseX)
        {
            if (_scrubberHitArea == null) return;
            double w = Math.Max(1.0, _scrubberHitArea.ActualWidth);
            double clamped = Math.Max(0.0, Math.Min(w, mouseX));
            double ratio = clamped / w;
            _dragScrubTargetSeconds = _totalSeconds > 0 ? ratio * _totalSeconds : 0.0;
            RefreshTimelineVisuals();
        }

        private void SetMusicVolumeFromMouseX(double mouseX)
        {
            if (_musicVolumeHitGrid == null) return;
            double w = Math.Max(10.0, _musicVolumeHitGrid.ActualWidth > 0 ? _musicVolumeHitGrid.ActualWidth : 64.0);
            double ratio = Math.Max(0.0, Math.Min(1.0, mouseX / w));
            _volume0To1 = Math.Round(ratio, 2);
            _isMuted = (_volume0To1 <= 0.001);
            UpdateAudioVisuals(_volume0To1, _isMuted);
            if (VolumeChanged != null) VolumeChanged(_volume0To1);
        }

        private void OnBarMouseWheel(object sender, MouseWheelEventArgs e)
        {
            double delta = e.Delta > 0 ? 0.02 : -0.02;
            double next = Math.Max(0.0, Math.Min(1.0, Math.Round(_volume0To1 + delta, 2)));
            if (VolumeChanged != null) VolumeChanged(next);
            e.Handled = true;
        }

        public void SetOfftopicSegments(List<SponsorSegment> segments)
        {
            _offtopicSegments = segments ?? new List<SponsorSegment>();
            RenderOfftopicSegments();
        }

        private void RenderOfftopicSegments()
        {
            if (_offtopicSegmentsCanvas == null || _scrubberHitArea == null) return;
            _offtopicSegmentsCanvas.Children.Clear();

            if (_totalSeconds <= 0 || _offtopicSegments == null || _offtopicSegments.Count == 0)
                return;

            double w = _scrubberHitArea.ActualWidth;
            if (w <= 2.0) return;
            double h = _offtopicSegmentsCanvas.Height > 0 ? _offtopicSegmentsCanvas.Height : 2.0;

            for (int i = 0; i < _offtopicSegments.Count; i++)
            {
                var seg = _offtopicSegments[i];
                if (seg == null || seg.End <= seg.Start || seg.Start >= _totalSeconds) continue;

                double sRatio = Math.Max(0.0, Math.Min(1.0, seg.Start / _totalSeconds));
                double eRatio = Math.Max(0.0, Math.Min(1.0, Math.Min(_totalSeconds, seg.End) / _totalSeconds));
                double sx = sRatio * w;
                double segW = Math.Max(3.0, (eRatio - sRatio) * w);

                var rect = new Rectangle
                {
                    Width = segW,
                    Height = h,
                    Fill = new SolidColorBrush(Color.FromArgb(220, 62, 166, 255)), // Azul/cinza #3EA6FF
                    IsHitTestVisible = false
                };
                Canvas.SetLeft(rect, sx);
                Canvas.SetTop(rect, 0);
                _offtopicSegmentsCanvas.Children.Add(rect);
            }
        }

        public void UpdateTrackMetadata(TrackItem track, bool isLiked)
        {
            if (track == null)
            {
                if (_txtTrackTitle != null) _txtTrackTitle.Text = "Nenhuma música tocando";
                if (_txtTrackArtist != null) _txtTrackArtist.Text = "Escolha uma faixa no catálogo";
                if (_thumbImageRect != null) _thumbImageRect.Visibility = Visibility.Collapsed;
                UpdateFavoriteVisual(false);
                return;
            }

            if (_txtTrackTitle != null) _txtTrackTitle.Text = track.Title ?? "Faixa sem título";
            if (_txtTrackArtist != null) _txtTrackArtist.Text = track.Artist ?? "YouTube Music";
            UpdateFavoriteVisual(isLiked);

            if (!string.IsNullOrEmpty(track.ThumbnailUrl))
            {
                UpdateThumbnailFromUrl(track.ThumbnailUrl);
            }
        }

        public async void UpdateThumbnailFromUrl(string url)
        {
            if (_thumbImageRect == null || _thumbImageBrush == null) return;
            if (string.IsNullOrWhiteSpace(url))
            {
                _thumbImageRect.Visibility = Visibility.Collapsed;
                return;
            }

            try
            {
                var loader = AsyncImageLoader.ImageLoaderBase.DefaultLoader;
                if (loader != null)
                {
                    var bmp = await loader.ProvideImageAsync(url).ConfigureAwait(false);
                    if (bmp != null)
                    {
                        var op = Dispatcher.BeginInvoke(new Action(() =>
                        {
                            _thumbImageBrush.ImageSource = bmp;
                            _thumbImageRect.Visibility = Visibility.Visible;
                        }));
                    }
                }
            }
            catch { }
        }

        public void UpdateThumbnailFromGdiImage(System.Drawing.Image gdiImg)
        {
            if (_thumbImageRect == null || _thumbImageBrush == null) return;
            if (gdiImg == null)
            {
                _thumbImageRect.Visibility = Visibility.Collapsed;
                return;
            }

            try
            {
                using (var ms = new MemoryStream())
                {
                    gdiImg.Save(ms, System.Drawing.Imaging.ImageFormat.Png);
                    ms.Position = 0;
                    var bmp = new BitmapImage();
                    bmp.BeginInit();
                    bmp.CacheOption = BitmapCacheOption.OnLoad;
                    bmp.StreamSource = ms;
                    bmp.EndInit();
                    bmp.Freeze();
                    _thumbImageBrush.ImageSource = bmp;
                    _thumbImageRect.Visibility = Visibility.Visible;
                }
            }
            catch
            {
                _thumbImageRect.Visibility = Visibility.Collapsed;
            }
        }

        public void UpdateFavoriteVisual(bool isFavorite)
        {
            if (_iconHeart == null) return;
            _iconHeart.Fill = isFavorite
                ? new SolidColorBrush(Color.FromRgb(255, 0, 0))
                : new SolidColorBrush(Color.FromRgb(170, 170, 170));
        }

        public void UpdateShuffleAndRepeatVisuals(bool shuffle, int repeatMode)
        {
            if (_iconShuffle != null)
            {
                _iconShuffle.Fill = shuffle
                    ? new SolidColorBrush(Color.FromRgb(255, 255, 255))
                    : new SolidColorBrush(Color.FromRgb(140, 140, 140));
            }

            if (_iconRepeat != null && _badgeRepeatOne != null)
            {
                bool active = repeatMode > 0;
                _iconRepeat.Fill = active
                    ? new SolidColorBrush(Color.FromRgb(255, 255, 255))
                    : new SolidColorBrush(Color.FromRgb(140, 140, 140));
                _badgeRepeatOne.Visibility = (repeatMode == 2) ? Visibility.Visible : Visibility.Collapsed;
            }
        }

        public void UpdatePlaybackState(bool isPlaying, double currentSec, double totalSec)
        {
            _isPlaying = isPlaying;
            _totalSeconds = Math.Max(0.0, totalSec);

            if (!_isUserDraggingScrubber)
            {
                _currentSeconds = Math.Max(0.0, currentSec);
            }

            if (_iconMusicPlay != null && _iconMusicPause != null)
            {
                _iconMusicPlay.Visibility = isPlaying ? Visibility.Collapsed : Visibility.Visible;
                _iconMusicPause.Visibility = isPlaying ? Visibility.Visible : Visibility.Collapsed;
            }

            RefreshTimelineVisuals();
        }

        private void RefreshTimelineVisuals()
        {
            double displayCur = _isUserDraggingScrubber ? _dragScrubTargetSeconds : _currentSeconds;
            if (_txtMusicTime != null)
            {
                _txtMusicTime.Text = string.Format("{0} / {1}",
                    YouTubeService.FormatSeconds(displayCur),
                    YouTubeService.FormatSeconds(_totalSeconds));
            }

            if (_scrubberHitArea == null) return;
            double w = _scrubberHitArea.ActualWidth;
            if (w <= 1.0 || _totalSeconds <= 0.0)
            {
                if (_scrubberPlayedBar != null) _scrubberPlayedBar.Width = 0;
                if (_scrubberThumb != null) Canvas.SetLeft(_scrubberThumb, -6.0);
                return;
            }

            double ratio = Math.Max(0.0, Math.Min(1.0, displayCur / _totalSeconds));
            double px = ratio * w;

            if (_scrubberPlayedBar != null) _scrubberPlayedBar.Width = px;
            if (_scrubberThumb != null)
            {
                Canvas.SetLeft(_scrubberThumb, Math.Max(-6.0, Math.Min(w - 6.0, px - 6.0)));
            }

            RenderOfftopicSegments();
        }

        public void UpdateAudioVisuals(double volume0To1, bool isMuted)
        {
            _volume0To1 = Math.Max(0.0, Math.Min(1.0, volume0To1));
            _isMuted = isMuted;

            bool effectiveMute = isMuted || _volume0To1 <= 0.005;
            if (_iconMusicSpeakerActive != null && _iconMusicSpeakerMuted != null)
            {
                _iconMusicSpeakerActive.Visibility = effectiveMute ? Visibility.Collapsed : Visibility.Visible;
                _iconMusicSpeakerMuted.Visibility = effectiveMute ? Visibility.Visible : Visibility.Collapsed;
            }

            double maxW = 64.0;
            double fillW = effectiveMute ? 0.0 : (_volume0To1 * maxW);
            if (_musicVolumeFillBar != null)
            {
                _musicVolumeFillBar.Width = fillW;
            }
            if (_musicVolumeThumb != null)
            {
                Canvas.SetLeft(_musicVolumeThumb, Math.Max(0.0, Math.Min(maxW - 11.0, fillW - 5.5)));
            }
        }
    }
}
