// ============================================================================
// LiteTube 2.0 (Rust) — Camada 3: Camada de Dados YouTube & YouTube Music
// InnerTube API + Account Feeds + Fast-Path ANDROID_VR + Disjuntor Anti-Bot + yt-dlp
// ============================================================================

#![allow(dead_code)]

use crate::auth::{
    DEFAULT_USER_AGENT, GoogleSessionService, MUSIC_CLIENT_VERSION, WEB_CLIENT_VERSION,
    YtDlpCookieScope, detect_logged_in_state,
};
use crate::memory::{log_error, log_info, log_warn};
use crate::models::{
    AppConfig, ChannelPageData, SearchResultPage, TrackItem, VideoChapter, VideoItem,
    WatchHistoryService, WatchNextDetails, format_seconds, get_base_dir,
    parse_duration_to_seconds,
};
use regex::Regex;
use serde_json::Value;
use std::collections::{HashMap, HashSet};
use std::io::Read;
use std::os::windows::process::CommandExt;
use std::process::Command;
use std::sync::{Mutex, OnceLock};
use std::time::{Duration, Instant};

const CREATE_NO_WINDOW: u32 = 0x08000000;
const BROWSE_ENDPOINT: &str = "https://www.youtube.com/youtubei/v1/browse?prettyPrint=false";
const SEARCH_ENDPOINT: &str = "https://www.youtube.com/youtubei/v1/search?prettyPrint=false";
const NEXT_ENDPOINT: &str = "https://www.youtube.com/youtubei/v1/next?prettyPrint=false";
const PLAYER_ENDPOINT: &str = "https://www.youtube.com/youtubei/v1/player?prettyPrint=false";
const MUSIC_BROWSE_ENDPOINT: &str = "https://music.youtube.com/youtubei/v1/browse?prettyPrint=false";
const MUSIC_SEARCH_ENDPOINT: &str = "https://music.youtube.com/youtubei/v1/search?prettyPrint=false";

static HTTP_AGENT: OnceLock<ureq::Agent> = OnceLock::new();
static AVATAR_CACHE: OnceLock<Mutex<HashMap<String, String>>> = OnceLock::new();

pub fn http_agent() -> &'static ureq::Agent {
    HTTP_AGENT.get_or_init(|| {
        let tls = ureq::tls::TlsConfig::builder()
            .provider(ureq::tls::TlsProvider::NativeTls)
            .root_certs(ureq::tls::RootCerts::PlatformVerifier)
            .build();
        ureq::Agent::config_builder()
            .tls_config(tls)
            .timeout_global(Some(Duration::from_secs(15)))
            .user_agent(DEFAULT_USER_AGENT)
            .build()
            .into()
    })
}

pub fn http_get_text(url: &str) -> Option<String> {
    let mut resp = http_agent()
        .get(url)
        .header("Accept-Language", "pt-BR,pt;q=0.9,en-US;q=0.8,en;q=0.7")
        .call()
        .ok()?;
    resp.body_mut().read_to_string().ok()
}

pub fn http_get_bytes(url: &str) -> Option<Vec<u8>> {
    let target = if url.starts_with("//") {
        format!("https:{}", url)
    } else {
        url.to_string()
    };
    let mut resp = http_agent().get(&target).call().ok()?;
    let mut buf = Vec::with_capacity(32 * 1024);
    resp.body_mut()
        .as_reader()
        .take(4 * 1024 * 1024)
        .read_to_end(&mut buf)
        .ok()?;
    if buf.is_empty() { None } else { Some(buf) }
}

pub fn http_post_json(
    url: &str,
    json_payload: &str,
    referer: Option<&str>,
    custom_headers: &HashMap<String, String>,
) -> Option<String> {
    let mut req = http_agent()
        .post(url)
        .header("Content-Type", "application/json")
        .header("Accept-Language", "pt-BR,pt;q=0.9,en-US;q=0.8,en;q=0.7");

    if let Some(r) = referer {
        if !r.is_empty() {
            req = req.header("Referer", r);
        }
    }
    for (k, v) in custom_headers {
        req = req.header(k, v);
    }

    match req.send(json_payload.as_bytes()) {
        Ok(mut resp) => resp.body_mut().read_to_string().ok(),
        Err(e) => {
            log_warn("HTTP", &format!("Erro POST {}: {}", url, e));
            None
        }
    }
}

// ============================================================================
// Cache Global de Avatares de Canais (Bounded 500 entries)
// ============================================================================

pub fn normalize_avatar_url(raw_url: &str) -> String {
    let mut url = raw_url
        .trim()
        .replace("\\/", "/")
        .replace("\\u0026", "&");
    if url.starts_with("//") {
        url = format!("https:{}", url);
    }
    if !url.starts_with("http") {
        return String::new();
    }
    let lower = url.to_ascii_lowercase();
    if !lower.contains("yt3.ggpht.com") && !lower.contains("googleusercontent.com") {
        return String::new();
    }
    url
}

pub fn cache_channel_avatar_url(channel_id: &str, channel_name: &str, avatar_url: &str) {
    let norm = normalize_avatar_url(avatar_url);
    if norm.is_empty() {
        return;
    }
    let cache = AVATAR_CACHE.get_or_init(|| Mutex::new(HashMap::new()));
    if let Ok(mut guard) = cache.lock() {
        if guard.len() > 500 {
            guard.clear();
        }
        if !channel_id.trim().is_empty() {
            guard.insert(
                format!("id:{}", channel_id.trim().to_ascii_lowercase()),
                norm.clone(),
            );
        }
        let cname = channel_name.trim();
        if !cname.is_empty() && !cname.eq_ignore_ascii_case("youtube") {
            guard.insert(format!("name:{}", cname.to_ascii_lowercase()), norm);
        }
    }
}

pub fn get_cached_channel_avatar_url(channel_id: &str, channel_name: &str) -> String {
    let cache = AVATAR_CACHE.get_or_init(|| Mutex::new(HashMap::new()));
    if let Ok(guard) = cache.lock() {
        if !channel_id.trim().is_empty() {
            if let Some(u) = guard.get(&format!("id:{}", channel_id.trim().to_ascii_lowercase())) {
                return u.clone();
            }
        }
        if !channel_name.trim().is_empty() {
            if let Some(u) =
                guard.get(&format!("name:{}", channel_name.trim().to_ascii_lowercase()))
            {
                return u.clone();
            }
        }
    }
    String::new()
}

// ============================================================================
// Account Feeds & Playlists (Home, Subscriptions, History, Playlists, YT Music)
// ============================================================================

#[derive(Debug, Clone, Default)]
pub struct AccountFeedPage {
    pub items: Vec<TrackItem>,
    pub continuation_token: String,
    pub used_account_auth: bool,
    pub session_expired: bool,
    pub status_message: String,
}

fn build_browse_payload(browse_id: Option<&str>, continuation_token: Option<&str>) -> String {
    if let Some(cont) = continuation_token {
        if !cont.trim().is_empty() {
            return serde_json::json!({
                "context": {
                    "client": {
                        "clientName": "WEB",
                        "clientVersion": WEB_CLIENT_VERSION,
                        "hl": "pt",
                        "gl": "BR"
                    }
                },
                "continuation": cont.trim()
            })
            .to_string();
        }
    }
    serde_json::json!({
        "context": {
            "client": {
                "clientName": "WEB",
                "clientVersion": WEB_CLIENT_VERSION,
                "hl": "pt",
                "gl": "BR"
            }
        },
        "browseId": browse_id.unwrap_or("FEwhat_to_watch")
    })
    .to_string()
}

fn build_music_browse_payload(browse_id: Option<&str>, continuation_token: Option<&str>) -> String {
    if let Some(cont) = continuation_token {
        if !cont.trim().is_empty() {
            return serde_json::json!({
                "context": {
                    "client": {
                        "clientName": "WEB_REMIX",
                        "clientVersion": MUSIC_CLIENT_VERSION,
                        "hl": "pt",
                        "gl": "BR"
                    }
                },
                "continuation": cont.trim()
            })
            .to_string();
        }
    }
    serde_json::json!({
        "context": {
            "client": {
                "clientName": "WEB_REMIX",
                "clientVersion": MUSIC_CLIENT_VERSION,
                "hl": "pt",
                "gl": "BR"
            }
        },
        "browseId": browse_id.unwrap_or("FEmusic_liked_playlists")
    })
    .to_string()
}

pub fn fetch_home_feed(config: &AppConfig, continuation: Option<&str>) -> AccountFeedPage {
    let mut page = fetch_browse_video_feed("FEwhat_to_watch", config, continuation);
    if continuation.is_none() && page.items.is_empty() {
        // Fallback para tendências gerais via search quando FEwhat_to_watch exige histórico prévio
        let fallback = search_youtube_page("vídeos em alta brasil", "videos", config);
        page.items = fallback.items;
        page.continuation_token = fallback.continuation_token;
        if page.status_message.is_empty() {
            page.status_message = "Início carregado (conecte sua Conta Google para recomendações personalizadas).".to_string();
        }
    }
    page
}

pub fn fetch_subscriptions_feed(config: &AppConfig, continuation: Option<&str>) -> AccountFeedPage {
    fetch_browse_video_feed("FEsubscriptions", config, continuation)
}

pub fn fetch_history_feed(config: &AppConfig, continuation: Option<&str>) -> AccountFeedPage {
    let mut page = fetch_browse_video_feed("FEhistory", config, continuation);
    if continuation.is_none() {
        let local_items = WatchHistoryService::instance().get_history_tracks();
        if !local_items.is_empty() {
            let mut merged = Vec::with_capacity(local_items.len() + page.items.len());
            let mut seen = HashSet::new();
            for loc in local_items {
                if !loc.id.is_empty() && seen.insert(loc.id.to_ascii_lowercase()) {
                    merged.push(loc);
                }
            }
            for rem in page.items {
                if !rem.id.is_empty() && seen.insert(rem.id.to_ascii_lowercase()) {
                    merged.push(rem);
                }
            }
            page.items = merged;
        }
    }
    page
}

fn fetch_browse_video_feed(
    browse_id: &str,
    config: &AppConfig,
    continuation: Option<&str>,
) -> AccountFeedPage {
    let session = GoogleSessionService::shared();
    let (headers, has_auth) = session.build_web_headers(config);

    if !has_auth && (browse_id == "FEsubscriptions" || browse_id == "FEhistory") {
        return AccountFeedPage {
            used_account_auth: false,
            status_message: if browse_id == "FEsubscriptions" {
                "Conecte sua Conta Google para ver as inscrições dos seus canais.".to_string()
            } else {
                "Histórico local ativo (conecte sua Conta Google para sincronizar o histórico da conta).".to_string()
            },
            ..Default::default()
        };
    }

    let payload = build_browse_payload(
        if continuation.is_some() {
            None
        } else {
            Some(browse_id)
        },
        continuation,
    );
    let referer = match browse_id {
        "FEsubscriptions" => "https://www.youtube.com/feed/subscriptions",
        "FEhistory" => "https://www.youtube.com/feed/history",
        _ => "https://www.youtube.com/",
    };

    let Some(json) = http_post_json(BROWSE_ENDPOINT, &payload, Some(referer), &headers) else {
        return AccountFeedPage {
            used_account_auth: false,
            status_message: "Não foi possível carregar este feed agora. Verifique a conexão.".to_string(),
            ..Default::default()
        };
    };

    let mut page = parse_video_feed_json(&json, has_auth);
    if page.session_expired {
        session.mark_session_expired();
        page.status_message = "Sua sessão Google expirou. Clique em 'Conta Google' para reconectar.".to_string();
    }
    log_info(
        "FEED",
        &format!(
            "Feed '{}' carregado: {} itens (Auth={}, Expired={})",
            browse_id,
            page.items.len(),
            page.used_account_auth,
            page.session_expired
        ),
    );
    page
}

