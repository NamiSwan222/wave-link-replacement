//! The Windows audio effect (APO) itself: what audiodg.exe creates for the mic.
//!
//! Lifecycle: Initialize (learns which mic) -> format checks -> LockForProcess (learns channels,
//! sample rate, buffer size; starts the config watcher) -> APOProcess on the real-time thread,
//! many times a second -> UnlockForProcess (stops the watcher).

use std::cell::UnsafeCell;
use std::path::PathBuf;
use std::sync::atomic::{AtomicBool, AtomicPtr, AtomicU32, Ordering};
use std::sync::{Arc, Mutex};
use std::thread::JoinHandle;
use std::time::{Duration, SystemTime};

use windows::Win32::Media::Audio::Apo::*;
use windows::Win32::Media::Multimedia::KSDATAFORMAT_SUBTYPE_IEEE_FLOAT;
use windows::Win32::System::Com::CoTaskMemAlloc;
use windows::core::{implement, Ref, Result, GUID};

use crate::chain::{Chain, Config};

pub const CLSID_WAVEFIX_ENGINE: GUID = GUID::from_u128(0x1F0E0A0C_41EE_4E1A_B2C4_0681D5344BDD);

/// What the real-time thread and the config watcher share.
struct Shared {
    /// The chain the audio thread runs (null = pass through).
    current: AtomicPtr<Chain>,
    /// Audio-thread calls in progress (the watcher waits for 0 before freeing an old chain).
    busy: AtomicU32,
    stop: AtomicBool,
}

impl Shared {
    /// Swaps in a new chain and frees the old one once the audio thread can't be using it.
    fn publish(&self, chain: Option<Box<Chain>>) {
        let new = chain.map_or(std::ptr::null_mut(), Box::into_raw);
        let old = self.current.swap(new, Ordering::AcqRel);
        if old.is_null() { return; }
        // Any call that saw `old` has incremented `busy` before loading it; wait until those finish.
        while self.busy.load(Ordering::Acquire) != 0 { std::thread::sleep(Duration::from_millis(1)); }
        drop(unsafe { Box::from_raw(old) });
    }
}

struct Control {
    endpoint: Option<String>,
    watcher: Option<JoinHandle<()>>,
    channels: u32,
}

#[implement(IAudioProcessingObject, IAudioProcessingObjectRT, IAudioProcessingObjectConfiguration, IAudioSystemEffects, IAudioSystemEffects2)]
pub struct Apo {
    control: Mutex<Control>,
    shared: Arc<Shared>,
    /// Only touched by the audio thread (between LockForProcess and UnlockForProcess).
    rt_channels: UnsafeCell<usize>,
}

unsafe impl Sync for Apo {}

impl Apo {
    pub fn new() -> Self {
        Apo {
            control: Mutex::new(Control { endpoint: None, watcher: None, channels: 0 }),
            shared: Arc::new(Shared { current: AtomicPtr::new(std::ptr::null_mut()), busy: AtomicU32::new(0), stop: AtomicBool::new(false) }),
            rt_channels: UnsafeCell::new(0),
        }
    }
}

impl Drop for Apo {
    fn drop(&mut self) { stop_watcher(&self.shared, &mut self.control.lock().unwrap()); }
}

fn stop_watcher(shared: &Arc<Shared>, c: &mut Control) {
    shared.stop.store(true, Ordering::Release);
    if let Some(t) = c.watcher.take() { let _ = t.join(); }
    shared.publish(None);
    shared.stop.store(false, Ordering::Release);
}

/// Where the app keeps settings and the engine reports back (ProgramData\WaveFix\Engine).
pub fn data_dir() -> PathBuf {
    if let Ok(d) = std::env::var("WAVEFIX_ENGINE_DATA") { return PathBuf::from(d); }
    let base = std::env::var("ProgramData").unwrap_or_else(|_| r"C:\ProgramData".into());
    PathBuf::from(base).join("WaveFix").join("Engine")
}

/// The folder plugins may be loaded from: the install folder above the engine DLL
/// (Program Files\WaveFix), which only administrators can write to.
pub fn plugin_root() -> Option<PathBuf> {
    crate::module_path().and_then(|p| p.parent()?.parent().map(|p| p.to_path_buf()))
}

fn config_path(endpoint: Option<&str>) -> PathBuf {
    let name = endpoint.map(|g| g.trim_matches(|c| c == '{' || c == '}').to_lowercase()).unwrap_or_else(|| "default".into());
    data_dir().join("config").join(format!("{name}.json"))
}

fn status_path(endpoint: Option<&str>) -> PathBuf {
    let name = endpoint.map(|g| g.trim_matches(|c| c == '{' || c == '}').to_lowercase()).unwrap_or_else(|| "default".into());
    data_dir().join("status").join(format!("{name}.json"))
}

