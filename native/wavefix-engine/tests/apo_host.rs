//! Loads the built engine DLL the way Windows' audio service does (DllGetClassObject ->
//! IClassFactory -> IAudioProcessingObject, Initialize, format checks, LockForProcess, APOProcess)
//! and checks what comes out.

use std::ffi::c_void;
use std::path::PathBuf;
use std::time::Duration;

use windows::Win32::Media::Audio::Apo::*;
use windows::Win32::Media::Audio::{WAVEFORMATEX, WAVEFORMATEXTENSIBLE, WAVEFORMATEXTENSIBLE_0};
use windows::Win32::Media::KernelStreaming::{KSDATAFORMAT_SUBTYPE_PCM, WAVE_FORMAT_EXTENSIBLE};
use windows::Win32::Media::Multimedia::KSDATAFORMAT_SUBTYPE_IEEE_FLOAT;
use windows::Win32::System::Com::IClassFactory;
use windows::Win32::System::LibraryLoader::{GetProcAddress, LoadLibraryW};
use windows::core::{implement, IUnknown, IUnknown_Vtbl, Interface, Ref, Result, BOOL, GUID, HRESULT, HSTRING, PCSTR};
use windows::Win32::Foundation::CLASS_E_NOAGGREGATION;
use std::sync::atomic::{AtomicU32, Ordering};

const CLSID: GUID = GUID::from_u128(0x1F0E0A0C_41EE_4E1A_B2C4_0681D5344BDD);
const RATE: u32 = 48_000;
// MSVC multi-character constants 'ACDS' / 'ACPS' from audioenginebaseapo.h.
const APO_CONNECTION_DESCRIPTOR_SIGNATURE: u32 = u32::from_be_bytes(*b"ACDS");
const APO_CONNECTION_PROPERTY_SIGNATURE: u32 = u32::from_be_bytes(*b"ACPS");
const BLOCK: usize = 480;

#[implement(IAudioMediaType)]
struct MediaType { fmt: Box<WAVEFORMATEXTENSIBLE>, float: bool }

impl MediaType {
    fn new(channels: u16, float: bool) -> IAudioMediaType {
        let bits = if float { 32 } else { 16 };
        let fmt = WAVEFORMATEXTENSIBLE {
            Format: WAVEFORMATEX {
                wFormatTag: WAVE_FORMAT_EXTENSIBLE as u16, nChannels: channels, nSamplesPerSec: RATE,
                nAvgBytesPerSec: RATE * channels as u32 * bits / 8, nBlockAlign: channels * bits as u16 / 8,
                wBitsPerSample: bits as u16, cbSize: 22,
            },
            Samples: WAVEFORMATEXTENSIBLE_0 { wValidBitsPerSample: bits as u16 },
            dwChannelMask: if channels == 2 { 3 } else { 4 },
            SubFormat: if float { KSDATAFORMAT_SUBTYPE_IEEE_FLOAT } else { KSDATAFORMAT_SUBTYPE_PCM },
        };
        MediaType { fmt: Box::new(fmt), float }.into()
    }
}

impl IAudioMediaType_Impl for MediaType_Impl {
    fn IsCompressedFormat(&self) -> Result<BOOL> { Ok(false.into()) }
    fn IsEqual(&self, _other: Ref<IAudioMediaType>) -> Result<u32> { Ok(0) }
    fn GetAudioFormat(&self) -> *mut WAVEFORMATEX { &self.fmt.Format as *const _ as *mut _ }
    fn GetUncompressedAudioFormat(&self, out: *mut UNCOMPRESSEDAUDIOFORMAT) -> Result<()> {
        let f = &self.fmt;
        unsafe {
            *out = UNCOMPRESSEDAUDIOFORMAT {
                guidFormatType: if self.float { KSDATAFORMAT_SUBTYPE_IEEE_FLOAT } else { KSDATAFORMAT_SUBTYPE_PCM },
                dwSamplesPerFrame: f.Format.nChannels as u32,
                dwBytesPerSampleContainer: f.Format.wBitsPerSample as u32 / 8,
                dwValidBitsPerSample: f.Format.wBitsPerSample as u32,
                fFramesPerSecond: f.Format.nSamplesPerSec as f32,
                dwChannelMask: f.dwChannelMask,
            };
        }
        Ok(())
    }
}

fn dll_path() -> PathBuf {
    let target = std::env::var("CARGO_TARGET_DIR").map(PathBuf::from).unwrap_or_else(|_| PathBuf::from(env!("CARGO_MANIFEST_DIR")).join("target"));
    let profile = if cfg!(debug_assertions) { "debug" } else { "release" };
    target.join(profile).join("wavefix_engine.dll")
}

