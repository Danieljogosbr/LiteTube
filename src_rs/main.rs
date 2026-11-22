// ============================================================================
// LiteTube 2.0 (Rust) — Camada 1: Interface Nativa GPU (egui / eframe)
// Layout 100% fiel ao design original LiteTube (WpfMainWindow / WpfVideoView /
// WpfVideoPlayerOverlay / WpfMediaListControl / WpfMusicView / WpfMusicPlayerBar).
// ============================================================================

#![cfg_attr(not(debug_assertions), windows_subsystem = "windows")]
#![allow(dead_code)]

mod auth;
mod icons;
mod media;
mod memory;
mod models;
mod sponsorblock;
mod youtube;

use crate::auth::{
    GoogleAccountProfile, GoogleSessionService, extract_cookies_from_browser,
    get_legacy_plaintext_path, get_vault_file_path, has_stored_cookies,
    looks_like_netscape_cookies, protect_text, save_protected_cookies, unprotect_text,
};
use crate::media::{RustMediaPlayer, linear_to_vlc_volume};
use crate::memory::{
    apply_dark_title_bar_to_current_process, bring_current_process_window_to_front,
    get_current_ram_usage_mb, init_logger, log_error, log_info, register_litetube_protocol,
    sanitize_sensitive_data, start_ipc_pipe_server, trim_memory, try_send_ipc_play_url,
};
use crate::models::{
    AppConfig, ChannelPageData, RepeatMode, TrackItem, UserPlaylist, VideoChapter, VideoItem,
    WatchHistoryService, format_seconds,
};
use crate::sponsorblock::{
    SponsorSegment, StoryboardData, check_and_get_skip_target, fetch_cropped_storyboard_frame,
    fetch_storyboard_data, get_music_sponsor_segments, get_video_sponsor_segments,
};
use crate::youtube::{
    AccountFeedPage, ResolvedStream, detect_codec_from_urls, extract_chapters_from_description,
    fetch_account_playlists, fetch_channel_page,
    fetch_history_feed, fetch_home_feed, fetch_music_account_playlists, fetch_music_explore_feed,
    fetch_music_playlist_items, fetch_personalized_music_feed, fetch_playlist_items,
    fetch_search_continuation, fetch_subscriptions_feed, fetch_video_metadata,
    fetch_watch_next_details, http_get_bytes, parse_litetube_or_youtube_url,
    parse_music_carousel_shelves, parse_music_playlist_items_json, parse_music_playlists_json,
    parse_playlists_json,
    resolve_audio_only_stream, resolve_video_streams, search_youtube_page,
};
use eframe::egui::{
    self, Align, Align2, Color32, ColorImage, CornerRadius, CursorIcon, FontData, FontDefinitions,
    FontFamily, FontId, Key, Layout, Margin, Pos2, Rect, RichText, ScrollArea, Sense, Stroke,
    StrokeKind, TextureHandle, TextureOptions, Vec2,
};
use std::collections::{HashMap, HashSet, VecDeque};
use std::fs;
use std::sync::{Arc, Mutex, mpsc};
use std::thread;
use std::time::{Duration, Instant};

// ============================================================================
// Paleta Exata do XAML Original (#0F0F0F YouTube Dark)
// ============================================================================

const BG_MAIN: Color32 = Color32::from_rgb(0x0F, 0x0F, 0x0F);
const BG_STATUS: Color32 = Color32::from_rgb(0x0A, 0x0A, 0x0C);
const BG_SURFACE: Color32 = Color32::from_rgb(0x18, 0x18, 0x1B);
const BG_HOVER: Color32 = Color32::from_rgb(0x1F, 0x1F, 0x1F);
const BG_HOVER_ALT: Color32 = Color32::from_rgb(0x24, 0x24, 0x28);
const BG_ACTIVE_NAV: Color32 = Color32::from_rgb(0x27, 0x27, 0x2A);
const BG_SEARCH: Color32 = Color32::from_rgb(0x12, 0x12, 0x12);
const BG_SEARCH_BTN: Color32 = Color32::from_rgb(0x22, 0x22, 0x26);
const BORDER_SUBTLE: Color32 = Color32::from_rgb(0x2A, 0x2A, 0x2E);
const DIVIDER_COLOR: Color32 = Color32::from_rgb(0x22, 0x22, 0x22);
const YT_RED: Color32 = Color32::from_rgb(0xFF, 0x00, 0x33);
const YT_RED_MUSIC: Color32 = Color32::from_rgb(0xFF, 0x00, 0x00);
const YT_BLUE: Color32 = Color32::from_rgb(0x3E, 0xA6, 0xFF);
const GREEN_RAM: Color32 = Color32::from_rgb(0x4A, 0xDE, 0x80);
const GREEN_CONN: Color32 = Color32::from_rgb(0x00, 0xD4, 0x82);
const YELLOW_WARN: Color32 = Color32::from_rgb(0xFA, 0xCC, 0x15);
const TEXT_PRIMARY: Color32 = Color32::from_rgb(0xF1, 0xF1, 0xF1);
const TEXT_NAV: Color32 = Color32::from_rgb(0xD4, 0xD4, 0xD8);
const TEXT_SECONDARY: Color32 = Color32::from_rgb(0xAA, 0xAA, 0xAA);
const TEXT_MUTED: Color32 = Color32::from_rgb(0x77, 0x77, 0x77);
const BG_MUSIC_BAR: Color32 = Color32::from_rgb(0x18, 0x18, 0x18);
const PILL_GLASS: Color32 = Color32::from_rgba_premultiplied(0x12, 0x12, 0x12, 0xBF);
const CHIP_TRANSLUCENT: Color32 = Color32::from_rgba_premultiplied(28, 28, 28, 28);
const CHIP_HOVER: Color32 = Color32::from_rgba_premultiplied(48, 48, 48, 48);

#[derive(Debug, Clone, Copy, PartialEq, Eq)]
enum ActiveEnvironment {
    Video,
    Music,
}

#[derive(Debug, Clone, Copy, PartialEq, Eq)]
enum VideoNavSection {
    Home,
    Subscriptions,
    History,
    Playlists,
    PlaylistItems,
    Saved,
    Search,
    Channel,
}

#[derive(Debug, Clone, Copy, PartialEq, Eq)]
enum MusicNavSection {
    Recommended,
    Explore,
    AccountPlaylists,
    AccountPlaylistTracks,
    Favorites,
    Queue,
    LocalPlaylist(usize),
    Search,
}

enum AsyncMessage {
    ProfileLoaded(GoogleAccountProfile),
    VideoFeedLoaded {
        request_id: u64,
        section: VideoNavSection,
        page: AccountFeedPage,
        append: bool,
    },
    SearchLoaded {
        request_id: u64,
        is_music: bool,
        items: Vec<TrackItem>,
        continuation: String,
        query: String,
        append: bool,
    },
    ChannelLoaded {
        request_id: u64,
        data: ChannelPageData,
    },
    MusicFeedLoaded {
        request_id: u64,
        section: MusicNavSection,
        items: Vec<TrackItem>,
        status: String,
        continuation: String,
        append: bool,
    },
    VideoStreamResolved {
        request_id: u64,
        video: TrackItem,
        result: Result<ResolvedStream, String>,
        resume_sec: f64,
    },
    VideoPrefetchResolved {
        video_id: String,
        quality: i32,
        stream: ResolvedStream,
    },
    VideoMetadataLoaded {
        video_id: String,
        meta: VideoItem,
        chapters: Vec<VideoChapter>,
        related: Vec<TrackItem>,
        sub_count: String,
    },
    VideoSponsorLoaded {
        video_id: String,
        segments: Vec<SponsorSegment>,
    },
    VideoStoryboardLoaded {
        video_id: String,
        storyboard: Option<StoryboardData>,
    },
    StoryboardPreviewReady {
        hover_sec: f64,
        width: u32,
        height: u32,
        rgba: Vec<u8>,
    },
    MusicStreamResolved {
        request_id: u64,
        track: TrackItem,
        result: Result<String, String>,
        segments: Vec<SponsorSegment>,
    },
    MusicBgStateChanged {
        request_id: u64,
        index: Option<usize>,
        is_resolving: bool,
        sponsors: Vec<SponsorSegment>,
        status: String,
    },
    ImageLoaded {
        cache_key: String,
        width: u32,
        height: u32,
        rgba: Vec<u8>,
    },
    BrowserCookiesExtracted {
        browser: String,
        result: Result<usize, String>,
    },
    ImportedPlaylistLoaded {
        name: String,
        url: String,
        tracks: Vec<TrackItem>,
    },
    ExternalPlayUrl(String),
}

// ============================================================================
// Cache LRU Estrito de Imagens GPU (Miniaturas Retangulares + Avatares Circulares)
// ============================================================================

struct BoundedTextureCache {
    textures: HashMap<String, TextureHandle>,
    order: VecDeque<String>,
    in_flight: HashSet<String>,
    failed: HashSet<String>,
    max_entries: usize,
}

impl BoundedTextureCache {
    fn new(max_entries: usize) -> Self {
        Self {
            textures: HashMap::new(),
            order: VecDeque::new(),
            in_flight: HashSet::new(),
            failed: HashSet::new(),
            max_entries,
        }
    }

    fn get_or_request_rect(
        &mut self,
        url: &str,
        tx: &mpsc::Sender<AsyncMessage>,
        ctx: &egui::Context,
    ) -> Option<TextureHandle> {
        self.get_or_request_internal(url, false, tx, ctx)
    }

    fn get_or_request_circle(
        &mut self,
        url: &str,
        tx: &mpsc::Sender<AsyncMessage>,
        ctx: &egui::Context,
    ) -> Option<TextureHandle> {
        self.get_or_request_internal(url, true, tx, ctx)
    }

    fn get_or_request_internal(
        &mut self,
        url: &str,
        circular_mask: bool,
        tx: &mpsc::Sender<AsyncMessage>,
        ctx: &egui::Context,
    ) -> Option<TextureHandle> {
        let u = url.trim();
        if u.is_empty() {
            return None;
        }
        let cache_key = if circular_mask {
            format!("circ:{}", u)
        } else {
            u.to_string()
        };
        if let Some(tex) = self.textures.get(&cache_key) {
            return Some(tex.clone());
        }
        if self.failed.contains(&cache_key) {
            return None;
        }
        if self.in_flight.len() < 16 && self.in_flight.insert(cache_key.clone()) {
            let url_owned = u.to_string();
            let key_owned = cache_key;
            let tx_clone = tx.clone();
            let ctx_clone = ctx.clone();
            thread::spawn(move || {
                let decoded = http_get_bytes(&url_owned)
                    .and_then(|bytes| image::load_from_memory(&bytes).ok());
                if let Some(img) = decoded {
                    let (w, h, rgba) = if circular_mask {
                        let sq = img
                            .resize_to_fill(72, 72, image::imageops::FilterType::Triangle)
                            .to_rgba8();
                        let (sw, sh) = (sq.width(), sq.height());
                        let mut raw = sq.into_raw();
                        apply_smooth_circle_mask(&mut raw, sw, sh);
                        (sw, sh, raw)
                    } else {
                        // Recorte 16:9 limpo sem barras pretas horizontais do hqdefault (4:3)
                        let mut rgb = img.to_rgba8();
                        let (iw, ih) = (rgb.width(), rgb.height());
                        if iw == 480 && ih == 360 {
                            // hqdefault.jpg tem barras pretas de 45px no topo e embaixo
                            rgb = image::imageops::crop_imm(&rgb, 0, 45, 480, 270).to_image();
                        }
                        let resized = image::imageops::resize(
                            &rgb,
                            352,
                            198,
                            image::imageops::FilterType::Triangle,
                        );
                        let (rw, rh) = (resized.width(), resized.height());
                        (rw, rh, resized.into_raw())
                    };
                    let _ = tx_clone.send(AsyncMessage::ImageLoaded {
                        cache_key: key_owned,
                        width: w,
                        height: h,
                        rgba,
                    });
                } else {
                    // Garante remoção de `in_flight` em caso de erro HTTP/404 ou imagem inválida
                    let _ = tx_clone.send(AsyncMessage::ImageLoaded {
                        cache_key: key_owned,
                        width: 0,
                        height: 0,
                        rgba: Vec::new(),
                    });
                }
                ctx_clone.request_repaint();
            });
        }
        None
    }

    fn insert(&mut self, ctx: &egui::Context, cache_key: String, width: u32, height: u32, rgba: Vec<u8>) {
        self.in_flight.remove(&cache_key);
        if width == 0 || height == 0 || rgba.len() != (width * height * 4) as usize {
            if self.failed.len() >= 512 {
                self.failed.clear();
            }
            self.failed.insert(cache_key);
            return;
        }
        self.failed.remove(&cache_key);
        let is_new_key = !self.textures.contains_key(&cache_key);
        if is_new_key {
            while self.textures.len() >= self.max_entries {
                if let Some(oldest) = self.order.pop_front() {
                    self.textures.remove(&oldest);
                } else {
                    break;
                }
            }
        }
        let color_img = ColorImage::from_rgba_unmultiplied([width as usize, height as usize], &rgba);
        let handle = ctx.load_texture(&cache_key, color_img, TextureOptions::LINEAR);
        if is_new_key {
            self.order.push_back(cache_key.clone());
        }
        self.textures.insert(cache_key, handle);
    }
}

fn apply_smooth_circle_mask(rgba: &mut [u8], w: u32, h: u32) {
    let cx = (w as f32 - 1.0) * 0.5;
    let cy = (h as f32 - 1.0) * 0.5;
    let radius = cx.min(cy);
    for y in 0..h {
        for x in 0..w {
            let dx = x as f32 - cx;
            let dy = y as f32 - cy;
            let dist = (dx * dx + dy * dy).sqrt();
            let idx = ((y * w + x) * 4) as usize;
            if dist > radius {
                rgba[idx + 3] = 0;
            } else if dist > radius - 1.2 {
                let alpha = ((radius - dist) / 1.2).clamp(0.0, 1.0);
                rgba[idx + 3] = ((rgba[idx + 3] as f32) * alpha) as u8;
            }
        }
    }
}

// ============================================================================
// Supervisor de Áudio em Segundo Plano (continua tocando e trocando faixas
// mesmo com o LiteTube minimizado ou atrás de jogos/navegadores!)
// ============================================================================

struct MusicBgState {
    queue: Vec<TrackItem>,
    current_index: Option<usize>,
    play_req_seq: u64,
    ended_handled_seq: u64,
    is_resolving: bool,
    shuffle: bool,
    repeat_mode: RepeatMode,
    volume: f64,
    is_muted: bool,
    enable_sponsor_block: bool,
    auto_skip_sponsors: bool,
    sponsors: Vec<SponsorSegment>,
    last_skipped_sponsor_uuid: String,
    config_snapshot: AppConfig,
}

fn spawn_music_resolve_and_play(
    bg: Arc<Mutex<MusicBgState>>,
    player: Arc<RustMediaPlayer>,
    tx: mpsc::Sender<AsyncMessage>,
    ctx: egui::Context,
    index: usize,
    req_id: u64,
    track: TrackItem,
    cfg: AppConfig,
    enable_sb: bool,
) {
    thread::spawn(move || {
        let res = resolve_audio_only_stream(&track.id, &cfg);
        let segs = if enable_sb {
            get_music_sponsor_segments(&track.id)
        } else {
            Vec::new()
        };

        let (still_current, vol, muted) = {
            let mut st = bg.lock().unwrap_or_else(|e| e.into_inner());
            if st.play_req_seq != req_id {
                return;
            }
            st.is_resolving = false;
            st.sponsors = segs.clone();
            st.last_skipped_sponsor_uuid.clear();
            (true, st.volume, st.is_muted)
        };
        if !still_current {
            return;
        }

        match res {
            Ok(url) => {
                // Abre e inicia a reprodução DIRETO na thread de fundo — não depende de eframe::App::update()!
                let ok = player.open_and_play(&url, None, 0.0, vol, muted);
                let status = if ok {
                    format!("Tocando: {} — {}", track.title, track.artist)
                } else {
                    format!("Falha ao tocar '{}'. Avançando...", track.title)
                };
                let _ = tx.send(AsyncMessage::MusicBgStateChanged {
                    request_id: req_id,
                    index: Some(index),
                    is_resolving: false,
                    sponsors: segs,
                    status,
                });
                ctx.request_repaint();
                if !ok {
                    advance_music_in_background(&bg, &player, &tx, &ctx, false);
                }
            }
            Err(e) => {
                let _ = tx.send(AsyncMessage::MusicBgStateChanged {
                    request_id: req_id,
                    index: Some(index),
                    is_resolving: false,
                    sponsors: Vec::new(),
                    status: e,
                });
                ctx.request_repaint();
                advance_music_in_background(&bg, &player, &tx, &ctx, false);
            }
        }
    });
}

fn next_pseudo_random_u64() -> u64 {
    static RNG_SEQ: std::sync::atomic::AtomicU64 =
        std::sync::atomic::AtomicU64::new(0x9E37_79B9_7F4A_7C15);
    let nanos = std::time::SystemTime::now()
        .duration_since(std::time::UNIX_EPOCH)
        .map(|d| d.as_nanos() as u64)
        .unwrap_or(1);
    let seq =
        RNG_SEQ.fetch_add(0xBF58_476D_1CE4_E5B9, std::sync::atomic::Ordering::Relaxed);
    let mut z = nanos ^ seq;
    z = (z ^ (z >> 30)).wrapping_mul(0xBF58_476D_1CE4_E5B9);
    z = (z ^ (z >> 27)).wrapping_mul(0x94D0_49BB_1331_11EB);
    z ^ (z >> 31)
}

fn advance_music_in_background(
    bg: &Arc<Mutex<MusicBgState>>,
    player: &Arc<RustMediaPlayer>,
    tx: &mpsc::Sender<AsyncMessage>,
    ctx: &egui::Context,
    respect_repeat_one: bool,
) {
    let next_job = {
        let mut st = bg.lock().unwrap_or_else(|e| e.into_inner());
        if st.queue.is_empty() {
            return;
        }
        let cur = st.current_index.unwrap_or(0);
        let next_idx = if respect_repeat_one && st.repeat_mode == RepeatMode::RepeatOne {
            Some(cur.min(st.queue.len() - 1))
        } else if respect_repeat_one && st.shuffle && st.queue.len() > 1 {
            let seed = next_pseudo_random_u64() as usize;
            let step = 1 + (seed % (st.queue.len() - 1));
            Some((cur + step) % st.queue.len())
        } else if cur + 1 < st.queue.len() {
            Some(cur + 1)
        } else if respect_repeat_one && st.repeat_mode == RepeatMode::RepeatAll {
            Some(0)
        } else {
            None
        };

        if let Some(idx) = next_idx {
            let track = st.queue[idx].clone();
            st.current_index = Some(idx);
            st.is_resolving = true;
            st.sponsors.clear();
            st.last_skipped_sponsor_uuid.clear();
            st.play_req_seq = st.play_req_seq.wrapping_add(1);
            let req_id = st.play_req_seq;
            Some((idx, req_id, track, st.config_snapshot.clone(), st.enable_sponsor_block))
        } else {
            st.current_index = None;
            st.is_resolving = false;
            st.sponsors.clear();
            None
        }
    };

    if let Some((idx, req_id, track, cfg, enable_sb)) = next_job {
        player.stop_async();
        let _ = tx.send(AsyncMessage::MusicBgStateChanged {
            request_id: req_id,
            index: Some(idx),
            is_resolving: true,
            sponsors: Vec::new(),
            status: format!("Carregando música: {} — {}...", track.title, track.artist),
        });
        ctx.request_repaint();
        spawn_music_resolve_and_play(
            Arc::clone(bg),
            Arc::clone(player),
            tx.clone(),
            ctx.clone(),
            idx,
            req_id,
            track,
            cfg,
            enable_sb,
        );
    } else {
        player.stop_async();
        let seq = bg.lock().map(|s| s.play_req_seq).unwrap_or(0);
        let _ = tx.send(AsyncMessage::MusicBgStateChanged {
            request_id: seq,
            index: None,
            is_resolving: false,
            sponsors: Vec::new(),
            status: "Fim da fila de música.".to_string(),
        });
        ctx.request_repaint();
    }
}

fn spawn_music_supervisor(
    bg: Arc<Mutex<MusicBgState>>,
    player: Arc<RustMediaPlayer>,
    tx: mpsc::Sender<AsyncMessage>,
    ctx: egui::Context,
    stop_flag: Arc<std::sync::atomic::AtomicBool>,
) {
    thread::spawn(move || {
        while !stop_flag.load(std::sync::atomic::Ordering::Acquire) {
            thread::sleep(Duration::from_millis(250));
            let state = player.get_state();

            // 3 = Playing: mantém volume DirectSound e pula SponsorBlock mesmo minimizado
            if state == 3 {
                player.sync_volume_if_needed();
                let pos = player.get_position_seconds();
                let skip_action = {
                    let mut st = bg.lock().unwrap_or_else(|e| e.into_inner());
                    if st.enable_sponsor_block && st.auto_skip_sponsors && !st.sponsors.is_empty() {
                        if let Some((skip_to, seg)) = check_and_get_skip_target(pos, &st.sponsors) {
                            if seg.uuid != st.last_skipped_sponsor_uuid {
                                st.last_skipped_sponsor_uuid = seg.uuid.clone();
                                Some((
                                    skip_to,
                                    seg.category_label().to_string(),
                                    st.play_req_seq,
                                    st.current_index,
                                    st.sponsors.clone(),
                                ))
                            } else {
                                None
                            }
                        } else {
                            None
                        }
                    } else {
                        None
                    }
                };
                if let Some((skip_to, label, req_id, idx, segs)) = skip_action {
                    player.seek_seconds(skip_to);
                    let _ = tx.send(AsyncMessage::MusicBgStateChanged {
                        request_id: req_id,
                        index: idx,
                        is_resolving: false,
                        sponsors: segs,
                        status: format!("[SponsorBlock] {} pulado", label),
                    });
                    ctx.request_repaint();
                }
                continue;
            }

            // 6 = Ended, 7 = Error: avança automaticamente para a próxima faixa em segundo plano!
            if state == 6 || state == 7 {
                let should_advance = {
                    let mut st = bg.lock().unwrap_or_else(|e| e.into_inner());
                    if st.current_index.is_some()
                        && !st.is_resolving
                        && st.ended_handled_seq != st.play_req_seq
                    {
                        st.ended_handled_seq = st.play_req_seq;
                        true
                    } else {
                        false
                    }
                };
                if should_advance {
                    advance_music_in_background(&bg, &player, &tx, &ctx, state == 6);
                }
            }
        }
    });
}

// ============================================================================
// Aplicação Principal LiteTubeApp
// ============================================================================

struct LiteTubeApp {
    config: AppConfig,
    tx: mpsc::Sender<AsyncMessage>,
    rx: mpsc::Receiver<AsyncMessage>,
    egui_ctx: egui::Context,
    dark_titlebar_applied: u8,

    // Ambiente & Navegação
    active_env: ActiveEnvironment,
    sidebar_expanded: bool,
    search_query: String,
    search_history_open: bool,
    status_text: String,
    toast_message: Option<(String, Instant)>,

    // Conta Google & Modais
    account_profile: GoogleAccountProfile,
    show_auth_modal: bool,
    auth_modal_busy: bool,
    auth_modal_status: String,
    auth_paste_buffer: String,
    show_open_url_modal: bool,
    open_url_input: String,
    show_new_playlist_modal: bool,
    new_playlist_name_input: String,
    show_import_playlist_modal: bool,
    import_playlist_url_input: String,
    center_osd: Option<(String, Instant)>,

    // Monitoramento de RAM Real (PrivateWorkingSet)
    current_ram_mb: f64,
    last_ram_check: Instant,
    ram_trim_counter: u32,

    // Cache de Imagens GPU
    thumb_cache: BoundedTextureCache,

    // Ambiente YouTube Vídeo
    video_nav: VideoNavSection,
    video_nav_req_seq: u64,
    video_watch_open: bool,
    video_is_fullscreen: bool,
    video_items: Vec<TrackItem>,
    video_continuation: String,
    video_feed_header: String,
    video_is_loading_feed: bool,
    video_search_chip: String, // "all", "videos", "channels"
    opened_video_playlist: Option<TrackItem>,
    active_channel_data: ChannelPageData,

    // Player de Vídeo (Camada 2) + HUD Auto-Hide
    video_player: Arc<RustMediaPlayer>,
    video_texture: Option<TextureHandle>,
    current_video: Option<TrackItem>,
    current_video_meta: VideoItem,
    current_video_chapters: Vec<VideoChapter>,
    current_video_related: Vec<TrackItem>,
    current_video_sub_count: String,
    current_video_sponsors: Vec<SponsorSegment>,
    current_video_storyboard: Option<StoryboardData>,
    storyboard_sheet_cache: Arc<Mutex<HashMap<String, image::RgbaImage>>>,
    storyboard_preview_tex: Option<(f64, TextureHandle)>,
    last_storyboard_req_sec: f64,
    last_skipped_video_sponsor_uuid: String,
    video_play_req_seq: u64,
    video_ended_handled_seq: u64,
    video_is_resolving: bool,
    video_codec_label: String,
    video_actual_height: i32,
    video_speed: f64,
    video_description_expanded: bool,
    video_show_gear_popup: bool,
    video_up_next_queue: Vec<TrackItem>,
    video_prefetched: Option<(String, i32, ResolvedStream)>,
    video_prefetch_triggered: bool,
    last_history_save: Instant,
    is_dragging_video_scrubber: bool,
    drag_video_scrub_sec: f64,
    last_player_mouse_pos: Option<Pos2>,
    last_player_activity: Instant,
    sponsor_skip_banner: Option<(String, Instant)>,

    // Ambiente YouTube Music & Player Headless (Camada 2)
    music_nav: MusicNavSection,
    music_nav_req_seq: u64,
    music_items: Vec<TrackItem>,
    music_continuation: String,
    music_header: String,
    music_is_loading_feed: bool,
    opened_music_playlist: Option<TrackItem>,
    music_player: Arc<RustMediaPlayer>,
    music_bg: Arc<Mutex<MusicBgState>>,
    music_supervisor_stop: Arc<std::sync::atomic::AtomicBool>,
    music_queue: Vec<TrackItem>,
    music_current_index: Option<usize>,
    music_play_req_seq: u64,
    music_ended_handled_seq: u64,
    music_is_resolving: bool,
    music_sponsors: Vec<SponsorSegment>,
    last_skipped_music_sponsor_uuid: String,
    is_dragging_music_scrubber: bool,
    drag_music_scrub_sec: f64,
    config_dirty_since: Option<Instant>,
}

