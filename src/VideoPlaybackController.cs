#nullable enable
using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Threading;
using FlyleafLib;
using FlyleafLib.MediaPlayer;

namespace LiteTube
{
    /// <summary>
    /// Camada 2 — Engine de Mídia / Player de Vídeo.
    /// Encapsula o motor FlyleafLib (Direct3D 11 / FFmpeg), resolução de streams (ProgressiveVideoSession),
    /// troca contínua de qualidade, SponsorBlock, Storyboard, WatchHistory (auto-resume) e pré-carregamento do próximo vídeo.
    /// 100% desacoplado de qualquer controle visual WinForms ou WPF.
    /// </summary>
    public sealed class VideoPlaybackController : IDisposable
    {
        private readonly AppConfig _config;
        private readonly AudioEngine _musicEngine;
        private readonly Dispatcher _uiDispatcher;
        private readonly SemaphoreSlim _playerInitLock = new SemaphoreSlim(1, 1);
        private readonly DispatcherTimer _playbackTimer;

        private Config? _playerConfig;
        private Player? _player;
        private ProgressiveVideoSession? _activeSession;
        private CancellationTokenSource? _activePlayResolveCts;
        private long _playRequestSequence = 0;

        private TrackItem? _currentVideo;
        private bool _isPlaying = false;
        private bool _isLoading = false;
        private bool _isSwitchingQuality = false;
        private bool _isLiveStream = false;
        private int _actualPlayingQualityHeight = 0;
        private string _activeCodecLabel = "Direct3D 11";
        private DateTime _lastHistorySavedUtc = DateTime.MinValue;
        private bool _isDisposed = false;

        // SponsorBlock
        private List<SponsorSegment> _currentSponsorSegments = new List<SponsorSegment>();
        private string? _lastSkippedSponsorUuid;

        // Up-Next Queue & Background Next-Video Prefetch (Zero-Latency Handoff)
        private readonly List<TrackItem> _upNextQueue = new List<TrackItem>();
        private string? _prefetchedVideoId;
        private int _prefetchedQualityHeight;
        private (string VideoUrl, string AudioUrl, string Codec)? _prefetchedStreams;
        private bool _isPrefetching = false;
        private bool _prefetchTriggeredForCurrent = false;

        public Func<TrackItem?>? NextVideoFallbackProvider { get; set; }

        public Player? Player => _player;
        public TrackItem? CurrentVideo => _currentVideo;
        public bool IsPlaying => _isPlaying || (_player != null && _player.IsPlaying);
        public bool IsLoading => _isLoading;
        public bool IsLiveStream => _isLiveStream;
        public int ActualQualityHeight => _actualPlayingQualityHeight > 0 ? _actualPlayingQualityHeight : _config.VideoQualityHeight;
        public string ActiveCodecLabel => _activeCodecLabel;
        public List<TrackItem> UpNextQueue => _upNextQueue;

        public bool HasLowLatencyDemuxerTuning =>
            _playerConfig?.Demuxer?.FormatOpt != null &&
            _playerConfig.Demuxer.FormatOpt.ContainsKey("probesize") &&
            _playerConfig.Demuxer.FormatOpt.ContainsKey("analyzeduration");

        public event Action<Player>? PlayerCreated;
        public event Action<TrackItem>? VideoStarted;
        public event Action<bool, string, string>? LoadingStateChanged; // (isLoading, title, progressText)
        public event Action<bool, double, double, double>? PlaybackProgressUpdated; // (isPlaying, curSec, bufSec, totalSec)
        public event Action<int, string>? MediaMetricsUpdated; // (actualQualityHeight, codecLabel)
        public event Action<double, bool>? AudioSettingsUpdated; // (vol0To1, isMuted)
        public event Action<bool>? LiveStreamModeChanged;
        public event Action<StoryboardData?>? StoryboardLoaded;
        public event Action<List<SponsorSegment>>? SponsorSegmentsLoaded;
        public event Action<string, double>? SponsorSkipped; // (label, skipToSeconds)
        public event Action<TrackItem>? VideoEndedWithNextCandidate;
        public event Action? VideoEnded;
        public event Action<string>? StatusNotification;
        public event Action<string>? ToastRequested;
        public event Action<string, bool>? PlaybackError; // (errorMessage, isUnavailable)

