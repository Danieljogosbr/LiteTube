// ============================================================================
// LiteTube 2.0 (Rust) — Camada 3: SponsorBlock & Storyboard Preview
// ============================================================================

#![allow(dead_code)]

use crate::memory::log_info;
use crate::youtube::{http_get_bytes, http_get_text, http_post_json};
use regex::Regex;
use serde_json::Value;
use std::collections::HashMap;
use std::sync::{Mutex, OnceLock};

#[derive(Debug, Clone)]
pub struct SponsorSegment {
    pub category: String,
    pub action_type: String,
    pub start: f64,
    pub end: f64,
    pub uuid: String,
}

impl SponsorSegment {
    pub fn category_label(&self) -> &'static str {
        match self.category.as_str() {
            "sponsor" => "Patrocínio",
            "selfpromo" => "Autopromoção",
            "interaction" => "Lembrete de Inscrição/Like",
            "intro" => "Introdução / Vinheta",
            "outro" => "Tela Final / Créditos",
            "preview" => "Prévia / Recap",
            "music_offtopic" => "Trecho Não-Musical",
            _ => "Segmento",
        }
    }

    pub fn color_rgba(&self) -> [u8; 4] {
        match self.category.as_str() {
            "sponsor" => [0x00, 0xD4, 0x00, 230],        // Verde SponsorBlock
            "selfpromo" => [0xFF, 0xFF, 0x00, 230],      // Amarelo
            "interaction" => [0xCC, 0x00, 0xFF, 230],    // Roxo
            "intro" => [0x00, 0xFF, 0xFF, 230],          // Ciano
            "outro" => [0x02, 0x02, 0xED, 230],          // Azul
            "music_offtopic" => [0xFF, 0x99, 0x00, 230], // Laranja
            _ => [0x00, 0xD4, 0x00, 230],
        }
    }
}

static SPONSOR_CACHE: OnceLock<Mutex<HashMap<String, Vec<SponsorSegment>>>> = OnceLock::new();

pub fn get_video_sponsor_segments(video_id: &str) -> Vec<SponsorSegment> {
    fetch_sponsor_segments(
        video_id,
        "%5B%22sponsor%22,%22selfpromo%22,%22interaction%22,%22intro%22,%22outro%22%5D",
        "vid",
    )
}

pub fn get_music_sponsor_segments(video_id: &str) -> Vec<SponsorSegment> {
    fetch_sponsor_segments(
        video_id,
        "%5B%22music_offtopic%22,%22sponsor%22,%22selfpromo%22,%22intro%22,%22outro%22%5D",
        "mus",
    )
}

fn fetch_sponsor_segments(
    video_id: &str,
    categories_param: &str,
    mode_key: &str,
) -> Vec<SponsorSegment> {
    let vid = video_id.trim();
    if vid.len() != 11 {
        return Vec::new();
    }
    let cache_key = format!("{}:{}", mode_key, vid);
    let cache = SPONSOR_CACHE.get_or_init(|| Mutex::new(HashMap::new()));
    if let Ok(guard) = cache.lock() {
        if let Some(cached) = guard.get(&cache_key) {
            return cached.clone();
        }
    }

    let url = format!(
        "https://sponsor.ajay.app/api/skipSegments?videoID={}&categories={}",
        vid, categories_param
    );

    let mut list = Vec::new();
    if let Some(json) = http_get_text(&url) {
        if let Ok(Value::Array(arr)) = serde_json::from_str::<Value>(&json) {
            for item in arr {
                let category = item
                    .get("category")
                    .and_then(|v| v.as_str())
                    .unwrap_or("sponsor")
                    .to_string();
                let action_type = item
                    .get("actionType")
                    .and_then(|v| v.as_str())
                    .unwrap_or("skip")
                    .to_string();
                let uuid = item
                    .get("UUID")
                    .and_then(|v| v.as_str())
                    .unwrap_or("")
                    .to_string();
                if let Some(seg) = item.get("segment").and_then(|v| v.as_array()) {
                    if seg.len() >= 2 {
                        let start = seg[0].as_f64().unwrap_or(0.0);
                        let end = seg[1].as_f64().unwrap_or(0.0);
                        if end > start && (end - start) >= 1.2 {
                            list.push(SponsorSegment {
                                category,
                                action_type,
                                start,
                                end,
                                uuid,
                            });
                        }
                    }
                }
            }
        }
    }

    if let Ok(mut guard) = cache.lock() {
        if guard.len() > 150 {
            guard.clear();
        }
        guard.insert(cache_key, list.clone());
    }
    list
}

