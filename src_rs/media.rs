// ============================================================================
// LiteTube 2.0 (Rust) — Camada 2: Engine de Mídia / Player (LibVLC FFI Nativo)
// - Áudio DirectSound por instância (NÃO sequestra o Mixer de Volume do Windows!)
// - Independência total entre Volume de Vídeo e Volume de Música
// - Curva linear de amplitude compatível com FlyleafLib / XAudio2 (compensação cbrt)
// - Sincronização automática de volume na abertura do stream (zero pico de 100%)
// - Suporte a DASH Dual-Stream (Vídeo + Áudio separado via libvlc_media_slaves_add)
// ============================================================================

#![allow(dead_code)]

use crate::memory::{log_error, log_info};
use crate::models::get_base_dir;
use std::ffi::{CStr, CString, c_char, c_float, c_int, c_uint, c_void};
use std::path::PathBuf;
use std::ptr;
use std::sync::{
    Arc, Mutex, OnceLock,
    atomic::{AtomicBool, AtomicI32, AtomicU64, Ordering},
};
use std::time::Duration;
use windows::Win32::Foundation::HMODULE;
use windows::Win32::System::LibraryLoader::{GetProcAddress, LoadLibraryW};
use windows::core::{PCSTR, PCWSTR};

type LibVlcInstance = c_void;
type LibVlcMediaPlayer = c_void;
type LibVlcMedia = c_void;

#[repr(C)]
struct LibVlcAudioOutputDevice {
    psz_device: *mut c_char,
    psz_description: *mut c_char,
    p_next: *mut LibVlcAudioOutputDevice,
}

type FnLibVlcNew =
    unsafe extern "C" fn(argc: c_int, argv: *const *const c_char) -> *mut LibVlcInstance;
type FnLibVlcRelease = unsafe extern "C" fn(inst: *mut LibVlcInstance);
type FnMediaNewLocation =
    unsafe extern "C" fn(inst: *mut LibVlcInstance, mrl: *const c_char) -> *mut LibVlcMedia;
type FnMediaNewPath =
    unsafe extern "C" fn(inst: *mut LibVlcInstance, path: *const c_char) -> *mut LibVlcMedia;
type FnMediaAddOption = unsafe extern "C" fn(md: *mut LibVlcMedia, options: *const c_char);
type FnMediaSlavesAdd = unsafe extern "C" fn(
    md: *mut LibVlcMedia,
    slave_type: c_int,
    priority: c_uint,
    uri: *const c_char,
) -> c_int;
type FnMediaRelease = unsafe extern "C" fn(md: *mut LibVlcMedia);

type FnPlayerNew = unsafe extern "C" fn(inst: *mut LibVlcInstance) -> *mut LibVlcMediaPlayer;
type FnPlayerRelease = unsafe extern "C" fn(mp: *mut LibVlcMediaPlayer);
type FnPlayerSetMedia = unsafe extern "C" fn(mp: *mut LibVlcMediaPlayer, md: *mut LibVlcMedia);
type FnPlayerPlay = unsafe extern "C" fn(mp: *mut LibVlcMediaPlayer) -> c_int;
type FnPlayerSetPause = unsafe extern "C" fn(mp: *mut LibVlcMediaPlayer, do_pause: c_int);
type FnPlayerStop = unsafe extern "C" fn(mp: *mut LibVlcMediaPlayer);
type FnPlayerIsPlaying = unsafe extern "C" fn(mp: *mut LibVlcMediaPlayer) -> c_int;
type FnPlayerGetState = unsafe extern "C" fn(mp: *mut LibVlcMediaPlayer) -> c_int;
type FnPlayerGetTime = unsafe extern "C" fn(mp: *mut LibVlcMediaPlayer) -> i64;
type FnPlayerSetTime = unsafe extern "C" fn(mp: *mut LibVlcMediaPlayer, time_ms: i64);
type FnPlayerGetLength = unsafe extern "C" fn(mp: *mut LibVlcMediaPlayer) -> i64;
type FnPlayerSetRate = unsafe extern "C" fn(mp: *mut LibVlcMediaPlayer, rate: c_float) -> c_int;
type FnPlayerGetRate = unsafe extern "C" fn(mp: *mut LibVlcMediaPlayer) -> c_float;
type FnAudioSetVolume = unsafe extern "C" fn(mp: *mut LibVlcMediaPlayer, volume: c_int) -> c_int;
type FnAudioGetVolume = unsafe extern "C" fn(mp: *mut LibVlcMediaPlayer) -> c_int;
type FnAudioSetMute = unsafe extern "C" fn(mp: *mut LibVlcMediaPlayer, status: c_int);
type FnAudioGetMute = unsafe extern "C" fn(mp: *mut LibVlcMediaPlayer) -> c_int;
type FnAudioOutputSet =
    unsafe extern "C" fn(mp: *mut LibVlcMediaPlayer, name: *const c_char) -> c_int;
