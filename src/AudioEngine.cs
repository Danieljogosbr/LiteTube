using System;
using System.Collections.Generic;
using System.IO;
using System.Threading;
using System.Threading.Tasks;

namespace LiteTube
{
    /// <summary>
    /// Motor de áudio dedicado ao Ambiente de Música (🎵 Ambiente Música) baseado na classe VlcPlayer em modo Headless.
    /// Elimina duplicações de chamadas P/Invoke e reproduz fluxos de áudio Opus/AAC diretamente via HTTPS sem intermediários.
    /// Utiliza _config.MusicAudio exclusivamente para manter o volume de música independente do vídeo.
    /// </summary>
    public class AudioEngine : IDisposable
    {
        private VlcPlayer _player;
        private LocalRangeUnthrottlerProxy _rangeProxy = null;
        private readonly YouTubeService _ytService;
        private readonly AppConfig _config;
        private readonly List<TrackItem> _queue;
        private readonly Random _rng;
        private readonly System.Threading.Timer _stateMonitorTimer;
        private readonly SynchronizationContext _syncContext;

        private int _currentIndex = -1;
        private bool _isPlaying = false;
        private bool _isLoading = false;
        private bool _isDisposed = false;
        private CancellationTokenSource _loadCts;
        private DateTime _playStartUtc = DateTime.MinValue;
        private int _consecutiveInstantEnds = 0;
        private int _consecutiveFailures = 0;

        public event Action<TrackItem> TrackChanged;
        public event Action<bool, bool> PlaybackStateChanged; // (isPlaying, isLoading)
        public event Action<string> StatusMessage;
        public event Action QueueUpdated;
        public event Action AudioSettingsChanged;

        public AudioEngine(YouTubeService ytService, AppConfig config)
        {
            _ytService = ytService;
            _config = config;
            _queue = new List<TrackItem>();
            _rng = new Random();
            _syncContext = SynchronizationContext.Current;

            // Player LibVLC Headless é instanciado apenas sob demanda (lazy init) ao tocar a primeira música,
            // evitando travar a inicialização da UI enquanto o LibVLC escaneia plugins no disco.

            // Monitor de segurança periódico para detecção de término
            _stateMonitorTimer = new System.Threading.Timer(OnStateMonitorTick, null, 250, 250);
        }

        private void EnsurePlayerCreated()
        {
            if (_player == null)
            {
                _player = new VlcPlayer(IntPtr.Zero);
                _player.OnEndReached += () => SafeDispatch(OnMediaEnded);
                ApplyAudioSettingsToPlayer();
            }
        }

        public List<TrackItem> Queue { get { return _queue; } }
        public int CurrentIndex { get { return _currentIndex; } }
        public bool IsPlaying { get { return _isPlaying || (_player != null && _player.IsPlaying); } }
        public bool IsLoading { get { return _isLoading; } }

        public TrackItem CurrentTrack
        {
            get
            {
                if (_currentIndex >= 0 && _currentIndex < _queue.Count)
                    return _queue[_currentIndex];
                return null;
            }
        }

        public double Volume
        {
            get { return _config.MusicVolume; }
            set
            {
                double v = Math.Max(0.0, Math.Min(1.0, value));
                _config.MusicVolume = v;
                if (v > 0.0 && _config.IsMusicMuted)
                {
                    _config.IsMusicMuted = false;
                }
                if (_player != null)
                {
                    _player.IsMuted = _config.IsMusicMuted;
                    _player.Volume = _config.IsMusicMuted ? 0 : (int)Math.Round(_config.MusicVolume * 100.0);
                }
                ApplyAudioSettingsToPlayer();
            }
        }

        public bool IsMuted
        {
            get { return _config.IsMusicMuted; }
            set
            {
                _config.IsMusicMuted = value;
                if (_player != null)
                {
                    _player.IsMuted = value;
                    _player.Volume = value ? 0 : (int)Math.Round(_config.MusicVolume * 100.0);
                }
                ApplyAudioSettingsToPlayer();
            }
        }

        public double MaxVolumeCeiling
        {
            get { return 1.0; }
            set { }
        }

        public double Balance
        {
            get { return 0.0; }
            set { }
        }

        public double PlaybackSpeed
        {
            get { return _player != null ? _player.SpeedRatio : 1.0; }
            set
            {
                if (_player != null)
                {
                    _player.SpeedRatio = (float)Math.Max(0.5, Math.Min(2.0, value));
                }
            }
        }

        public double EffectiveOutputVolumePercent
        {
            get { return (int)Math.Round(_config.MusicVolume * 100); }
        }