pub fn fetch_account_playlists(config: &AppConfig, continuation: Option<&str>) -> AccountFeedPage {
    let session = GoogleSessionService::shared();
    let (headers, has_auth) = session.build_web_headers(config);
    if !has_auth {
        return AccountFeedPage {
            used_account_auth: false,
            status_message: "Conecte sua Conta Google para visualizar suas playlists do YouTube.".to_string(),
            ..Default::default()
        };
    }

    let is_first = continuation.map(|s| s.trim().is_empty()).unwrap_or(true);
    let payload = if is_first {
        build_browse_payload(Some("FEplaylist_aggregation"), None)
    } else {
        build_browse_payload(None, continuation)
    };

    let Some(json) = http_post_json(
        BROWSE_ENDPOINT,
        &payload,
        Some("https://www.youtube.com/feed/playlists"),
        &headers,
    ) else {
        return AccountFeedPage {
            status_message: "Não foi possível carregar suas playlists agora.".to_string(),
            ..Default::default()
        };
    };

    let mut page = parse_playlists_json(&json, has_auth);
    if is_first && page.items.is_empty() && !page.session_expired {
        let lib_payload = build_browse_payload(Some("FElibrary"), None);
        if let Some(lib_json) = http_post_json(
            BROWSE_ENDPOINT,
            &lib_payload,
            Some("https://www.youtube.com/feed/you"),
            &headers,
        ) {
            let lib_page = parse_playlists_json(&lib_json, has_auth);
            if !lib_page.items.is_empty() || lib_page.session_expired {
                page = lib_page;
            }
        }
    }
    if page.session_expired {
        session.mark_session_expired();
        page.status_message = "Sua sessão Google expirou. Clique em 'Conta Google' para reconectar.".to_string();
    }
    page
}

pub fn fetch_playlist_items(
    playlist_id: &str,
    config: &AppConfig,
    continuation: Option<&str>,
) -> AccountFeedPage {
    let mut clean_id = playlist_id.trim();
    if clean_id.len() > 2 && clean_id[..2].eq_ignore_ascii_case("VL") {
        clean_id = &clean_id[2..];
    }
    if clean_id.is_empty() {
        return AccountFeedPage::default();
    }

    let (headers, has_auth) = GoogleSessionService::shared().build_web_headers(config);
    let payload = if continuation.map(|s| s.trim().is_empty()).unwrap_or(true) {
        build_browse_payload(Some(&format!("VL{}", clean_id)), None)
    } else {
        build_browse_payload(None, continuation)
    };

    let Some(json) = http_post_json(
        BROWSE_ENDPOINT,
        &payload,
        Some(&format!("https://www.youtube.com/playlist?list={}", clean_id)),
        &headers,
    ) else {
        return AccountFeedPage::default();
    };

    parse_web_playlist_items_json(&json, has_auth)
}

pub fn fetch_music_account_playlists(
    config: &AppConfig,
    continuation: Option<&str>,
) -> AccountFeedPage {
    let session = GoogleSessionService::shared();
    let (headers, has_auth) = session.build_music_headers(config);
    if !has_auth {
        return AccountFeedPage {
            used_account_auth: false,
            status_message: "Conecte sua Conta Google para visualizar suas playlists do YouTube Music.".to_string(),
            ..Default::default()
        };
    }

    let is_first = continuation.map(|s| s.trim().is_empty()).unwrap_or(true);
    let payload = if is_first {
        build_music_browse_payload(Some("FEmusic_liked_playlists"), None)
    } else {
        build_music_browse_payload(None, continuation)
    };

    let Some(json) = http_post_json(
        MUSIC_BROWSE_ENDPOINT,
        &payload,
        Some("https://music.youtube.com/library/playlists"),
        &headers,
    ) else {
        return AccountFeedPage {
            status_message: "Não foi possível carregar suas playlists do YouTube Music.".to_string(),
            ..Default::default()
        };
    };

    let mut page = parse_music_playlists_json(&json, has_auth);
    if is_first && page.items.is_empty() && !page.session_expired {
        let lib_payload = build_music_browse_payload(Some("FEmusic_library_landing"), None);
        if let Some(lib_json) = http_post_json(
            MUSIC_BROWSE_ENDPOINT,
            &lib_payload,
            Some("https://music.youtube.com/library"),
            &headers,
        ) {
            let lib_page = parse_music_playlists_json(&lib_json, has_auth);
            if !lib_page.items.is_empty() || lib_page.session_expired {
                page = lib_page;
            }
        }
    }
    page
}

pub fn fetch_music_playlist_items(
    playlist_id: &str,
    config: &AppConfig,
    continuation: Option<&str>,
) -> AccountFeedPage {
    let mut clean_id = playlist_id.trim();
    if clean_id.len() > 2 && clean_id[..2].eq_ignore_ascii_case("VL") {
        clean_id = &clean_id[2..];
    }
    if clean_id.is_empty() {
        return AccountFeedPage::default();
    }

    let (headers, has_auth) = GoogleSessionService::shared().build_music_headers(config);
    let payload = if continuation.map(|s| s.trim().is_empty()).unwrap_or(true) {
        build_music_browse_payload(Some(&format!("VL{}", clean_id)), None)
    } else {
        build_music_browse_payload(None, continuation)
    };

    if let Some(json) = http_post_json(
        MUSIC_BROWSE_ENDPOINT,
        &payload,
        Some(&format!("https://music.youtube.com/playlist?list={}", clean_id)),
        &headers,
    ) {
        let page = parse_music_playlist_items_json(&json, has_auth);
        if !page.items.is_empty() || continuation.is_some() {
            return page;
        }
    }

    fetch_playlist_items(clean_id, config, continuation)
}

// ============================================================================
// YouTube Music Personalized & Explore Feeds (WEB_REMIX)
// ============================================================================

pub fn fetch_personalized_music_feed(config: &AppConfig) -> (Vec<TrackItem>, String) {
    let (headers, has_auth) = GoogleSessionService::shared().build_music_headers(config);
    let mut list = Vec::new();
    let mut seen = HashSet::new();
    let mut used_fallback = false;

    let payload = build_music_browse_payload(Some("FEmusic_home"), None);
    if let Some(json) = http_post_json(
        MUSIC_BROWSE_ENDPOINT,
        &payload,
        Some("https://music.youtube.com/"),
        &headers,
    ) {
        let parsed = parse_music_carousel_shelves(&json, &mut seen);
        list.extend(parsed);
    }

    if !has_auth && list.len() < 8 {
        used_fallback = true;
    }

    if list.is_empty() || used_fallback {
        let exp_payload = build_music_browse_payload(Some("FEmusic_explore"), None);
        if let Some(exp_json) = http_post_json(
            MUSIC_BROWSE_ENDPOINT,
            &exp_payload,
            Some("https://music.youtube.com/explore"),
            &headers,
        ) {
            let exp_tracks = parse_music_carousel_shelves(&exp_json, &mut seen);
            if !exp_tracks.is_empty() {
                list.extend(exp_tracks);
                used_fallback = true;
            }
        }

        if list.len() < 10 {
            let trend = search_youtube_page("top músicas mais tocadas brasil oficial audio", "songs", config);
            for t in trend.items {
                if !t.is_channel() && !t.is_playlist_card() && seen.insert(t.id.to_ascii_lowercase()) {
                    list.push(t);
                    if list.len() >= 45 {
                        break;
                    }
                }
            }
            used_fallback = true;
        }
    }

    let status = if !list.is_empty() {
        if used_fallback {
            "Tendências musicais carregadas (conecte os cookies para personalizar o Supermix).".to_string()
        } else {
            "Recomendados personalizados do YouTube Music carregados.".to_string()
        }
    } else {
        "Não foi possível carregar o feed do YouTube Music.".to_string()
    };

    (list, status)
}

pub fn fetch_music_explore_feed(config: &AppConfig) -> (Vec<TrackItem>, String) {
    let (headers, _) = GoogleSessionService::shared().build_music_headers(config);
    let mut list = Vec::new();
    let mut seen = HashSet::new();

    for bid in ["FEmusic_explore", "FEmusic_charts"] {
        let payload = build_music_browse_payload(Some(bid), None);
        if let Some(json) = http_post_json(
            MUSIC_BROWSE_ENDPOINT,
            &payload,
            Some("https://music.youtube.com/explore"),
            &headers,
        ) {
            let parsed = parse_music_carousel_shelves(&json, &mut seen);
            list.extend(parsed);
        }
        if list.len() >= 25 {
            break;
        }
    }

    if list.len() < 8 {
        let page = search_youtube_page("lançamentos musicais paradas youtube music oficial", "songs", config);
        for t in page.items {
            if !t.is_channel() && !t.is_playlist_card() && seen.insert(t.id.to_ascii_lowercase()) {
                list.push(t);
                if list.len() >= 45 {
                    break;
                }
            }
        }
    }

    let status = if !list.is_empty() {
        "Paradas e lançamentos do YouTube Music carregados.".to_string()
    } else {
        "Não foi possível carregar o Explorar do YouTube Music.".to_string()
    };
    (list, status)
}

pub fn parse_music_carousel_shelves(
    json: &str,
    seen_ids: &mut HashSet<String>,
) -> Vec<TrackItem> {
    let mut out = Vec::new();
    let Ok(root) = serde_json::from_str::<Value>(json) else {
        return out;
    };
    walk_music_carousel_items(&root, seen_ids, &mut out);
    out
}

fn walk_music_carousel_items(
    el: &Value,
    seen_ids: &mut HashSet<String>,
    out: &mut Vec<TrackItem>,
) {
    if out.len() >= 80 {
        return;
    }
    match el {
        Value::Object(map) => {
            if let Some(mrlir) = map.get("musicResponsiveListItemRenderer") {
                if let Some(track) = parse_music_responsive_track(mrlir) {
                    if seen_ids.insert(track.id.to_ascii_lowercase()) {
                        out.push(track);
                    }
                }
                return;
            }
            if let Some(mtrir) = map.get("musicTwoRowItemRenderer") {
                if let Some(track) = parse_music_two_row_track_or_playlist(mtrir) {
                    if !track.is_playlist_card() && seen_ids.insert(track.id.to_ascii_lowercase()) {
                        out.push(track);
                    }
                }
                return;
            }
            for val in map.values() {
                walk_music_carousel_items(val, seen_ids, out);
            }
        }
        Value::Array(arr) => {
            for child in arr {
                walk_music_carousel_items(child, seen_ids, out);
            }
        }
        _ => {}
    }
}

// ============================================================================
// Busca YouTube / YT Music, Continuação, Canais e WatchNext
// ============================================================================

pub fn search_youtube_page(
    query: &str,
    filter_type: &str,
    config: &AppConfig,
) -> SearchResultPage {
    let q = query.trim();
    if q.is_empty() {
        return SearchResultPage::default();
    }

    // URL direta colada na busca
    if q.starts_with("http://")
        || q.starts_with("https://")
        || q.starts_with("www.youtube.com")
        || q.starts_with("music.youtube.com")
        || q.starts_with("youtu.be/")
    {
        if let Some(vid) = extract_video_id_from_any_url(q) {
            let meta = fetch_video_metadata(&vid, config);
            let item = TrackItem {
                id: vid.clone(),
                title: if meta.title.is_empty() {
                    format!("Vídeo {}", vid)
                } else {
                    meta.title
                },
                artist: if meta.author.is_empty() {
                    "YouTube".to_string()
                } else {
                    meta.author
                },
                duration: if meta.duration_seconds > 0 {
                    format_seconds(meta.duration_seconds as f64)
                } else {
                    "--:--".to_string()
                },
                duration_seconds: meta.duration_seconds as i32,
                custom_thumb_url: format!("https://i.ytimg.com/vi/{}/hqdefault.jpg", vid),
                item_type: "video".to_string(),
                ..Default::default()
            };
            return SearchResultPage {
                items: vec![item],
                continuation_token: String::new(),
                query: q.to_string(),
            };
        }
    }

    let effective_query = match filter_type {
        "albums" => format!("{} full album", q),
        "songs" => format!("{} official audio", q),
        _ => q.to_string(),
    };

    let payload = serde_json::json!({
        "context": {
            "client": {
                "clientName": "WEB",
                "clientVersion": WEB_CLIENT_VERSION,
                "hl": "pt",
                "gl": "BR"
            }
        },
        "query": effective_query
    })
    .to_string();

    let (headers, has_auth) = GoogleSessionService::shared().build_web_headers(config);
    let mut page = SearchResultPage {
        query: q.to_string(),
        ..Default::default()
    };

    if let Some(json) = http_post_json(
        SEARCH_ENDPOINT,
        &payload,
        Some("https://www.youtube.com/"),
        &headers,
    ) {
        let parsed = parse_video_and_channel_feed_json(&json, has_auth, true);
        page.items = parsed.items;
        page.continuation_token = parsed.continuation_token;
    }

    page
}