impl LiteTubeApp {
    fn new(cc: &eframe::CreationContext<'_>, startup_url: Option<String>) -> Self {
        egui_extras::install_image_loaders(&cc.egui_ctx);
        configure_windows_fonts_and_theme(&cc.egui_ctx);

        let (tx, rx) = mpsc::channel();
        let config = AppConfig::load();
        let ram = get_current_ram_usage_mb();

        let video_player = Arc::new(RustMediaPlayer::new(false));
        let music_player = Arc::new(RustMediaPlayer::new(true));
        video_player.set_volume(config.video_volume, config.is_video_muted);
        music_player.set_volume(config.music_volume, config.is_music_muted);

        let music_bg = Arc::new(Mutex::new(MusicBgState {
            queue: Vec::new(),
            current_index: None,
            play_req_seq: 0,
            ended_handled_seq: 0,
            is_resolving: false,
            shuffle: config.shuffle,
            repeat_mode: config.repeat_mode(),
            volume: config.music_volume,
            is_muted: config.is_music_muted,
            enable_sponsor_block: config.enable_sponsor_block,
            auto_skip_sponsors: config.auto_skip_sponsors,
            sponsors: Vec::new(),
            last_skipped_sponsor_uuid: String::new(),
            config_snapshot: config.clone(),
        }));
        let music_supervisor_stop = Arc::new(std::sync::atomic::AtomicBool::new(false));

        let mut app = Self {
            config,
            tx,
            rx,
            egui_ctx: cc.egui_ctx.clone(),
            dark_titlebar_applied: 0,
            active_env: ActiveEnvironment::Video,
            sidebar_expanded: true,
            search_query: String::new(),
            search_history_open: false,
            status_text: "LiteTube pronto.".to_string(),
            toast_message: None,
            account_profile: GoogleAccountProfile::default(),
            show_auth_modal: false,
            auth_modal_busy: false,
            auth_modal_status: String::new(),
            auth_paste_buffer: String::new(),
            show_open_url_modal: false,
            open_url_input: String::new(),
            show_new_playlist_modal: false,
            new_playlist_name_input: String::new(),
            show_import_playlist_modal: false,
            import_playlist_url_input: String::new(),
            center_osd: None,
            current_ram_mb: ram,
            last_ram_check: Instant::now(),
            ram_trim_counter: 0,
            thumb_cache: BoundedTextureCache::new(160),
            video_nav: VideoNavSection::Home,
            video_nav_req_seq: 0,
            video_watch_open: false,
            video_is_fullscreen: false,
            video_items: Vec::new(),
            video_continuation: String::new(),
            video_feed_header: "Início • Vídeos recomendados".to_string(),
            video_is_loading_feed: false,
            video_search_chip: "all".to_string(),
            opened_video_playlist: None,
            active_channel_data: ChannelPageData::default(),
            video_player,
            video_texture: None,
            current_video: None,
            current_video_meta: VideoItem::default(),
            current_video_chapters: Vec::new(),
            current_video_related: Vec::new(),
            current_video_sub_count: String::new(),
            current_video_sponsors: Vec::new(),
            current_video_storyboard: None,
            storyboard_sheet_cache: Arc::new(Mutex::new(HashMap::new())),
            storyboard_preview_tex: None,
            last_storyboard_req_sec: -10.0,
            last_skipped_video_sponsor_uuid: String::new(),
            video_play_req_seq: 0,
            video_ended_handled_seq: 0,
            video_is_resolving: false,
            video_codec_label: "Direct3D 11".to_string(),
            video_actual_height: 720,
            video_speed: 1.0,
            video_description_expanded: false,
            video_show_gear_popup: false,
            video_up_next_queue: Vec::new(),
            video_prefetched: None,
            video_prefetch_triggered: false,
            last_history_save: Instant::now(),
            is_dragging_video_scrubber: false,
            drag_video_scrub_sec: 0.0,
            last_player_mouse_pos: None,
            last_player_activity: Instant::now(),
            sponsor_skip_banner: None,
            music_nav: MusicNavSection::Recommended,
            music_nav_req_seq: 0,
            music_items: Vec::new(),
            music_continuation: String::new(),
            music_header: "Recomendados do YouTube Music".to_string(),
            music_is_loading_feed: false,
            opened_music_playlist: None,
            music_player,
            music_bg,
            music_supervisor_stop,
            music_queue: Vec::new(),
            music_current_index: None,
            music_play_req_seq: 0,
            music_ended_handled_seq: 0,
            music_is_resolving: false,
            music_sponsors: Vec::new(),
            last_skipped_music_sponsor_uuid: String::new(),
            is_dragging_music_scrubber: false,
            drag_music_scrub_sec: 0.0,
            config_dirty_since: None,
        };

        app.refresh_account_profile(false);
        app.load_video_section(VideoNavSection::Home, false);

        if let Some(url) = startup_url {
            app.play_video_by_url(&url);
        }

        // Servidor IPC Named Pipe para receber links externos (Cortex / litetube://) em instância única
        let ipc_tx = app.tx.clone();
        let ipc_ctx = cc.egui_ctx.clone();
        start_ipc_pipe_server(move |url| {
            let _ = ipc_tx.send(AsyncMessage::ExternalPlayUrl(url));
            ipc_ctx.request_repaint();
        });

        // Supervisor de Música em segundo plano (garante troca de faixa mesmo minimizado/oculto)
        spawn_music_supervisor(
            Arc::clone(&app.music_bg),
            Arc::clone(&app.music_player),
            app.tx.clone(),
            cc.egui_ctx.clone(),
            Arc::clone(&app.music_supervisor_stop),
        );

        app
    }

    fn show_toast(&mut self, msg: impl Into<String>) {
        self.toast_message = Some((msg.into(), Instant::now()));
    }

    fn show_center_osd(&mut self, msg: impl Into<String>) {
        self.center_osd = Some((msg.into(), Instant::now()));
    }

    fn adjust_video_volume(&mut self, delta: f64) {
        let new_vol = (self.config.video_volume + delta).clamp(0.0, 1.0);
        self.config.video_volume = (new_vol * 100.0).round() / 100.0;
        if self.config.video_volume > 0.001 {
            self.config.is_video_muted = false;
        }
        self.config.save();
        self.video_player
            .set_volume(self.config.video_volume, self.config.is_video_muted);
        let pct = (self.config.video_volume * 100.0).round() as i32;
        if self.config.is_video_muted || pct == 0 {
            self.show_center_osd("Mudo");
        } else {
            self.show_center_osd(format!("Volume {}%", pct));
        }
        self.last_player_activity = Instant::now();
    }

    fn adjust_music_volume(&mut self, delta: f64) {
        let new_vol = (self.config.music_volume + delta).clamp(0.0, 1.0);
        self.config.music_volume = (new_vol * 100.0).round() / 100.0;
        if self.config.music_volume > 0.001 {
            self.config.is_music_muted = false;
        }
        self.config.save();
        self.sync_music_bg_prefs();
        self.music_player
            .set_volume(self.config.music_volume, self.config.is_music_muted);
    }

    fn pause_or_abort_music_for_video(&mut self) {
        if !self.config.auto_pause_music_when_video_plays {
            return;
        }
        if self.music_is_resolving {
            self.music_is_resolving = false;
            if let Ok(mut st) = self.music_bg.lock() {
                st.is_resolving = false;
                st.play_req_seq = st.play_req_seq.wrapping_add(1);
                st.ended_handled_seq = st.play_req_seq;
                self.music_play_req_seq = st.play_req_seq;
            }
            self.music_player.stop_async();
        } else if self.music_player.is_playing() {
            self.music_player.pause();
        }
    }

    fn pause_or_abort_video_for_music(&mut self) {
        if !self.config.auto_pause_music_when_video_plays {
            return;
        }
        if self.video_is_resolving {
            self.video_is_resolving = false;
            self.video_play_req_seq = self.video_play_req_seq.wrapping_add(1);
            self.video_ended_handled_seq = self.video_play_req_seq;
            self.video_player.stop_async();
        } else if self.video_player.is_playing() {
            self.save_current_video_progress(false);
            self.video_player.pause();
        }
    }

    fn toggle_video_play_pause(&mut self) {
        let playing = self.video_player.is_playing();
        if !playing {
            self.pause_or_abort_music_for_video();
            if !self.video_is_resolving && matches!(self.video_player.get_state(), 0 | 5 | 6 | 7) {
                if let Some(cur) = self.current_video.clone() {
                    self.start_video_playback(cur, Some(0.0));
                    return;
                }
            }
        }
        self.video_player.set_paused(playing);
        self.last_player_activity = Instant::now();
    }

    fn toggle_music_play_pause(&mut self) {
        let playing = self.music_player.is_playing();
        if !playing {
            self.pause_or_abort_video_for_music();
            if !self.music_is_resolving && matches!(self.music_player.get_state(), 0 | 5 | 6 | 7) {
                if let Some(idx) = self.music_current_index {
                    self.play_music_index(idx);
                    return;
                }
            }
        }
        self.music_player.set_paused(playing);
    }

    fn refresh_account_profile(&mut self, force: bool) {
        let cfg = self.config.clone();
        let tx = self.tx.clone();
        thread::spawn(move || {
            let profile = GoogleSessionService::shared().get_account_profile(&cfg, force);
            let _ = tx.send(AsyncMessage::ProfileLoaded(profile));
        });
    }

    fn load_video_section(&mut self, section: VideoNavSection, append: bool) {
        if append && self.video_is_loading_feed {
            return;
        }
        self.video_nav = section;
        self.video_watch_open = false;
        self.video_nav_req_seq = self.video_nav_req_seq.wrapping_add(1);
        let req_id = self.video_nav_req_seq;

        if section == VideoNavSection::Saved {
            self.video_is_loading_feed = false;
            self.video_feed_header = format!("Você • Vídeos salvos ({})", self.config.saved_videos.len());
            self.video_items = self.config.saved_videos.clone();
            self.video_continuation.clear();
            return;
        }

        let cont = if append && !self.video_continuation.is_empty() {
            Some(self.video_continuation.clone())
        } else {
            None
        };

        if !append {
            self.video_items.clear();
            self.video_continuation.clear();
            self.video_feed_header = match section {
                VideoNavSection::Home => "Início • Vídeos recomendados".to_string(),
                VideoNavSection::Subscriptions => "Inscrições • Últimos envios".to_string(),
                VideoNavSection::History => "Histórico de exibição".to_string(),
                VideoNavSection::Playlists => "Playlists da sua Conta Google".to_string(),
                VideoNavSection::PlaylistItems => self
                    .opened_video_playlist
                    .as_ref()
                    .map(|p| format!("Playlist • {}", p.title))
                    .unwrap_or_else(|| "Playlist".to_string()),
                _ => "Catálogo".to_string(),
            };
        }

        self.video_is_loading_feed = true;
        self.status_text = format!("Carregando {}...", self.video_feed_header);

        let cfg = self.config.clone();
        let tx = self.tx.clone();
        let pl_id = self
            .opened_video_playlist
            .as_ref()
            .map(|p| p.playlist_id.clone())
            .unwrap_or_default();

        thread::spawn(move || {
            let page = match section {
                VideoNavSection::Home => fetch_home_feed(&cfg, cont.as_deref()),
                VideoNavSection::Subscriptions => fetch_subscriptions_feed(&cfg, cont.as_deref()),
                VideoNavSection::History => fetch_history_feed(&cfg, cont.as_deref()),
                VideoNavSection::Playlists => fetch_account_playlists(&cfg, cont.as_deref()),
                VideoNavSection::PlaylistItems => {
                    fetch_playlist_items(&pl_id, &cfg, cont.as_deref())
                }
                _ => AccountFeedPage::default(),
            };
            let _ = tx.send(AsyncMessage::VideoFeedLoaded {
                request_id: req_id,
                section,
                page,
                append,
            });
        });
    }

    fn load_music_section(&mut self, section: MusicNavSection, append: bool) {
        if append && self.music_is_loading_feed {
            return;
        }
        self.music_nav = section;
        self.music_nav_req_seq = self.music_nav_req_seq.wrapping_add(1);
        let req_id = self.music_nav_req_seq;

        match section {
            MusicNavSection::Favorites => {
                self.music_is_loading_feed = false;
                self.music_header = format!("Músicas Favoritas ({})", self.config.favorites.len());
                self.music_items = self.config.favorites.clone();
                self.music_continuation.clear();
                return;
            }
            MusicNavSection::Queue => {
                self.music_is_loading_feed = false;
                self.music_header = format!("Fila de Reprodução ({})", self.music_queue.len());
                self.music_items = self.music_queue.clone();
                self.music_continuation.clear();
                return;
            }
            MusicNavSection::LocalPlaylist(idx) => {
                self.music_is_loading_feed = false;
                if let Some(pl) = self.config.playlists.get(idx) {
                    self.music_header =
                        format!("Playlist Local: {} ({} faixas)", pl.name, pl.tracks.len());
                    self.music_items = pl.tracks.clone();
                    self.music_continuation.clear();
                }
                return;
            }
            _ => {}
        }

        let cont = if append && !self.music_continuation.is_empty() {
            Some(self.music_continuation.clone())
        } else {
            None
        };

        if !append {
            self.music_items.clear();
            self.music_continuation.clear();
            self.music_header = match section {
                MusicNavSection::Recommended => "Recomendados do YouTube Music".to_string(),
                MusicNavSection::Explore => "Explorar • Paradas e Lançamentos".to_string(),
                MusicNavSection::AccountPlaylists => "Playlists da Conta (YT Music)".to_string(),
                MusicNavSection::AccountPlaylistTracks => self
                    .opened_music_playlist
                    .as_ref()
                    .map(|p| format!("Playlist • {}", p.title))
                    .unwrap_or_else(|| "Playlist".to_string()),
                _ => "YouTube Music".to_string(),
            };
        }

        self.music_is_loading_feed = true;
        self.status_text = format!("Carregando {}...", self.music_header);

        let cfg = self.config.clone();
        let tx = self.tx.clone();
        let pl_id = self
            .opened_music_playlist
            .as_ref()
            .map(|p| p.playlist_id.clone())
            .unwrap_or_default();

        thread::spawn(move || match section {
            MusicNavSection::Recommended => {
                let (items, status) = fetch_personalized_music_feed(&cfg);
                let _ = tx.send(AsyncMessage::MusicFeedLoaded {
                    request_id: req_id,
                    section,
                    items,
                    status,
                    continuation: String::new(),
                    append: false,
                });
            }
            MusicNavSection::Explore => {
                let (items, status) = fetch_music_explore_feed(&cfg);
                let _ = tx.send(AsyncMessage::MusicFeedLoaded {
                    request_id: req_id,
                    section,
                    items,
                    status,
                    continuation: String::new(),
                    append: false,
                });
            }
            MusicNavSection::AccountPlaylists => {
                let page = fetch_music_account_playlists(&cfg, cont.as_deref());
                let _ = tx.send(AsyncMessage::MusicFeedLoaded {
                    request_id: req_id,
                    section,
                    items: page.items,
                    status: page.status_message,
                    continuation: page.continuation_token,
                    append,
                });
            }
            MusicNavSection::AccountPlaylistTracks => {
                let page = fetch_music_playlist_items(&pl_id, &cfg, cont.as_deref());
                let _ = tx.send(AsyncMessage::MusicFeedLoaded {
                    request_id: req_id,
                    section,
                    items: page.items,
                    status: page.status_message,
                    continuation: page.continuation_token,
                    append,
                });
            }
            _ => {}
        });
    }

    fn execute_search(&mut self, append: bool) {
        let q = self.search_query.trim().to_string();
        if q.is_empty() {
            return;
        }
        self.search_history_open = false;

        if !append {
            self.config.add_search_history(&q);
        }

        let is_music = self.active_env == ActiveEnvironment::Music;
        let cfg = self.config.clone();
        let tx = self.tx.clone();

        if is_music {
            if append && self.music_is_loading_feed {
                return;
            }
            self.music_nav = MusicNavSection::Search;
            self.music_nav_req_seq = self.music_nav_req_seq.wrapping_add(1);
            let req_id = self.music_nav_req_seq;
            self.music_is_loading_feed = true;
            if !append {
                self.music_items.clear();
                self.music_continuation.clear();
                self.music_header = format!("Resultados em Música para \"{}\"", q);
            }
            let cont = if append {
                self.music_continuation.clone()
            } else {
                String::new()
            };
            thread::spawn(move || {
                let page = if append && !cont.is_empty() {
                    fetch_search_continuation(&cont, &cfg)
                } else {
                    search_youtube_page(&q, "songs", &cfg)
                };
                let _ = tx.send(AsyncMessage::SearchLoaded {
                    request_id: req_id,
                    is_music: true,
                    items: page.items,
                    continuation: page.continuation_token,
                    query: q,
                    append,
                });
            });
        } else {
            if append && self.video_is_loading_feed {
                return;
            }
            self.video_nav = VideoNavSection::Search;
            self.video_nav_req_seq = self.video_nav_req_seq.wrapping_add(1);
            let req_id = self.video_nav_req_seq;
            self.video_watch_open = false;
            self.video_search_chip = "all".to_string();
            self.video_is_loading_feed = true;
            if !append {
                self.video_items.clear();
                self.video_continuation.clear();
                self.video_feed_header = format!("Resultados para \"{}\"", q);
            }
            let cont = if append {
                self.video_continuation.clone()
            } else {
                String::new()
            };
            thread::spawn(move || {
                let page = if append && !cont.is_empty() {
                    fetch_search_continuation(&cont, &cfg)
                } else {
                    search_youtube_page(&q, "videos", &cfg)
                };
                let _ = tx.send(AsyncMessage::SearchLoaded {
                    request_id: req_id,
                    is_music: false,
                    items: page.items,
                    continuation: page.continuation_token,
                    query: q,
                    append,
                });
            });
        }
    }

    fn open_channel(&mut self, channel_id: &str, channel_name: &str, tab: &str) {
        self.active_env = ActiveEnvironment::Video;
        self.video_nav = VideoNavSection::Channel;
        self.video_nav_req_seq = self.video_nav_req_seq.wrapping_add(1);
        let req_id = self.video_nav_req_seq;
        self.video_watch_open = false;
        self.video_is_loading_feed = true;
        self.video_items.clear();
        self.video_feed_header = format!("Canal • {}", channel_name);
        self.status_text = format!("Abrindo canal {}...", channel_name);

        let cid = channel_id.to_string();
        let cname = channel_name.to_string();
        let tab_owned = tab.to_string();
        let cfg = self.config.clone();
        let tx = self.tx.clone();

        thread::spawn(move || {
            let data = fetch_channel_page(&cid, &cname, &tab_owned, &cfg);
            let _ = tx.send(AsyncMessage::ChannelLoaded {
                request_id: req_id,
                data,
            });
        });
    }

    fn play_video_by_url(&mut self, raw_url: &str) {
        if let Some((vid, start_sec)) = parse_litetube_or_youtube_url(raw_url) {
            let item = TrackItem {
                id: vid.clone(),
                title: format!("Vídeo ({})", vid),
                artist: "YouTube".to_string(),
                custom_thumb_url: format!("https://i.ytimg.com/vi/{}/hqdefault.jpg", vid),
                ..Default::default()
            };
            self.active_env = ActiveEnvironment::Video;
            self.start_video_playback(item, start_sec);
        } else {
            self.show_toast("URL ou ID de vídeo inválido.");
        }
    }

    fn start_video_playback(&mut self, mut video: TrackItem, override_start_sec: Option<f64>) {
        if video.id.is_empty() {
            return;
        }
        if video.is_channel() {
            let cid = if !video.channel_id.is_empty() {
                video.channel_id.clone()
            } else {
                video.id.trim_start_matches("CH_").to_string()
            };
            let title = video.title.clone();
            self.open_channel(&cid, &title, "home");
            return;
        }
        if video.is_playlist_card() {
            self.opened_video_playlist = Some(video);
            self.load_video_section(VideoNavSection::PlaylistItems, false);
            return;
        }

        self.save_current_video_progress(false);
        self.pause_or_abort_music_for_video();

        if video.custom_thumb_url.is_empty() {
            video.custom_thumb_url = format!("https://i.ytimg.com/vi/{}/hqdefault.jpg", video.id);
        }

        let is_same_video = self
            .current_video
            .as_ref()
            .map(|cur| cur.id.eq_ignore_ascii_case(&video.id))
            .unwrap_or(false);

        let resume_sec = override_start_sec.unwrap_or_else(|| {
            WatchHistoryService::instance()
                .get_resume_position_seconds(&video.id, video.duration_seconds) as f64
        });
        WatchHistoryService::instance().record_video_started(&video);
        self.last_history_save = Instant::now();

        self.video_watch_open = true;
        self.current_video = Some(video.clone());
        self.last_skipped_video_sponsor_uuid.clear();
        self.video_prefetch_triggered = false;
        self.video_show_gear_popup = false;
        self.last_player_activity = Instant::now();

        if !is_same_video {
            self.video_texture = None;
            self.current_video_meta = VideoItem::default();
            self.current_video_chapters.clear();
            self.current_video_related.clear();
            self.current_video_sub_count.clear();
            self.current_video_sponsors.clear();
            self.current_video_storyboard = None;
            self.storyboard_preview_tex = None;
            self.last_storyboard_req_sec = -10.0;
            if let Ok(mut sc) = self.storyboard_sheet_cache.lock() {
                sc.clear();
            }
            self.video_description_expanded = false;
        }

        self.video_play_req_seq = self.video_play_req_seq.wrapping_add(1);
        let req_id = self.video_play_req_seq;
        let target_q = self.config.video_quality_height;

        if let Some((ref pf_id, pf_q, ref pf_stream)) = self.video_prefetched {
            if pf_id.eq_ignore_ascii_case(&video.id) && pf_q == target_q {
                let stream = pf_stream.clone();
                self.video_prefetched = None;
                self.apply_resolved_video_stream(&video, stream, resume_sec);
                if !is_same_video {
                    self.spawn_video_aux_fetches(&video);
                }
                return;
            }
        }

        self.video_player.stop_async();
        self.video_is_resolving = true;
        self.status_text = format!("Conectando ao YouTube ({}p): {}...", target_q, video.title);

        let cfg = self.config.clone();
        let tx = self.tx.clone();
        let vid_clone = video.clone();

        thread::spawn(move || {
            let res = resolve_video_streams(&vid_clone.id, target_q, &cfg);
            let _ = tx.send(AsyncMessage::VideoStreamResolved {
                request_id: req_id,
                video: vid_clone,
                result: res,
                resume_sec,
            });
        });

        if !is_same_video {
            self.spawn_video_aux_fetches(&video);
        }
    }

    fn spawn_video_aux_fetches(&self, video: &TrackItem) {
        let vid_id = video.id.clone();
        let dur_sec = video.duration_seconds as f64;
        let cfg = self.config.clone();
        let tx1 = self.tx.clone();

        thread::spawn(move || {
            let meta = fetch_video_metadata(&vid_id, &cfg);
            let eff_dur = if meta.duration_seconds > 0 {
                meta.duration_seconds as f64
            } else {
                dur_sec
            };
            let chapters = extract_chapters_from_description(&meta.description, eff_dur);
            let next_details = fetch_watch_next_details(&vid_id, &cfg);
            let _ = tx1.send(AsyncMessage::VideoMetadataLoaded {
                video_id: vid_id,
                meta,
                chapters,
                related: next_details.related_videos,
                sub_count: next_details.subscriber_count_text,
            });
        });

        if self.config.enable_sponsor_block {
            let vid_id2 = video.id.clone();
            let tx2 = self.tx.clone();
            thread::spawn(move || {
                let segs = get_video_sponsor_segments(&vid_id2);
                let _ = tx2.send(AsyncMessage::VideoSponsorLoaded {
                    video_id: vid_id2,
                    segments: segs,
                });
            });
        }

        let vid_id3 = video.id.clone();
        let tx3 = self.tx.clone();
        thread::spawn(move || {
            let sb = fetch_storyboard_data(&vid_id3, dur_sec);
            let _ = tx3.send(AsyncMessage::VideoStoryboardLoaded {
                video_id: vid_id3,
                storyboard: sb,
            });
        });
    }

    fn apply_resolved_video_stream(
        &mut self,
        video: &TrackItem,
        stream: ResolvedStream,
        resume_sec: f64,
    ) {
        self.video_is_resolving = false;
        self.video_codec_label = stream.codec_label.clone();
        self.video_actual_height = stream.quality_height;

        let separate_audio = if !stream.audio_url.is_empty() && stream.audio_url != stream.video_url
        {
            Some(stream.audio_url.clone())
        } else {
            None
        };

        let eff_start = if !stream.is_live && resume_sec > 1.0 {
            resume_sec
        } else {
            0.0
        };

        if self.video_player.is_available() {
            self.video_player.open_and_play_async(
                stream.video_url.clone(),
                separate_audio,
                eff_start,
                self.config.video_volume,
                self.config.is_video_muted,
                if (self.video_speed - 1.0).abs() > 0.01 {
                    Some(self.video_speed)
                } else {
                    None
                },
            );
            if eff_start > 1.0 {
                self.show_toast(format!("Retomado de {}", format_seconds(eff_start)));
            }
            self.status_text = format!(
                "Assistindo ({}p • {}): {}",
                self.video_actual_height, self.video_codec_label, video.title
            );
        } else {
            self.show_toast("Falha ao abrir fluxo de vídeo no motor nativo.");
        }
    }

    fn switch_video_quality(&mut self, new_height: i32) {
        self.config.video_quality_height = new_height;
        self.config.save();
        self.video_show_gear_popup = false;

        if let Some(cur) = self.current_video.clone() {
            let pos = self.video_player.get_position_seconds();
            self.show_toast(format!("Alterando qualidade para {}p...", new_height));
            self.start_video_playback(cur, Some(pos));
        } else {
            self.show_toast(format!("Qualidade padrão definida para {}p", new_height));
        }
    }

    fn save_current_video_progress(&mut self, finished: bool) {
        let Some(ref cur_vid) = self.current_video else {
            return;
        };
        let dur = self
            .video_player
            .get_duration_seconds()
            .max(cur_vid.duration_seconds as f64);
        if dur <= 0.0 {
            return;
        }
        let pos = if finished {
            dur
        } else {
            self.video_player.get_position_seconds()
        };
        if pos > 2.0 {
            WatchHistoryService::instance().update_playback_progress(
                &cur_vid.id,
                pos.floor() as i64,
                dur.floor() as i64,
            );
            self.last_history_save = Instant::now();
        }
    }

    fn get_next_video_candidate(&mut self, consume: bool) -> Option<TrackItem> {
        if !self.video_up_next_queue.is_empty() {
            if consume {
                return Some(self.video_up_next_queue.remove(0));
            } else {
                return self.video_up_next_queue.first().cloned();
            }
        }
        self.current_video_related.first().cloned()
    }

    fn trigger_next_video_prefetch(&mut self) {
        if self.video_prefetch_triggered {
            return;
        }
        self.video_prefetch_triggered = true;
        let Some(next) = self.get_next_video_candidate(false) else {
            return;
        };
        let target_q = self.config.video_quality_height;
        if let Some((ref pf_id, pf_q, _)) = self.video_prefetched {
            if pf_id.eq_ignore_ascii_case(&next.id) && pf_q == target_q {
                return;
            }
        }
        let cfg = self.config.clone();
        let tx = self.tx.clone();
        let next_id = next.id.clone();
        thread::spawn(move || {
            if let Ok(stream) = resolve_video_streams(&next_id, target_q, &cfg) {
                let _ = tx.send(AsyncMessage::VideoPrefetchResolved {
                    video_id: next_id,
                    quality: target_q,
                    stream,
                });
            }
        });
    }

    fn sync_music_bg_prefs(&self) {
        if let Ok(mut st) = self.music_bg.lock() {
            st.queue = self.music_queue.clone();
            st.shuffle = self.config.shuffle;
            st.repeat_mode = self.config.repeat_mode();
            st.volume = self.config.music_volume;
            st.is_muted = self.config.is_music_muted;
            st.enable_sponsor_block = self.config.enable_sponsor_block;
            st.auto_skip_sponsors = self.config.auto_skip_sponsors;
            st.config_snapshot = self.config.clone();
        }
    }

    fn set_music_queue_and_play(&mut self, tracks: Vec<TrackItem>, start_index: usize) {
        let playable: Vec<TrackItem> = tracks
            .into_iter()
            .filter(|t| !t.is_channel() && !t.is_playlist_card() && !t.id.is_empty())
            .collect();
        if playable.is_empty() {
            return;
        }
        let idx = start_index.min(playable.len() - 1);
        self.music_queue = playable;
        if self.music_nav == MusicNavSection::Queue {
            self.music_items = self.music_queue.clone();
        }
        self.sync_music_bg_prefs();
        self.play_music_index(idx);
    }

    fn play_music_index(&mut self, index: usize) {
        if index >= self.music_queue.len() {
            return;
        }
        self.pause_or_abort_video_for_music();
        self.music_current_index = Some(index);
        let track = self.music_queue[index].clone();

        self.music_player.stop_async();
        self.music_sponsors.clear();
        self.last_skipped_music_sponsor_uuid.clear();
        self.music_is_resolving = true;

        let req_id = {
            let mut st = self.music_bg.lock().unwrap_or_else(|e| e.into_inner());
            st.queue = self.music_queue.clone();
            st.current_index = Some(index);
            st.is_resolving = true;
            st.sponsors.clear();
            st.last_skipped_sponsor_uuid.clear();
            st.shuffle = self.config.shuffle;
            st.repeat_mode = self.config.repeat_mode();
            st.volume = self.config.music_volume;
            st.is_muted = self.config.is_music_muted;
            st.enable_sponsor_block = self.config.enable_sponsor_block;
            st.auto_skip_sponsors = self.config.auto_skip_sponsors;
            st.config_snapshot = self.config.clone();
            st.play_req_seq = st.play_req_seq.wrapping_add(1);
            st.play_req_seq
        };
        self.music_play_req_seq = req_id;

        self.status_text = format!("Carregando música: {} — {}...", track.title, track.artist);

        spawn_music_resolve_and_play(
            Arc::clone(&self.music_bg),
            Arc::clone(&self.music_player),
            self.tx.clone(),
            self.egui_ctx.clone(),
            index,
            req_id,
            track,
            self.config.clone(),
            self.config.enable_sponsor_block,
        );
    }