        public VideoPlaybackController(AppConfig config, AudioEngine musicEngine, Dispatcher uiDispatcher)
        {
            _config = config;
            _musicEngine = musicEngine;
            _uiDispatcher = uiDispatcher;

            _playbackTimer = new DispatcherTimer(DispatcherPriority.Normal, _uiDispatcher)
            {
                Interval = TimeSpan.FromMilliseconds(250)
            };
            _playbackTimer.Tick += OnPlaybackTimerTick;
            _playbackTimer.Start();
        }

        public async Task EnsurePlayerInitializedAsync()
        {
            if (_player != null || _isDisposed) return;

            await _playerInitLock.WaitAsync().ConfigureAwait(false);
            try
            {
                if (_player != null || _isDisposed) return;

                if (!FlyleafLib.Engine.IsLoaded)
                {
                    await Program.EngineReadyTask.ConfigureAwait(false);
                }

                await _uiDispatcher.InvokeAsync(() =>
                {
                    if (_player != null || _isDisposed) return;
                    InitializeFlyleafPlayerCore();
                });
            }
            finally
            {
                _playerInitLock.Release();
            }
        }

        private void InitializeFlyleafPlayerCore()
        {
            try
            {
                _playerConfig = new Config();
                _playerConfig.Video.VideoAcceleration = true;
                _playerConfig.Player.AutoPlay = true;
                _playerConfig.Player.SeekAccurate = true;
                _playerConfig.Player.MinBufferDuration = TimeSpan.FromMilliseconds(600).Ticks;

                string ua = HttpPipeline.DefaultUserAgent;
                _playerConfig.Demuxer.FormatOpt["user_agent"] = ua;
                _playerConfig.Demuxer.FormatOpt["headers"] = "User-Agent: " + ua + "\r\n";
                _playerConfig.Demuxer.FormatOpt["probesize"] = "524288";
                _playerConfig.Demuxer.FormatOpt["analyzeduration"] = "500000";
                _playerConfig.Demuxer.FormatOpt["reconnect"] = "1";
                _playerConfig.Demuxer.FormatOpt["reconnect_streamed"] = "1";
                _playerConfig.Demuxer.FormatOpt["reconnect_delay_max"] = "5";

                _playerConfig.Demuxer.AudioFormatOpt["user_agent"] = ua;
                _playerConfig.Demuxer.AudioFormatOpt["headers"] = "User-Agent: " + ua + "\r\n";
                _playerConfig.Demuxer.AudioFormatOpt["probesize"] = "524288";
                _playerConfig.Demuxer.AudioFormatOpt["analyzeduration"] = "500000";
                _playerConfig.Demuxer.AudioFormatOpt["reconnect"] = "1";
                _playerConfig.Demuxer.AudioFormatOpt["reconnect_streamed"] = "1";
                _playerConfig.Demuxer.AudioFormatOpt["reconnect_delay_max"] = "5";

                _player = new Player(_playerConfig);
                _player.PlaybackStopped += OnPlayerPlaybackStopped;
                _player.OpenCompleted += OnPlayerOpenCompleted;

                ApplyAudioSettings();
                PlayerCreated?.Invoke(_player);
            }
            catch (Exception ex)
            {
                Logger.Error("FLYLEAF", "Erro ao inicializar Player FlyleafLib: " + ex.Message, ex);
            }
        }