type FnAudioDeviceEnum =
    unsafe extern "C" fn(mp: *mut LibVlcMediaPlayer) -> *mut LibVlcAudioOutputDevice;
type FnAudioDeviceListRelease = unsafe extern "C" fn(list: *mut LibVlcAudioOutputDevice);
type FnAudioDeviceSet = unsafe extern "C" fn(
    mp: *mut LibVlcMediaPlayer,
    module: *const c_char,
    device_id: *const c_char,
);

type VideoLockCb = unsafe extern "C" fn(opaque: *mut c_void, planes: *mut *mut c_void) -> *mut c_void;
type VideoUnlockCb = unsafe extern "C" fn(
    opaque: *mut c_void,
    picture: *mut c_void,
    planes: *const *mut c_void,
);
type VideoDisplayCb = unsafe extern "C" fn(opaque: *mut c_void, picture: *mut c_void);
type VideoFormatCb = unsafe extern "C" fn(
    opaque: *mut *mut c_void,
    chroma: *mut c_char,
    width: *mut c_uint,
    height: *mut c_uint,
    pitches: *mut c_uint,
    lines: *mut c_uint,
) -> c_uint;
type VideoCleanupCb = unsafe extern "C" fn(opaque: *mut c_void);

type FnVideoSetCallbacks = unsafe extern "C" fn(
    mp: *mut LibVlcMediaPlayer,
    lock: Option<VideoLockCb>,
    unlock: Option<VideoUnlockCb>,
    display: Option<VideoDisplayCb>,
    opaque: *mut c_void,
);
type FnVideoSetFormatCallbacks = unsafe extern "C" fn(
    mp: *mut LibVlcMediaPlayer,
    setup: Option<VideoFormatCb>,
    cleanup: Option<VideoCleanupCb>,
);

struct LibVlcApi {
    libvlc_new: FnLibVlcNew,
    libvlc_release: FnLibVlcRelease,
    media_new_location: FnMediaNewLocation,
    media_new_path: FnMediaNewPath,
    media_add_option: FnMediaAddOption,
    media_slaves_add: FnMediaSlavesAdd,
    media_release: FnMediaRelease,
    player_new: FnPlayerNew,
    player_release: FnPlayerRelease,
    player_set_media: FnPlayerSetMedia,
    player_play: FnPlayerPlay,
    player_set_pause: FnPlayerSetPause,
    player_stop: FnPlayerStop,
    player_is_playing: FnPlayerIsPlaying,
    player_get_state: FnPlayerGetState,
    player_get_time: FnPlayerGetTime,
    player_set_time: FnPlayerSetTime,
    player_get_length: FnPlayerGetLength,
    player_set_rate: FnPlayerSetRate,
    player_get_rate: FnPlayerGetRate,
    audio_set_volume: FnAudioSetVolume,
    audio_get_volume: FnAudioGetVolume,
    audio_set_mute: FnAudioSetMute,
    audio_get_mute: FnAudioGetMute,
    audio_output_set: Option<FnAudioOutputSet>,
    audio_device_enum: Option<FnAudioDeviceEnum>,
    audio_device_list_release: Option<FnAudioDeviceListRelease>,
    audio_device_set: Option<FnAudioDeviceSet>,
    video_set_callbacks: FnVideoSetCallbacks,
    video_set_format_callbacks: FnVideoSetFormatCallbacks,
    instance: *mut LibVlcInstance,
}

unsafe impl Send for LibVlcApi {}
unsafe impl Sync for LibVlcApi {}

static VLC_API: OnceLock<Option<Arc<LibVlcApi>>> = OnceLock::new();

fn to_wide_null(s: &str) -> Vec<u16> {
    s.encode_utf16().chain(std::iter::once(0)).collect()
}

unsafe fn get_proc<T>(module: HMODULE, name: &[u8]) -> Option<T> {
    let addr = unsafe { GetProcAddress(module, PCSTR(name.as_ptr()))? };
    Some(unsafe { std::mem::transmute_copy(&addr) })
}

