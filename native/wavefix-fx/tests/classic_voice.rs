//! The voice changer (classic.rs) through the same Dsp the audio engine runs.

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
fn table_has_the_voice_changer_params() {
    assert_eq!(COUNT, 60);
    let names: Vec<&str> = PARAMS.iter().map(|q| q.name).collect();
    for n in ["VmOn", "VmPitch", "VmRobot", "VmRobotHz", "VmChorus", "VmDrive", "VmRadio", "VmEcho", "VmEchoTime", "VmEchoFb", "VmVerb", "VmRoom", "VmDamp", "VmGain"] {
        assert!(names.contains(&n), "missing {n}");
        assert!(n.len() <= 15 && !n.contains(' '), "{n}: names must fit VST hosts' buffers");
    }
    let mut sorted = names.clone();
    sorted.sort();
    sorted.dedup();
    assert_eq!(sorted.len(), names.len(), "parameter names must be unique");
}

#[test]
fn off_by_default_and_bit_exact() {
    let x = sine(440.0, -12.0, 0.5);
    assert_eq!(run(&[(p::VM_PITCH, 7.0), (p::VM_ROBOT, 1.0)], x.clone()), x, "VmOn off must not touch the sound");
}

#[test]
fn on_but_neutral_passes_the_voice() {
    let x = sine(440.0, -12.0, 1.0);
    let y = run(&[(p::VM_ON, 1.0)], x.clone());
    assert!((rms_db(tail(&y)) - rms_db(tail(&x))).abs() < 0.5);
    assert!((peak_freq(tail(&y), 300, 600) - 440.0).abs() < 3.0);
}

/// Power of `x` at frequency `f` (Goertzel), relative units.
fn power_at(x: &[f32], f: f32) -> f64 {
    let w = 2.0 * std::f64::consts::PI * f as f64 / SR as f64;
    let c = 2.0 * w.cos();
    let (mut s1, mut s2) = (0.0f64, 0.0f64);
    for &v in x { let s0 = v as f64 + c * s1 - s2; s2 = s1; s1 = s0; }
    s1 * s1 + s2 * s2 - c * s1 * s2
}

#[test]
fn pitch_up_an_octave() {
    // This two-head delay-line shifter splits a steady tone into sidebands around the target
    // (about +-25 Hz at an octave: its 40 ms grains), so check where the energy went, not one bin.
    let y = run(&[(p::VM_ON, 1.0), (p::VM_PITCH, 12.0)], sine(220.0, -12.0, 1.0));
    let t = tail(&y);
    let f = peak_freq(t, 150, 700);
    assert!((f - 440.0).abs() < 35.0, "energy should sit around 440 Hz, got {f} Hz");
    let near: f64 = (400..=480).step_by(5).map(|h| power_at(t, h as f32)).sum();
    assert!(power_at(t, 220.0) < near * 0.01, "the original 220 Hz should be gone");
}

#[test]
fn pitch_down_five_semitones() {
    let y = run(&[(p::VM_ON, 1.0), (p::VM_PITCH, -5.0)], sine(440.0, -12.0, 1.0));
    let want = 440.0 * 2f32.powf(-5.0 / 12.0);
    let f = peak_freq(tail(&y), 150, 700);
    assert!((f - want).abs() < 8.0, "got {f} Hz, want {want}");
}

#[test]
fn echo_repeats_after_the_voice_stops() {
    let mut x = sine(440.0, -12.0, 0.2);
    x.extend(vec![0.0; (SR * 0.8) as usize]);
    let y = run(&[(p::VM_ON, 1.0), (p::VM_ECHO, 0.8), (p::VM_ECHO_TIME, 300.0), (p::VM_ECHO_FB, 0.5)], x);
    // 0.3-0.5 s: the first repeat of a voice that stopped at 0.2 s.
    let rep = &y[(SR * 0.32) as usize..(SR * 0.48) as usize];
    assert!(rms_db(rep) > -30.0, "echo too quiet: {} dB", rms_db(rep));
}

#[test]
fn reverb_rings_on() {
    let mut x = sine(440.0, -12.0, 0.2);
    x.extend(vec![0.0; (SR * 0.6) as usize]);
    let dry = run(&[(p::VM_ON, 1.0)], x.clone());
    let wet = run(&[(p::VM_ON, 1.0), (p::VM_VERB, 0.6), (p::VM_ROOM, 0.9)], x);
    let after = |y: &[f32]| rms_db(&y[(SR * 0.3) as usize..(SR * 0.5) as usize]);
    assert!(after(&wet) > after(&dry) + 20.0, "wet {} vs dry {}", after(&wet), after(&dry));
}

#[test]
fn robot_and_radio_change_the_sound() {
    let x = sine(440.0, -12.0, 1.0);
    for (name, set) in [
        ("robot", vec![(p::VM_ON, 1.0), (p::VM_ROBOT, 0.85), (p::VM_ROBOT_HZ, 90.0)]),
        ("radio", vec![(p::VM_ON, 1.0), (p::VM_RADIO, 1.0)]),
        ("chorus", vec![(p::VM_ON, 1.0), (p::VM_CHORUS, 1.0)]),
        ("drive", vec![(p::VM_ON, 1.0), (p::VM_DRIVE, 0.6)]),
    ] {
        let y = run(&set, x.clone());
        let diff: f32 = tail(&y).iter().zip(tail(&x)).map(|(a, b)| (a - b) * (a - b)).sum::<f32>() / (x.len() / 2) as f32;
        assert!(diff > 1e-5, "{name} did nothing");
    }
}

#[test]
fn gain_and_soft_limit_keep_peaks_under_full_scale() {
    let y = run(&[(p::VM_ON, 1.0), (p::VM_GAIN, 12.0), (p::VM_DRIVE, 1.0)], sine(200.0, 0.0, 1.0));
    assert!(y.iter().all(|v| v.is_finite() && v.abs() <= 1.0));
}

#[test]
fn survives_bad_input_and_every_extreme() {
    let mut x = sine(300.0, 0.0, 0.5);
    x[1000] = f32::NAN;
    x[2000] = f32::INFINITY;
    let all_max: Vec<(usize, f32)> = (p::VM_ON..=p::VM_GAIN).map(|i| (i, PARAMS[i].max)).collect();
    let all_min: Vec<(usize, f32)> = std::iter::once((p::VM_ON, 1.0)).chain((p::VM_PITCH..=p::VM_GAIN).map(|i| (i, PARAMS[i].min))).collect();
    for set in [all_max, all_min] {
        let y = run(&set, x.clone());
        assert!(y.iter().all(|v| v.is_finite() && v.abs() <= 1.0));
    }
}

#[test]
fn every_sample_rate_works() {
    for sr in [8000.0, 44_100.0, 96_000.0, 192_000.0] {
        let mut d = Dsp::new();
        d.set_sample_rate(sr);
        d.reset();
        let n = norms(&[(p::VM_ON, 1.0), (p::VM_PITCH, -9.0), (p::VM_VERB, 0.3), (p::VM_ECHO, 0.3)]);
        let mut x: Vec<f32> = (0..(sr as usize / 2)).map(|i| (i as f32 * 0.05).sin() * 0.3).collect();
        for c in x.chunks_mut(512) { d.process(&n, c); }
        assert!(x.iter().all(|v| v.is_finite()), "{sr} Hz");
    }
}