        public async Task PlayVideoAsync(TrackItem video, double overrideStartSeconds = 0.0)
        {
            if (video == null || string.IsNullOrEmpty(video.Id) || _isDisposed) return;

            if (string.IsNullOrWhiteSpace(video.CustomThumbUrl))
            {
                video.CustomThumbUrl = "https://i.ytimg.com/vi/" + video.Id + "/hqdefault.jpg";
            }

            int targetQuality = _config.VideoQualityHeight;
            Logger.Info("WATCH", string.Format("PlayVideo chamado: Id={0}, Title='{1}', Dur={2:0.0}s, Res={3}p",
                video.Id, video.Title, video.DurationSeconds, targetQuality));

            bool hasCompletedPrefetch = string.Equals(_prefetchedVideoId, video.Id, StringComparison.OrdinalIgnoreCase) &&
                                        _prefetchedQualityHeight == targetQuality &&
                                        _prefetchedStreams != null &&
                                        !string.IsNullOrEmpty(_prefetchedStreams.Value.VideoUrl);

            SaveCurrentPlaybackProgress(false);
            StopActiveSession();

            long requestId = ++_playRequestSequence;

            // 1. Inicia inicialização do Flyleaf e resolução de stream IMEDIATAMENTE em paralelo
            Task ensurePlayerTask = EnsurePlayerInitializedAsync();

            string? vUrl = null;
            string? aUrl = null;
            string codec = "Direct";
            bool isLiveStream = false;

            ProgressiveVideoSession? foregroundSession = null;
            Task<string[]>? foregroundResolveTask = null;
            CancellationTokenSource? playCts = null;

            if (hasCompletedPrefetch && _prefetchedStreams != null)
            {
                var prefetched = _prefetchedStreams.Value;
                vUrl = prefetched.VideoUrl;
                aUrl = prefetched.AudioUrl;
                codec = !string.IsNullOrEmpty(prefetched.Codec) ? prefetched.Codec : "Direct";
                isLiveStream = vUrl.IndexOf(".m3u8", StringComparison.OrdinalIgnoreCase) >= 0 ||
                               vUrl.IndexOf("manifest.googlevideo.com", StringComparison.OrdinalIgnoreCase) >= 0;

                _prefetchedVideoId = null;
                _prefetchedQualityHeight = 0;
                _prefetchedStreams = null;
            }
            else
            {
                _prefetchedVideoId = null;
                _prefetchedQualityHeight = 0;
                _prefetchedStreams = null;

                try { _player?.Stop(); } catch { }

                playCts = new CancellationTokenSource(TimeSpan.FromSeconds(30));
                _activePlayResolveCts = playCts;

                string binDir = YtDlpResolver.GetYtDlpDirectory();
                foregroundSession = new ProgressiveVideoSession(video, targetQuality, binDir, _config.BrowserForCookies, _config.CookiesFilePath);
                _activeSession = foregroundSession;
                foregroundResolveTask = foregroundSession.ResolveStreamUrlsAsync(playCts.Token);
            }

            if (_config.AutoPauseMusicWhenVideoPlays && _musicEngine != null && _musicEngine.IsPlaying)
            {
                _musicEngine.PauseIfPlaying();
            }

            long resumeSeconds = overrideStartSeconds > 0.5
                ? (long)Math.Floor(overrideStartSeconds)
                : WatchHistoryService.Instance.GetResumePositionSeconds(video.Id, video.DurationSeconds);

            WatchHistoryService.Instance.RecordVideoStarted(video);
            _lastHistorySavedUtc = DateTime.UtcNow;

            _currentVideo = video;
            _currentSponsorSegments = new List<SponsorSegment>();
            _lastSkippedSponsorUuid = null;
            _prefetchTriggeredForCurrent = false;
            _isLiveStream = false;
            _actualPlayingQualityHeight = 0;
            _activeCodecLabel = "Direct3D 11";

            LiveStreamModeChanged?.Invoke(false);
            SponsorSegmentsLoaded?.Invoke(_currentSponsorSegments);
            StoryboardLoaded?.Invoke(null);
            VideoStarted?.Invoke(video);
            PlaybackProgressUpdated?.Invoke(false, resumeSeconds, resumeSeconds, video.DurationSeconds);

            // Carrega Storyboard em background (exceto lives)
            if (video.DurationSeconds > 0)
            {
                string targetVidId = video.Id;
                double durSec = video.DurationSeconds;
                _ = Task.Run(async () =>
                {
                    try
                    {
                        var sb = await StoryboardService.GetStoryboardAsync(targetVidId, durSec).ConfigureAwait(false);
                        _ = _uiDispatcher.BeginInvoke(new Action(() =>
                        {
                            if (!_isDisposed && _currentVideo != null && _currentVideo.Id == targetVidId)
                            {
                                StoryboardLoaded?.Invoke(sb);
                            }
                        }));
                    }
                    catch { }
                });
            }

            if (hasCompletedPrefetch)
            {
                _isLoading = false;
                LoadingStateChanged?.Invoke(false, video.Title, "");
                Logger.Info("PREFETCH", string.Format("Transição Gapless (Zero-Latency Handoff) ativada para Id={0} ({1})", video.Id, codec));
            }
            else
            {
                _isPlaying = false;
                _isLoading = true;
                LoadingStateChanged?.Invoke(true, video.Title, "Conectando ao YouTube (FlyleafLib Direct3D 11)...");

                string[]? streamUrls = null;
                string? resolveError = null;
                bool resolveUnavailable = false;

                try
                {
                    if (foregroundResolveTask != null && foregroundSession != null)
                    {
                        streamUrls = await foregroundResolveTask;
                        if (_playRequestSequence != requestId || _activeSession != foregroundSession) return;
                        resolveError = foregroundSession.LastResolveError;
                        resolveUnavailable = foregroundSession.IsVideoUnavailable;
                        isLiveStream = foregroundSession.IsLiveStream;
                    }
                }
                finally
                {
                    if (playCts != null)
                    {
                        if (ReferenceEquals(_activePlayResolveCts, playCts))
                            _activePlayResolveCts = null;
                        try { playCts.Dispose(); } catch { }
                    }
                }

                _isLoading = false;
                LoadingStateChanged?.Invoke(false, video.Title, "");

                if (streamUrls == null || streamUrls.Length == 0 || string.IsNullOrEmpty(streamUrls[0]))
                {
                    string errMsg = !string.IsNullOrEmpty(resolveError)
                        ? resolveError
                        : "Não foi possível obter os fluxos de streaming deste vídeo.";
                    StatusNotification?.Invoke("❌ " + errMsg);
                    ToastRequested?.Invoke("❌ " + errMsg);
                    PlaybackError?.Invoke(errMsg, resolveUnavailable);
                    return;
                }

                vUrl = streamUrls[0];
                aUrl = streamUrls.Length > 1 ? streamUrls[1] : null;
                codec = streamUrls.Length > 2 ? streamUrls[2] : "Direct";
            }

            await ensurePlayerTask;
            if (_playRequestSequence != requestId || _isDisposed) return;

            _isLiveStream = isLiveStream;
            LiveStreamModeChanged?.Invoke(isLiveStream);

            ApplyAudioSettings();
            double effectiveResumeSec = (!isLiveStream && resumeSeconds > 0) ? (double)resumeSeconds : 0.0;
            await OpenStreamInFlyleafAsync(vUrl!, aUrl, effectiveResumeSec);
            if (_playRequestSequence != requestId || _isDisposed) return;

            if (effectiveResumeSec > 0.0)
            {
                ToastRequested?.Invoke("Retomado de " + YouTubeService.FormatSeconds(effectiveResumeSec));
            }

            _isPlaying = true;
            _activeCodecLabel = codec;
            DetectAndEmitMediaMetrics(codec);
            StatusNotification?.Invoke(string.Format("▶ Assistindo ({0} • Flyleaf): {1}",
                ActualQualityHeight >= 2160 ? "4K" : string.Format("{0}p", ActualQualityHeight), video.Title));

            if (_config.EnableSponsorBlock && !isLiveStream)
            {
                string targetVidId = video.Id;
                string targetTitle = video.Title;
                _ = Task.Run(async () =>
                {
                    try
                    {
                        var segs = await SponsorBlockService.GetVideoSegmentsAsync(targetVidId).ConfigureAwait(false);
                        _ = _uiDispatcher.BeginInvoke(new Action(() =>
                        {
                            if (!_isDisposed && _currentVideo != null && _currentVideo.Id == targetVidId && segs != null)
                            {
                                _currentSponsorSegments = segs;
                                SponsorSegmentsLoaded?.Invoke(_currentSponsorSegments);
                                if (_currentSponsorSegments.Count > 0)
                                {
                                    Logger.Info("SPONSORBLOCK", string.Format("SponsorBlock: {0} trechos carregados para {1}", _currentSponsorSegments.Count, targetTitle));
                                }
                            }
                        }));
                    }
                    catch { }
                });
            }
        }