/// Converte o volume linear do FlyleafLib / XAudio2 (0.0 .. 1.0) para a escala
/// cúbica interna do LibVLC (0 .. 100), preservando o volume perceptível idêntico!
/// Exemplo: 0.04 (4% linear) -> cbrt(0.04)*100 = 34 (no VLC: 0.34^3 = 0.0393 ≈ 4% linear).
///          0.41 (41% linear) -> cbrt(0.41)*100 = 74 (no VLC: 0.74^3 = 0.405 ≈ 41% linear).
pub fn linear_to_vlc_volume(vol_0_to_1: f64, is_muted: bool) -> i32 {
    if is_muted || vol_0_to_1 <= 0.001 {
        return 0;
    }
    let clamped = vol_0_to_1.clamp(0.0, 1.0);
    (clamped.cbrt() * 100.0).round().clamp(1.0, 100.0) as i32
}

fn load_libvlc_api() -> Option<Arc<LibVlcApi>> {
    VLC_API
        .get_or_init(|| unsafe {
            let base = get_base_dir();
            let candidates = [
                base.clone(),
                PathBuf::from(r"C:\Users\azeve\.gemini\antigravity\scratch\vlc_test"),
            ];

            let mut vlc_dir = base.clone();
            for dir in &candidates {
                if dir.join("libvlc.dll").exists() && dir.join("libvlccore.dll").exists() {
                    vlc_dir = dir.clone();
                    break;
                }
            }

            let core_path = vlc_dir.join("libvlccore.dll");
            let vlc_path = vlc_dir.join("libvlc.dll");
            let plugins_dir = vlc_dir.join("plugins");

            if !vlc_path.exists() {
                log_error("VLC", &format!("libvlc.dll não encontrado em {}", vlc_path.display()));
                return None;
            }

            std::env::set_var("VLC_PLUGIN_PATH", &plugins_dir);

            let core_w = to_wide_null(&core_path.to_string_lossy());
            let _ = LoadLibraryW(PCWSTR(core_w.as_ptr()));

            let vlc_w = to_wide_null(&vlc_path.to_string_lossy());
            let h_vlc = match LoadLibraryW(PCWSTR(vlc_w.as_ptr())) {
                Ok(h) => h,
                Err(e) => {
                    log_error("VLC", &format!("Falha ao carregar libvlc.dll: {}", e));
                    return None;
                }
            };

            let libvlc_new: FnLibVlcNew = get_proc(h_vlc, b"libvlc_new\0")?;
            let libvlc_release: FnLibVlcRelease = get_proc(h_vlc, b"libvlc_release\0")?;
            let media_new_location: FnMediaNewLocation =
                get_proc(h_vlc, b"libvlc_media_new_location\0")?;
            let media_new_path: FnMediaNewPath = get_proc(h_vlc, b"libvlc_media_new_path\0")?;
            let media_add_option: FnMediaAddOption = get_proc(h_vlc, b"libvlc_media_add_option\0")?;
            let media_slaves_add: FnMediaSlavesAdd = get_proc(h_vlc, b"libvlc_media_slaves_add\0")?;
            let media_release: FnMediaRelease = get_proc(h_vlc, b"libvlc_media_release\0")?;
            let player_new: FnPlayerNew = get_proc(h_vlc, b"libvlc_media_player_new\0")?;
            let player_release: FnPlayerRelease =
                get_proc(h_vlc, b"libvlc_media_player_release\0")?;
            let player_set_media: FnPlayerSetMedia =
                get_proc(h_vlc, b"libvlc_media_player_set_media\0")?;
            let player_play: FnPlayerPlay = get_proc(h_vlc, b"libvlc_media_player_play\0")?;
            let player_set_pause: FnPlayerSetPause =
                get_proc(h_vlc, b"libvlc_media_player_set_pause\0")?;
            let player_stop: FnPlayerStop = get_proc(h_vlc, b"libvlc_media_player_stop\0")?;
            let player_is_playing: FnPlayerIsPlaying =
                get_proc(h_vlc, b"libvlc_media_player_is_playing\0")?;
            let player_get_state: FnPlayerGetState =
                get_proc(h_vlc, b"libvlc_media_player_get_state\0")?;
            let player_get_time: FnPlayerGetTime =
                get_proc(h_vlc, b"libvlc_media_player_get_time\0")?;
            let player_set_time: FnPlayerSetTime =
                get_proc(h_vlc, b"libvlc_media_player_set_time\0")?;
            let player_get_length: FnPlayerGetLength =
                get_proc(h_vlc, b"libvlc_media_player_get_length\0")?;
            let player_set_rate: FnPlayerSetRate =
                get_proc(h_vlc, b"libvlc_media_player_set_rate\0")?;
            let player_get_rate: FnPlayerGetRate =
                get_proc(h_vlc, b"libvlc_media_player_get_rate\0")?;
            let audio_set_volume: FnAudioSetVolume =
                get_proc(h_vlc, b"libvlc_audio_set_volume\0")?;
            let audio_get_volume: FnAudioGetVolume =
                get_proc(h_vlc, b"libvlc_audio_get_volume\0")?;
            let audio_set_mute: FnAudioSetMute = get_proc(h_vlc, b"libvlc_audio_set_mute\0")?;
            let audio_get_mute: FnAudioGetMute = get_proc(h_vlc, b"libvlc_audio_get_mute\0")?;
            let audio_output_set: Option<FnAudioOutputSet> =
                get_proc(h_vlc, b"libvlc_audio_output_set\0");
            let audio_device_enum: Option<FnAudioDeviceEnum> =
                get_proc(h_vlc, b"libvlc_audio_output_device_enum\0");
            let audio_device_list_release: Option<FnAudioDeviceListRelease> =
                get_proc(h_vlc, b"libvlc_audio_output_device_list_release\0");
            let audio_device_set: Option<FnAudioDeviceSet> =
                get_proc(h_vlc, b"libvlc_audio_output_device_set\0");
            let video_set_callbacks: FnVideoSetCallbacks =
                get_proc(h_vlc, b"libvlc_video_set_callbacks\0")?;
            let video_set_format_callbacks: FnVideoSetFormatCallbacks =
                get_proc(h_vlc, b"libvlc_video_set_format_callbacks\0")?;

            // CRÍTICO: --aout=directsound + --no-volume-save garante que cada player
            // (Vídeo e Música) tenha seu próprio buffer de áudio independente sem
            // alterar ou sequestrar o Mixer de Volume do Windows!
            let args_c = [
                CString::new("--aout=directsound").unwrap(),
                CString::new("--no-volume-save").unwrap(),
                CString::new("--no-video-title-show").unwrap(),
                CString::new("--no-stats").unwrap(),
                CString::new("--no-sub-autodetect-file").unwrap(),
                CString::new("--network-caching=1000").unwrap(),
                CString::new("--quiet").unwrap(),
            ];
            let argv: Vec<*const c_char> = args_c.iter().map(|s| s.as_ptr()).collect();
            let instance = libvlc_new(argv.len() as c_int, argv.as_ptr());
            if instance.is_null() {
                log_error("VLC", "libvlc_new retornou NULL.");
                return None;
            }

            log_info(
                "VLC",
                "Motor LibVLC nativo (C ABI) carregado com DirectSound independente por player!",
            );

            Some(Arc::new(LibVlcApi {
                libvlc_new,
                libvlc_release,
                media_new_location,
                media_new_path,
                media_add_option,
                media_slaves_add,
                media_release,
                player_new,
                player_release,
                player_set_media,
                player_play,
                player_set_pause,
                player_stop,
                player_is_playing,
                player_get_state,
                player_get_time,
                player_set_time,
                player_get_length,
                player_set_rate,
                player_get_rate,
                audio_set_volume,
                audio_get_volume,
                audio_set_mute,
                audio_get_mute,
                audio_output_set,
                audio_device_enum,
                audio_device_list_release,
                audio_device_set,
                video_set_callbacks,
                video_set_format_callbacks,
                instance,
            }))
        })
        .clone()
}