        public void ToggleMute()
        {
            IsMuted = !_config.IsMusicMuted;
            _config.Save();
            EmitStatus(_config.IsMusicMuted
                ? "🔇 Áudio de música mutado"
                : string.Format("🔊 Volume da Música: {0}%", (int)Math.Round(_config.MusicVolume * 100)));
        }

        public void ChangeVolumeBy(double delta)
        {
            Volume = Math.Round(_config.MusicVolume + delta, 2);
            _config.Save();
            EmitStatus(string.Format("🔊 Volume da Música: {0}%", (int)Math.Round(_config.MusicVolume * 100)));
        }

        public void PauseIfPlaying()
        {
            if (IsPlaying)
            {
                try
                {
                    if (_player != null)
                    {
                        _player.Pause();
                    }
                    _isPlaying = false;
                    if (PlaybackStateChanged != null) SafeDispatch(delegate { PlaybackStateChanged(_isPlaying, _isLoading); });
                }
                catch { }
            }
        }

        public void ApplyAudioSettingsToPlayer()
        {
            if (_player != null)
            {
                try
                {
                    _player.IsMuted = _config.IsMusicMuted;
                    _player.Volume = _config.IsMusicMuted ? 0 : (int)Math.Round(_config.MusicVolume * 100.0);
                }
                catch { }
            }

            if (AudioSettingsChanged != null)
            {
                SafeDispatch(AudioSettingsChanged);
            }
        }

        public double CurrentPositionSeconds
        {
            get
            {
                return _player != null ? _player.CurrentTimeSeconds : 0.0;
            }
        }

        public double TotalDurationSeconds
        {
            get
            {
                if (_player != null && _player.DurationSeconds > 0)
                {
                    return _player.DurationSeconds;
                }
                if (CurrentTrack != null && CurrentTrack.DurationSeconds > 0)
                {
                    return CurrentTrack.DurationSeconds;
                }
                return 0.0;
            }
        }

        public void SafeDispatch(Action action)
        {
            if (action == null) return;
            if (_syncContext != null)
            {
                try
                {
                    _syncContext.Post(delegate { try { action(); } catch { } }, null);
                }
                catch
                {
                    try { action(); } catch { }
                }
            }
            else
            {
                try { action(); } catch { }
            }
        }

        public void SeekToSeconds(double seconds)
        {
            if (_player == null) return;
            try
            {
                double max = TotalDurationSeconds;
                if (max > 0)
                {
                    seconds = Math.Max(0.0, Math.Min(max - 0.5, seconds));
                }
                else
                {
                    seconds = Math.Max(0.0, seconds);
                }
                _player.Seek(seconds);
            }
            catch { }
        }

        public void SetQueueAndPlay(List<TrackItem> tracks, int startIndex)
        {
            if (tracks == null || tracks.Count == 0) return;
            _queue.Clear();
            _queue.AddRange(tracks);
            if (QueueUpdated != null) SafeDispatch(QueueUpdated);

            int idx = Math.Max(0, Math.Min(_queue.Count - 1, startIndex));
            PlayIndex(idx);
        }

        public void PlaySingleAndAddToQueue(TrackItem track)
        {
            if (track == null) return;
            for (int i = 0; i < _queue.Count; i++)
            {
                if (string.Equals(_queue[i].Id, track.Id, StringComparison.OrdinalIgnoreCase))
                {
                    PlayIndex(i);
                    return;
                }
            }
            _queue.Add(track);
            if (QueueUpdated != null) SafeDispatch(QueueUpdated);
            PlayIndex(_queue.Count - 1);
        }

        public void AddToQueue(TrackItem track)
        {
            if (track == null) return;
            _queue.Add(track);
            if (QueueUpdated != null) SafeDispatch(QueueUpdated);
            EmitStatus("Adicionado à fila de música: " + track.Title);
        }

        public void RemoveFromQueueAt(int index)
        {
            if (index < 0 || index >= _queue.Count) return;
            bool wasCurrent = (index == _currentIndex);
            _queue.RemoveAt(index);

            if (index < _currentIndex)
            {
                _currentIndex--;
            }
            else if (wasCurrent)
            {
                if (_queue.Count == 0)
                {
                    Stop();
                    _currentIndex = -1;
                }
                else
                {
                    if (_currentIndex >= _queue.Count) _currentIndex = 0;
                    PlayIndex(_currentIndex);
                }
            }

            if (QueueUpdated != null) SafeDispatch(QueueUpdated);
        }

        public void ClearQueue()
        {
            Stop();
            _queue.Clear();
            _currentIndex = -1;
            if (QueueUpdated != null) SafeDispatch(QueueUpdated);
            if (TrackChanged != null) SafeDispatch(delegate { TrackChanged(null); });
        }