        private async Task OpenStreamInFlyleafAsync(string videoUrl, string? audioUrl, double resumeSeconds)
        {
            if (_player == null || string.IsNullOrEmpty(videoUrl)) return;

            try
            {
                if (_playerConfig != null)
                {
                    _playerConfig.Player.AutoPlay = true;
                    _playerConfig.Audio.Enabled = true;
                }

                var openTask = Task.Run(() =>
                {
                    bool hasSeparateAudio = !string.IsNullOrEmpty(audioUrl) &&
                                            !string.Equals(audioUrl, videoUrl, StringComparison.OrdinalIgnoreCase);

                    var res = _player.Open(videoUrl, defaultVideo: true, defaultAudio: !hasSeparateAudio, defaultSubtitles: false);
                    if (res != null && res.Success && hasSeparateAudio)
                    {
                        try
                        {
                            var extAudio = new FlyleafLib.MediaFramework.MediaStream.ExternalAudioStream
                            {
                                Url = audioUrl
                            };
                            var audioRes = _player.Open(extAudio, resync: true);
                            if (audioRes == null || !audioRes.Success)
                            {
                                Logger.Warn("FLYLEAF", "Aviso ao acoplar fluxo de áudio separado: " + (audioRes != null ? audioRes.Error : "desconhecido"));
                            }
                        }
                        catch (Exception exAudio)
                        {
                            Logger.Warn("FLYLEAF", "Aviso ao acoplar fluxo de áudio separado: " + exAudio.Message);
                        }
                    }
                    return res;
                });

                var result = await openTask;
                if (result == null || !result.Success)
                {
                    string err = result != null ? result.Error : "Erro desconhecido";
                    Logger.Error("FLYLEAF", "Falha ao abrir fluxo no FlyleafLib: " + err);
                    ToastRequested?.Invoke("❌ Falha ao abrir vídeo: " + err);
                    return;
                }

                ApplyAudioSettings();
                if (resumeSeconds > 1.0)
                {
                    try { _player.SeekAccurate((int)(resumeSeconds * 1000.0)); } catch { }
                }
                _player.Play();
            }
            catch (Exception ex)
            {
                Logger.Error("FLYLEAF", "Exceção ao abrir fluxo no FlyleafLib: " + ex.Message, ex);
            }
        }

