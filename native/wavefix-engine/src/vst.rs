//! Minimal VST2 host for plugins in the chain (RNNoise and the user's own plugins).
//!
//! Plugins run inside Windows' audio service, so only files inside WaveFix's protected install
//! folder (Program Files\WaveFix, admin-only) are loaded: no other program can slip code in.

use std::collections::HashMap;
use std::ffi::c_void;
use std::path::{Path, PathBuf};

use windows::Win32::Foundation::HMODULE;
use windows::Win32::System::LibraryLoader::{GetProcAddress, LoadLibraryExW, LOAD_WITH_ALTERED_SEARCH_PATH};
use windows::core::{HSTRING, PCSTR};

const MAGIC: i32 = 0x5673_7450;
const FLAG_CAN_REPLACING: i32 = 1 << 4;
const FLAG_PROGRAM_CHUNKS: i32 = 1 << 5;
const EFF_OPEN: i32 = 0;
const EFF_CLOSE: i32 = 1;
const EFF_GET_PARAM_NAME: i32 = 8;
const EFF_SET_SAMPLE_RATE: i32 = 10;
const EFF_SET_BLOCK_SIZE: i32 = 11;
const EFF_MAINS_CHANGED: i32 = 12;
const EFF_SET_CHUNK: i32 = 24;
const EFF_START_PROCESS: i32 = 71;
const EFF_STOP_PROCESS: i32 = 72;

type Dispatcher = unsafe extern "C" fn(*mut c_void, i32, i32, isize, *mut c_void, f32) -> isize;
type Process = unsafe extern "C" fn(*mut c_void, *const *const f32, *mut *mut f32, i32);
type SetParam = unsafe extern "C" fn(*mut c_void, i32, f32);
type Main = unsafe extern "C" fn(HostCallback) -> *mut c_void;
type HostCallback = extern "C" fn(*mut c_void, i32, i32, isize, *mut c_void, f32) -> isize;

/// Saved plugin settings, as the app stores them (Equalizer APO's format: a base64 chunk, or
/// parameter name -> 0..1 value).
#[derive(Default, Clone)]
pub struct PluginState {
    pub chunk: Option<String>,
    pub params: Option<HashMap<String, f32>>,
}

/// VST 2.4 VstTimeInfo (what audioMasterGetTime returns). Some plugins crash on a null answer.
#[repr(C)]
struct VstTimeInfo {
    sample_pos: f64, sample_rate: f64, nano_seconds: f64, ppq_pos: f64, tempo: f64,
    bar_start_pos: f64, cycle_start_pos: f64, cycle_end_pos: f64,
    time_sig_numerator: i32, time_sig_denominator: i32, smpte_offset: i32, smpte_frame_rate: i32,
    samples_to_next_clock: i32, flags: i32,
}

/// Shared by every plugin instance in this process (all on the audio thread). A voice chain has
/// no song position, so this is a steady clock: running, 120 BPM, 4/4.
struct TimeCell(std::cell::UnsafeCell<VstTimeInfo>);
unsafe impl Sync for TimeCell {}
static TIME: TimeCell = TimeCell(std::cell::UnsafeCell::new(VstTimeInfo {
    sample_pos: 0.0, sample_rate: 48_000.0, nano_seconds: 0.0, ppq_pos: 0.0, tempo: 120.0,
    bar_start_pos: 0.0, cycle_start_pos: 0.0, cycle_end_pos: 0.0,
    time_sig_numerator: 4, time_sig_denominator: 4, smpte_offset: 0, smpte_frame_rate: 0, samples_to_next_clock: 0,
    // playing | ppq | tempo | bars | time signature valid
    flags: 2 | (1 << 9) | (1 << 10) | (1 << 11) | (1 << 13),
}));
static SAMPLE_RATE: std::sync::atomic::AtomicU32 = std::sync::atomic::AtomicU32::new(48_000);
static BLOCK_SIZE: std::sync::atomic::AtomicU32 = std::sync::atomic::AtomicU32::new(480);

