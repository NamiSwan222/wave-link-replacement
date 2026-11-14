//! COM aggregation, which Windows' audio service uses for every audio effect it creates: it
//! wraps the effect in an "outer" object of its own and asks for our inner IUnknown. Without this,
//! creating the engine fails with CLASS_E_NOAGGREGATION and Windows can't open the mic at all.
//!
//! Layout: one heap block holds a non-delegating IUnknown (owns everything) plus one interface
//! pointer per engine interface. Those interfaces send QueryInterface/AddRef/Release to the outer
//! object (COM's rule for aggregated objects) and forward every real call to the engine object
//! built by windows-rs, which stays unchanged.

use std::ffi::c_void;
use std::mem::offset_of;
use std::sync::atomic::{AtomicU32, Ordering};

use windows::Win32::Foundation::{E_NOINTERFACE, E_POINTER, HANDLE};
use windows::Win32::Media::Audio::Apo::*;
use windows::core::{IUnknown, IUnknown_Vtbl, Interface, GUID, HRESULT};

#[repr(C)]
struct Agg {
    unk: *const IUnknown_Vtbl,
    apo: *const IAudioProcessingObject_Vtbl,
    rt: *const IAudioProcessingObjectRT_Vtbl,
    cfg: *const IAudioProcessingObjectConfiguration_Vtbl,
    se: *const IAudioSystemEffects_Vtbl,
    se2: *const IAudioSystemEffects2_Vtbl,
    refs: AtomicU32,
    /// The controlling (outer) object. Not AddRef'd: it owns us, per COM's aggregation rules.
    outer: *mut c_void,
    i_apo: IAudioProcessingObject,
    i_rt: IAudioProcessingObjectRT,
    i_cfg: IAudioProcessingObjectConfiguration,
    i_se2: IAudioSystemEffects2,
}

const OFF_UNK: usize = offset_of!(Agg, unk);
const OFF_APO: usize = offset_of!(Agg, apo);
const OFF_RT: usize = offset_of!(Agg, rt);
const OFF_CFG: usize = offset_of!(Agg, cfg);
const OFF_SE: usize = offset_of!(Agg, se);
const OFF_SE2: usize = offset_of!(Agg, se2);

unsafe fn agg<'a, const OFF: usize>(this: *mut c_void) -> &'a Agg {
    unsafe { &*((this as *mut u8).sub(OFF) as *const Agg) }
}

unsafe fn outer_vtbl(outer: *mut c_void) -> &'static IUnknown_Vtbl {
    unsafe { &**(outer as *const *const IUnknown_Vtbl) }
}

// ---- Delegating IUnknown (for every engine interface): goes to the outer object ----

unsafe extern "system" fn d_qi<const OFF: usize>(this: *mut c_void, iid: *const GUID, out: *mut *mut c_void) -> HRESULT {
    unsafe { let a = agg::<OFF>(this); (outer_vtbl(a.outer).QueryInterface)(a.outer, iid, out) }
}
unsafe extern "system" fn d_addref<const OFF: usize>(this: *mut c_void) -> u32 {
    unsafe { let a = agg::<OFF>(this); (outer_vtbl(a.outer).AddRef)(a.outer) }
}
unsafe extern "system" fn d_release<const OFF: usize>(this: *mut c_void) -> u32 {
    unsafe { let a = agg::<OFF>(this); (outer_vtbl(a.outer).Release)(a.outer) }
}

const fn delegating<const OFF: usize>() -> IUnknown_Vtbl {
    IUnknown_Vtbl { QueryInterface: d_qi::<OFF>, AddRef: d_addref::<OFF>, Release: d_release::<OFF> }
}

// ---- Non-delegating IUnknown: what the outer object holds ----