        public async Task SwitchQualityAsync(int targetHeight)
        {
            if (targetHeight <= 0 || _isSwitchingQuality || _isDisposed) return;

            _config.VideoQualityHeight = targetHeight;
            _config.Save();

            if (_currentVideo == null || _player == null)
            {
                ToastRequested?.Invoke(string.Format("Qualidade padrão definida para {0}p", targetHeight));
                return;
            }

            _isSwitchingQuality = true;
            double savedSeconds = GetCurrentSeconds();
            bool wasPlaying = IsPlaying;
            string videoId = _currentVideo.Id;

            ToastRequested?.Invoke(string.Format("🔄 Alterando qualidade para {0}p...", targetHeight));

            try
            {
                string binDir = YtDlpResolver.GetYtDlpDirectory();
                using var session = new ProgressiveVideoSession(_currentVideo, targetHeight, binDir, _config.BrowserForCookies, _config.CookiesFilePath);
                using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(25));
                var urls = await session.ResolveStreamUrlsAsync(cts.Token);

                if (_currentVideo == null || _currentVideo.Id != videoId || _isDisposed) return;

                if (urls != null && urls.Length > 0 && !string.IsNullOrEmpty(urls[0]))
                {
                    string vUrl = urls[0];
                    string? aUrl = urls.Length > 1 ? urls[1] : null;
                    string codec = urls.Length > 2 ? urls[2] : "Direct";

                    await OpenStreamInFlyleafAsync(vUrl, aUrl, _isLiveStream ? 0.0 : savedSeconds);
                    if (!wasPlaying && _player != null)
                    {
                        try { _player.Pause(); } catch { }
                        _isPlaying = false;
                    }
                    else
                    {
                        _isPlaying = true;
                    }

                    _activeCodecLabel = codec;
                    DetectAndEmitMediaMetrics(codec);
                    ToastRequested?.Invoke(string.Format("✅ Qualidade alterada para {0}p", ActualQualityHeight));
                }
                else
                {
                    ToastRequested?.Invoke("❌ Não foi possível trocar para " + targetHeight + "p");
                }
            }
            catch (Exception ex)
            {
                Logger.Warn("QUALITY", "Erro ao trocar qualidade: " + ex.Message);
            }
            finally
            {
                _isSwitchingQuality = false;
            }
        }