// ============================================================================
// Buffer de Quadros de Vídeo Compartilhado (LibVLC vmem -> egui Texture)
// ============================================================================

pub struct SharedVideoFrameBuffer {
    pub width: u32,
    pub height: u32,
    pub back_buffer: Vec<u8>,
    pub front_rgba: Vec<u8>,
    pub frame_seq: u64,
}

impl Default for SharedVideoFrameBuffer {
    fn default() -> Self {
        Self {
            width: 0,
            height: 0,
            back_buffer: Vec::new(),
            front_rgba: Vec::new(),
            frame_seq: 0,
        }
    }
}

pub struct VideoCallbackContext {
    pub buffer: Mutex<SharedVideoFrameBuffer>,
    pub has_new_frame: AtomicBool,
    pub frame_counter: AtomicU64,
}

unsafe extern "C" fn vlc_video_format_cb(
    opaque: *mut *mut c_void,
    chroma: *mut c_char,
    width: *mut c_uint,
    height: *mut c_uint,
    pitches: *mut c_uint,
    lines: *mut c_uint,
) -> c_uint {
    unsafe {
        if opaque.is_null() || (*opaque).is_null() {
            return 0;
        }
        let ctx = &*(*opaque as *const VideoCallbackContext);
        let mut w = (*width).max(2);
        let mut h = (*height).max(2);
        if w > 1920 {
            h = (h * 1920) / w;
            w = 1920;
            *width = w;
            *height = h;
        }

        let rv32 = b"RV32";
        ptr::copy_nonoverlapping(rv32.as_ptr() as *const c_char, chroma, 4);

        let pitch = w * 4;
        *pitches = pitch;
        *lines = h;

        let total_bytes = (pitch * h) as usize;
        if let Ok(mut guard) = ctx.buffer.lock() {
            guard.width = w;
            guard.height = h;
            guard.back_buffer.resize(total_bytes, 0);
            guard.front_rgba.resize(total_bytes, 255);
        }
        1
    }
}

