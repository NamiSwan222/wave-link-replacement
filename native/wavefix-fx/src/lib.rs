//! WaveFix Voice FX: a mono VST2 effect (expander, de-esser, compressor, voice changer, limiter).
//!
//! Equalizer APO loads it inside audiodg.exe, so like the rest of WaveFix's chain it runs in the
//! Windows audio engine and never shows up in Discord's process-loopback stream audio.
//! The VST2 ABI is declared here by hand (no Steinberg SDK needed); it matches the layout
//! WaveFix's own host reads in src/WaveFix/VstPlugin.cs.

pub mod bridge;
pub mod dsp;
pub mod params;

use params::{COUNT, PARAMS};
use std::ffi::c_void;
use std::panic::{AssertUnwindSafe, catch_unwind};
use std::sync::atomic::{AtomicU32, Ordering};

type HostCallback = extern "C" fn(*mut AEffect, i32, i32, isize, *mut c_void, f32) -> isize;
type Dispatcher = extern "C" fn(*mut AEffect, i32, i32, isize, *mut c_void, f32) -> isize;
type Process = extern "C" fn(*mut AEffect, *const *const f32, *mut *mut f32, i32);
type ProcessDouble = extern "C" fn(*mut AEffect, *const *const f64, *mut *mut f64, i32);
type SetParameter = extern "C" fn(*mut AEffect, i32, f32);
type GetParameter = extern "C" fn(*mut AEffect, i32) -> f32;

/// The VST 2.4 AEffect struct (64-bit layout).
#[repr(C)]
pub struct AEffect {
    magic: i32,
    dispatcher: Dispatcher,
    process: Process,
    set_parameter: SetParameter,
    get_parameter: GetParameter,
    num_programs: i32,
    num_params: i32,
    num_inputs: i32,
    num_outputs: i32,
    flags: i32,
    reserved1: isize,
    reserved2: isize,
    initial_delay: i32,
    real_qualities: i32,
    off_qualities: i32,
    io_ratio: f32,
    object: *mut c_void,
    user: *mut c_void,
    unique_id: i32,
    version: i32,
    process_replacing: Process,
    process_double_replacing: ProcessDouble,
    future: [u8; 56],
}

const MAGIC: i32 = 0x5673_7450; // 'VstP'
const UNIQUE_ID: i32 = 0x5746_6678; // 'WFfx'
const FLAG_CAN_REPLACING: i32 = 1 << 4;

const EFF_OPEN: i32 = 0;
const EFF_CLOSE: i32 = 1;
const EFF_GET_PROGRAM: i32 = 3;
const EFF_GET_PROGRAM_NAME: i32 = 5;
const EFF_GET_PARAM_LABEL: i32 = 6;
const EFF_GET_PARAM_DISPLAY: i32 = 7;
const EFF_GET_PARAM_NAME: i32 = 8;
const EFF_SET_SAMPLE_RATE: i32 = 10;
const EFF_MAINS_CHANGED: i32 = 12;
const EFF_GET_PROGRAM_NAME_INDEXED: i32 = 29;
const EFF_GET_PLUG_CATEGORY: i32 = 35;
const EFF_GET_EFFECT_NAME: i32 = 45;
const EFF_GET_VENDOR_STRING: i32 = 47;
const EFF_GET_PRODUCT_STRING: i32 = 48;
const EFF_GET_VENDOR_VERSION: i32 = 49;
const EFF_CAN_DO: i32 = 51;
const EFF_GET_VST_VERSION: i32 = 58;

/// Hosts often give only small string buffers (the VST2 spec says 8 bytes for parameter
/// strings, but every real host gives more); never write more than this, including the NUL.
const MAX_SHORT: usize = 16;
const MAX_LONG: usize = 32;

pub const EFFECT_NAME: &str = "WaveFix Voice FX";

/// Plugin instance. `effect` must stay the first field (the host holds a pointer to it).
#[repr(C)]
struct Plugin {
    effect: AEffect,
    params: [AtomicU32; COUNT],
    dsp: dsp::Dsp,
    scratch: Vec<f32>,
}

