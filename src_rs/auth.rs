// ============================================================================
// LiteTube 2.0 (Rust) — Camada 4: Autenticação & Privacidade
// 100% compatível com o cofre Windows DPAPI (LTDPAPI1:) em data/cookies.dpapi
// e geração nativa de SAPISIDHASH (SHA-1) para YouTube e YouTube Music.
// ============================================================================

#![allow(dead_code)]

use crate::memory::{log_info, log_warn};
use crate::models::{get_base_dir, AppConfig};
use base64::Engine;
use base64::engine::general_purpose::STANDARD as BASE64_STD;
use serde_json::Value;
use sha1::{Digest, Sha1};
use std::collections::HashMap;
use std::fs;
use std::path::PathBuf;
use std::process::Command;
use std::os::windows::process::CommandExt;
use std::sync::{Arc, Mutex, OnceLock};
use std::time::{Duration, Instant, SystemTime, UNIX_EPOCH};
use windows::Win32::Foundation::{HLOCAL, LocalFree};
use windows::Win32::Security::Cryptography::{
    CRYPT_INTEGER_BLOB, CRYPTPROTECT_UI_FORBIDDEN, CryptProtectData, CryptUnprotectData,
};
use windows::core::PCWSTR;

pub const VAULT_PREFIX: &str = "LTDPAPI1:";
pub const WEB_CLIENT_VERSION: &str = "2.20250501.01.00";
pub const MUSIC_CLIENT_VERSION: &str = "1.20250501.01.00";
pub const DEFAULT_USER_AGENT: &str = "Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/125.0.0.0 Safari/537.36";

const CREATE_NO_WINDOW: u32 = 0x08000000;

pub fn get_vault_file_path() -> PathBuf {
    get_base_dir().join("data").join("cookies.dpapi")
}

pub fn get_legacy_plaintext_path() -> PathBuf {
    get_base_dir().join("data").join("cookies.txt")
}

/// Criptografa uma string UTF-8 usando Windows DPAPI (CurrentUser) com prefixo "LTDPAPI1:".
pub fn protect_text(plain_text: &str) -> Option<String> {
    if plain_text.trim().is_empty() {
        return None;
    }
    let bytes = plain_text.as_bytes();
    let in_blob = CRYPT_INTEGER_BLOB {
        cbData: bytes.len() as u32,
        pbData: bytes.as_ptr() as *mut u8,
    };
    let mut out_blob = CRYPT_INTEGER_BLOB::default();

    unsafe {
        let ok = CryptProtectData(
            &in_blob,
            PCWSTR::null(),
            None,
            None,
            None,
            CRYPTPROTECT_UI_FORBIDDEN,
            &mut out_blob,
        );
        if ok.is_err() || out_blob.pbData.is_null() || out_blob.cbData == 0 {
            return None;
        }
        let slice = std::slice::from_raw_parts(out_blob.pbData, out_blob.cbData as usize);
        let b64 = BASE64_STD.encode(slice);
        let _ = LocalFree( Some(HLOCAL(out_blob.pbData as *mut _)) );
        Some(format!("{}{}", VAULT_PREFIX, b64))
    }
}

/// Descriptografa o conteúdo de data/cookies.dpapi ("LTDPAPI1:<base64>") via Windows DPAPI.
pub fn unprotect_text(stored_content: &str) -> Option<String> {
    let trimmed = stored_content.trim().trim_start_matches('\u{feff}');
    if trimmed.is_empty() {
        return None;
    }
    if !trimmed.starts_with(VAULT_PREFIX) {
        if looks_like_netscape_cookies(trimmed) {
            return Some(trimmed.to_string());
        }
        return None;
    }

    let b64 = trimmed[VAULT_PREFIX.len()..].trim();
    let cipher = BASE64_STD.decode(b64).ok()?;
    if cipher.is_empty() {
        return None;
    }

    let in_blob = CRYPT_INTEGER_BLOB {
        cbData: cipher.len() as u32,
        pbData: cipher.as_ptr() as *mut u8,
    };
    let mut out_blob = CRYPT_INTEGER_BLOB::default();

    unsafe {
        let ok = CryptUnprotectData(
            &in_blob,
            None,
            None,
            None,
            None,
            CRYPTPROTECT_UI_FORBIDDEN,
            &mut out_blob,
        );
        if ok.is_err() || out_blob.pbData.is_null() || out_blob.cbData == 0 {
            log_warn("VAULT", "Falha ao descriptografar cofre DPAPI.");
            return None;
        }
        let slice = std::slice::from_raw_parts(out_blob.pbData, out_blob.cbData as usize);
        let text = String::from_utf8_lossy(slice)
            .trim_start_matches('\u{feff}')
            .to_string();
        let _ = LocalFree( Some(HLOCAL(out_blob.pbData as *mut _)) );
        Some(text)
    }
}