    fn play_next_music(&mut self) {
        if self.music_queue.is_empty() {
            return;
        }
        let cur = self.music_current_index.unwrap_or(0);
        if self.config.shuffle && self.music_queue.len() > 1 {
            let seed = next_pseudo_random_u64() as usize;
            let step = 1 + (seed % (self.music_queue.len() - 1));
            let next = (cur + step) % self.music_queue.len();
            self.play_music_index(next);
            return;
        }
        let next = cur + 1;
        if next < self.music_queue.len() {
            self.play_music_index(next);
        } else if self.config.repeat_mode() == RepeatMode::RepeatAll {
            self.play_music_index(0);
        } else {
            self.music_current_index = None;
            self.music_is_resolving = false;
            self.music_sponsors.clear();
            if let Ok(mut st) = self.music_bg.lock() {
                st.current_index = None;
                st.is_resolving = false;
                st.sponsors.clear();
                st.play_req_seq = st.play_req_seq.wrapping_add(1);
                st.ended_handled_seq = st.play_req_seq;
                self.music_play_req_seq = st.play_req_seq;
            }
            self.music_player.stop_async();
            self.status_text = "Fim da fila de música.".to_string();
        }
    }

    fn play_prev_music(&mut self) {
        if self.music_queue.is_empty() {
            return;
        }
        if self.music_player.get_position_seconds() > 4.0 {
            self.music_player.seek_seconds(0.0);
            return;
        }
        let cur = self.music_current_index.unwrap_or(0);
        let prev = if cur > 0 {
            cur - 1
        } else if self.config.repeat_mode() == RepeatMode::RepeatAll {
            self.music_queue.len() - 1
        } else {
            0
        };
        self.play_music_index(prev);
    }

    fn process_async_messages(&mut self, ctx: &egui::Context) {
        while let Ok(msg) = self.rx.try_recv() {
            match msg {
                AsyncMessage::ProfileLoaded(prof) => {
                    self.account_profile = prof;
                }
                AsyncMessage::VideoFeedLoaded {
                    request_id,
                    section,
                    page,
                    append,
                } => {
                    if request_id == self.video_nav_req_seq && self.video_nav == section {
                        self.video_is_loading_feed = false;
                        if append {
                            let mut seen: HashSet<String> = self
                                .video_items
                                .iter()
                                .map(|x| x.id.to_ascii_lowercase())
                                .collect();
                            for it in page.items {
                                if seen.insert(it.id.to_ascii_lowercase()) {
                                    self.video_items.push(it);
                                }
                            }
                        } else {
                            self.video_items = page.items;
                        }
                        self.video_continuation = page.continuation_token;
                        if !page.status_message.is_empty() {
                            self.status_text = page.status_message;
                        } else {
                            self.status_text = format!(
                                "{} ({} itens)",
                                self.video_feed_header,
                                self.video_items.len()
                            );
                        }
                    }
                }
                AsyncMessage::SearchLoaded {
                    request_id,
                    is_music,
                    items,
                    continuation,
                    query,
                    append,
                } => {
                    if is_music {
                        if request_id == self.music_nav_req_seq {
                            self.music_is_loading_feed = false;
                            if append {
                                self.music_items.extend(items);
                            } else {
                                self.music_items = items;
                            }
                            self.music_continuation = continuation;
                            self.status_text = format!(
                                "Busca YT Music \"{}\": {} faixas",
                                query,
                                self.music_items.len()
                            );
                        }
                    } else if request_id == self.video_nav_req_seq {
                        self.video_is_loading_feed = false;
                        if append {
                            self.video_items.extend(items);
                        } else {
                            self.video_items = items;
                        }
                        self.video_continuation = continuation;
                        self.status_text =
                            format!("Busca \"{}\": {} resultados", query, self.video_items.len());
                    }
                }
                AsyncMessage::ChannelLoaded { request_id, data } => {
                    if request_id == self.video_nav_req_seq {
                        self.video_is_loading_feed = false;
                        self.video_items = data.items.clone();
                        self.video_continuation = data.continuation_token.clone();
                        self.video_feed_header = format!("Canal • {}", data.channel_name);
                        self.status_text = format!(
                            "Canal {} carregado ({} itens)",
                            data.channel_name,
                            self.video_items.len()
                        );
                        self.active_channel_data = data;
                    }
                }
                AsyncMessage::MusicFeedLoaded {
                    request_id,
                    section,
                    items,
                    status,
                    continuation,
                    append,
                } => {
                    if request_id == self.music_nav_req_seq && self.music_nav == section {
                        self.music_is_loading_feed = false;
                        if append {
                            self.music_items.extend(items);
                        } else {
                            self.music_items = items;
                        }
                        self.music_continuation = continuation;
                        self.status_text = if !status.is_empty() {
                            status
                        } else {
                            format!("{} ({} itens)", self.music_header, self.music_items.len())
                        };
                    }
                }
                AsyncMessage::VideoStreamResolved {
                    request_id,
                    video,
                    result,
                    resume_sec,
                } => {
                    if request_id == self.video_play_req_seq {
                        match result {
                            Ok(stream) => {
                                self.apply_resolved_video_stream(&video, stream, resume_sec);
                            }
                            Err(err) => {
                                self.video_is_resolving = false;
                                self.status_text = err.clone();
                                self.show_toast(err);
                            }
                        }
                    }
                }
                AsyncMessage::VideoPrefetchResolved {
                    video_id,
                    quality,
                    stream,
                } => {
                    self.video_prefetched = Some((video_id, quality, stream));
                }
                AsyncMessage::VideoMetadataLoaded {
                    video_id,
                    meta,
                    chapters,
                    related,
                    sub_count,
                } => {
                    if let Some(ref cur) = self.current_video {
                        if cur.id.eq_ignore_ascii_case(&video_id) {
                            self.current_video_meta = meta;
                            self.current_video_chapters = chapters;
                            self.current_video_related = related;
                            self.current_video_sub_count = sub_count;
                        }
                    }
                }
                AsyncMessage::VideoSponsorLoaded { video_id, segments } => {
                    if let Some(ref cur) = self.current_video {
                        if cur.id.eq_ignore_ascii_case(&video_id) {
                            self.current_video_sponsors = segments;
                        }
                    }
                }
                AsyncMessage::VideoStoryboardLoaded {
                    video_id,
                    storyboard,
                } => {
                    if let Some(ref cur) = self.current_video {
                        if cur.id.eq_ignore_ascii_case(&video_id) {
                            self.current_video_storyboard = storyboard;
                        }
                    }
                }
                AsyncMessage::StoryboardPreviewReady {
                    hover_sec,
                    width,
                    height,
                    rgba,
                } => {
                    if width > 0 && height > 0 && rgba.len() == (width * height * 4) as usize {
                        let img = ColorImage::from_rgba_unmultiplied(
                            [width as usize, height as usize],
                            &rgba,
                        );
                        let tex = ctx.load_texture("sb_preview", img, TextureOptions::LINEAR);
                        self.storyboard_preview_tex = Some((hover_sec, tex));
                    }
                }
                AsyncMessage::MusicStreamResolved {
                    request_id,
                    track,
                    result,
                    segments,
                } => {
                    if request_id == self.music_play_req_seq {
                        self.music_is_resolving = false;
                        self.music_sponsors = segments;
                        match result {
                            Ok(url) => {
                                if self.music_player.is_available() {
                                    self.music_player.open_and_play_async(
                                        url,
                                        None,
                                        0.0,
                                        self.config.music_volume,
                                        self.config.is_music_muted,
                                        None,
                                    );
                                    self.status_text =
                                        format!("Tocando: {} — {}", track.title, track.artist);
                                } else {
                                    self.status_text =
                                        format!("Falha ao tocar '{}'. Avançando...", track.title);
                                    self.play_next_music();
                                }
                            }
                            Err(e) => {
                                self.status_text = e;
                                self.play_next_music();
                            }
                        }
                    }
                }
                AsyncMessage::MusicBgStateChanged {
                    request_id,
                    index,
                    is_resolving,
                    sponsors,
                    status,
                } => {
                    if request_id >= self.music_play_req_seq {
                        self.music_play_req_seq = request_id;
                        self.music_current_index = index;
                        self.music_is_resolving = is_resolving;
                        self.music_sponsors = sponsors;
                        self.status_text = status;
                    }
                }
                AsyncMessage::ImageLoaded {
                    cache_key,
                    width,
                    height,
                    rgba,
                } => {
                    self.thumb_cache.insert(ctx, cache_key, width, height, rgba);
                }
                AsyncMessage::BrowserCookiesExtracted { browser, result } => {
                    self.auth_modal_busy = false;
                    match result {
                        Ok(count) => {
                            self.config.cookies_file_path =
                                get_vault_file_path().to_string_lossy().to_string();
                            self.config.browser_for_cookies = "none".to_string();
                            self.config.save();
                            GoogleSessionService::shared().invalidate_cache();
                            self.auth_modal_status = format!(
                                "Sessão do {} salva no cofre DPAPI ({} cookies).",
                                browser.to_uppercase(),
                                count
                            );
                            self.refresh_account_profile(true);
                            self.load_video_section(VideoNavSection::Home, false);
                        }
                        Err(err) => {
                            self.auth_modal_status = err;
                        }
                    }
                }
                AsyncMessage::ImportedPlaylistLoaded { name, url, tracks } => {
                    if !tracks.is_empty() {
                        self.config.playlists.push(UserPlaylist {
                            id: format!("pl_{:x}", next_pseudo_random_u64()),
                            name: name.clone(),
                            source_url: url,
                            tracks,
                        });
                        self.config.save();
                        let idx = self.config.playlists.len() - 1;
                        self.load_music_section(MusicNavSection::LocalPlaylist(idx), false);
                        self.status_text = format!("Playlist '{}' importada.", name);
                    } else {
                        self.status_text = "Nenhuma faixa encontrada na URL informada.".to_string();
                    }
                }
                AsyncMessage::ExternalPlayUrl(url) => {
                    self.play_video_by_url(&url);
                    bring_current_process_window_to_front();
                    ctx.send_viewport_cmd(egui::ViewportCommand::Focus);
                }
            }
        }
    }

    fn periodic_tick(&mut self, ctx: &egui::Context) {
        if self.dark_titlebar_applied < 3 {
            self.dark_titlebar_applied += 1;
            apply_dark_title_bar_to_current_process();
        }

        // Mantém os volumes independentes de Vídeo e Música sincronizados com o DirectSound
        self.video_player.sync_volume_if_needed();
        self.music_player.sync_volume_if_needed();

        if let Some((_, ts)) = self.center_osd {
            if ts.elapsed() < Duration::from_millis(700) {
                ctx.request_repaint_after(Duration::from_millis(40));
            } else {
                self.center_osd = None;
            }
        }

        if let Some((w, h, rgba)) = self.video_player.take_new_video_frame() {
            let color_img = ColorImage::from_rgba_unmultiplied([w as usize, h as usize], &rgba);
            if let Some(ref mut tex) = self.video_texture {
                tex.set(color_img, TextureOptions::LINEAR);
            } else {
                self.video_texture =
                    Some(ctx.load_texture("video_frame", color_img, TextureOptions::LINEAR));
            }
            ctx.request_repaint();
        }

        if self.current_video.is_some() {
            if self.video_player.is_playing() {
                let pos = self.video_player.get_position_seconds();

                if self.last_history_save.elapsed() >= Duration::from_secs(10) && pos > 3.0 {
                    self.save_current_video_progress(false);
                }

                if self.config.enable_sponsor_block
                    && self.config.auto_skip_sponsors
                    && !self.current_video_sponsors.is_empty()
                {
                    if let Some((skip_to, seg)) =
                        check_and_get_skip_target(pos, &self.current_video_sponsors)
                    {
                        if seg.uuid != self.last_skipped_video_sponsor_uuid {
                            self.last_skipped_video_sponsor_uuid = seg.uuid.clone();
                            self.video_player.seek_seconds(skip_to);
                            self.sponsor_skip_banner = Some((
                                format!("{} pulado", seg.category_label()),
                                Instant::now(),
                            ));
                        }
                    }
                }

                if !self.video_prefetch_triggered && pos >= 10.0 {
                    self.trigger_next_video_prefetch();
                }

                ctx.request_repaint_after(Duration::from_millis(33));
            } else if !self.video_is_resolving
                && self.video_ended_handled_seq != self.video_play_req_seq
                && self.video_player.is_ended()
            {
                self.video_ended_handled_seq = self.video_play_req_seq;
                self.save_current_video_progress(true);
                if let Some(next) = self.get_next_video_candidate(true) {
                    self.start_video_playback(next, Some(0.0));
                }
            }
        }

        if self.music_current_index.is_some() {
            let mstate = self.music_player.get_state();
            // Mantém a barra de progresso atualizando na UI durante Opening(1), Buffering(2), Playing(3) ou resolução
            if self.music_is_resolving || matches!(mstate, 1 | 2 | 3) {
                ctx.request_repaint_after(Duration::from_millis(200));
            }
        }

        if let Some(since) = self.config_dirty_since {
            if since.elapsed() >= Duration::from_millis(400) {
                self.config_dirty_since = None;
                self.config.save();
            }
        }

        if self.last_ram_check.elapsed() >= Duration::from_millis(2500) {
            self.last_ram_check = Instant::now();
            self.current_ram_mb = get_current_ram_usage_mb();
            self.ram_trim_counter += 1;
            if self.ram_trim_counter >= 24 {
                self.ram_trim_counter = 0;
                trim_memory();
            }
        }
    }
}

fn configure_windows_fonts_and_theme(ctx: &egui::Context) {
    // Carrega Segoe UI nativa do Windows (fonte exata do XAML original!)
    let mut fonts = FontDefinitions::default();
    if let Ok(segoe_bytes) = fs::read(r"C:\Windows\Fonts\segoeui.ttf") {
        fonts
            .font_data
            .insert("segoe_ui".to_owned(), Arc::new(FontData::from_owned(segoe_bytes)));
        if let Some(fam) = fonts.families.get_mut(&FontFamily::Proportional) {
            fam.insert(0, "segoe_ui".to_owned());
        }
    }
    ctx.set_fonts(fonts);

    let mut visuals = egui::Visuals::dark();
    visuals.panel_fill = BG_MAIN;
    visuals.window_fill = BG_SURFACE;
    visuals.extreme_bg_color = BG_SEARCH;
    visuals.faint_bg_color = BG_HOVER;
    visuals.widgets.noninteractive.bg_fill = BG_MAIN;
    visuals.widgets.noninteractive.fg_stroke = Stroke::new(1.0_f32, TEXT_PRIMARY);
    visuals.widgets.inactive.bg_fill = BG_HOVER;
    visuals.widgets.inactive.fg_stroke = Stroke::new(1.0_f32, TEXT_PRIMARY);
    visuals.widgets.hovered.bg_fill = BG_HOVER_ALT;
    visuals.widgets.hovered.fg_stroke = Stroke::new(1.0_f32, Color32::WHITE);
    visuals.widgets.active.bg_fill = YT_BLUE;
    visuals.widgets.active.fg_stroke = Stroke::new(1.0_f32, BG_MAIN);
    visuals.selection.bg_fill = Color32::from_rgba_unmultiplied(62, 166, 255, 80);
    ctx.set_visuals(visuals);

    // Scrollbar fina escura (DarkThinScrollBar do XAML)
    let mut style = (*ctx.style()).clone();
    style.spacing.scroll = egui::style::ScrollStyle {
        floating: true,
        bar_width: 7.0,
        floating_width: 7.0,
        ..egui::style::ScrollStyle::thin()
    };
    ctx.set_style(style);
}

// ============================================================================
// Implementação da UI (eframe::App) — Layout 1:1 com o WPF Original
// ============================================================================

impl eframe::App for LiteTubeApp {
    fn update(&mut self, ctx: &egui::Context, _frame: &mut eframe::Frame) {
        self.process_async_messages(ctx);
        self.periodic_tick(ctx);

        if self.video_is_fullscreen
            && (!self.video_watch_open || self.active_env != ActiveEnvironment::Video)
        {
            self.video_is_fullscreen = false;
            ctx.send_viewport_cmd(egui::ViewportCommand::Fullscreen(false));
        }

        // Atalhos globais de teclado (K / Espaço / F / J / L / M / Setas)
        if !ctx.wants_keyboard_input() {
            if ctx.input(|i| i.key_pressed(Key::Space) || i.key_pressed(Key::K)) {
                if self.active_env == ActiveEnvironment::Video && self.video_watch_open {
                    self.toggle_video_play_pause();
                } else if self.active_env == ActiveEnvironment::Music {
                    self.toggle_music_play_pause();
                }
            }
            if ctx.input(|i| i.key_pressed(Key::F) || i.key_pressed(Key::F11))
                && self.active_env == ActiveEnvironment::Video
                && self.video_watch_open
            {
                self.video_is_fullscreen = !self.video_is_fullscreen;
                ctx.send_viewport_cmd(egui::ViewportCommand::Fullscreen(
                    self.video_is_fullscreen,
                ));
            }
            if ctx.input(|i| i.key_pressed(Key::Escape)) && self.video_is_fullscreen {
                self.video_is_fullscreen = false;
                ctx.send_viewport_cmd(egui::ViewportCommand::Fullscreen(false));
            }
            if ctx.input(|i| i.key_pressed(Key::T))
                && self.active_env == ActiveEnvironment::Video
                && self.video_watch_open
            {
                if self.video_is_fullscreen {
                    self.video_is_fullscreen = false;
                    ctx.send_viewport_cmd(egui::ViewportCommand::Fullscreen(false));
                    self.config.is_theater_mode = true;
                } else {
                    self.config.is_theater_mode = !self.config.is_theater_mode;
                }
                self.config_dirty_since = Some(Instant::now());
                self.show_center_osd(if self.config.is_theater_mode {
                    "Modo Teatro"
                } else {
                    "Modo Padrão"
                });
            }
            if ctx.input(|i| i.key_pressed(Key::M))
                && self.active_env == ActiveEnvironment::Video
                && self.video_watch_open
            {
                self.config.is_video_muted = !self.config.is_video_muted;
                self.config.save();
                self.video_player
                    .set_volume(self.config.video_volume, self.config.is_video_muted);
                if self.config.is_video_muted {
                    self.show_center_osd("Mudo");
                } else {
                    let pct = (self.config.video_volume * 100.0).round() as i32;
                    self.show_center_osd(format!("Volume {}%", pct));
                }
            }
            if ctx.input(|i| i.key_pressed(Key::ArrowUp))
                && self.active_env == ActiveEnvironment::Video
                && self.video_watch_open
            {
                self.adjust_video_volume(0.05);
            }
            if ctx.input(|i| i.key_pressed(Key::ArrowDown))
                && self.active_env == ActiveEnvironment::Video
                && self.video_watch_open
            {
                self.adjust_video_volume(-0.05);
            }
            if ctx.input(|i| i.key_pressed(Key::ArrowRight) || i.key_pressed(Key::L))
                && self.active_env == ActiveEnvironment::Video
                && self.video_watch_open
            {
                let delta = if ctx.input(|i| i.key_pressed(Key::L)) { 10.0 } else { 5.0 };
                let pos = self.video_player.get_position_seconds();
                self.video_player.seek_seconds(pos + delta);
                self.show_center_osd(format!("+{}s", delta as i32));
            }
            if ctx.input(|i| i.key_pressed(Key::ArrowLeft) || i.key_pressed(Key::J))
                && self.active_env == ActiveEnvironment::Video
                && self.video_watch_open
            {
                let delta = if ctx.input(|i| i.key_pressed(Key::J)) { 10.0 } else { 5.0 };
                let pos = self.video_player.get_position_seconds();
                self.video_player.seek_seconds((pos - delta).max(0.0));
                self.show_center_osd(format!("-{}s", delta as i32));
            }
        }

        // 1. Top Header Bar (56px) e Barra de Status Inferior (24px)
        if !self.video_is_fullscreen {
            self.render_top_header_bar(ctx);
            self.render_bottom_status_bar(ctx);
        }

        // 2. Barra Inferior do Player de Música (82px, igual WpfMusicPlayerBar.xaml)
        if self.active_env == ActiveEnvironment::Music && !self.video_is_fullscreen {
            self.render_music_player_bar(ctx);
        }

        // 3. Sidebar Esquerda (216px expandida / 72px mini rail)
        // Na Watch Page o original recolhe a sidebar se não estiver expandida explicitamente
        if !self.video_is_fullscreen {
            self.render_left_sidebar(ctx);
        }

        // 4. Painel Central
        egui::CentralPanel::default()
            .frame(egui::Frame::NONE.fill(BG_MAIN))
            .show(ctx, |ui| match self.active_env {
                ActiveEnvironment::Video => {
                    if self.video_watch_open {
                        self.render_video_watch_page(ui, ctx);
                    } else {
                        self.render_video_catalog_page(ui, ctx);
                    }
                }
                ActiveEnvironment::Music => {
                    self.render_music_catalog_page(ui, ctx);
                }
            });

        // 5. Modais
        if self.show_auth_modal {
            self.render_google_auth_modal(ctx);
        }
        if self.show_open_url_modal {
            self.render_open_url_modal(ctx);
        }
        if self.show_new_playlist_modal {
            self.render_new_playlist_modal(ctx);
        }
        if self.show_import_playlist_modal {
            self.render_import_playlist_modal(ctx);
        }
    }

    fn on_exit(&mut self, _gl: Option<&eframe::glow::Context>) {
        self.music_supervisor_stop
            .store(true, std::sync::atomic::Ordering::Release);
        self.save_current_video_progress(false);
        if self.config_dirty_since.take().is_some() {
            self.config.save();
        }
    }
}

// ============================================================================
// Top Header Bar (WpfMainWindow.xaml) — 56px, Centralizado, Sem Emojis
// ============================================================================