/// Watches the mic's config file and rebuilds the chain when it changes.
fn watch(shared: Arc<Shared>, endpoint: Option<String>, channels: usize, rate: f32, max_frames: usize) {
    let path = config_path(endpoint.as_deref());
    let status = status_path(endpoint.as_deref());
    let root = plugin_root();
    let mut last: Option<(SystemTime, u64)> = None;
    let mut first = true;
    let mut beat = std::time::Instant::now() - Duration::from_secs(60);
    let mut warnings: Vec<String> = Vec::new();
    while !shared.stop.load(Ordering::Acquire) {
        let stamp = std::fs::metadata(&path).ok().and_then(|m| Some((m.modified().ok()?, m.len())));
        if first || stamp != last {
            first = false;
            last = stamp;
            let cfg = match std::fs::read_to_string(&path) {
                Ok(text) => Config::parse(&text).unwrap_or_else(|e| { warnings = vec![format!("config: {e}")]; Config::default() }),
                Err(_) => Config::default(), // no config yet: pass the mic through untouched
            };
            let chain = Chain::build(&cfg, channels, rate, max_frames, root.as_deref());
            warnings = chain.warnings.clone();
            shared.publish(Some(Box::new(chain)));
            beat = std::time::Instant::now() - Duration::from_secs(60);
        }
        if beat.elapsed() > Duration::from_secs(5) {
            beat = std::time::Instant::now();
            write_status(&status, endpoint.as_deref(), channels, rate, &warnings);
        }
        std::thread::sleep(Duration::from_millis(200));
    }
}

/// Tells the app the engine is running on this mic (and any problems), so it can show it.
fn write_status(path: &PathBuf, endpoint: Option<&str>, channels: usize, rate: f32, warnings: &[String]) {
    let secs = SystemTime::now().duration_since(SystemTime::UNIX_EPOCH).map(|d| d.as_secs()).unwrap_or(0);
    let body = serde_json::json!({
        "engine": env!("CARGO_PKG_VERSION"),
        "endpoint": endpoint,
        "pid": std::process::id(),
        "channels": channels,
        "rate": rate,
        "time": secs,
        "warnings": warnings,
    });
    if let Some(dir) = path.parent() { let _ = std::fs::create_dir_all(dir); }
    let tmp = path.with_extension("tmp");
    if std::fs::write(&tmp, body.to_string()).is_ok() { let _ = std::fs::rename(&tmp, path); }
}

/// Float32 PCM (what Windows' audio engine runs on internally) is the only format accepted.
fn float_format(t: &IAudioMediaType) -> Option<UNCOMPRESSEDAUDIOFORMAT> {
    let mut f = UNCOMPRESSEDAUDIOFORMAT::default();
    unsafe { t.GetUncompressedAudioFormat(&mut f).ok()?; }
    (f.guidFormatType == KSDATAFORMAT_SUBTYPE_IEEE_FLOAT && f.dwBytesPerSampleContainer == 4 && f.dwSamplesPerFrame >= 1).then_some(f)
}

fn check_format(opposite: Ref<IAudioMediaType>, requested: Ref<IAudioMediaType>) -> Result<IAudioMediaType> {
    let req = requested.ok().map_err(|_| windows::core::Error::from(APOERR_FORMAT_NOT_SUPPORTED))?;
    let rf = float_format(req).ok_or_else(|| windows::core::Error::from(APOERR_FORMAT_NOT_SUPPORTED))?;
    if let Ok(opp) = opposite.ok() {
        // In place: input and output must match exactly.
        if let Some(of) = float_format(opp) {
            if of.dwSamplesPerFrame != rf.dwSamplesPerFrame || of.fFramesPerSecond != rf.fFramesPerSecond {
                return Err(APOERR_FORMAT_NOT_SUPPORTED.into());
            }
        }
    }
    Ok(req.clone())
}

impl IAudioProcessingObject_Impl for Apo_Impl {
    fn Reset(&self) -> Result<()> { Ok(()) }
    fn GetLatency(&self) -> Result<i64> { Ok(0) }

    fn GetRegistrationProperties(&self) -> Result<*mut APO_REG_PROPERTIES> {
        unsafe {
            let p = CoTaskMemAlloc(std::mem::size_of::<APO_REG_PROPERTIES>()) as *mut APO_REG_PROPERTIES;
            if p.is_null() { return Err(windows::Win32::Foundation::E_OUTOFMEMORY.into()); }
            p.write(crate::registration_properties());
            Ok(p)
        }
    }

    fn Initialize(&self, cbdatasize: u32, pbydata: *const u8) -> Result<()> {
        let mut c = self.control.lock().unwrap();
        c.endpoint = None;
        if !pbydata.is_null() && cbdatasize as usize >= std::mem::size_of::<APOInitSystemEffects>() {
            // APOInitSystemEffects and ...2/3 share this layout up to the device collection.
            let init = unsafe { &*(pbydata as *const APOInitSystemEffects) };
            if let Some(devices) = init.pDeviceCollection.as_ref() {
                c.endpoint = crate::endpoint_guid(devices);
            }
        }
        Ok(())
    }

