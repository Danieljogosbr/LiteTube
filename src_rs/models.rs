// ============================================================================
// LiteTube 2.0 (Rust) — Modelos de Dados & Persistência Compatível (100% JSON)
// Preserva 100% de compatibilidade com data/config.json e data/watch_history.json
// ============================================================================

#![allow(dead_code)]

use serde::{Deserialize, Serialize};
use std::fs;
use std::path::PathBuf;
use std::sync::{Arc, Mutex, OnceLock};
use std::time::{SystemTime, UNIX_EPOCH};

#[derive(Debug, Clone, Copy, PartialEq, Eq, Serialize, Deserialize)]
#[repr(i32)]
pub enum RepeatMode {
    None = 0,
    RepeatAll = 1,
    RepeatOne = 2,
}

impl Default for RepeatMode {
    fn default() -> Self {
        RepeatMode::None
    }
}

impl RepeatMode {
    pub fn from_i32(val: i32) -> Self {
        match val {
            1 => RepeatMode::RepeatAll,
            2 => RepeatMode::RepeatOne,
            _ => RepeatMode::None,
        }
    }

    pub fn next(self) -> Self {
        match self {
            RepeatMode::None => RepeatMode::RepeatAll,
            RepeatMode::RepeatAll => RepeatMode::RepeatOne,
            RepeatMode::RepeatOne => RepeatMode::None,
        }
    }
}

#[derive(Debug, Clone, Serialize, Deserialize)]
#[serde(rename_all = "PascalCase")]
pub struct TrackItem {
    #[serde(default)]
    pub id: String,
    #[serde(default)]
    pub title: String,
    #[serde(default)]
    pub artist: String,
    #[serde(default = "default_duration")]
    pub duration: String,
    #[serde(default)]
    pub duration_seconds: i32,
    #[serde(default = "default_item_type")]
    pub item_type: String, // "video", "channel", "playlist", "short"
    #[serde(default)]
    pub channel_id: String,
    #[serde(default)]
    pub playlist_id: String,
    #[serde(default)]
    pub custom_thumb_url: String,
    #[serde(default)]
    pub channel_avatar_url: String,
    #[serde(default)]
    pub view_count_text: String,
    #[serde(default)]
    pub published_time_text: String,
}

fn default_duration() -> String {
    "--:--".to_string()
}

fn default_item_type() -> String {
    "video".to_string()
}

impl Default for TrackItem {
    fn default() -> Self {
        Self {
            id: String::new(),
            title: String::new(),
            artist: String::new(),
            duration: default_duration(),
            duration_seconds: 0,
            item_type: default_item_type(),
            channel_id: String::new(),
            playlist_id: String::new(),
            custom_thumb_url: String::new(),
            channel_avatar_url: String::new(),
            view_count_text: String::new(),
            published_time_text: String::new(),
        }
    }
}

impl TrackItem {
    pub fn is_channel(&self) -> bool {
        self.item_type.eq_ignore_ascii_case("channel")
    }

    pub fn is_playlist_card(&self) -> bool {
        self.item_type.eq_ignore_ascii_case("playlist")
    }

    pub fn is_short(&self) -> bool {
        self.item_type.eq_ignore_ascii_case("short")
    }

    pub fn url(&self) -> String {
        if self.is_channel() && !self.channel_id.is_empty() {
            format!("https://www.youtube.com/channel/{}", self.channel_id)
        } else if self.is_playlist_card() && !self.playlist_id.is_empty() {
            format!("https://www.youtube.com/playlist?list={}", self.playlist_id)
        } else if !self.id.is_empty() {
            format!("https://www.youtube.com/watch?v={}", self.id)
        } else {
            String::new()
        }
    }