unsafe extern "C" fn vlc_video_cleanup_cb(_opaque: *mut c_void) {}

unsafe extern "C" fn vlc_video_lock_cb(
    opaque: *mut c_void,
    planes: *mut *mut c_void,
) -> *mut c_void {
    unsafe {
        if opaque.is_null() {
            return ptr::null_mut();
        }
        let ctx = &*(opaque as *const VideoCallbackContext);
        if let Ok(mut guard) = ctx.buffer.lock() {
            if guard.back_buffer.is_empty() {
                guard.back_buffer.resize(640 * 360 * 4, 0);
            }
            *planes = guard.back_buffer.as_mut_ptr() as *mut c_void;
        }
        ptr::null_mut()
    }
}

unsafe extern "C" fn vlc_video_unlock_cb(
    opaque: *mut c_void,
    _picture: *mut c_void,
    _planes: *const *mut c_void,
) {
    unsafe {
        if opaque.is_null() {
            return;
        }
        let ctx = &*(opaque as *const VideoCallbackContext);
        if let Ok(mut guard) = ctx.buffer.lock() {
            let len = guard.back_buffer.len();
            if len > 0 && guard.front_rgba.len() == len {
                let SharedVideoFrameBuffer {
                    ref back_buffer,
                    ref mut front_rgba,
                    ref mut frame_seq,
                    ..
                } = *guard;
                for (s, d) in back_buffer.chunks_exact(4).zip(front_rgba.chunks_exact_mut(4)) {
                    d[0] = s[2];
                    d[1] = s[1];
                    d[2] = s[0];
                    d[3] = 255;
                }
                *frame_seq = frame_seq.wrapping_add(1);
                ctx.frame_counter.store(*frame_seq, Ordering::Release);
                ctx.has_new_frame.store(true, Ordering::Release);
            }
        }
    }
}

unsafe extern "C" fn vlc_video_display_cb(_opaque: *mut c_void, _picture: *mut c_void) {}

// ============================================================================
// RustMediaPlayer — Instância de Player para Vídeo ou Áudio Headless
// ============================================================================

#[derive(Debug, Clone)]
pub struct AudioOutputDeviceInfo {
    pub id: String,
    pub description: String,
}

pub struct RustMediaPlayer {
    api: Option<Arc<LibVlcApi>>,
    mp: *mut LibVlcMediaPlayer,
    video_ctx: Option<Box<VideoCallbackContext>>,
    is_audio_only: bool,
    target_vlc_volume: AtomicI32,
    target_muted: AtomicBool,
    play_epoch: AtomicU64,
    op_lock: Mutex<()>,
}

unsafe impl Send for RustMediaPlayer {}
unsafe impl Sync for RustMediaPlayer {}

impl RustMediaPlayer {
    pub fn new(is_audio_only: bool) -> Self {
        let api = load_libvlc_api();
        let mut mp = ptr::null_mut();
        let mut video_ctx = None;

        if let Some(ref a) = api {
            unsafe {
                mp = (a.player_new)(a.instance);
                if !mp.is_null() {
                    // Força explicitamente saída DirectSound por player (isolamento Vídeo vs Música)
                    if let Some(set_aout) = a.audio_output_set {
                        if let Ok(c_ds) = CString::new("directsound") {
                            let _ = set_aout(mp, c_ds.as_ptr());
                        }
                    }

                    if !is_audio_only {
                        let mut ctx = Box::new(VideoCallbackContext {
                            buffer: Mutex::new(SharedVideoFrameBuffer::default()),
                            has_new_frame: AtomicBool::new(false),
                            frame_counter: AtomicU64::new(0),
                        });
                        let ctx_ptr = &mut *ctx as *mut VideoCallbackContext as *mut c_void;
                        (a.video_set_callbacks)(
                            mp,
                            Some(vlc_video_lock_cb),
                            Some(vlc_video_unlock_cb),
                            Some(vlc_video_display_cb),
                            ctx_ptr,
                        );
                        (a.video_set_format_callbacks)(
                            mp,
                            Some(vlc_video_format_cb),
                            Some(vlc_video_cleanup_cb),
                        );
                        video_ctx = Some(ctx);
                    }
                }
            }
        }

        Self {
            api,
            mp,
            video_ctx,
            is_audio_only,
            target_vlc_volume: AtomicI32::new(75),
            target_muted: AtomicBool::new(false),
            play_epoch: AtomicU64::new(0),
            op_lock: Mutex::new(()),
        }
    }

