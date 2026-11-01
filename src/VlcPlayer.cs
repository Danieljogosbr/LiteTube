using System;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading.Tasks;

namespace LiteTube
{
    public class VlcPlayer : IDisposable
    {
        #region LibVLC P/Invoke

        [DllImport("kernel32.dll", SetLastError = true, CharSet = CharSet.Auto)]
        private static extern bool SetDllDirectory(string lpPathName);

        [DllImport("libvlc.dll", CallingConvention = CallingConvention.Cdecl)]
        private static extern IntPtr libvlc_new(int argc, IntPtr argv);

        [DllImport("libvlc.dll", CallingConvention = CallingConvention.Cdecl)]
        private static extern void libvlc_release(IntPtr p_instance);

        [DllImport("libvlc.dll", CallingConvention = CallingConvention.Cdecl)]
        private static extern IntPtr libvlc_media_player_new(IntPtr p_instance);

        [DllImport("libvlc.dll", CallingConvention = CallingConvention.Cdecl)]
        private static extern void libvlc_media_player_release(IntPtr p_mi);

        [DllImport("libvlc.dll", CallingConvention = CallingConvention.Cdecl)]
        private static extern void libvlc_media_player_set_hwnd(IntPtr p_mi, IntPtr drawable);

        [DllImport("libvlc.dll", CallingConvention = CallingConvention.Cdecl)]
        private static extern IntPtr libvlc_media_new_location(IntPtr p_instance, [MarshalAs(UnmanagedType.LPStr)] string psz_mrl);

        [DllImport("libvlc.dll", CallingConvention = CallingConvention.Cdecl)]
        private static extern IntPtr libvlc_media_new_path(IntPtr p_instance, [MarshalAs(UnmanagedType.LPStr)] string psz_path);

        [DllImport("libvlc.dll", CallingConvention = CallingConvention.Cdecl)]
        private static extern void libvlc_media_release(IntPtr p_md);

        [DllImport("libvlc.dll", CallingConvention = CallingConvention.Cdecl)]
        private static extern void libvlc_media_player_set_media(IntPtr p_mi, IntPtr p_md);

        [DllImport("libvlc.dll", CallingConvention = CallingConvention.Cdecl)]
        private static extern int libvlc_media_player_add_slave(IntPtr p_mi, int i_type, [MarshalAs(UnmanagedType.LPStr)] string psz_uri, bool b_select);

        [DllImport("libvlc.dll", CallingConvention = CallingConvention.Cdecl)]
        private static extern void libvlc_media_add_option(IntPtr p_md, [MarshalAs(UnmanagedType.LPStr)] string psz_options);

        [DllImport("libvlc.dll", CallingConvention = CallingConvention.Cdecl)]
        private static extern int libvlc_media_player_play(IntPtr p_mi);

        [DllImport("libvlc.dll", CallingConvention = CallingConvention.Cdecl)]
        private static extern void libvlc_media_player_pause(IntPtr p_mi);

        [DllImport("libvlc.dll", CallingConvention = CallingConvention.Cdecl)]
        private static extern void libvlc_media_player_stop(IntPtr p_mi);

        [DllImport("libvlc.dll", CallingConvention = CallingConvention.Cdecl)]
        private static extern int libvlc_media_player_is_playing(IntPtr p_mi);

        [DllImport("libvlc.dll", CallingConvention = CallingConvention.Cdecl)]
        private static extern int libvlc_media_player_get_state(IntPtr p_mi);

        [DllImport("libvlc.dll", CallingConvention = CallingConvention.Cdecl)]
        private static extern long libvlc_media_player_get_time(IntPtr p_mi);

        [DllImport("libvlc.dll", CallingConvention = CallingConvention.Cdecl)]
        private static extern void libvlc_media_player_set_time(IntPtr p_mi, long i_time);

        [DllImport("libvlc.dll", CallingConvention = CallingConvention.Cdecl)]
        private static extern float libvlc_media_player_get_position(IntPtr p_mi);

        [DllImport("libvlc.dll", CallingConvention = CallingConvention.Cdecl)]
        private static extern void libvlc_media_player_set_position(IntPtr p_mi, float f_pos);

        [DllImport("libvlc.dll", CallingConvention = CallingConvention.Cdecl)]
        private static extern int libvlc_media_player_is_seekable(IntPtr p_mi);