    pub fn thumbnail_url(&self) -> String {
        if !self.custom_thumb_url.trim().is_empty() {
            let u = self.custom_thumb_url.trim();
            if u.starts_with("//") {
                return format!("https:{}", u);
            }
            return u.to_string();
        }
        if self.is_channel() && !self.channel_avatar_url.trim().is_empty() {
            let u = self.channel_avatar_url.trim();
            if u.starts_with("//") {
                return format!("https:{}", u);
            }
            return u.to_string();
        }
        if !self.id.is_empty() && !self.id.starts_with("UC") && !self.id.starts_with("PL_") {
            return format!("https://i.ytimg.com/vi/{}/hqdefault.jpg", self.id);
        }
        String::new()
    }
}

#[derive(Debug, Clone, Default)]
pub struct SearchResultPage {
    pub items: Vec<TrackItem>,
    pub continuation_token: String,
    pub query: String,
}

#[derive(Debug, Clone, Default)]
pub struct ChannelPageData {
    pub channel_id: String,
    pub channel_name: String,
    pub subscriber_text: String,
    pub avatar_url: String,
    pub active_tab: String, // "home" | "videos" | "playlists"
    pub items: Vec<TrackItem>,
    pub continuation_token: String,
}

#[derive(Debug, Clone, Default)]
pub struct VideoChapter {
    pub start_seconds: i32,
    pub title: String,
}

#[derive(Debug, Clone, Default)]
pub struct VideoItem {
    pub id: String,
    pub title: String,
    pub author: String,
    pub description: String,
    pub duration_seconds: i64,
    pub view_count: i64,
    pub upload_date_raw: String,
    pub thumbnail_url: String,
    pub available_resolutions: Vec<i32>,
}

impl VideoItem {
    pub fn formatted_views(&self) -> String {
        if self.view_count <= 0 {
            return String::new();
        }
        format_pt_br_integer(self.view_count) + " visualizações"
    }

    pub fn formatted_upload_date(&self) -> String {
        let raw = self.upload_date_raw.trim();
        if raw.len() == 8 && raw.chars().all(|c| c.is_ascii_digit()) {
            let yyyy = &raw[0..4];
            let mm: usize = raw[4..6].parse().unwrap_or(0);
            let dd: usize = raw[6..8].parse().unwrap_or(0);
            let months = [
                "", "jan.", "fev.", "mar.", "abr.", "mai.", "jun.", "jul.", "ago.", "set.", "out.",
                "nov.", "dez.",
            ];
            if (1..=12).contains(&mm) && (1..=31).contains(&dd) {
                return format!("{} de {} de {}", dd, months[mm], yyyy);
            }
        }
        if raw.len() >= 10 && raw.as_bytes()[4] == b'-' && raw.as_bytes()[7] == b'-' {
            let yyyy = &raw[0..4];
            let mm: usize = raw[5..7].parse().unwrap_or(0);
            let dd: usize = raw[8..10].parse().unwrap_or(0);
            let months = [
                "", "jan.", "fev.", "mar.", "abr.", "mai.", "jun.", "jul.", "ago.", "set.", "out.",
                "nov.", "dez.",
            ];
            if (1..=12).contains(&mm) && (1..=31).contains(&dd) {
                return format!("{} de {} de {}", dd, months[mm], yyyy);
            }
        }
        raw.to_string()
    }
}

pub fn format_pt_br_integer(n: i64) -> String {
    let s = n.abs().to_string();
    let mut out = String::new();
    for (idx, ch) in s.chars().rev().enumerate() {
        if idx > 0 && idx % 3 == 0 {
            out.push('.');
        }
        out.push(ch);
    }
    if n < 0 {
        out.push('-');
    }
    out.chars().rev().collect()
}

#[derive(Debug, Clone, Default)]
pub struct WatchNextDetails {
    pub related_videos: Vec<TrackItem>,
    pub subscriber_count_text: String,
    pub like_count_text: String,
    pub comment_count_text: String,
    pub comments_continuation_token: String,
}

#[derive(Debug, Clone, Serialize, Deserialize, Default)]
#[serde(rename_all = "PascalCase")]
pub struct UserPlaylist {
    #[serde(default)]
    pub id: String,
    #[serde(default)]
    pub name: String,
    #[serde(default, alias = "RemoteUrl")]
    pub source_url: String,
    #[serde(default)]
    pub tracks: Vec<TrackItem>,
}