pub fn looks_like_netscape_cookies(text: &str) -> bool {
    for raw_line in text.lines() {
        let line = raw_line.trim();
        if line.is_empty() {
            continue;
        }
        if line.starts_with("#HttpOnly_") {
            let unprefixed = &line["#HttpOnly_".len()..];
            if unprefixed.split('\t').count() >= 7 {
                return true;
            }
        }
        if line.starts_with('#') {
            continue;
        }
        if line.split('\t').count() >= 7 {
            return true;
        }
    }
    false
}

pub fn sanitize_netscape_content(raw_text: &str) -> String {
    let mut out = String::from("# Netscape HTTP Cookie File\n");
    for raw_line in raw_text.lines() {
        let mut line = raw_line.trim();
        if line.is_empty() {
            continue;
        }
        if line.starts_with("#HttpOnly_") {
            line = &line["#HttpOnly_".len()..];
        } else if line.starts_with('#') {
            continue;
        }
        let parts: Vec<&str> = line.split('\t').collect();
        if parts.len() >= 7 {
            out.push_str(line);
            out.push('\n');
        }
    }
    out
}

pub fn has_stored_cookies(path_or_default: &str) -> bool {
    let target = if path_or_default.trim().is_empty() {
        get_vault_file_path()
    } else {
        let p = PathBuf::from(path_or_default);
        if p.exists() {
            p
        } else {
            get_vault_file_path()
        }
    };
    if let Ok(meta) = fs::metadata(&target) {
        if meta.len() >= 16 {
            return true;
        }
    }
    let vault = get_vault_file_path();
    if let Ok(meta) = fs::metadata(&vault) {
        return meta.len() >= 16;
    }
    false
}

pub fn load_cookies_plaintext(path_or_default: &str) -> Option<String> {
    let candidates = [
        if path_or_default.trim().is_empty() {
            get_vault_file_path()
        } else {
            PathBuf::from(path_or_default)
        },
        get_vault_file_path(),
        get_legacy_plaintext_path(),
    ];

    for p in &candidates {
        if !p.exists() {
            continue;
        }
        if let Ok(raw) = fs::read_to_string(p) {
            if let Some(plain) = unprotect_text(&raw) {
                if !plain.trim().is_empty() {
                    return Some(plain);
                }
            }
        }
    }
    None
}

pub fn save_protected_cookies(raw_netscape_text: &str) -> bool {
    let clean = sanitize_netscape_content(raw_netscape_text);
    if let Some(encrypted) = protect_text(&clean) {
        let vault_path = get_vault_file_path();
        if let Some(dir) = vault_path.parent() {
            let _ = fs::create_dir_all(dir);
        }
        if fs::write(&vault_path, encrypted).is_ok() {
            let legacy = get_legacy_plaintext_path();
            if legacy.exists() {
                let _ = fs::remove_file(&legacy);
            }
            log_info("VAULT", "Sessão Google salva com criptografia Windows DPAPI (cookies.dpapi).");
            return true;
        }
    }
    false
}

/// Escopo RAII que materializa um arquivo temporário de cookies para o yt-dlp
/// e o destrói imediatamente ao sair de escopo (Drop).
pub struct YtDlpCookieScope {
    pub arg: String,
    temp_file_path: Option<PathBuf>,
}