impl LiteTubeApp {
    fn render_top_header_bar(&mut self, ctx: &egui::Context) {
        let mut search_bar_rect_out: Option<Rect> = None;

        egui::TopBottomPanel::top("top_header_bar")
            .exact_height(56.0)
            .show_separator_line(false)
            .frame(
                egui::Frame::NONE
                    .fill(BG_MAIN)
                    .inner_margin(Margin {
                        left: 10,
                        right: 14,
                        top: 0,
                        bottom: 0,
                    }),
            )
            .show(ctx, |ui| {
                let full_rect = ui.max_rect();
                let center_y = full_rect.center().y;

                // ---- COLUNA ESQUERDA: Hamburger + Logo + Switcher YouTube/YT Music ----
                let mut left_x = full_rect.left() + 4.0;

                // 1. Hamburger (38x38 circular)
                let ham_rect = Rect::from_center_size(
                    Pos2::new(left_x + 19.0, center_y),
                    Vec2::new(38.0, 38.0),
                );
                let ham_resp = ui.interact(ham_rect, ui.id().with("btn_ham"), Sense::click());
                if ham_resp.hovered() {
                    ui.ctx().set_cursor_icon(CursorIcon::PointingHand);
                    ui.painter()
                        .rect_filled(ham_rect, CornerRadius::same(19), BG_HOVER_ALT);
                }
                icons::draw_hamburger(ui.painter(), ham_rect, TEXT_PRIMARY);
                if ham_resp.clicked() {
                    self.sidebar_expanded = !self.sidebar_expanded;
                }
                left_x += 46.0;

                // 2. Marca LiteTube (30x21 pílula #FF0033 + "LiteTube" 17px Bold)
                let brand_rect = Rect::from_min_size(
                    Pos2::new(left_x, center_y - 16.0),
                    Vec2::new(112.0, 32.0),
                );
                let brand_resp = ui.interact(brand_rect, ui.id().with("btn_brand"), Sense::click());
                if brand_resp.hovered() {
                    ui.ctx().set_cursor_icon(CursorIcon::PointingHand);
                }
                let logo_pill = Rect::from_center_size(
                    Pos2::new(left_x + 15.0, center_y),
                    Vec2::new(30.0, 21.0),
                );
                ui.painter()
                    .rect_filled(logo_pill, CornerRadius::same(6), YT_RED);
                icons::draw_play_triangle(
                    ui.painter(),
                    Pos2::new(logo_pill.center().x + 1.0, logo_pill.center().y),
                    9.5,
                    Color32::WHITE,
                );
                ui.painter().text(
                    Pos2::new(left_x + 37.0, center_y - 0.5),
                    Align2::LEFT_CENTER,
                    "LiteTube",
                    FontId::new(17.5, FontFamily::Proportional),
                    TEXT_PRIMARY,
                );
                if brand_resp.clicked() {
                    if self.active_env == ActiveEnvironment::Video {
                        self.load_video_section(VideoNavSection::Home, false);
                    } else {
                        self.load_music_section(MusicNavSection::Recommended, false);
                    }
                }
                left_x += 126.0;

                // 3. Switcher Pill (#1A1A1D, ativo em #3EA6FF com texto #0F0F0F!)
                let sw_rect = Rect::from_min_size(
                    Pos2::new(left_x, center_y - 16.0),
                    Vec2::new(166.0, 32.0),
                );
                ui.painter().rect_filled(
                    sw_rect,
                    CornerRadius::same(16),
                    Color32::from_rgb(0x1A, 0x1A, 0x1D),
                );

                let vid_rect = Rect::from_min_max(
                    Pos2::new(sw_rect.left() + 3.0, sw_rect.top() + 3.0),
                    Pos2::new(sw_rect.center().x - 1.0, sw_rect.bottom() - 3.0),
                );
                let mus_rect = Rect::from_min_max(
                    Pos2::new(sw_rect.center().x + 1.0, sw_rect.top() + 3.0),
                    Pos2::new(sw_rect.right() - 3.0, sw_rect.bottom() - 3.0),
                );

                let is_vid = self.active_env == ActiveEnvironment::Video;
                let vid_resp = ui.interact(vid_rect, ui.id().with("env_vid"), Sense::click());
                if vid_resp.hovered() {
                    ui.ctx().set_cursor_icon(CursorIcon::PointingHand);
                }
                if is_vid {
                    ui.painter()
                        .rect_filled(vid_rect, CornerRadius::same(14), YT_BLUE);
                } else if vid_resp.hovered() {
                    ui.painter().rect_filled(
                        vid_rect,
                        CornerRadius::same(14),
                        Color32::from_rgb(0x26, 0x26, 0x2A),
                    );
                }
                ui.painter().text(
                    vid_rect.center(),
                    Align2::CENTER_CENTER,
                    "YouTube",
                    FontId::new(12.5, FontFamily::Proportional),
                    if is_vid { BG_MAIN } else { Color32::from_rgb(0xB9, 0xB9, 0xC0) },
                );
                if vid_resp.clicked() {
                    self.active_env = ActiveEnvironment::Video;
                }

                let is_mus = self.active_env == ActiveEnvironment::Music;
                let mus_resp = ui.interact(mus_rect, ui.id().with("env_mus"), Sense::click());
                if mus_resp.hovered() {
                    ui.ctx().set_cursor_icon(CursorIcon::PointingHand);
                }
                if is_mus {
                    ui.painter()
                        .rect_filled(mus_rect, CornerRadius::same(14), YT_BLUE);
                } else if mus_resp.hovered() {
                    ui.painter().rect_filled(
                        mus_rect,
                        CornerRadius::same(14),
                        Color32::from_rgb(0x26, 0x26, 0x2A),
                    );
                }
                ui.painter().text(
                    mus_rect.center(),
                    Align2::CENTER_CENTER,
                    "YT Music",
                    FontId::new(12.5, FontFamily::Proportional),
                    if is_mus { BG_MAIN } else { Color32::from_rgb(0xB9, 0xB9, 0xC0) },
                );
                if mus_resp.clicked() {
                    self.active_env = ActiveEnvironment::Music;
                    self.save_current_video_progress(false);
                    if self.video_is_resolving {
                        self.video_is_resolving = false;
                        self.video_play_req_seq = self.video_play_req_seq.wrapping_add(1);
                        self.video_ended_handled_seq = self.video_play_req_seq;
                        self.video_player.stop_async();
                    } else if self.video_player.is_playing() {
                        self.video_player.pause();
                    }
                    if self.music_items.is_empty() {
                        self.load_music_section(MusicNavSection::Recommended, false);
                    }
                }
                let left_end_x = sw_rect.right() + 16.0;

                // ---- COLUNA DIREITA: Refresh + RAM Badge + Conta Google com Avatar Circular ----
                let mut right_x = full_rect.right();

                // 1. Botão Conta Google (Height 36, #1F1F1F, Avatar circular 24x24 + Nome)
                let (acc_name, acc_fg) = if self.account_profile.session_expired {
                    ("Sessão Expirada".to_string(), YELLOW_WARN)
                } else if self.account_profile.is_authenticated
                    && !self.account_profile.display_name.is_empty()
                {
                    (
                        truncate_chars(&self.account_profile.display_name, 18),
                        TEXT_PRIMARY,
                    )
                } else if GoogleSessionService::shared().has_stored_session(&self.config) {
                    ("Conectado".to_string(), GREEN_CONN)
                } else {
                    ("Conta Google".to_string(), TEXT_PRIMARY)
                };

                let acc_w = (acc_name.chars().count() as f32 * 7.2 + 52.0).clamp(112.0, 200.0);
                let acc_rect = Rect::from_min_size(
                    Pos2::new(right_x - acc_w, center_y - 18.0),
                    Vec2::new(acc_w, 36.0),
                );
                let acc_resp = ui.interact(acc_rect, ui.id().with("btn_google_acc"), Sense::click());
                if acc_resp.hovered() {
                    ui.ctx().set_cursor_icon(CursorIcon::PointingHand);
                }
                ui.painter().rect_filled(
                    acc_rect,
                    CornerRadius::same(18),
                    if acc_resp.hovered() { BG_HOVER_ALT } else { BG_HOVER },
                );
                // Avatar circular 24x24
                let av_rect = Rect::from_center_size(
                    Pos2::new(acc_rect.left() + 20.0, center_y),
                    Vec2::new(24.0, 24.0),
                );
                let av_tex = self.thumb_cache.get_or_request_circle(
                    &self.account_profile.avatar_url,
                    &self.tx,
                    ctx,
                );
                if let Some(ref tex) = av_tex {
                    ui.painter().image(
                        tex.id(),
                        av_rect,
                        Rect::from_min_max(Pos2::ZERO, Pos2::new(1.0, 1.0)),
                        Color32::WHITE,
                    );
                } else {
                    ui.painter().circle_filled(
                        av_rect.center(),
                        12.0,
                        Color32::from_rgb(0x2E, 0x2E, 0x34),
                    );
                    icons::draw_person(ui.painter(), av_rect.center(), 13.0, TEXT_SECONDARY);
                }
                ui.painter().text(
                    Pos2::new(acc_rect.left() + 38.0, center_y),
                    Align2::LEFT_CENTER,
                    &acc_name,
                    FontId::new(12.5, FontFamily::Proportional),
                    acc_fg,
                );
                if acc_resp.clicked() {
                    self.show_auth_modal = true;
                }
                right_x = acc_rect.left() - 8.0;

                // 2. Badge RAM (#18181B, CornerRadius 14, #4ADE80)
                let ram_str = format!("{:.1} MB", self.current_ram_mb);
                let ram_w = 72.0;
                let ram_rect = Rect::from_min_size(
                    Pos2::new(right_x - ram_w, center_y - 14.0),
                    Vec2::new(ram_w, 28.0),
                );
                ui.painter()
                    .rect_filled(ram_rect, CornerRadius::same(14), BG_SURFACE);
                ui.painter().text(
                    ram_rect.center(),
                    Align2::CENTER_CENTER,
                    ram_str,
                    FontId::new(12.0, FontFamily::Proportional),
                    GREEN_RAM,
                );
                ui.interact(ram_rect, ui.id().with("ram_tip"), Sense::hover())
                    .on_hover_text("Consumo real de memória RAM física (Private Working Set)");
                right_x = ram_rect.left() - 6.0;

                // 3. Botão Refresh circular (36x36)
                let ref_rect = Rect::from_center_size(
                    Pos2::new(right_x - 18.0, center_y),
                    Vec2::new(36.0, 36.0),
                );
                let ref_resp = ui
                    .interact(ref_rect, ui.id().with("btn_refresh"), Sense::click())
                    .on_hover_text("Atualizar página atual");
                if ref_resp.hovered() {
                    ui.ctx().set_cursor_icon(CursorIcon::PointingHand);
                    ui.painter()
                        .rect_filled(ref_rect, CornerRadius::same(18), BG_HOVER_ALT);
                }
                icons::draw_refresh(ui.painter(), ref_rect.center(), 17.0, TEXT_NAV);
                if ref_resp.clicked() {
                    if self.active_env == ActiveEnvironment::Video {
                        let sec = self.video_nav;
                        self.load_video_section(sec, false);
                    } else {
                        let sec = self.music_nav;
                        self.load_music_section(sec, false);
                    }
                }
                let right_start_x = ref_rect.left() - 16.0;

                // ---- COLUNA CENTRAL: YouTube Pill Search Box (MaxWidth 560, Height 38) ----
                let avail_mid = (right_start_x - left_end_x).max(160.0);
                let sb_w = avail_mid.min(560.0);
                let sb_cx = (left_end_x + right_start_x) * 0.5;
                let sb_rect = Rect::from_center_size(Pos2::new(sb_cx, center_y), Vec2::new(sb_w, 38.0));
                search_bar_rect_out = Some(sb_rect);

                ui.painter()
                    .rect_filled(sb_rect, CornerRadius::same(19), BG_SEARCH);
                ui.painter().rect_stroke(
                    sb_rect,
                    CornerRadius::same(19),
                    Stroke::new(1.0_f32, BORDER_SUBTLE),
                    StrokeKind::Inside,
                );

                // Botão Lupa à direita (Width 52, #222226, cantos direitos arredondados)
                let go_rect = Rect::from_min_max(
                    Pos2::new(sb_rect.right() - 52.0, sb_rect.top() + 1.0),
                    Pos2::new(sb_rect.right() - 1.0, sb_rect.bottom() - 1.0),
                );
                let go_resp = ui
                    .interact(go_rect, ui.id().with("btn_search_go"), Sense::click())
                    .on_hover_text("Pesquisar");
                if go_resp.hovered() {
                    ui.ctx().set_cursor_icon(CursorIcon::PointingHand);
                }
                ui.painter().rect_filled(
                    go_rect,
                    CornerRadius {
                        nw: 0,
                        ne: 18,
                        sw: 0,
                        se: 18,
                    },
                    if go_resp.hovered() {
                        Color32::from_rgb(0x2C, 0x2C, 0x32)
                    } else {
                        BG_SEARCH_BTN
                    },
                );
                icons::draw_search(ui.painter(), go_rect.center(), 16.0, TEXT_NAV);
                if go_resp.clicked() {
                    self.execute_search(false);
                }

                // Botão limpar (✕) se houver texto
                let mut input_right = go_rect.left() - 6.0;
                if !self.search_query.is_empty() {
                    let clr_rect = Rect::from_center_size(
                        Pos2::new(go_rect.left() - 16.0, center_y),
                        Vec2::new(26.0, 26.0),
                    );
                    let clr_resp = ui.interact(clr_rect, ui.id().with("btn_clr"), Sense::click());
                    if clr_resp.hovered() {
                        ui.ctx().set_cursor_icon(CursorIcon::PointingHand);
                        ui.painter()
                            .rect_filled(clr_rect, CornerRadius::same(13), BG_HOVER_ALT);
                    }
                    ui.painter().text(
                        clr_rect.center(),
                        Align2::CENTER_CENTER,
                        "×",
                        FontId::new(15.0, FontFamily::Proportional),
                        TEXT_SECONDARY,
                    );
                    if clr_resp.clicked() {
                        self.search_query.clear();
                    }
                    input_right = clr_rect.left() - 4.0;
                }

                // Campo de texto interno
                let text_rect = Rect::from_min_max(
                    Pos2::new(sb_rect.left() + 16.0, sb_rect.top() + 7.0),
                    Pos2::new(input_right, sb_rect.bottom() - 5.0),
                );
                let mut edit_ui = ui.new_child(
                    egui::UiBuilder::new()
                        .max_rect(text_rect)
                        .layout(Layout::left_to_right(Align::Center)),
                );
                let edit_resp = edit_ui.add(
                    egui::TextEdit::singleline(&mut self.search_query)
                        .hint_text(RichText::new("Pesquisar").color(TEXT_MUTED))
                        .font(FontId::new(14.0, FontFamily::Proportional))
                        .frame(false)
                        .desired_width(text_rect.width()),
                );
                if edit_resp.gained_focus() {
                    self.search_history_open = true;
                }
                if edit_resp.lost_focus() && ui.input(|i| i.key_pressed(Key::Enter)) {
                    self.execute_search(false);
                }
            });

        // Dropdown Flutuante de Histórico de Buscas (igual SearchHistoryPopup no XAML)
        if self.search_history_open && !self.config.search_history.is_empty() {
            if let Some(sb_rect) = search_bar_rect_out {
                let filter = self.search_query.trim().to_ascii_lowercase();
                let matches: Vec<String> = self
                    .config
                    .search_history
                    .iter()
                    .filter(|h| filter.is_empty() || h.to_ascii_lowercase().contains(&filter))
                    .take(8)
                    .cloned()
                    .collect();

                if !matches.is_empty() {
                    let mut pick_query: Option<String> = None;
                    let mut remove_query: Option<String> = None;

                    let area_resp = egui::Area::new(egui::Id::new("search_history_popup"))
                        .order(egui::Order::Foreground)
                        .fixed_pos(Pos2::new(sb_rect.left(), sb_rect.bottom() + 4.0))
                        .show(ctx, |ui| {
                            egui::Frame::NONE
                                .fill(Color32::from_rgb(0x1B, 0x1B, 0x1F))
                                .stroke(Stroke::new(1.0_f32, Color32::from_rgb(0x2E, 0x2E, 0x34)))
                                .corner_radius(CornerRadius::same(12))
                                .inner_margin(Margin::same(6))
                                .show(ui, |ui| {
                                    ui.set_width(sb_rect.width() - 12.0);
                                    for item in matches {
                                        ui.horizontal(|ui| {
                                            let row_w = ui.available_width() - 28.0;
                                            let (r_rect, r_resp) = ui.allocate_exact_size(
                                                Vec2::new(row_w, 30.0),
                                                Sense::click(),
                                            );
                                            if r_resp.hovered() {
                                                ui.ctx().set_cursor_icon(CursorIcon::PointingHand);
                                                ui.painter().rect_filled(
                                                    r_rect,
                                                    CornerRadius::same(6),
                                                    BG_HOVER_ALT,
                                                );
                                            }
                                            icons::draw_history(
                                                ui.painter(),
                                                Pos2::new(r_rect.left() + 14.0, r_rect.center().y),
                                                13.0,
                                                TEXT_SECONDARY,
                                            );
                                            ui.painter().text(
                                                Pos2::new(r_rect.left() + 32.0, r_rect.center().y),
                                                Align2::LEFT_CENTER,
                                                &item,
                                                FontId::new(13.0, FontFamily::Proportional),
                                                TEXT_PRIMARY,
                                            );
                                            if r_resp.clicked() {
                                                pick_query = Some(item.clone());
                                            }

                                            if ui
                                                .add(
                                                    egui::Button::new(
                                                        RichText::new("×")
                                                            .size(13.0)
                                                            .color(TEXT_MUTED),
                                                    )
                                                    .fill(Color32::TRANSPARENT),
                                                )
                                                .clicked()
                                            {
                                                remove_query = Some(item);
                                            }
                                        });
                                    }
                                });
                        });

                    if let Some(q) = pick_query {
                        self.search_query = q;
                        self.execute_search(false);
                    } else if let Some(rem) = remove_query {
                        self.config.remove_search_history(&rem);
                    } else if ctx.input(|i| i.pointer.any_click()) {
                        if let Some(pos) = ctx.input(|i| i.pointer.interact_pos()) {
                            if !sb_rect.contains(pos) && !area_resp.response.rect.contains(pos) {
                                self.search_history_open = false;
                            }
                        }
                    }
                } else if ctx.input(|i| i.pointer.any_click()) {
                    if let Some(pos) = ctx.input(|i| i.pointer.interact_pos()) {
                        if !sb_rect.contains(pos) {
                            self.search_history_open = false;
                        }
                    }
                }
            }
        }
    }

    // ========================================================================
    // Barra de Status Inferior (WpfMainWindow.xaml: 24px, #0A0A0C)
    // ========================================================================

    fn render_bottom_status_bar(&mut self, ctx: &egui::Context) {
        egui::TopBottomPanel::bottom("bottom_status_bar")
            .exact_height(24.0)
            .show_separator_line(false)
            .frame(
                egui::Frame::NONE
                    .fill(BG_STATUS)
                    .inner_margin(Margin::symmetric(14, 3)),
            )
            .show(ctx, |ui| {
                ui.horizontal_centered(|ui| {
                    ui.label(
                        RichText::new(&self.status_text)
                            .size(11.5)
                            .color(Color32::from_rgb(0x8A, 0x8A, 0x92)),
                    );
                    ui.with_layout(Layout::right_to_left(Align::Center), |ui| {
                        ui.label(
                            RichText::new("LibVLC D3D11 • Rust Native")
                                .size(11.0)
                                .color(Color32::from_rgb(0x66, 0x66, 0x70)),
                        );
                    });
                });
            });
    }

    // ========================================================================
    // Sidebar Esquerda (WpfVideoView.xaml / WpfMusicView.xaml: 216px ou 72px)
    // ========================================================================

    fn render_left_sidebar(&mut self, ctx: &egui::Context) {
        // Na Watch Page do YouTube, o original recolhe para mini rail (ou esconde),
        // mantendo o usuário focado no vídeo; clicar no Hamburger expande quando quiser.
        let width = if self.sidebar_expanded { 216.0 } else { 72.0 };
        let margin = if self.sidebar_expanded {
            Margin {
                left: 10,
                right: 10,
                top: 8,
                bottom: 12,
            }
        } else {
            Margin {
                left: 4,
                right: 4,
                top: 8,
                bottom: 12,
            }
        };

        egui::SidePanel::left("left_sidebar")
            .exact_width(width)
            .resizable(false)
            .show_separator_line(false)
            .frame(egui::Frame::NONE.fill(BG_MAIN).inner_margin(margin))
            .show(ctx, |ui| {
                ScrollArea::vertical().show(ui, |ui| match self.active_env {
                    ActiveEnvironment::Video => self.render_video_sidebar(ui),
                    ActiveEnvironment::Music => self.render_music_sidebar(ui),
                });
            });
    }

    fn render_video_sidebar(&mut self, ui: &mut egui::Ui) {
        let expanded = self.sidebar_expanded;
        let nav_list = [
            (VideoNavSection::Home, "Início"),
            (VideoNavSection::Subscriptions, "Inscrições"),
            (VideoNavSection::History, "Histórico"),
            (VideoNavSection::Playlists, "Playlists"),
            (VideoNavSection::Saved, "Você"),
        ];

        for (sec, label) in nav_list {
            let active = !self.video_watch_open && self.video_nav == sec;
            let clicked = draw_sidebar_item(ui, label, active, expanded, |p, c, sz, col| {
                match sec {
                    VideoNavSection::Home => icons::draw_home(p, c, sz, col, active),
                    VideoNavSection::Subscriptions => icons::draw_subscriptions(p, c, sz, col),
                    VideoNavSection::History => icons::draw_history(p, c, sz, col),
                    VideoNavSection::Playlists => icons::draw_playlists(p, c, sz, col),
                    VideoNavSection::Saved => icons::draw_person(p, c, sz, col),
                    _ => {}
                }
            });
            if clicked {
                self.load_video_section(sec, false);
            }
        }

        if expanded {
            ui.add_space(8.0);
            let (div_r, _) = ui.allocate_exact_size(Vec2::new(ui.available_width(), 1.0), Sense::hover());
            ui.painter().rect_filled(div_r, CornerRadius::ZERO, DIVIDER_COLOR);
            ui.add_space(8.0);
        }

        if self.current_video.is_some() {
            let active = self.video_watch_open;
            if draw_sidebar_item(ui, "Assistindo", active, expanded, |p, c, sz, _| {
                icons::draw_play_triangle(p, c, sz * 0.85, YT_RED);
            }) {
                self.video_watch_open = true;
            }
        }

        if expanded {
            if draw_sidebar_item(ui, "Abrir Link", false, true, |p, c, sz, col| {
                icons::draw_link(p, c, sz, col);
            }) {
                self.show_open_url_modal = true;
            }
        }
    }

    fn render_music_sidebar(&mut self, ui: &mut egui::Ui) {
        let expanded = self.sidebar_expanded;
        let nav_list = [
            (MusicNavSection::Recommended, "Início"),
            (MusicNavSection::Explore, "Explorar"),
            (
                MusicNavSection::AccountPlaylists,
                if expanded { "Playlists (Conta)" } else { "Playlists" },
            ),
            (MusicNavSection::Favorites, "Favoritos"),
            (
                MusicNavSection::Queue,
                if expanded { "Fila Atual" } else { "Fila" },
            ),
        ];

        for (sec, label) in nav_list {
            let active = self.music_nav == sec;
            let clicked = draw_sidebar_item(ui, label, active, expanded, |p, c, sz, col| {
                match sec {
                    MusicNavSection::Recommended => icons::draw_home(p, c, sz, col, active),
                    MusicNavSection::Explore => icons::draw_explore(p, c, sz, col),
                    MusicNavSection::AccountPlaylists => icons::draw_playlists(p, c, sz, col),
                    MusicNavSection::Favorites => icons::draw_heart(p, c, sz, col, active),
                    MusicNavSection::Queue => icons::draw_queue(p, c, sz, col),
                    _ => {}
                }
            });
            if clicked {
                self.load_music_section(sec, false);
            }
        }

        if expanded {
            ui.add_space(10.0);
            let (div_r, _) = ui.allocate_exact_size(Vec2::new(ui.available_width(), 1.0), Sense::hover());
            ui.painter().rect_filled(div_r, CornerRadius::ZERO, DIVIDER_COLOR);
            ui.add_space(10.0);

            ui.horizontal(|ui| {
                ui.add_space(12.0);
                ui.label(
                    RichText::new("PLAYLISTS LOCAIS")
                        .size(11.0)
                        .strong()
                        .color(TEXT_MUTED),
                );
            });
            ui.add_space(8.0);

            let btn_w = ui.available_width() - 8.0;
            ui.horizontal(|ui| {
                ui.add_space(4.0);
                if ui
                    .add(
                        egui::Button::new(
                            RichText::new("＋  Nova Playlist")
                                .size(12.5)
                                .color(TEXT_PRIMARY),
                        )
                        .fill(BG_HOVER)
                        .corner_radius(CornerRadius::same(16))
                        .min_size(Vec2::new(btn_w, 32.0)),
                    )
                    .clicked()
                {
                    self.show_new_playlist_modal = true;
                }
            });
            ui.add_space(6.0);
            ui.horizontal(|ui| {
                ui.add_space(4.0);
                if ui
                    .add(
                        egui::Button::new(
                            RichText::new("↓  Importar URL")
                                .size(12.5)
                                .color(TEXT_PRIMARY),
                        )
                        .fill(BG_HOVER)
                        .corner_radius(CornerRadius::same(16))
                        .min_size(Vec2::new(btn_w, 32.0)),
                    )
                    .clicked()
                {
                    self.show_import_playlist_modal = true;
                }
            });
            ui.add_space(10.0);

            let mut clicked_pl = None;
            for (idx, pl) in self.config.playlists.iter().enumerate() {
                let active = self.music_nav == MusicNavSection::LocalPlaylist(idx);
                let (rect, resp) = ui.allocate_exact_size(
                    Vec2::new(ui.available_width(), 34.0),
                    Sense::click(),
                );
                if resp.hovered() {
                    ui.ctx().set_cursor_icon(CursorIcon::PointingHand);
                }
                if active {
                    ui.painter()
                        .rect_filled(rect, CornerRadius::same(8), Color32::from_rgb(0x26, 0x26, 0x2A));
                } else if resp.hovered() {
                    ui.painter()
                        .rect_filled(rect, CornerRadius::same(8), BG_HOVER);
                }
                ui.painter().text(
                    Pos2::new(rect.left() + 12.0, rect.center().y),
                    Align2::LEFT_CENTER,
                    truncate_chars(&pl.name, 22),
                    FontId::new(13.0, FontFamily::Proportional),
                    if active { TEXT_PRIMARY } else { TEXT_NAV },
                );
                if resp.clicked() {
                    clicked_pl = Some(idx);
                }
            }
            if let Some(idx) = clicked_pl {
                self.load_music_section(MusicNavSection::LocalPlaylist(idx), false);
            }
        }
    }

    // ========================================================================
    // Catálogo de Vídeos (WpfVideoView.xaml + WpfMediaListControl.xaml)
    // ========================================================================

    fn render_video_catalog_page(&mut self, ui: &mut egui::Ui, ctx: &egui::Context) {
        if self.video_nav == VideoNavSection::Channel {
            self.render_channel_banner(ui, ctx);
        }

        // Chips de filtro (igual SearchChipsBar: Tudo, Vídeos, Canais, Sem Shorts, SponsorBlock)
        if self.video_nav == VideoNavSection::Search {
            ui.add_space(6.0);
            ui.horizontal(|ui| {
                ui.add_space(20.0);
                for (id, lbl) in [("all", "Tudo"), ("videos", "Vídeos"), ("channels", "Canais")] {
                    let active = self.video_search_chip == id;
                    if pill_chip_button(ui, lbl, active) {
                        self.video_search_chip = id.to_string();
                    }
                    ui.add_space(4.0);
                }
                if pill_chip_button(ui, "Sem Shorts", self.config.hide_shorts) {
                    self.config.hide_shorts = !self.config.hide_shorts;
                    self.config.save();
                }
                ui.add_space(4.0);
                let sb_lbl = if self.config.enable_sponsor_block {
                    "SponsorBlock: ON"
                } else {
                    "SponsorBlock: OFF"
                };
                if pill_chip_button(ui, sb_lbl, self.config.enable_sponsor_block) {
                    self.config.enable_sponsor_block = !self.config.enable_sponsor_block;
                    self.config.save();
                }
            });
        }

        // Header da Seção (Margin 20,8,20,6; FontSize 15.5 SemiBold)
        ui.add_space(8.0);
        ui.horizontal(|ui| {
            ui.add_space(20.0);
            if self.video_nav == VideoNavSection::PlaylistItems {
                if pill_chip_button(ui, "← Voltar para Playlists", false) {
                    self.load_video_section(VideoNavSection::Playlists, false);
                }
                ui.add_space(10.0);
            }

            ui.label(
                RichText::new(&self.video_feed_header)
                    .size(15.5)
                    .strong()
                    .color(TEXT_PRIMARY),
            );

            ui.with_layout(Layout::right_to_left(Align::Center), |ui| {
                ui.add_space(20.0);
                if self.video_nav == VideoNavSection::PlaylistItems && !self.video_items.is_empty() {
                    if white_pill_button(ui, "▶  Reproduzir tudo") {
                        let items = self.video_items.clone();
                        if let Some(first) = items.first().cloned() {
                            self.video_up_next_queue = items.into_iter().skip(1).collect();
                            self.start_video_playback(first, Some(0.0));
                        }
                    }
                }
            });
        });
        ui.add_space(6.0);

        if self.video_is_loading_feed && self.video_items.is_empty() {
            ui.centered_and_justified(|ui| {
                ui.label(
                    RichText::new("Carregando vídeos...")
                        .size(14.5)
                        .color(TEXT_SECONDARY),
                );
            });
            return;
        }

        if self.video_items.is_empty() {
            ui.centered_and_justified(|ui| {
                ui.label(
                    RichText::new(&self.status_text)
                        .size(14.0)
                        .color(TEXT_SECONDARY),
                );
            });
            return;
        }

        let hide_shorts = self.config.hide_shorts;
        let chip = self.video_search_chip.clone();
        let is_search = self.video_nav == VideoNavSection::Search;

        let filtered: Vec<TrackItem> = self
            .video_items
            .iter()
            .filter(|it| {
                if hide_shorts && it.is_short() {
                    return false;
                }
                if is_search {
                    if chip == "videos" && (it.is_channel() || it.is_playlist_card()) {
                        return false;
                    }
                    if chip == "channels" && !it.is_channel() {
                        return false;
                    }
                }
                true
            })
            .cloned()
            .collect();

        let mut action_play: Option<TrackItem> = None;
        let mut action_channel: Option<(String, String)> = None;
        let mut action_save: Option<TrackItem> = None;
        let mut action_queue: Option<TrackItem> = None;
        let mut action_load_more = false;

        ScrollArea::vertical()
            .auto_shrink([false, false])
            .show(ui, |ui| {
                let avail_w = (ui.available_width() - 28.0).max(260.0);
                // Exatamente 1 a 4 colunas responsivas como WpfMediaListControl
                let cols: usize = if avail_w >= 1120.0 {
                    4
                } else if avail_w >= 820.0 {
                    3
                } else if avail_w >= 540.0 {
                    2
                } else {
                    1
                };
                let gap = 16.0;
                let card_w = ((avail_w - (gap * (cols.saturating_sub(1) as f32))) / cols as f32)
                    .clamp(220.0, 440.0);
                let thumb_h = (card_w * 9.0 / 16.0).round();
                let card_h = thumb_h + 98.0;
                let total_row_w = (card_w * cols as f32) + (gap * (cols.saturating_sub(1) as f32));
                let left_pad = ((ui.available_width() - total_row_w) * 0.5).max(14.0);

                for chunk in filtered.chunks(cols) {
                    ui.horizontal(|ui| {
                        ui.add_space(left_pad);
                        for item in chunk {
                            let is_saved = self.config.is_saved_video(&item.id);
                            let ratio =
                                WatchHistoryService::instance().get_watch_progress_ratio(&item.id);
                            let thumb_tex = self.thumb_cache.get_or_request_rect(
                                &item.thumbnail_url(),
                                &self.tx,
                                ctx,
                            );
                            let av_url = if !item.channel_avatar_url.is_empty() {
                                item.channel_avatar_url.clone()
                            } else {
                                crate::youtube::get_cached_channel_avatar_url(
                                    &item.channel_id,
                                    &item.artist,
                                )
                            };
                            let av_tex =
                                self.thumb_cache
                                    .get_or_request_circle(&av_url, &self.tx, ctx);

                            let (c_card, c_ch, c_save, c_queue) = render_youtube_grid_card(
                                ui,
                                item,
                                Vec2::new(card_w, card_h),
                                thumb_h,
                                thumb_tex,
                                av_tex,
                                is_saved,
                                ratio as f32,
                            );

                            if c_card {
                                action_play = Some(item.clone());
                            }
                            if c_ch {
                                if item.is_channel() {
                                    let cid = if !item.channel_id.is_empty() {
                                        item.channel_id.clone()
                                    } else {
                                        item.id.trim_start_matches("CH_").to_string()
                                    };
                                    action_channel = Some((cid, item.title.clone()));
                                } else {
                                    action_channel =
                                        Some((item.channel_id.clone(), item.artist.clone()));
                                }
                            }
                            if c_save {
                                action_save = Some(item.clone());
                            }
                            if c_queue {
                                action_queue = Some(item.clone());
                            }
                            ui.add_space(gap);
                        }
                    });
                    ui.add_space(16.0);
                }

                if !self.video_continuation.is_empty() {
                    ui.add_space(6.0);
                    ui.vertical_centered(|ui| {
                        let lbl = if self.video_is_loading_feed {
                            "Carregando mais vídeos..."
                        } else {
                            "Carregar mais vídeos"
                        };
                        if ui
                            .add_enabled(
                                !self.video_is_loading_feed,
                                egui::Button::new(RichText::new(lbl).size(13.0).color(TEXT_PRIMARY))
                                    .fill(BG_HOVER)
                                    .corner_radius(CornerRadius::same(18))
                                    .min_size(Vec2::new(200.0, 36.0)),
                            )
                            .clicked()
                        {
                            action_load_more = true;
                        }
                    });
                    ui.add_space(24.0);
                }
            });

        if let Some(v) = action_play {
            self.start_video_playback(v, None);
        }
        if let Some((cid, cname)) = action_channel {
            self.open_channel(&cid, &cname, "home");
        }
        if let Some(v) = action_save {
            let saved = self.config.toggle_saved_video(&v);
            self.status_text = if saved {
                format!("Salvo em Você: {}", v.title)
            } else {
                format!("Removido de Você: {}", v.title)
            };
            if self.video_nav == VideoNavSection::Saved {
                self.video_items = self.config.saved_videos.clone();
            }
        }
        if let Some(v) = action_queue {
            self.video_up_next_queue
                .retain(|x| !x.id.eq_ignore_ascii_case(&v.id));
            self.video_up_next_queue.insert(0, v.clone());
            self.status_text = format!("Adicionado a seguir na fila: {}", v.title);
        }
        if action_load_more {
            if self.video_nav == VideoNavSection::Search {
                self.execute_search(true);
            } else {
                let sec = self.video_nav;
                self.load_video_section(sec, true);
            }
        }
    }