pub fn set_host_format(sr: f32, block: usize) {
    use std::sync::atomic::Ordering::Relaxed;
    SAMPLE_RATE.store(sr as u32, Relaxed);
    BLOCK_SIZE.store(block as u32, Relaxed);
}

/// Moves the clock on by one block (called once per block by the chain).
pub fn advance_time(frames: usize) {
    let t = unsafe { &mut *TIME.0.get() };
    let sr = SAMPLE_RATE.load(std::sync::atomic::Ordering::Relaxed).max(1) as f64;
    t.sample_rate = sr;
    t.sample_pos += frames as f64;
    t.ppq_pos = t.sample_pos / sr * t.tempo / 60.0;
    t.bar_start_pos = (t.ppq_pos / 4.0).floor() * 4.0;
}

fn write_cstr(ptr: *mut c_void, s: &str, max: usize) {
    if ptr.is_null() { return; }
    let b = s.as_bytes();
    let n = b.len().min(max - 1);
    unsafe { std::ptr::copy_nonoverlapping(b.as_ptr(), ptr as *mut u8, n); *(ptr as *mut u8).add(n) = 0; }
}

/// Answers plugins' questions the way a normal (if simple) host does.
extern "C" fn host_callback(_e: *mut c_void, opcode: i32, _i: i32, _v: isize, ptr: *mut c_void, _o: f32) -> isize {
    use std::sync::atomic::Ordering::Relaxed;
    match opcode {
        1 => 2400,                                         // audioMasterVersion
        7 => TIME.0.get() as isize,                         // audioMasterGetTime
        16 => SAMPLE_RATE.load(Relaxed) as isize,           // audioMasterGetSampleRate
        17 => BLOCK_SIZE.load(Relaxed) as isize,            // audioMasterGetBlockSize
        23 => 2,                                            // audioMasterGetCurrentProcessLevel: realtime
        24 => 1,                                            // audioMasterGetAutomationState: off
        32 => { write_cstr(ptr, "WaveFix", 64); 1 }         // audioMasterGetVendorString
        33 => { write_cstr(ptr, "WaveFix Engine", 64); 1 }  // audioMasterGetProductString
        34 => 1000,                                         // audioMasterGetVendorVersion
        37 => {                                             // audioMasterCanDo
            if ptr.is_null() { return 0; }
            let s = unsafe { std::ffi::CStr::from_ptr(ptr as *const std::ffi::c_char) }.to_string_lossy();
            if s == "sendVstTimeInfo" { 1 } else { -1 }
        }
        38 => 1,                                            // audioMasterGetLanguage: English
        42 | 43 | 44 => 1,                                  // updateDisplay, beginEdit, endEdit
        _ => 0,
    }
}

/// Marks plugin output samples before processing (a NaN no plugin produces by accident).
const UNWRITTEN: f32 = f32::from_bits(0x7FC0_5A5A);

/// One loaded plugin instance covering `channels` consecutive channels.
pub struct VstInstance {
    effect: *mut c_void,
    dispatch: Dispatcher,
    process: Process,
    inputs: usize,
    outputs: usize,
    channels: usize,
    /// Buffers for channels the plugin has but the stream doesn't (or vice versa).
    spare_in: Vec<Vec<f32>>,
    spare_out: Vec<Vec<f32>>,
    in_ptrs: Vec<*const f32>,
    out_ptrs: Vec<*mut f32>,
}

// The plugin is only used by one thread at a time (built off the audio thread, then handed over).
unsafe impl Send for VstInstance {}

impl VstInstance {
    pub fn channels(&self) -> usize { self.channels }

