// ============================================================================
// LiteTube 2.0 (Rust) — Camada 5: Gestão de Recursos, RAM Real, Logger & IPC
// ============================================================================

#![allow(dead_code)]

use crate::models::get_base_dir;
use regex::Regex;
use std::fs::{self, OpenOptions};
use std::io::Write;
use std::mem::size_of;
use std::sync::{Mutex, OnceLock};
use std::time::{SystemTime, UNIX_EPOCH};
use windows::Win32::Foundation::{HANDLE, HWND, LPARAM};
use windows_core::BOOL;
use windows::Win32::Graphics::Dwm::{DWMWA_CAPTION_COLOR, DWMWA_TEXT_COLOR, DWMWA_USE_IMMERSIVE_DARK_MODE, DwmSetWindowAttribute};
use windows::Win32::System::ProcessStatus::{K32EmptyWorkingSet, K32GetProcessMemoryInfo, PROCESS_MEMORY_COUNTERS};
use windows::Win32::System::Threading::{GetCurrentProcess, GetCurrentProcessId};
use windows::Win32::UI::WindowsAndMessaging::{EnumWindows, GetWindowThreadProcessId, IsWindowVisible};

#[repr(C)]
#[derive(Default)]
struct ProcessMemoryCountersEx2 {
    cb: u32,
    page_fault_count: u32,
    peak_working_set_size: usize,
    working_set_size: usize,
    quota_peak_paged_pool_usage: usize,
    quota_paged_pool_usage: usize,
    quota_peak_non_paged_pool_usage: usize,
    quota_non_paged_pool_usage: usize,
    pagefile_usage: usize,
    peak_pagefile_usage: usize,
    private_usage: usize,
    private_working_set_size: usize,
    shared_commit_usage: u64,
}

/// Retorna a memória RAM privada ativa (Private Working Set) exata que o
/// Gerenciador de Tarefas do Windows exibe, em Megabytes (MB).
pub fn get_current_ram_usage_mb() -> f64 {
    unsafe {
        let proc: HANDLE = GetCurrentProcess();
        let mut counters2 = ProcessMemoryCountersEx2 {
            cb: size_of::<ProcessMemoryCountersEx2>() as u32,
            ..Default::default()
        };

        let ok = K32GetProcessMemoryInfo(
            proc,
            &mut counters2 as *mut _ as *mut PROCESS_MEMORY_COUNTERS,
            counters2.cb,
        );

        if ok.as_bool() {
            if counters2.private_working_set_size > 0 {
                return counters2.private_working_set_size as f64 / (1024.0 * 1024.0);
            }
            if counters2.working_set_size > 0 {
                return counters2.working_set_size as f64 / (1024.0 * 1024.0);
            }
        }
    }
    0.0
}

/// Compacta o Working Set do processo nativo Rust no Windows.
pub fn trim_memory() {
    unsafe {
        let proc = GetCurrentProcess();
        let _ = K32EmptyWorkingSet(proc);
    }
}

/// Aplica a Barra de Título Escura nativa do Windows 11 (#0F0F0F / texto #F1F1F1)
/// idêntica ao WpfMainWindow.ApplyDarkTitleBar.
pub fn apply_dark_title_bar_to_current_process() {
    unsafe extern "system" fn enum_cb(hwnd: HWND, lparam: LPARAM) -> BOOL {
        unsafe {
            let target_pid = lparam.0 as u32;
            let mut win_pid = 0u32;
            GetWindowThreadProcessId(hwnd, Some(&mut win_pid));
            if win_pid == target_pid && IsWindowVisible(hwnd).as_bool() {
                let dark: i32 = 1;
                let caption_color: u32 = 0x000F0F0F; // COLORREF #0F0F0F
                let text_color: u32 = 0x00F1F1F1;    // COLORREF #F1F1F1
                let _ = DwmSetWindowAttribute(
                    hwnd,
                    DWMWA_USE_IMMERSIVE_DARK_MODE,
                    &dark as *const _ as *const _,
                    size_of::<i32>() as u32,
                );
                let _ = DwmSetWindowAttribute(
                    hwnd,
                    DWMWA_CAPTION_COLOR,
                    &caption_color as *const _ as *const _,
                    size_of::<u32>() as u32,
                );
                let _ = DwmSetWindowAttribute(
                    hwnd,
                    DWMWA_TEXT_COLOR,
                    &text_color as *const _ as *const _,
                    size_of::<u32>() as u32,
                );
            }
            BOOL(1)
        }
    }

    unsafe {
        let pid = GetCurrentProcessId();
        let _ = EnumWindows(Some(enum_cb), LPARAM(pid as isize));
    }
}