    pub fn is_available(&self) -> bool {
        self.api.is_some() && !self.mp.is_null()
    }

    pub fn open_and_play(
        self: &Arc<Self>,
        video_or_main_url: &str,
        separate_audio_url: Option<&str>,
        start_seconds: f64,
        volume_0_to_1: f64,
        is_muted: bool,
    ) -> bool {
        let epoch = self.play_epoch.fetch_add(1, Ordering::AcqRel) + 1;
        let vlc_vol = linear_to_vlc_volume(volume_0_to_1, is_muted);
        self.target_vlc_volume.store(vlc_vol, Ordering::Release);
        self.target_muted.store(is_muted || vlc_vol == 0, Ordering::Release);
        self.open_and_play_for_epoch(
            epoch,
            video_or_main_url,
            separate_audio_url,
            start_seconds,
            None,
        )
    }

    /// Abre e inicia a reprodução em thread dedicada, serializada por `op_lock` e
    /// cancelável via `play_epoch`, evitando qualquer travamento da UI no `player_stop` do LibVLC.
    pub fn open_and_play_async(
        self: &Arc<Self>,
        video_or_main_url: String,
        separate_audio_url: Option<String>,
        start_seconds: f64,
        volume_0_to_1: f64,
        is_muted: bool,
        playback_speed: Option<f64>,
    ) {
        let vlc_vol = linear_to_vlc_volume(volume_0_to_1, is_muted);
        self.target_vlc_volume.store(vlc_vol, Ordering::Release);
        self.target_muted.store(is_muted || vlc_vol == 0, Ordering::Release);
        let epoch = self.play_epoch.fetch_add(1, Ordering::AcqRel) + 1;
        let me = Arc::clone(self);
        std::thread::spawn(move || {
            let _ = me.open_and_play_for_epoch(
                epoch,
                &video_or_main_url,
                separate_audio_url.as_deref(),
                start_seconds,
                playback_speed,
            );
        });
    }

    fn open_and_play_for_epoch(
        self: &Arc<Self>,
        epoch: u64,
        video_or_main_url: &str,
        separate_audio_url: Option<&str>,
        start_seconds: f64,
        playback_speed: Option<f64>,
    ) -> bool {
        let Some(ref api) = self.api else {
            return false;
        };
        if self.mp.is_null() || video_or_main_url.trim().is_empty() {
            return false;
        }

        let _guard = self.op_lock.lock().unwrap_or_else(|e| e.into_inner());
        if self.play_epoch.load(Ordering::Acquire) != epoch {
            return false;
        }

        unsafe {
            (api.player_stop)(self.mp);
            if self.play_epoch.load(Ordering::Acquire) != epoch {
                return false;
            }

            let c_url = match CString::new(video_or_main_url.trim()) {
                Ok(c) => c,
                Err(_) => return false,
            };

            let md = if video_or_main_url.starts_with("http") {
                (api.media_new_location)(api.instance, c_url.as_ptr())
            } else {
                (api.media_new_path)(api.instance, c_url.as_ptr())
            };
            if md.is_null() {
                return false;
            }

            if self.is_audio_only {
                let opt_no_vid = CString::new(":no-video").unwrap();
                (api.media_add_option)(md, opt_no_vid.as_ptr());
            }

            if start_seconds > 1.0 {
                if let Ok(opt_start) = CString::new(format!(":start-time={:.1}", start_seconds)) {
                    (api.media_add_option)(md, opt_start.as_ptr());
                }
            }

            if !self.is_audio_only {
                if let Some(a_url) = separate_audio_url {
                    if !a_url.trim().is_empty() && a_url.trim() != video_or_main_url.trim() {
                        if let Ok(c_aurl) = CString::new(a_url.trim()) {
                            let _ = (api.media_slaves_add)(md, 1, 4, c_aurl.as_ptr());
                        }
                    }
                }
            }

            (api.player_set_media)(self.mp, md);
            (api.media_release)(md);

            self.apply_target_volume_now();
            let rc = (api.player_play)(self.mp);
            self.apply_target_volume_now();
            if let Some(spd) = playback_speed {
                if (spd - 1.0).abs() > 0.01 {
                    let r = spd.clamp(0.25, 2.0) as c_float;
                    let _ = (api.player_set_rate)(self.mp, r);
                }
            }

            // Garante reaplicação imediata assim que o DirectSound abrir (evita qualquer pico de 100%)
            let me = Arc::clone(self);
            std::thread::spawn(move || {
                for _ in 0..40 {
                    if me.play_epoch.load(Ordering::Acquire) != epoch {
                        break;
                    }
                    me.sync_volume_if_needed();
                    std::thread::sleep(Duration::from_millis(35));
                }
            });

            rc == 0
        }
    }