unsafe fn plugin<'a>(e: *mut AEffect) -> Option<&'a mut Plugin> {
    if e.is_null() { return None; }
    // SAFETY: the host only passes back the pointer we returned from VSTPluginMain.
    let obj = unsafe { (*e).object } as *mut Plugin;
    if obj.is_null() { None } else { Some(unsafe { &mut *obj }) }
}

fn write_str(ptr: *mut c_void, s: &str, max: usize) {
    if ptr.is_null() { return; }
    let bytes = s.as_bytes();
    let n = bytes.len().min(max - 1);
    // SAFETY: `max` stays within what VST hosts provide for this kind of string.
    unsafe {
        std::ptr::copy_nonoverlapping(bytes.as_ptr(), ptr as *mut u8, n);
        *(ptr as *mut u8).add(n) = 0;
    }
}

extern "C" fn dispatcher(e: *mut AEffect, op: i32, index: i32, value: isize, ptr: *mut c_void, opt: f32) -> isize {
    let r = catch_unwind(AssertUnwindSafe(|| {
        let param = |i: i32| if i >= 0 && (i as usize) < COUNT { Some(i as usize) } else { None };
        match op {
            EFF_OPEN => 0,
            EFF_CLOSE => {
                if let Some(p) = unsafe { plugin(e) } {
                    // SAFETY: created with Box::into_raw in VSTPluginMain; the host won't use it again.
                    unsafe { drop(Box::from_raw(p as *mut Plugin)); }
                }
                1
            }
            EFF_SET_SAMPLE_RATE => {
                if let Some(p) = unsafe { plugin(e) } { p.dsp.set_sample_rate(opt); }
                0
            }
            EFF_MAINS_CHANGED => {
                if value != 0 {
                    if let Some(p) = unsafe { plugin(e) } { p.dsp.reset(); }
                    // Not the audio thread: a good moment to connect to the app's audio.
                    bridge::attach();
                }
                0
            }
            EFF_GET_PARAM_NAME => {
                if let Some(i) = param(index) { write_str(ptr, PARAMS[i].name, MAX_SHORT); }
                0
            }
            EFF_GET_PARAM_LABEL => {
                if let Some(i) = param(index) { write_str(ptr, PARAMS[i].unit, MAX_SHORT); }
                0
            }
            EFF_GET_PARAM_DISPLAY => {
                if let (Some(i), Some(p)) = (param(index), unsafe { plugin(e) }) {
                    let v = f32::from_bits(p.params[i].load(Ordering::Relaxed));
                    write_str(ptr, &PARAMS[i].display(v), MAX_SHORT);
                }
                0
            }
            EFF_GET_PROGRAM => 0,
            EFF_GET_PROGRAM_NAME | EFF_GET_PROGRAM_NAME_INDEXED => { write_str(ptr, "Default", MAX_SHORT); 1 }
            EFF_GET_PLUG_CATEGORY => 1, // kPlugCategEffect
            EFF_GET_EFFECT_NAME | EFF_GET_PRODUCT_STRING => { write_str(ptr, EFFECT_NAME, MAX_LONG); 1 }
            EFF_GET_VENDOR_STRING => { write_str(ptr, "WaveFix", MAX_LONG); 1 }
            EFF_GET_VENDOR_VERSION => 1000,
            EFF_CAN_DO => 0,
            EFF_GET_VST_VERSION => 2400,
            _ => 0,
        }
    }));
    r.unwrap_or(0)
}

extern "C" fn set_parameter(e: *mut AEffect, index: i32, value: f32) {
    if let Some(p) = unsafe { plugin(e) } {
        if index >= 0 && (index as usize) < COUNT {
            let v = if value.is_finite() { value.clamp(0.0, 1.0) } else { 0.0 };
            p.params[index as usize].store(v.to_bits(), Ordering::Relaxed);
        }
    }
}