struct Engine { apo: IAudioProcessingObject, rt: IAudioProcessingObjectRT, cfg: IAudioProcessingObjectConfiguration, channels: usize }

/// The "outer" object Windows' audio service wraps every effect in (COM aggregation). It answers
/// IUnknown itself and passes every other request to the engine's inner IUnknown.
#[repr(C)]
struct Outer { vtbl: *const IUnknown_Vtbl, refs: AtomicU32, inner: *mut c_void }

unsafe extern "system" fn outer_qi(this: *mut c_void, iid: *const GUID, out: *mut *mut c_void) -> HRESULT {
    unsafe {
        let o = &*(this as *const Outer);
        if *iid == IUnknown::IID { o.refs.fetch_add(1, Ordering::SeqCst); *out = this; return HRESULT(0); }
        let inner_vt = &**(o.inner as *const *const IUnknown_Vtbl);
        (inner_vt.QueryInterface)(o.inner, iid, out)
    }
}
unsafe extern "system" fn outer_addref(this: *mut c_void) -> u32 { unsafe { (*(this as *const Outer)).refs.fetch_add(1, Ordering::SeqCst) + 1 } }
unsafe extern "system" fn outer_release(this: *mut c_void) -> u32 { unsafe { (*(this as *const Outer)).refs.fetch_sub(1, Ordering::SeqCst) - 1 } }
static OUTER_VTBL: IUnknown_Vtbl = IUnknown_Vtbl { QueryInterface: outer_qi, AddRef: outer_addref, Release: outer_release };

fn factory() -> IClassFactory {
    let dll = dll_path();
    assert!(dll.is_file(), "build the DLL first: {}", dll.display());
    unsafe {
        let module = LoadLibraryW(&HSTRING::from(dll.as_os_str())).expect("load engine");
        let get = GetProcAddress(module, PCSTR(c"DllGetClassObject".as_ptr() as _)).expect("DllGetClassObject export");
        let get: extern "system" fn(*const GUID, *const GUID, *mut *mut c_void) -> HRESULT = std::mem::transmute(get);
        let mut factory: *mut c_void = std::ptr::null_mut();
        get(&CLSID, &IClassFactory::IID, &mut factory).ok().expect("class factory");
        IClassFactory::from_raw(factory)
    }
}

/// Creates the engine exactly like Windows' audio service: aggregated in an outer object.
fn create() -> IAudioProcessingObject {
    let f = factory();
    unsafe {
        let outer = Box::leak(Box::new(Outer { vtbl: &OUTER_VTBL, refs: AtomicU32::new(1), inner: std::ptr::null_mut() }));
        let outer_ptr = outer as *mut Outer as *mut c_void;
        // Asking for anything but IUnknown while aggregating must be refused (COM's rule).
        let mut bad: *mut c_void = std::ptr::null_mut();
        let hr = (f.vtable().CreateInstance)(f.as_raw(), outer_ptr, &IAudioProcessingObject::IID, &mut bad);
        assert_eq!(hr, CLASS_E_NOAGGREGATION, "aggregation must ask for IUnknown");
        let mut inner: *mut c_void = std::ptr::null_mut();
        let hr = (f.vtable().CreateInstance)(f.as_raw(), outer_ptr, &IUnknown::IID, &mut inner);
        assert_eq!(hr, HRESULT(0), "aggregated creation failed: {hr:?}");
        outer.inner = inner;
        // The outer object hands out the engine's interfaces; their reference counts go to it.
        let before = outer.refs.load(Ordering::SeqCst);
        let mut p: *mut c_void = std::ptr::null_mut();
        outer_qi(outer_ptr, &IAudioProcessingObject::IID, &mut p).ok().expect("engine interface through the outer object");
        assert_eq!(outer.refs.load(Ordering::SeqCst), before + 1, "AddRef must go to the outer object");
        IAudioProcessingObject::from_raw(p)
    }
}