pub fn fetch_search_continuation(
    continuation_token: &str,
    config: &AppConfig,
) -> SearchResultPage {
    let tok = continuation_token.trim();
    if tok.is_empty() {
        return SearchResultPage::default();
    }
    let payload = serde_json::json!({
        "context": {
            "client": {
                "clientName": "WEB",
                "clientVersion": WEB_CLIENT_VERSION,
                "hl": "pt",
                "gl": "BR"
            }
        },
        "continuation": tok
    })
    .to_string();

    let (headers, has_auth) = GoogleSessionService::shared().build_web_headers(config);
    if let Some(json) = http_post_json(
        SEARCH_ENDPOINT,
        &payload,
        Some("https://www.youtube.com/"),
        &headers,
    ) {
        let parsed = parse_video_and_channel_feed_json(&json, has_auth, false);
        return SearchResultPage {
            items: parsed.items,
            continuation_token: parsed.continuation_token,
            query: String::new(),
        };
    }
    SearchResultPage::default()
}

pub fn fetch_watch_next_details(video_id: &str, config: &AppConfig) -> WatchNextDetails {
    let vid = video_id.trim();
    if vid.len() != 11 {
        return WatchNextDetails::default();
    }
    let payload = serde_json::json!({
        "context": {
            "client": {
                "clientName": "WEB",
                "clientVersion": WEB_CLIENT_VERSION,
                "hl": "pt",
                "gl": "BR"
            }
        },
        "videoId": vid
    })
    .to_string();

    let (headers, has_auth) = GoogleSessionService::shared().build_web_headers(config);
    let mut details = WatchNextDetails::default();

    if let Some(json) = http_post_json(
        NEXT_ENDPOINT,
        &payload,
        Some(&format!("https://www.youtube.com/watch?v={}", vid)),
        &headers,
    ) {
        let parsed = parse_video_feed_json(&json, has_auth);
        let mut seen = HashSet::new();
        seen.insert(vid.to_ascii_lowercase());
        for item in parsed.items {
            if !item.is_channel()
                && !item.is_playlist_card()
                && !item.is_short()
                && seen.insert(item.id.to_ascii_lowercase())
            {
                details.related_videos.push(item);
                if details.related_videos.len() >= 32 {
                    break;
                }
            }
        }

        if let Ok(root) = serde_json::from_str::<Value>(&json) {
            details.subscriber_count_text =
                find_first_text_by_key(&root, "subscriberCountText");
        }
    }

    details
}

pub fn fetch_channel_page(
    channel_id: &str,
    channel_name: &str,
    tab: &str,
    config: &AppConfig,
) -> ChannelPageData {
    let active_tab = if tab.trim().is_empty() { "home" } else { tab.trim() };
    let mut data = ChannelPageData {
        channel_id: channel_id.trim().to_string(),
        channel_name: if channel_name.trim().is_empty() {
            "Canal do YouTube".to_string()
        } else {
            channel_name.trim().to_string()
        },
        active_tab: active_tab.to_string(),
        ..Default::default()
    };

    let (headers, has_auth) = GoogleSessionService::shared().build_web_headers(config);

    // Resolve UC... ID se estiver vazio
    if !data.channel_id.starts_with("UC") && !data.channel_name.is_empty() {
        let search_payload = serde_json::json!({
            "context": {
                "client": {
                    "clientName": "WEB",
                    "clientVersion": WEB_CLIENT_VERSION,
                    "hl": "pt",
                    "gl": "BR"
                }
            },
            "query": data.channel_name,
            "params": "EgIQAg=="
        })
        .to_string();
        if let Some(sjson) = http_post_json(
            SEARCH_ENDPOINT,
            &search_payload,
            Some("https://www.youtube.com/"),
            &headers,
        ) {
            if let Ok(rx) = Regex::new(r#""channelId"\s*:\s*"(UC[a-zA-Z0-9_-]+)""#) {
                if let Some(c) = rx.captures(&sjson).and_then(|c| c.get(1)) {
                    data.channel_id = c.as_str().to_string();
                }
            }
        }
    }

    if data.channel_id.starts_with("UC") && data.channel_id.len() > 4 {
        let uploads_id = format!("VLUU{}", &data.channel_id[2..]);

        if active_tab == "playlists" {
            let payload = serde_json::json!({
                "context": {
                    "client": {
                        "clientName": "WEB",
                        "clientVersion": WEB_CLIENT_VERSION,
                        "hl": "pt",
                        "gl": "BR"
                    }
                },
                "browseId": data.channel_id,
                "params": "EglwbGF5bGlzdHPyBgQKAkIA"
            })
            .to_string();
            if let Some(json) = http_post_json(
                BROWSE_ENDPOINT,
                &payload,
                Some(&format!("https://www.youtube.com/channel/{}", data.channel_id)),
                &headers,
            ) {
                extract_channel_header_info(&json, &mut data);
                let pl_page = parse_playlists_json(&json, has_auth);
                data.items = pl_page.items;
                data.continuation_token = pl_page.continuation_token;
            }
        } else {
            // "home" ou "videos": carrega cabeçalho do canal + playlist oficial de envios VLUU...
            let home_payload = build_browse_payload(Some(&data.channel_id), None);
            if let Some(hjson) = http_post_json(
                BROWSE_ENDPOINT,
                &home_payload,
                Some(&format!("https://www.youtube.com/channel/{}", data.channel_id)),
                &headers,
            ) {
                extract_channel_header_info(&hjson, &mut data);
                if active_tab == "home" {
                    let parsed = parse_video_feed_json(&hjson, has_auth);
                    data.items = parsed.items;
                    data.continuation_token = parsed.continuation_token;
                }
            }

            if data.items.len() < 5 {
                let uu_payload = build_browse_payload(Some(&uploads_id), None);
                if let Some(ujson) = http_post_json(
                    BROWSE_ENDPOINT,
                    &uu_payload,
                    Some(&format!("https://www.youtube.com/channel/{}", data.channel_id)),
                    &headers,
                ) {
                    let parsed = parse_video_feed_json(&ujson, has_auth);
                    let mut seen: HashSet<String> =
                        data.items.iter().map(|x| x.id.to_ascii_lowercase()).collect();
                    for mut v in parsed.items {
                        if v.artist.is_empty() || v.artist == "YouTube" {
                            v.artist = data.channel_name.clone();
                        }
                        v.channel_id = data.channel_id.clone();
                        if !data.avatar_url.is_empty() && v.channel_avatar_url.is_empty() {
                            v.channel_avatar_url = data.avatar_url.clone();
                        }
                        if seen.insert(v.id.to_ascii_lowercase()) {
                            data.items.push(v);
                        }
                    }
                    if data.continuation_token.is_empty() {
                        data.continuation_token = parsed.continuation_token;
                    }
                }
            }
        }
    }

    if data.avatar_url.is_empty() {
        data.avatar_url = get_cached_channel_avatar_url(&data.channel_id, &data.channel_name);
    }
    data
}

fn extract_channel_header_info(json: &str, data: &mut ChannelPageData) {
    let Ok(root) = serde_json::from_str::<Value>(json) else {
        return;
    };
    if let Some(meta) = find_property_recursive(&root, "channelMetadataRenderer") {
        let title = read_str(meta, "title");
        if !title.is_empty() {
            data.channel_name = title;
        }
        let av = extract_best_thumbnail_url(meta);
        if !av.is_empty() {
            data.avatar_url = normalize_avatar_url(&av);
            cache_channel_avatar_url(&data.channel_id, &data.channel_name, &data.avatar_url);
        }
    }
    let sub = find_first_text_by_key(&root, "subscriberCountText");
    if !sub.is_empty() {
        data.subscriber_text = sub;
    }
}

// ============================================================================
// Parsers JSON InnerTube (Clássicos + 2025/2026 lockupViewModel + WEB_REMIX)
// ============================================================================

pub fn parse_video_feed_json(json: &str, sent_auth_cookies: bool) -> AccountFeedPage {
    parse_video_and_channel_feed_json(json, sent_auth_cookies, false)
}

pub fn parse_video_and_channel_feed_json(
    json: &str,
    sent_auth_cookies: bool,
    include_channels_and_playlists: bool,
) -> AccountFeedPage {
    let mut page = AccountFeedPage::default();
    let Ok(root) = serde_json::from_str::<Value>(json) else {
        return page;
    };

    if let Some(logged_in) = detect_logged_in_state(&root) {
        page.used_account_auth = logged_in;
        page.session_expired = sent_auth_cookies && !logged_in;
    } else {
        page.used_account_auth = sent_auth_cookies;
    }

    let mut seen = HashSet::new();
    let mut continuation = String::new();
    walk_for_videos(
        &root,
        &mut page.items,
        &mut seen,
        &mut continuation,
        include_channels_and_playlists,
    );
    page.continuation_token = continuation;
    page
}

pub fn parse_playlists_json(json: &str, sent_auth_cookies: bool) -> AccountFeedPage {
    let mut page = AccountFeedPage::default();
    let Ok(root) = serde_json::from_str::<Value>(json) else {
        return page;
    };

    if let Some(logged_in) = detect_logged_in_state(&root) {
        page.used_account_auth = logged_in;
        page.session_expired = sent_auth_cookies && !logged_in;
    } else {
        page.used_account_auth = sent_auth_cookies;
    }

    let mut seen = HashSet::new();
    let mut continuation = String::new();
    walk_for_playlists(&root, &mut page.items, &mut seen, &mut continuation);
    page.continuation_token = continuation;
    page
}

pub fn parse_music_playlists_json(json: &str, sent_auth_cookies: bool) -> AccountFeedPage {
    let mut page = AccountFeedPage::default();
    let Ok(root) = serde_json::from_str::<Value>(json) else {
        return page;
    };

    if let Some(logged_in) = detect_logged_in_state(&root) {
        page.used_account_auth = logged_in;
        page.session_expired = sent_auth_cookies && !logged_in;
    } else {
        page.used_account_auth = sent_auth_cookies;
    }

    let mut seen = HashSet::new();
    let mut continuation = String::new();
    walk_for_music_playlists(&root, &mut page.items, &mut seen, &mut continuation);
    page.continuation_token = continuation;
    page
}

pub fn parse_web_playlist_items_json(json: &str, sent_auth_cookies: bool) -> AccountFeedPage {
    let mut page = AccountFeedPage::default();
    let Ok(root) = serde_json::from_str::<Value>(json) else {
        return page;
    };

    if let Some(logged_in) = detect_logged_in_state(&root) {
        page.used_account_auth = logged_in;
        page.session_expired = sent_auth_cookies && !logged_in;
    } else {
        page.used_account_auth = sent_auth_cookies;
    }

    let mut seen = HashSet::new();
    let mut continuation = String::new();
    if let Some(pl_list) = find_property_recursive(&root, "playlistVideoListRenderer") {
        walk_for_videos(pl_list, &mut page.items, &mut seen, &mut continuation, false);
    } else if let Some(cont_el) = find_property_recursive(&root, "appendContinuationItemsAction") {
        walk_for_videos(cont_el, &mut page.items, &mut seen, &mut continuation, false);
    } else {
        walk_for_videos(&root, &mut page.items, &mut seen, &mut continuation, false);
    }
    page.continuation_token = continuation;
    page
}

pub fn parse_music_playlist_items_json(json: &str, sent_auth_cookies: bool) -> AccountFeedPage {
    let mut page = AccountFeedPage::default();
    let Ok(root) = serde_json::from_str::<Value>(json) else {
        return page;
    };

    if let Some(logged_in) = detect_logged_in_state(&root) {
        page.used_account_auth = logged_in;
        page.session_expired = sent_auth_cookies && !logged_in;
    } else {
        page.used_account_auth = sent_auth_cookies;
    }

    let mut seen = HashSet::new();
    let mut continuation = String::new();
    if let Some(shelf) = find_property_recursive(&root, "musicPlaylistShelfRenderer") {
        walk_for_music_playlist_tracks(shelf, &mut page.items, &mut seen, &mut continuation);
    } else if let Some(cont) = find_property_recursive(&root, "musicPlaylistShelfContinuation") {
        walk_for_music_playlist_tracks(cont, &mut page.items, &mut seen, &mut continuation);
    } else if let Some(app) = find_property_recursive(&root, "appendContinuationItemsAction") {
        walk_for_music_playlist_tracks(app, &mut page.items, &mut seen, &mut continuation);
    }
    page.continuation_token = continuation;
    page
}

