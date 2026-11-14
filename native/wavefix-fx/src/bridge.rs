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
//! Layout (little endian; must match src/WaveFix/Audio/Soundboard.cs, AudioBridge):
//!   0  u32 magic 'WFBR'          4  u32 version (2)
//!   8  u32 plugin sample rate    12 u32 ring length (samples, power of two)
//!   16 u64 soundboard consumer (absolute sample index where the plugin's current block starts)
//!   24 u64 soundboard write position (the app has written samples up to here, exclusive)
//!   32 u64 plugin heartbeat (GetTickCount64 ms)
//!   40 u64 app heartbeat
//!   AI voice (RVC), on its own timeline:
//!   48 u64 RVC clock (where the plugin's current block starts)
//!   56 u64 RVC input written (the plugin has written the voice up to here)
//!   64 u64 RVC output written (the engine has written converted voice up to here)
//!   72 u64 RVC engine heartbeat
//!   80 u32 RVC delay in samples (output for position p is played at p + delay)
//!   128 f32 soundboard ring, then RVC input ring, then RVC output ring (each ring length)

use std::ffi::c_void;
use std::sync::atomic::{AtomicPtr, AtomicU32, AtomicU64, Ordering};

pub const MAGIC: u32 = 0x5242_4657; // "WFBR"
pub const VERSION: u32 = 2;
pub const HEADER: usize = 128;
const RINGS: i64 = 3;
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
        if !ok || size < (HEADER + 4096) as i64 || size > (HEADER as i64 + RINGS * 4 * MAX_RING as i64) { CloseHandle(file); return None; }
        let mapping = CreateFileMappingW(file, std::ptr::null_mut(), PAGE_READWRITE, 0, 0, std::ptr::null());
        CloseHandle(file);
        if mapping.is_null() { return None; }
        let view = MapViewOfFile(mapping, FILE_MAP_ALL_ACCESS, 0, 0, 0) as *mut u8;
        CloseHandle(mapping); // the view keeps the mapping alive
        if view.is_null() { return None; }
        let h = &*(view as *const Header);
        let len = h.ring_len.load(Ordering::Acquire);
        if h.magic.load(Ordering::Acquire) != MAGIC || h.version.load(Ordering::Acquire) != VERSION
            || !len.is_power_of_two() || len > MAX_RING || (HEADER as i64 + RINGS * 4 * len as i64) > size
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
    pub rvc_clock: AtomicU64,
    pub rvc_in_write: AtomicU64,
    pub rvc_out_write: AtomicU64,
    pub rvc_engine_beat: AtomicU64,
    pub rvc_delay: AtomicU32,
}

/// Bytes of shared memory needed for a ring length (header + three rings).
pub const fn bytes_for(ring_len: u32) -> usize { HEADER + 3 * 4 * ring_len as usize }

impl Header {
    /// Sets up a header in caller-owned memory of at least `bytes_for(ring_len)` bytes, 8-aligned
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

    fn ring(&self) -> (*const f32, u64) { self.ring_n(0) }

    /// Ring 0 = soundboard, 1 = RVC input, 2 = RVC output.
    fn ring_n(&self, n: usize) -> (*const f32, u64) {
        let len = self.ring_len.load(Ordering::Relaxed) as u64;
        (unsafe { (self as *const Header as *const u8).add(HEADER + n * 4 * len as usize) as *const f32 }, len)
    }

    /// Test helper standing in for the app's RVC engine: reads the voice the plugin sent for
    /// [start, end) and writes `f(sample)` back as the converted voice.
    pub fn engine_convert(&self, start: u64, end: u64, delay: u32, f: impl Fn(f32) -> f32) {
        let (inp, len) = self.ring_n(1);
        let (out, _) = self.ring_n(2);
        let mask = len - 1;
        for pos in start..end {
            unsafe { *(out as *mut f32).add((pos & mask) as usize) = f(*inp.add((pos & mask) as usize)); }
        }
        self.rvc_delay.store(delay, Ordering::Relaxed);
        self.rvc_out_write.store(end, Ordering::Release);
        self.rvc_engine_beat.store(now_ms(), Ordering::Relaxed);
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

/// One plugin instance's link to the AI voice engine. It writes the voice into the input ring
/// and plays the converted voice from the output ring `delay` samples later. When the engine
/// isn't keeping up (or isn't running) it fades to the real voice, or to silence if asked.
#[derive(Default)]
pub struct RvcLink { cursor: u64, started: bool, wet: f32 }

impl RvcLink {
    pub fn process(&mut self, h: Option<&Header>, sr: f32, buf: &mut [f32], mute_fallback: bool) {
        let step = 1.0 / (0.03 * sr).max(1.0); // 30 ms fades
        let Some(h) = h else {
            self.fade(buf, None, 0, 0, 0.0, step, mute_fallback);
            return;
        };
        h.rate.store(sr as u32, Ordering::Relaxed);
        h.plugin_beat.store(now_ms(), Ordering::Relaxed);
        let (inp, len) = h.ring_n(1);
        let clock = h.rvc_clock.load(Ordering::Acquire);
        if !self.started || self.cursor + len < clock || self.cursor > clock + len {
            self.cursor = clock;
            self.started = true;
        }
        h.rvc_clock.fetch_max(self.cursor, Ordering::AcqRel);
        let mask = len - 1;
        let n = buf.len() as u64;
        for (i, s) in buf.iter().enumerate() {
            unsafe { *(inp as *mut f32).add(((self.cursor + i as u64) & mask) as usize) = *s; }
        }
        h.rvc_in_write.fetch_max(self.cursor + n, Ordering::AcqRel);

        // The whole block must be there for the AI voice to play; otherwise fade out of it.
        let delay = h.rvc_delay.load(Ordering::Relaxed) as u64;
        let out_written = h.rvc_out_write.load(Ordering::Acquire);
        let alive = now_ms().saturating_sub(h.rvc_engine_beat.load(Ordering::Relaxed)) < 500;
        let first = self.cursor.wrapping_sub(delay);
        let ready = alive && delay > 0 && delay < len && self.cursor >= delay
            && first + n <= out_written && first + len > out_written;
        let target = if ready { 1.0 } else { 0.0 };
        let (out, _) = h.ring_n(2);
        let src = if ready { Some(out) } else { None };
        self.fade(buf, src, first, mask, target, step, mute_fallback);
        self.cursor += n;
    }

    #[allow(clippy::too_many_arguments)]
    fn fade(&mut self, buf: &mut [f32], out: Option<*const f32>, first: u64, mask: u64, target: f32, step: f32, mute: bool) {
        for (i, s) in buf.iter_mut().enumerate() {
            self.wet = if self.wet < target { (self.wet + step).min(target) } else { (self.wet - step).max(target) };
            let wet = match out {
                Some(o) => {
                    let v = unsafe { *o.add(((first + i as u64) & mask) as usize) };
                    if v.is_finite() { v.clamp(-4.0, 4.0) } else { 0.0 }
                }
                None => 0.0,
            };
            let dry = if mute { 0.0 } else { *s };
            *s = wet * self.wet + dry * (1.0 - self.wet);
        }
    }
}