#[derive(Debug, Clone, Serialize, Deserialize)]
#[serde(rename_all = "PascalCase")]
pub struct AppConfig {
    #[serde(default = "default_browser_for_cookies")]
    pub browser_for_cookies: String,
    #[serde(default)]
    pub cookies_file_path: String,
    #[serde(default)]
    pub volume: Option<f64>,
    #[serde(default)]
    pub is_muted: Option<bool>,
    #[serde(default = "default_video_volume")]
    pub video_volume: f64,
    #[serde(default)]
    pub is_video_muted: bool,
    #[serde(default = "default_music_volume")]
    pub music_volume: f64,
    #[serde(default)]
    pub is_music_muted: bool,
    #[serde(default = "default_video_quality")]
    pub video_quality_height: i32,
    #[serde(default = "default_true")]
    pub auto_pause_music_when_video_plays: bool,
    #[serde(default = "default_max_cache", rename = "MaxCacheSizeMB", alias = "MaxCacheSizeMb")]
    pub max_cache_size_mb: i32,
    #[serde(default)]
    pub repeat: i32,
    #[serde(default)]
    pub shuffle: bool,
    #[serde(default = "default_true")]
    pub minimize_to_tray_on_close: bool,
    #[serde(default = "default_true")]
    pub enable_global_hotkeys: bool,
    #[serde(default)]
    pub hide_shorts: bool,
    #[serde(default = "default_true")]
    pub enable_sponsor_block: bool,
    #[serde(default = "default_true")]
    pub auto_skip_sponsors: bool,
    #[serde(default)]
    pub is_theater_mode: bool,
    #[serde(default)]
    pub favorites: Vec<TrackItem>,
    #[serde(default)]
    pub saved_videos: Vec<TrackItem>,
    #[serde(default)]
    pub playlists: Vec<UserPlaylist>,
    #[serde(default)]
    pub search_history: Vec<String>,
}

fn default_browser_for_cookies() -> String {
    "none".to_string()
}
fn default_volume() -> f64 {
    0.12
}
fn default_video_volume() -> f64 {
    0.85
}
fn default_music_volume() -> f64 {
    0.12
}
fn default_video_quality() -> i32 {
    720
}
fn default_true() -> bool {
    true
}
fn default_max_cache() -> i32 {
    100
}

impl Default for AppConfig {
    fn default() -> Self {
        Self {
            browser_for_cookies: default_browser_for_cookies(),
            cookies_file_path: String::new(),
            volume: Some(default_volume()),
            is_muted: Some(false),
            video_volume: default_video_volume(),
            is_video_muted: false,
            music_volume: default_music_volume(),
            is_music_muted: false,
            video_quality_height: default_video_quality(),
            auto_pause_music_when_video_plays: true,
            max_cache_size_mb: default_max_cache(),
            repeat: 0,
            shuffle: false,
            minimize_to_tray_on_close: true,
            enable_global_hotkeys: true,
            hide_shorts: false,
            enable_sponsor_block: true,
            auto_skip_sponsors: true,
            is_theater_mode: false,
            favorites: Vec::new(),
            saved_videos: Vec::new(),
            playlists: Vec::new(),
            search_history: Vec::new(),
        }
    }
}

pub fn get_base_dir() -> PathBuf {
    if let Ok(exe) = std::env::current_exe() {
        if let Some(parent) = exe.parent() {
            // Se estiver em target/release ou target/debug, sobe para a raiz do projeto
            if parent.ends_with("release") || parent.ends_with("debug") {
                if let Some(p2) = parent.parent().and_then(|p| p.parent()) {
                    if p2.join("data").exists() || p2.join("bin").exists() {
                        return p2.to_path_buf();
                    }
                }
            }
            return parent.to_path_buf();
        }
    }
    std::env::current_dir().unwrap_or_else(|_| PathBuf::from("."))
}

impl AppConfig {
    pub fn config_path() -> PathBuf {
        get_base_dir().join("data").join("config.json")
    }