/// Verifica se `current_seconds` entrou num segmento do SponsorBlock e retorna `( target_seconds, segment )`.
pub fn check_and_get_skip_target(
    current_seconds: f64,
    segments: &[SponsorSegment],
) -> Option<(f64, SponsorSegment)> {
    for seg in segments {
        if !seg.action_type.is_empty() && !seg.action_type.eq_ignore_ascii_case("skip") {
            continue;
        }
        if current_seconds >= seg.start && current_seconds < (seg.end - 0.6) {
            return Some((seg.end, seg.clone()));
        }
    }
    None
}

// ============================================================================
// StoryboardService — Miniaturas de Pré-visualização na Timeline (Scrubber)
// ============================================================================

#[derive(Debug, Clone)]
pub struct StoryboardData {
    pub video_id: String,
    pub url_template: String,
    pub frame_width: u32,
    pub frame_height: u32,
    pub total_frames: u32,
    pub cols: u32,
    pub rows: u32,
    pub interval_ms: u32,
    pub duration_seconds: f64,
}

impl StoryboardData {
    pub fn frames_per_sheet(&self) -> u32 {
        (self.cols * self.rows).max(1)
    }

    /// Calcula `(sheet_index, sheet_url, crop_x, crop_y, crop_w, crop_h)` para um segundo do vídeo.
    pub fn get_frame_location(&self, hover_seconds: f64) -> Option<(u32, String, u32, u32, u32, u32)> {
        if self.url_template.is_empty()
            || self.total_frames == 0
            || self.cols == 0
            || self.rows == 0
            || self.frame_width == 0
            || self.frame_height == 0
        {
            return None;
        }
        let clamped_sec = hover_seconds.max(0.0);
        let mut frame_index = if self.interval_ms > 0 {
            ((clamped_sec * 1000.0) / self.interval_ms as f64).floor() as u32
        } else if self.duration_seconds > 0.0 {
            ((clamped_sec / self.duration_seconds) * self.total_frames as f64).floor() as u32
        } else {
            0
        };
        if frame_index >= self.total_frames {
            frame_index = self.total_frames.saturating_sub(1);
        }
        let per_sheet = self.frames_per_sheet();
        let sheet_index = frame_index / per_sheet;
        let index_in_sheet = frame_index % per_sheet;
        let row = index_in_sheet / self.cols;
        let col = index_in_sheet % self.cols;

        let sheet_url = self
            .url_template
            .replace("$M", &sheet_index.to_string());
        Some((
            sheet_index,
            sheet_url,
            col * self.frame_width,
            row * self.frame_height,
            self.frame_width,
            self.frame_height,
        ))
    }
}

static STORYBOARD_META_CACHE: OnceLock<Mutex<HashMap<String, StoryboardData>>> = OnceLock::new();