fn walk_for_videos(
    el: &Value,
    items: &mut Vec<TrackItem>,
    seen: &mut HashSet<String>,
    continuation: &mut String,
    include_channels_and_playlists: bool,
) {
    match el {
        Value::Object(map) => {
            for (name, val) in map {
                if name == "reelShelfRenderer"
                    || name == "shortsLockupViewModel"
                    || name == "reelItemRenderer"
                {
                    continue;
                }
                if name == "videoRenderer"
                    || name == "gridVideoRenderer"
                    || name == "playlistVideoRenderer"
                    || name == "compactVideoRenderer"
                {
                    if let Some(item) = parse_classic_video_renderer(val) {
                        if seen.insert(item.id.to_ascii_lowercase()) {
                            items.push(item);
                        }
                    }
                    continue;
                }
                if name == "channelRenderer" && include_channels_and_playlists {
                    if let Some(ch) = parse_channel_renderer(val) {
                        if seen.insert(ch.id.to_ascii_lowercase()) {
                            items.push(ch);
                        }
                    }
                    continue;
                }
                if (name == "playlistRenderer" || name == "gridPlaylistRenderer")
                    && include_channels_and_playlists
                {
                    if let Some(pl) = parse_classic_playlist_renderer(val) {
                        if seen.insert(pl.playlist_id.to_ascii_lowercase()) {
                            items.push(pl);
                        }
                    }
                    continue;
                }
                if name == "lockupViewModel" {
                    if let Some(item) = parse_video_lockup_view_model(val) {
                        if seen.insert(item.id.to_ascii_lowercase()) {
                            items.push(item);
                        }
                    } else if include_channels_and_playlists {
                        if let Some(pl) = parse_playlist_lockup_view_model(val) {
                            if seen.insert(pl.playlist_id.to_ascii_lowercase()) {
                                items.push(pl);
                            }
                        }
                    }
                    continue;
                }
                if name == "continuationItemRenderer" && continuation.is_empty() {
                    let tok = extract_continuation_from_item(val);
                    if !tok.is_empty() {
                        *continuation = tok;
                    }
                    continue;
                }
                walk_for_videos(
                    val,
                    items,
                    seen,
                    continuation,
                    include_channels_and_playlists,
                );
            }
        }
        Value::Array(arr) => {
            for child in arr {
                walk_for_videos(
                    child,
                    items,
                    seen,
                    continuation,
                    include_channels_and_playlists,
                );
            }
        }
        _ => {}
    }
}

fn walk_for_playlists(
    el: &Value,
    items: &mut Vec<TrackItem>,
    seen: &mut HashSet<String>,
    continuation: &mut String,
) {
    match el {
        Value::Object(map) => {
            for (name, val) in map {
                if name == "playlistRenderer" || name == "gridPlaylistRenderer" {
                    if let Some(pl) = parse_classic_playlist_renderer(val) {
                        if !is_excluded_playlist(&pl.playlist_id, &pl.title)
                            && seen.insert(pl.playlist_id.to_ascii_lowercase())
                        {
                            items.push(pl);
                        }
                    }
                    continue;
                }
                if name == "lockupViewModel" {
                    if let Some(pl) = parse_playlist_lockup_view_model(val) {
                        if !is_excluded_playlist(&pl.playlist_id, &pl.title)
                            && seen.insert(pl.playlist_id.to_ascii_lowercase())
                        {
                            items.push(pl);
                        }
                    }
                    continue;
                }
                if name == "continuationItemRenderer" && continuation.is_empty() {
                    let tok = extract_continuation_from_item(val);
                    if !tok.is_empty() {
                        *continuation = tok;
                    }
                    continue;
                }
                walk_for_playlists(val, items, seen, continuation);
            }
        }
        Value::Array(arr) => {
            for child in arr {
                walk_for_playlists(child, items, seen, continuation);
            }
        }
        _ => {}
    }
}

fn walk_for_music_playlists(
    el: &Value,
    items: &mut Vec<TrackItem>,
    seen: &mut HashSet<String>,
    continuation: &mut String,
) {
    match el {
        Value::Object(map) => {
            for (name, val) in map {
                if name == "musicTwoRowItemRenderer" {
                    if let Some(pl) = parse_music_two_row_playlist(val) {
                        if !is_excluded_playlist(&pl.playlist_id, &pl.title)
                            && seen.insert(pl.playlist_id.to_ascii_lowercase())
                        {
                            items.push(pl);
                        }
                    }
                    continue;
                }
                if name == "playlistRenderer" || name == "gridPlaylistRenderer" {
                    if let Some(pl) = parse_classic_playlist_renderer(val) {
                        if !is_excluded_playlist(&pl.playlist_id, &pl.title)
                            && seen.insert(pl.playlist_id.to_ascii_lowercase())
                        {
                            items.push(pl);
                        }
                    }
                    continue;
                }
                if (name == "continuationItemRenderer" || name == "nextContinuationData")
                    && continuation.is_empty()
                {
                    let tok = if name == "nextContinuationData" {
                        read_str(val, "continuation")
                    } else {
                        extract_continuation_from_item(val)
                    };
                    if !tok.is_empty() {
                        *continuation = tok;
                    }
                    continue;
                }
                walk_for_music_playlists(val, items, seen, continuation);
            }
        }
        Value::Array(arr) => {
            for child in arr {
                walk_for_music_playlists(child, items, seen, continuation);
            }
        }
        _ => {}
    }
}

fn walk_for_music_playlist_tracks(
    el: &Value,
    items: &mut Vec<TrackItem>,
    seen: &mut HashSet<String>,
    continuation: &mut String,
) {
    match el {
        Value::Object(map) => {
            for (name, val) in map {
                if name == "musicShelfRenderer" || name == "musicCarouselShelfRenderer" {
                    continue;
                }
                if name == "musicResponsiveListItemRenderer" {
                    if let Some(track) = parse_music_responsive_track(val) {
                        if seen.insert(track.id.to_ascii_lowercase()) {
                            items.push(track);
                        }
                    }
                    continue;
                }
                if (name == "continuationItemRenderer" || name == "nextContinuationData")
                    && continuation.is_empty()
                {
                    let tok = if name == "nextContinuationData" {
                        read_str(val, "continuation")
                    } else {
                        extract_continuation_from_item(val)
                    };
                    if !tok.is_empty() {
                        *continuation = tok;
                    }
                    continue;
                }
                walk_for_music_playlist_tracks(val, items, seen, continuation);
            }
        }
        Value::Array(arr) => {
            for child in arr {
                walk_for_music_playlist_tracks(child, items, seen, continuation);
            }
        }
        _ => {}
    }
}

fn parse_channel_renderer(obj: &Value) -> Option<TrackItem> {
    let cid = read_str(obj, "channelId");
    if !cid.starts_with("UC") {
        return None;
    }
    let title = read_text(obj, "title");
    if title.is_empty() {
        return None;
    }
    let mut sub = read_text(obj, "videoCountText");
    if sub.is_empty() {
        sub = read_text(obj, "subscriberCountText");
    }
    let av = normalize_avatar_url(&extract_best_thumbnail_url(obj));
    if !av.is_empty() {
        cache_channel_avatar_url(&cid, &title, &av);
    }
    Some(TrackItem {
        id: format!("CH_{}", cid),
        channel_id: cid,
        title,
        artist: if sub.is_empty() {
            "Canal do YouTube".to_string()
        } else {
            sub
        },
        duration: "CANAL".to_string(),
        item_type: "channel".to_string(),
        channel_avatar_url: av.clone(),
        custom_thumb_url: av,
        ..Default::default()
    })
}

fn parse_classic_video_renderer(obj: &Value) -> Option<TrackItem> {
    let vid_id = read_str(obj, "videoId");
    if vid_id.len() != 11 {
        return None;
    }
    let title = read_text(obj, "title");
    if title.is_empty()
        || title == "[Private video]"
        || title == "[Deleted video]"
        || title == "[Vídeo privado]"
        || title == "[Vídeo excluído]"
    {
        return None;
    }

    let mut artist = read_text(obj, "ownerText");
    if artist.is_empty() {
        artist = read_text(obj, "longBylineText");
    }
    if artist.is_empty() {
        artist = read_text(obj, "shortBylineText");
    }
    if artist.is_empty() {
        artist = "YouTube".to_string();
    }

    let mut channel_id = extract_browse_id_from_owner(obj, "ownerText");
    if channel_id.is_empty() {
        channel_id = extract_browse_id_from_owner(obj, "longBylineText");
    }
    if channel_id.is_empty() {
        channel_id = extract_browse_id_from_owner(obj, "shortBylineText");
    }

    let mut dur_text = read_text(obj, "lengthText");
    let len_raw = read_str(obj, "lengthSeconds");
    let dur_sec = if let Ok(s) = len_raw.parse::<i32>() {
        s
    } else {
        parse_duration_to_seconds(&dur_text)
    };
    if dur_text.is_empty() && dur_sec > 0 {
        dur_text = format_seconds(dur_sec as f64);
    }
    let is_live = dur_text.is_empty() && dur_sec <= 0;
    if is_live && dur_text.is_empty() {
        dur_text = "AO VIVO".to_string();
    }

    let mut views_text = read_text(obj, "shortViewCountText");
    if views_text.is_empty() {
        views_text = read_text(obj, "viewCountText");
    }
    let published_text = read_text(obj, "publishedTimeText");

    let mut avatar_url = extract_classic_channel_avatar(obj);
    if !avatar_url.is_empty() {
        cache_channel_avatar_url(&channel_id, &artist, &avatar_url);
    } else {
        avatar_url = get_cached_channel_avatar_url(&channel_id, &artist);
    }

    let is_short = !is_live
        && dur_sec > 0
        && dur_sec <= 60
        && title.to_ascii_lowercase().contains("#shorts");

    Some(TrackItem {
        id: vid_id.clone(),
        title,
        artist,
        channel_id,
        channel_avatar_url: avatar_url,
        duration: if dur_text.is_empty() {
            "--:--".to_string()
        } else {
            dur_text
        },
        duration_seconds: dur_sec,
        view_count_text: views_text,
        published_time_text: published_text,
        custom_thumb_url: format!("https://i.ytimg.com/vi/{}/hqdefault.jpg", vid_id),
        item_type: if is_short {
            "short".to_string()
        } else {
            "video".to_string()
        },
        ..Default::default()
    })
}

fn parse_video_lockup_view_model(obj: &Value) -> Option<TrackItem> {
    let ctype = read_str(obj, "contentType");
    if !ctype.is_empty() && !ctype.eq_ignore_ascii_case("LOCKUP_CONTENT_TYPE_VIDEO") {
        return None;
    }
    let vid_id = read_str(obj, "contentId");
    if vid_id.len() != 11 {
        return None;
    }

    let mut title = String::new();
    let mut artist = String::new();
    let mut channel_id = String::new();
    let mut avatar_url = String::new();
    let mut views_text = String::new();
    let mut published_text = String::new();

    if let Some(meta_vm) = obj.get("metadata").and_then(|m| m.get("lockupMetadataViewModel")) {
        title = read_text(meta_vm, "title");
        if let Some(dec_av) = meta_vm.get("image").and_then(|i| i.get("decoratedAvatarViewModel")) {
            avatar_url = extract_nested_image_url(dec_av);
            channel_id = extract_first_channel_browse_id(dec_av);
        }
        if let Some(rows) = meta_vm
            .get("metadata")
            .and_then(|m| m.get("contentMetadataViewModel"))
            .and_then(|c| c.get("metadataRows"))
            .and_then(|r| r.as_array())
        {
            for (r_idx, row) in rows.iter().enumerate() {
                let Some(parts) = row.get("metadataParts").and_then(|p| p.as_array()) else {
                    continue;
                };
                for part in parts {
                    let txt = read_text(part, "text");
                    if txt.trim().is_empty() {
                        continue;
                    }
                    if r_idx == 0 && artist.is_empty() {
                        artist = txt;
                        if channel_id.is_empty() {
                            channel_id = extract_first_channel_browse_id(part);
                        }
                    } else if looks_like_views(&txt) && views_text.is_empty() {
                        views_text = txt;
                    } else if looks_like_date(&txt) && published_text.is_empty() {
                        published_text = txt;
                    } else if artist.is_empty() {
                        artist = txt;
                    }
                }
            }
        }
    }

    if title.trim().is_empty() {
        return None;
    }
    if artist.is_empty() {
        artist = "YouTube".to_string();
    }

    let dur_text = extract_lockup_badge_text(obj);
    let dur_sec = parse_duration_to_seconds(&dur_text);
    let is_live = dur_text.to_ascii_uppercase().contains("AO VIVO")
        || dur_text.to_ascii_uppercase().contains("LIVE");

    if !avatar_url.is_empty() {
        avatar_url = normalize_avatar_url(&avatar_url);
        if !avatar_url.is_empty() {
            cache_channel_avatar_url(&channel_id, &artist, &avatar_url);
        }
    } else {
        avatar_url = get_cached_channel_avatar_url(&channel_id, &artist);
    }

    let is_short = !is_live
        && dur_sec > 0
        && dur_sec <= 60
        && title.to_ascii_lowercase().contains("#shorts");

    Some(TrackItem {
        id: vid_id.clone(),
        title,
        artist,
        channel_id,
        channel_avatar_url: avatar_url,
        duration: if dur_text.is_empty() {
            if is_live {
                "AO VIVO".to_string()
            } else {
                "--:--".to_string()
            }
        } else {
            dur_text
        },
        duration_seconds: dur_sec,
        view_count_text: views_text,
        published_time_text: published_text,
        custom_thumb_url: format!("https://i.ytimg.com/vi/{}/hqdefault.jpg", vid_id),
        item_type: if is_short {
            "short".to_string()
        } else {
            "video".to_string()
        },
        ..Default::default()
    })
}