unsafe extern "system" fn nd_qi(this: *mut c_void, iid: *const GUID, out: *mut *mut c_void) -> HRESULT {
    if iid.is_null() || out.is_null() { return E_POINTER; }
    unsafe {
        *out = std::ptr::null_mut();
        let base = (this as *mut u8).sub(OFF_UNK);
        let iid = *iid;
        let field = if iid == IUnknown::IID { OFF_UNK }
            else if iid == IAudioProcessingObject::IID { OFF_APO }
            else if iid == IAudioProcessingObjectRT::IID { OFF_RT }
            else if iid == IAudioProcessingObjectConfiguration::IID { OFF_CFG }
            else if iid == IAudioSystemEffects::IID { OFF_SE }
            else if iid == IAudioSystemEffects2::IID { OFF_SE2 }
            else { return E_NOINTERFACE; };
        let p = base.add(field) as *mut c_void;
        // AddRef through the returned interface: our own count for IUnknown, the outer's otherwise.
        let vt = &**(p as *const *const IUnknown_Vtbl);
        (vt.AddRef)(p);
        *out = p;
        HRESULT(0)
    }
}
unsafe extern "system" fn nd_addref(this: *mut c_void) -> u32 {
    unsafe { agg::<OFF_UNK>(this).refs.fetch_add(1, Ordering::AcqRel) + 1 }
}
unsafe extern "system" fn nd_release(this: *mut c_void) -> u32 {
    unsafe {
        let left = agg::<OFF_UNK>(this).refs.fetch_sub(1, Ordering::AcqRel) - 1;
        if left == 0 { drop(Box::from_raw((this as *mut u8).sub(OFF_UNK) as *mut Agg)); }
        left
    }
}

static UNK_VTBL: IUnknown_Vtbl = IUnknown_Vtbl { QueryInterface: nd_qi, AddRef: nd_addref, Release: nd_release };

// ---- Forwarders: each real call goes to the engine object ----

unsafe extern "system" fn apo_reset(this: *mut c_void) -> HRESULT {
    unsafe { let a = agg::<OFF_APO>(this); (a.i_apo.vtable().Reset)(a.i_apo.as_raw()) }
}
unsafe extern "system" fn apo_latency(this: *mut c_void, t: *mut i64) -> HRESULT {
    unsafe { let a = agg::<OFF_APO>(this); (a.i_apo.vtable().GetLatency)(a.i_apo.as_raw(), t) }
}
unsafe extern "system" fn apo_regprops(this: *mut c_void, p: *mut *mut APO_REG_PROPERTIES) -> HRESULT {
    unsafe { let a = agg::<OFF_APO>(this); (a.i_apo.vtable().GetRegistrationProperties)(a.i_apo.as_raw(), p) }
}
unsafe extern "system" fn apo_init(this: *mut c_void, cb: u32, data: *const u8) -> HRESULT {
    unsafe { let a = agg::<OFF_APO>(this); (a.i_apo.vtable().Initialize)(a.i_apo.as_raw(), cb, data) }
}
unsafe extern "system" fn apo_in_fmt(this: *mut c_void, opp: *mut c_void, req: *mut c_void, out: *mut *mut c_void) -> HRESULT {
    unsafe { let a = agg::<OFF_APO>(this); (a.i_apo.vtable().IsInputFormatSupported)(a.i_apo.as_raw(), opp, req, out) }
}
unsafe extern "system" fn apo_out_fmt(this: *mut c_void, opp: *mut c_void, req: *mut c_void, out: *mut *mut c_void) -> HRESULT {
    unsafe { let a = agg::<OFF_APO>(this); (a.i_apo.vtable().IsOutputFormatSupported)(a.i_apo.as_raw(), opp, req, out) }
}
unsafe extern "system" fn apo_channels(this: *mut c_void, n: *mut u32) -> HRESULT {
    unsafe { let a = agg::<OFF_APO>(this); (a.i_apo.vtable().GetInputChannelCount)(a.i_apo.as_raw(), n) }
}

static APO_VTBL: IAudioProcessingObject_Vtbl = IAudioProcessingObject_Vtbl {
    base__: delegating::<OFF_APO>(),
    Reset: apo_reset,
    GetLatency: apo_latency,
    GetRegistrationProperties: apo_regprops,
    Initialize: apo_init,
    IsInputFormatSupported: apo_in_fmt,
    IsOutputFormatSupported: apo_out_fmt,
    GetInputChannelCount: apo_channels,
};