pub fn fetch_storyboard_data(video_id: &str, duration_seconds: f64) -> Option<StoryboardData> {
    let vid = video_id.trim();
    if vid.len() != 11 {
        return None;
    }
    let cache = STORYBOARD_META_CACHE.get_or_init(|| Mutex::new(HashMap::new()));
    if let Ok(guard) = cache.lock() {
        if let Some(sb) = guard.get(vid) {
            return Some(sb.clone());
        }
    }

    let payload = format!(
        "{{\"context\":{{\"client\":{{\"clientName\":\"WEB\",\"clientVersion\":\"2.20240726.00.00\",\"hl\":\"pt\",\"gl\":\"BR\"}}}},\"videoId\":\"{}\"}}",
        vid
    );
    let headers = HashMap::new();
    let json = http_post_json(
        "https://www.youtube.com/youtubei/v1/player?prettyPrint=false",
        &payload,
        Some(&format!("https://www.youtube.com/watch?v={}", vid)),
        &headers,
    )?;

    let rx = Regex::new(r#""spec"\s*:\s*"((?:[^"\\]|\\.)*)""#).ok()?;
    let caps = rx.captures(&json)?;
    let raw_spec = caps
        .get(1)?
        .as_str()
        .replace("\\u0026", "&")
        .replace("\\/", "/")
        .replace("&amp;", "&");

    let sb = parse_storyboard_spec(vid, &raw_spec, duration_seconds)?;
    if let Ok(mut guard) = cache.lock() {
        if guard.len() > 64 {
            guard.clear();
        }
        guard.insert(vid.to_string(), sb.clone());
    }
    log_info(
        "STORYBOARD",
        &format!(
            "Storyboard carregado para {}: {}x{} ({} quadros)",
            vid, sb.frame_width, sb.frame_height, sb.total_frames
        ),
    );
    Some(sb)
}

fn parse_storyboard_spec(
    video_id: &str,
    spec: &str,
    fallback_duration_sec: f64,
) -> Option<StoryboardData> {
    let parts: Vec<&str> = spec.split('|').collect();
    if parts.len() < 2 {
        return None;
    }
    let base_url = parts[0];
    let level_index = parts.len() - 1;
    let level_num = level_index - 1;
    let fields: Vec<&str> = parts[level_index].split('#').collect();
    if fields.len() < 8 {
        return None;
    }

    let w = fields[0].parse::<u32>().ok()?;
    let h = fields[1].parse::<u32>().ok()?;
    let count = fields[2].parse::<u32>().ok()?;
    let cols = fields[3].parse::<u32>().ok()?;
    let rows = fields[4].parse::<u32>().ok()?;
    let interval_ms = fields[5].parse::<u32>().unwrap_or(2000);
    let name_pattern = fields[6];
    let sigh = fields[7];

    if w == 0 || h == 0 || count == 0 || cols == 0 || rows == 0 {
        return None;
    }

    let mut url_template = base_url
        .replace("$L", &level_num.to_string())
        .replace("$N", name_pattern);

    if !sigh.is_empty() && !url_template.contains("sigh=") && !url_template.contains("rs=") {
        let sep = if url_template.contains('?') { '&' } else { '?' };
        let param = if sigh.starts_with("rs$") || sigh.starts_with("AOn") {
            "rs"
        } else {
            "sigh"
        };
        url_template = format!("{}{}{}={}", url_template, sep, param, sigh);
    }

    let calc_dur = if interval_ms > 0 {
        (count as f64 * interval_ms as f64) / 1000.0
    } else {
        fallback_duration_sec
    };

    Some(StoryboardData {
        video_id: video_id.to_string(),
        url_template,
        frame_width: w,
        frame_height: h,
        total_frames: count,
        cols,
        rows,
        interval_ms,
        duration_seconds: if fallback_duration_sec > 0.0 {
            fallback_duration_sec
        } else {
            calc_dur
        },
    })
}

/// Baixa e recorta em RGBA (largura, altura, pixels) o quadro do Storyboard para exibição no Scrubber.
pub fn fetch_cropped_storyboard_frame(
    sb: &StoryboardData,
    hover_seconds: f64,
    sheet_cache: &Mutex<HashMap<String, image::RgbaImage>>,
) -> Option<(u32, u32, Vec<u8>)> {
    let (_, sheet_url, cx, cy, cw, ch) = sb.get_frame_location(hover_seconds)?;

    let sheet_img = {
        if let Ok(guard) = sheet_cache.lock() {
            guard.get(&sheet_url).cloned()
        } else {
            None
        }
    };

    let img = match sheet_img {
        Some(cached) => cached,
        None => {
            let bytes = http_get_bytes(&sheet_url)?;
            let decoded = image::load_from_memory(&bytes).ok()?.to_rgba8();
            if let Ok(mut guard) = sheet_cache.lock() {
                if guard.len() > 24 {
                    guard.clear();
                }
                guard.insert(sheet_url, decoded.clone());
            }
            decoded
        }
    };

    if cx + cw > img.width() || cy + ch > img.height() {
        return None;
    }

    let sub = image::imageops::crop_imm(&img, cx, cy, cw, ch).to_image();
    Some((cw, ch, sub.into_raw()))
}