        public void TogglePlayPause()
        {
            if (_player == null || _currentVideo == null) return;
            try
            {
                if (_player.IsPlaying)
                {
                    _player.Pause();
                    _isPlaying = false;
                    SaveCurrentPlaybackProgress(false);
                }
                else
                {
                    if (_config.AutoPauseMusicWhenVideoPlays && _musicEngine != null && _musicEngine.IsPlaying)
                        _musicEngine.PauseIfPlaying();
                    _player.Play();
                    _isPlaying = true;
                }
            }
            catch { }
        }

        public void PauseIfPlaying()
        {
            if (_player != null && _player.IsPlaying)
            {
                try
                {
                    _player.Pause();
                    _isPlaying = false;
                    SaveCurrentPlaybackProgress(false);
                }
                catch { }
            }
        }

        public void Stop()
        {
            SaveCurrentPlaybackProgress(false);
            StopActiveSession();
            try { _player?.Stop(); } catch { }
            _isPlaying = false;
            _isLoading = false;
            LoadingStateChanged?.Invoke(false, "", "");
        }

        public void CancelLoad()
        {
            StopActiveSession();
            _isLoading = false;
            LoadingStateChanged?.Invoke(false, "", "");
            StatusNotification?.Invoke("Carregamento do vídeo cancelado.");
        }

        public void SeekTo(double seconds)
        {
            if (_player == null || _isLiveStream) return;
            try
            {
                double total = GetTotalSeconds();
                double target = Math.Max(0.0, total > 0 ? Math.Min(total - 0.5, seconds) : seconds);
                int ms = (int)Math.Round(target * 1000.0);
                Logger.Info("SEEK", string.Format("Flyleaf SeekAccurate para {0:0.0}s ({1}ms)", target, ms));
                _player.SeekAccurate(ms);
            }
            catch { }
        }

        public void SeekRelative(double deltaSeconds)
        {
            if (_player == null || _isLiveStream) return;
            SeekTo(GetCurrentSeconds() + deltaSeconds);
        }

        public void SetSpeed(double speed)
        {
            if (_player == null) return;
            try
            {
                double clamped = Math.Max(0.25, Math.Min(2.0, speed));
                _player.Speed = clamped;
                ToastRequested?.Invoke(string.Format("Velocidade: {0:0.##}x", clamped));
            }
            catch { }
        }

        public void SetVolume(double vol0To1)
        {
            double clamped = Math.Max(0.0, Math.Min(1.0, vol0To1));
            _config.VideoVolume = clamped;
            if (clamped > 0.0 && _config.IsVideoMuted)
            {
                _config.IsVideoMuted = false;
            }
            _config.Save();
            ApplyAudioSettings();
        }

        public void ChangeVolumeBy(double delta)
        {
            SetVolume(_config.VideoVolume + delta);
        }

        public void ToggleMute()
        {
            _config.IsVideoMuted = !_config.IsVideoMuted;
            _config.Save();
            ApplyAudioSettings();
        }

        public void ApplyAudioSettings()
        {
            if (_player != null)
            {
                try
                {
                    _player.Audio.Mute = _config.IsVideoMuted;
                    _player.Audio.Volume = (int)Math.Round(Math.Max(0.0, Math.Min(1.0, _config.VideoVolume)) * 100.0);
                }
                catch { }
            }
            AudioSettingsUpdated?.Invoke(_config.VideoVolume, _config.IsVideoMuted);
        }

        public void EnqueueNext(TrackItem video)
        {
            if (video == null || string.IsNullOrEmpty(video.Id) || video.IsChannel || video.IsPlaylistCard) return;
            _upNextQueue.RemoveAll(x => string.Equals(x.Id, video.Id, StringComparison.OrdinalIgnoreCase));
            _upNextQueue.Insert(0, video);
            ToastRequested?.Invoke("➕ Adicionado a seguir: " + video.Title);
            StatusNotification?.Invoke("➕ Adicionado à fila de vídeo: " + video.Title);
        }

        public TrackItem? GetNextVideoCandidate(bool consumeFromQueue = false)
        {
            if (_upNextQueue.Count > 0)
            {
                var next = _upNextQueue[0];
                if (consumeFromQueue) _upNextQueue.RemoveAt(0);
                return next;
            }
            return NextVideoFallbackProvider?.Invoke();
        }

