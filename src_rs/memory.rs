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
use windows::Win32::Foundation::{CloseHandle, HANDLE, HWND, LPARAM};
use windows::Win32::Graphics::Dwm::{DWMWA_CAPTION_COLOR, DWMWA_TEXT_COLOR, DWMWA_USE_IMMERSIVE_DARK_MODE, DwmSetWindowAttribute};
use windows::Win32::Storage::FileSystem::{PIPE_ACCESS_DUPLEX, ReadFile};
use windows::Win32::System::Pipes::{
    ConnectNamedPipe, CreateNamedPipeW, DisconnectNamedPipe, PIPE_READMODE_BYTE, PIPE_TYPE_BYTE,
    PIPE_WAIT,
};
use windows::Win32::System::ProcessStatus::{K32GetProcessMemoryInfo, PROCESS_MEMORY_COUNTERS};
use windows::Win32::System::Registry::{
    HKEY, HKEY_CURRENT_USER, KEY_WRITE, REG_OPTION_NON_VOLATILE, REG_SZ, RegCloseKey,
    RegCreateKeyExW, RegSetValueExW,
};
use windows::Win32::System::Threading::{GetCurrentProcess, GetCurrentProcessId};
use windows::Win32::UI::WindowsAndMessaging::{
    EnumWindows, GetForegroundWindow, GetWindowThreadProcessId, IsIconic, IsWindow,
    IsWindowVisible, SW_RESTORE, SetForegroundWindow, ShowWindow,
};
use windows_core::{BOOL, PCWSTR};

static CACHED_MAIN_HWND: std::sync::atomic::AtomicIsize = std::sync::atomic::AtomicIsize::new(0);

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

/// Compacta recursos sem despejar o Working Set do driver OpenGL/WGL (evita tela preta ou
/// congelamento ao restaurar a janela após reprodução prolongada em segundo plano).
pub fn trim_memory() {
    // Intencionalmente não invoca K32EmptyWorkingSet durante a execução da UI, pois
    // forçar page-out do driver OpenGL/WGL (nvoglv64/AMD/Intel) congela o SwapBuffers ao voltar do background.
}

fn resolve_main_process_hwnd() -> Option<HWND> {
    let cached = CACHED_MAIN_HWND.load(std::sync::atomic::Ordering::Acquire);
    if cached != 0 {
        let h = HWND(cached as *mut std::ffi::c_void);
        if unsafe { IsWindow(Some(h)).as_bool() } {
            return Some(h);
        }
        CACHED_MAIN_HWND.store(0, std::sync::atomic::Ordering::Release);
    }

    unsafe extern "system" fn find_cb(hwnd: HWND, lparam: LPARAM) -> BOOL {
        unsafe {
            let target_pid = lparam.0 as u32;
            let mut win_pid = 0u32;
            GetWindowThreadProcessId(hwnd, Some(&mut win_pid));
            if win_pid == target_pid && IsWindowVisible(hwnd).as_bool() {
                CACHED_MAIN_HWND.store(hwnd.0 as isize, std::sync::atomic::Ordering::Release);
                return BOOL(0);
            }
            BOOL(1)
        }
    }

    unsafe {
        let pid = GetCurrentProcessId();
        let _ = EnumWindows(Some(find_cb), LPARAM(pid as isize));
    }
    let found = CACHED_MAIN_HWND.load(std::sync::atomic::Ordering::Acquire);
    if found != 0 {
        Some(HWND(found as *mut std::ffi::c_void))
    } else {
        None
    }
}

/// Retorna `true` se a janela principal do LiteTube estiver minimizada na barra de tarefas (`IsIconic`).
pub fn is_current_process_window_minimized() -> bool {
    if let Some(hwnd) = resolve_main_process_hwnd() {
        unsafe { IsIconic(hwnd).as_bool() }
    } else {
        false
    }
}