    /// Processes `n` frames of the given channel planes in place.
    pub fn process(&mut self, planes: &mut [Vec<f32>], n: usize) {
        let n = n.min(self.spare_in.first().map_or(usize::MAX, |b| b.len()));
        for i in 0..self.inputs {
            self.in_ptrs[i] = if i < planes.len() { planes[i].as_ptr() } else { self.spare_in[i].as_ptr() };
        }
        for i in 0..self.outputs {
            // Outputs go to spare buffers first: plugins may write outputs before reading inputs.
            // They're marked first so an output the plugin never writes can be told apart.
            self.spare_out[i][..n].fill(UNWRITTEN);
            self.out_ptrs[i] = self.spare_out[i].as_mut_ptr();
        }
        unsafe { (self.process)(self.effect, self.in_ptrs.as_ptr(), self.out_ptrs.as_mut_ptr(), n as i32); }
        let written = |b: &[f32]| b[..n].iter().any(|s| s.to_bits() != UNWRITTEN.to_bits());
        let first_written = self.outputs > 0 && written(&self.spare_out[0]);
        for (i, plane) in planes.iter_mut().enumerate() {
            // A mono plugin on this channel: output 0; extra stream channels keep plugin output i.
            // Some "mono" plugins (RNNoise's rnnoise_mono) say they have two outputs but only fill
            // the first: the other channel then gets the first output instead of silence.
            let j = i.min(self.outputs - 1);
            let src = if written(&self.spare_out[j]) { j } else if first_written { 0 } else { continue };
            plane[..n].copy_from_slice(&self.spare_out[src][..n]);
        }
    }
}

impl Drop for VstInstance {
    fn drop(&mut self) {
        unsafe {
            (self.dispatch)(self.effect, EFF_STOP_PROCESS, 0, 0, std::ptr::null_mut(), 0.0);
            (self.dispatch)(self.effect, EFF_MAINS_CHANGED, 0, 0, std::ptr::null_mut(), 0.0);
            (self.dispatch)(self.effect, EFF_CLOSE, 0, 0, std::ptr::null_mut(), 0.0);
        }
        // The DLL stays loaded: some plugins crash when unloaded.
    }
}

/// True if `path` is inside `root` (both made absolute; no `..` tricks).
pub fn inside(path: &Path, root: &Path) -> bool {
    fn norm(p: &Path) -> Option<PathBuf> {
        let s = std::path::absolute(p).ok()?;
        let mut out = PathBuf::new();
        for c in s.components() {
            match c {
                std::path::Component::ParentDir => { out.pop(); }
                std::path::Component::CurDir => {}
                other => out.push(other),
            }
        }
        Some(PathBuf::from(out.to_string_lossy().to_lowercase()))
    }
    match (norm(path), norm(root)) {
        (Some(p), Some(r)) => p.starts_with(&r) && p != r,
        _ => false,
    }
}

/// Loads enough instances of a plugin to cover `channels` channels.
pub fn load_for_channels(path: &Path, root: Option<&Path>, channels: usize, sr: f32, max_frames: usize, state: &PluginState) -> Result<Vec<VstInstance>, String> {
    if let Some(root) = root {
        if !inside(path, root) { return Err("not in WaveFix's plugin folder".into()); }
    }
    if !path.is_file() { return Err("file not found".into()); }
    let module: HMODULE = unsafe { LoadLibraryExW(&HSTRING::from(path.as_os_str()), None, LOAD_WITH_ALTERED_SEARCH_PATH) }
        .map_err(|e| format!("couldn't load ({e})"))?;
    let entry = unsafe { GetProcAddress(module, PCSTR(c"VSTPluginMain".as_ptr() as _)) }
        .or_else(|| unsafe { GetProcAddress(module, PCSTR(c"main".as_ptr() as _)) })
        .ok_or("not a VST2 plugin")?;
    let main: Main = unsafe { std::mem::transmute(entry) };

    let mut out = Vec::new();
    let mut covered = 0;
    while covered < channels {
        let inst = open(main, channels - covered, sr, max_frames, state)?;
        covered += inst.channels;
        out.push(inst);
    }
    Ok(out)
}