impl YtDlpCookieScope {
    pub fn create(cookies_file_path: &str, browser_for_cookies: &str) -> Self {
        static TEMP_COOKIE_SEQ: std::sync::atomic::AtomicU64 =
            std::sync::atomic::AtomicU64::new(1);
        if let Some(plain) = load_cookies_plaintext(cookies_file_path) {
            if !plain.trim().is_empty() {
                let nanos = SystemTime::now()
                    .duration_since(UNIX_EPOCH)
                    .map(|d| d.as_nanos())
                    .unwrap_or(0);
                let seq = TEMP_COOKIE_SEQ.fetch_add(1, std::sync::atomic::Ordering::Relaxed);
                let tmp = std::env::temp_dir().join(format!(
                    "lt_ck_{}_{}_{}.tmp",
                    std::process::id(),
                    nanos,
                    seq
                ));
                if fs::write(&tmp, plain).is_ok() {
                    let arg = format!("--cookies \"{}\" ", tmp.display());
                    return Self {
                        arg,
                        temp_file_path: Some(tmp),
                    };
                }
            }
        }
        let b = browser_for_cookies.trim();
        if !b.is_empty() && !b.eq_ignore_ascii_case("none") {
            return Self {
                arg: format!("--cookies-from-browser {} ", b),
                temp_file_path: None,
            };
        }
        Self {
            arg: String::new(),
            temp_file_path: None,
        }
    }
}

impl Drop for YtDlpCookieScope {
    fn drop(&mut self) {
        if let Some(ref p) = self.temp_file_path {
            let _ = fs::remove_file(p);
        }
    }
}

// ============================================================================
// GoogleSessionService — SAPISIDHASH, Perfil da Conta e Cabeçalhos InnerTube
// ============================================================================

#[derive(Debug, Clone, Default)]
pub struct GoogleAccountProfile {
    pub is_authenticated: bool,
    pub session_expired: bool,
    pub display_name: String,
    pub email_or_handle: String,
    pub avatar_url: String,
}

pub struct GoogleSessionService {
    cached_cookie_header: Mutex<Option<(String, Instant)>>,
    cached_profile: Mutex<Option<(GoogleAccountProfile, Instant)>>,
    session_expired: Mutex<bool>,
}

static SESSION_INSTANCE: OnceLock<Arc<GoogleSessionService>> = OnceLock::new();

impl GoogleSessionService {
    pub fn shared() -> Arc<GoogleSessionService> {
        SESSION_INSTANCE
            .get_or_init(|| {
                Arc::new(GoogleSessionService {
                    cached_cookie_header: Mutex::new(None),
                    cached_profile: Mutex::new(None),
                    session_expired: Mutex::new(false),
                })
            })
            .clone()
    }

    pub fn invalidate_cache(&self) {
        if let Ok(mut g) = self.cached_cookie_header.lock() {
            *g = None;
        }
        if let Ok(mut g) = self.cached_profile.lock() {
            *g = None;
        }
        if let Ok(mut g) = self.session_expired.lock() {
            *g = false;
        }
    }

    pub fn mark_session_expired(&self) {
        if let Ok(mut g) = self.session_expired.lock() {
            *g = true;
        }
    }

    pub fn is_session_expired(&self) -> bool {
        self.session_expired.lock().map(|g| *g).unwrap_or(false)
    }

    pub fn has_stored_session(&self, config: &AppConfig) -> bool {
        has_stored_cookies(&config.cookies_file_path)
            || (!config.browser_for_cookies.trim().is_empty()
                && !config.browser_for_cookies.eq_ignore_ascii_case("none"))
    }

    pub fn resolve_cookie_header(&self, config: &AppConfig) -> String {
        let mut guard = self
            .cached_cookie_header
            .lock()
            .unwrap_or_else(|e| e.into_inner());
        if let Some((ref hdr, ts)) = *guard {
            if ts.elapsed() < Duration::from_secs(120) && !hdr.is_empty() {
                return hdr.clone();
            }
        }

        if let Some(plain) = load_cookies_plaintext(&config.cookies_file_path) {
            let hdr = parse_netscape_cookies_to_header(&plain);
            if !hdr.is_empty() {
                *guard = Some((hdr.clone(), Instant::now()));
                return hdr;
            }
        }
        String::new()
    }

