//! Checks each effect does what its name says, plus the VST2 entry point the audio engine uses.

use std::ffi::c_void;
use wavefix_fx::dsp::Dsp;
use wavefix_fx::params::{self as p, COUNT, PARAMS};

const SR: f32 = 48_000.0;

fn norms(set: &[(usize, f32)]) -> [f32; COUNT] {
    let mut n: [f32; COUNT] = std::array::from_fn(|i| PARAMS[i].default_norm());
    for &(i, v) in set { n[i] = PARAMS[i].to_norm(v); }
    n
}

fn sine(freq: f32, db: f32, secs: f32) -> Vec<f32> {
    let a = 10f32.powf(db / 20.0);
    (0..(SR * secs) as usize).map(|i| a * (2.0 * std::f32::consts::PI * freq * i as f32 / SR).sin()).collect()
}

fn run(set: &[(usize, f32)], mut x: Vec<f32>) -> Vec<f32> {
    let mut d = Dsp::new();
    d.set_sample_rate(SR);
    d.reset();
    let n = norms(set);
    for chunk in x.chunks_mut(480) { d.process(&n, chunk); }
    x
}

fn tail(x: &[f32]) -> &[f32] { &x[x.len() / 2..] }
fn rms_db(x: &[f32]) -> f32 { 10.0 * (x.iter().map(|v| v * v).sum::<f32>() / x.len() as f32).max(1e-20).log10() }
fn peak_db(x: &[f32]) -> f32 { 20.0 * x.iter().fold(0f32, |m, v| m.max(v.abs())).max(1e-10).log10() }

/// The strongest frequency between `lo` and `hi` Hz (Goertzel scan, 1 Hz steps).
fn peak_freq(x: &[f32], lo: u32, hi: u32) -> f32 {
    let mut best = (0.0f64, 0u32);
    for f in lo..=hi {
        let w = 2.0 * std::f64::consts::PI * f as f64 / SR as f64;
        let c = 2.0 * w.cos();
        let (mut s1, mut s2) = (0.0f64, 0.0f64);
        for &v in x { let s0 = v as f64 + c * s1 - s2; s2 = s1; s1 = s0; }
        let power = s1 * s1 + s2 * s2 - c * s1 * s2;
        if power > best.0 { best = (power, f); }
    }
    best.1 as f32
}

#[test]
fn everything_off_is_bit_exact() {
    let x = sine(440.0, -12.0, 0.5);
    assert_eq!(run(&[], x.clone()), x);
}

#[test]
fn voice_switch_off_ignores_voice_settings() {
    let x = sine(440.0, -12.0, 0.5);
    let y = run(&[(p::PITCH, 7.0), (p::ROBOT_MIX, 1.0), (p::ECHO_MIX, 1.0)], x.clone());
    assert_eq!(y, x);
}

#[test]
fn pitch_up_an_octave_doubles_frequency() {
    let y = run(&[(p::VOICE_ON, 1.0), (p::PITCH, 12.0)], sine(220.0, -12.0, 1.0));
    let f = peak_freq(tail(&y), 150, 700);
    assert!((f - 440.0).abs() < 8.0, "got {f} Hz");
}

#[test]
fn pitch_down_five_semitones() {
    let y = run(&[(p::VOICE_ON, 1.0), (p::PITCH, -5.0)], sine(440.0, -12.0, 1.0));
    let want = 440.0 * 2f32.powf(-5.0 / 12.0);
    let f = peak_freq(tail(&y), 150, 700);
    assert!((f - want).abs() < 6.0, "got {f} Hz, want {want}");
}

/// Share of the signal's power that sits within 15 Hz of `f`.
fn purity(x: &[f32], f: f32) -> f32 {
    let total: f64 = x.iter().map(|v| (*v as f64).powi(2)).sum::<f64>() * x.len() as f64 / 2.0;
    let mut near = 0.0f64;
    for df in -15..=15 {
        let w = 2.0 * std::f64::consts::PI * (f as f64 + df as f64) / SR as f64;
        let c = 2.0 * w.cos();
        let (mut s1, mut s2) = (0.0f64, 0.0f64);
        for &v in x { let s0 = v as f64 + c * s1 - s2; s2 = s1; s1 = s0; }
        near += s1 * s1 + s2 * s2 - c * s1 * s2;
    }
    // Goertzel bins at 1 Hz spacing over a 0.5 s window overlap by about 2x.
    (near / total / 2.0) as f32
}