fn start(channels: u16) -> Engine {
    let apo = create();
    unsafe {
        // Windows passes APOInitSystemEffects2; with no device collection the engine uses default.json.
        let mut init: APOInitSystemEffects2 = std::mem::zeroed();
        init.APOInit.cbSize = std::mem::size_of::<APOInitSystemEffects2>() as u32;
        init.APOInit.clsid = CLSID;
        apo.Initialize(std::slice::from_raw_parts(&init as *const _ as *const u8, init.APOInit.cbSize as usize)).expect("Initialize");
        std::mem::forget(init);

        let float = MediaType::new(channels, true);
        apo.IsInputFormatSupported(&float, &float).expect("float accepted");
        apo.IsOutputFormatSupported(&float, &float).expect("float accepted");

        let cfg: IAudioProcessingObjectConfiguration = apo.cast().unwrap();
        let desc = APO_CONNECTION_DESCRIPTOR {
            Type: APO_CONNECTION_BUFFER_TYPE_EXTERNAL, pBuffer: 0, u32MaxFrameCount: BLOCK as u32,
            pFormat: std::mem::ManuallyDrop::new(Some(float.clone())), u32Signature: APO_CONNECTION_DESCRIPTOR_SIGNATURE,
        };
        let ins = [&desc as *const APO_CONNECTION_DESCRIPTOR];
        let outs = [&desc as *const APO_CONNECTION_DESCRIPTOR];
        cfg.LockForProcess(&ins, &outs).expect("LockForProcess");
        let rt: IAudioProcessingObjectRT = apo.cast().unwrap();
        Engine { apo, rt, cfg, channels: channels as usize }
    }
}

impl Engine {
    /// Runs interleaved audio through APOProcess in 10 ms blocks, in place like Windows does.
    fn run(&self, audio: &mut [f32], silent: bool) {
        for block in audio.chunks_mut(BLOCK * self.channels) {
            let mut prop = APO_CONNECTION_PROPERTY {
                pBuffer: block.as_mut_ptr() as usize,
                u32ValidFrameCount: (block.len() / self.channels) as u32,
                u32BufferFlags: if silent { BUFFER_SILENT } else { BUFFER_VALID },
                u32Signature: APO_CONNECTION_PROPERTY_SIGNATURE,
            };
            let ins = [&prop as *const APO_CONNECTION_PROPERTY];
            let mut outs = [&mut prop as *mut APO_CONNECTION_PROPERTY];
            unsafe { self.rt.APOProcess(1, ins.as_ptr(), 1, outs.as_mut_ptr()); }
            assert_eq!(prop.u32BufferFlags, BUFFER_VALID);
        }
    }
}

impl Drop for Engine {
    fn drop(&mut self) { unsafe { let _ = self.cfg.UnlockForProcess(); } }
}

fn data_dir() -> PathBuf {
    let d = std::env::temp_dir().join(format!("wavefix-engine-test-{}", std::process::id()));
    std::fs::create_dir_all(d.join("config")).unwrap();
    d
}

fn set_config(json: &str) {
    let p = data_dir().join("config").join("default.json");
    std::fs::write(&p, json).unwrap();
    // The engine checks every 200 ms (by time and size): give it time to rebuild.
    std::thread::sleep(Duration::from_millis(700));
}

fn tone(channels: usize, secs: f32, amp: [f32; 2], freq: [f32; 2]) -> Vec<f32> {
    let n = (RATE as f32 * secs) as usize;
    let mut v = vec![0f32; n * channels];
    for i in 0..n {
        for c in 0..channels {
            v[i * channels + c] = amp[c % 2] * (2.0 * std::f32::consts::PI * freq[c % 2] * i as f32 / RATE as f32).sin();
        }
    }
    v
}

fn rms(v: impl Iterator<Item = f32>) -> f32 {
    let (s, n) = v.fold((0f64, 0usize), |(s, n), x| (s + (x as f64).powi(2), n + 1));
    (s / n.max(1) as f64).sqrt() as f32
}

fn channel(v: &[f32], channels: usize, c: usize) -> impl Iterator<Item = f32> + '_ { v.iter().skip(c).step_by(channels).copied() }