        public async void PlayIndex(int index)
        {
            if (index < 0 || index >= _queue.Count) return;

            if (_loadCts != null)
            {
                try { _loadCts.Cancel(); _loadCts.Dispose(); } catch { }
            }
            _loadCts = new CancellationTokenSource();
            var token = _loadCts.Token;

            _currentIndex = index;
            TrackItem track = _queue[_currentIndex];

            StopCurrentPlaybackInternal();

            _isPlaying = false;
            _isLoading = true;
            if (TrackChanged != null) SafeDispatch(delegate { TrackChanged(track); });
            if (PlaybackStateChanged != null) SafeDispatch(delegate { PlaybackStateChanged(_isPlaying, _isLoading); });
            if (QueueUpdated != null) SafeDispatch(QueueUpdated);

            EmitStatus("🎵 Carregando música: " + track.Title + "...");

            string source = await _ytService.GetPlayableSourceAsync(track, _config, token);
            if (token.IsCancellationRequested) return;

            if (string.IsNullOrEmpty(source))
            {
                _consecutiveFailures++;
                if (_consecutiveFailures < 4 && _queue.Count > 1)
                {
                    EmitStatus(string.Format("⚠️ Faixa '{0}' indisponível no momento. Avançando...", track.Title));
                    await Task.Delay(500);
                    if (!token.IsCancellationRequested) SafeDispatch(PlayNext);
                    return;
                }
                _consecutiveFailures = 0;
                _isLoading = false;
                if (PlaybackStateChanged != null) SafeDispatch(delegate { PlaybackStateChanged(false, false); });
                EmitStatus("Erro ao carregar áudio de: " + track.Title);
                return;
            }

            bool playFailed = false;
            string failMsg = "";
            try
            {
                EnsurePlayerCreated();

                string playMrl;
                if (source.StartsWith("http://", StringComparison.OrdinalIgnoreCase) ||
                    source.StartsWith("https://", StringComparison.OrdinalIgnoreCase))
                {
                    if (_rangeProxy != null)
                    {
                        try { _rangeProxy.Dispose(); } catch { }
                        _rangeProxy = null;
                    }
                    _rangeProxy = new LocalRangeUnthrottlerProxy(null, source);
                    await _rangeProxy.PrewarmHeadersAsync();
                    playMrl = _rangeProxy.AudioLoopbackUrl;
                }
                else
                {
                    playMrl = Path.GetFullPath(source);
                }

                _player.IsMuted = _config.IsMusicMuted;
                _player.Volume = _config.IsMusicMuted ? 0 : (int)Math.Round(_config.MusicVolume * 100.0);
                _player.PlayStream(playMrl, null, track.DurationSeconds, 0);
                _playStartUtc = DateTime.UtcNow;

                _consecutiveFailures = 0;
                _isLoading = false;
                _isPlaying = true;
                if (PlaybackStateChanged != null) SafeDispatch(delegate { PlaybackStateChanged(_isPlaying, _isLoading); });
                EmitStatus("🎵 Tocando: " + track.Title + " — " + track.Artist);

                int nextIdx = PeekNextIndex();
                if (nextIdx >= 0 && nextIdx < _queue.Count && nextIdx != _currentIndex)
                {
                    _ytService.PrefetchTrackInBackground(_queue[nextIdx], _config);
                }

                MemoryOptimizer.TrimMemory();
            }
            catch (Exception ex)
            {
                playFailed = true;
                failMsg = ex.Message;
            }

            if (playFailed)
            {
                _consecutiveFailures++;
                if (_consecutiveFailures < 4 && _queue.Count > 1)
                {
                    EmitStatus(string.Format("⚠️ Falha ao tocar '{0}'. Avançando...", track.Title));
                    await Task.Delay(500);
                    if (!token.IsCancellationRequested) SafeDispatch(PlayNext);
                    return;
                }
                _consecutiveFailures = 0;
                _isLoading = false;
                _isPlaying = false;
                if (PlaybackStateChanged != null) SafeDispatch(delegate { PlaybackStateChanged(false, false); });
                EmitStatus("Falha na reprodução: " + failMsg);
            }
        }

        public void TogglePlayPause()
        {
            if (_isLoading) return;

            if (_currentIndex < 0 && _queue.Count > 0)
            {
                PlayIndex(0);
                return;
            }

            if (CurrentTrack == null) return;

            try
            {
                if (_isPlaying || (_player != null && _player.IsPlaying))
                {
                    if (_player != null)
                    {
                        _player.Pause();
                    }
                    _isPlaying = false;
                    EmitStatus("🎵 Pausado: " + CurrentTrack.Title);
                }
                else
                {
                    if (_player != null)
                    {
                        ApplyAudioSettingsToPlayer();
                        _player.Play();
                    }
                    _isPlaying = true;
                    EmitStatus("🎵 Tocando: " + CurrentTrack.Title);
                }
                if (PlaybackStateChanged != null) SafeDispatch(delegate { PlaybackStateChanged(_isPlaying, _isLoading); });
            }
            catch { }
        }