        public double GetCurrentSeconds()
        {
            if (_player == null) return 0.0;
            try { return Math.Max(0.0, _player.CurTime / 10_000_000.0); } catch { return 0.0; }
        }

        public double GetTotalSeconds()
        {
            if (_player != null && _player.Duration > 0)
            {
                return _player.Duration / 10_000_000.0;
            }
            return _currentVideo != null ? _currentVideo.DurationSeconds : 0.0;
        }

        private double GetBufferedSeconds()
        {
            if (_player == null) return 0.0;
            try
            {
                double cur = GetCurrentSeconds();
                double buf = _player.BufferedDuration / 10_000_000.0;
                return cur + Math.Max(0.0, buf);
            }
            catch { return 0.0; }
        }

        private void OnPlayerOpenCompleted(object? sender, OpenCompletedArgs e)
        {
            if (_isDisposed || !e.Success) return;
            _uiDispatcher.BeginInvoke(new Action(() =>
            {
                DetectAndEmitMediaMetrics(_activeCodecLabel);
            }));
        }

        private void OnPlayerPlaybackStopped(object? sender, PlaybackStoppedArgs e)
        {
            if (_isDisposed || _isSwitchingQuality) return;
            if (_player != null && _player.Status == Status.Ended)
            {
                Logger.Info("FLYLEAF", "Fim do vídeo alcançado sem cortes prematuros.");
                _uiDispatcher.BeginInvoke(new Action(() =>
                {
                    _isPlaying = false;
                    SaveCurrentPlaybackProgress(true);
                    var next = GetNextVideoCandidate(false);
                    if (next != null)
                        VideoEndedWithNextCandidate?.Invoke(next);
                    else
                        VideoEnded?.Invoke();
                }));
            }
        }

        private void OnPlaybackTimerTick(object? sender, EventArgs e)
        {
            if (_isDisposed || _player == null || _currentVideo == null) return;

            bool playingNow = _player.IsPlaying;
            _isPlaying = playingNow;

            double cur = GetCurrentSeconds();
            double total = GetTotalSeconds();
            double buf = GetBufferedSeconds();

            PlaybackProgressUpdated?.Invoke(playingNow, cur, buf, total);

            if (playingNow && !_isLiveStream)
            {
                // Auto-save WatchHistory a cada 10 segundos
                if ((DateTime.UtcNow - _lastHistorySavedUtc).TotalSeconds >= 10.0 && cur > 3.0)
                {
                    SaveCurrentPlaybackProgress(false);
                }

                // SponsorBlock Auto-Skip
                if (_config.EnableSponsorBlock && _config.AutoSkipSponsors && _currentSponsorSegments.Count > 0)
                {
                    if (SponsorBlockService.CheckAndGetSkipTarget(cur, _currentSponsorSegments, out double skipTo, out SponsorSegment matched))
                    {
                        if (matched != null && matched.UUID != _lastSkippedSponsorUuid)
                        {
                            _lastSkippedSponsorUuid = matched.UUID;
                            SeekTo(skipTo);
                            SponsorSkipped?.Invoke(matched.CategoryLabel, skipTo);
                        }
                    }
                }

                // Pré-resolução do próximo vídeo da fila após 10s assistindo (único prefetch automático ativo)
                if (!_prefetchTriggeredForCurrent && cur >= 10.0)
                {
                    TriggerNextVideoPrefetch();
                }
            }
        }

        private void DetectAndEmitMediaMetrics(string codecHint)
        {
            if (_player == null) return;
            try
            {
                int vW = _player.Video?.Width ?? 0;
                int vH = _player.Video?.Height ?? 0;
                int actualQ = ProgressiveVideoSession.ResolveEffectiveQualityHeight(vW, vH);
                if (actualQ > 0) _actualPlayingQualityHeight = actualQ;

                string codec = _player.Video?.Codec ?? "";
                string pixFmt = _player.Video?.PixelFormat ?? "";
                bool hw = _player.Video?.VideoAcceleration ?? false;
                if (vW > 0 && vH > 0)
                {
                    Logger.Info("FLYLEAF", string.Format("Fluxo aberto e validado pelo FFmpeg: Res={0}x{1} (QualidadeReal={2}p, Solicitada={3}p), Codec={4}, PixelFormat={5}, AceleraçãoHW={6}",
                        vW, vH, ActualQualityHeight, _config.VideoQualityHeight, codec, pixFmt, hw));
                }

                MediaMetricsUpdated?.Invoke(ActualQualityHeight, !string.IsNullOrEmpty(codecHint) ? codecHint : _activeCodecLabel);
            }
            catch { }
        }