        [DllImport("libvlc.dll", CallingConvention = CallingConvention.Cdecl)]
        private static extern long libvlc_media_player_get_length(IntPtr p_mi);

        [DllImport("libvlc.dll", CallingConvention = CallingConvention.Cdecl)]
        private static extern int libvlc_audio_get_volume(IntPtr p_mi);

        [DllImport("libvlc.dll", CallingConvention = CallingConvention.Cdecl)]
        private static extern int libvlc_audio_set_volume(IntPtr p_mi, int i_volume);

        [DllImport("libvlc.dll", CallingConvention = CallingConvention.Cdecl)]
        private static extern int libvlc_audio_get_mute(IntPtr p_mi);

        [DllImport("libvlc.dll", CallingConvention = CallingConvention.Cdecl)]
        private static extern void libvlc_audio_set_mute(IntPtr p_mi, int status);

        [DllImport("libvlc.dll", CallingConvention = CallingConvention.Cdecl)]
        private static extern int libvlc_audio_toggle_mute(IntPtr p_mi);

        [DllImport("libvlc.dll", CallingConvention = CallingConvention.Cdecl)]
        private static extern float libvlc_media_player_get_rate(IntPtr p_mi);

        [DllImport("libvlc.dll", CallingConvention = CallingConvention.Cdecl)]
        private static extern int libvlc_media_player_set_rate(IntPtr p_mi, float rate);

        [DllImport("libvlc.dll", CallingConvention = CallingConvention.Cdecl)]
        private static extern IntPtr libvlc_get_version();

        #endregion

        private static IntPtr _vlcInstance = IntPtr.Zero;
        private static readonly object _initLock = new object();

        private IntPtr _mediaPlayer = IntPtr.Zero;
        private IntPtr _currentMedia = IntPtr.Zero;
        private IntPtr _hwnd = IntPtr.Zero;
        private bool _isDisposed = false;
        private double _knownDurationSeconds = 0;
        private bool _isMuted = false;

        private string _lastVideoUrl = null;
        private string _lastAudioUrl = null;
        private readonly System.Diagnostics.Stopwatch _stopwatch = new System.Diagnostics.Stopwatch();
        private double _stopwatchBaseSeconds = 0.0;
        private System.Threading.Timer _stateMonitorTimer;
        private int _lastObservedState = 0;

        public event Action OnPlaying;
        public event Action OnPaused;
        public event Action OnStopped;
        public event Action OnEndReached;

        public static bool EnsureInitialized()
        {
            lock (_initLock)
            {
                if (_vlcInstance != IntPtr.Zero) return true;
                try
                {
                    string baseDir = AppDomain.CurrentDomain.BaseDirectory;
                    SetDllDirectory(baseDir);
                    Environment.SetEnvironmentVariable("VLC_PLUGIN_PATH", Path.Combine(baseDir, "plugins"));

                    string[] vlcArgs = new string[] {
                        "--no-video-title-show",
                        "--network-caching=2500",
                        "--no-osd",
                        "--no-snapshot-preview",
                        "--avcodec-hw=any",
                        "--http-reconnect",
                        "--no-mouse-events"
                    };

                    IntPtr[] ptrs = new IntPtr[vlcArgs.Length];
                    for (int i = 0; i < vlcArgs.Length; i++)
                    {
                        byte[] bytes = Encoding.UTF8.GetBytes(vlcArgs[i] + "\0");
                        ptrs[i] = Marshal.AllocHGlobal(bytes.Length);
                        Marshal.Copy(bytes, 0, ptrs[i], bytes.Length);
                    }
                    GCHandle handle = GCHandle.Alloc(ptrs, GCHandleType.Pinned);
                    try
                    {
                        _vlcInstance = libvlc_new(vlcArgs.Length, handle.AddrOfPinnedObject());
                    }
                    finally
                    {
                        handle.Free();
                        for (int i = 0; i < ptrs.Length; i++) Marshal.FreeHGlobal(ptrs[i]);
                    }

                    if (_vlcInstance != IntPtr.Zero)
                    {
                        string ver = Marshal.PtrToStringAnsi(libvlc_get_version());
                        Logger.Info("VLC", "Motor LibVLC inicializado com sucesso: " + ver);
                        return true;
                    }
                }
                catch (Exception ex)
                {
                    Logger.Error("VLC", "Falha ao inicializar LibVLC", ex);
                }
                return false;
            }
        }