    fn render_channel_banner(&mut self, ui: &mut egui::Ui, ctx: &egui::Context) {
        let ch = self.active_channel_data.clone();
        let av_tex = self
            .thumb_cache
            .get_or_request_circle(&ch.avatar_url, &self.tx, ctx);

        egui::Frame::NONE
            .fill(Color32::from_rgb(0x16, 0x16, 0x16))
            .corner_radius(CornerRadius::same(12))
            .inner_margin(Margin::symmetric(18, 14))
            .outer_margin(Margin {
                left: 18,
                right: 18,
                top: 8,
                bottom: 8,
            })
            .show(ui, |ui| {
                ui.vertical(|ui| {
                    ui.horizontal(|ui| {
                        if pill_chip_button(ui, "← Voltar", false) {
                            self.load_video_section(VideoNavSection::Home, false);
                        }
                        ui.add_space(12.0);
                        // Avatar 54x54 circular
                        let (av_r, _) = ui.allocate_exact_size(Vec2::new(54.0, 54.0), Sense::hover());
                        draw_circular_avatar(ui.painter(), av_r, &ch.channel_name, av_tex.as_ref());
                        ui.add_space(12.0);
                        ui.vertical(|ui| {
                            ui.label(
                                RichText::new(&ch.channel_name)
                                    .size(20.0)
                                    .strong()
                                    .color(TEXT_PRIMARY),
                            );
                            if !ch.subscriber_text.is_empty() {
                                ui.label(
                                    RichText::new(&ch.subscriber_text)
                                        .size(12.5)
                                        .color(TEXT_SECONDARY),
                                );
                            }
                        });
                    });
                    ui.add_space(12.0);
                    ui.horizontal(|ui| {
                        for (tab_id, lbl) in [
                            ("home", "Início"),
                            ("videos", "Vídeos"),
                            ("playlists", "Playlists"),
                        ] {
                            let active = ch.active_tab == tab_id;
                            let (r, resp) = ui.allocate_exact_size(Vec2::new(92.0, 32.0), Sense::click());
                            if resp.hovered() {
                                ui.ctx().set_cursor_icon(CursorIcon::PointingHand);
                            }
                            ui.painter().text(
                                r.center(),
                                Align2::CENTER_CENTER,
                                lbl,
                                FontId::new(13.5, FontFamily::Proportional),
                                if active || resp.hovered() { TEXT_PRIMARY } else { TEXT_SECONDARY },
                            );
                            if active {
                                let line = Rect::from_min_max(
                                    Pos2::new(r.left() + 8.0, r.bottom() - 2.0),
                                    Pos2::new(r.right() - 8.0, r.bottom()),
                                );
                                ui.painter().rect_filled(line, CornerRadius::ZERO, TEXT_PRIMARY);
                            }
                            if resp.clicked() {
                                self.open_channel(&ch.channel_id, &ch.channel_name, tab_id);
                            }
                        }
                    });
                });
            });
    }

    // ========================================================================
    // Watch Page (WpfVideoView.xaml + WpfVideoPlayerOverlay.xaml)
    // ========================================================================

    fn render_video_watch_page(&mut self, ui: &mut egui::Ui, ctx: &egui::Context) {
        if self.video_is_fullscreen {
            let rect = ui.available_rect_before_wrap();
            self.render_video_stage_and_glass_hud(ui, ctx, rect);
            return;
        }

        let mut play_related: Option<TrackItem> = None;
        let mut open_ch: Option<(String, String)> = None;
        let mut queue_rel: Option<TrackItem> = None;

        let viewport_h = ui.available_height();
        let is_theater = self.config.is_theater_mode;

        ScrollArea::vertical()
            .auto_shrink([false, false])
            .show(ui, |ui| {
                let full_w = ui.available_width().max(480.0);

                // ---- MODO TEATRO (#full-bleed-container: 100% largura, #000000, 56.25vw limitado a calc(100vh - 169px)) ----
                if is_theater {
                    let max_theater_h = (viewport_h - 113.0).max(360.0);
                    let theater_h = (full_w * 9.0 / 16.0).min(max_theater_h).max(360.0);
                    let (stage_rect, _) =
                        ui.allocate_exact_size(Vec2::new(full_w, theater_h), Sense::hover());
                    self.render_video_stage_and_glass_hud(ui, ctx, stage_rect);
                    ui.add_space(16.0);
                } else {
                    ui.add_space(14.0);
                }

                // ---- CONTAINER 2 COLUNAS OFICIAL DO YOUTUBE (MaxWidth 1754px, margens 24px, gap 24px, sidebar 402px) ----
                let outer_margin = 24.0_f32;
                let max_container_w = 1754.0_f32;
                let content_w = (full_w - outer_margin * 2.0)
                    .min(max_container_w)
                    .max(420.0);
                let left_margin = ((full_w - content_w) * 0.5).max(outer_margin);
                let sidebar_w = 402.0_f32;
                let col_gap = 24.0_f32;
                let two_column = content_w >= 920.0;
                let left_w = if two_column {
                    (content_w - sidebar_w - col_gap).max(460.0)
                } else {
                    content_w
                };

                ui.horizontal_top(|ui| {
                    ui.spacing_mut().item_spacing.x = 0.0;
                    ui.add_space(left_margin);

                    // ---- COLUNA ESQUERDA: Player (no Modo Padrão) + Título + Canal + Descrição (#272727) ----
                    ui.vertical(|ui| {
                        ui.set_width(left_w);

                        if !is_theater {
                            let max_norm_h = (viewport_h - 120.0).max(340.0);
                            let stage_h = (left_w * 9.0 / 16.0).min(max_norm_h).max(300.0);
                            let (stage_rect, _) =
                                ui.allocate_exact_size(Vec2::new(left_w, stage_h), Sense::hover());
                            self.render_video_stage_and_glass_hud(ui, ctx, stage_rect);
                            ui.add_space(14.0);
                        }

                        self.render_watch_metadata_and_description(ui, ctx, left_w, &mut open_ch);

                        // Fallback responsivo para janelas estreitas (< 920px): empilha recomendados abaixo
                        if !two_column && !self.current_video_related.is_empty() {
                            ui.add_space(18.0);
                            self.render_watch_sidebar_list(
                                ui,
                                ctx,
                                left_w,
                                &mut play_related,
                                &mut open_ch,
                                &mut queue_rel,
                            );
                        }
                    });

                    // ---- COLUNA DIREITA (402px): Vídeos Recomendados Compactos (168x94, CornerRadius 8) ----
                    if two_column {
                        ui.add_space(col_gap);
                        ui.vertical(|ui| {
                            ui.set_width(sidebar_w);
                            self.render_watch_sidebar_list(
                                ui,
                                ctx,
                                sidebar_w,
                                &mut play_related,
                                &mut open_ch,
                                &mut queue_rel,
                            );
                        });
                    }
                });

                ui.add_space(28.0);
            });

        if let Some(rel) = play_related {
            self.start_video_playback(rel, None);
        }
        if let Some((cid, cname)) = open_ch {
            self.open_channel(&cid, &cname, "home");
        }
        if let Some(q) = queue_rel {
            self.video_up_next_queue
                .retain(|x| !x.id.eq_ignore_ascii_case(&q.id));
            self.video_up_next_queue.insert(0, q.clone());
            self.show_toast(format!("Adicionado a seguir: {}", q.title));
        }
    }

    fn render_watch_metadata_and_description(
        &mut self,
        ui: &mut egui::Ui,
        ctx: &egui::Context,
        col_w: f32,
        open_ch: &mut Option<(String, String)>,
    ) {
        let Some(cur) = self.current_video.clone() else {
            return;
        };

        let av_url = if !cur.channel_avatar_url.is_empty() {
            cur.channel_avatar_url.clone()
        } else {
            crate::youtube::get_cached_channel_avatar_url(&cur.channel_id, &cur.artist)
        };
        let av_tex = self.thumb_cache.get_or_request_circle(&av_url, &self.tx, ctx);

        // Linha 1: Título (19.5px Bold)
        let title = if !self.current_video_meta.title.is_empty() {
            &self.current_video_meta.title
        } else {
            &cur.title
        };
        ui.add(
            egui::Label::new(
                RichText::new(title)
                    .size(19.5)
                    .strong()
                    .color(TEXT_PRIMARY),
            )
            .wrap(),
        );

        ui.add_space(10.0);

        // Linha 2: Avatar 38x38 + Nome do Canal + Inscritos/Qualidade/Codec + Pill Branco "Acessar Canal"
        ui.horizontal(|ui| {
            let author = if !self.current_video_meta.author.is_empty() {
                self.current_video_meta.author.clone()
            } else {
                cur.artist.clone()
            };

            let (av_r, av_resp) = ui.allocate_exact_size(Vec2::new(38.0, 38.0), Sense::click());
            if av_resp.hovered() {
                ui.ctx().set_cursor_icon(CursorIcon::PointingHand);
            }
            draw_circular_avatar(ui.painter(), av_r, &author, av_tex.as_ref());
            if av_resp.clicked() {
                *open_ch = Some((cur.channel_id.clone(), author.clone()));
            }

            ui.add_space(10.0);

            ui.vertical(|ui| {
                let ch_lbl = ui.add(
                    egui::Label::new(
                        RichText::new(&author)
                            .size(15.0)
                            .strong()
                            .color(TEXT_PRIMARY),
                    )
                    .sense(Sense::click()),
                );
                if ch_lbl.hovered() {
                    ui.ctx().set_cursor_icon(CursorIcon::PointingHand);
                }
                if ch_lbl.clicked() {
                    *open_ch = Some((cur.channel_id.clone(), author.clone()));
                }

                let sub_info = if !self.current_video_sub_count.is_empty() {
                    format!(
                        "{}  •  {}p  •  {}",
                        self.current_video_sub_count,
                        self.video_actual_height,
                        self.video_codec_label
                    )
                } else {
                    format!(
                        "{}p  •  {}",
                        self.video_actual_height, self.video_codec_label
                    )
                };
                ui.label(RichText::new(sub_info).size(12.0).color(TEXT_SECONDARY));
            });

            ui.add_space(16.0);

            if white_pill_button(ui, "Acessar Canal") {
                *open_ch = Some((cur.channel_id.clone(), author));
            }
        });

        ui.add_space(12.0);

        // Linha 3: Caixa de Descrição #272727 sempre visível (compacta com "...mais" / expandida com "Mostrar menos")
        let views = self.current_video_meta.formatted_views();
        let date = self.current_video_meta.formatted_upload_date();
        let meta_line = match (!views.is_empty(), !date.is_empty()) {
            (true, true) => format!("{}  •  {}", views, date),
            (true, false) => views,
            (false, true) => date,
            (false, false) => cur.view_count_text.clone(),
        };

        let desc_box_bg = Color32::from_rgb(0x27, 0x27, 0x27);
        egui::Frame::NONE
            .fill(desc_box_bg)
            .corner_radius(CornerRadius::same(12))
            .inner_margin(Margin::symmetric(14, 12))
            .show(ui, |ui| {
                ui.set_width((col_w - 28.0).max(200.0));

                if !meta_line.is_empty() {
                    ui.label(
                        RichText::new(&meta_line)
                            .size(13.0)
                            .strong()
                            .color(TEXT_PRIMARY),
                    );
                    ui.add_space(4.0);
                }

                let full_desc = if self.current_video_meta.description.is_empty() {
                    "Sem descrição disponível."
                } else {
                    self.current_video_meta.description.as_str()
                };

                if self.video_description_expanded {
                    if !self.current_video_chapters.is_empty() {
                        ui.add_space(4.0);
                        ui.horizontal_wrapped(|ui| {
                            for ch in &self.current_video_chapters {
                                let lbl = format!(
                                    "{}  {}",
                                    format_seconds(ch.start_seconds as f64),
                                    ch.title
                                );
                                let btn = ui.add(
                                    egui::Button::new(
                                        RichText::new(lbl).size(11.5).color(YT_BLUE),
                                    )
                                    .fill(Color32::from_rgb(24, 34, 46))
                                    .corner_radius(CornerRadius::same(8)),
                                );
                                if btn.hovered() {
                                    ui.ctx().set_cursor_icon(CursorIcon::PointingHand);
                                }
                                if btn.clicked() {
                                    self.video_player.seek_seconds(ch.start_seconds as f64);
                                }
                            }
                        });
                        ui.add_space(8.0);
                    }

                    ui.add(
                        egui::Label::new(
                            RichText::new(full_desc)
                                .size(13.0)
                                .color(Color32::from_rgb(0xE4, 0xE4, 0xE7)),
                        )
                        .wrap(),
                    );
                    ui.add_space(8.0);
                    let less = ui.add(
                        egui::Label::new(
                            RichText::new("Mostrar menos")
                                .size(13.0)
                                .strong()
                                .color(TEXT_PRIMARY),
                        )
                        .sense(Sense::click()),
                    );
                    if less.hovered() {
                        ui.ctx().set_cursor_icon(CursorIcon::PointingHand);
                    }
                    if less.clicked() {
                        self.video_description_expanded = false;
                    }
                } else {
                    // Prévia compacta de até 2 linhas (estilo oficial do YouTube)
                    let mut preview_lines: Vec<&str> = full_desc
                        .lines()
                        .map(|l| l.trim())
                        .filter(|l| !l.is_empty())
                        .take(2)
                        .collect();
                    if preview_lines.is_empty() {
                        preview_lines.push("Sem descrição disponível.");
                    }
                    let preview_raw = preview_lines.join("\n");
                    let preview = truncate_chars(&preview_raw, 175);

                    let desc_lbl = ui.add(
                        egui::Label::new(
                            RichText::new(preview)
                                .size(13.0)
                                .color(Color32::from_rgb(0xE4, 0xE4, 0xE7)),
                        )
                        .wrap()
                        .sense(Sense::click()),
                    );
                    if desc_lbl.hovered() {
                        ui.ctx().set_cursor_icon(CursorIcon::PointingHand);
                    }
                    ui.add_space(4.0);
                    let more = ui.add(
                        egui::Label::new(
                            RichText::new("...mais")
                                .size(13.0)
                                .strong()
                                .color(TEXT_PRIMARY),
                        )
                        .sense(Sense::click()),
                    );
                    if more.hovered() {
                        ui.ctx().set_cursor_icon(CursorIcon::PointingHand);
                    }
                    if desc_lbl.clicked() || more.clicked() {
                        self.video_description_expanded = true;
                    }
                }
            });
    }

    fn render_watch_sidebar_list(
        &mut self,
        ui: &mut egui::Ui,
        ctx: &egui::Context,
        sidebar_w: f32,
        play_related: &mut Option<TrackItem>,
        open_ch: &mut Option<(String, String)>,
        queue_rel: &mut Option<TrackItem>,
    ) {
        if self.current_video_related.is_empty() {
            ui.label(
                RichText::new("Carregando vídeos recomendados...")
                    .size(12.5)
                    .color(TEXT_SECONDARY),
            );
            return;
        }

        let rel_clone = self.current_video_related.clone();
        for item in &rel_clone {
            let is_saved = self.config.is_saved_video(&item.id);
            let thumb_tex =
                self.thumb_cache
                    .get_or_request_rect(&item.thumbnail_url(), &self.tx, ctx);

            let (c_card, c_ch, c_save, c_queue) = render_youtube_sidebar_card(
                ui,
                item,
                sidebar_w,
                thumb_tex,
                is_saved,
            );
            if c_card {
                *play_related = Some(item.clone());
            }
            if c_ch {
                *open_ch = Some((item.channel_id.clone(), item.artist.clone()));
            }
            if c_save {
                self.config.toggle_saved_video(item);
            }
            if c_queue {
                *queue_rel = Some(item.clone());
            }
            ui.add_space(8.0);
        }
    }

    // ========================================================================
    // Player Stage + YouTube 2026 Glass Pills Overlay (WpfVideoPlayerOverlay.xaml)
    // ========================================================================