        private void TriggerNextVideoPrefetch()
        {
            if (_isPrefetching || _prefetchTriggeredForCurrent || _currentVideo == null)
                return;

            var nextVideo = GetNextVideoCandidate(false);
            if (nextVideo == null || string.IsNullOrEmpty(nextVideo.Id))
                return;

            if (string.Equals(_prefetchedVideoId, nextVideo.Id, StringComparison.OrdinalIgnoreCase) &&
                _prefetchedQualityHeight == _config.VideoQualityHeight &&
                _prefetchedStreams != null)
            {
                _prefetchTriggeredForCurrent = true;
                return;
            }

            if (!ProgressiveVideoSession.SharedPrefetchSemaphore.Wait(0))
                return;

            _isPrefetching = true;
            _prefetchTriggeredForCurrent = true;
            int targetQuality = _config.VideoQualityHeight;
            string currentPlayingId = _currentVideo.Id;

            _ = Task.Run(async () =>
            {
                try
                {
                    Logger.Info("PREFETCH", string.Format("Iniciando pré-resolução em background do próximo vídeo: Id={0}, Title='{1}' ({2}p)",
                        nextVideo.Id, nextVideo.Title, targetQuality));

                    string binDir = YtDlpResolver.GetYtDlpDirectory();
                    using var prefetchSession = new ProgressiveVideoSession(
                        nextVideo,
                        targetQuality,
                        binDir,
                        _config.BrowserForCookies,
                        _config.CookiesFilePath);

                    using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(25));
                    var urls = await prefetchSession.ResolveStreamUrlsAsync(cts.Token).ConfigureAwait(false);
                    if (urls != null && urls.Length > 0 && !string.IsNullOrEmpty(urls[0]))
                    {
                        string vUrl = urls[0];
                        string aUrl = urls.Length > 1 ? urls[1] : "";
                        string codec = urls.Length > 2 ? urls[2] : "Direct";

                        await _uiDispatcher.InvokeAsync(() =>
                        {
                            if (!_isDisposed && _currentVideo != null && _currentVideo.Id == currentPlayingId && _config.VideoQualityHeight == targetQuality)
                            {
                                _prefetchedVideoId = nextVideo.Id;
                                _prefetchedQualityHeight = targetQuality;
                                _prefetchedStreams = (vUrl, aUrl, codec);
                                Logger.Info("PREFETCH", string.Format("Próximo vídeo pré-resolvido e pronto em cache (Zero-Latency Handoff): Id={0} ({1}p, {2})",
                                    nextVideo.Id, targetQuality, codec));
                            }
                        });
                    }
                }
                catch { }
                finally
                {
                    _isPrefetching = false;
                    ProgressiveVideoSession.SharedPrefetchSemaphore.Release();
                }
            });
        }

        public void SaveCurrentPlaybackProgress(bool finished)
        {
            if (_currentVideo == null || _isLiveStream) return;
            try
            {
                double cur = finished ? GetTotalSeconds() : GetCurrentSeconds();
                double total = GetTotalSeconds();
                if (total > 0)
                {
                    WatchHistoryService.Instance.UpdatePlaybackProgress(_currentVideo.Id, (long)Math.Floor(cur), (long)Math.Floor(total));
                    _lastHistorySavedUtc = DateTime.UtcNow;
                }
            }
            catch { }
        }

        private void StopActiveSession()
        {
            _playRequestSequence++;
            if (_activePlayResolveCts != null)
            {
                try { _activePlayResolveCts.Cancel(); } catch { }
                try { _activePlayResolveCts.Dispose(); } catch { }
                _activePlayResolveCts = null;
            }
            if (_activeSession != null)
            {
                try { _activeSession.Dispose(); } catch { }
                _activeSession = null;
            }
        }

        public void Dispose()
        {
            if (_isDisposed) return;
            _isDisposed = true;

            SaveCurrentPlaybackProgress(false);
            StopActiveSession();

            try { _playbackTimer.Stop(); } catch { }
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
            }
            catch { }
        }
    }
}