    pub fn load() -> Self {
        let path = Self::config_path();
        if let Ok(raw) = fs::read_to_string(&path) {
            // Remove UTF-8 BOM se presente
            let clean = raw.trim_start_matches('\u{feff}');
            if let Ok(mut cfg) = serde_json::from_str::<AppConfig>(clean) {
                cfg.video_volume = cfg.video_volume.clamp(0.0, 1.0);
                cfg.music_volume = cfg.music_volume.clamp(0.0, 1.0);
                if cfg.video_quality_height < 144 {
                    cfg.video_quality_height = 720;
                }
                if cfg.max_cache_size_mb < 15 {
                    cfg.max_cache_size_mb = 100;
                }
                return cfg;
            }
        }
        let cfg = Self::default();
        cfg.save();
        cfg
    }

    pub fn save(&self) {
        let path = Self::config_path();
        if let Some(dir) = path.parent() {
            let _ = fs::create_dir_all(dir);
        }
        if let Ok(json) = serde_json::to_string_pretty(self) {
            let tmp = path.with_extension("json.tmp");
            if fs::write(&tmp, &json).is_ok() {
                let _ = fs::rename(&tmp, &path);
            } else {
                let _ = fs::write(&path, &json);
            }
        }
    }

    pub fn add_search_history(&mut self, query: &str) {
        let q = query.trim();
        if q.len() < 2 {
            return;
        }
        if q.starts_with("http://") || q.starts_with("https://") || q.starts_with("www.youtube.com")
        {
            return;
        }
        self.search_history.retain(|item| !item.eq_ignore_ascii_case(q));
        self.search_history.insert(0, q.to_string());
        if self.search_history.len() > 15 {
            self.search_history.truncate(15);
        }
        self.save();
    }

    pub fn remove_search_history(&mut self, query: &str) {
        let q = query.trim();
        self.search_history.retain(|item| !item.eq_ignore_ascii_case(q));
        self.save();
    }

    pub fn clear_search_history(&mut self) {
        self.search_history.clear();
        self.save();
    }

    pub fn is_favorite(&self, id: &str) -> bool {
        if id.is_empty() {
            return false;
        }
        self.favorites.iter().any(|x| x.id == id)
    }

    pub fn toggle_favorite(&mut self, track: &TrackItem) -> bool {
        if track.id.is_empty() {
            return false;
        }
        if let Some(pos) = self.favorites.iter().position(|x| x.id == track.id) {
            self.favorites.remove(pos);
            self.save();
            false
        } else {
            self.favorites.push(track.clone());
            self.save();
            true
        }
    }

    pub fn is_saved_video(&self, id: &str) -> bool {
        if id.is_empty() {
            return false;
        }
        self.saved_videos.iter().any(|x| x.id == id)
    }

    pub fn toggle_saved_video(&mut self, video: &TrackItem) -> bool {
        if video.id.is_empty() {
            return false;
        }
        if let Some(pos) = self.saved_videos.iter().position(|x| x.id == video.id) {
            self.saved_videos.remove(pos);
            self.save();
            false
        } else {
            self.saved_videos.insert(0, video.clone());
            self.save();
            true
        }
    }

    pub fn repeat_mode(&self) -> RepeatMode {
        RepeatMode::from_i32(self.repeat)
    }
}

// ============================================================================
// WatchHistoryService — Histórico Local e Auto-Resume (data/watch_history.json)
// ============================================================================

#[derive(Debug, Clone, Serialize, Deserialize)]
#[serde(rename_all = "PascalCase")]
pub struct WatchHistoryEntry {
    #[serde(default)]
    pub video_id: String,
    #[serde(default)]
    pub title: String,
    #[serde(default)]
    pub author: String,
    #[serde(default)]
    pub thumbnail_url: String,
    #[serde(default)]
    pub duration_seconds: i64,
    #[serde(default)]
    pub last_position_seconds: i64,
    #[serde(default)]
    pub watched_at_utc: String,
}