    fn render_video_stage_and_glass_hud(
        &mut self,
        ui: &mut egui::Ui,
        ctx: &egui::Context,
        stage_rect: Rect,
    ) {
        let corner = if self.video_is_fullscreen || self.config.is_theater_mode {
            CornerRadius::ZERO
        } else {
            CornerRadius::same(12)
        };
        let painter = ui.painter_at(stage_rect);
        painter.rect_filled(stage_rect, corner, Color32::BLACK);

        // Quadro de vídeo decodificado (aspect ratio 16:9 centralizado)
        if let Some(ref tex) = self.video_texture {
            let tex_size = tex.size_vec2();
            if tex_size.x > 0.0 && tex_size.y > 0.0 {
                let scale = (stage_rect.width() / tex_size.x).min(stage_rect.height() / tex_size.y);
                let draw_rect = Rect::from_center_size(
                    stage_rect.center(),
                    Vec2::new(tex_size.x * scale, tex_size.y * scale),
                );
                painter.image(
                    tex.id(),
                    draw_rect,
                    Rect::from_min_max(Pos2::ZERO, Pos2::new(1.0, 1.0)),
                    Color32::WHITE,
                );
            }
        } else if let Some(ref cur) = self.current_video {
            if let Some(thumb) =
                self.thumb_cache
                    .get_or_request_rect(&cur.thumbnail_url(), &self.tx, ctx)
            {
                painter.image(
                    thumb.id(),
                    stage_rect,
                    Rect::from_min_max(Pos2::ZERO, Pos2::new(1.0, 1.0)),
                    Color32::from_gray(95),
                );
            }
        }

        // Loading overlay limpo
        if self.video_is_resolving {
            painter.rect_filled(stage_rect, corner, Color32::from_rgba_unmultiplied(12, 12, 14, 215));
            let title = self
                .current_video
                .as_ref()
                .map(|v| v.title.as_str())
                .unwrap_or("Carregando...");
            painter.text(
                Pos2::new(stage_rect.center().x, stage_rect.center().y - 12.0),
                Align2::CENTER_CENTER,
                truncate_chars(title, 65),
                FontId::new(15.0, FontFamily::Proportional),
                TEXT_PRIMARY,
            );
            painter.text(
                Pos2::new(stage_rect.center().x, stage_rect.center().y + 14.0),
                Align2::CENTER_CENTER,
                "Conectando ao YouTube...",
                FontId::new(12.5, FontFamily::Proportional),
                TEXT_SECONDARY,
            );
        }

        // Detecta atividade do mouse para Auto-Hide de 2,5s (exatamente como WpfVideoPlayerOverlay!)
        if let Some(p) = ctx.input(|i| i.pointer.hover_pos()) {
            if stage_rect.contains(p) {
                let moved = self
                    .last_player_mouse_pos
                    .map(|prev| prev.distance(p) > 2.0)
                    .unwrap_or(true);
                if moved {
                    self.last_player_mouse_pos = Some(p);
                    self.last_player_activity = Instant::now();
                }
            }
        }

        let is_playing = self.video_player.is_playing();
        let hud_visible = !is_playing
            || self.is_dragging_video_scrubber
            || self.video_show_gear_popup
            || self.last_player_activity.elapsed() < Duration::from_millis(2500);

        // Toast Banner (Topo esquerdo, borda #3EA6FF, igual ToastNotificationBanner)
        if let Some((ref msg, ts)) = self.toast_message {
            if ts.elapsed() < Duration::from_millis(2500) {
                let tw = (msg.chars().count() as f32 * 7.2 + 42.0).clamp(160.0, 380.0);
                let tr = Rect::from_min_size(
                    Pos2::new(stage_rect.left() + 24.0, stage_rect.top() + 24.0),
                    Vec2::new(tw, 34.0),
                );
                painter.rect_filled(tr, CornerRadius::same(8), Color32::from_rgba_unmultiplied(24, 24, 27, 230));
                painter.rect_stroke(tr, CornerRadius::same(8), Stroke::new(1.0_f32, YT_BLUE), StrokeKind::Inside);
                painter.circle_filled(Pos2::new(tr.left() + 16.0, tr.center().y), 4.0, YT_BLUE);
                painter.text(
                    Pos2::new(tr.left() + 28.0, tr.center().y),
                    Align2::LEFT_CENTER,
                    msg,
                    FontId::new(12.5, FontFamily::Proportional),
                    TEXT_PRIMARY,
                );
            }
        }

        // SponsorBlock Skip Banner (Canto inferior direito, bolinha verde #00D600)
        if let Some((ref msg, ts)) = self.sponsor_skip_banner {
            if ts.elapsed() < Duration::from_millis(3000) {
                let bw = (msg.chars().count() as f32 * 7.2 + 42.0).clamp(150.0, 300.0);
                let br = Rect::from_min_size(
                    Pos2::new(stage_rect.right() - bw - 22.0, stage_rect.bottom() - 116.0),
                    Vec2::new(bw, 34.0),
                );
                painter.rect_filled(br, CornerRadius::same(8), Color32::from_rgba_unmultiplied(24, 24, 27, 230));
                painter.rect_stroke(br, CornerRadius::same(8), Stroke::new(1.0_f32, BORDER_SUBTLE), StrokeKind::Inside);
                painter.circle_filled(Pos2::new(br.left() + 16.0, br.center().y), 4.0, Color32::from_rgb(0x00, 0xD6, 0x00));
                painter.text(
                    Pos2::new(br.left() + 28.0, br.center().y),
                    Align2::LEFT_CENTER,
                    msg,
                    FontId::new(12.5, FontFamily::Proportional),
                    TEXT_PRIMARY,
                );
            }
        }

        // Clique na área central do vídeo (Play/Pause ou Duplo Clique Fullscreen)
        let hud_height = 84.0;
        let center_hit = Rect::from_min_max(
            stage_rect.min,
            Pos2::new(stage_rect.max.x, (stage_rect.max.y - hud_height).max(stage_rect.min.y)),
        );
        let stage_resp = ui.interact(center_hit, ui.id().with("vid_center"), Sense::click());
        if stage_resp.double_clicked() {
            self.video_is_fullscreen = !self.video_is_fullscreen;
            ctx.send_viewport_cmd(egui::ViewportCommand::Fullscreen(self.video_is_fullscreen));
        } else if stage_resp.clicked() {
            self.video_show_gear_popup = false;
            self.toggle_video_play_pause();
        }

        // Scroll do mouse em Fullscreen ajusta o volume do vídeo (±5%, igual WpfVideoPlayerOverlay)
        if self.video_is_fullscreen && !self.video_show_gear_popup {
            if let Some(p) = ctx.input(|i| i.pointer.hover_pos()) {
                if stage_rect.contains(p) {
                    let scroll_y = ctx.input(|i| i.raw_scroll_delta.y);
                    if scroll_y.abs() > 0.5 {
                        self.adjust_video_volume(if scroll_y > 0.0 { 0.05 } else { -0.05 });
                    }
                }
            }
        }

        // Center OSD Badge (Volume / Mudo / Seek ±5s/±10s, igual CenterOsdBadge do XAML)
        if let Some((ref osd_txt, ts)) = self.center_osd {
            if ts.elapsed() < Duration::from_millis(650) {
                let ow = (osd_txt.chars().count() as f32 * 9.5 + 36.0).clamp(84.0, 220.0);
                let or = Rect::from_center_size(stage_rect.center(), Vec2::new(ow, 44.0));
                painter.rect_filled(
                    or,
                    CornerRadius::same(8),
                    Color32::from_rgba_unmultiplied(24, 24, 27, 215),
                );
                painter.rect_stroke(
                    or,
                    CornerRadius::same(8),
                    Stroke::new(1.0_f32, Color32::from_rgb(0x27, 0x27, 0x2A)),
                    StrokeKind::Inside,
                );
                painter.text(
                    or.center(),
                    Align2::CENTER_CENTER,
                    osd_txt,
                    FontId::new(16.0, FontFamily::Proportional),
                    TEXT_PRIMARY,
                );
            }
        }

        if !hud_visible {
            return;
        }

        // Degradê suave inferior (simula o LinearGradientBrush transparente -> #C4060608)
        let grad_top = stage_rect.bottom() - 96.0;
        for step in 0..8 {
            let t0 = step as f32 / 8.0;
            let t1 = (step + 1) as f32 / 8.0;
            let y0 = grad_top + t0 * 96.0;
            let y1 = grad_top + t1 * 96.0;
            let alpha = (t1 * t1 * 195.0) as u8;
            painter.rect_filled(
                Rect::from_min_max(Pos2::new(stage_rect.left(), y0), Pos2::new(stage_rect.right(), y1)),
                CornerRadius::ZERO,
                Color32::from_rgba_unmultiplied(6, 6, 8, alpha),
            );
        }

        let total_dur = self
            .video_player
            .get_duration_seconds()
            .max(
                self.current_video
                    .as_ref()
                    .map(|v| v.duration_seconds as f64)
                    .unwrap_or(0.0),
            )
            .max(1.0);
        let cur_pos = if self.is_dragging_video_scrubber {
            self.drag_video_scrub_sec
        } else {
            self.video_player.get_position_seconds()
        };

        // 1. Scrubber Fino Estilo YouTube (3px -> 5px no hover, cabeça branca com borda #FF0033)
        let scrub_hit = Rect::from_min_max(
            Pos2::new(stage_rect.left() + 16.0, stage_rect.bottom() - 68.0),
            Pos2::new(stage_rect.right() - 16.0, stage_rect.bottom() - 50.0),
        );
        let scrub_resp = ui.interact(scrub_hit, ui.id().with("vid_scrub"), Sense::click_and_drag());
        if scrub_resp.hovered() {
            ui.ctx().set_cursor_icon(CursorIcon::PointingHand);
        }

        let bar_h = if scrub_resp.hovered() || self.is_dragging_video_scrubber {
            5.0
        } else {
            3.0
        };
        let track_rect = Rect::from_center_size(scrub_hit.center(), Vec2::new(scrub_hit.width(), bar_h));
        painter.rect_filled(track_rect, CornerRadius::same(2), Color32::from_white_alpha(55));

        // Segmentos SponsorBlock
        for seg in &self.current_video_sponsors {
            let s = (seg.start / total_dur).clamp(0.0, 1.0) as f32;
            let e = (seg.end / total_dur).clamp(0.0, 1.0) as f32;
            if e > s {
                let sr = Rect::from_min_max(
                    Pos2::new(track_rect.left() + track_rect.width() * s, track_rect.top()),
                    Pos2::new(track_rect.left() + track_rect.width() * e, track_rect.bottom()),
                );
                let [r, g, b, a] = seg.color_rgba();
                painter.rect_filled(sr, CornerRadius::same(1), Color32::from_rgba_unmultiplied(r, g, b, a));
            }
        }

        // Progresso #FF0033
        let played_ratio = (cur_pos / total_dur).clamp(0.0, 1.0) as f32;
        let played_rect = Rect::from_min_max(
            track_rect.min,
            Pos2::new(track_rect.left() + track_rect.width() * played_ratio, track_rect.bottom()),
        );
        painter.rect_filled(played_rect, CornerRadius::same(2), YT_RED);

        // Divisórias sutis de capítulos
        for ch in &self.current_video_chapters {
            let r = (ch.start_seconds as f64 / total_dur).clamp(0.0, 1.0) as f32;
            if r > 0.01 && r < 0.99 {
                let x = track_rect.left() + track_rect.width() * r;
                painter.line_segment(
                    [Pos2::new(x, track_rect.top()), Pos2::new(x, track_rect.bottom())],
                    Stroke::new(1.5_f32, BG_MAIN),
                );
            }
        }

        if scrub_resp.hovered() || self.is_dragging_video_scrubber {
            let thumb_c = Pos2::new(
                track_rect.left() + track_rect.width() * played_ratio,
                track_rect.center().y,
            );
            painter.circle_filled(thumb_c, 6.5, Color32::WHITE);
            painter.circle_stroke(thumb_c, 6.5, Stroke::new(2.2_f32, YT_RED));
        }

        if let Some(ppos) = scrub_resp.hover_pos().or_else(|| ctx.pointer_interact_pos()) {
            if scrub_resp.hovered() || scrub_resp.dragged() || scrub_resp.clicked() {
                self.last_player_activity = Instant::now();
                let rel = ((ppos.x - track_rect.left()) / track_rect.width()).clamp(0.0, 1.0);
                let target_sec = (rel as f64) * total_dur;

                if scrub_resp.dragged() {
                    self.is_dragging_video_scrubber = true;
                    self.drag_video_scrub_sec = target_sec;
                }
                if scrub_resp.drag_stopped() || scrub_resp.clicked() {
                    self.is_dragging_video_scrubber = false;
                    self.video_player.seek_seconds(target_sec);
                }

                self.render_storyboard_hover_tooltip(
                    &painter,
                    ctx,
                    Pos2::new(ppos.x, track_rect.top()),
                    stage_rect,
                    target_sec,
                );
            }
        }

        // 2. Cápsula Esquerda Translúcida (#BF121212, CornerRadius 22): Play/Pause, Next, Som+Slider, Divisor, Tempo
        let pill_y = stage_rect.bottom() - 28.0;
        let time_text = format!("{} / {}", format_seconds(cur_pos), format_seconds(total_dur));
        let left_pill_w = 290.0;
        let left_pill = Rect::from_min_size(
            Pos2::new(stage_rect.left() + 16.0, pill_y - 20.0),
            Vec2::new(left_pill_w, 40.0),
        );
        painter.rect_filled(left_pill, CornerRadius::same(20), PILL_GLASS);

        // Botão Play/Pause
        let pp_r = Rect::from_center_size(Pos2::new(left_pill.left() + 24.0, pill_y), Vec2::new(30.0, 30.0));
        let pp_resp = ui.interact(pp_r, ui.id().with("hud_pp"), Sense::click());
        if pp_resp.hovered() {
            ui.ctx().set_cursor_icon(CursorIcon::PointingHand);
        }
        if is_playing {
            icons::draw_pause_bars(&painter, pp_r.center(), 14.0, TEXT_PRIMARY);
        } else {
            icons::draw_play_triangle(&painter, pp_r.center(), 14.0, TEXT_PRIMARY);
        }
        if pp_resp.clicked() {
            self.toggle_video_play_pause();
        }

        // Botão Próximo
        let nxt_r = Rect::from_center_size(Pos2::new(left_pill.left() + 56.0, pill_y), Vec2::new(28.0, 28.0));
        let nxt_resp = ui.interact(nxt_r, ui.id().with("hud_nxt"), Sense::click());
        if nxt_resp.hovered() {
            ui.ctx().set_cursor_icon(CursorIcon::PointingHand);
        }
        icons::draw_next_track(&painter, nxt_r.center(), 13.0, TEXT_PRIMARY);
        if nxt_resp.clicked() {
            if let Some(next) = self.get_next_video_candidate(true) {
                self.start_video_playback(next, Some(0.0));
            }
        }

        // Botão Alto-falante + Slider fino branco 64px
        let spk_r = Rect::from_center_size(Pos2::new(left_pill.left() + 88.0, pill_y), Vec2::new(28.0, 28.0));
        let spk_resp = ui.interact(spk_r, ui.id().with("hud_spk"), Sense::click());
        if spk_resp.hovered() {
            ui.ctx().set_cursor_icon(CursorIcon::PointingHand);
        }
        icons::draw_speaker(
            &painter,
            spk_r.center(),
            15.0,
            TEXT_PRIMARY,
            self.config.is_video_muted || self.config.video_volume <= 0.01,
        );
        if spk_resp.clicked() {
            self.config.is_video_muted = !self.config.is_video_muted;
            self.config.save();
            self.video_player
                .set_volume(self.config.video_volume, self.config.is_video_muted);
        }

        let vol_rect = Rect::from_min_size(Pos2::new(left_pill.left() + 108.0, pill_y - 10.0), Vec2::new(64.0, 20.0));
        if let Some(new_vol) = draw_thin_white_slider(
            ui,
            &painter,
            vol_rect,
            "vid_vol_slider",
            if self.config.is_video_muted { 0.0 } else { self.config.video_volume as f32 },
        ) {
            self.config.video_volume = (new_vol as f64 * 100.0).round() / 100.0;
            if self.config.video_volume > 0.001 {
                self.config.is_video_muted = false;
            }
            self.config.save();
            self.video_player
                .set_volume(self.config.video_volume, self.config.is_video_muted);
        }

        // Scroll do mouse sobre o grupo de volume ajusta ±5% (quando não está em fullscreen, que já captura o palco inteiro)
        let vol_group_rect = spk_r.union(vol_rect).expand(6.0);
        if let Some(p) = ctx.input(|i| i.pointer.hover_pos()) {
            if vol_group_rect.contains(p) {
                if !self.video_is_fullscreen {
                    let scroll_y = ctx.input(|i| i.raw_scroll_delta.y);
                    if scroll_y.abs() > 0.5 {
                        self.adjust_video_volume(if scroll_y > 0.0 { 0.05 } else { -0.05 });
                    }
                }
                let pct_txt = if self.config.is_video_muted {
                    "Mudo".to_string()
                } else {
                    format!("{}%", (self.config.video_volume * 100.0).round() as i32)
                };
                let tip_r = Rect::from_center_size(
                    Pos2::new(vol_rect.center().x, left_pill.top() - 14.0),
                    Vec2::new(42.0, 20.0),
                );
                painter.rect_filled(tip_r, CornerRadius::same(4), BG_SURFACE);
                painter.rect_stroke(tip_r, CornerRadius::same(4), Stroke::new(1.0_f32, BORDER_SUBTLE), StrokeKind::Inside);
                painter.text(
                    tip_r.center(),
                    Align2::CENTER_CENTER,
                    pct_txt,
                    FontId::new(10.5, FontFamily::Proportional),
                    TEXT_PRIMARY,
                );
            }
        }

        // Divisor vertical + Tempo
        let div_x = vol_rect.right() + 12.0;
        painter.line_segment(
            [Pos2::new(div_x, pill_y - 7.0), Pos2::new(div_x, pill_y + 7.0)],
            Stroke::new(1.0_f32, Color32::from_white_alpha(45)),
        );
        painter.text(
            Pos2::new(div_x + 10.0, pill_y),
            Align2::LEFT_CENTER,
            time_text,
            FontId::new(12.0, FontFamily::Proportional),
            TEXT_PRIMARY,
        );

        // 3. Cápsula Direita Simétrica (#BF121212, CornerRadius 22): Quality Pill, Speed Pill, Gear, [Modo Teatro], Fullscreen
        let show_theater_btn = !self.video_is_fullscreen;
        let right_pill_w = if show_theater_btn { 252.0 } else { 216.0 };
        let right_pill = Rect::from_min_size(
            Pos2::new(stage_rect.right() - right_pill_w - 16.0, pill_y - 20.0),
            Vec2::new(right_pill_w, 40.0),
        );
        painter.rect_filled(right_pill, CornerRadius::same(20), PILL_GLASS);

        // Pill Qualidade (ex: "1080p HD")
        let q_txt = if self.config.video_quality_height >= 2160 {
            "4K".to_string()
        } else if self.config.video_quality_height >= 720 {
            format!("{}p HD", self.config.video_quality_height)
        } else {
            format!("{}p", self.config.video_quality_height)
        };
        let qp_r = Rect::from_center_size(Pos2::new(right_pill.left() + 44.0, pill_y), Vec2::new(66.0, 24.0));
        let qp_resp = ui.interact(qp_r, ui.id().with("hud_q"), Sense::click());
        if qp_resp.hovered() {
            ui.ctx().set_cursor_icon(CursorIcon::PointingHand);
        }
        painter.rect_filled(qp_r, CornerRadius::same(11), CHIP_TRANSLUCENT);
        painter.text(qp_r.center(), Align2::CENTER_CENTER, q_txt, FontId::new(11.0, FontFamily::Proportional), TEXT_PRIMARY);
        if qp_resp.clicked() {
            self.video_show_gear_popup = !self.video_show_gear_popup;
        }

        // Pill Velocidade ("1x")
        let spd_txt = format!("{:.2}x", self.video_speed).replace(".00x", "x");
        let sp_r = Rect::from_center_size(Pos2::new(right_pill.left() + 102.0, pill_y), Vec2::new(40.0, 24.0));
        let sp_resp = ui.interact(sp_r, ui.id().with("hud_spd"), Sense::click());
        if sp_resp.hovered() {
            ui.ctx().set_cursor_icon(CursorIcon::PointingHand);
        }
        painter.rect_filled(sp_r, CornerRadius::same(11), CHIP_TRANSLUCENT);
        painter.text(sp_r.center(), Align2::CENTER_CENTER, spd_txt, FontId::new(11.0, FontFamily::Proportional), TEXT_PRIMARY);
        if sp_resp.clicked() {
            let speeds = [0.5, 0.75, 1.0, 1.25, 1.5, 2.0];
            let pos = speeds
                .iter()
                .position(|&s| (s - self.video_speed).abs() < 0.05)
                .unwrap_or(2);
            self.video_speed = speeds[(pos + 1) % speeds.len()];
            self.video_player.set_speed(self.video_speed);
        }

        // Engrenagem (Configurações)
        let gr_r = Rect::from_center_size(Pos2::new(right_pill.left() + 148.0, pill_y), Vec2::new(28.0, 28.0));
        let gr_resp = ui.interact(gr_r, ui.id().with("hud_gear"), Sense::click());
        if gr_resp.hovered() {
            ui.ctx().set_cursor_icon(CursorIcon::PointingHand);
        }
        icons::draw_gear(&painter, gr_r.center(), 15.0, TEXT_PRIMARY);
        if gr_resp.clicked() {
            self.video_show_gear_popup = !self.video_show_gear_popup;
        }

        // Modo Teatro (T) — oculto automaticamente em Tela Cheia
        if show_theater_btn {
            let th_r = Rect::from_center_size(Pos2::new(right_pill.left() + 186.0, pill_y), Vec2::new(28.0, 28.0));
            let th_resp = ui
                .interact(th_r, ui.id().with("hud_theater"), Sense::click())
                .on_hover_text(if self.config.is_theater_mode {
                    "Visualização padrão (T)"
                } else {
                    "Modo Teatro (T)"
                });
            if th_resp.hovered() {
                ui.ctx().set_cursor_icon(CursorIcon::PointingHand);
            }
            icons::draw_theater_mode(
                &painter,
                th_r.center(),
                16.0,
                if self.config.is_theater_mode { YT_BLUE } else { TEXT_PRIMARY },
                self.config.is_theater_mode,
            );
            if th_resp.clicked() {
                self.config.is_theater_mode = !self.config.is_theater_mode;
                self.config_dirty_since = Some(Instant::now());
                self.show_center_osd(if self.config.is_theater_mode {
                    "Modo Teatro"
                } else {
                    "Modo Padrão"
                });
            }
        }

        // Tela Cheia
        let fs_x = if show_theater_btn {
            right_pill.left() + 224.0
        } else {
            right_pill.left() + 186.0
        };
        let fs_r = Rect::from_center_size(Pos2::new(fs_x, pill_y), Vec2::new(28.0, 28.0));
        let fs_resp = ui
            .interact(fs_r, ui.id().with("hud_fs"), Sense::click())
            .on_hover_text(if self.video_is_fullscreen {
                "Sair da tela cheia (F)"
            } else {
                "Tela cheia (F)"
            });
        if fs_resp.hovered() {
            ui.ctx().set_cursor_icon(CursorIcon::PointingHand);
        }
        icons::draw_fullscreen(&painter, fs_r.center(), 15.0, TEXT_PRIMARY);
        if fs_resp.clicked() {
            self.video_is_fullscreen = !self.video_is_fullscreen;
            ctx.send_viewport_cmd(egui::ViewportCommand::Fullscreen(self.video_is_fullscreen));
        }

        // Painel Flutuante da Engrenagem (Qualidade + Velocidade)
        if self.video_show_gear_popup {
            let pop_w = 276.0;
            let pop_h = 192.0;
            let pop_rect = Rect::from_min_size(
                Pos2::new(stage_rect.right() - pop_w - 18.0, stage_rect.bottom() - pop_h - 68.0),
                Vec2::new(pop_w, pop_h),
            );
            painter.rect_filled(pop_rect, CornerRadius::same(8), BG_SURFACE);
            painter.rect_stroke(pop_rect, CornerRadius::same(8), Stroke::new(1.0_f32, BORDER_SUBTLE), StrokeKind::Inside);

            let mut pop_ui = ui.new_child(
                egui::UiBuilder::new()
                    .max_rect(pop_rect.shrink(12.0))
                    .layout(Layout::top_down(Align::Min)),
            );
            pop_ui.label(RichText::new("QUALIDADE DO VÍDEO").size(10.5).strong().color(TEXT_SECONDARY));
            pop_ui.add_space(6.0);

            let mut chosen_q = None;
            let qualities = [2160, 1440, 1080, 720, 480, 360, 240, 144];
            for row in qualities.chunks(4) {
                pop_ui.horizontal(|ui| {
                    for &q in row {
                        let active = self.config.video_quality_height == q;
                        let lbl = if q == 2160 { "4K".to_string() } else { format!("{}p", q) };
                        if ui
                            .add(
                                egui::Button::new(RichText::new(lbl).size(11.0).color(if active { BG_MAIN } else { TEXT_PRIMARY }))
                                    .fill(if active { YT_BLUE } else { BG_SEARCH_BTN })
                                    .corner_radius(CornerRadius::same(5))
                                    .min_size(Vec2::new(56.0, 26.0)),
                            )
                            .clicked()
                        {
                            chosen_q = Some(q);
                        }
                    }
                });
                pop_ui.add_space(4.0);
            }

            pop_ui.add_space(6.0);
            pop_ui.label(RichText::new("VELOCIDADE DE REPRODUÇÃO").size(10.5).strong().color(TEXT_SECONDARY));
            pop_ui.add_space(6.0);

            let speeds = [0.5, 0.75, 1.0, 1.25, 1.5, 2.0];
            for row in speeds.chunks(3) {
                pop_ui.horizontal(|ui| {
                    for &s in row {
                        let active = (self.video_speed - s).abs() < 0.05;
                        let lbl = if (s - 1.0).abs() < 0.01 { "1.0x".to_string() } else { format!("{}x", s) };
                        if ui
                            .add(
                                egui::Button::new(RichText::new(lbl).size(11.0).color(if active { BG_MAIN } else { TEXT_PRIMARY }))
                                    .fill(if active { YT_BLUE } else { BG_SEARCH_BTN })
                                    .corner_radius(CornerRadius::same(5))
                                    .min_size(Vec2::new(78.0, 26.0)),
                            )
                            .clicked()
                        {
                            self.video_speed = s;
                            self.video_player.set_speed(s);
                            self.video_show_gear_popup = false;
                        }
                    }
                });
                pop_ui.add_space(4.0);
            }

            if let Some(q) = chosen_q {
                self.switch_video_quality(q);
            }
        }
    }

    fn render_storyboard_hover_tooltip(
        &mut self,
        painter: &egui::Painter,
        _ctx: &egui::Context,
        anchor: Pos2,
        stage_rect: Rect,
        hover_sec: f64,
    ) {
        if let Some(ref sb) = self.current_video_storyboard {
            if (hover_sec - self.last_storyboard_req_sec).abs() > 1.5 {
                self.last_storyboard_req_sec = hover_sec;
                let sb_clone = sb.clone();
                let cache_clone = self.storyboard_sheet_cache.clone();
                let tx = self.tx.clone();
                thread::spawn(move || {
                    if let Some((w, h, rgba)) =
                        fetch_cropped_storyboard_frame(&sb_clone, hover_sec, &cache_clone)
                    {
                        let _ = tx.send(AsyncMessage::StoryboardPreviewReady {
                            hover_sec,
                            width: w,
                            height: h,
                            rgba,
                        });
                    }
                });
            }
        }

        let chapter_title = self
            .current_video_chapters
            .iter()
            .rev()
            .find(|c| hover_sec >= c.start_seconds as f64)
            .map(|c| c.title.as_str())
            .unwrap_or("");

        let has_tex = self.storyboard_preview_tex.is_some();
        let box_w = 170.0;
        let extra_ch_h = if chapter_title.is_empty() { 0.0 } else { 16.0 };
        let box_h = (if has_tex { 118.0 } else { 28.0 }) + extra_ch_h;
        let cx = anchor.x.clamp(
            stage_rect.left() + box_w * 0.5 + 8.0,
            stage_rect.right() - box_w * 0.5 - 8.0,
        );
        let box_rect = Rect::from_min_size(
            Pos2::new(cx - box_w * 0.5, anchor.y - box_h - 10.0),
            Vec2::new(box_w, box_h),
        );

        painter.rect_filled(box_rect, CornerRadius::same(6), Color32::from_rgba_unmultiplied(24, 24, 27, 240));
        painter.rect_stroke(box_rect, CornerRadius::same(6), Stroke::new(1.0_f32, BORDER_SUBTLE), StrokeKind::Inside);

        if let Some((_, ref tex)) = self.storyboard_preview_tex {
            let img_rect = Rect::from_min_size(
                Pos2::new(box_rect.left() + 5.0, box_rect.top() + 5.0),
                Vec2::new(160.0, 90.0),
            );
            painter.image(
                tex.id(),
                img_rect,
                Rect::from_min_max(Pos2::ZERO, Pos2::new(1.0, 1.0)),
                Color32::WHITE,
            );
        }

        let time_y = if chapter_title.is_empty() {
            box_rect.bottom() - 12.0
        } else {
            box_rect.bottom() - 26.0
        };
        painter.text(
            Pos2::new(box_rect.center().x, time_y),
            Align2::CENTER_CENTER,
            format_seconds(hover_sec),
            FontId::new(11.5, FontFamily::Proportional),
            TEXT_PRIMARY,
        );
        if !chapter_title.is_empty() {
            painter.text(
                Pos2::new(box_rect.center().x, box_rect.bottom() - 10.0),
                Align2::CENTER_CENTER,
                truncate_chars(chapter_title, 26),
                FontId::new(10.5, FontFamily::Proportional),
                TEXT_SECONDARY,
            );
        }
    }

    // ========================================================================
    // YouTube Music — Lista de Faixas (MusicRowTemplate) & Barra Inferior (82px)
    // ========================================================================

    fn render_music_catalog_page(&mut self, ui: &mut egui::Ui, ctx: &egui::Context) {
        ui.add_space(10.0);

        // Header igual WpfMusicView.xaml
        ui.horizontal(|ui| {
            ui.add_space(20.0);
            if self.music_nav == MusicNavSection::AccountPlaylistTracks {
                if pill_chip_button(ui, "← Voltar para Playlists", false) {
                    self.load_music_section(MusicNavSection::AccountPlaylists, false);
                }
                ui.add_space(10.0);
            }

            ui.label(
                RichText::new(&self.music_header)
                    .size(16.0)
                    .strong()
                    .color(TEXT_PRIMARY),
            );

            if self.music_nav != MusicNavSection::AccountPlaylists && !self.music_items.is_empty() {
                ui.with_layout(Layout::right_to_left(Align::Center), |ui| {
                    ui.add_space(20.0);
                    if ui
                        .add(
                            egui::Button::new(
                                RichText::new("▶  Tocar Tudo")
                                    .size(12.5)
                                    .strong()
                                    .color(Color32::WHITE),
                            )
                            .fill(YT_RED)
                            .corner_radius(CornerRadius::same(16))
                            .min_size(Vec2::new(112.0, 32.0)),
                        )
                        .clicked()
                    {
                        let tracks = self.music_items.clone();
                        self.set_music_queue_and_play(tracks, 0);
                    }
                    ui.add_space(8.0);
                    if ui
                        .add(
                            egui::Button::new(
                                RichText::new("Aleatório")
                                    .size(12.5)
                                    .strong()
                                    .color(TEXT_PRIMARY),
                            )
                            .fill(BG_HOVER)
                            .corner_radius(CornerRadius::same(16))
                            .min_size(Vec2::new(92.0, 32.0)),
                        )
                        .clicked()
                    {
                        self.config.shuffle = true;
                        self.config.save();
                        let tracks = self.music_items.clone();
                        let start = (next_pseudo_random_u64() as usize)
                            % tracks.len().max(1);
                        self.set_music_queue_and_play(tracks, start);
                    }
                });
            }
        });

        ui.add_space(8.0);

        if self.music_is_loading_feed && self.music_items.is_empty() {
            ui.centered_and_justified(|ui| {
                ui.label(
                    RichText::new("Carregando faixas do YouTube Music...")
                        .size(14.5)
                        .color(TEXT_SECONDARY),
                );
            });
            return;
        }

        let mut action_activate: Option<(usize, TrackItem)> = None;
        let mut action_fav: Option<TrackItem> = None;
        let mut action_queue: Option<TrackItem> = None;
        let mut action_watch_clip: Option<TrackItem> = None;
        let mut action_load_more = false;

        let cur_playing_id = self
            .music_current_index
            .and_then(|i| self.music_queue.get(i))
            .map(|t| t.id.clone())
            .unwrap_or_default();

        let items_snapshot = self.music_items.clone();

        ScrollArea::vertical()
            .auto_shrink([false, false])
            .show(ui, |ui| {
                for (idx, item) in items_snapshot.iter().enumerate() {
                    let is_playing_now =
                        !cur_playing_id.is_empty() && cur_playing_id.eq_ignore_ascii_case(&item.id);
                    let is_fav = self.config.is_favorite(&item.id);
                    let tex = self
                        .thumb_cache
                        .get_or_request_rect(&item.thumbnail_url(), &self.tx, ctx);

                    let row_w = (ui.available_width() - 24.0).max(320.0);
                    ui.horizontal(|ui| {
                        ui.add_space(12.0);
                        let (row_rect, row_resp) =
                            ui.allocate_exact_size(Vec2::new(row_w, 56.0), Sense::click());
                        let hovered = row_resp.hovered();
                        if hovered {
                            ui.ctx().set_cursor_icon(CursorIcon::PointingHand);
                        }

                        let painter = ui.painter_at(row_rect);
                        if is_playing_now {
                            painter.rect_filled(
                                row_rect,
                                CornerRadius::same(8),
                                Color32::from_rgb(0x22, 0x1A, 0x1D),
                            );
                        } else if hovered {
                            painter.rect_filled(row_rect, CornerRadius::same(8), BG_SEARCH_BTN);
                        }

                        // Col 0: Número da faixa ou ▶ (36px)
                        let ind_txt = if is_playing_now {
                            "▶".to_string()
                        } else {
                            format!("{}", idx + 1)
                        };
                        painter.text(
                            Pos2::new(row_rect.left() + 18.0, row_rect.center().y),
                            Align2::CENTER_CENTER,
                            ind_txt,
                            FontId::new(13.0, FontFamily::Proportional),
                            if is_playing_now { YT_RED } else { TEXT_SECONDARY },
                        );

                        // Col 1: Miniatura 78x46 com badge de duração
                        let thumb_rect = Rect::from_center_size(
                            Pos2::new(row_rect.left() + 78.0, row_rect.center().y),
                            Vec2::new(78.0, 46.0),
                        );
                        painter.rect_filled(thumb_rect, CornerRadius::same(4), BG_SURFACE);
                        if let Some(ref t) = tex {
                            painter.image(
                                t.id(),
                                thumb_rect,
                                Rect::from_min_max(Pos2::ZERO, Pos2::new(1.0, 1.0)),
                                Color32::WHITE,
                            );
                        }
                        if !item.duration.is_empty() && item.duration != "--:--" {
                            let dw = (item.duration.len() as f32 * 6.0 + 8.0).max(28.0);
                            let dr = Rect::from_min_size(
                                Pos2::new(thumb_rect.right() - dw - 3.0, thumb_rect.bottom() - 16.0),
                                Vec2::new(dw, 14.0),
                            );
                            painter.rect_filled(
                                dr,
                                CornerRadius::same(3),
                                Color32::from_rgba_unmultiplied(15, 15, 15, 210),
                            );
                            painter.text(
                                dr.center(),
                                Align2::CENTER_CENTER,
                                &item.duration,
                                FontId::new(9.5, FontFamily::Proportional),
                                TEXT_PRIMARY,
                            );
                        }

                        // Col 2: Título e Artista
                        let text_x = thumb_rect.right() + 14.0;
                        let max_chars = ((row_rect.width() - 340.0) / 7.2).max(24.0) as usize;
                        painter.text(
                            Pos2::new(text_x, row_rect.center().y - 9.0),
                            Align2::LEFT_CENTER,
                            truncate_chars(&item.title, max_chars),
                            FontId::new(13.0, FontFamily::Proportional),
                            if is_playing_now { YT_RED } else { TEXT_PRIMARY },
                        );
                        painter.text(
                            Pos2::new(text_x, row_rect.center().y + 10.0),
                            Align2::LEFT_CENTER,
                            truncate_chars(&item.artist, max_chars),
                            FontId::new(11.5, FontFamily::Proportional),
                            TEXT_SECONDARY,
                        );

                        // Col 3: Ações à Direita (Coração, +Fila, Clipe)
                        let mut hit_action = false;
                        if !item.is_playlist_card() {
                            let clip_r = Rect::from_center_size(
                                Pos2::new(row_rect.right() - 36.0, row_rect.center().y),
                                Vec2::new(54.0, 26.0),
                            );
                            let clip_resp = ui.interact(
                                clip_r,
                                ui.id().with(("m_clip", idx)),
                                Sense::click(),
                            );
                            painter.rect_filled(clip_r, CornerRadius::same(4), BG_SEARCH_BTN);
                            painter.rect_stroke(clip_r, CornerRadius::same(4), Stroke::new(1.0_f32, BORDER_SUBTLE), StrokeKind::Inside);
                            painter.text(clip_r.center(), Align2::CENTER_CENTER, "Clipe", FontId::new(11.0, FontFamily::Proportional), TEXT_PRIMARY);
                            if clip_resp.clicked() {
                                action_watch_clip = Some(item.clone());
                                hit_action = true;
                            }

                            let q_r = Rect::from_center_size(
                                Pos2::new(clip_r.left() - 34.0, row_rect.center().y),
                                Vec2::new(54.0, 26.0),
                            );
                            let q_resp = ui.interact(
                                q_r,
                                ui.id().with(("m_q", idx)),
                                Sense::click(),
                            );
                            painter.rect_filled(q_r, CornerRadius::same(4), BG_SEARCH_BTN);
                            painter.rect_stroke(q_r, CornerRadius::same(4), Stroke::new(1.0_f32, BORDER_SUBTLE), StrokeKind::Inside);
                            painter.text(q_r.center(), Align2::CENTER_CENTER, "+ Fila", FontId::new(11.0, FontFamily::Proportional), TEXT_PRIMARY);
                            if q_resp.clicked() {
                                action_queue = Some(item.clone());
                                hit_action = true;
                            }

                            let fav_r = Rect::from_center_size(
                                Pos2::new(q_r.left() - 24.0, row_rect.center().y),
                                Vec2::new(28.0, 28.0),
                            );
                            let fav_resp = ui.interact(
                                fav_r,
                                ui.id().with(("m_fav", idx)),
                                Sense::click(),
                            );
                            icons::draw_heart(
                                &painter,
                                fav_r.center(),
                                14.0,
                                if is_fav { YT_RED } else { TEXT_SECONDARY },
                                is_fav,
                            );
                            if fav_resp.clicked() {
                                action_fav = Some(item.clone());
                                hit_action = true;
                            }
                        }

                        if row_resp.clicked() && !hit_action {
                            action_activate = Some((idx, item.clone()));
                        }
                    });
                }

                if !self.music_continuation.is_empty() {
                    ui.add_space(10.0);
                    ui.vertical_centered(|ui| {
                        if ui
                            .add_enabled(
                                !self.music_is_loading_feed,
                                egui::Button::new(
                                    RichText::new("Carregar mais").size(12.5).color(TEXT_PRIMARY),
                                )
                                .fill(BG_HOVER)
                                .corner_radius(CornerRadius::same(16))
                                .min_size(Vec2::new(160.0, 34.0)),
                            )
                            .clicked()
                        {
                            action_load_more = true;
                        }
                    });
                    ui.add_space(16.0);
                }
            });

        if let Some((idx, item)) = action_activate {
            if item.is_playlist_card() {
                self.opened_music_playlist = Some(item);
                self.load_music_section(MusicNavSection::AccountPlaylistTracks, false);
            } else if self.music_nav == MusicNavSection::Queue && idx < self.music_queue.len() {
                self.play_music_index(idx);
            } else {
                let tracks = self.music_items.clone();
                self.set_music_queue_and_play(tracks, idx);
            }
        }
        if let Some(fav_track) = action_fav {
            self.config.toggle_favorite(&fav_track);
            if self.music_nav == MusicNavSection::Favorites {
                self.music_items = self.config.favorites.clone();
            }
        }
        if let Some(q_track) = action_queue {
            self.music_queue.push(q_track.clone());
            if self.music_nav == MusicNavSection::Queue {
                self.music_items = self.music_queue.clone();
            }
            self.sync_music_bg_prefs();
            self.status_text = format!("Adicionado à fila de música: {}", q_track.title);
        }
        if let Some(clip) = action_watch_clip {
            self.active_env = ActiveEnvironment::Video;
            self.start_video_playback(clip, None);
        }
        if action_load_more {
            if self.music_nav == MusicNavSection::Search {
                self.execute_search(true);
            } else {
                let sec = self.music_nav;
                self.load_music_section(sec, true);
            }
        }
    }