    pub fn build_web_headers(&self, config: &AppConfig) -> (HashMap<String, String>, bool) {
        self.build_origin_headers(
            config,
            "https://www.youtube.com",
            "1",
            WEB_CLIENT_VERSION,
        )
    }

    pub fn build_music_headers(&self, config: &AppConfig) -> (HashMap<String, String>, bool) {
        self.build_origin_headers(
            config,
            "https://music.youtube.com",
            "67",
            MUSIC_CLIENT_VERSION,
        )
    }

    fn build_origin_headers(
        &self,
        config: &AppConfig,
        origin: &str,
        client_name_id: &str,
        client_version: &str,
    ) -> (HashMap<String, String>, bool) {
        let mut headers = HashMap::new();
        headers.insert("X-YouTube-Client-Name".to_string(), client_name_id.to_string());
        headers.insert("X-YouTube-Client-Version".to_string(), client_version.to_string());
        headers.insert("Origin".to_string(), origin.to_string());

        let cookie_header = self.resolve_cookie_header(config);
        if cookie_header.is_empty() {
            return (headers, false);
        }

        let has_auth = has_required_auth_cookies(&cookie_header);
        headers.insert("Cookie".to_string(), cookie_header.clone());
        headers.insert("X-Goog-AuthUser".to_string(), "0".to_string());

        if let Some(sapisid_hash) = build_sapisid_hash_header(&cookie_header, origin) {
            headers.insert("Authorization".to_string(), sapisid_hash);
        }

        (headers, has_auth)
    }

    pub fn get_account_profile(
        &self,
        config: &AppConfig,
        force_refresh: bool,
    ) -> GoogleAccountProfile {
        if !force_refresh {
            if let Ok(guard) = self.cached_profile.lock() {
                if let Some((ref prof, ts)) = *guard {
                    if ts.elapsed() < Duration::from_secs(300) {
                        return prof.clone();
                    }
                }
            }
        }

        let (headers, has_auth) = self.build_web_headers(config);
        if !has_auth {
            return GoogleAccountProfile::default();
        }

        let payload = format!(
            "{{\"context\":{{\"client\":{{\"clientName\":\"WEB\",\"clientVersion\":\"{}\",\"hl\":\"pt\",\"gl\":\"BR\"}}}}}}",
            WEB_CLIENT_VERSION
        );

        let json_res = crate::youtube::http_post_json(
            "https://www.youtube.com/youtubei/v1/account/account_menu?prettyPrint=false",
            &payload,
            Some("https://www.youtube.com/"),
            &headers,
        );

        let profile = match json_res {
            Some(ref json) if !json.is_empty() => parse_account_menu_response(json),
            _ => GoogleAccountProfile {
                is_authenticated: true,
                session_expired: false,
                display_name: "Conta Google".to_string(),
                email_or_handle: String::new(),
                avatar_url: String::new(),
            },
        };

        if profile.session_expired {
            self.mark_session_expired();
        } else if let Ok(mut g) = self.session_expired.lock() {
            *g = false;
        }

        if let Ok(mut guard) = self.cached_profile.lock() {
            *guard = Some((profile.clone(), Instant::now()));
        }

        profile
    }
}

pub fn parse_netscape_cookies_to_header(netscape_content: &str) -> String {
    let mut map: Vec<(String, String)> = Vec::new();
    for raw_line in netscape_content.lines() {
        let mut line = raw_line.trim();
        if line.is_empty() {
            continue;
        }
        if line.starts_with("#HttpOnly_") {
            line = &line["#HttpOnly_".len()..];
        } else if line.starts_with('#') {
            continue;
        }
        let parts: Vec<&str> = line.split('\t').collect();
        if parts.len() < 7 {
            continue;
        }
        let domain = parts[0].trim().to_ascii_lowercase();
        let name = parts[5].trim();
        let value = parts[6].trim();
        if name.is_empty() {
            continue;
        }
        if domain.contains("youtube.com") || domain.contains("google.com") {
            if let Some(existing) = map.iter_mut().find(|(k, _)| k == name) {
                existing.1 = value.to_string();
            } else {
                map.push((name.to_string(), value.to_string()));
            }
        }
    }

    map.into_iter()
        .map(|(k, v)| format!("{}={}", k, v))
        .collect::<Vec<_>>()
        .join("; ")
}