/// Retorna `true` se a janela principal do LiteTube estiver em primeiro plano (ativa no foco do Windows).
pub fn is_current_process_window_foreground() -> bool {
    if let Some(hwnd) = resolve_main_process_hwnd() {
        unsafe {
            if IsIconic(hwnd).as_bool() {
                return false;
            }
            GetForegroundWindow() == hwnd
        }
    } else {
        true
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
                CACHED_MAIN_HWND.store(hwnd.0 as isize, std::sync::atomic::Ordering::Release);
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

// ============================================================================
// Integração Externa (Protocolo litetube:// + Named Pipe IPC Instância Única)
// ============================================================================

pub const LITETUBE_PIPE_NAME: &str = r"\\.\pipe\LiteTubeIPC";

fn to_wide_null(s: &str) -> Vec<u16> {
    s.encode_utf16().chain(std::iter::once(0)).collect()
}

fn reg_set_sz(hkey: HKEY, name: Option<&str>, value: &str) {
    let name_w = name.map(to_wide_null);
    let val_w = to_wide_null(value);
    let bytes: &[u8] = unsafe {
        std::slice::from_raw_parts(val_w.as_ptr() as *const u8, val_w.len() * 2)
    };
    unsafe {
        let _ = RegSetValueExW(
            hkey,
            match name_w {
                Some(ref w) => PCWSTR(w.as_ptr()),
                None => PCWSTR::null(),
            },
            Some(0),
            REG_SZ,
            Some(bytes),
        );
    }
}

/// Registra/atualiza o protocolo `litetube://` em HKCU\Software\Classes\litetube
/// apontando para o executável atual (sem exigir privilégios de administrador).
pub fn register_litetube_protocol() {
    let root_exe = get_base_dir().join("LiteTube.exe");
    let exe_path = if root_exe.exists() {
        root_exe
    } else if let Ok(p) = std::env::current_exe() {
        p
    } else {
        return;
    };
    let exe_str = exe_path.to_string_lossy().into_owned();
    let cmd_str = format!("\"{}\" \"%1\"", exe_str);
    let icon_str = format!("\"{}\",0", exe_str);

    unsafe {
        let sub_root = to_wide_null(r"Software\Classes\litetube");
        let mut h_root = HKEY::default();
        if RegCreateKeyExW(
            HKEY_CURRENT_USER,
            PCWSTR(sub_root.as_ptr()),
            Some(0),
            PCWSTR::null(),
            REG_OPTION_NON_VOLATILE,
            KEY_WRITE,
            None,
            &mut h_root,
            None,
        )
        .is_ok()
        {
            reg_set_sz(h_root, None, "URL:LiteTube Protocol");
            reg_set_sz(h_root, Some("URL Protocol"), "");
            let _ = RegCloseKey(h_root);
        }

        let sub_icon = to_wide_null(r"Software\Classes\litetube\DefaultIcon");
        let mut h_icon = HKEY::default();
        if RegCreateKeyExW(
            HKEY_CURRENT_USER,
            PCWSTR(sub_icon.as_ptr()),
            Some(0),
            PCWSTR::null(),
            REG_OPTION_NON_VOLATILE,
            KEY_WRITE,
            None,
            &mut h_icon,
            None,
        )
        .is_ok()
        {
            reg_set_sz(h_icon, None, &icon_str);
            let _ = RegCloseKey(h_icon);
        }

        let sub_cmd = to_wide_null(r"Software\Classes\litetube\shell\open\command");
        let mut h_cmd = HKEY::default();
        if RegCreateKeyExW(
            HKEY_CURRENT_USER,
            PCWSTR(sub_cmd.as_ptr()),
            Some(0),
            PCWSTR::null(),
            REG_OPTION_NON_VOLATILE,
            KEY_WRITE,
            None,
            &mut h_cmd,
            None,
        )
        .is_ok()
        {
            reg_set_sz(h_cmd, None, &cmd_str);
            let _ = RegCloseKey(h_cmd);
        }
    }
}

/// Restaura e traz a janela principal do processo atual para primeiro plano.
pub fn bring_current_process_window_to_front() {
    unsafe extern "system" fn enum_cb(hwnd: HWND, lparam: LPARAM) -> BOOL {
        unsafe {
            let target_pid = lparam.0 as u32;
            let mut win_pid = 0u32;
            GetWindowThreadProcessId(hwnd, Some(&mut win_pid));
            if win_pid == target_pid && IsWindowVisible(hwnd).as_bool() {
                if IsIconic(hwnd).as_bool() {
                    let _ = ShowWindow(hwnd, SW_RESTORE);
                }
                let _ = SetForegroundWindow(hwnd);
                return BOOL(0);
            }
            BOOL(1)
        }
    }

    unsafe {
        let pid = GetCurrentProcessId();
        let _ = EnumWindows(Some(enum_cb), LPARAM(pid as isize));
    }
}

/// Tenta enviar uma URL de vídeo para uma instância já em execução do LiteTube via Named Pipe.
/// Retorna `true` se uma instância ativa recebeu o comando (evitando abrir segunda janela).
pub fn try_send_ipc_play_url(url: &str) -> bool {
    let trimmed = url.trim();
    if trimmed.is_empty() {
        return false;
    }
    if let Ok(mut pipe) = OpenOptions::new().read(true).write(true).open(LITETUBE_PIPE_NAME) {
        let payload = format!("PLAY {}\n", trimmed);
        if pipe.write_all(payload.as_bytes()).is_ok() && pipe.flush().is_ok() {
            return true;
        }
    }
    false
}

/// Inicia o servidor Named Pipe em thread background para receber URLs externas
/// (ex.: Cortex ou protocolo `litetube://` em instância única).
pub fn start_ipc_pipe_server<F>(on_play_url: F)
where
    F: Fn(String) + Send + 'static,
{
    std::thread::spawn(move || {
        let pipe_w = to_wide_null(LITETUBE_PIPE_NAME);
        loop {
            let handle = unsafe {
                CreateNamedPipeW(
                    PCWSTR(pipe_w.as_ptr()),
                    PIPE_ACCESS_DUPLEX,
                    PIPE_TYPE_BYTE | PIPE_READMODE_BYTE | PIPE_WAIT,
                    4,
                    4096,
                    4096,
                    0,
                    None,
                )
            };
            if handle.is_invalid() {
                std::thread::sleep(std::time::Duration::from_millis(500));
                continue;
            }

            let connected = unsafe { ConnectNamedPipe(handle, None) };
            // ConnectNamedPipe retorna Err(ERROR_PIPE_CONNECTED) quando o cliente conectou antes
            let ok = connected.is_ok()
                || connected
                    .err()
                    .map(|e| e.code().0 == 0x80070217u32 as i32)
                    .unwrap_or(false);

            if ok {
                let mut buf = [0u8; 4096];
                let mut read_bytes = 0u32;
                let res = unsafe { ReadFile(handle, Some(&mut buf), Some(&mut read_bytes), None) };
                if res.is_ok() && read_bytes > 0 {
                    let raw = String::from_utf8_lossy(&buf[..read_bytes as usize]);
                    for line in raw.lines() {
                        let line = line.trim();
                        if line.is_empty() {
                            continue;
                        }
                        if let Some(rest) = line.strip_prefix("PLAY ").or_else(|| line.strip_prefix("play ")) {
                            let url = rest.trim();
                            if !url.is_empty() {
                                log_info("IPC", &format!("Comando PLAY recebido via NamedPipe: {}", url));
                                on_play_url(url.to_string());
                            }
                        } else if line.starts_with("http://")
                            || line.starts_with("https://")
                            || line.to_ascii_lowercase().starts_with("litetube://")
                        {
                            log_info("IPC", &format!("URL direta recebida via NamedPipe: {}", line));
                            on_play_url(line.to_string());
                        }
                    }
                }
                unsafe {
                    let _ = DisconnectNamedPipe(handle);
                }
            }

            unsafe {
                let _ = CloseHandle(handle);
            }
        }
    });
}