impl WatchHistoryEntry {
    pub fn watch_progress_ratio(&self) -> f64 {
        if self.duration_seconds <= 0 || self.last_position_seconds <= 0 {
            return 0.0;
        }
        (self.last_position_seconds as f64 / self.duration_seconds as f64).clamp(0.0, 1.0)
    }
}

pub struct WatchHistoryService {
    file_path: PathBuf,
    entries: Mutex<Vec<WatchHistoryEntry>>,
}

static WATCH_HISTORY_INSTANCE: OnceLock<Arc<WatchHistoryService>> = OnceLock::new();

impl WatchHistoryService {
    pub fn instance() -> Arc<WatchHistoryService> {
        WATCH_HISTORY_INSTANCE
            .get_or_init(|| {
                let path = get_base_dir().join("data").join("watch_history.json");
                let svc = Arc::new(WatchHistoryService {
                    file_path: path,
                    entries: Mutex::new(Vec::new()),
                });
                svc.load();
                svc
            })
            .clone()
    }

    pub fn load(&self) {
        if let Ok(raw) = fs::read_to_string(&self.file_path) {
            let clean = raw.trim_start_matches('\u{feff}');
            if let Ok(loaded) = serde_json::from_str::<Vec<WatchHistoryEntry>>(clean) {
                if let Ok(mut guard) = self.entries.lock() {
                    *guard = loaded
                        .into_iter()
                        .filter(|e| !e.video_id.trim().is_empty())
                        .collect();
                }
            }
        }
    }

    fn save_locked(&self, list: &[WatchHistoryEntry]) {
        if let Some(dir) = self.file_path.parent() {
            let _ = fs::create_dir_all(dir);
        }
        if let Ok(json) = serde_json::to_string_pretty(list) {
            let tmp = self.file_path.with_extension("json.tmp");
            if fs::write(&tmp, &json).is_ok() {
                let _ = fs::rename(&tmp, &self.file_path);
            } else {
                let _ = fs::write(&self.file_path, &json);
            }
        }
    }

    pub fn record_video_started(&self, item: &TrackItem) {
        if item.id.trim().is_empty() || item.is_channel() || item.is_playlist_card() {
            return;
        }
        if let Ok(mut guard) = self.entries.lock() {
            let prev_pos = guard
                .iter()
                .find(|x| x.video_id.eq_ignore_ascii_case(&item.id))
                .map(|x| x.last_position_seconds)
                .unwrap_or(0);
            let prev_dur = guard
                .iter()
                .find(|x| x.video_id.eq_ignore_ascii_case(&item.id))
                .map(|x| x.duration_seconds)
                .unwrap_or(0);

            guard.retain(|x| !x.video_id.eq_ignore_ascii_case(&item.id));
            let dur = if item.duration_seconds > 0 {
                item.duration_seconds as i64
            } else {
                prev_dur
            };

            guard.insert(
                0,
                WatchHistoryEntry {
                    video_id: item.id.clone(),
                    title: item.title.clone(),
                    author: item.artist.clone(),
                    thumbnail_url: item.thumbnail_url(),
                    duration_seconds: dur,
                    last_position_seconds: prev_pos,
                    watched_at_utc: now_iso_utc(),
                },
            );
            if guard.len() > 200 {
                guard.truncate(200);
            }
            self.save_locked(&guard);
        }
    }

    pub fn update_playback_progress(&self, video_id: &str, position_sec: i64, total_sec: i64) {
        if video_id.trim().is_empty() || position_sec < 0 {
            return;
        }
        if let Ok(mut guard) = self.entries.lock() {
            if let Some(entry) = guard
                .iter_mut()
                .find(|x| x.video_id.eq_ignore_ascii_case(video_id))
            {
                if total_sec > 0 {
                    entry.duration_seconds = total_sec;
                }
                if entry.duration_seconds > 0
                    && (position_sec >= entry.duration_seconds - 12
                        || (position_sec as f64 / entry.duration_seconds as f64) >= 0.95)
                {
                    entry.last_position_seconds = 0;
                } else {
                    entry.last_position_seconds = position_sec;
                }
                entry.watched_at_utc = now_iso_utc();
                self.save_locked(&guard);
            }
        }
    }