pub fn has_required_auth_cookies(cookie_header: &str) -> bool {
    let lower = cookie_header.to_ascii_lowercase();
    lower.contains("sapisid=")
        || lower.contains("__secure-3papisid=")
        || lower.contains("__secure-1papisid=")
        || lower.contains("ssid=")
        || lower.contains("__secure-3psid=")
        || lower.contains("login_info=")
}

pub fn extract_cookie_value(cookie_header: &str, cookie_name: &str) -> Option<String> {
    for part in cookie_header.split(';') {
        let trimmed = part.trim();
        if let Some((k, v)) = trimmed.split_once('=') {
            if k.trim().eq_ignore_ascii_case(cookie_name) && !v.trim().is_empty() {
                return Some(v.trim().to_string());
            }
        }
    }
    None
}

/// Constrói o cabeçalho oficial `Authorization: SAPISIDHASH <unix_ts>_<sha1_hex>`
pub fn build_sapisid_hash_header(cookie_header: &str, origin: &str) -> Option<String> {
    let sapisid = extract_cookie_value(cookie_header, "SAPISID")
        .or_else(|| extract_cookie_value(cookie_header, "__Secure-3PAPISID"))
        .or_else(|| extract_cookie_value(cookie_header, "__Secure-1PAPISID"))?;

    let ts = SystemTime::now()
        .duration_since(UNIX_EPOCH)
        .map(|d| d.as_secs())
        .unwrap_or(0);
    let input = format!("{} {} {}", ts, sapisid, origin);
    let mut hasher = Sha1::new();
    hasher.update(input.as_bytes());
    let digest = hasher.finalize();
    let hex_hash: String = digest.iter().map(|b| format!("{:02x}", b)).collect();

    Some(format!("SAPISIDHASH {}_{}", ts, hex_hash))
}

pub fn detect_logged_in_state(root: &Value) -> Option<bool> {
    let stp = root
        .get("responseContext")?
        .get("serviceTrackingParams")?
        .as_array()?;
    for svc in stp {
        if let Some(params) = svc.get("params").and_then(|p| p.as_array()) {
            for p in params {
                let key = p.get("key").and_then(|v| v.as_str()).unwrap_or("");
                let val = p.get("value").and_then(|v| v.as_str()).unwrap_or("");
                if key == "logged_in" {
                    return Some(val == "1");
                }
            }
        }
    }
    None
}

fn parse_account_menu_response(json: &str) -> GoogleAccountProfile {
    let mut profile = GoogleAccountProfile::default();
    let Ok(root) = serde_json::from_str::<Value>(json) else {
        return profile;
    };

    let logged_in = detect_logged_in_state(&root);
    let mut name = String::new();
    let mut handle = String::new();
    let mut avatar = String::new();

    extract_account_header_recursive(&root, &mut name, &mut handle, &mut avatar);

    if logged_in == Some(false) && name.is_empty() {
        profile.is_authenticated = false;
        profile.session_expired = true;
        return profile;
    }

    if !name.is_empty() || logged_in == Some(true) {
        profile.is_authenticated = true;
        profile.session_expired = false;
        profile.display_name = if name.is_empty() {
            "Conta Google".to_string()
        } else {
            name
        };
        profile.email_or_handle = handle;
        profile.avatar_url = avatar;
    } else {
        profile.is_authenticated = false;
        profile.session_expired = true;
    }

    profile
}