    fn IsInputFormatSupported(&self, opposite: Ref<IAudioMediaType>, requested: Ref<IAudioMediaType>) -> Result<IAudioMediaType> {
        check_format(opposite, requested)
    }

    fn IsOutputFormatSupported(&self, opposite: Ref<IAudioMediaType>, requested: Ref<IAudioMediaType>) -> Result<IAudioMediaType> {
        check_format(opposite, requested)
    }

    fn GetInputChannelCount(&self) -> Result<u32> {
        Ok(self.control.lock().unwrap().channels)
    }
}

impl IAudioProcessingObjectConfiguration_Impl for Apo_Impl {
    fn LockForProcess(&self, nin: u32, ins: *const *const APO_CONNECTION_DESCRIPTOR, nout: u32, outs: *const *const APO_CONNECTION_DESCRIPTOR) -> Result<()> {
        if nin != 1 || nout != 1 || ins.is_null() || outs.is_null() { return Err(windows::Win32::Foundation::E_INVALIDARG.into()); }
        let input = unsafe { &**ins };
        let fmt = input.pFormat.as_ref().and_then(float_format).ok_or_else(|| windows::core::Error::from(APOERR_FORMAT_NOT_SUPPORTED))?;
        let channels = fmt.dwSamplesPerFrame as usize;
        let rate = fmt.fFramesPerSecond;
        let max_frames = (input.u32MaxFrameCount as usize).clamp(64, 1 << 16);

        let mut c = self.control.lock().unwrap();
        stop_watcher(&self.shared, &mut c);
        c.channels = channels as u32;
        unsafe { *self.rt_channels.get() = channels; }
        let shared = self.shared.clone();
        let endpoint = c.endpoint.clone();
        c.watcher = Some(std::thread::Builder::new().name("WaveFix engine config".into())
            .spawn(move || watch(shared, endpoint, channels, rate, max_frames))
            .map_err(|_| windows::core::Error::from(windows::Win32::Foundation::E_FAIL))?);
        Ok(())
    }

    fn UnlockForProcess(&self) -> Result<()> {
        stop_watcher(&self.shared, &mut self.control.lock().unwrap());
        Ok(())
    }
}

impl IAudioProcessingObjectRT_Impl for Apo_Impl {
    fn APOProcess(&self, nin: u32, ins: *const *const APO_CONNECTION_PROPERTY, nout: u32, outs: *mut *mut APO_CONNECTION_PROPERTY) {
        if nin == 0 || nout == 0 || ins.is_null() || outs.is_null() { return; }
        let (input, output) = unsafe { (&**ins, &mut **outs) };
        let channels = unsafe { *self.rt_channels.get() }.max(1);
        let frames = input.u32ValidFrameCount as usize;
        let n = frames * channels;
        let src = input.pBuffer as *const f32;
        let dst = output.pBuffer as *mut f32;
        if dst.is_null() { return; }
        let buf = unsafe { std::slice::from_raw_parts_mut(dst, n) };
        if input.u32BufferFlags == BUFFER_SILENT || src.is_null() {
            // A silent buffer's contents are undefined; the chain may still add sound (soundboard).
            buf.fill(0.0);
        } else if src != dst as *const f32 {
            buf.copy_from_slice(unsafe { std::slice::from_raw_parts(src, n) });
        }

        self.shared.busy.fetch_add(1, Ordering::AcqRel);
        let chain = self.shared.current.load(Ordering::Acquire);
        if !chain.is_null() {
            // Never let a panic cross into the audio engine: the mic just passes through.
            let _ = std::panic::catch_unwind(std::panic::AssertUnwindSafe(|| {
                let chain = unsafe { &mut *chain };
                if chain.channels() == channels { chain.process(buf); }
            }));
        }
        self.shared.busy.fetch_sub(1, Ordering::AcqRel);

        output.u32ValidFrameCount = frames as u32;
        output.u32BufferFlags = BUFFER_VALID;
    }

    fn CalcInputFrames(&self, frames: u32) -> u32 { frames }
    fn CalcOutputFrames(&self, frames: u32) -> u32 { frames }
}

impl IAudioSystemEffects_Impl for Apo_Impl {}

impl IAudioSystemEffects2_Impl for Apo_Impl {
    fn GetEffectsList(&self, ids: *mut *mut GUID, count: *mut u32, _event: windows::Win32::Foundation::HANDLE) -> Result<()> {
        unsafe {
            if !ids.is_null() { *ids = std::ptr::null_mut(); }
            if !count.is_null() { *count = 0; }
        }
        Ok(())
    }
}