// ============================================================================
// Logger Assíncrono com Sanitização Automática (Regra de Ouro 3.4 do GEMINI.md)
// ============================================================================

static LOG_MUTEX: OnceLock<Mutex<()>> = OnceLock::new();
static RX_SAPISID: OnceLock<Regex> = OnceLock::new();
static RX_HEADERS: OnceLock<Regex> = OnceLock::new();
static RX_COOKIES: OnceLock<Regex> = OnceLock::new();

pub fn sanitize_sensitive_data(msg: &str) -> String {
    if msg.is_empty() {
        return String::new();
    }
    let mut safe = msg.to_string();
    let lower = safe.to_ascii_lowercase();

    if lower.contains("sapisidhash") {
        let rx = RX_SAPISID.get_or_init(|| {
            Regex::new(r"(?i)SAPISIDHASH\s+[^\s\x22';,]+").unwrap()
        });
        safe = rx.replace_all(&safe, "SAPISIDHASH [REDACTED]").into_owned();
    }

    if lower.contains("authorization") || lower.contains("cookie") {
        let rx = RX_HEADERS.get_or_init(|| {
            Regex::new(r"(?i)\b(Authorization|Cookie|Set-Cookie)\s*:\s*[^\r\n]+").unwrap()
        });
        safe = rx.replace_all(&safe, "$1: [REDACTED]").into_owned();
    }

    if lower.contains("sid") || lower.contains("login_info") {
        let rx = RX_COOKIES.get_or_init(|| {
            Regex::new(r"(?i)\b(__Secure-3PAPISID|__Secure-1PAPISID|__Secure-3PSID|__Secure-1PSID|__Secure-3PSIDCC|__Secure-1PSIDCC|SAPISID|APISID|HSID|SSID|SID|LOGIN_INFO)\s*=\s*[^;\s\x22'&\r\n]+").unwrap()
        });
        safe = rx.replace_all(&safe, "$1=[REDACTED]").into_owned();
    }

    safe
}

pub fn log_info(category: &str, msg: &str) {
    write_log("INFO", category, msg);
}

pub fn log_warn(category: &str, msg: &str) {
    write_log("WARN", category, msg);
}

pub fn log_error(category: &str, msg: &str) {
    write_log("ERROR", category, msg);
}

pub fn init_logger() {
    let logs_dir = get_base_dir().join("logs");
    let _ = fs::create_dir_all(&logs_dir);
    let latest = logs_dir.join("litetube_latest.log");
    let _ = fs::write(&latest, "");
    log_info(
        "LOGGER",
        "================================================================================",
    );
    log_info(
        "LOGGER",
        "LiteTube 2.0 (100% Rust Nativo) iniciado com sucesso.",
    );
    log_info(
        "LOGGER",
        &format!("Diretório base: {}", get_base_dir().display()),
    );
    log_info(
        "LOGGER",
        &format!("RAM inicial (PrivateWorkingSet): {:.1} MB", get_current_ram_usage_mb()),
    );
    log_info(
        "LOGGER",
        "================================================================================",
    );
}

fn write_log(level: &str, category: &str, msg: &str) {
    let mtx = LOG_MUTEX.get_or_init(|| Mutex::new(()));
    let _guard = mtx.lock();

    let clean = sanitize_sensitive_data(msg);
    let secs = SystemTime::now()
        .duration_since(UNIX_EPOCH)
        .map(|d| d.as_secs())
        .unwrap_or(0);
    let line = format!("[{}] [{}] [{}] {}\r\n", secs, level, category, clean);

    let logs_dir = get_base_dir().join("logs");
    let latest = logs_dir.join("litetube_latest.log");
    if let Ok(mut f) = OpenOptions::new().create(true).append(true).open(&latest) {
        let _ = f.write_all(line.as_bytes());
    }
}