fn extract_account_header_recursive(
    el: &Value,
    name: &mut String,
    handle: &mut String,
    avatar: &mut String,
) {
    match el {
        Value::Object(map) => {
            if let Some(header) = map.get("activeAccountHeaderRenderer") {
                if name.is_empty() {
                    *name = read_json_text(header, "accountName");
                }
                if handle.is_empty() {
                    *handle = read_json_text(header, "channelHandle");
                    if handle.is_empty() {
                        *handle = read_json_text(header, "email");
                    }
                }
                if avatar.is_empty() {
                    if let Some(photo) = header.get("accountPhoto") {
                        *avatar = extract_thumbnail_url(photo);
                    }
                }
                return;
            }
            for val in map.values() {
                extract_account_header_recursive(val, name, handle, avatar);
                if !name.is_empty() {
                    return;
                }
            }
        }
        Value::Array(arr) => {
            for item in arr {
                extract_account_header_recursive(item, name, handle, avatar);
                if !name.is_empty() {
                    return;
                }
            }
        }
        _ => {}
    }
}

fn read_json_text(obj: &Value, prop: &str) -> String {
    let Some(el) = obj.get(prop) else {
        return String::new();
    };
    if let Some(s) = el.as_str() {
        return s.to_string();
    }
    if let Some(st) = el.get("simpleText").and_then(|v| v.as_str()) {
        return st.to_string();
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

fn extract_thumbnail_url(photo_obj: &Value) -> String {
    if let Some(arr) = photo_obj.get("thumbnails").and_then(|v| v.as_array()) {
        let mut last = String::new();
        for item in arr {
            if let Some(u) = item.get("url").and_then(|v| v.as_str()) {
                if !u.is_empty() {
                    last = u.to_string();
                }
            }
        }
        if last.starts_with("//") {
            return format!("https:{}", last);
        }
        return last;
    }
    String::new()
}

/// Extrai cookies de um navegador instalado (firefox, chrome, edge, brave) usando yt-dlp e salva no cofre DPAPI.
pub fn extract_cookies_from_browser(browser: &str) -> Result<usize, String> {
    let yt_dlp = get_base_dir().join("bin").join("yt-dlp.exe");
    if !yt_dlp.exists() {
        return Err("Executável bin\\yt-dlp.exe não encontrado.".to_string());
    }
    let nanos = SystemTime::now()
        .duration_since(UNIX_EPOCH)
        .map(|d| d.as_nanos())
        .unwrap_or(0);
    let temp_out = std::env::temp_dir().join(format!("litetube_ck_{}.txt", nanos));

    let output = Command::new(&yt_dlp)
        .args([
            "--no-config",
            "--cookies-from-browser",
            browser,
            "--cookies",
            &temp_out.to_string_lossy(),
            "--skip-download",
            "--no-warnings",
            "https://www.youtube.com/",
        ])
        .creation_flags(CREATE_NO_WINDOW)
        .output()
        .map_err(|e| format!("Falha ao executar yt-dlp: {}", e))?;

    let res = if temp_out.exists() && fs::metadata(&temp_out).map(|m| m.len()).unwrap_or(0) > 100 {
        let raw = fs::read_to_string(&temp_out).unwrap_or_default();
        let count = raw.lines().filter(|l| l.split('\t').count() >= 7).count();
        if save_protected_cookies(&raw) {
            Ok(count)
        } else {
            Err("Falha ao salvar no cofre Windows DPAPI.".to_string())
        }
    } else {
        let stderr = String::from_utf8_lossy(&output.stderr);
        if stderr.to_ascii_lowercase().contains("could not copy")
            || stderr.to_ascii_lowercase().contains("permission")
        {
            Err(format!(
                "O {} bloqueia o banco de cookies enquanto está aberto. Feche o {} por 3 segundos e tente novamente, ou cole o conteúdo cookies.txt.",
                browser.to_uppercase(),
                browser.to_uppercase()
            ))
        } else {
            Err(format!(
                "Não foi possível extrair cookies do {}. Verifique se você está logado no YouTube nele.",
                browser.to_uppercase()
            ))
        }
    };

    let _ = fs::remove_file(&temp_out);
    res
}
