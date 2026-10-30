using System;
using System.Collections.Generic;
using System.IO;
using System.Threading;
using System.Windows.Media;

namespace LiteTube
{
    /// <summary>
    /// Audio Engine dedicated to the Music Environment (🎵 Ambiente Música).
    /// Uses _config.MusicAudio exclusively so music volume stays independent from video volume.
    /// </summary>
    public class AudioEngine : IDisposable
    {
        private readonly MediaPlayer _player;
        private readonly YouTubeService _ytService;
        private readonly AppConfig _config;
        private readonly List<TrackItem> _queue;
        private readonly Random _rng;
        private int _currentIndex = -1;
        private bool _isPlaying = false;
        private bool _isLoading = false;
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

            _player = new MediaPlayer();
            ApplyAudioSettingsToPlayer();
            _player.MediaOpened += OnMediaOpened;
            _player.MediaEnded += OnMediaEnded;
            _player.MediaFailed += OnMediaFailed;
        }

        public AudioProfile Profile { get { return _config.MusicAudio; } }
        public List<TrackItem> Queue { get { return _queue; } }
        public int CurrentIndex { get { return _currentIndex; } }
        public bool IsPlaying { get { return _isPlaying; } }
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
            get { return Profile.Volume; }
            set
            {
                double v = Math.Max(0.0, Math.Min(1.0, value));
                Profile.Volume = v;
                if (v > 0.0 && Profile.IsMuted)
                {
                    Profile.IsMuted = false;
                }
                ApplyAudioSettingsToPlayer();
            }
        }

        public bool IsMuted
        {
            get { return Profile.IsMuted; }
            set
            {
                Profile.IsMuted = value;
                ApplyAudioSettingsToPlayer();
            }
        }

        public double MaxVolumeCeiling
        {
            get { return Profile.MaxVolumeCeiling; }
            set
            {
                Profile.MaxVolumeCeiling = Math.Max(0.10, Math.Min(1.0, value));
                ApplyAudioSettingsToPlayer();
            }
        }

        public double Balance
        {
            get { return Profile.Balance; }
            set
            {
                Profile.Balance = Math.Max(-1.0, Math.Min(1.0, value));
                ApplyAudioSettingsToPlayer();
            }
        }

        public double PlaybackSpeed
        {
            get { return Profile.PlaybackSpeed; }
            set
            {
                Profile.PlaybackSpeed = Math.Max(0.5, Math.Min(2.0, value));
                ApplyAudioSettingsToPlayer();
            }
        }

        public double EffectiveOutputVolumePercent
        {
            get { return Profile.EffectivePercent; }
        }

        public void ToggleMute()
        {
            IsMuted = !Profile.IsMuted;
            _config.Save();
            EmitStatus(Profile.IsMuted
                ? "🎵 Áudio de Música mutado (🔇)"
                : string.Format("🎵 Volume de Música: {0}%", (int)Math.Round(Profile.Volume * 100)));
        }

        public void ChangeVolumeBy(double delta)
        {
            Volume = Math.Round(Profile.Volume + delta, 2);
            _config.Save();
            EmitStatus(string.Format("🎵 Volume de Música: {0}% (Saída real: {1}%)",
                (int)Math.Round(Profile.Volume * 100), Profile.EffectivePercent));
        }

        public void PauseIfPlaying()
        {
            if (_isPlaying)
            {
                try
                {
                    _player.Pause();
                    _isPlaying = false;
                    if (PlaybackStateChanged != null) PlaybackStateChanged(_isPlaying, _isLoading);
                }
                catch { }
            }
        }

        public void ApplyAudioSettingsToPlayer()
        {
            if (_player != null && _player.Dispatcher != null && !_player.Dispatcher.CheckAccess())
            {
                _player.Dispatcher.BeginInvoke(new Action(ApplyAudioSettingsToPlayer));
                return;
            }

            try
            {
                _player.IsMuted = Profile.IsMuted;
                _player.Volume = Profile.EffectiveVolume;
                _player.Balance = Math.Max(-1.0, Math.Min(1.0, Profile.Balance));
                _player.SpeedRatio = Math.Max(0.5, Math.Min(2.0, Profile.PlaybackSpeed));
            }
            catch { }

            if (AudioSettingsChanged != null) AudioSettingsChanged();
        }

        public double CurrentPositionSeconds
        {
            get
            {
                try { return _player.Position.TotalSeconds; }
                catch { return 0; }
            }
        }

        public double TotalDurationSeconds
        {
            get
            {
                try
                {
                    if (_player.NaturalDuration.HasTimeSpan)
                        return _player.NaturalDuration.TimeSpan.TotalSeconds;
                    if (CurrentTrack != null && CurrentTrack.DurationSeconds > 0)
                        return CurrentTrack.DurationSeconds;
                }
                catch { }
                return 0;
            }
        }

        public void SafeDispatch(Action action)
        {
            if (action == null) return;
            if (_player != null && _player.Dispatcher != null && !_player.Dispatcher.CheckAccess())
            {
                try { _player.Dispatcher.BeginInvoke(action); } catch { }
            }
            else
            {
                action();
            }
        }

        public void SeekToSeconds(double seconds)
        {
            if (_player != null && _player.Dispatcher != null && !_player.Dispatcher.CheckAccess())
            {
                _player.Dispatcher.BeginInvoke(new Action(() => SeekToSeconds(seconds)));
                return;
            }

            try
            {
                if (_player.NaturalDuration.HasTimeSpan)
                {
                    double max = _player.NaturalDuration.TimeSpan.TotalSeconds;
                    seconds = Math.Max(0, Math.Min(max - 0.5, seconds));
                    _player.Position = TimeSpan.FromSeconds(seconds);
                }
            }
            catch { }
        }

        public void SetQueueAndPlay(List<TrackItem> tracks, int startIndex)
        {
            if (tracks == null || tracks.Count == 0) return;
            _queue.Clear();
            _queue.AddRange(tracks);
            if (QueueUpdated != null) QueueUpdated();

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
            if (QueueUpdated != null) QueueUpdated();
            PlayIndex(_queue.Count - 1);
        }

        public void AddToQueue(TrackItem track)
        {
            if (track == null) return;
            _queue.Add(track);
            if (QueueUpdated != null) QueueUpdated();
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

            if (QueueUpdated != null) QueueUpdated();
        }

        public void ClearQueue()
        {
            Stop();
            _queue.Clear();
            _currentIndex = -1;
            if (QueueUpdated != null) QueueUpdated();
            if (TrackChanged != null) TrackChanged(null);
        }

        public async void PlayIndex(int index)
        {
            if (_player != null && _player.Dispatcher != null && !_player.Dispatcher.CheckAccess())
            {
                var op = _player.Dispatcher.BeginInvoke(new Action<int>(PlayIndex), index);
                GC.KeepAlive(op);
                return;
            }

            if (index < 0 || index >= _queue.Count) return;

            if (_loadCts != null)
            {
                _loadCts.Cancel();
                _loadCts.Dispose();
            }
            _loadCts = new CancellationTokenSource();
            var token = _loadCts.Token;

            _currentIndex = index;
            TrackItem track = _queue[_currentIndex];

            try
            {
                _player.Stop();
                _player.Close();
            }
            catch { }

            _isPlaying = false;
            _isLoading = true;
            if (TrackChanged != null) TrackChanged(track);
            if (PlaybackStateChanged != null) PlaybackStateChanged(_isPlaying, _isLoading);
            if (QueueUpdated != null) QueueUpdated();

            EmitStatus("🎵 Carregando música: " + track.Title + "...");

            string source = await _ytService.GetPlayableSourceAsync(track, _config, token);
            if (token.IsCancellationRequested) return;

            if (string.IsNullOrEmpty(source))
            {
                _consecutiveFailures++;
                if (_consecutiveFailures < 4 && _queue.Count > 1)
                {
                    EmitStatus(string.Format("⚠️ Faixa '{0}' indisponível no momento. Avançando...", track.Title));
                    await System.Threading.Tasks.Task.Delay(500);
                    if (!token.IsCancellationRequested) SafeDispatch(PlayNext);
                    return;
                }
                _consecutiveFailures = 0;
                _isLoading = false;
                if (PlaybackStateChanged != null) PlaybackStateChanged(false, false);
                EmitStatus("Erro ao carregar áudio de: " + track.Title);
                return;
            }

            bool playFailed = false;
            string failMsg = "";
            try
            {
                Uri uri = source.StartsWith("http", StringComparison.OrdinalIgnoreCase)
                    ? new Uri(source)
                    : new Uri(Path.GetFullPath(source));

                _player.Open(uri);
                ApplyAudioSettingsToPlayer();
                _playStartUtc = DateTime.UtcNow;
                _player.Play();

                _consecutiveFailures = 0;
                _isLoading = false;
                _isPlaying = true;
                if (PlaybackStateChanged != null) PlaybackStateChanged(_isPlaying, _isLoading);
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
                    await System.Threading.Tasks.Task.Delay(500);
                    if (!token.IsCancellationRequested) SafeDispatch(PlayNext);
                    return;
                }
                _consecutiveFailures = 0;
                _isLoading = false;
                _isPlaying = false;
                if (PlaybackStateChanged != null) PlaybackStateChanged(false, false);
                EmitStatus("Falha na reprodução: " + failMsg);
            }
        }

        public void TogglePlayPause()
        {
            if (_player != null && _player.Dispatcher != null && !_player.Dispatcher.CheckAccess())
            {
                _player.Dispatcher.BeginInvoke(new Action(TogglePlayPause));
                return;
            }

            if (_isLoading) return;

            if (_currentIndex < 0 && _queue.Count > 0)
            {
                PlayIndex(0);
                return;
            }

            if (CurrentTrack == null) return;

            try
            {
                if (_isPlaying)
                {
                    _player.Pause();
                    _isPlaying = false;
                    EmitStatus("🎵 Pausado: " + CurrentTrack.Title);
                }
                else
                {
                    ApplyAudioSettingsToPlayer();
                    _player.Play();
                    _isPlaying = true;
                    EmitStatus("🎵 Tocando: " + CurrentTrack.Title);
                }
                if (PlaybackStateChanged != null) PlaybackStateChanged(_isPlaying, _isLoading);
            }
            catch { }
        }

        public void Stop()
        {
            if (_player != null && _player.Dispatcher != null && !_player.Dispatcher.CheckAccess())
            {
                _player.Dispatcher.BeginInvoke(new Action(Stop));
                return;
            }

            if (_loadCts != null) _loadCts.Cancel();
            try
            {
                _player.Stop();
                _player.Close();
            }
            catch { }
            _isPlaying = false;
            _isLoading = false;
            if (PlaybackStateChanged != null) PlaybackStateChanged(false, false);
            MemoryOptimizer.TrimMemory();
        }

        public void PlayNext()
        {
            if (_player != null && _player.Dispatcher != null && !_player.Dispatcher.CheckAccess())
            {
                _player.Dispatcher.BeginInvoke(new Action(PlayNext));
                return;
            }

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
            if (_player != null && _player.Dispatcher != null && !_player.Dispatcher.CheckAccess())
            {
                _player.Dispatcher.BeginInvoke(new Action(PlayPrevious));
                return;
            }

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

        private void OnMediaOpened(object sender, EventArgs e)
        {
            ApplyAudioSettingsToPlayer();
            MemoryOptimizer.TrimMemory();
        }

        private void OnMediaEnded(object sender, EventArgs e)
        {
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
                _player.Play();
                return;
            }
            PlayNext();
        }

        private void OnMediaFailed(object sender, ExceptionEventArgs e)
        {
            string err = (e != null && e.ErrorException != null) ? e.ErrorException.Message : "Erro desconhecido";
            EmitStatus("⚠️ Erro de mídia (" + err + "), pulando para próxima...");
            _consecutiveFailures++;
            if (_consecutiveFailures < 4 && _queue.Count > 1)
            {
                System.Threading.Tasks.Task.Run(async () =>
                {
                    await System.Threading.Tasks.Task.Delay(400);
                    SafeDispatch(PlayNext);
                });
            }
            else
            {
                _consecutiveFailures = 0;
                _isLoading = false;
                _isPlaying = false;
                if (PlaybackStateChanged != null) PlaybackStateChanged(false, false);
            }
        }

        private void EmitStatus(string msg)
        {
            if (StatusMessage != null) StatusMessage(msg);
        }

        public void Dispose()
        {
            Stop();
        }
    }
}