fn parse_classic_playlist_renderer(obj: &Value) -> Option<TrackItem> {
    let pl_id = read_str(obj, "playlistId");
    if pl_id.trim().is_empty() {
        return None;
    }
    let title = read_text(obj, "title");
    if title.trim().is_empty() {
        return None;
    }
    let mut count_text = read_text(obj, "videoCountShortText");
    if count_text.is_empty() {
        count_text = read_text(obj, "videoCountText");
    }
    if count_text.is_empty() {
        count_text = read_str(obj, "videoCount");
    }
    let mut owner = read_text(obj, "shortBylineText");
    if owner.is_empty() {
        owner = read_text(obj, "longBylineText");
    }

    let mut thumb_url = extract_best_thumbnail_url(obj);
    let cover_vid = extract_video_id_from_thumb_url(&thumb_url);
    if !cover_vid.is_empty() {
        thumb_url = format!("https://i.ytimg.com/vi/{}/hqdefault.jpg", cover_vid);
    }

    let mut subtitle = if owner.is_empty() {
        "Playlist".to_string()
    } else {
        owner
    };
    if !count_text.is_empty() {
        let formatted = if count_text.chars().all(|c| c.is_ascii_digit()) {
            format!("{} vídeos", count_text)
        } else {
            count_text
        };
        subtitle = format!("{} • {}", subtitle, formatted);
    }

    Some(TrackItem {
        id: if !cover_vid.is_empty() {
            cover_vid
        } else {
            format!("PL_{}", pl_id)
        },
        playlist_id: pl_id,
        title,
        artist: subtitle,
        duration: "PLAYLIST".to_string(),
        item_type: "playlist".to_string(),
        custom_thumb_url: thumb_url,
        ..Default::default()
    })
}

fn parse_playlist_lockup_view_model(obj: &Value) -> Option<TrackItem> {
    let ctype = read_str(obj, "contentType");
    let cid = read_str(obj, "contentId");
    let is_pl = ctype.eq_ignore_ascii_case("LOCKUP_CONTENT_TYPE_PLAYLIST")
        || ctype.eq_ignore_ascii_case("LOCKUP_CONTENT_TYPE_PODCAST");

    let mut pl_id = if is_pl { cid.clone() } else { String::new() };
    if pl_id.is_empty() && (cid.starts_with("PL") || cid.starts_with("OL") || cid.starts_with("UU")) {
        pl_id = cid;
    }
    if pl_id.is_empty() {
        pl_id = extract_playlist_id_from_context(obj);
    }
    if pl_id.trim().is_empty() {
        return None;
    }

    let mut title = String::new();
    let mut sub = String::new();
    if let Some(meta_vm) = obj.get("metadata").and_then(|m| m.get("lockupMetadataViewModel")) {
        title = read_text(meta_vm, "title");
        if let Some(rows) = meta_vm
            .get("metadata")
            .and_then(|m| m.get("contentMetadataViewModel"))
            .and_then(|c| c.get("metadataRows"))
            .and_then(|r| r.as_array())
        {
            for row in rows {
                let Some(parts) = row.get("metadataParts").and_then(|p| p.as_array()) else {
                    continue;
                };
                for part in parts {
                    let t = read_text(part, "text");
                    if t.trim().is_empty()
                        || t.to_ascii_lowercase().contains("ver playlist")
                        || t.to_ascii_lowercase().contains("view full playlist")
                    {
                        continue;
                    }
                    if sub.is_empty() {
                        sub = t;
                    }
                }
            }
        }
    }

    if title.trim().is_empty() {
        return None;
    }
    let badge = extract_lockup_badge_text(obj);
    let mut thumb_url = extract_nested_image_url(obj);
    let cover_vid = extract_video_id_from_thumb_url(&thumb_url);
    if !cover_vid.is_empty() {
        thumb_url = format!("https://i.ytimg.com/vi/{}/hqdefault.jpg", cover_vid);
    }

    let mut artist = if sub.is_empty() {
        "Playlist".to_string()
    } else {
        sub
    };
    if !badge.is_empty() && !artist.to_ascii_lowercase().contains(&badge.to_ascii_lowercase()) {
        artist = format!("{} • {}", artist, badge);
    }

    Some(TrackItem {
        id: if !cover_vid.is_empty() {
            cover_vid
        } else {
            format!("PL_{}", pl_id)
        },
        playlist_id: pl_id,
        title,
        artist,
        duration: "PLAYLIST".to_string(),
        item_type: "playlist".to_string(),
        custom_thumb_url: thumb_url,
        ..Default::default()
    })
}

fn parse_music_two_row_playlist(obj: &Value) -> Option<TrackItem> {
    let item = parse_music_two_row_track_or_playlist(obj)?;
    if item.is_playlist_card() {
        Some(item)
    } else {
        None
    }
}

fn parse_music_two_row_track_or_playlist(obj: &Value) -> Option<TrackItem> {
    let title = read_text(obj, "title");
    if title.trim().is_empty() {
        return None;
    }
    let subtitle = read_text(obj, "subtitle");
    let thumb_url = extract_best_thumbnail_url(obj);

    // Check if it has a direct watchEndpoint videoId
    if let Some(vid) = obj
        .get("navigationEndpoint")
        .and_then(|n| n.get("watchEndpoint"))
        .and_then(|w| w.get("videoId"))
        .and_then(|v| v.as_str())
    {
        if vid.len() == 11 {
            return Some(TrackItem {
                id: vid.to_string(),
                title,
                artist: if subtitle.is_empty() {
                    "YouTube Music".to_string()
                } else {
                    subtitle
                },
                duration: "--:--".to_string(),
                custom_thumb_url: if thumb_url.is_empty() {
                    format!("https://i.ytimg.com/vi/{}/hqdefault.jpg", vid)
                } else {
                    thumb_url
                },
                item_type: "video".to_string(),
                ..Default::default()
            });
        }
    }

    let mut pl_id = String::new();
    if let Some(nav) = obj.get("navigationEndpoint") {
        if let Some(be) = nav.get("browseEndpoint") {
            let bid = read_str(be, "browseId");
            if bid.len() > 2 && bid[..2].eq_ignore_ascii_case("VL") {
                pl_id = bid[2..].to_string();
            } else if bid.starts_with("PL") || bid.starts_with("OL") {
                pl_id = bid;
            }
        }
        if pl_id.is_empty() {
            if let Some(wpe) = nav.get("watchPlaylistEndpoint") {
                pl_id = read_str(wpe, "playlistId");
            }
        }
    }
    if pl_id.is_empty() {
        pl_id = extract_playlist_id_from_context(obj);
    }
    if pl_id.trim().is_empty() {
        return None;
    }

    let cover_vid = extract_video_id_from_thumb_url(&thumb_url);
    Some(TrackItem {
        id: if !cover_vid.is_empty() {
            cover_vid
        } else {
            format!("PL_{}", pl_id)
        },
        playlist_id: pl_id,
        title,
        artist: if subtitle.is_empty() {
            "Playlist • YT Music".to_string()
        } else {
            subtitle
        },
        duration: "PLAYLIST".to_string(),
        item_type: "playlist".to_string(),
        custom_thumb_url: thumb_url,
        ..Default::default()
    })
}

fn parse_music_responsive_track(obj: &Value) -> Option<TrackItem> {
    let mut vid_id = obj
        .get("playlistItemData")
        .map(|p| read_str(p, "videoId"))
        .unwrap_or_default();
    if vid_id.is_empty() {
        vid_id = extract_first_video_id_recursive(obj);
    }
    if vid_id.len() != 11 {
        return None;
    }

    let mut title = String::new();
    let mut artist = String::new();
    if let Some(cols) = obj.get("flexColumns").and_then(|c| c.as_array()) {
        for (idx, col) in cols.iter().enumerate() {
            if let Some(fcr) = col.get("musicResponsiveListItemFlexColumnRenderer") {
                let text = read_text(fcr, "text");
                if idx == 0 && !text.trim().is_empty() {
                    title = text;
                } else if idx == 1 && !text.trim().is_empty() {
                    artist = text;
                }
            }
        }
    }
    if title.trim().is_empty() {
        return None;
    }
    if artist.trim().is_empty() {
        artist = "YouTube Music".to_string();
    }

    let mut dur_text = String::new();
    if let Some(fcols) = obj.get("fixedColumns").and_then(|c| c.as_array()) {
        for col in fcols {
            if let Some(fxr) = col.get("musicResponsiveListItemFixedColumnRenderer") {
                let text = read_text(fxr, "text");
                if text.contains(':') {
                    dur_text = text.trim().to_string();
                    break;
                }
            }
        }
    }

    let dur_sec = parse_duration_to_seconds(&dur_text);
    let mut thumb_url = extract_best_thumbnail_url(obj);
    if thumb_url.trim().is_empty() {
        thumb_url = format!("https://i.ytimg.com/vi/{}/mqdefault.jpg", vid_id);
    }

    Some(TrackItem {
        id: vid_id,
        title,
        artist,
        duration: if dur_text.is_empty() {
            "--:--".to_string()
        } else {
            dur_text
        },
        duration_seconds: dur_sec,
        custom_thumb_url: thumb_url,
        item_type: "video".to_string(),
        ..Default::default()
    })
}

pub fn is_excluded_playlist(playlist_id: &str, title: &str) -> bool {
    let mut id = playlist_id.trim();
    if id.len() > 2 && id[..2].eq_ignore_ascii_case("VL") {
        id = &id[2..];
    }
    if id.is_empty() {
        return true;
    }
    let excluded = ["WL", "LL", "LM", "SE", "SS", "VLLM", "VLWL", "VLLL", "VLSE"];
    if excluded.iter().any(|x| x.eq_ignore_ascii_case(id)) {
        return true;
    }
    if id.len() >= 2 && id[..2].eq_ignore_ascii_case("RD") {
        return true;
    }
    let t = title.trim().to_ascii_lowercase();
    if !t.is_empty() {
        if t == "assistir mais tarde"
            || t == "watch later"
            || t.contains("vídeos marcados com")
            || t.contains("músicas marcadas com")
            || t.contains("episódios para mais tarde")
            || t == "liked videos"
            || t == "liked music"
            || t == "episodes for later"
            || t == "nova playlist"
            || t == "new playlist"
        {
            return true;
        }
    }
    false
}

// ============================================================================
// Resolução de Streams (Fast-Path ANDROID_VR + Circuit Breaker + yt-dlp)
// ============================================================================

#[derive(Debug, Clone)]
pub struct ResolvedStream {
    pub video_url: String,
    pub audio_url: String,
    pub codec_label: String,
    pub is_live: bool,
    pub quality_height: i32,
}

static STREAM_CACHE: OnceLock<Mutex<HashMap<String, (ResolvedStream, Instant)>>> = OnceLock::new();
static VISITOR_DATA_CACHE: OnceLock<Mutex<Option<(String, Instant)>>> = OnceLock::new();
static FASTPATH_PAUSED_UNTIL: OnceLock<Mutex<Option<Instant>>> = OnceLock::new();