#[test]
fn pitch_shift_is_clean_on_a_steady_tone() {
    for (semis, f) in [(12.0f32, 220.0f32), (-7.0, 330.0), (4.0, 200.0)] {
        let y = run(&[(p::VOICE_ON, 1.0), (p::PITCH, semis)], sine(f, -12.0, 1.0));
        let want = f * 2f32.powf(semis / 12.0);
        let pure = purity(tail(&y), want);
        assert!(pure > 0.8, "{semis:+} st: only {:.0}% of the power is at {want:.0} Hz", pure * 100.0);
    }
}

#[test]
fn whole_chain_is_cheap_enough_for_real_time() {
    let set: Vec<(usize, f32)> = vec![(p::EXP_ON, 1.0), (p::DEESS_ON, 1.0), (p::COMP_ON, 1.0), (p::VOICE_ON, 1.0), (p::PITCH, -5.0),
        (p::ROBOT_MIX, 0.5), (p::WOBBLE, 0.5), (p::DRIVE, 0.3), (p::LOW_CUT, 300.0), (p::HIGH_CUT, 4000.0),
        (p::ECHO_MIX, 0.3), (p::VERB_MIX, 0.3), (p::LIM_ON, 1.0)];
    let x: Vec<f32> = (0..(SR as usize * 10)).map(|i| (i as f32 * 0.031).sin() * 0.3 + (i as f32 * 0.0071).sin() * 0.2).collect();
    let t = std::time::Instant::now();
    let _ = run(&set, x);
    let secs = t.elapsed().as_secs_f32();
    // 10 s of audio must take well under 1 s (under 10% of one core); usually it's about 1%.
    assert!(secs < 1.0, "10 s of audio took {secs:.2} s");
    println!("10 s of audio with everything on: {:.1} ms", secs * 1000.0);
}

#[test]
fn pitch_shift_keeps_loudness() {
    let x = sine(300.0, -12.0, 1.0);
    let y = run(&[(p::VOICE_ON, 1.0), (p::PITCH, 4.0)], x.clone());
    let diff = rms_db(tail(&y)) - rms_db(tail(&x));
    assert!(diff.abs() < 3.0, "loudness changed by {diff} dB");
}

#[test]
fn compressor_reduces_loud_signal_by_ratio() {
    // -6 dBFS sine (peaks), threshold -20, ratio 4: peaks should sit near -20 + 14/4 = -16.5.
    let y = run(&[(p::COMP_ON, 1.0), (p::COMP_THRESH, -20.0), (p::COMP_RATIO, 4.0), (p::COMP_ATTACK, 1.0), (p::COMP_RELEASE, 200.0)], sine(200.0, -6.0, 1.0));
    let pk = peak_db(tail(&y));
    assert!(pk < -12.0 && pk > -20.0, "peak {pk} dB");
}

#[test]
fn compressor_leaves_quiet_signal_alone() {
    let x = sine(200.0, -40.0, 0.5);
    let y = run(&[(p::COMP_ON, 1.0), (p::COMP_THRESH, -20.0), (p::COMP_RATIO, 4.0)], x.clone());
    assert!((rms_db(tail(&y)) - rms_db(tail(&x))).abs() < 0.1);
}

#[test]
fn compressor_makeup_adds_gain() {
    let x = sine(200.0, -40.0, 0.5);
    let y = run(&[(p::COMP_ON, 1.0), (p::COMP_MAKEUP, 6.0)], x.clone());
    let diff = rms_db(tail(&y)) - rms_db(tail(&x));
    assert!((diff - 6.0).abs() < 0.2, "makeup {diff}");
}

#[test]
fn limiter_holds_the_ceiling() {
    let y = run(&[(p::LIM_ON, 1.0), (p::LIM_CEILING, -3.0)], sine(150.0, 6.0, 0.5));
    assert!(peak_db(&y) <= -2.99, "peak {}", peak_db(&y));
}

#[test]
fn limiter_leaves_signal_under_ceiling_alone() {
    let x = sine(150.0, -12.0, 0.5);
    let y = run(&[(p::LIM_ON, 1.0), (p::LIM_CEILING, -1.0)], x.clone());
    assert_eq!(x, y);
}

#[test]
fn expander_turns_down_quiet_sounds() {
    // -60 dB peaks, threshold -50, ratio 2: about 10 dB quieter.
    let x = sine(200.0, -60.0, 1.0);
    let y = run(&[(p::EXP_ON, 1.0), (p::EXP_THRESH, -50.0), (p::EXP_RATIO, 2.0)], x.clone());
    let diff = rms_db(tail(&y)) - rms_db(tail(&x));
    assert!(diff < -7.0 && diff > -14.0, "expander changed level by {diff} dB");
}

#[test]
fn expander_leaves_speech_level_alone() {
    let x = sine(200.0, -15.0, 1.0);
    let y = run(&[(p::EXP_ON, 1.0), (p::EXP_THRESH, -50.0), (p::EXP_RATIO, 2.0)], x.clone());
    assert!((rms_db(tail(&y)) - rms_db(tail(&x))).abs() < 0.3);
}

