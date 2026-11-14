//! Audio from the WaveFix app into the mic, mixed inside the audio engine.
//!
//! The app (soundboard, later AI voices) can't simply play sound: anything an app plays is what
//! Discord's stream audio records. Instead it writes samples into a small memory-mapped file,
//! C:\ProgramData\WaveFix\Engine\bridge.bin, and this plugin, running in audiodg.exe, mixes them
//! into the mic. The file (not a named section) is used because creating Global\ objects needs a
//! privilege the app doesn't have; the app creates it and grants LOCAL SERVICE access.
//!
//! No threads: the plugin's DLL may be unloaded when Equalizer APO reloads its chain, and a
//! thread left running in an unloaded DLL would crash the audio engine. Attaching happens on
//! the host's non-real-time calls, plus a rare retry from the audio thread.
//!
//! Layout (little endian; must match src/WaveFix/Audio/Bridge.cs):
//!   0  u32 magic 'WFBR'          4  u32 version (1)
//!   8  u32 plugin sample rate    12 u32 ring length (samples, power of two)
//!   16 u64 consumer position (absolute sample index where the plugin's current block starts)
//!   24 u64 write position (the app has written samples up to here, exclusive)
//!   32 u64 plugin heartbeat (GetTickCount64 ms)
//!   40 u64 app heartbeat
//!   64 f32 ring[ring length]

use std::ffi::c_void;
use std::sync::atomic::{AtomicPtr, AtomicU32, AtomicU64, Ordering};

pub const MAGIC: u32 = 0x5242_4657; // "WFBR"
pub const VERSION: u32 = 1;
pub const HEADER: usize = 64;
const MAX_RING: u32 = 1 << 22;

#[allow(non_snake_case)]
#[link(name = "kernel32")]
unsafe extern "system" {
    fn CreateFileW(name: *const u16, access: u32, share: u32, sa: *mut c_void, disposition: u32, flags: u32, template: *mut c_void) -> *mut c_void;
    fn GetFileSizeEx(file: *mut c_void, size: *mut i64) -> i32;
    fn CreateFileMappingW(file: *mut c_void, sa: *mut c_void, protect: u32, high: u32, low: u32, name: *const u16) -> *mut c_void;
    fn MapViewOfFile(mapping: *mut c_void, access: u32, high: u32, low: u32, bytes: usize) -> *mut c_void;
    fn UnmapViewOfFile(view: *const c_void) -> i32;
    fn CloseHandle(h: *mut c_void) -> i32;
    fn GetTickCount64() -> u64;
}

const GENERIC_READ: u32 = 0x8000_0000;
const GENERIC_WRITE: u32 = 0x4000_0000;
const FILE_SHARE_ALL: u32 = 0x7;
const OPEN_EXISTING: u32 = 3;
const PAGE_READWRITE: u32 = 0x04;
const FILE_MAP_ALL_ACCESS: u32 = 0x000F_001F;
const INVALID_HANDLE: *mut c_void = -1isize as *mut c_void;

/// The process-wide mapping (shared by every plugin instance, never unmapped: it's 1 MB and
/// lives until audiodg.exe restarts, which is harmless and avoids use-after-unmap races).
static VIEW: AtomicPtr<u8> = AtomicPtr::new(std::ptr::null_mut());
static LAST_TRY: AtomicU64 = AtomicU64::new(0);

pub fn now_ms() -> u64 { unsafe { GetTickCount64() } }

fn bridge_path() -> Vec<u16> {
    let base = std::env::var("ProgramData").unwrap_or_else(|_| r"C:\ProgramData".into());
    let path = format!(r"{base}\WaveFix\Engine\bridge.bin");
    path.encode_utf16().chain(std::iter::once(0)).collect()
}