        public static Task<bool> EnsureInitializedAsync()
        {
            return Task.Run(() => EnsureInitialized());
        }

        public VlcPlayer(IntPtr windowHandle)
        {
            _hwnd = windowHandle;
            EnsureInitialized();
            if (_vlcInstance != IntPtr.Zero)
            {
                _mediaPlayer = libvlc_media_player_new(_vlcInstance);
                if (_mediaPlayer != IntPtr.Zero && _hwnd != IntPtr.Zero)
                {
                    libvlc_media_player_set_hwnd(_mediaPlayer, _hwnd);
                }
            }
            _stateMonitorTimer = new System.Threading.Timer(CheckPlayerStateTick, null, 200, 200);
        }

        public static int MapLinearPercentToVlcVolume(int linearPercent, bool isMuted)
        {
            if (isMuted || linearPercent <= 0) return 0;
            if (linearPercent >= 100) return 100;
            double normalized = linearPercent / 100.0;
            // Compensa a curva cúbica interna do módulo mmdevice da LibVLC: (x^(1/3))^3 = x
            int vlcVol = (int)Math.Round(Math.Pow(normalized, 1.0 / 3.0) * 100.0);
            return Math.Max(0, Math.Min(100, vlcVol));
        }

        private void ApplyVolumeToNative()
        {
            if (_mediaPlayer == IntPtr.Zero) return;
            try
            {
                libvlc_audio_set_mute(_mediaPlayer, _isMuted ? 1 : 0);
                int safeVol = MapLinearPercentToVlcVolume(_cachedVolume, _isMuted);
                libvlc_audio_set_volume(_mediaPlayer, safeVol);
            }
            catch { }
        }

        private void CheckPlayerStateTick(object state)
        {
            if (_isDisposed || _mediaPlayer == IntPtr.Zero) return;
            try
            {
                int st = libvlc_media_player_get_state(_mediaPlayer);
                if (st != _lastObservedState)
                {
                    _lastObservedState = st;
                    if (st == 6) // libvlc_Ended
                    {
                        var handler = OnEndReached;
                        if (handler != null) handler();
                    }
                    else if (st == 3) // libvlc_Playing: o dispositivo aout acabou de ser criado e aberto
                    {
                        ApplyVolumeToNative();
                        var handler = OnPlaying;
                        if (handler != null) handler();
                    }
                    else if (st == 4) // libvlc_Paused
                    {
                        var handler = OnPaused;
                        if (handler != null) handler();
                    }
                    else if (st == 5) // libvlc_Stopped
                    {
                        var handler = OnStopped;
                        if (handler != null) handler();
                    }
                }
            }
            catch { }
        }

        public void SetHwnd(IntPtr hwnd)
        {
            _hwnd = hwnd;
            if (_mediaPlayer != IntPtr.Zero && _hwnd != IntPtr.Zero)
            {
                libvlc_media_player_set_hwnd(_mediaPlayer, _hwnd);
            }
        }

        public void PlayStream(string videoUrl, string audioUrl, double durationSeconds, double startSeconds = 0)
        {
            _lastVideoUrl = videoUrl;
            _lastAudioUrl = audioUrl;
            _knownDurationSeconds = durationSeconds;
            _stopwatchBaseSeconds = startSeconds;
            _stopwatch.Restart();

            int safeVol = Math.Max(0, Math.Min(100, _isMuted ? 0 : _cachedVolume));
            _cachedVolume = safeVol;

            PlayStreamInternal(videoUrl, audioUrl, durationSeconds, startSeconds);
        }