extern "C" fn get_parameter(e: *mut AEffect, index: i32) -> f32 {
    match unsafe { plugin(e) } {
        Some(p) if index >= 0 && (index as usize) < COUNT => f32::from_bits(p.params[index as usize].load(Ordering::Relaxed)),
        _ => 0.0,
    }
}

fn run(p: &mut Plugin, input: *const f32, output: *mut f32, n: usize) {
    let mut norm = [0f32; COUNT];
    for (i, a) in p.params.iter().enumerate() { norm[i] = f32::from_bits(a.load(Ordering::Relaxed)); }
    // Hosts may pass the same buffer for input and output; process through scratch in chunks.
    let mut done = 0;
    while done < n {
        let len = (n - done).min(p.scratch.len());
        let chunk = &mut p.scratch[..len];
        // SAFETY: the host guarantees `n` samples in each buffer.
        unsafe { std::ptr::copy(input.add(done), chunk.as_mut_ptr(), len); }
        p.dsp.process(&norm, chunk);
        unsafe { std::ptr::copy(chunk.as_ptr(), output.add(done), len); }
        done += len;
    }
}

extern "C" fn process_replacing(e: *mut AEffect, inputs: *const *const f32, outputs: *mut *mut f32, frames: i32) {
    if frames <= 0 || inputs.is_null() || outputs.is_null() { return; }
    let r = catch_unwind(AssertUnwindSafe(|| {
        let Some(p) = (unsafe { plugin(e) }) else { return };
        let (input, output) = unsafe { (*inputs, *outputs) };
        if input.is_null() || output.is_null() { return; }
        with_flush_to_zero(|| run(p, input, output, frames as usize));
    }));
    if r.is_err() {
        // Something went badly wrong: pass the mic through untouched rather than stop the audio engine.
        unsafe {
            let (input, output) = (*inputs, *outputs);
            if !input.is_null() && !output.is_null() { std::ptr::copy(input, output, frames as usize); }
        }
    }
}

extern "C" fn process_double(_: *mut AEffect, _: *const *const f64, _: *mut *mut f64, _: i32) {}

/// Sets the CPU's flush-to-zero / denormals-are-zero bits for the block, then restores them.
#[allow(deprecated)]
fn with_flush_to_zero(f: impl FnOnce()) {
    #[cfg(target_arch = "x86_64")]
    unsafe {
        use std::arch::x86_64::{_mm_getcsr, _mm_setcsr};
        let old = _mm_getcsr();
        _mm_setcsr(old | 0x8040);
        f();
        _mm_setcsr(old);
    }
    #[cfg(not(target_arch = "x86_64"))]
    f();
}

/// The VST2 entry point.
#[unsafe(no_mangle)]
pub extern "C" fn VSTPluginMain(_host: Option<HostCallback>) -> *mut AEffect {
    let r = catch_unwind(|| {
        let params: [AtomicU32; COUNT] = std::array::from_fn(|i| AtomicU32::new(PARAMS[i].default_norm().to_bits()));
        let plugin = Box::new(Plugin {
            effect: AEffect {
                magic: MAGIC,
                dispatcher,
                process: process_replacing,
                set_parameter,
                get_parameter,
                num_programs: 1,
                num_params: COUNT as i32,
                num_inputs: 1,
                num_outputs: 1,
                flags: FLAG_CAN_REPLACING,
                reserved1: 0,
                reserved2: 0,
                initial_delay: 0,
                real_qualities: 0,
                off_qualities: 0,
                io_ratio: 1.0,
                object: std::ptr::null_mut(),
                user: std::ptr::null_mut(),
                unique_id: UNIQUE_ID,
                version: 1000,
                process_replacing,
                process_double_replacing: process_double,
                future: [0; 56],
            },
            params,
            dsp: dsp::Dsp::new(),
            scratch: vec![0.0; 4096],
        });
        let raw = Box::into_raw(plugin);
        // SAFETY: raw is valid; the object pointer lets callbacks find the plugin.
        unsafe { (*raw).effect.object = raw as *mut c_void; }
        unsafe { &mut (*raw).effect as *mut AEffect }
    });
    r.unwrap_or(std::ptr::null_mut())
}
