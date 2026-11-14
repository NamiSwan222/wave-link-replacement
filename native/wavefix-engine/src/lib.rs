//! WaveFix Engine: WaveFix's own Windows audio effect (APO), replacing Equalizer APO on new installs.
//!
//! Windows' audio service (audiodg.exe) loads this DLL for the mic and runs the chain the app
//! describes in C:\ProgramData\WaveFix\Engine\config\<mic>.json: filters, gain, WaveFix's own
//! effects (voice polish, voice changer, soundboard, peak protection) and VST2 plugins from
//! Program Files\WaveFix. Running inside the audio engine, not in an app, is what keeps the mic
//! out of Discord's stream audio.
//!
//! Written from Microsoft's documented APO interfaces. Equalizer APO (GPL) was a reference for how
//! the pieces fit, not a source of code.

pub mod aggregate;
pub mod apo;
pub mod chain;
pub mod diag;
pub mod vst;

use std::ffi::c_void;
use std::path::PathBuf;

use windows::Win32::Foundation::{CLASS_E_CLASSNOTAVAILABLE, CLASS_E_NOAGGREGATION, E_POINTER, HMODULE, S_FALSE};
use windows::Win32::Media::Audio::Apo::*;
use windows::Win32::Media::Audio::IMMDeviceCollection;
use windows::Win32::System::Com::{IClassFactory, IClassFactory_Impl};
use windows::Win32::System::LibraryLoader::{GetModuleFileNameW, GetModuleHandleExW, GET_MODULE_HANDLE_EX_FLAG_FROM_ADDRESS, GET_MODULE_HANDLE_EX_FLAG_UNCHANGED_REFCOUNT};
use windows::core::{implement, IUnknown, Interface, Ref, Result, BOOL, GUID, HRESULT, PCWSTR};

pub use apo::CLSID_WAVEFIX_ENGINE;

pub const FRIENDLY_NAME: &str = "WaveFix Engine";
pub const COPYRIGHT: &str = "WaveFix";
pub const VERSION_MAJOR: u32 = 1;
pub const VERSION_MINOR: u32 = 0;

/// In place, same channels / rate / sample size in and out (what a system effect must do).
pub const APO_FLAGS: i32 = APO_FLAG_INPLACE.0 | APO_FLAG_SAMPLESPERFRAME_MUST_MATCH.0 | APO_FLAG_FRAMESPERSECOND_MUST_MATCH.0 | APO_FLAG_BITSPERSAMPLE_MUST_MATCH.0;

fn wide<const N: usize>(s: &str) -> [u16; N] {
    let mut out = [0u16; N];
    for (i, c) in s.encode_utf16().take(N - 1).enumerate() { out[i] = c; }
    out
}

pub fn registration_properties() -> APO_REG_PROPERTIES {
    APO_REG_PROPERTIES {
        clsid: CLSID_WAVEFIX_ENGINE,
        Flags: APO_FLAG(APO_FLAGS),
        szFriendlyName: wide(FRIENDLY_NAME),
        szCopyrightInfo: wide(COPYRIGHT),
        u32MajorVersion: VERSION_MAJOR,
        u32MinorVersion: VERSION_MINOR,
        u32MinInputConnections: 1,
        u32MaxInputConnections: 1,
        u32MinOutputConnections: 1,
        u32MaxOutputConnections: 1,
        u32MaxInstances: u32::MAX,
        u32NumAPOInterfaces: 1,
        iidAPOInterfaceList: [IAudioProcessingObject::IID],
    }
}

/// Full path of this DLL.
pub fn module_path() -> Option<PathBuf> {
    unsafe {
        let mut module = HMODULE::default();
        GetModuleHandleExW(
            GET_MODULE_HANDLE_EX_FLAG_FROM_ADDRESS | GET_MODULE_HANDLE_EX_FLAG_UNCHANGED_REFCOUNT,
            PCWSTR(module_path as *const () as *const u16),
            &mut module,
        ).ok()?;
        let mut buf = vec![0u16; 1024];
        let n = GetModuleFileNameW(Some(module), &mut buf) as usize;
        (n > 0).then(|| PathBuf::from(String::from_utf16_lossy(&buf[..n])))
    }
}

/// The mic's endpoint GUID ("{0.0.1.00000000}.{guid}" -> "{guid}") from the devices Windows
/// passes at Initialize.
pub fn endpoint_guid(devices: &IMMDeviceCollection) -> Option<String> {
    unsafe {
        let count = devices.GetCount().ok()?;
        for i in (0..count).rev() {
            let id = devices.Item(i).ok()?.GetId().ok()?;
            let id = id.to_string().ok()?;
            if id.starts_with("{0.0.") {
                return id.rsplit('.').next().map(|g| g.to_string());
            }
        }
        None
    }
}

#[implement(IClassFactory)]
struct Factory;

impl IClassFactory_Impl for Factory_Impl {
    fn CreateInstance(&self, outer: Ref<IUnknown>, riid: *const GUID, ppv: *mut *mut c_void) -> Result<()> {
        if ppv.is_null() || riid.is_null() { return Err(E_POINTER.into()); }
        unsafe { *ppv = std::ptr::null_mut(); }
        if let Some(outer) = outer.as_ref() {
            // Windows' audio service always creates effects this way: inside an outer object of
            // its own, asking for our inner IUnknown (COM's rule for aggregation).
            if unsafe { *riid } != IUnknown::IID { return Err(CLASS_E_NOAGGREGATION.into()); }
            let inner = aggregate::create_aggregated(outer.as_raw()).inspect_err(|e| diag::error(&format!("Couldn't create the engine: {e}")))?;
            unsafe { *ppv = inner; }
            return Ok(());
        }
        let unknown: IUnknown = apo::Apo::new().into();
        unsafe { unknown.query(riid, ppv).ok() }
    }

    fn LockServer(&self, _lock: BOOL) -> Result<()> { Ok(()) }
}

/// COM entry point: Windows asks for the engine's class factory here.
#[unsafe(no_mangle)]
pub extern "system" fn DllGetClassObject(rclsid: *const GUID, riid: *const GUID, ppv: *mut *mut c_void) -> HRESULT {
    if rclsid.is_null() || riid.is_null() || ppv.is_null() { return E_POINTER; }
    unsafe { *ppv = std::ptr::null_mut(); }
    if unsafe { *rclsid } != CLSID_WAVEFIX_ENGINE { return CLASS_E_CLASSNOTAVAILABLE; }
    static ANNOUNCED: std::sync::Once = std::sync::Once::new();
    ANNOUNCED.call_once(|| diag::info(&format!(
        "WaveFix Engine {} loaded from {} into {}",
        env!("CARGO_PKG_VERSION"),
        module_path().map(|p| p.display().to_string()).unwrap_or_default(),
        std::env::current_exe().map(|p| p.display().to_string()).unwrap_or_default())));
    let factory: IUnknown = Factory.into();
    unsafe { factory.query(riid, ppv) }
}

/// Never unloaded: the config watcher thread must not outlive the code it runs.
#[unsafe(no_mangle)]
pub extern "system" fn DllCanUnloadNow() -> HRESULT { S_FALSE }