#[test]
fn deesser_cuts_hiss_but_not_voice() {
    let set = [(p::DEESS_ON, 1.0), (p::DEESS_FREQ, 5000.0), (p::DEESS_THRESH, -35.0), (p::DEESS_RANGE, 10.0)];
    let hiss = sine(7500.0, -12.0, 1.0);
    let voice = sine(250.0, -12.0, 1.0);
    let hiss_drop = rms_db(tail(&run(&set, hiss.clone()))) - rms_db(tail(&hiss));
    let voice_drop = rms_db(tail(&run(&set, voice.clone()))) - rms_db(tail(&voice));
    assert!(hiss_drop < -6.0, "hiss only dropped {hiss_drop} dB");
    assert!(voice_drop.abs() < 0.5, "voice changed {voice_drop} dB");
}

#[test]
fn phone_band_cuts_lows_and_highs() {
    let set = [(p::VOICE_ON, 1.0), (p::LOW_CUT, 400.0), (p::HIGH_CUT, 3400.0)];
    let low = rms_db(tail(&run(&set, sine(80.0, -12.0, 0.5)))) - rms_db(&sine(80.0, -12.0, 0.5));
    let mid = rms_db(tail(&run(&set, sine(1200.0, -12.0, 0.5)))) - rms_db(&sine(1200.0, -12.0, 0.5));
    let high = rms_db(tail(&run(&set, sine(10_000.0, -12.0, 0.5)))) - rms_db(&sine(10_000.0, -12.0, 0.5));
    assert!(low < -30.0 && high < -30.0, "low {low}, high {high}");
    assert!(mid.abs() < 1.5, "mid {mid}");
}

#[test]
fn echo_repeats_after_the_delay() {
    let mut x = vec![0f32; 48_000];
    x[100] = 0.5;
    let y = run(&[(p::VOICE_ON, 1.0), (p::ECHO_MIX, 0.5), (p::ECHO_TIME, 100.0), (p::ECHO_FEEDBACK, 0.0)], x);
    let at = 100 + 4800;
    let around = y[at - 3..at + 4].iter().fold(0f32, |m, v| m.max(v.abs()));
    assert!(around > 0.05, "no echo at 100 ms (max {around})");
    assert!(y[1000..at - 10].iter().all(|v| v.abs() < 1e-3), "sound before the echo");
}

#[test]
fn reverb_adds_a_tail_at_about_voice_level() {
    let mut x = sine(300.0, -12.0, 0.3);
    x.extend(std::iter::repeat(0.0).take(24_000));
    let y = run(&[(p::VOICE_ON, 1.0), (p::VERB_MIX, 1.0), (p::VERB_SIZE, 0.6)], x);
    let body = rms_db(&y[4800..14_000]);
    let tail = rms_db(&y[14_400 + 2400..14_400 + 7200]);
    assert!(tail > -60.0, "no reverb tail ({tail} dB)");
    assert!(body > -20.0 && body < -3.0, "wet level {body} dB");
}

#[test]
fn robot_wobble_drive_stay_bounded() {
    let set = [(p::VOICE_ON, 1.0), (p::ROBOT_MIX, 1.0), (p::WOBBLE, 1.0), (p::DRIVE, 1.0)];
    let y = run(&set, sine(200.0, 0.0, 0.5));
    assert!(y.iter().all(|v| v.is_finite() && v.abs() <= 1.5));
    assert!(rms_db(tail(&y)) > -20.0);
}

#[test]
fn everything_at_extremes_with_noise_and_garbage_stays_finite() {
    let mut seed = 12345u32;
    let mut x: Vec<f32> = (0..96_000).map(|_| { seed = seed.wrapping_mul(1664525).wrapping_add(1013904223); (seed as f32 / u32::MAX as f32) * 4.0 - 2.0 }).collect();
    x[500] = f32::NAN;
    x[900] = f32::INFINITY;
    for set in [0.0f32, 1.0] {
        let all: Vec<(usize, f32)> = (0..COUNT).map(|i| (i, PARAMS[i].min + set * (PARAMS[i].max - PARAMS[i].min))).collect();
        let all: Vec<(usize, f32)> = all.into_iter().map(|(i, v)| if PARAMS[i].kind == p::Kind::Toggle { (i, 1.0) } else { (i, v) }).collect();
        let y = run(&all, x.clone());
        assert!(y.iter().all(|v| v.is_finite() && v.abs() <= 4.0));
    }
}