    // Barra Inferior YT Music (Exatamente WpfMusicPlayerBar.xaml: Height 82, #181818)
    fn render_music_player_bar(&mut self, ctx: &egui::Context) {
        egui::TopBottomPanel::bottom("music_player_bar")
            .exact_height(82.0)
            .show_separator_line(false)
            .frame(egui::Frame::NONE.fill(BG_MUSIC_BAR))
            .show(ctx, |ui| {
                let full_rect = ui.max_rect();
                let painter = ui.painter_at(full_rect);

                let cur_track = self
                    .music_current_index
                    .and_then(|i| self.music_queue.get(i))
                    .cloned();

                let total_dur = self
                    .music_player
                    .get_duration_seconds()
                    .max(
                        cur_track
                            .as_ref()
                            .map(|t| t.duration_seconds as f64)
                            .unwrap_or(0.0),
                    )
                    .max(1.0);
                let cur_pos = if self.is_dragging_music_scrubber {
                    self.drag_music_scrub_sec
                } else {
                    self.music_player.get_position_seconds()
                };

                // 1. Scrubber na borda superior (2px -> 4px no hover, #FF0000 + music_offtopic #3EA6FF)
                let scrub_hit = Rect::from_min_max(
                    full_rect.min,
                    Pos2::new(full_rect.right(), full_rect.top() + 14.0),
                );
                let scrub_resp = ui.interact(scrub_hit, ui.id().with("mus_scrub"), Sense::click_and_drag());
                if scrub_resp.hovered() {
                    ui.ctx().set_cursor_icon(CursorIcon::PointingHand);
                }
                let bar_h = if scrub_resp.hovered() || self.is_dragging_music_scrubber {
                    4.0
                } else {
                    2.0
                };
                let track_r = Rect::from_min_max(
                    full_rect.min,
                    Pos2::new(full_rect.right(), full_rect.top() + bar_h),
                );
                painter.rect_filled(track_r, CornerRadius::ZERO, Color32::from_white_alpha(56));

                for seg in &self.music_sponsors {
                    let s = (seg.start / total_dur).clamp(0.0, 1.0) as f32;
                    let e = (seg.end / total_dur).clamp(0.0, 1.0) as f32;
                    if e > s {
                        let sr = Rect::from_min_max(
                            Pos2::new(track_r.left() + track_r.width() * s, track_r.top()),
                            Pos2::new(track_r.left() + track_r.width() * e, track_r.bottom()),
                        );
                        painter.rect_filled(sr, CornerRadius::ZERO, YT_BLUE);
                    }
                }

                let ratio = (cur_pos / total_dur).clamp(0.0, 1.0) as f32;
                let played_r = Rect::from_min_max(
                    track_r.min,
                    Pos2::new(track_r.left() + track_r.width() * ratio, track_r.bottom()),
                );
                painter.rect_filled(played_r, CornerRadius::ZERO, YT_RED_MUSIC);
                if scrub_resp.hovered() || self.is_dragging_music_scrubber {
                    painter.circle_filled(
                        Pos2::new(track_r.left() + track_r.width() * ratio, track_r.center().y),
                        6.0,
                        YT_RED_MUSIC,
                    );
                }
                if let Some(ppos) = scrub_resp.hover_pos().or_else(|| ctx.pointer_interact_pos()) {
                    if scrub_resp.dragged() || scrub_resp.clicked() {
                        let r = ((ppos.x - track_r.left()) / track_r.width()).clamp(0.0, 1.0);
                        let target = (r as f64) * total_dur;
                        if scrub_resp.dragged() {
                            self.is_dragging_music_scrubber = true;
                            self.drag_music_scrub_sec = target;
                        }
                        if scrub_resp.drag_stopped() || scrub_resp.clicked() {
                            self.is_dragging_music_scrubber = false;
                            self.music_player.seek_seconds(target);
                        }
                    }
                }

                let cy = full_rect.top() + 14.0 + (full_rect.height() - 14.0) * 0.5;

                // 2. ESQUERDA (Capa 48x48 + Título/Artista + Coração)
                let thumb_r = Rect::from_center_size(
                    Pos2::new(full_rect.left() + 40.0, cy),
                    Vec2::new(48.0, 48.0),
                );
                painter.rect_filled(thumb_r, CornerRadius::same(6), Color32::from_rgb(0x26, 0x26, 0x26));
                if let Some(ref t) = cur_track {
                    if let Some(tex) = self
                        .thumb_cache
                        .get_or_request_rect(&t.thumbnail_url(), &self.tx, ctx)
                    {
                        painter.image(
                            tex.id(),
                            thumb_r,
                            Rect::from_min_max(Pos2::ZERO, Pos2::new(1.0, 1.0)),
                            Color32::WHITE,
                        );
                    }
                    painter.text(
                        Pos2::new(thumb_r.right() + 12.0, cy - 9.0),
                        Align2::LEFT_CENTER,
                        truncate_chars(&t.title, 30),
                        FontId::new(13.5, FontFamily::Proportional),
                        TEXT_PRIMARY,
                    );
                    painter.text(
                        Pos2::new(thumb_r.right() + 12.0, cy + 10.0),
                        Align2::LEFT_CENTER,
                        truncate_chars(&t.artist, 30),
                        FontId::new(11.5, FontFamily::Proportional),
                        TEXT_SECONDARY,
                    );

                    let like_r = Rect::from_center_size(
                        Pos2::new(full_rect.left() + 318.0, cy),
                        Vec2::new(34.0, 34.0),
                    );
                    let like_resp = ui.interact(like_r, ui.id().with("bar_like"), Sense::click());
                    let is_fav = self.config.is_favorite(&t.id);
                    icons::draw_heart(
                        &painter,
                        like_r.center(),
                        16.0,
                        if is_fav { YT_RED } else { TEXT_SECONDARY },
                        is_fav,
                    );
                    if like_resp.clicked() {
                        self.config.toggle_favorite(t);
                    }
                } else {
                    painter.text(
                        Pos2::new(thumb_r.right() + 12.0, cy - 8.0),
                        Align2::LEFT_CENTER,
                        "Nenhuma música tocando",
                        FontId::new(13.5, FontFamily::Proportional),
                        TEXT_PRIMARY,
                    );
                    painter.text(
                        Pos2::new(thumb_r.right() + 12.0, cy + 10.0),
                        Align2::LEFT_CENTER,
                        "Escolha uma faixa no catálogo",
                        FontId::new(11.5, FontFamily::Proportional),
                        TEXT_SECONDARY,
                    );
                }

                // 3. CENTRO (Shuffle, Prev, Play/Pause Circular Branco 46x46, Next, Repeat)
                let cx = full_rect.center().x;
                let pp_r = Rect::from_center_size(Pos2::new(cx, cy), Vec2::new(46.0, 46.0));
                let pp_resp = ui.interact(pp_r, ui.id().with("mbar_pp"), Sense::click());
                if pp_resp.hovered() {
                    ui.ctx().set_cursor_icon(CursorIcon::PointingHand);
                }
                painter.circle_filled(pp_r.center(), 23.0, Color32::WHITE);
                let is_playing = self.music_player.is_playing();
                if is_playing {
                    icons::draw_pause_bars(&painter, pp_r.center(), 16.0, BG_MAIN);
                } else {
                    icons::draw_play_triangle(
                        &painter,
                        Pos2::new(pp_r.center().x + 1.5, pp_r.center().y),
                        16.0,
                        BG_MAIN,
                    );
                }
                if pp_resp.clicked() {
                    if self.music_current_index.is_none() && !self.music_items.is_empty() {
                        let tracks = self.music_items.clone();
                        self.set_music_queue_and_play(tracks, 0);
                    } else {
                        self.toggle_music_play_pause();
                    }
                }

                let prev_r = Rect::from_center_size(Pos2::new(cx - 52.0, cy), Vec2::new(36.0, 36.0));
                let prev_resp = ui.interact(prev_r, ui.id().with("mbar_prev"), Sense::click());
                if prev_resp.hovered() {
                    ui.ctx().set_cursor_icon(CursorIcon::PointingHand);
                }
                icons::draw_prev_track(&painter, prev_r.center(), 15.0, Color32::WHITE);
                if prev_resp.clicked() {
                    self.play_prev_music();
                }

                let next_r = Rect::from_center_size(Pos2::new(cx + 52.0, cy), Vec2::new(36.0, 36.0));
                let next_resp = ui.interact(next_r, ui.id().with("mbar_next"), Sense::click());
                if next_resp.hovered() {
                    ui.ctx().set_cursor_icon(CursorIcon::PointingHand);
                }
                icons::draw_next_track(&painter, next_r.center(), 15.0, Color32::WHITE);
                if next_resp.clicked() {
                    self.play_next_music();
                }

                let shuf_r = Rect::from_center_size(Pos2::new(cx - 98.0, cy), Vec2::new(34.0, 34.0));
                let shuf_resp = ui
                    .interact(shuf_r, ui.id().with("mbar_shuf"), Sense::click())
                    .on_hover_text(if self.config.shuffle {
                        "Ordem aleatória: Ativada"
                    } else {
                        "Ordem aleatória: Desativada"
                    });
                if shuf_resp.hovered() {
                    ui.ctx().set_cursor_icon(CursorIcon::PointingHand);
                    painter.circle_filled(shuf_r.center(), 16.0, BG_HOVER_ALT);
                }
                icons::draw_shuffle(
                    &painter,
                    shuf_r.center(),
                    18.0,
                    if self.config.shuffle { YT_RED } else { Color32::from_rgb(0xB3, 0xB3, 0xB3) },
                );
                if self.config.shuffle {
                    painter.circle_filled(
                        Pos2::new(shuf_r.center().x, shuf_r.bottom() - 3.0),
                        2.0,
                        YT_RED,
                    );
                }
                if shuf_resp.clicked() {
                    self.config.shuffle = !self.config.shuffle;
                    self.config.save();
                    self.sync_music_bg_prefs();
                }

                let rep_r = Rect::from_center_size(Pos2::new(cx + 98.0, cy), Vec2::new(34.0, 34.0));
                let rep = self.config.repeat_mode();
                let rep_resp = ui
                    .interact(rep_r, ui.id().with("mbar_rep"), Sense::click())
                    .on_hover_text(match rep {
                        RepeatMode::None => "Repetir: Desativado",
                        RepeatMode::RepeatAll => "Repetir: Todas as faixas",
                        RepeatMode::RepeatOne => "Repetir: Música atual (1)",
                    });
                if rep_resp.hovered() {
                    ui.ctx().set_cursor_icon(CursorIcon::PointingHand);
                    painter.circle_filled(rep_r.center(), 16.0, BG_HOVER_ALT);
                }
                icons::draw_repeat(
                    &painter,
                    rep_r.center(),
                    18.0,
                    if rep == RepeatMode::None {
                        Color32::from_rgb(0xB3, 0xB3, 0xB3)
                    } else {
                        YT_RED
                    },
                    rep == RepeatMode::RepeatOne,
                );
                if rep != RepeatMode::None {
                    painter.circle_filled(
                        Pos2::new(rep_r.center().x, rep_r.bottom() - 3.0),
                        2.0,
                        YT_RED,
                    );
                }
                if rep_resp.clicked() {
                    self.config.repeat = rep.next() as i32;
                    self.config.save();
                    self.sync_music_bg_prefs();
                }

                // 4. DIREITA (Tempo + Slider Fino Branco 64px + Alto-falante + Fila)
                let q_btn = Rect::from_center_size(
                    Pos2::new(full_rect.right() - 32.0, cy),
                    Vec2::new(34.0, 34.0),
                );
                let q_resp = ui.interact(q_btn, ui.id().with("mbar_q"), Sense::click());
                if q_resp.hovered() {
                    ui.ctx().set_cursor_icon(CursorIcon::PointingHand);
                }
                icons::draw_queue(&painter, q_btn.center(), 16.0, Color32::WHITE);
                if q_resp.clicked() {
                    self.load_music_section(MusicNavSection::Queue, false);
                }

                let spk_btn = Rect::from_center_size(
                    Pos2::new(q_btn.left() - 24.0, cy),
                    Vec2::new(34.0, 34.0),
                );
                let spk_resp = ui.interact(spk_btn, ui.id().with("mbar_spk"), Sense::click());
                if spk_resp.hovered() {
                    ui.ctx().set_cursor_icon(CursorIcon::PointingHand);
                }
                icons::draw_speaker(
                    &painter,
                    spk_btn.center(),
                    16.0,
                    Color32::WHITE,
                    self.config.is_music_muted || self.config.music_volume <= 0.01,
                );
                if spk_resp.clicked() {
                    self.config.is_music_muted = !self.config.is_music_muted;
                    self.config.save();
                    self.sync_music_bg_prefs();
                    self.music_player
                        .set_volume(self.config.music_volume, self.config.is_music_muted);
                }

                let vol_r = Rect::from_min_size(
                    Pos2::new(spk_btn.left() - 72.0, cy - 10.0),
                    Vec2::new(64.0, 20.0),
                );
                if let Some(v) = draw_thin_white_slider(
                    ui,
                    &painter,
                    vol_r,
                    "mus_vol_slider",
                    if self.config.is_music_muted {
                        0.0
                    } else {
                        self.config.music_volume as f32
                    },
                ) {
                    self.config.music_volume = (v as f64 * 100.0).round() / 100.0;
                    if self.config.music_volume > 0.001 {
                        self.config.is_music_muted = false;
                    }
                    self.config.save();
                    self.sync_music_bg_prefs();
                    self.music_player
                        .set_volume(self.config.music_volume, self.config.is_music_muted);
                }

                // Scroll do mouse em qualquer ponto da barra de música ajusta volume em ±2% (igual WpfMusicPlayerBar)
                if let Some(p) = ctx.input(|i| i.pointer.hover_pos()) {
                    if full_rect.contains(p) {
                        let scroll_y = ctx.input(|i| i.raw_scroll_delta.y);
                        if scroll_y.abs() > 0.5 {
                            self.adjust_music_volume(if scroll_y > 0.0 { 0.02 } else { -0.02 });
                        }
                    }
                    if spk_btn.union(vol_r).expand(6.0).contains(p) {
                        let pct_txt = if self.config.is_music_muted {
                            "Mudo".to_string()
                        } else {
                            format!("{}%", (self.config.music_volume * 100.0).round() as i32)
                        };
                        painter.text(
                            Pos2::new(vol_r.center().x, vol_r.top() - 8.0),
                            Align2::CENTER_CENTER,
                            pct_txt,
                            FontId::new(10.5, FontFamily::Proportional),
                            TEXT_PRIMARY,
                        );
                    }
                }

                painter.text(
                    Pos2::new(vol_r.left() - 14.0, cy),
                    Align2::RIGHT_CENTER,
                    format!("{} / {}", format_seconds(cur_pos), format_seconds(total_dur)),
                    FontId::new(12.0, FontFamily::Proportional),
                    TEXT_SECONDARY,
                );
            });
    }

    // ========================================================================
    // Modais (Conta Google DPAPI fiel ao WpfGoogleAuthDialog.xaml)
    // ========================================================================

    fn render_google_auth_modal(&mut self, ctx: &egui::Context) {
        let mut open = self.show_auth_modal;
        egui::Window::new("LiteTube — Conta Google")
            .open(&mut open)
            .collapsible(false)
            .resizable(false)
            .fixed_size(Vec2::new(500.0, 410.0))
            .anchor(Align2::CENTER_CENTER, Vec2::ZERO)
            .show(ctx, |ui| {
                ui.add_space(4.0);
                ui.label(
                    RichText::new("Conta Google & Sessão Segura")
                        .size(19.0)
                        .strong()
                        .color(TEXT_PRIMARY),
                );
                ui.label(
                    RichText::new("Seus cookies ficam protegidos no Cofre Criptografado do Windows (DPAPI) e nunca são salvos em texto aberto.")
                        .size(12.0)
                        .color(TEXT_SECONDARY),
                );
                ui.add_space(12.0);

                // Card de Status (#1F1F1F, CornerRadius 12)
                let has_vault = has_stored_cookies(&self.config.cookies_file_path);
                egui::Frame::NONE
                    .fill(BG_HOVER)
                    .corner_radius(CornerRadius::same(12))
                    .inner_margin(Margin::same(14))
                    .show(ui, |ui| {
                        ui.set_width(452.0);
                        if self.account_profile.is_authenticated {
                            ui.label(
                                RichText::new(format!(
                                    "● Conectado como {} {}",
                                    self.account_profile.display_name,
                                    self.account_profile.email_or_handle
                                ))
                                .size(14.0)
                                .strong()
                                .color(GREEN_CONN),
                            );
                            ui.label(
                                RichText::new("Cofre DPAPI ativo • Início, Inscrições, Histórico e Playlists sincronizados.")
                                    .size(12.0)
                                    .color(TEXT_SECONDARY),
                            );
                        } else if has_vault {
                            ui.label(
                                RichText::new("● Conta Google Conectada (Cofre DPAPI)")
                                    .size(14.0)
                                    .strong()
                                    .color(GREEN_CONN),
                            );
                        } else {
                            ui.label(
                                RichText::new("○ Nenhuma conta conectada")
                                    .size(14.0)
                                    .strong()
                                    .color(TEXT_SECONDARY),
                            );
                        }
                        if !self.auth_modal_status.is_empty() {
                            ui.add_space(4.0);
                            ui.label(
                                RichText::new(&self.auth_modal_status)
                                    .size(12.0)
                                    .color(YT_BLUE),
                            );
                        }
                    });

                ui.add_space(14.0);
                ui.label(
                    RichText::new("EXTRAIR DO NAVEGADOR (1 CLIQUE)")
                        .size(11.0)
                        .strong()
                        .color(TEXT_MUTED),
                );
                ui.add_space(6.0);
                ui.horizontal(|ui| {
                    for (id, lbl) in [
                        ("firefox", "Firefox"),
                        ("chrome", "Chrome"),
                        ("edge", "Edge"),
                        ("brave", "Brave"),
                    ] {
                        if ui
                            .add_enabled(
                                !self.auth_modal_busy,
                                egui::Button::new(RichText::new(lbl).size(12.5).color(TEXT_PRIMARY))
                                    .fill(BG_HOVER)
                                    .corner_radius(CornerRadius::same(10))
                                    .min_size(Vec2::new(112.0, 34.0)),
                            )
                            .clicked()
                        {
                            self.auth_modal_busy = true;
                            self.auth_modal_status = format!("Extraindo sessão do {}...", lbl);
                            let b = id.to_string();
                            let tx = self.tx.clone();
                            thread::spawn(move || {
                                let res = extract_cookies_from_browser(&b);
                                let _ = tx.send(AsyncMessage::BrowserCookiesExtracted {
                                    browser: b,
                                    result: res,
                                });
                            });
                        }
                    }
                });

                ui.add_space(14.0);
                ui.label(
                    RichText::new("IMPORTAÇÃO MANUAL (NETSCAPE COOKIES.TXT)")
                        .size(11.0)
                        .strong()
                        .color(TEXT_MUTED),
                );
                ui.add_space(6.0);
                ui.add(
                    egui::TextEdit::multiline(&mut self.auth_paste_buffer)
                        .hint_text("Cole aqui as linhas do arquivo cookies.txt...")
                        .desired_rows(4)
                        .desired_width(470.0),
                );

                ui.add_space(12.0);
                ui.horizontal(|ui| {
                    if ui
                        .add_enabled(
                            !self.auth_modal_busy,
                            egui::Button::new(
                                RichText::new("Salvar no Cofre DPAPI")
                                    .size(12.5)
                                    .strong()
                                    .color(BG_MAIN),
                            )
                            .fill(YT_BLUE)
                            .corner_radius(CornerRadius::same(10))
                            .min_size(Vec2::new(165.0, 34.0)),
                        )
                        .clicked()
                    {
                        if looks_like_netscape_cookies(&self.auth_paste_buffer) {
                            if save_protected_cookies(&self.auth_paste_buffer) {
                                self.config.cookies_file_path =
                                    get_vault_file_path().to_string_lossy().to_string();
                                self.config.browser_for_cookies = "none".to_string();
                                self.config.save();
                                self.auth_paste_buffer.clear();
                                GoogleSessionService::shared().invalidate_cache();
                                self.auth_modal_status =
                                    "Cookies salvos com criptografia Windows DPAPI!".to_string();
                                self.refresh_account_profile(true);
                                self.load_video_section(VideoNavSection::Home, false);
                            }
                        } else {
                            self.auth_modal_status =
                                "O texto colado não parece um arquivo Netscape cookies.txt válido."
                                    .to_string();
                        }
                    }

                    ui.with_layout(Layout::right_to_left(Align::Center), |ui| {
                        if ui
                            .add_enabled(
                                !self.auth_modal_busy && has_vault,
                                egui::Button::new(
                                    RichText::new("Desconectar Conta")
                                        .size(12.5)
                                        .color(Color32::from_rgb(0xFF, 0x6B, 0x6B)),
                                )
                                .fill(Color32::from_rgb(0x2A, 0x16, 0x18))
                                .corner_radius(CornerRadius::same(10))
                                .min_size(Vec2::new(145.0, 34.0)),
                            )
                            .clicked()
                        {
                            let _ = fs::remove_file(get_vault_file_path());
                            let _ = fs::remove_file(get_legacy_plaintext_path());
                            self.config.cookies_file_path.clear();
                            self.config.browser_for_cookies = "none".to_string();
                            self.config.save();
                            GoogleSessionService::shared().invalidate_cache();
                            self.account_profile = GoogleAccountProfile::default();
                            self.auth_modal_status = "Conta desconectada.".to_string();
                        }
                    });
                });
            });
        self.show_auth_modal = open;
    }

    fn render_open_url_modal(&mut self, ctx: &egui::Context) {
        let mut open = self.show_open_url_modal;
        let mut trigger_play = false;
        egui::Window::new("Abrir Link do YouTube")
            .open(&mut open)
            .collapsible(false)
            .resizable(false)
            .fixed_size(Vec2::new(420.0, 125.0))
            .anchor(Align2::CENTER_CENTER, Vec2::ZERO)
            .show(ctx, |ui| {
                ui.label("Cole a URL do vídeo do YouTube ou o ID de 11 caracteres:");
                ui.add_space(6.0);
                let resp = ui.add(
                    egui::TextEdit::singleline(&mut self.open_url_input)
                        .hint_text("https://www.youtube.com/watch?v=...")
                        .desired_width(390.0),
                );
                if resp.lost_focus() && ui.input(|i| i.key_pressed(Key::Enter)) {
                    trigger_play = true;
                }
                ui.add_space(10.0);
                if white_pill_button(ui, "Reproduzir Agora") {
                    trigger_play = true;
                }
            });
        if trigger_play {
            let input = self.open_url_input.clone();
            self.open_url_input.clear();
            open = false;
            self.play_video_by_url(&input);
        }
        self.show_open_url_modal = open;
    }

    fn render_new_playlist_modal(&mut self, ctx: &egui::Context) {
        let mut open = self.show_new_playlist_modal;
        let mut create_now = false;
        egui::Window::new("Nova Playlist Local")
            .open(&mut open)
            .collapsible(false)
            .resizable(false)
            .fixed_size(Vec2::new(360.0, 120.0))
            .anchor(Align2::CENTER_CENTER, Vec2::ZERO)
            .show(ctx, |ui| {
                ui.label("Nome da nova playlist:");
                ui.add_space(4.0);
                let resp = ui.add(
                    egui::TextEdit::singleline(&mut self.new_playlist_name_input)
                        .desired_width(330.0),
                );
                if resp.lost_focus() && ui.input(|i| i.key_pressed(Key::Enter)) {
                    create_now = true;
                }
                ui.add_space(8.0);
                if white_pill_button(ui, "Criar Playlist") {
                    create_now = true;
                }
            });
        if create_now {
            let name = self.new_playlist_name_input.trim().to_string();
            if !name.is_empty() {
                self.config.playlists.push(UserPlaylist {
                    id: format!("pl_{:x}", next_pseudo_random_u64()),
                    name,
                    source_url: String::new(),
                    tracks: Vec::new(),
                });
                self.config.save();
            }
            self.new_playlist_name_input.clear();
            open = false;
        }
        self.show_new_playlist_modal = open;
    }

    fn render_import_playlist_modal(&mut self, ctx: &egui::Context) {
        let mut open = self.show_import_playlist_modal;
        let mut do_import = false;
        egui::Window::new("Importar Playlist por URL")
            .open(&mut open)
            .collapsible(false)
            .resizable(false)
            .fixed_size(Vec2::new(420.0, 125.0))
            .anchor(Align2::CENTER_CENTER, Vec2::ZERO)
            .show(ctx, |ui| {
                ui.label("Cole a URL de uma playlist do YouTube ou YouTube Music:");
                ui.add_space(6.0);
                let resp = ui.add(
                    egui::TextEdit::singleline(&mut self.import_playlist_url_input)
                        .hint_text("https://music.youtube.com/playlist?list=...")
                        .desired_width(390.0),
                );
                if resp.lost_focus() && ui.input(|i| i.key_pressed(Key::Enter)) {
                    do_import = true;
                }
                ui.add_space(10.0);
                if white_pill_button(ui, "Importar Playlist") {
                    do_import = true;
                }
            });
        if do_import {
            let raw = self.import_playlist_url_input.trim().to_string();
            self.import_playlist_url_input.clear();
            open = false;
            if let Some(pos) = raw.find("list=") {
                let list_id = raw[pos + 5..].split('&').next().unwrap_or("").to_string();
                if !list_id.is_empty() {
                    let cfg = self.config.clone();
                    let tx = self.tx.clone();
                    self.status_text = "Importando playlist...".to_string();
                    thread::spawn(move || {
                        let page = fetch_music_playlist_items(&list_id, &cfg, None);
                        let _ = tx.send(AsyncMessage::ImportedPlaylistLoaded {
                            name: format!("Playlist {}", &list_id[..list_id.len().min(8)]),
                            url: raw,
                            tracks: page.items,
                        });
                    });
                }
            }
        }
        self.show_import_playlist_modal = open;
    }
}

// ============================================================================
// Componentes Visuais Fiéis ao XAML Original
// ============================================================================

fn draw_sidebar_item(
    ui: &mut egui::Ui,
    label: &str,
    active: bool,
    expanded: bool,
    draw_icon: impl FnOnce(&egui::Painter, Pos2, f32, Color32),
) -> bool {
    let size = if expanded {
        Vec2::new(ui.available_width(), 40.0)
    } else {
        Vec2::new(ui.available_width(), 58.0)
    };
    let (rect, resp) = ui.allocate_exact_size(size, Sense::click());
    if resp.hovered() {
        ui.ctx().set_cursor_icon(CursorIcon::PointingHand);
    }

    let bg = if active {
        BG_ACTIVE_NAV
    } else if resp.hovered() {
        BG_HOVER
    } else {
        Color32::TRANSPARENT
    };
    let fg = if active { TEXT_PRIMARY } else { TEXT_NAV };

    if bg != Color32::TRANSPARENT {
        ui.painter().rect_filled(rect, CornerRadius::same(10), bg);
    }

    if expanded {
        let icon_c = Pos2::new(rect.left() + 21.0, rect.center().y);
        draw_icon(ui.painter(), icon_c, 18.0, fg);
        ui.painter().text(
            Pos2::new(rect.left() + 46.0, rect.center().y),
            Align2::LEFT_CENTER,
            label,
            FontId::new(13.5, FontFamily::Proportional),
            fg,
        );
    } else {
        let icon_c = Pos2::new(rect.center().x, rect.top() + 21.0);
        draw_icon(ui.painter(), icon_c, 20.0, fg);
        ui.painter().text(
            Pos2::new(rect.center().x, rect.bottom() - 13.0),
            Align2::CENTER_CENTER,
            label,
            FontId::new(9.8, FontFamily::Proportional),
            fg,
        );
    }
    resp.clicked()
}