    pub fn play(&self) {
        if let Some(ref api) = self.api {
            if !self.mp.is_null() {
                unsafe {
                    let _ = (api.player_play)(self.mp);
                }
                self.apply_target_volume_now();
            }
        }
    }

    pub fn pause(&self) {
        if let Some(ref api) = self.api {
            if !self.mp.is_null() {
                unsafe {
                    (api.player_set_pause)(self.mp, 1);
                }
            }
        }
    }

    pub fn set_paused(&self, paused: bool) {
        if let Some(ref api) = self.api {
            if !self.mp.is_null() {
                unsafe {
                    (api.player_set_pause)(self.mp, if paused { 1 } else { 0 });
                }
                if !paused {
                    self.apply_target_volume_now();
                }
            }
        }
    }

    pub fn stop(&self) {
        self.play_epoch.fetch_add(1, Ordering::AcqRel);
        let _guard = self.op_lock.lock().unwrap_or_else(|e| e.into_inner());
        if let Some(ref api) = self.api {
            if !self.mp.is_null() {
                unsafe {
                    (api.player_stop)(self.mp);
                }
            }
        }
    }

    pub fn stop_async(self: &Arc<Self>) {
        let epoch = self.play_epoch.fetch_add(1, Ordering::AcqRel) + 1;
        let me = Arc::clone(self);
        std::thread::spawn(move || {
            let _guard = me.op_lock.lock().unwrap_or_else(|e| e.into_inner());
            if me.play_epoch.load(Ordering::Acquire) != epoch {
                return;
            }
            if let Some(ref api) = me.api {
                if !me.mp.is_null() {
                    unsafe {
                        (api.player_stop)(me.mp);
                    }
                }
            }
        });
    }

    pub fn is_playing(&self) -> bool {
        if let Some(ref api) = self.api {
            if !self.mp.is_null() {
                unsafe {
                    return (api.player_is_playing)(self.mp) != 0;
                }
            }
        }
        false
    }

    pub fn get_state(&self) -> i32 {
        if let Some(ref api) = self.api {
            if !self.mp.is_null() {
                unsafe {
                    return (api.player_get_state)(self.mp);
                }
            }
        }
        0
    }

    pub fn is_ended(&self) -> bool {
        self.get_state() == 6
    }

    pub fn get_position_seconds(&self) -> f64 {
        if let Some(ref api) = self.api {
            if !self.mp.is_null() {
                unsafe {
                    let ms = (api.player_get_time)(self.mp);
                    if ms > 0 {
                        return ms as f64 / 1000.0;
                    }
                }
            }
        }
        0.0
    }

    pub fn get_duration_seconds(&self) -> f64 {
        if let Some(ref api) = self.api {
            if !self.mp.is_null() {
                unsafe {
                    let ms = (api.player_get_length)(self.mp);
                    if ms > 0 {
                        return ms as f64 / 1000.0;
                    }
                }
            }
        }
        0.0
    }

    pub fn seek_seconds(&self, seconds: f64) {
        if let Some(ref api) = self.api {
            if !self.mp.is_null() {
                unsafe {
                    let ms = (seconds.max(0.0) * 1000.0).round() as i64;
                    (api.player_set_time)(self.mp, ms);
                }
            }
        }
    }

    pub fn set_volume(&self, vol_0_to_1: f64, is_muted: bool) {
        let vlc_vol = linear_to_vlc_volume(vol_0_to_1, is_muted);
        self.target_vlc_volume.store(vlc_vol, Ordering::Release);
        self.target_muted.store(is_muted || vlc_vol == 0, Ordering::Release);
        self.apply_target_volume_now();
    }

    pub fn get_target_vlc_volume(&self) -> i32 {
        if self.target_muted.load(Ordering::Acquire) {
            0
        } else {
            self.target_vlc_volume.load(Ordering::Acquire)
        }
    }