        private void PlayStreamInternal(string videoUrl, string audioUrl, double durationSeconds, double startSeconds)
        {
            if (_mediaPlayer == IntPtr.Zero || _vlcInstance == IntPtr.Zero) return;

            StopInternal();
            _lastObservedState = 0;

            _stopwatchBaseSeconds = startSeconds;
            _stopwatch.Restart();

            try
            {
                // Create media for primary video stream
                IntPtr media;
                if (videoUrl.StartsWith("http://", StringComparison.OrdinalIgnoreCase) ||
                    videoUrl.StartsWith("https://", StringComparison.OrdinalIgnoreCase))
                {
                    media = libvlc_media_new_location(_vlcInstance, videoUrl);
                }
                else
                {
                    media = libvlc_media_new_path(_vlcInstance, videoUrl);
                }

                if (media == IntPtr.Zero)
                {
                    Logger.Error("VLC", "Falha ao criar libvlc_media para: " + videoUrl, null);
                    return;
                }

                // Optimization options
                libvlc_media_add_option(media, ":network-caching=2500");
                libvlc_media_add_option(media, ":clock-jitter=0");

                if (startSeconds > 0.5)
                {
                    string startOpt = string.Format(System.Globalization.CultureInfo.InvariantCulture, ":start-time={0:0.00}", startSeconds);
                    libvlc_media_add_option(media, startOpt);
                }

                _currentMedia = media;
                libvlc_media_player_set_media(_mediaPlayer, media);

                // Add slave audio stream if separate
                if (!string.IsNullOrEmpty(audioUrl) && !string.Equals(audioUrl, videoUrl, StringComparison.OrdinalIgnoreCase))
                {
                    // 1 = libvlc_media_slave_type_audio
                    int res = libvlc_media_player_add_slave(_mediaPlayer, 1, audioUrl, true);
                    Logger.Info("VLC", string.Format("Slave audio track adicionado: ok={0}, url={1}", res == 0, audioUrl));
                }

                libvlc_media_player_play(_mediaPlayer);
                ApplyVolumeToNative();

                Task.Run(async () =>
                {
                    for (int i = 0; i < 10; i++)
                    {
                        await Task.Delay(100);
                        if (_mediaPlayer != IntPtr.Zero && IsPlaying)
                        {
                            ApplyVolumeToNative();
                            break;
                        }
                    }
                });

                Logger.Info("VLC", string.Format("PlayStream iniciado: dur={0:0.0}s, start={1:0.0}s, vol={2}%", durationSeconds, startSeconds, _cachedVolume));
            }
            catch (Exception ex)
            {
                Logger.Error("VLC", "Erro ao iniciar PlayStream", ex);
            }
        }

        public int State
        {
            get
            {
                if (_mediaPlayer == IntPtr.Zero) return 0;
                try { return libvlc_media_player_get_state(_mediaPlayer); } catch { return 0; }
            }
        }

        public bool HasEnded
        {
            get
            {
                return State == 6; // libvlc_Ended
            }
        }

        public void Play()
        {
            if (_mediaPlayer != IntPtr.Zero)
            {
                int st = State;
                if ((st == 6 || st == 5) && !string.IsNullOrEmpty(_lastVideoUrl))
                {
                    PlayStreamInternal(_lastVideoUrl, _lastAudioUrl, _knownDurationSeconds, 0);
                    return;
                }

                _stopwatch.Start();
                libvlc_media_player_play(_mediaPlayer);
                ApplyVolumeToNative();

                Task.Run(async () =>
                {
                    for (int i = 0; i < 10; i++)
                    {
                        await Task.Delay(100);
                        if (_mediaPlayer != IntPtr.Zero && IsPlaying)
                        {
                            ApplyVolumeToNative();
                            break;
                        }
                    }
                });
            }
        }

        public void Pause()
        {
            if (_mediaPlayer != IntPtr.Zero)
            {
                _stopwatch.Stop();
                libvlc_media_player_pause(_mediaPlayer);
            }
        }

        public void Stop()
        {
            _stopwatch.Reset();
            _stopwatchBaseSeconds = 0.0;
            _lastVideoUrl = null;
            _lastAudioUrl = null;
            StopInternal();
            _lastObservedState = 0;
        }

        private void StopInternal()
        {
            if (_mediaPlayer != IntPtr.Zero)
            {
                try { libvlc_media_player_stop(_mediaPlayer); } catch { }
            }
            if (_currentMedia != IntPtr.Zero)
            {
                try { libvlc_media_release(_currentMedia); } catch { }
                _currentMedia = IntPtr.Zero;
            }
        }

