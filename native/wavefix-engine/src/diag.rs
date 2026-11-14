//! Diagnostics in the Windows Event Log (Application log, source "WaveFix Engine").
//!
//! Windows' audio service runs the engine under a restricted account that may not be able to
//! write files, but the Event Log reserves write access for exactly such processes. Read it with:
//!   Get-WinEvent -ProviderName 'WaveFix Engine' -MaxEvents 50 | Format-List TimeCreated,Message
//! Never called from the real-time audio thread (it's a call into another service).

use std::sync::atomic::{AtomicU32, Ordering};
use std::sync::OnceLock;

use windows::Win32::Foundation::HANDLE;
use windows::Win32::System::EventLog::{RegisterEventSourceW, ReportEventW, EVENTLOG_ERROR_TYPE, EVENTLOG_INFORMATION_TYPE, EVENTLOG_WARNING_TYPE, REPORT_EVENT_TYPE};
use windows::core::{w, HSTRING, PCWSTR};

pub enum Level { Info, Warning, Error }

static SOURCE: OnceLock<isize> = OnceLock::new();
/// Caps the number of events per process so a misbehaving host can't flood the log.
static COUNT: AtomicU32 = AtomicU32::new(0);
const MAX_EVENTS: u32 = 400;

fn source() -> HANDLE {
    let h = *SOURCE.get_or_init(|| unsafe { RegisterEventSourceW(None, w!("WaveFix Engine")) }.map(|h| h.0 as isize).unwrap_or(0));
    HANDLE(h as *mut _)
}

pub fn log(level: Level, msg: &str) {
    if COUNT.fetch_add(1, Ordering::Relaxed) >= MAX_EVENTS { return; }
    let h = source();
    if h.is_invalid() || h.0.is_null() { return; }
    let kind: REPORT_EVENT_TYPE = match level { Level::Info => EVENTLOG_INFORMATION_TYPE, Level::Warning => EVENTLOG_WARNING_TYPE, Level::Error => EVENTLOG_ERROR_TYPE };
    let text = HSTRING::from(format!("[pid {}] {}", std::process::id(), msg));
    let strings = [PCWSTR(text.as_ptr())];
    // Event 1000 with one string: the message file setup registers (.NET's EventLogMessages.dll)
    // shows it as is.
    let _ = unsafe { ReportEventW(h, kind, 0, 1000, None, 0, Some(&strings), None) };
}

pub fn info(msg: &str) { log(Level::Info, msg) }
pub fn warn(msg: &str) { log(Level::Warning, msg) }
pub fn error(msg: &str) { log(Level::Error, msg) }