fn open(main: Main, remaining: usize, sr: f32, max_frames: usize, state: &PluginState) -> Result<VstInstance, String> {
    let effect = unsafe { main(host_callback) };
    if effect.is_null() || unsafe { *(effect as *const i32) } != MAGIC { return Err("the plugin didn't start".into()); }
    let read_i32 = |off: usize| unsafe { *((effect as *const u8).add(off) as *const i32) };
    let read_ptr = |off: usize| unsafe { *((effect as *const u8).add(off) as *const usize) };
    let flags = read_i32(56);
    if flags & FLAG_CAN_REPLACING == 0 { return Err("plugin can't process in replacing mode".into()); }
    let dispatch: Dispatcher = unsafe { std::mem::transmute(read_ptr(8)) };
    let set_param: SetParam = unsafe { std::mem::transmute(read_ptr(24)) };
    let process: Process = unsafe { std::mem::transmute(read_ptr(120)) };
    let num_params = read_i32(44).max(0);
    let inputs = read_i32(48).max(1) as usize;
    let outputs = read_i32(52).max(1) as usize;

    unsafe {
        dispatch(effect, EFF_OPEN, 0, 0, std::ptr::null_mut(), 0.0);
        dispatch(effect, EFF_SET_SAMPLE_RATE, 0, 0, std::ptr::null_mut(), sr);
        dispatch(effect, EFF_SET_BLOCK_SIZE, 0, max_frames as isize, std::ptr::null_mut(), 0.0);
    }
    if let Some(chunk) = state.chunk.as_deref().and_then(base64_decode) {
        if flags & FLAG_PROGRAM_CHUNKS != 0 {
            let mut chunk = chunk;
            unsafe { dispatch(effect, EFF_SET_CHUNK, 1, chunk.len() as isize, chunk.as_mut_ptr() as *mut c_void, 0.0); }
        }
    } else if let Some(params) = &state.params {
        for i in 0..num_params {
            let mut buf = [0u8; 256];
            unsafe { dispatch(effect, EFF_GET_PARAM_NAME, i, 0, buf.as_mut_ptr() as *mut c_void, 0.0); }
            let len = buf.iter().position(|&b| b == 0).unwrap_or(buf.len());
            let name = String::from_utf8_lossy(&buf[..len]).trim().to_string();
            if let Some(v) = params.get(&name) { unsafe { set_param(effect, i, v.clamp(0.0, 1.0)); } }
        }
    }
    unsafe {
        dispatch(effect, EFF_MAINS_CHANGED, 0, 1, std::ptr::null_mut(), 0.0);
        // VST 2.4: tell the plugin audio is about to flow (some only output sound, or update their
        // meters, after this).
        dispatch(effect, EFF_START_PROCESS, 0, 0, std::ptr::null_mut(), 0.0);
    }

    let channels = inputs.min(remaining).max(1);
    Ok(VstInstance {
        effect, dispatch, process, inputs, outputs, channels,
        spare_in: vec![vec![0.0; max_frames]; inputs],
        spare_out: vec![vec![0.0; max_frames]; outputs.max(channels)],
        in_ptrs: vec![std::ptr::null(); inputs],
        out_ptrs: vec![std::ptr::null_mut(); outputs],
    })
}

pub fn base64_decode(s: &str) -> Option<Vec<u8>> {
    let val = |c: u8| -> Option<u32> {
        Some(match c {
            b'A'..=b'Z' => c - b'A',
            b'a'..=b'z' => c - b'a' + 26,
            b'0'..=b'9' => c - b'0' + 52,
            b'+' => 62,
            b'/' => 63,
            _ => return None,
        } as u32)
    };
    let bytes: Vec<u8> = s.bytes().filter(|b| !b.is_ascii_whitespace() && *b != b'=').collect();
    let mut out = Vec::with_capacity(bytes.len() * 3 / 4);
    for chunk in bytes.chunks(4) {
        let mut acc = 0u32;
        for (i, &b) in chunk.iter().enumerate() { acc |= val(b)? << (18 - 6 * i); }
        out.push((acc >> 16) as u8);
        if chunk.len() > 2 { out.push((acc >> 8) as u8); }
        if chunk.len() > 3 { out.push(acc as u8); }
    }
    Some(out)
}