    fn apply_target_volume_now(&self) {
        if let Some(ref api) = self.api {
            if !self.mp.is_null() {
                let v = self.target_vlc_volume.load(Ordering::Acquire);
                let muted = self.target_muted.load(Ordering::Acquire);
                unsafe {
                    (api.audio_set_mute)(self.mp, if muted { 1 } else { 0 });
                    let _ = (api.audio_set_volume)(self.mp, if muted { 0 } else { v });
                }
            }
        }
    }

    /// Reaplica o volume desejado caso o backend de áudio do LibVLC tenha acabado de abrir
    /// ou reinicializado em 100%.
    pub fn sync_volume_if_needed(&self) {
        if let Some(ref api) = self.api {
            if !self.mp.is_null() {
                let target_v = self.target_vlc_volume.load(Ordering::Acquire);
                let target_m = self.target_muted.load(Ordering::Acquire);
                unsafe {
                    let cur_v = (api.audio_get_volume)(self.mp);
                    let cur_m = (api.audio_get_mute)(self.mp);
                    let want_v = if target_m { 0 } else { target_v };
                    let want_m = if target_m { 1 } else { 0 };
                    if (cur_v >= 0 && cur_v != want_v) || (cur_m >= 0 && cur_m != want_m) {
                        (api.audio_set_mute)(self.mp, want_m);
                        let _ = (api.audio_set_volume)(self.mp, want_v);
                    }
                }
            }
        }
    }

    pub fn list_audio_devices(&self) -> Vec<AudioOutputDeviceInfo> {
        let mut out = Vec::new();
        let Some(ref api) = self.api else {
            return out;
        };
        let (Some(enum_fn), Some(rel_fn)) = (api.audio_device_enum, api.audio_device_list_release)
        else {
            return out;
        };
        if self.mp.is_null() {
            return out;
        }
        unsafe {
            let head = enum_fn(self.mp);
            let mut cur = head;
            while !cur.is_null() {
                let id = if !(*cur).psz_device.is_null() {
                    CStr::from_ptr((*cur).psz_device).to_string_lossy().into_owned()
                } else {
                    String::new()
                };
                let description = if !(*cur).psz_description.is_null() {
                    CStr::from_ptr((*cur).psz_description).to_string_lossy().into_owned()
                } else {
                    id.clone()
                };
                if !id.is_empty() {
                    out.push(AudioOutputDeviceInfo { id, description });
                }
                cur = (*cur).p_next;
            }
            if !head.is_null() {
                rel_fn(head);
            }
        }
        out
    }

    pub fn set_audio_device(&self, device_id: &str) {
        let Some(ref api) = self.api else {
            return;
        };
        let Some(set_fn) = api.audio_device_set else {
            return;
        };
        if self.mp.is_null() {
            return;
        }
        if let Ok(c_id) = CString::new(device_id) {
            unsafe {
                set_fn(self.mp, ptr::null(), c_id.as_ptr());
            }
            self.apply_target_volume_now();
        }
    }

    pub fn set_speed(&self, speed: f64) {
        if let Some(ref api) = self.api {
            if !self.mp.is_null() {
                unsafe {
                    let r = speed.clamp(0.25, 2.0) as c_float;
                    let _ = (api.player_set_rate)(self.mp, r);
                }
            }
        }
    }

    pub fn get_speed(&self) -> f64 {
        if let Some(ref api) = self.api {
            if !self.mp.is_null() {
                unsafe {
                    let r = (api.player_get_rate)(self.mp) as f64;
                    if r > 0.1 {
                        return r;
                    }
                }
            }
        }
        1.0
    }

    pub fn take_new_video_frame(&self) -> Option<(u32, u32, Vec<u8>)> {
        let ctx = self.video_ctx.as_ref()?;
        if !ctx.has_new_frame.swap(false, Ordering::AcqRel) {
            return None;
        }
        let guard = ctx.buffer.lock().ok()?;
        if guard.width == 0 || guard.height == 0 || guard.front_rgba.is_empty() {
            return None;
        }
        Some((guard.width, guard.height, guard.front_rgba.clone()))
    }
}

impl Drop for RustMediaPlayer {
    fn drop(&mut self) {
        self.play_epoch.fetch_add(1, Ordering::AcqRel);
        if let Some(ref api) = self.api {
            if !self.mp.is_null() {
                unsafe {
                    (api.player_stop)(self.mp);
                    (api.player_release)(self.mp);
                }
                self.mp = ptr::null_mut();
            }
        }
    }
}