    pub fn get_resume_position_seconds(&self, video_id: &str, known_duration_sec: i32) -> i64 {
        if video_id.trim().is_empty() {
            return 0;
        }
        if let Ok(guard) = self.entries.lock() {
            if let Some(entry) = guard
                .iter()
                .find(|x| x.video_id.eq_ignore_ascii_case(video_id))
            {
                let pos = entry.last_position_seconds;
                let dur = if entry.duration_seconds > 0 {
                    entry.duration_seconds
                } else {
                    known_duration_sec as i64
                };
                if pos < 5 {
                    return 0;
                }
                if dur > 0 && (pos >= dur - 12 || (pos as f64 / dur as f64) >= 0.95) {
                    return 0;
                }
                return pos;
            }
        }
        0
    }

    pub fn get_watch_progress_ratio(&self, video_id: &str) -> f64 {
        if video_id.trim().is_empty() {
            return 0.0;
        }
        if let Ok(guard) = self.entries.lock() {
            if let Some(entry) = guard
                .iter()
                .find(|x| x.video_id.eq_ignore_ascii_case(video_id))
            {
                return entry.watch_progress_ratio();
            }
        }
        0.0
    }

    pub fn get_history_tracks(&self) -> Vec<TrackItem> {
        let mut out = Vec::new();
        if let Ok(guard) = self.entries.lock() {
            for e in guard.iter() {
                let dur_str = if e.duration_seconds > 0 {
                    format_seconds(e.duration_seconds as f64)
                } else {
                    "--:--".to_string()
                };
                out.push(TrackItem {
                    id: e.video_id.clone(),
                    title: e.title.clone(),
                    artist: if e.author.is_empty() {
                        "YouTube".to_string()
                    } else {
                        e.author.clone()
                    },
                    duration: dur_str,
                    duration_seconds: e.duration_seconds as i32,
                    custom_thumb_url: e.thumbnail_url.clone(),
                    item_type: "video".to_string(),
                    ..Default::default()
                });
            }
        }
        out
    }

    pub fn clear_history(&self) {
        if let Ok(mut guard) = self.entries.lock() {
            guard.clear();
            self.save_locked(&guard);
        }
    }
}

pub fn format_seconds(seconds: f64) -> String {
    if !seconds.is_finite() || seconds < 0.0 {
        return "0:00".to_string();
    }
    let total = seconds.round() as i64;
    let h = total / 3600;
    let m = (total % 3600) / 60;
    let s = total % 60;
    if h > 0 {
        format!("{}:{:02}:{:02}", h, m, s)
    } else {
        format!("{}:{:02}", m, s)
    }
}

pub fn parse_duration_to_seconds(dur: &str) -> i32 {
    let trimmed = dur.trim();
    if trimmed.is_empty() || trimmed == "--:--" {
        return 0;
    }
    if !trimmed.contains(':') {
        return trimmed.parse::<f64>().map(|v| v.round() as i32).unwrap_or(0);
    }
    let parts: Vec<&str> = trimmed.split(':').collect();
    if parts.len() == 2 {
        let m = parts[0].trim().parse::<i32>().unwrap_or(0);
        let s = parts[1].trim().parse::<f64>().unwrap_or(0.0).round() as i32;
        return m * 60 + s;
    }
    if parts.len() == 3 {
        let h = parts[0].trim().parse::<i32>().unwrap_or(0);
        let m = parts[1].trim().parse::<i32>().unwrap_or(0);
        let s = parts[2].trim().parse::<f64>().unwrap_or(0.0).round() as i32;
        return h * 3600 + m * 60 + s;
    }
    0
}

fn now_iso_utc() -> String {
    let secs = SystemTime::now()
        .duration_since(UNIX_EPOCH)
        .map(|d| d.as_secs())
        .unwrap_or(0);
    format!("{}Z", secs)
}