        public void Seek(double seconds)
        {
            if (_mediaPlayer == IntPtr.Zero) return;
            try
            {
                seconds = Math.Max(0.0, seconds);
                long ms = (long)(seconds * 1000.0);

                int st = State;
                if ((st == 6 || st == 5) && !string.IsNullOrEmpty(_lastVideoUrl))
                {
                    _stopwatchBaseSeconds = seconds;
                    PlayStreamInternal(_lastVideoUrl, _lastAudioUrl, _knownDurationSeconds, seconds);
                    return;
                }

                _stopwatchBaseSeconds = seconds;
                if (IsPlaying) _stopwatch.Restart(); else _stopwatch.Reset();

                // Native in-place LibVLC set_position and set_time (keeps Direct3D HWND swapchain alive)
                if (_knownDurationSeconds > 0)
                {
                    float pos = (float)(seconds / _knownDurationSeconds);
                    libvlc_media_player_set_position(_mediaPlayer, Math.Max(0.0f, Math.Min(0.999f, pos)));
                }
                libvlc_media_player_set_time(_mediaPlayer, ms);

                Logger.Info("VLC", string.Format("Seek realizado para {0:0.0}s (ms={1})", seconds, ms));
            }
            catch (Exception ex)
            {
                Logger.Error("VLC", "Erro no Seek", ex);
            }
        }

        public double CurrentTimeSeconds
        {
            get
            {
                if (_mediaPlayer == IntPtr.Zero) return _stopwatchBaseSeconds;
                try
                {
                    if (HasEnded && _knownDurationSeconds > 0)
                    {
                        return _knownDurationSeconds;
                    }

                    long ms = libvlc_media_player_get_time(_mediaPlayer);
                    if (ms >= 0)
                    {
                        double sec = ms / 1000.0;
                        _stopwatchBaseSeconds = sec;
                        _stopwatch.Restart();
                        return sec;
                    }

                    float pos = libvlc_media_player_get_position(_mediaPlayer);
                    if (pos >= 0.0f && _knownDurationSeconds > 0)
                    {
                        double sec = pos * _knownDurationSeconds;
                        _stopwatchBaseSeconds = sec;
                        _stopwatch.Restart();
                        return sec;
                    }

                    if (IsPlaying)
                    {
                        return _stopwatchBaseSeconds + _stopwatch.Elapsed.TotalSeconds;
                    }

                    return _stopwatchBaseSeconds;
                }
                catch
                {
                    return _stopwatchBaseSeconds;
                }
            }
        }

        public double DurationSeconds
        {
            get
            {
                if (_mediaPlayer != IntPtr.Zero)
                {
                    try
                    {
                        long len = libvlc_media_player_get_length(_mediaPlayer);
                        if (len > 0) return len / 1000.0;
                    }
                    catch { }
                }
                return _knownDurationSeconds;
            }
        }

        public bool IsPlaying
        {
            get
            {
                if (_mediaPlayer == IntPtr.Zero) return false;
                try
                {
                    return libvlc_media_player_is_playing(_mediaPlayer) == 1;
                }
                catch { return false; }
            }
        }

        private int _cachedVolume = 70;

        public int Volume
        {
            get
            {
                return _cachedVolume;
            }
            set
            {
                _cachedVolume = Math.Max(0, Math.Min(100, value));
                if (_cachedVolume > 0) _isMuted = false;
                ApplyVolumeToNative();
            }
        }

        public void SetVolume(double volume0to1)
        {
            int safeVolume = Math.Max(0, Math.Min(100, (int)Math.Round(volume0to1 * 100.0)));
            Volume = safeVolume;
        }

        public bool IsMuted
        {
            get
            {
                if (_mediaPlayer == IntPtr.Zero) return _isMuted;
                try
                {
                    int m = libvlc_audio_get_mute(_mediaPlayer);
                    if (m >= 0) _isMuted = (m == 1);
                    return _isMuted;
                }
                catch { return _isMuted; }
            }
            set
            {
                _isMuted = value;
                ApplyVolumeToNative();
            }
        }

        public float SpeedRatio
        {
            get
            {
                if (_mediaPlayer == IntPtr.Zero) return 1.0f;
                try { return libvlc_media_player_get_rate(_mediaPlayer); } catch { return 1.0f; }
            }
            set
            {
                if (_mediaPlayer == IntPtr.Zero) return;
                try { libvlc_media_player_set_rate(_mediaPlayer, Math.Max(0.25f, Math.Min(3.0f, value))); } catch { }
            }
        }

        public void Dispose()
        {
            if (_isDisposed) return;
            _isDisposed = true;
            if (_stateMonitorTimer != null)
            {
                try { _stateMonitorTimer.Dispose(); } catch { }
                _stateMonitorTimer = null;
            }
            Stop();
            if (_mediaPlayer != IntPtr.Zero)
            {
                try { libvlc_media_player_release(_mediaPlayer); } catch { }
                _mediaPlayer = IntPtr.Zero;
            }
        }
    }
}