fn pill_chip_button(ui: &mut egui::Ui, label: &str, active: bool) -> bool {
    let fill = if active { TEXT_PRIMARY } else { CHIP_TRANSLUCENT };
    let fg = if active { BG_MAIN } else { TEXT_PRIMARY };
    let resp = ui.add(
        egui::Button::new(RichText::new(label).size(12.5).color(fg))
            .fill(fill)
            .corner_radius(CornerRadius::same(16))
            .min_size(Vec2::new(0.0, 30.0)),
    );
    if resp.hovered() {
        ui.ctx().set_cursor_icon(CursorIcon::PointingHand);
    }
    resp.clicked()
}

fn white_pill_button(ui: &mut egui::Ui, label: &str) -> bool {
    let resp = ui.add(
        egui::Button::new(RichText::new(label).size(13.0).strong().color(BG_MAIN))
            .fill(TEXT_PRIMARY)
            .corner_radius(CornerRadius::same(18))
            .min_size(Vec2::new(0.0, 34.0)),
    );
    if resp.hovered() {
        ui.ctx().set_cursor_icon(CursorIcon::PointingHand);
    }
    resp.clicked()
}

fn draw_thin_white_slider(
    ui: &mut egui::Ui,
    painter: &egui::Painter,
    rect: Rect,
    id_salt: &str,
    value_0_to_1: f32,
) -> Option<f32> {
    let resp = ui.interact(rect, ui.id().with(id_salt), Sense::click_and_drag());
    if resp.hovered() {
        ui.ctx().set_cursor_icon(CursorIcon::PointingHand);
    }
    let cy = rect.center().y;
    let track = Rect::from_min_max(
        Pos2::new(rect.left(), cy - 1.75),
        Pos2::new(rect.right(), cy + 1.75),
    );
    painter.rect_filled(track, CornerRadius::same(2), Color32::from_white_alpha(75));

    let v = value_0_to_1.clamp(0.0, 1.0);
    let fill_x = track.left() + track.width() * v;
    painter.rect_filled(
        Rect::from_min_max(track.min, Pos2::new(fill_x, track.bottom())),
        CornerRadius::same(2),
        Color32::WHITE,
    );
    painter.circle_filled(Pos2::new(fill_x, cy), 5.5, Color32::WHITE);

    if resp.clicked() || resp.dragged() {
        if let Some(pos) = resp.interact_pointer_pos() {
            return Some(((pos.x - track.left()) / track.width()).clamp(0.0, 1.0));
        }
    }
    None
}

fn draw_circular_avatar(
    painter: &egui::Painter,
    rect: Rect,
    channel_name: &str,
    texture: Option<&TextureHandle>,
) {
    if let Some(tex) = texture {
        painter.image(
            tex.id(),
            rect,
            Rect::from_min_max(Pos2::ZERO, Pos2::new(1.0, 1.0)),
            Color32::WHITE,
        );
    } else {
        let col = icons::channel_avatar_color(channel_name);
        painter.circle_filled(rect.center(), rect.width() * 0.5, col);
        let initial = channel_name
            .chars()
            .find(|c| c.is_alphanumeric())
            .map(|c| c.to_uppercase().to_string())
            .unwrap_or_else(|| "Y".to_string());
        painter.text(
            rect.center(),
            Align2::CENTER_CENTER,
            initial,
            FontId::new(rect.width() * 0.38, FontFamily::Proportional),
            TEXT_PRIMARY,
        );
    }
}

/// Card de Vídeo/Canal 100% fiel ao `GridRowTemplate` do `WpfMediaListControl.xaml`:
/// Fundo transparente em repouso (#1F1F1F no hover), miniatura 16:9 CornerRadius 12,
/// avatar circular 36x36 à esquerda, título 2 linhas, canal, views/data, +Fila e estrela.
fn render_youtube_grid_card(
    ui: &mut egui::Ui,
    item: &TrackItem,
    size: Vec2,
    thumb_h: f32,
    thumb_tex: Option<TextureHandle>,
    avatar_tex: Option<TextureHandle>,
    is_saved: bool,
    watch_ratio: f32,
) -> (bool, bool, bool, bool) {
    let (rect, resp) = ui.allocate_exact_size(size, Sense::click());
    let hovered = resp.hovered();
    if hovered {
        ui.ctx().set_cursor_icon(CursorIcon::PointingHand);
    }

    let painter = ui.painter_at(rect);
    if hovered {
        painter.rect_filled(rect, CornerRadius::same(12), BG_HOVER);
    }

    // ---- 1B. CHANNEL CARD (se item.is_channel()) ----
    if item.is_channel() {
        let av_rect = Rect::from_center_size(
            Pos2::new(rect.center().x, rect.top() + 62.0),
            Vec2::new(78.0, 78.0),
        );
        draw_circular_avatar(&painter, av_rect, &item.title, avatar_tex.as_ref());
        painter.circle_stroke(av_rect.center(), 40.0, Stroke::new(2.0_f32, YT_BLUE));

        painter.text(
            Pos2::new(rect.center().x, av_rect.bottom() + 20.0),
            Align2::CENTER_CENTER,
            truncate_chars(&item.title, 26),
            FontId::new(15.0, FontFamily::Proportional),
            TEXT_PRIMARY,
        );
        painter.text(
            Pos2::new(rect.center().x, av_rect.bottom() + 40.0),
            Align2::CENTER_CENTER,
            truncate_chars(&item.artist, 30),
            FontId::new(12.0, FontFamily::Proportional),
            TEXT_SECONDARY,
        );
        let btn_r = Rect::from_center_size(
            Pos2::new(rect.center().x, rect.bottom() - 28.0),
            Vec2::new(138.0, 32.0),
        );
        painter.rect_filled(btn_r, CornerRadius::same(16), TEXT_PRIMARY);
        painter.text(
            btn_r.center(),
            Align2::CENTER_CENTER,
            "Acessar Canal",
            FontId::new(12.5, FontFamily::Proportional),
            BG_MAIN,
        );
        return (false, resp.clicked(), false, false);
    }

    // ---- 1A. VIDEO / PLAYLIST CARD ----
    let thumb_rect = Rect::from_min_size(rect.min, Vec2::new(rect.width(), thumb_h));
    painter.rect_filled(thumb_rect, CornerRadius::same(12), BG_HOVER);
    if let Some(ref tex) = thumb_tex {
        // Desenha imagem com cantos arredondados via mesh ou rect_filled + image
        painter.image(
            tex.id(),
            thumb_rect,
            Rect::from_min_max(Pos2::ZERO, Pos2::new(1.0, 1.0)),
            Color32::WHITE,
        );
    }

    // Barra de progresso assistido (3px, #FF0033 na base)
    if watch_ratio > 0.02 {
        let pbg = Rect::from_min_max(
            Pos2::new(thumb_rect.left(), thumb_rect.bottom() - 3.0),
            thumb_rect.max,
        );
        painter.rect_filled(pbg, CornerRadius::ZERO, Color32::from_black_alpha(120));
        let pfg = Rect::from_min_max(
            pbg.min,
            Pos2::new(pbg.left() + pbg.width() * watch_ratio.clamp(0.0, 1.0), pbg.bottom()),
        );
        painter.rect_filled(pfg, CornerRadius::ZERO, YT_RED);
    }

    // Badge de duração (#CC0F0F0F, CornerRadius 6)
    if !item.duration.is_empty() && item.duration != "--:--" {
        let is_live = item.duration.eq_ignore_ascii_case("AO VIVO");
        let bw = (item.duration.len() as f32 * 6.6 + 12.0).max(38.0);
        let br = Rect::from_min_size(
            Pos2::new(thumb_rect.right() - bw - 8.0, thumb_rect.bottom() - 26.0),
            Vec2::new(bw, 19.0),
        );
        painter.rect_filled(
            br,
            CornerRadius::same(6),
            if is_live {
                YT_RED
            } else {
                Color32::from_rgba_unmultiplied(15, 15, 15, 210)
            },
        );
        painter.text(
            br.center(),
            Align2::CENTER_CENTER,
            &item.duration,
            FontId::new(11.0, FontFamily::Proportional),
            TEXT_PRIMARY,
        );
    }

    // Área de informações inferior (Margin 6,10,6,8)
    let info_top = thumb_rect.bottom() + 10.0;
    let av_rect = Rect::from_min_size(Pos2::new(rect.left() + 6.0, info_top + 2.0), Vec2::new(36.0, 36.0));
    draw_circular_avatar(&painter, av_rect, &item.artist, avatar_tex.as_ref());
    let av_resp = ui.interact(av_rect, ui.id().with(("card_av", &item.id)), Sense::click());

    // Ações rápidas (+Fila e Salvar ★) visíveis no hover ou se salvo
    let mut clicked_queue = false;
    let mut clicked_save = false;
    let mut text_right = rect.right() - 8.0;

    if !item.is_playlist_card() && (hovered || is_saved) {
        let fav_r = Rect::from_min_size(
            Pos2::new(rect.right() - 28.0, info_top),
            Vec2::new(22.0, 22.0),
        );
        let fav_resp = ui.interact(fav_r, ui.id().with(("card_fav", &item.id)), Sense::click());
        painter.text(
            fav_r.center(),
            Align2::CENTER_CENTER,
            if is_saved { "★" } else { "☆" },
            FontId::new(15.0, FontFamily::Proportional),
            if is_saved { YT_BLUE } else { TEXT_SECONDARY },
        );
        if fav_resp.clicked() {
            clicked_save = true;
        }

        if hovered {
            let q_r = Rect::from_min_size(
                Pos2::new(fav_r.left() - 42.0, info_top + 1.0),
                Vec2::new(38.0, 20.0),
            );
            let q_resp = ui.interact(q_r, ui.id().with(("card_q", &item.id)), Sense::click());
            painter.rect_filled(q_r, CornerRadius::same(10), CHIP_TRANSLUCENT);
            painter.text(
                q_r.center(),
                Align2::CENTER_CENTER,
                "+Fila",
                FontId::new(10.0, FontFamily::Proportional),
                TEXT_PRIMARY,
            );
            if q_resp.clicked() {
                clicked_queue = true;
            }
            text_right = q_r.left() - 6.0;
        } else {
            text_right = fav_r.left() - 4.0;
        }
    }

    // Coluna Central: Título (2 linhas) + Canal + Views/Data
    let text_rect = Rect::from_min_max(
        Pos2::new(av_rect.right() + 10.0, info_top),
        Pos2::new(text_right.max(av_rect.right() + 60.0), rect.bottom() - 4.0),
    );
    let mut clicked_channel = av_resp.clicked() && !item.is_playlist_card();

    let mut child_ui = ui.new_child(
        egui::UiBuilder::new()
            .max_rect(text_rect)
            .layout(Layout::top_down(Align::Min)),
    );
    child_ui.add(
        egui::Label::new(
            RichText::new(truncate_chars(&item.title, 62))
                .size(13.0)
                .strong()
                .color(TEXT_PRIMARY),
        )
        .wrap(),
    );
    child_ui.add_space(2.0);
    let ch_resp = child_ui.add(
        egui::Label::new(
            RichText::new(truncate_chars(&item.artist, 34))
                .size(12.0)
                .color(TEXT_SECONDARY),
        )
        .sense(Sense::click()),
    );
    if ch_resp.clicked() && !item.is_playlist_card() {
        clicked_channel = true;
    }

    let meta_line = match (
        !item.view_count_text.is_empty(),
        !item.published_time_text.is_empty(),
    ) {
        (true, true) => format!("{} • {}", item.view_count_text, item.published_time_text),
        (true, false) => item.view_count_text.clone(),
        (false, true) => item.published_time_text.clone(),
        (false, false) => String::new(),
    };
    if !meta_line.is_empty() {
        child_ui.label(
            RichText::new(truncate_chars(&meta_line, 38))
                .size(11.5)
                .color(TEXT_SECONDARY),
        );
    }

    let clicked_card = resp.clicked() && !clicked_save && !clicked_channel && !clicked_queue;
    (clicked_card, clicked_channel, clicked_save, clicked_queue)
}

/// Card horizontal compacto da barra lateral da Watch Page (padrão oficial YouTube:
/// coluna 402px, miniatura 168x94 CornerRadius 8, espaçamento 8px, título 2 linhas, canal e views/data).
fn render_youtube_sidebar_card(
    ui: &mut egui::Ui,
    item: &TrackItem,
    width: f32,
    thumb_tex: Option<TextureHandle>,
    is_saved: bool,
) -> (bool, bool, bool, bool) {
    let row_h = 98.0_f32;
    let (rect, resp) = ui.allocate_exact_size(Vec2::new(width, row_h), Sense::click());
    let hovered = resp.hovered();
    if hovered {
        ui.ctx().set_cursor_icon(CursorIcon::PointingHand);
    }

    let painter = ui.painter_at(rect);
    if hovered {
        painter.rect_filled(rect, CornerRadius::same(8), BG_HOVER);
    }

    // Miniatura oficial 168x94 (CornerRadius 8)
    let thumb_w = 168.0_f32.min(width * 0.45);
    let thumb_h = 94.0_f32;
    let thumb_rect = Rect::from_min_size(
        Pos2::new(rect.left() + 2.0, rect.top() + 2.0),
        Vec2::new(thumb_w, thumb_h),
    );
    painter.rect_filled(thumb_rect, CornerRadius::same(8), BG_HOVER);
    if let Some(ref tex) = thumb_tex {
        painter.image(
            tex.id(),
            thumb_rect,
            Rect::from_min_max(Pos2::ZERO, Pos2::new(1.0, 1.0)),
            Color32::WHITE,
        );
    }

    // Badge de duração no canto inferior direito da miniatura
    if !item.duration.is_empty() && item.duration != "--:--" {
        let is_live = item.duration.eq_ignore_ascii_case("AO VIVO");
        let bw = (item.duration.len() as f32 * 6.2 + 10.0).max(34.0);
        let br = Rect::from_min_size(
            Pos2::new(thumb_rect.right() - bw - 5.0, thumb_rect.bottom() - 21.0),
            Vec2::new(bw, 17.0),
        );
        painter.rect_filled(
            br,
            CornerRadius::same(4),
            if is_live {
                YT_RED
            } else {
                Color32::from_rgba_unmultiplied(15, 15, 15, 215)
            },
        );
        painter.text(
            br.center(),
            Align2::CENTER_CENTER,
            &item.duration,
            FontId::new(10.5, FontFamily::Proportional),
            TEXT_PRIMARY,
        );
    }

    // Ações rápidas (+Fila e Salvar ★) discretas no canto inferior direito no hover
    let mut clicked_queue = false;
    let mut clicked_save = false;
    if !item.is_playlist_card() && (hovered || is_saved) {
        let fav_r = Rect::from_min_size(
            Pos2::new(rect.right() - 24.0, rect.bottom() - 24.0),
            Vec2::new(20.0, 20.0),
        );
        let fav_resp = ui.interact(fav_r, ui.id().with(("sb_fav", &item.id)), Sense::click());
        painter.text(
            fav_r.center(),
            Align2::CENTER_CENTER,
            if is_saved { "★" } else { "☆" },
            FontId::new(14.0, FontFamily::Proportional),
            if is_saved { YT_BLUE } else { TEXT_SECONDARY },
        );
        if fav_resp.clicked() {
            clicked_save = true;
        }

        if hovered {
            let q_r = Rect::from_min_size(
                Pos2::new(fav_r.left() - 40.0, rect.bottom() - 23.0),
                Vec2::new(36.0, 18.0),
            );
            let q_resp = ui.interact(q_r, ui.id().with(("sb_q", &item.id)), Sense::click());
            painter.rect_filled(q_r, CornerRadius::same(9), CHIP_TRANSLUCENT);
            painter.text(
                q_r.center(),
                Align2::CENTER_CENTER,
                "+Fila",
                FontId::new(9.8, FontFamily::Proportional),
                TEXT_PRIMARY,
            );
            if q_resp.clicked() {
                clicked_queue = true;
            }
        }
    }

    // Metadados à direita da miniatura (espaçamento 8px)
    let text_rect = Rect::from_min_max(
        Pos2::new(thumb_rect.right() + 8.0, rect.top() + 3.0),
        Pos2::new((rect.right() - 6.0).max(thumb_rect.right() + 60.0), rect.bottom() - 3.0),
    );

    let mut clicked_channel = false;
    let mut child_ui = ui.new_child(
        egui::UiBuilder::new()
            .max_rect(text_rect)
            .layout(Layout::top_down(Align::Min)),
    );
    child_ui.spacing_mut().item_spacing.y = 2.0;
    child_ui.add(
        egui::Label::new(
            RichText::new(truncate_chars(&item.title, 54))
                .size(13.5)
                .strong()
                .color(TEXT_PRIMARY),
        )
        .wrap(),
    );
    let ch_resp = child_ui.add(
        egui::Label::new(
            RichText::new(truncate_chars(&item.artist, 30))
                .size(12.0)
                .color(TEXT_SECONDARY),
        )
        .sense(Sense::click()),
    );
    if ch_resp.clicked() && !item.is_playlist_card() {
        clicked_channel = true;
    }

    let meta_line = match (
        !item.view_count_text.is_empty(),
        !item.published_time_text.is_empty(),
    ) {
        (true, true) => format!("{} • {}", item.view_count_text, item.published_time_text),
        (true, false) => item.view_count_text.clone(),
        (false, true) => item.published_time_text.clone(),
        (false, false) => String::new(),
    };
    if !meta_line.is_empty() {
        child_ui.label(
            RichText::new(truncate_chars(&meta_line, 34))
                .size(11.5)
                .color(TEXT_SECONDARY),
        );
    }

    let clicked_card = resp.clicked() && !clicked_save && !clicked_channel && !clicked_queue;
    (clicked_card, clicked_channel, clicked_save, clicked_queue)
}

fn truncate_chars(s: &str, max_chars: usize) -> String {
    if s.chars().count() <= max_chars {
        return s.to_string();
    }
    let mut out: String = s.chars().take(max_chars.saturating_sub(1)).collect();
    out.push('…');
    out
}

// ============================================================================
// Protocolo Automatizado --self-test
// ============================================================================

fn run_self_test() -> i32 {
    init_logger();
    log_info(
        "SELFTEST",
        "Iniciando validação automatizada (LiteTube 2.0 — 100% Rust Nativo)...",
    );

    let config = AppConfig::load();
    log_info(
        "SELFTEST",
        &format!(
            "1. AppConfig & WatchHistory: VideoQuality={}p | SavedVideos={} | Favorites={} | Playlists={}",
            config.video_quality_height,
            config.saved_videos.len(),
            config.favorites.len(),
            config.playlists.len()
        ),
    );

    let enc = protect_text("test_cookie_payload").unwrap_or_default();
    let dec = unprotect_text(&enc).unwrap_or_default();
    let vault_ok = dec == "test_cookie_payload";
    let real_vault_present = has_stored_cookies(&config.cookies_file_path);
    let sapisid =
        crate::auth::build_sapisid_hash_header("SAPISID=mock_val_123", "https://www.youtube.com")
            .unwrap_or_default();
    let sapisid_ok = sapisid.starts_with("SAPISIDHASH ");
    let redacted = sanitize_sensitive_data(
        "Authorization: SAPISIDHASH 123_abc | Cookie: SAPISID=secret123; HSID=xyz",
    );
    let redact_ok = !redacted.contains("secret123") && !redacted.contains("123_abc");

    let mock_carousel = r#"{"musicCarouselShelfRenderer":{"header":{"musicCarouselShelfBasicHeaderRenderer":{"title":{"runs":[{"text":"Ouvir novamente"}]}}},"contents":[{"musicTwoRowItemRenderer":{"title":{"runs":[{"text":"Bohemian Rhapsody"}]},"subtitle":{"runs":[{"text":"Queen"}]},"navigationEndpoint":{"watchEndpoint":{"videoId":"fJ9rUzIMcZQ"}}}}]}}"#;
    let parsed_carousel = parse_music_carousel_shelves(mock_carousel, &mut HashSet::new());

    let parsed_pls = parse_playlists_json(
        r#"{"items":[{"playlistRenderer":{"playlistId":"WL","title":{"simpleText":"Assistir mais tarde"}}},{"playlistRenderer":{"playlistId":"LL","title":{"simpleText":"Liked videos"}}},{"lockupViewModel":{"contentType":"LOCKUP_CONTENT_TYPE_PLAYLIST","contentId":"PL1234567890ABCDEF","metadata":{"lockupMetadataViewModel":{"title":{"content":"Minha Playlist Real"}}}}}]}"#,
        true,
    );
    let pls_ok =
        parsed_pls.items.len() == 1 && parsed_pls.items[0].playlist_id == "PL1234567890ABCDEF";

    let parsed_music_pls = parse_music_playlists_json(
        r#"{"items":[{"musicTwoRowItemRenderer":{"title":{"runs":[{"text":"Músicas marcadas com Gostei"}]},"navigationEndpoint":{"browseEndpoint":{"browseId":"VLLM"}}}},{"musicTwoRowItemRenderer":{"title":{"runs":[{"text":"Rock Favoritas"}]},"subtitle":{"runs":[{"text":"Playlist • 42 músicas"}]},"navigationEndpoint":{"browseEndpoint":{"browseId":"VLPLMusicReal123456"}}}}]}"#,
        true,
    );
    let music_pls_ok = parsed_music_pls.items.len() == 1
        && parsed_music_pls.items[0].playlist_id == "PLMusicReal123456";

    let parsed_music_tracks = parse_music_playlist_items_json(
        r#"{"contents":{"singleColumnBrowseResultsRenderer":{"tabs":[{"tabRenderer":{"content":{"sectionListRenderer":{"contents":[{"musicPlaylistShelfRenderer":{"contents":[{"musicResponsiveListItemRenderer":{"playlistItemData":{"videoId":"fJ9rUzIMcZQ"},"flexColumns":[{"musicResponsiveListItemFlexColumnRenderer":{"text":{"runs":[{"text":"Bohemian Rhapsody"}]}}},{"musicResponsiveListItemFlexColumnRenderer":{"text":{"runs":[{"text":"Queen"}]}}}],"fixedColumns":[{"musicResponsiveListItemFixedColumnRenderer":{"text":{"runs":[{"text":"5:55"}]}}}]}}]}},{"musicShelfRenderer":{"title":{"runs":[{"text":"Sugestões"}]},"contents":[{"musicResponsiveListItemRenderer":{"playlistItemData":{"videoId":"dQw4w9WgXcQ"},"flexColumns":[{"musicResponsiveListItemFlexColumnRenderer":{"text":{"runs":[{"text":"Recomendada Fora da Playlist"}]}}}]}}]}}],"continuations":[{"nextContinuationData":{"continuation":"OUTER_SUGGESTIONS_TOKEN"}}]}}}}]}}}"#,
        true,
    );
    let music_tracks_ok = parsed_music_tracks.items.len() == 1
        && parsed_music_tracks.items[0].id == "fJ9rUzIMcZQ"
        && parsed_music_tracks.continuation_token.is_empty();

    let mock_segs = vec![
        SponsorSegment {
            category: "sponsor".to_string(),
            action_type: "skip".to_string(),
            start: 10.0,
            end: 42.0,
            uuid: "u1".to_string(),
        },
        SponsorSegment {
            category: "music_offtopic".to_string(),
            action_type: "skip".to_string(),
            start: 180.0,
            end: 210.0,
            uuid: "u2".to_string(),
        },
    ];
    let sb_ok = check_and_get_skip_target(15.0, &mock_segs).map(|(t, _)| t) == Some(42.0)
        && check_and_get_skip_target(185.0, &mock_segs).map(|(t, _)| t) == Some(210.0);
    let cascade_ok =
        detect_codec_from_urls("https://g.com/vp?itag=398", "https://g.com/vp?itag=251")
            == "AV1/Opus (Direct3D 11)"
            && detect_codec_from_urls("https://g.com/vp?itag=136", "https://g.com/vp?itag=140")
                == "H.264/AAC (Direct3D 11)";

    let v_player = RustMediaPlayer::new(false);
    let m_player = RustMediaPlayer::new(true);
    v_player.set_volume(0.41, false);
    m_player.set_volume(0.04, false);
    let audio_curve_ok = linear_to_vlc_volume(0.04, false) == 34
        && linear_to_vlc_volume(0.41, false) == 74
        && linear_to_vlc_volume(0.0, false) == 0
        && linear_to_vlc_volume(1.0, false) == 100
        && linear_to_vlc_volume(0.5, true) == 0
        && v_player.get_target_vlc_volume() == 74
        && m_player.get_target_vlc_volume() == 34;
    let media_ok = v_player.is_available() && m_player.is_available() && audio_curve_ok;
    let uri_ok = parse_litetube_or_youtube_url(
        "litetube://play?url=https%3A%2F%2Fwww.youtube.com%2Fwatch%3Fv%3DdQw4w9WgXcQ%26t%3D125s",
    ) == Some(("dQw4w9WgXcQ".to_string(), Some(125.0)))
        && parse_litetube_or_youtube_url("https://youtu.be/dQw4w9WgXcQ?t=1m30s")
            == Some(("dQw4w9WgXcQ".to_string(), Some(90.0)));
    let home_page = fetch_home_feed(&config, None);
    trim_memory();
    let ram_mb = get_current_ram_usage_mb();

    log_info(
        "SELFTEST",
        &format!(
            "Resumo: VaultOk={} | RealDpapi={} | SapisidOk={} | RedactOk={} | Carousel={} | PlsOk={} | MusicPlsOk={} | MusicTracksOk={} | SbOk={} | CodecOk={} | AudioCurveOk={} | MediaOk={} | UriOk={} | HomeItems={} (Auth={}) | RAM={:.1}MB",
            vault_ok,
            real_vault_present,
            sapisid_ok,
            redact_ok,
            parsed_carousel.len(),
            pls_ok,
            music_pls_ok,
            music_tracks_ok,
            sb_ok,
            cascade_ok,
            audio_curve_ok,
            media_ok,
            uri_ok,
            home_page.items.len(),
            home_page.used_account_auth,
            ram_mb
        ),
    );

    if vault_ok
        && sapisid_ok
        && redact_ok
        && pls_ok
        && music_pls_ok
        && music_tracks_ok
        && sb_ok
        && cascade_ok
        && media_ok
        && uri_ok
    {
        log_info("SELFTEST", "Validação automatizada concluída com 0 falhas!");
        0
    } else {
        log_error("SELFTEST", "Falha no protocolo --self-test.");
        1
    }
}

fn main() -> eframe::Result<()> {
    let args: Vec<String> = std::env::args().collect();
    if args.iter().any(|a| a.eq_ignore_ascii_case("--self-test")) {
        let code = run_self_test();
        std::process::exit(code);
    }

    let startup_url = args.iter().skip(1).find(|a| !a.starts_with('-')).cloned();

    // Instância única: se recebeu URL externa (litetube:// ou YouTube) e já existe um LiteTube aberto,
    // encaminha via Named Pipe e encerra imediatamente sem abrir segunda janela.
    if let Some(ref url) = startup_url {
        if try_send_ipc_play_url(url) {
            return Ok(());
        }
    }

    init_logger();
    register_litetube_protocol();

    let icon_data = load_app_icon();
    let mut viewport = egui::ViewportBuilder::default()
        .with_title("LiteTube — YouTube & YT Music")
        .with_inner_size([1340.0, 840.0])
        .with_min_inner_size([940.0, 620.0]);
    if let Some(icon) = icon_data {
        viewport = viewport.with_icon(Arc::new(icon));
    }

    let options = eframe::NativeOptions {
        viewport,
        ..Default::default()
    };

    eframe::run_native(
        "LiteTube",
        options,
        Box::new(move |cc| Ok(Box::new(LiteTubeApp::new(cc, startup_url)))),
    )
}

fn load_app_icon() -> Option<egui::IconData> {
    let ico_path = models::get_base_dir().join("app.ico");
    let bytes = fs::read(ico_path).ok()?;
    let img = image::load_from_memory(&bytes).ok()?.to_rgba8();
    let (w, h) = (img.width(), img.height());
    Some(egui::IconData {
        rgba: img.into_raw(),
        width: w,
        height: h,
    })
}