pub fn resolve_video_streams(
    video_id: &str,
    target_height: i32,
    config: &AppConfig,
) -> Result<ResolvedStream, String> {
    let vid = video_id.trim();
    if vid.is_empty() {
        return Err("ID de vídeo vazio.".to_string());
    }
    let q = if target_height < 144 { 720 } else { target_height };
    let cache_key = format!("{}:{}", vid, q);
    let cache = STREAM_CACHE.get_or_init(|| Mutex::new(HashMap::new()));

    if let Ok(guard) = cache.lock() {
        if let Some((stream, ts)) = guard.get(&cache_key) {
            if ts.elapsed() < Duration::from_secs(12 * 60) {
                log_info("STREAM", &format!("Stream em cache RAM reutilizado para {} ({}p)", vid, q));
                return Ok(stream.clone());
            }
        }
    }

    // 1. Fast-Path InnerTube ANDROID_VR (~200-400ms) com Disjuntor Anti-Bot
    if let Some(fast) = try_resolve_via_inner_tube_fast_path(vid, q) {
        if let Ok(mut guard) = cache.lock() {
            guard.insert(cache_key, (fast.clone(), Instant::now()));
        }
        return Ok(fast);
    }

    // 2. Fallback Oficial yt-dlp + deno
    let resolved = resolve_via_yt_dlp(vid, q, config)?;
    if let Ok(mut guard) = cache.lock() {
        if guard.len() > 48 {
            guard.clear();
        }
        guard.insert(cache_key, (resolved.clone(), Instant::now()));
    }
    Ok(resolved)
}

pub fn resolve_audio_only_stream(video_id: &str, config: &AppConfig) -> Result<String, String> {
    let vid = video_id.trim();
    if vid.is_empty() {
        return Err("ID de áudio vazio.".to_string());
    }
    let cache_key = format!("{}:audio", vid);
    let cache = STREAM_CACHE.get_or_init(|| Mutex::new(HashMap::new()));
    if let Ok(guard) = cache.lock() {
        if let Some((stream, ts)) = guard.get(&cache_key) {
            if ts.elapsed() < Duration::from_secs(20 * 60) && !stream.audio_url.is_empty() {
                return Ok(stream.audio_url.clone());
            }
        }
    }

    // Tenta Fast-Path ANDROID_VR para áudio puro
    if let Some(fast) = try_resolve_via_inner_tube_fast_path(vid, 360) {
        if !fast.audio_url.is_empty() {
            if let Ok(mut guard) = cache.lock() {
                guard.insert(cache_key, (fast.clone(), Instant::now()));
            }
            return Ok(fast.audio_url);
        }
    }

    let yt_dlp = get_base_dir().join("bin").join("yt-dlp.exe");
    if !yt_dlp.exists() {
        return Err("bin\\yt-dlp.exe não encontrado.".to_string());
    }
    let deno = get_base_dir().join("bin").join("deno.exe");
    let cookie_scope =
        YtDlpCookieScope::create(&config.cookies_file_path, &config.browser_for_cookies);

    let mut args: Vec<String> = vec![
        "--no-config".to_string(),
        "--no-playlist".to_string(),
        "--no-warnings".to_string(),
        "-g".to_string(),
        "-f".to_string(),
        "bestaudio[ext=m4a]/bestaudio[ext=webm]/bestaudio/best".to_string(),
    ];
    if deno.exists() {
        args.push("--js-runtimes".to_string());
        args.push(format!("deno:{}", deno.display()));
    }
    if !cookie_scope.arg.is_empty() {
        for part in split_cli_args(&cookie_scope.arg) {
            args.push(part);
        }
    }
    args.push(format!("https://www.youtube.com/watch?v={}", vid));

    let output = Command::new(&yt_dlp)
        .args(&args)
        .creation_flags(CREATE_NO_WINDOW)
        .output()
        .map_err(|e| e.to_string())?;

    let stdout = String::from_utf8_lossy(&output.stdout);
    for line in stdout.lines() {
        let l = line.trim();
        if l.starts_with("http") {
            let stream = ResolvedStream {
                video_url: l.to_string(),
                audio_url: l.to_string(),
                codec_label: "Opus/AAC".to_string(),
                is_live: false,
                quality_height: 0,
            };
            if let Ok(mut guard) = cache.lock() {
                guard.insert(cache_key, (stream, Instant::now()));
            }
            return Ok(l.to_string());
        }
    }

    // Fallback sem cookies via client visionos/ios
    let fallback_out = Command::new(&yt_dlp)
        .args([
            "--no-config",
            "--no-playlist",
            "--no-warnings",
            "--extractor-args",
            "youtube:player_client=ios,web",
            "-g",
            "-f",
            "bestaudio/best",
            &format!("https://www.youtube.com/watch?v={}", vid),
        ])
        .creation_flags(CREATE_NO_WINDOW)
        .output()
        .map_err(|e| e.to_string())?;

    let stdout2 = String::from_utf8_lossy(&fallback_out.stdout);
    for line in stdout2.lines() {
        let l = line.trim();
        if l.starts_with("http") {
            return Ok(l.to_string());
        }
    }

    Err("Não foi possível resolver o fluxo de áudio desta faixa.".to_string())
}

fn is_fastpath_paused() -> bool {
    let mtx = FASTPATH_PAUSED_UNTIL.get_or_init(|| Mutex::new(None));
    if let Ok(guard) = mtx.lock() {
        if let Some(until) = *guard {
            return Instant::now() < until;
        }
    }
    false
}

fn pause_fastpath(duration: Duration, reason: &str) {
    let mtx = FASTPATH_PAUSED_UNTIL.get_or_init(|| Mutex::new(None));
    if let Ok(mut guard) = mtx.lock() {
        *guard = Some(Instant::now() + duration);
    }
    log_warn(
        "FASTPATH",
        &format!(
            "Disjuntor Fast-Path ativado por {} min ({}). Usando yt-dlp direto.",
            duration.as_secs() / 60,
            reason
        ),
    );
}

fn get_visitor_data() -> String {
    let mtx = VISITOR_DATA_CACHE.get_or_init(|| Mutex::new(None));
    if let Ok(guard) = mtx.lock() {
        if let Some((ref vd, ts)) = *guard {
            if ts.elapsed() < Duration::from_secs(4 * 3600) && !vd.is_empty() {
                return vd.clone();
            }
        }
    }

    let payload = "{\"context\":{\"client\":{\"clientName\":\"WEB\",\"clientVersion\":\"2.20250501.01.00\",\"hl\":\"pt\",\"gl\":\"BR\"}},\"browseId\":\"FEwhat_to_watch\"}";
    let mut headers = HashMap::new();
    headers.insert("X-YouTube-Client-Name".to_string(), "1".to_string());
    headers.insert(
        "X-YouTube-Client-Version".to_string(),
        "2.20250501.01.00".to_string(),
    );

    if let Some(json) = http_post_json(BROWSE_ENDPOINT, payload, None, &headers) {
        if let Ok(root) = serde_json::from_str::<Value>(&json) {
            if let Some(vd) = root
                .get("responseContext")
                .and_then(|rc| rc.get("visitorData"))
                .and_then(|v| v.as_str())
            {
                if !vd.is_empty() {
                    if let Ok(mut guard) = mtx.lock() {
                        *guard = Some((vd.to_string(), Instant::now()));
                    }
                    return vd.to_string();
                }
            }
        }
    }
    String::new()
}

fn try_resolve_via_inner_tube_fast_path(video_id: &str, target_height: i32) -> Option<ResolvedStream> {
    if is_fastpath_paused() {
        return None;
    }
    let sw = Instant::now();
    let visitor_data = get_visitor_data();

    let payload = serde_json::json!({
        "context": {
            "client": {
                "clientName": "ANDROID_VR",
                "clientVersion": "1.60.19",
                "deviceMake": "Oculus",
                "deviceModel": "Quest 3",
                "osName": "Android",
                "osVersion": "12L",
                "androidSdkVersion": 32,
                "visitorData": visitor_data,
                "hl": "pt",
                "gl": "BR"
            }
        },
        "videoId": video_id,
        "contentCheckOk": true,
        "racyCheckOk": true
    })
    .to_string();

    let mut headers = HashMap::new();
    headers.insert(
        "User-Agent".to_string(),
        "com.google.android.apps.youtube.vr.oculus/1.60.19 (Linux; U; Android 12L; eureka-user Build/SQ3A.220605.009.A1) gzip".to_string(),
    );
    headers.insert("X-YouTube-Client-Name".to_string(), "28".to_string());
    headers.insert("X-YouTube-Client-Version".to_string(), "1.60.19".to_string());
    if !visitor_data.is_empty() {
        headers.insert("X-Goog-Visitor-Id".to_string(), visitor_data);
    }

    let json = http_post_json(PLAYER_ENDPOINT, &payload, None, &headers)?;
    let root = serde_json::from_str::<Value>(&json).ok()?;

    let status = root
        .get("playabilityStatus")
        .and_then(|ps| ps.get("status"))
        .and_then(|s| s.as_str())
        .unwrap_or("MISSING");
    if !status.eq_ignore_ascii_case("OK") {
        if status.eq_ignore_ascii_case("LOGIN_REQUIRED") {
            pause_fastpath(Duration::from_secs(30 * 60), "LOGIN_REQUIRED");
        }
        return None;
    }

    if root
        .get("videoDetails")
        .and_then(|vd| vd.get("isLive"))
        .and_then(|l| l.as_bool())
        == Some(true)
    {
        return None;
    }

    let af = root
        .get("streamingData")
        .and_then(|sd| sd.get("adaptiveFormats"))
        .and_then(|a| a.as_array())?;

    let mut max_any_q = 0;
    let mut best_v_url = String::new();
    let mut best_v_q = 0;
    let mut best_v_rank = -1;
    let mut best_v_fps = 0;
    let mut best_v_br = 0i64;

    let mut best_a_url = String::new();
    let mut best_a_rank = -1;
    let mut best_a_br = 0i64;

    for fmt in af {
        let mime = fmt.get("mimeType").and_then(|v| v.as_str()).unwrap_or("");
        let Some(url) = fmt.get("url").and_then(|v| v.as_str()) else {
            continue;
        };
        if url.is_empty() {
            continue;
        }
        let br = fmt.get("bitrate").and_then(|v| v.as_i64()).unwrap_or(0);

        if mime.starts_with("video/") {
            let w = fmt.get("width").and_then(|v| v.as_i64()).unwrap_or(0) as i32;
            let h = fmt.get("height").and_then(|v| v.as_i64()).unwrap_or(0) as i32;
            let eff_q = resolve_effective_quality_height(w, h);
            if eff_q > max_any_q {
                max_any_q = eff_q;
            }
            if eff_q <= 0 || eff_q > target_height {
                continue;
            }
            let fps = fmt.get("fps").and_then(|v| v.as_i64()).unwrap_or(30) as i32;
            let rank = get_video_codec_rank(mime);

            let better = eff_q > best_v_q
                || (eff_q == best_v_q
                    && (rank > best_v_rank
                        || (rank == best_v_rank
                            && (fps > best_v_fps || (fps == best_v_fps && br > best_v_br)))));
            if better {
                best_v_url = url.to_string();
                best_v_q = eff_q;
                best_v_rank = rank;
                best_v_fps = fps;
                best_v_br = br;
            }
        } else if mime.starts_with("audio/") {
            let arank = if mime.contains("opus") {
                2
            } else if mime.contains("mp4a") {
                1
            } else {
                0
            };
            if arank > best_a_rank || (arank == best_a_rank && br > best_a_br) {
                best_a_url = url.to_string();
                best_a_rank = arank;
                best_a_br = br;
            }
        }
    }

    let min_acceptable = if target_height >= 720 {
        target_height.min(max_any_q.max(720))
    } else {
        target_height.min(max_any_q)
    };

    if !best_v_url.is_empty() && !best_a_url.is_empty() && best_v_q >= min_acceptable {
        let codec_label = detect_codec_from_urls(&best_v_url, &best_a_url);
        log_info(
            "FASTPATH",
            &format!(
                "InnerTube ANDROID_VR resolveu Id={} em {}ms ({}p, {})",
                video_id,
                sw.elapsed().as_millis(),
                best_v_q,
                codec_label
            ),
        );
        return Some(ResolvedStream {
            video_url: best_v_url,
            audio_url: best_a_url,
            codec_label,
            is_live: false,
            quality_height: best_v_q,
        });
    }

    None
}