// One test function: the engine reads its data folder from an environment variable, and tests in
// one process share it.
#[test]
fn engine_behaves_like_a_windows_audio_effect() {
    unsafe { std::env::set_var("WAVEFIX_ENGINE_DATA", data_dir()); }
    let _ = std::fs::remove_file(data_dir().join("config").join("default.json"));

    // Plain (non-aggregated) creation still works too.
    let plain: IAudioProcessingObject = unsafe { factory().CreateInstance::<_, IAudioProcessingObject>(None::<&IUnknown>) }.expect("plain creation");
    drop(plain);

    // Registration properties match what setup registers.
    let apo = create();
    let props = unsafe { &*apo.GetRegistrationProperties().unwrap() };
    assert_eq!(props.clsid, CLSID);
    assert_eq!(props.Flags.0, 15, "in place + must-match flags");
    assert_eq!(String::from_utf16_lossy(&props.szFriendlyName).trim_end_matches('\0'), "WaveFix Engine");

    // Only float audio is accepted.
    let int16 = MediaType::new(2, false);
    assert!(unsafe { apo.IsInputFormatSupported(&int16, &int16) }.is_err(), "16-bit PCM must be refused");
    drop(apo);

    let e = start(2);

    // No config yet: the mic passes through untouched.
    let x = tone(2, 0.2, [0.3, 0.1], [300.0, 500.0]);
    let mut y = x.clone();
    e.run(&mut y, false);
    assert_eq!(x, y, "no config should be bit-exact pass-through");

    // Gain on both channels, filter state kept per channel.
    set_config(r#"{"stages":[{"type":"gain","db":6.0206},{"type":"filter","kind":"hp","freq":1000}]}"#);
    let x = tone(2, 1.0, [0.2, 0.2], [100.0, 5000.0]);
    let mut y = x.clone();
    e.run(&mut y, false);
    let left = rms(channel(&y[RATE as usize..], 2, 0)) / rms(channel(&x[RATE as usize..], 2, 0));
    let right = rms(channel(&y[RATE as usize..], 2, 1)) / rms(channel(&x[RATE as usize..], 2, 1));
    assert!(left < 0.05, "100 Hz on the left should be cut by the 1 kHz high-pass, got x{left}");
    assert!((right - 2.0).abs() < 0.05, "5 kHz on the right should be doubled, got x{right}");

    // A silent buffer comes out as real silence (its contents are undefined on the way in).
    // (The filter may still ring out the previous tone's tail, but never the garbage.)
    let mut garbage = vec![123.0f32; BLOCK * 2 * 40];
    e.run(&mut garbage, true);
    assert!(garbage.iter().all(|v| v.abs() < 0.5), "garbage from a silent buffer leaked through");
    assert!(garbage[garbage.len() - BLOCK * 2..].iter().all(|v| v.abs() < 1e-3), "tail should have died away");

    // WaveFix's built-in effects: peak protection at -6 dB.
    let ceiling = 10f32.powf(-6.0 / 20.0);
    set_config(&format!(r#"{{"stages":[{{"type":"gain","db":12}},{{"type":"fx","params":{{"LimOn":1,"LimCeiling":{}}}}}]}}"#, 18.0 / 24.0));
    let mut y = tone(2, 0.5, [0.5, 0.5], [440.0, 440.0]);
    e.run(&mut y, false);
    let peak = y.iter().fold(0f32, |m, v| m.max(v.abs()));
    assert!(peak <= ceiling + 1e-4, "limiter peak {peak} > {ceiling}");

    // A VST plugin inside the install folder runs (WaveFix's own effects plugin, as a VST).
    let fx = PathBuf::from(env!("CARGO_MANIFEST_DIR")).join("..").join("wavefix-fx").join("target").join("release").join("wavefix_fx.dll");
    assert!(fx.is_file(), "build native/wavefix-fx first");
    let plugins = dll_path().parent().unwrap().join("plugins-test");
    std::fs::create_dir_all(&plugins).unwrap();
    let inside = plugins.join("fx.dll");
    std::fs::copy(&fx, &inside).unwrap();
    set_config(&format!(r#"{{"stages":[{{"type":"vst","path":{},"params":{{"LimOn":1,"LimCeiling":{}}}}}]}}"#,
        serde_json::to_string(&inside.to_string_lossy()).unwrap(), 18.0 / 24.0));
    let mut y = tone(2, 0.5, [0.9, 0.9], [440.0, 440.0]);
    e.run(&mut y, false);
    let peak = y.iter().fold(0f32, |m, v| m.max(v.abs()));
    assert!(peak <= ceiling + 1e-4, "plugin limiter on both channels: peak {peak}");

    // A plugin outside the install folder is refused (and reported), the mic passes through.
    let outside = std::env::temp_dir().join(format!("wavefix-outside-{}.dll", std::process::id()));
    std::fs::copy(&fx, &outside).unwrap();
    set_config(&format!(r#"{{"stages":[{{"type":"vst","path":{},"params":{{"LimOn":1,"LimCeiling":0.1}}}}]}}"#,
        serde_json::to_string(&outside.to_string_lossy()).unwrap()));
    let x = tone(2, 0.2, [0.9, 0.9], [440.0, 440.0]);
    let mut y = x.clone();
    e.run(&mut y, false);
    assert_eq!(x, y, "a plugin outside the install folder must not run");
    std::thread::sleep(Duration::from_millis(5500)); // status is written every 5 s
    let status = std::fs::read_to_string(data_dir().join("status").join("default.json")).expect("status file");
    assert!(status.contains("not in WaveFix's plugin folder"), "status should explain: {status}");
    assert!(status.contains("\"channels\":2"));

    // Plugin tap: a settings window asks for the audio entering stage 2; stage 1 doubles it.
    {
        use std::io::{Seek, SeekFrom, Write};
        let ring: u32 = 1 << 16;
        let dir = data_dir().join("pd").join("WaveFix").join("Engine");
        std::fs::create_dir_all(&dir).unwrap();
        let bridge = dir.join("bridge.bin");
        let mut f = std::fs::OpenOptions::new().create(true).read(true).write(true).truncate(true).open(&bridge).unwrap();
        f.set_len((128 + 4 * 4 * ring) as u64).unwrap();
        let mut header = vec![0u8; 128];
        header[0..4].copy_from_slice(&0x5242_4657u32.to_le_bytes());
        header[4..8].copy_from_slice(&2u32.to_le_bytes());
        header[12..16].copy_from_slice(&ring.to_le_bytes());
        header[84..88].copy_from_slice(&2u32.to_le_bytes()); // stage 2
        f.write_all(&header).unwrap();
        f.flush().unwrap();
        unsafe { std::env::set_var("ProgramData", data_dir().join("pd")); }
        let beat = |f: &mut std::fs::File| {
            let now = unsafe { windows::Win32::System::SystemInformation::GetTickCount64() };
            f.seek(SeekFrom::Start(104)).unwrap(); f.write_all(&now.to_le_bytes()).unwrap(); f.flush().unwrap();
        };
        set_config(r#"{"stages":[{"type":"gain","db":6.0206},{"type":"filter","kind":"hp","freq":20}]}"#);
        // The engine retries attaching every 3 s; run blocks (with a fresh heartbeat) until it taps.
        let x = tone(2, 0.5, [0.2, 0.05], [300.0, 900.0]);
        let tap_written = |f: &mut std::fs::File| { use std::io::Read; let mut b = [0u8; 8]; f.seek(SeekFrom::Start(96)).unwrap(); f.read_exact(&mut b).unwrap(); u64::from_le_bytes(b) };
        let started = std::time::Instant::now();
        let mut before = 0;
        while tap_written(&mut f) == 0 && started.elapsed() < Duration::from_secs(8) {
            beat(&mut f);
            let mut y = x.clone(); e.run(&mut y, false);
            before = tap_written(&mut f);
        }
        assert!(before > 0, "the engine never wrote the tap");
        beat(&mut f);
        let mut y = x.clone();
        e.run(&mut y, false);
        let after = tap_written(&mut f);
        let frames = x.len() / 2;
        assert_eq!(after - before, frames as u64, "one tap sample per frame");
        let mut ringbuf = vec![0u8; 4 * frames];
        let off = 128 + 3 * 4 * ring as u64 + 4 * (before % ring as u64);
        { use std::io::Read; f.seek(SeekFrom::Start(off)).unwrap(); f.read_exact(&mut ringbuf).unwrap(); }
        let tapped: Vec<f32> = ringbuf.chunks(4).map(|c| f32::from_le_bytes([c[0], c[1], c[2], c[3]])).collect();
        let left: Vec<f32> = channel(&x, 2, 0).collect();
        let worst = tapped.iter().zip(&left).map(|(t, l)| (t - 2.0 * l).abs()).fold(0f32, f32::max);
        assert!(worst < 1e-4, "tap should be the left channel after stage 1 (doubled); worst error {worst}");
        // No heartbeat: the tap stops by itself.
        std::thread::sleep(Duration::from_millis(2200));
        let mut y = x.clone(); e.run(&mut y, false);
        assert_eq!(tap_written(&mut f), after, "tap must stop when the settings window's heartbeat stops");
        unsafe { std::env::remove_var("ProgramData"); }
    }

    // The diagnostic tone shows up even on a silent mic.
    set_config(r#"{"stages":[{"type":"tone","freq":1000,"db":-20}]}"#);
    let mut y = vec![0f32; 2 * RATE as usize / 2];
    e.run(&mut y, true);
    let peak = y.iter().fold(0f32, |m, v| m.max(v.abs()));
    assert!((peak - 0.1).abs() < 0.01, "tone peak {peak}");

    // A broken config doesn't break the audio.
    set_config("{ this is not json");
    let x = tone(2, 0.1, [0.3, 0.3], [440.0, 440.0]);
    let mut y = x.clone();
    e.run(&mut y, false);
    assert_eq!(x, y);

    drop(e);
    let _ = std::fs::remove_file(&outside);
    let _ = std::fs::remove_dir_all(&plugins);
    let _ = std::fs::remove_dir_all(data_dir());
}