        public void Stop()
        {
            if (_loadCts != null)
            {
                try { _loadCts.Cancel(); } catch { }
            }

            StopCurrentPlaybackInternal();

            _isPlaying = false;
            _isLoading = false;
            if (PlaybackStateChanged != null) SafeDispatch(delegate { PlaybackStateChanged(false, false); });
            MemoryOptimizer.TrimMemory();
        }

        private void StopCurrentPlaybackInternal()
        {
            if (_rangeProxy != null)
            {
                try { _rangeProxy.Dispose(); } catch { }
                _rangeProxy = null;
            }
            if (_player != null)
            {
                try { _player.Stop(); } catch { }
            }
        }

        public void PlayNext()
        {
            if (_queue.Count == 0) return;

            if (_config.Shuffle && _queue.Count > 1)
            {
                int next = _currentIndex;
                for (int tries = 0; tries < 5 && next == _currentIndex; tries++)
                {
                    next = _rng.Next(0, _queue.Count);
                }
                PlayIndex(next);
                return;
            }

            int nextIdx = _currentIndex + 1;
            if (nextIdx >= _queue.Count)
            {
                if ((RepeatMode)_config.Repeat == RepeatMode.RepeatAll)
                {
                    nextIdx = 0;
                }
                else
                {
                    Stop();
                    EmitStatus("Fim da fila de música.");
                    return;
                }
            }
            PlayIndex(nextIdx);
        }

        public void PlayPrevious()
        {
            if (_queue.Count == 0) return;

            if (CurrentPositionSeconds > 4.0)
            {
                SeekToSeconds(0);
                return;
            }

            int prevIdx = _currentIndex - 1;
            if (prevIdx < 0)
            {
                prevIdx = ((RepeatMode)_config.Repeat == RepeatMode.RepeatAll) ? _queue.Count - 1 : 0;
            }
            PlayIndex(prevIdx);
        }

        private int PeekNextIndex()
        {
            if (_queue.Count == 0) return -1;
            int next = _currentIndex + 1;
            if (next >= _queue.Count)
            {
                return ((RepeatMode)_config.Repeat == RepeatMode.RepeatAll) ? 0 : -1;
            }
            return next;
        }

        private void OnStateMonitorTick(object state)
        {
            if (_isDisposed || _player == null || _isLoading) return;

            try
            {
                if (_isPlaying && _player.HasEnded)
                {
                    _isPlaying = false;
                    SafeDispatch(OnMediaEnded);
                }
            }
            catch { }
        }

        private void OnMediaEnded()
        {
            if (_currentIndex < 0 || _queue.Count == 0) return;
            if (!_isPlaying && (_player != null && !_player.HasEnded)) return;
            _isPlaying = false;

            double elapsedSec = (DateTime.UtcNow - _playStartUtc).TotalSeconds;
            if (elapsedSec < 3.0 && TotalDurationSeconds > 8.0)
            {
                if (CurrentTrack != null)
                {
                    _ytService.InvalidateCachedTrack(CurrentTrack.Id);
                }
                _consecutiveInstantEnds++;
                if (_consecutiveInstantEnds >= 3)
                {
                    _consecutiveInstantEnds = 0;
                    EmitStatus("⚠️ Arquivo de áudio ilegível, pulando para a próxima faixa...");
                    PlayNext();
                    return;
                }
            }
            else
            {
                _consecutiveInstantEnds = 0;
            }

            if ((RepeatMode)_config.Repeat == RepeatMode.RepeatOne && _currentIndex >= 0)
            {
                _playStartUtc = DateTime.UtcNow;
                SeekToSeconds(0);
                if (_player != null)
                {
                    _player.Play();
                    _isPlaying = true;
                    if (PlaybackStateChanged != null) SafeDispatch(delegate { PlaybackStateChanged(_isPlaying, _isLoading); });
                }
                return;
            }
            PlayNext();
        }

        private void EmitStatus(string msg)
        {
            if (StatusMessage != null) SafeDispatch(delegate { StatusMessage(msg); });
        }

        public void Dispose()
        {
            if (_isDisposed) return;
            _isDisposed = true;

            if (_stateMonitorTimer != null)
            {
                try { _stateMonitorTimer.Dispose(); } catch { }
            }

            if (_loadCts != null)
            {
                try { _loadCts.Cancel(); _loadCts.Dispose(); } catch { }
                _loadCts = null;
            }

            StopCurrentPlaybackInternal();

            if (_player != null)
            {
                try { _player.Dispose(); } catch { }
                _player = null;
            }
        }
    }
}