fn resolve_via_yt_dlp(
    video_id: &str,
    target_height: i32,
    config: &AppConfig,
) -> Result<ResolvedStream, String> {
    let yt_dlp = get_base_dir().join("bin").join("yt-dlp.exe");
    if !yt_dlp.exists() {
        return Err("Executável bin\\yt-dlp.exe não encontrado.".to_string());
    }
    let deno = get_base_dir().join("bin").join("deno.exe");
    let cookie_scope =
        YtDlpCookieScope::create(&config.cookies_file_path, &config.browser_for_cookies);

    let video_url = format!("https://www.youtube.com/watch?v={}", video_id);
    let format_arg = "bestvideo+bestaudio/best";
    let sort_arg = format!(
        "res:{},vcodec:av01,vcodec:vp9.2,vcodec:vp9,vcodec:h264,fps,acodec:opus,acodec:m4a",
        target_height
    );

    let mut args = vec![
        "--no-config".to_string(),
        "--no-playlist".to_string(),
        "--no-warnings".to_string(),
        "-g".to_string(),
        "-f".to_string(),
        format_arg.to_string(),
        "-S".to_string(),
        sort_arg.clone(),
        "--format-sort-force".to_string(),
    ];
    if deno.exists() {
        args.push("--js-runtimes".to_string());
        args.push(format!("deno:{}", deno.display()));
    }
    if !cookie_scope.arg.is_empty() {
        for part in split_cli_args(&cookie_scope.arg) {
            args.push(part);
        }
    }
    args.push(video_url.clone());

    if let Ok(out) = Command::new(&yt_dlp)
        .args(&args)
        .creation_flags(CREATE_NO_WINDOW)
        .output()
    {
        let urls = extract_http_lines(&String::from_utf8_lossy(&out.stdout));
        if !urls.is_empty() {
            return Ok(build_resolved_stream_from_urls(urls, target_height));
        }
    }

    // Fallback multi-client sem cookies
    let mut fb_args = vec![
        "--no-config".to_string(),
        "--no-playlist".to_string(),
        "--no-warnings".to_string(),
        "--extractor-args".to_string(),
        "youtube:player_client=ios,web".to_string(),
        "-g".to_string(),
        "-f".to_string(),
        format_arg.to_string(),
        "-S".to_string(),
        sort_arg,
        "--format-sort-force".to_string(),
    ];
    if deno.exists() {
        fb_args.push("--js-runtimes".to_string());
        fb_args.push(format!("deno:{}", deno.display()));
    }
    fb_args.push(video_url);

    let out2 = Command::new(&yt_dlp)
        .args(&fb_args)
        .creation_flags(CREATE_NO_WINDOW)
        .output()
        .map_err(|e| e.to_string())?;

    let urls2 = extract_http_lines(&String::from_utf8_lossy(&out2.stdout));
    if !urls2.is_empty() {
        return Ok(build_resolved_stream_from_urls(urls2, target_height));
    }

    let stderr = String::from_utf8_lossy(&out2.stderr);
    log_error("YTDLP", &format!("Falha ao resolver stream {}: {}", video_id, stderr));
    Err("Não foi possível resolver os fluxos deste vídeo no YouTube.".to_string())
}

fn build_resolved_stream_from_urls(urls: Vec<String>, target_height: i32) -> ResolvedStream {
    let v_url = urls[0].clone();
    let a_url = if urls.len() > 1 {
        urls[1].clone()
    } else {
        String::new()
    };
    let is_live = v_url.contains(".m3u8") || v_url.contains("manifest.googlevideo.com");
    let codec_label = detect_codec_from_urls(&v_url, &a_url);
    ResolvedStream {
        video_url: v_url,
        audio_url: a_url,
        codec_label,
        is_live,
        quality_height: target_height,
    }
}

fn extract_http_lines(text: &str) -> Vec<String> {
    text.lines()
        .map(|l| l.trim())
        .filter(|l| l.starts_with("http"))
        .map(|l| l.to_string())
        .collect()
}

fn split_cli_args(raw: &str) -> Vec<String> {
    let mut out = Vec::new();
    let mut cur = String::new();
    let mut in_quotes = false;
    for ch in raw.chars() {
        if ch == '"' {
            in_quotes = !in_quotes;
        } else if ch.is_whitespace() && !in_quotes {
            if !cur.is_empty() {
                out.push(cur.clone());
                cur.clear();
            }
        } else {
            cur.push(ch);
        }
    }
    if !cur.is_empty() {
        out.push(cur);
    }
    out
}

// ============================================================================
// Metadados do Vídeo, Capítulos & Detecção de Codecs
// ============================================================================

pub fn fetch_video_metadata(video_id: &str, config: &AppConfig) -> VideoItem {
    let vid = video_id.trim();
    if vid.is_empty() {
        return VideoItem::default();
    }
    let payload = serde_json::json!({
        "context": {
            "client": {
                "hl": "pt",
                "gl": "BR",
                "clientName": "WEB",
                "clientVersion": WEB_CLIENT_VERSION
            }
        },
        "videoId": vid
    })
    .to_string();

    let (headers, _) = GoogleSessionService::shared().build_web_headers(config);
    if let Some(json) = http_post_json(
        PLAYER_ENDPOINT,
        &payload,
        Some(&format!("https://www.youtube.com/watch?v={}", vid)),
        &headers,
    ) {
        if let Some(meta) = parse_player_metadata_json(&json, vid) {
            return meta;
        }
    }
    VideoItem {
        id: vid.to_string(),
        ..Default::default()
    }
}

fn parse_player_metadata_json(json: &str, fallback_id: &str) -> Option<VideoItem> {
    let root = serde_json::from_str::<Value>(json).ok()?;
    let mut item = VideoItem {
        id: fallback_id.to_string(),
        ..Default::default()
    };

    if let Some(vd) = root.get("videoDetails") {
        let id = read_str(vd, "videoId");
        if !id.is_empty() {
            item.id = id;
        }
        item.title = read_str(vd, "title");
        item.author = read_str(vd, "author");
        item.description = read_str(vd, "shortDescription");
        if let Ok(vc) = read_str(vd, "viewCount").parse::<i64>() {
            item.view_count = vc;
        }
        if let Ok(len) = read_str(vd, "lengthSeconds").parse::<i64>() {
            item.duration_seconds = len;
        }
    }

    if let Some(pmf) = root
        .get("microformat")
        .and_then(|m| m.get("playerMicroformatRenderer"))
    {
        let ud = read_str(pmf, "uploadDate");
        if !ud.is_empty() {
            item.upload_date_raw = ud;
        } else {
            item.upload_date_raw = read_str(pmf, "publishDate");
        }
        if item.description.is_empty() {
            item.description = read_text(pmf, "description");
        }
    }

    let mut heights = HashSet::new();
    if let Some(sd) = root.get("streamingData") {
        for prop in ["formats", "adaptiveFormats"] {
            if let Some(arr) = sd.get(prop).and_then(|v| v.as_array()) {
                for fmt in arr {
                    let mime = read_str(fmt, "mimeType");
                    if mime.starts_with("audio/") {
                        continue;
                    }
                    let w = fmt.get("width").and_then(|v| v.as_i64()).unwrap_or(0) as i32;
                    let h = fmt.get("height").and_then(|v| v.as_i64()).unwrap_or(0) as i32;
                    let q = resolve_effective_quality_height(w, h);
                    if (144..=4320).contains(&q) {
                        heights.insert(q);
                    }
                }
            }
        }
    }
    if !heights.is_empty() {
        let mut sorted: Vec<i32> = heights.into_iter().collect();
        sorted.sort_unstable();
        item.available_resolutions = sorted;
    } else {
        item.available_resolutions = vec![144, 240, 360, 480, 720, 1080];
    }

    Some(item)
}

pub fn extract_chapters_from_description(
    description: &str,
    total_duration_sec: f64,
) -> Vec<VideoChapter> {
    let mut chapters = Vec::new();
    if description.trim().is_empty() {
        return chapters;
    }
    let Ok(rx) = Regex::new(r"(?m)^\s*(?:(\d{1,2}):)?(\d{1,2}):(\d{2})\s*[-–—:|]?\s*(.+?)\s*$")
    else {
        return chapters;
    };
    let mut seen = HashSet::new();
    for caps in rx.captures_iter(description) {
        let h = caps
            .get(1)
            .and_then(|m| m.as_str().parse::<i32>().ok())
            .unwrap_or(0);
        let min = caps
            .get(2)
            .and_then(|m| m.as_str().parse::<i32>().ok())
            .unwrap_or(0);
        let sec = caps
            .get(3)
            .and_then(|m| m.as_str().parse::<i32>().ok())
            .unwrap_or(0);
        if min >= 60 || sec >= 60 {
            continue;
        }
        let total_sec = h * 3600 + min * 60 + sec;
        if total_duration_sec > 0.0 && total_sec as f64 >= total_duration_sec {
            continue;
        }
        if !seen.insert(total_sec) {
            continue;
        }
        let title = caps
            .get(4)
            .map(|m| m.as_str().trim().to_string())
            .unwrap_or_default();
        if !title.is_empty() {
            chapters.push(VideoChapter {
                start_seconds: total_sec,
                title,
            });
        }
    }
    chapters.sort_by_key(|c| c.start_seconds);
    if chapters.len() >= 2 {
        chapters
    } else {
        Vec::new()
    }
}

pub fn resolve_effective_quality_height(width: i32, height: i32) -> i32 {
    if width <= 0 && height <= 0 {
        return 0;
    }
    let effective = if width > 0 && height > 0 {
        if height > width {
            width
        } else {
            let wide_equiv = ((width as f64) * 9.0 / 16.0).round() as i32;
            height.max(wide_equiv)
        }
    } else {
        height
    };
    if !(144..=4320).contains(&effective) {
        return 0;
    }
    let std_res = [144, 240, 360, 480, 720, 1080, 1440, 2160];
    let mut best = effective;
    let mut min_diff = i32::MAX;
    for &s in &std_res {
        let diff = (s - effective).abs();
        if diff < min_diff {
            min_diff = diff;
            best = s;
        }
    }
    if min_diff <= 60 { best } else { effective }
}

fn get_video_codec_rank(mime: &str) -> i32 {
    let lower = mime.to_ascii_lowercase();
    if lower.contains("av01") {
        4
    } else if lower.contains("vp09.02") || lower.contains("vp9.2") {
        3
    } else if lower.contains("vp09") || lower.contains("vp9") {
        2
    } else if lower.contains("avc1") {
        1
    } else {
        0
    }
}

pub fn detect_codec_from_urls(video_url: &str, audio_url: &str) -> String {
    if video_url.is_empty() {
        return "Direct3D 11".to_string();
    }
    if video_url.contains(".m3u8") || video_url.contains("manifest.googlevideo.com") {
        return "LIVE HLS (Direct3D 11)".to_string();
    }
    let v_itag = extract_itag(video_url);
    let a_itag = extract_itag(audio_url);

    let v_codec = if (394..=402).contains(&v_itag)
        || v_itag == 571
        || (694..=702).contains(&v_itag)
        || video_url.contains("av01")
    {
        "AV1"
    } else if (330..=337).contains(&v_itag)
        || video_url.contains("vp09.02")
        || video_url.contains("vp9.2")
    {
        "VP9.2 HDR"
    } else if v_itag == 278
        || (242..=248).contains(&v_itag)
        || v_itag == 271
        || v_itag == 313
        || v_itag == 302
        || v_itag == 303
        || v_itag == 308
        || v_itag == 315
        || video_url.contains("vp9")
        || video_url.contains("vp09")
    {
        "VP9"
    } else {
        "H.264"
    };

    let a_codec = if matches!(a_itag, 249..=251) || audio_url.contains("opus") {
        "Opus"
    } else {
        "AAC"
    };

    format!("{}/{} (Direct3D 11)", v_codec, a_codec)
}

pub fn extract_itag(url: &str) -> i32 {
    let Ok(rx) = Regex::new(r"[?&]itag=(\d+)") else {
        return 0;
    };
    rx.captures(url)
        .and_then(|c| c.get(1))
        .and_then(|m| m.as_str().parse::<i32>().ok())
        .unwrap_or(0)
}

pub fn parse_youtube_timestamp_seconds(raw_val: &str) -> Option<f64> {
    let s = raw_val.trim().to_ascii_lowercase();
    if s.is_empty() {
        return None;
    }
    if let Ok(secs) = s.trim_end_matches('s').parse::<f64>() {
        if secs >= 0.0 && secs.is_finite() {
            return Some(secs);
        }
    }
    let rx = Regex::new(r"^(?:(\d+)h)?(?:(\d+)m)?(?:(\d+)s?)?$").ok()?;
    let caps = rx.captures(&s)?;
    let h: f64 = caps.get(1).and_then(|m| m.as_str().parse().ok()).unwrap_or(0.0);
    let m: f64 = caps.get(2).and_then(|m| m.as_str().parse().ok()).unwrap_or(0.0);
    let sec: f64 = caps.get(3).and_then(|m| m.as_str().parse().ok()).unwrap_or(0.0);
    let total = h * 3600.0 + m * 60.0 + sec;
    if total > 0.0 { Some(total) } else { None }
}