unsafe extern "system" fn rt_process(this: *mut c_void, nin: u32, ins: *const *const APO_CONNECTION_PROPERTY, nout: u32, outs: *mut *mut APO_CONNECTION_PROPERTY) {
    unsafe { let a = agg::<OFF_RT>(this); (a.i_rt.vtable().APOProcess)(a.i_rt.as_raw(), nin, ins, nout, outs) }
}
unsafe extern "system" fn rt_calc_in(this: *mut c_void, n: u32) -> u32 {
    unsafe { let a = agg::<OFF_RT>(this); (a.i_rt.vtable().CalcInputFrames)(a.i_rt.as_raw(), n) }
}
unsafe extern "system" fn rt_calc_out(this: *mut c_void, n: u32) -> u32 {
    unsafe { let a = agg::<OFF_RT>(this); (a.i_rt.vtable().CalcOutputFrames)(a.i_rt.as_raw(), n) }
}

static RT_VTBL: IAudioProcessingObjectRT_Vtbl = IAudioProcessingObjectRT_Vtbl {
    base__: delegating::<OFF_RT>(),
    APOProcess: rt_process,
    CalcInputFrames: rt_calc_in,
    CalcOutputFrames: rt_calc_out,
};

unsafe extern "system" fn cfg_lock(this: *mut c_void, nin: u32, ins: *const *const APO_CONNECTION_DESCRIPTOR, nout: u32, outs: *const *const APO_CONNECTION_DESCRIPTOR) -> HRESULT {
    unsafe { let a = agg::<OFF_CFG>(this); (a.i_cfg.vtable().LockForProcess)(a.i_cfg.as_raw(), nin, ins, nout, outs) }
}
unsafe extern "system" fn cfg_unlock(this: *mut c_void) -> HRESULT {
    unsafe { let a = agg::<OFF_CFG>(this); (a.i_cfg.vtable().UnlockForProcess)(a.i_cfg.as_raw()) }
}

static CFG_VTBL: IAudioProcessingObjectConfiguration_Vtbl = IAudioProcessingObjectConfiguration_Vtbl {
    base__: delegating::<OFF_CFG>(),
    LockForProcess: cfg_lock,
    UnlockForProcess: cfg_unlock,
};

static SE_VTBL: IAudioSystemEffects_Vtbl = IAudioSystemEffects_Vtbl { base__: delegating::<OFF_SE>() };

unsafe extern "system" fn se2_effects(this: *mut c_void, ids: *mut *mut GUID, count: *mut u32, event: HANDLE) -> HRESULT {
    unsafe { let a = agg::<OFF_SE2>(this); (a.i_se2.vtable().GetEffectsList)(a.i_se2.as_raw(), ids, count, event) }
}

static SE2_VTBL: IAudioSystemEffects2_Vtbl = IAudioSystemEffects2_Vtbl {
    base__: IAudioSystemEffects_Vtbl { base__: delegating::<OFF_SE2>() },
    GetEffectsList: se2_effects,
};

/// Creates the engine inside an outer object. Returns the inner (non-delegating) IUnknown with
/// one reference, which the outer object owns.
pub fn create_aggregated(outer: *mut c_void) -> windows::core::Result<*mut c_void> {
    let engine: IUnknown = crate::apo::Apo::new().into();
    let a = Box::new(Agg {
        unk: &UNK_VTBL, apo: &APO_VTBL, rt: &RT_VTBL, cfg: &CFG_VTBL, se: &SE_VTBL, se2: &SE2_VTBL,
        refs: AtomicU32::new(1),
        outer,
        i_apo: engine.cast()?,
        i_rt: engine.cast()?,
        i_cfg: engine.cast()?,
        i_se2: engine.cast()?,
    });
    let base = Box::into_raw(a) as *mut u8;
    Ok(unsafe { base.add(OFF_UNK) } as *mut c_void)
}