/// Maps the bridge file if the app has created it. Cheap when already attached.
pub fn attach() -> Option<&'static Header> {
    let p = VIEW.load(Ordering::Acquire);
    if !p.is_null() { return Some(unsafe { &*(p as *const Header) }); }
    let path = bridge_path();
    unsafe {
        let file = CreateFileW(path.as_ptr(), GENERIC_READ | GENERIC_WRITE, FILE_SHARE_ALL, std::ptr::null_mut(), OPEN_EXISTING, 0, std::ptr::null_mut());
        if file == INVALID_HANDLE || file.is_null() { return None; }
        let mut size = 0i64;
        let ok = GetFileSizeEx(file, &mut size) != 0;
        if !ok || size < (HEADER + 4096) as i64 || size > (HEADER as i64 + 4 * MAX_RING as i64) { CloseHandle(file); return None; }
        let mapping = CreateFileMappingW(file, std::ptr::null_mut(), PAGE_READWRITE, 0, 0, std::ptr::null());
        CloseHandle(file);
        if mapping.is_null() { return None; }
        let view = MapViewOfFile(mapping, FILE_MAP_ALL_ACCESS, 0, 0, 0) as *mut u8;
        CloseHandle(mapping); // the view keeps the mapping alive
        if view.is_null() { return None; }
        let h = &*(view as *const Header);
        let len = h.ring_len.load(Ordering::Acquire);
        if h.magic.load(Ordering::Acquire) != MAGIC || h.version.load(Ordering::Acquire) != VERSION
            || !len.is_power_of_two() || len > MAX_RING || (HEADER as i64 + 4 * len as i64) > size
        {
            UnmapViewOfFile(view as *const c_void); // not ready, or another version: retry later
            return None;
        }
        match VIEW.compare_exchange(std::ptr::null_mut(), view, Ordering::AcqRel, Ordering::Acquire) {
            Ok(_) => Some(h),
            Err(existing) => {
                UnmapViewOfFile(view as *const c_void);
                Some(&*(existing as *const Header))
            }
        }
    }
}

/// From the audio thread: attached view, or one attach attempt every few seconds.
pub fn attach_rt() -> Option<&'static Header> {
    let p = VIEW.load(Ordering::Acquire);
    if !p.is_null() { return Some(unsafe { &*(p as *const Header) }); }
    let now = now_ms();
    let last = LAST_TRY.load(Ordering::Relaxed);
    if now.wrapping_sub(last) < 3000 { return None; }
    LAST_TRY.store(now, Ordering::Relaxed);
    attach()
}

#[repr(C)]
pub struct Header {
    magic: AtomicU32,
    version: AtomicU32,
    pub rate: AtomicU32,
    ring_len: AtomicU32,
    pub consumer: AtomicU64,
    pub write: AtomicU64,
    pub plugin_beat: AtomicU64,
    pub app_beat: AtomicU64,
}

impl Header {
    /// Sets up a header in caller-owned memory of at least HEADER + 4 * ring_len bytes, 8-aligned
    /// (the app does the same from C#; tests use this).
    ///
    /// # Safety
    /// `mem` must be valid, aligned and large enough, and outlive every use of the result.
    pub unsafe fn init<'a>(mem: *mut u8, ring_len: u32) -> &'a Header {
        let h = unsafe { &*(mem as *const Header) };
        h.ring_len.store(ring_len, Ordering::Relaxed);
        h.version.store(VERSION, Ordering::Relaxed);
        h.magic.store(MAGIC, Ordering::Release);
        h
    }

    /// Writes app audio at absolute sample positions [start, start + data.len()) (test helper
    /// mirroring what Bridge.cs does).
    pub fn write_at(&self, start: u64, data: &[f32]) {
        let (ring, len) = self.ring();
        for (i, v) in data.iter().enumerate() {
            unsafe { *(ring as *mut f32).add(((start + i as u64) & (len - 1)) as usize) = *v; }
        }
        self.write.store(start + data.len() as u64, Ordering::Release);
    }

    fn ring(&self) -> (*const f32, u64) {
        let len = self.ring_len.load(Ordering::Relaxed) as u64;
        (unsafe { (self as *const Header as *const u8).add(HEADER) as *const f32 }, len)
    }
}

/// One plugin instance's read position. Each instance (one per mic channel) reads the same
/// stream, so a stereo mic gets the sound in both channels.
#[derive(Default)]
pub struct Reader { cursor: u64, started: bool }

impl Reader {
    /// Adds the app's audio for this block into `buf` (scaled by `gain`).
    pub fn mix(&mut self, h: &Header, rate: u32, buf: &mut [f32], gain: f32) {
        h.rate.store(rate, Ordering::Relaxed);
        h.plugin_beat.store(now_ms(), Ordering::Relaxed);
        let (ring, len) = h.ring();
        let write = h.write.load(Ordering::Acquire);
        let consumer = h.consumer.load(Ordering::Acquire);
        // Start (or resync after a long gap) where the other instances are. `consumer` is the
        // start of the block they're on, so a new instance lines up with them exactly.
        if !self.started || self.cursor + len < consumer || self.cursor > consumer + len {
            self.cursor = consumer.max(write.saturating_sub(len));
            self.started = true;
        }
        h.consumer.fetch_max(self.cursor, Ordering::AcqRel);
        let mask = len - 1;
        for (i, s) in buf.iter_mut().enumerate() {
            let pos = self.cursor + i as u64;
            // Only samples the app has written and hasn't since overwritten.
            if pos < write && pos + len > write {
                let v = unsafe { *ring.add((pos & mask) as usize) };
                if v.is_finite() { *s += v.clamp(-4.0, 4.0) * gain; }
            }
        }
        self.cursor += buf.len() as u64;
    }
}