/// Faz o parse completo de URLs diretas do YouTube OU do protocolo `litetube://play?url=...`,
/// retornando `(video_id, Option<segundos_iniciais>)`.
pub fn parse_litetube_or_youtube_url(raw: &str) -> Option<(String, Option<f64>)> {
    let mut candidate = raw.trim().trim_matches('"').to_string();
    let mut start_sec: Option<f64> = None;

    // 1. Desembrulha litetube://play?url=<encoded> (e captura &t= / &start= externo se houver)
    if candidate.to_ascii_lowercase().starts_with("litetube://") {
        if let Ok(parsed) = url::Url::parse(&candidate) {
            let mut inner_url: Option<String> = None;
            for (k, v) in parsed.query_pairs() {
                let kl = k.to_ascii_lowercase();
                if kl == "url" || kl == "u" || kl == "v" {
                    inner_url = Some(v.into_owned());
                } else if (kl == "t" || kl == "start") && start_sec.is_none() {
                    start_sec = parse_youtube_timestamp_seconds(&v);
                }
            }
            if let Some(u) = inner_url {
                candidate = u.trim().trim_matches('"').to_string();
            }
        }
    }

    // 2. Se for uma URL válida, extrai query params (v, t, start) diretamente
    if let Ok(parsed) = url::Url::parse(&candidate) {
        let mut vid_from_query: Option<String> = None;
        for (k, v) in parsed.query_pairs() {
            let kl = k.to_ascii_lowercase();
            if kl == "v" && v.len() == 11 {
                vid_from_query = Some(v.into_owned());
            } else if (kl == "t" || kl == "start") && start_sec.is_none() {
                start_sec = parse_youtube_timestamp_seconds(&v);
            }
        }
        if let Some(vid) = vid_from_query {
            if vid.chars().all(|c| c.is_ascii_alphanumeric() || c == '_' || c == '-') {
                return Some((vid, start_sec));
            }
        }
    }

    // 3. Fallback: ID puro de 11 caracteres ou regex em URLs curtas (/shorts/, /live/, youtu.be/, etc.)
    let trimmed = candidate.trim();
    if trimmed.len() == 11
        && trimmed
            .chars()
            .all(|c| c.is_ascii_alphanumeric() || c == '_' || c == '-')
    {
        return Some((trimmed.to_string(), start_sec));
    }

    let rx = Regex::new(r"(?:v=|youtu\.be/|/embed/|/shorts/|/live/|/watch/)([a-zA-Z0-9_-]{11})").ok()?;
    let vid = rx
        .captures(trimmed)
        .and_then(|c| c.get(1))
        .map(|m| m.as_str().to_string())?;

    if start_sec.is_none() {
        if let Ok(rx_t) = Regex::new(r"[?&#](?:t|start)=([0-9hmsHMS]+)") {
            if let Some(t_val) = rx_t.captures(trimmed).and_then(|c| c.get(1)) {
                start_sec = parse_youtube_timestamp_seconds(t_val.as_str());
            }
        }
    }

    Some((vid, start_sec))
}

pub fn extract_video_id_from_any_url(raw: &str) -> Option<String> {
    parse_litetube_or_youtube_url(raw).map(|(vid, _)| vid)
}

// ============================================================================
// Helpers Internos JSON
// ============================================================================

fn read_str(obj: &Value, prop: &str) -> String {
    obj.get(prop)
        .and_then(|v| v.as_str())
        .unwrap_or("")
        .to_string()
}

fn read_text(obj: &Value, prop: &str) -> String {
    let Some(el) = obj.get(prop) else {
        return String::new();
    };
    if let Some(s) = el.as_str() {
        return s.to_string();
    }
    if let Some(st) = el.get("simpleText").and_then(|v| v.as_str()) {
        return st.to_string();
    }
    if let Some(ct) = el.get("content").and_then(|v| v.as_str()) {
        return ct.to_string();
    }
    if let Some(runs) = el.get("runs").and_then(|v| v.as_array()) {
        let mut out = String::new();
        for r in runs {
            if let Some(t) = r.get("text").and_then(|v| v.as_str()) {
                out.push_str(t);
            }
        }
        return out.trim().to_string();
    }
    String::new()
}

fn find_property_recursive<'a>(el: &'a Value, prop_name: &str) -> Option<&'a Value> {
    match el {
        Value::Object(map) => {
            if let Some(found) = map.get(prop_name) {
                return Some(found);
            }
            for val in map.values() {
                if let Some(found) = find_property_recursive(val, prop_name) {
                    return Some(found);
                }
            }
            None
        }
        Value::Array(arr) => {
            for child in arr {
                if let Some(found) = find_property_recursive(child, prop_name) {
                    return Some(found);
                }
            }
            None
        }
        _ => None,
    }
}

fn find_first_text_by_key(el: &Value, key_name: &str) -> String {
    if let Some(found) = find_property_recursive(el, key_name) {
        if let Some(s) = found.as_str() {
            return s.to_string();
        }
        if let Some(st) = found.get("simpleText").and_then(|v| v.as_str()) {
            return st.to_string();
        }
        if let Some(runs) = found.get("runs").and_then(|v| v.as_array()) {
            let mut out = String::new();
            for r in runs {
                if let Some(t) = r.get("text").and_then(|v| v.as_str()) {
                    out.push_str(t);
                }
            }
            return out.trim().to_string();
        }
    }
    String::new()
}

fn extract_continuation_from_item(el: &Value) -> String {
    el.get("continuationEndpoint")
        .and_then(|ep| ep.get("continuationCommand"))
        .and_then(|cmd| cmd.get("token"))
        .and_then(|t| t.as_str())
        .unwrap_or("")
        .to_string()
}

fn extract_browse_id_from_owner(obj: &Value, owner_prop: &str) -> String {
    let Some(runs) = obj
        .get(owner_prop)
        .and_then(|o| o.get("runs"))
        .and_then(|r| r.as_array())
    else {
        return String::new();
    };
    for r in runs {
        if let Some(bid) = r
            .get("navigationEndpoint")
            .and_then(|n| n.get("browseEndpoint"))
            .and_then(|b| b.get("browseId"))
            .and_then(|v| v.as_str())
        {
            if bid.starts_with("UC") {
                return bid.to_string();
            }
        }
    }
    String::new()
}

fn extract_classic_channel_avatar(obj: &Value) -> String {
    for prop in ["channelThumbnailSupportedRenderers", "channelThumbnail", "avatar"] {
        if let Some(sub) = obj.get(prop) {
            let u = if prop == "avatar" {
                extract_nested_image_url(sub)
            } else {
                extract_best_thumbnail_url(sub)
            };
            if !u.is_empty() {
                return normalize_avatar_url(&u);
            }
        }
    }
    String::new()
}

fn extract_best_thumbnail_url(el: &Value) -> String {
    match el {
        Value::Object(map) => {
            if let Some(arr) = map.get("thumbnails").and_then(|v| v.as_array()) {
                let mut best = String::new();
                for t in arr {
                    let u = read_str(t, "url");
                    if !u.is_empty() {
                        best = u;
                    }
                }
                if !best.is_empty() {
                    return if best.starts_with("//") {
                        format!("https:{}", best)
                    } else {
                        best
                    };
                }
            }
            for val in map.values() {
                let nested = extract_best_thumbnail_url(val);
                if !nested.is_empty() {
                    return nested;
                }
            }
        }
        Value::Array(arr) => {
            for item in arr {
                let nested = extract_best_thumbnail_url(item);
                if !nested.is_empty() {
                    return nested;
                }
            }
        }
        _ => {}
    }
    String::new()
}

fn extract_nested_image_url(el: &Value) -> String {
    match el {
        Value::Object(map) => {
            for key in ["sources", "thumbnails"] {
                if let Some(arr) = map.get(key).and_then(|v| v.as_array()) {
                    let mut best = String::new();
                    for s in arr {
                        let u = read_str(s, "url");
                        if !u.is_empty() {
                            best = u;
                        }
                    }
                    if !best.is_empty() {
                        return if best.starts_with("//") {
                            format!("https:{}", best)
                        } else {
                            best
                        };
                    }
                }
            }
            for val in map.values() {
                let nested = extract_nested_image_url(val);
                if !nested.is_empty() {
                    return nested;
                }
            }
        }
        Value::Array(arr) => {
            for item in arr {
                let nested = extract_nested_image_url(item);
                if !nested.is_empty() {
                    return nested;
                }
            }
        }
        _ => {}
    }
    String::new()
}

fn extract_first_channel_browse_id(el: &Value) -> String {
    match el {
        Value::Object(map) => {
            if let Some(bid) = map.get("browseId").and_then(|v| v.as_str()) {
                if bid.starts_with("UC") {
                    return bid.to_string();
                }
            }
            for val in map.values() {
                let found = extract_first_channel_browse_id(val);
                if !found.is_empty() {
                    return found;
                }
            }
        }
        Value::Array(arr) => {
            for item in arr {
                let found = extract_first_channel_browse_id(item);
                if !found.is_empty() {
                    return found;
                }
            }
        }
        _ => {}
    }
    String::new()
}

fn extract_playlist_id_from_context(el: &Value) -> String {
    match el {
        Value::Object(map) => {
            if let Some(pid) = map.get("playlistId").and_then(|v| v.as_str()) {
                if !pid.trim().is_empty() {
                    return pid.to_string();
                }
            }
            if let Some(bid) = map.get("browseId").and_then(|v| v.as_str()) {
                if bid.len() > 2 && bid[..2].eq_ignore_ascii_case("VL") {
                    return bid[2..].to_string();
                }
            }
            for val in map.values() {
                let found = extract_playlist_id_from_context(val);
                if !found.is_empty() {
                    return found;
                }
            }
        }
        Value::Array(arr) => {
            for item in arr {
                let found = extract_playlist_id_from_context(item);
                if !found.is_empty() {
                    return found;
                }
            }
        }
        _ => {}
    }
    String::new()
}

fn extract_lockup_badge_text(el: &Value) -> String {
    match el {
        Value::Object(map) => {
            if let Some(badge) = map.get("thumbnailBadgeViewModel") {
                let t = read_str(badge, "text");
                if !t.trim().is_empty() {
                    return t;
                }
            }
            for (k, v) in map {
                if k == "metadata" {
                    continue;
                }
                let found = extract_lockup_badge_text(v);
                if !found.is_empty() {
                    return found;
                }
            }
        }
        Value::Array(arr) => {
            for item in arr {
                let found = extract_lockup_badge_text(item);
                if !found.is_empty() {
                    return found;
                }
            }
        }
        _ => {}
    }
    String::new()
}

fn extract_first_video_id_recursive(el: &Value) -> String {
    match el {
        Value::Object(map) => {
            if let Some(vid) = map.get("videoId").and_then(|v| v.as_str()) {
                if vid.len() == 11 {
                    return vid.to_string();
                }
            }
            for val in map.values() {
                let found = extract_first_video_id_recursive(val);
                if !found.is_empty() {
                    return found;
                }
            }
        }
        Value::Array(arr) => {
            for item in arr {
                let found = extract_first_video_id_recursive(item);
                if !found.is_empty() {
                    return found;
                }
            }
        }
        _ => {}
    }
    String::new()
}

fn extract_video_id_from_thumb_url(url: &str) -> String {
    let Ok(rx) = Regex::new(r"/vi(?:_webp)?/([a-zA-Z0-9_-]{11})/") else {
        return String::new();
    };
    rx.captures(url)
        .and_then(|c| c.get(1))
        .map(|m| m.as_str().to_string())
        .unwrap_or_default()
}

fn looks_like_views(text: &str) -> bool {
    let l = text.to_ascii_lowercase();
    l.contains("visualiza") || l.contains("view") || l.contains("assistindo") || l.contains("watching")
}

fn looks_like_date(text: &str) -> bool {
    let l = text.to_ascii_lowercase();
    l.starts_with("há ") || l.contains("ago") || l.contains("transmitido") || l.contains("streamed")
}