#[test]
fn works_at_other_sample_rates() {
    for sr in [16_000.0f32, 44_100.0, 96_000.0, 192_000.0, 384_000.0] {
        let mut d = Dsp::new();
        d.set_sample_rate(sr);
        let n = norms(&[(p::VOICE_ON, 1.0), (p::PITCH, 5.0), (p::ECHO_MIX, 0.5), (p::ECHO_TIME, 1000.0), (p::VERB_MIX, 0.5), (p::WOBBLE, 1.0), (p::LIM_ON, 1.0)]);
        let mut x: Vec<f32> = (0..(sr as usize)).map(|i| (i as f32 * 0.05).sin() * 0.5).collect();
        for c in x.chunks_mut(1024) { d.process(&n, c); }
        assert!(x.iter().all(|v| v.is_finite()), "rate {sr}");
    }
}

#[test]
fn param_names_are_short_unique_and_spaceless() {
    let mut seen = std::collections::HashSet::new();
    for prm in &PARAMS {
        assert!(prm.name.len() <= 15 && !prm.name.contains(' '), "{}", prm.name);
        assert!(seen.insert(prm.name), "duplicate {}", prm.name);
        let back = prm.to_value(prm.default_norm());
        assert!((back - prm.default).abs() < 1e-3, "{} default round-trip", prm.name);
    }
}

// ---- The VST2 interface, called the way Equalizer APO calls it. ----

type Dispatcher = extern "C" fn(*mut c_void, i32, i32, isize, *mut c_void, f32) -> isize;
type Process = extern "C" fn(*mut c_void, *const *const f32, *mut *mut f32, i32);
type SetParam = extern "C" fn(*mut c_void, i32, f32);
type GetParam = extern "C" fn(*mut c_void, i32) -> f32;

unsafe fn field<T: Copy>(e: *mut c_void, offset: usize) -> T { unsafe { *((e as *const u8).add(offset) as *const T) } }

fn string(dispatch: Dispatcher, e: *mut c_void, op: i32, index: i32) -> String {
    let mut buf = [0u8; 256];
    dispatch(e, op, index, 0, buf.as_mut_ptr() as *mut c_void, 0.0);
    let n = buf.iter().position(|&b| b == 0).unwrap();
    String::from_utf8_lossy(&buf[..n]).into_owned()
}

#[test]
fn vst2_interface_matches_host_layout() {
    let e = wavefix_fx::VSTPluginMain(None) as *mut c_void;
    assert!(!e.is_null());
    unsafe {
        assert_eq!(field::<i32>(e, 0), 0x5673_7450);
        let dispatch: Dispatcher = field(e, 8);
        let set: SetParam = field(e, 24);
        let get: GetParam = field(e, 32);
        assert_eq!(field::<i32>(e, 44), COUNT as i32); // numParams
        assert_eq!(field::<i32>(e, 48), 1); // numInputs
        assert_eq!(field::<i32>(e, 52), 1); // numOutputs
        assert_ne!(field::<i32>(e, 56) & (1 << 4), 0); // canReplacing
        let process: Process = field(e, 120); // processReplacing

        dispatch(e, 0, 0, 0, std::ptr::null_mut(), 0.0);
        dispatch(e, 10, 0, 0, std::ptr::null_mut(), 48_000.0);
        dispatch(e, 11, 0, 512, std::ptr::null_mut(), 0.0);
        dispatch(e, 12, 0, 1, std::ptr::null_mut(), 0.0);

        assert_eq!(string(dispatch, e, 45, 0), "WaveFix Voice FX");
        assert_eq!(string(dispatch, e, 8, p::PITCH as i32), "Pitch");

        // Set by name like Equalizer APO does, then read back.
        for i in 0..COUNT as i32 {
            if string(dispatch, e, 8, i) == "LimOn" { set(e, i, 1.0); }
            if string(dispatch, e, 8, i) == "LimCeiling" { set(e, i, PARAMS[p::LIM_CEILING].to_norm(-6.0)); }
        }
        assert_eq!(get(e, p::LIM_ON as i32), 1.0);
        assert_eq!(string(dispatch, e, 7, p::LIM_CEILING as i32), "-6.0");

        // In-place processing (same buffer in and out), longer than the scratch buffer.
        let mut buf: Vec<f32> = (0..10_000).map(|i| (i as f32 * 0.01).sin()).collect();
        let ptr = buf.as_mut_ptr();
        let ins = [ptr as *const f32];
        let mut outs = [ptr];
        process(e, ins.as_ptr(), outs.as_mut_ptr(), buf.len() as i32);
        let limit = 10f32.powf(-6.0 / 20.0) + 1e-6;
        assert!(buf.iter().all(|v| v.abs() <= limit));

        dispatch(e, 12, 0, 0, std::ptr::null_mut(), 0.0);
        dispatch(e, 1, 0, 0, std::ptr::null_mut(), 0.0);
    }
}
