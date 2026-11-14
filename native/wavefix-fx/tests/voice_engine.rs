//! Measures each of the voice changer's new effects: pitch that keeps the voice's character,
//! formant, harmonics, octave layer, detune, breath, auto-tune, vocoder, bitcrusher,
//! modulation, distortion types, echo styles and radio/telephone.

use wavefix_fx::dsp::Dsp;
use wavefix_fx::params::{self as p, COUNT, PARAMS};
use wavefix_fx::voice::Fft;

const SR: f32 = 48_000.0;
const TAU: f32 = 2.0 * std::f32::consts::PI;

fn norms(set: &[(usize, f32)]) -> [f32; COUNT] {
    let mut n: [f32; COUNT] = std::array::from_fn(|i| PARAMS[i].default_norm());
    for &(i, v) in set { n[i] = PARAMS[i].to_norm(v); }
    n
}

fn run(set: &[(usize, f32)], mut x: Vec<f32>) -> Vec<f32> {
    let mut d = Dsp::new();
    d.set_sample_rate(SR);
    d.reset();
    let mut all = vec![(p::VOICE_ON, 1.0)];
    all.extend_from_slice(set);
    let n = norms(&all);
    for chunk in x.chunks_mut(480) { d.process(&n, chunk); }
    x
}

/// A voice-like "ah": pitch `f0`, 40 harmonics shaped by formants at 700, 1200 and 2600 Hz.
fn vowel(f0: f32, secs: f32) -> Vec<f32> {
    let n = (SR * secs) as usize;
    let mut out = vec![0.0; n];
    for h in 1..=40 {
        let f = f0 * h as f32;
        if f > 9000.0 { break; }
        let shape = |c: f32, w: f32| (-((f - c) / w).powi(2)).exp();
        let a = (shape(700.0, 160.0) + 0.6 * shape(1200.0, 220.0) + 0.25 * shape(2600.0, 300.0) + 0.02) / (h as f32).sqrt();
        for (i, v) in out.iter_mut().enumerate() { *v += a * (TAU * f * i as f32 / SR).sin(); }
    }
    let peak = out.iter().fold(0f32, |m, v| m.max(v.abs()));
    out.iter_mut().for_each(|v| *v *= 0.25 / peak);
    out
}

/// Magnitude spectrum of the middle 4096 samples (Hann window). Bin width ~11.7 Hz.
fn spectrum(x: &[f32]) -> Vec<f32> {
    const N: usize = 4096;
    let start = x.len() / 2 - N / 2;
    let fft = Fft::new(N);
    let mut re: Vec<f32> = (0..N).map(|i| x[start + i] * (0.5 - 0.5 * (TAU * i as f32 / N as f32).cos())).collect();
    let mut im = vec![0.0; N];
    fft.run(&mut re, &mut im, false);
    (0..=N / 2).map(|k| (re[k] * re[k] + im[k] * im[k]).sqrt()).collect()
}
fn hz(bin: usize) -> f32 { bin as f32 * SR / 4096.0 }
fn bin(f: f32) -> usize { (f * 4096.0 / SR).round() as usize }

/// Energy-weighted mean frequency (brightness).
fn centroid(s: &[f32]) -> f32 {
    let (mut num, mut den) = (0.0, 0.0);
    for (k, m) in s.iter().enumerate() { num += hz(k) * m * m; den += m * m; }
    num / den
}

/// The frequency with the most energy in its +-3 Hz neighbourhood, between lo and hi.
fn peak_hz(s: &[f32], lo: f32, hi: f32) -> f32 {
    let (a, b) = (bin(lo), bin(hi));
    let k = (a..=b).max_by(|&i, &j| s[i].partial_cmp(&s[j]).unwrap()).unwrap();
    // Parabolic refinement.
    let (y0, y1, y2) = (s[k - 1], s[k], s[k + 1]);
    let d = 0.5 * (y0 - y2) / (y0 - 2.0 * y1 + y2);
    hz(k) + d * SR / 4096.0
}

/// Pitch from harmonic spacing: the median gap between the strong peaks below 3 kHz.
fn pitch_of(x: &[f32]) -> f32 {
    let s = spectrum(x);
    let max = s[bin(60.0)..bin(3000.0)].iter().fold(0f32, |m, v| m.max(*v));
    let mut peaks = vec![];
    for k in bin(60.0)..bin(3000.0) {
        if s[k] > 0.05 * max && s[k] >= s[k - 1] && s[k] >= s[k + 1] { peaks.push(peak_hz(&s, hz(k) - 8.0, hz(k) + 8.0)); }
    }
    let mut gaps: Vec<f32> = peaks.windows(2).map(|w| w[1] - w[0]).filter(|g| *g > 50.0).collect();
    gaps.sort_by(|a, b| a.partial_cmp(b).unwrap());
    if gaps.is_empty() { 0.0 } else { gaps[gaps.len() / 2] }
}

/// Where the vowel's main formant sits: the frequency of the strongest region after smoothing
/// the spectrum over ~400 Hz (so harmonics don't matter, only the overall shape).
fn formant_of(x: &[f32]) -> f32 {
    let s = spectrum(x);
    let w = bin(200.0);
    let mut best = (0.0, 0.0);
    for k in bin(300.0)..bin(3000.0) {
        let e: f32 = s[k - w..k + w].iter().map(|m| m * m).sum();
        if e > best.0 { best = (e, hz(k)); }
    }
    best.1
}

fn band_energy(s: &[f32], lo: f32, hi: f32) -> f32 { s[bin(lo)..=bin(hi)].iter().map(|m| m * m).sum() }
fn rms(x: &[f32]) -> f32 { (x.iter().map(|v| v * v).sum::<f32>() / x.len() as f32).sqrt() }
fn db(x: f32) -> f32 { 20.0 * x.max(1e-12).log10() }

#[test]
fn pitch_keeps_the_voice_character() {
    let x = vowel(150.0, 1.0);
    let y = run(&[(p::PITCH, 5.0)], x.clone());
    let want = 150.0 * 2f32.powf(5.0 / 12.0);
    let got = pitch_of(&y);
    assert!((got / want - 1.0).abs() < 0.02, "pitch {got:.1} Hz, wanted {want:.1}");
    let (f_in, f_out) = (formant_of(&x), formant_of(&y));
    assert!((f_out / f_in - 1.0).abs() < 0.15, "formant moved from {f_in:.0} to {f_out:.0} Hz");
}

#[test]
fn formant_changes_voice_size_but_not_pitch() {
    let x = vowel(150.0, 1.0);
    let down = run(&[(p::FORMANT, -5.0)], x.clone());
    let up = run(&[(p::FORMANT, 5.0)], x.clone());
    for (name, y) in [("down", &down), ("up", &up)] {
        let got = pitch_of(y);
        assert!((got / 150.0 - 1.0).abs() < 0.02, "formant {name} changed the pitch to {got:.1} Hz");
    }
    let (f_in, f_down, f_up) = (formant_of(&x), formant_of(&down), formant_of(&up));
    assert!(f_down < f_in * 0.85, "formant down: {f_in:.0} -> {f_down:.0} Hz");
    assert!(f_up > f_in * 1.15, "formant up: {f_in:.0} -> {f_up:.0} Hz");
}

#[test]
fn loudness_holds_through_the_voice_engine() {
    let x = vowel(150.0, 1.0);
    for set in [vec![(p::PITCH, 7.0)], vec![(p::PITCH, -7.0)], vec![(p::FORMANT, -6.0)], vec![(p::HARMONICS, 1.0)]] {
        let y = run(&set, x.clone());
        let d = db(rms(&y[24_000..])) - db(rms(&x[24_000..]));
        assert!(d.abs() < 3.0, "{set:?}: loudness changed by {d:.1} dB");
    }
}

#[test]
fn harmonics_make_the_voice_darker_or_brighter() {
    let x = vowel(150.0, 1.0);
    let c = centroid(&spectrum(&run(&[(p::PITCH, 0.02)], x.clone())));
    let dark = centroid(&spectrum(&run(&[(p::HARMONICS, -1.0)], x.clone())));
    let bright = centroid(&spectrum(&run(&[(p::HARMONICS, 1.0)], x.clone())));
    assert!(dark < c * 0.9 && bright > c * 1.1, "brightness: dark {dark:.0}, normal {c:.0}, bright {bright:.0} Hz");
}

#[test]
fn octave_layer_adds_a_voice_an_octave_away() {
    let x = vowel(200.0, 1.0);
    let base = spectrum(&run(&[(p::PITCH, 0.02)], x.clone()));
    let low = spectrum(&run(&[(p::OCTAVE, -1.0)], x.clone()));
    let e = |s: &[f32]| band_energy(s, 90.0, 110.0);
    assert!(e(&low) > e(&base) * 20.0, "no energy at 100 Hz (octave below 200 Hz): {} vs {}", e(&low), e(&base));
}

#[test]
fn detune_pulls_harmonics_off_pitch() {
    let x = vowel(150.0, 1.0);
    let on_grid = |s: &[f32]| {
        let total: f32 = band_energy(s, 100.0, 4000.0);
        let grid: f32 = (1..=26).map(|h| band_energy(s, 150.0 * h as f32 - 15.0, 150.0 * h as f32 + 15.0)).sum();
        grid / total
    };
    let clean = on_grid(&spectrum(&run(&[(p::PITCH, 0.02)], x.clone())));
    let detuned = on_grid(&spectrum(&run(&[(p::DETUNE, 1.0)], x.clone())));
    assert!(detuned < clean * 0.7, "harmonics on pitch: clean {clean:.2}, detuned {detuned:.2}");
}

#[test]
fn breath_adds_noise_between_the_harmonics() {
    let x = vowel(150.0, 1.0);
    let between = |s: &[f32]| (1..=20).map(|h| band_energy(s, 150.0 * h as f32 + 50.0, 150.0 * h as f32 + 100.0)).sum::<f32>();
    let clean = between(&spectrum(&run(&[(p::PITCH, 0.02)], x.clone())));
    let breathy = between(&spectrum(&run(&[(p::BREATH, 1.0)], x.clone())));
    assert!(breathy > clean * 5.0, "noise between harmonics: {clean} -> {breathy}");
}

#[test]
fn auto_tune_snaps_to_the_nearest_note() {
    // 150 Hz is between D3 (146.8 Hz) and D#3 (155.6 Hz), nearer D3.
    let x = vowel(150.0, 1.5);
    let y = run(&[(p::TUNE, 1.0)], x);
    let got = pitch_of(&y);
    assert!((got - 146.83).abs() < 1.5, "auto-tuned to {got:.1} Hz, wanted 146.8 (D3)");
}

#[test]
fn vocoder_plays_a_chord_on_your_note() {
    let x = vowel(150.0, 1.5);
    let s = spectrum(&run(&[(p::VOCODER, 1.0)], x));
    // Root D3 (146.8 Hz) and its fifth (220 Hz) must both be present.
    let root = band_energy(&s, 140.0, 154.0);
    let fifth = band_energy(&s, 213.0, 227.0);
    let gap = band_energy(&s, 170.0, 200.0);
    assert!(root > gap * 10.0 && fifth > gap * 10.0, "root {root}, fifth {fifth}, gap {gap}");
}

#[test]
fn bitcrusher_steps_the_waveform() {
    let x = vowel(150.0, 0.5);
    let y = run(&[(p::CRUSH, 1.0)], x);
    let tail = &y[12_000..];
    let mut levels: Vec<i32> = tail.iter().map(|v| (v * 1e6) as i32).collect();
    levels.sort(); levels.dedup();
    assert!(levels.len() < 40, "{} different levels: not crushed", levels.len());
    let holds = tail.windows(2).filter(|w| w[0] == w[1]).count();
    assert!(holds > tail.len() / 2, "samples aren't held (lower sample rate)");
}

#[test]
fn modulation_types_change_the_sound_and_stay_bounded() {
    let x = vowel(150.0, 1.0);
    for t in 1..=3 {
        let y = run(&[(p::MOD_TYPE, t as f32), (p::MOD_MIX, 1.0), (p::MOD_RATE, 1.0)], x.clone());
        let diff: f32 = y.iter().zip(&x).map(|(a, b)| (a - b).powi(2)).sum::<f32>() / y.len() as f32;
        assert!(diff > 1e-5, "mod type {t} didn't change the sound");
        assert!(y.iter().all(|v| v.is_finite() && v.abs() < 2.0), "mod type {t} out of range");
    }
}

#[test]
fn megaphone_is_a_narrow_horn_band() {
    // Broadband noise shows the band shape: lows and highs well below the horn's middle.
    let mut seed = 7u32;
    let x: Vec<f32> = (0..48_000).map(|_| { seed = seed.wrapping_mul(1_664_525).wrapping_add(1_013_904_223); ((seed >> 8) as f32 / 8_388_608.0 - 1.0) * 0.05 }).collect();
    let s = spectrum(&run(&[(p::DRIVE, 0.3), (p::DRIVE_TYPE, 2.0)], x));
    let per_hz = |lo: f32, hi: f32| band_energy(&s, lo, hi) / (hi - lo);
    let (low, mid, high) = (per_hz(80.0, 200.0), per_hz(1000.0, 2000.0), per_hz(9000.0, 15000.0));
    assert!(low < mid / 30.0 && high < mid / 30.0, "low {low:.3e}, mid {mid:.3e}, high {high:.3e}");
}

#[test]
fn fuzz_and_overdrive_stay_bounded() {
    let x: Vec<f32> = vowel(150.0, 0.5).iter().map(|v| v * 4.0).collect();
    for t in 0..=2 {
        let y = run(&[(p::DRIVE, 1.0), (p::DRIVE_TYPE, t as f32)], x.clone());
        assert!(y.iter().all(|v| v.abs() <= 1.2), "drive type {t} peaks above 1.2");
    }
}

/// A short click, then silence: where the repeats land.
fn click_echo(style: f32, time_ms: f32) -> Vec<f32> {
    let mut x = vec![0.0; (SR * 1.6) as usize];
    for v in &mut x[0..48] { *v = 0.5; }
    run(&[(p::ECHO_MIX, 1.0), (p::ECHO_TIME, time_ms), (p::ECHO_FEEDBACK, 0.0), (p::ECHO_STYLE, style)], x)
}
fn energy_at(y: &[f32], ms: f32) -> f32 { let i = (ms * SR / 1000.0) as usize; y[i - 100..i + 200].iter().map(|v| v * v).sum() }

#[test]
fn echo_styles_put_repeats_where_they_should() {
    // Slapback: one repeat at 100 ms (from a 100 ms echo time) and nothing later.
    let y = click_echo(1.0, 100.0);
    assert!(energy_at(&y, 100.0) > 1e-3 && energy_at(&y, 200.0) < 1e-6, "slapback");
    // Rhythm: repeats at 200, 400 and 600 ms.
    let y = click_echo(3.0, 200.0);
    for ms in [200.0, 400.0, 600.0] { assert!(energy_at(&y, ms) > 1e-3, "rhythm repeat at {ms} ms missing"); }
    // Canyon: repeats at 1x, 1.7x and 2.6x of 300 ms.
    let y = click_echo(2.0, 300.0);
    for ms in [300.0, 510.0, 780.0] { assert!(energy_at(&y, ms) > 1e-4, "canyon repeat at {ms} ms missing"); }
}

#[test]
fn radio_and_phone_cut_lows_and_highs() {
    let x = vowel(150.0, 1.0);
    let s_in = spectrum(&x);
    for t in 1..=4 {
        let s = spectrum(&run(&[(p::RADIO, t as f32)], x.clone()));
        let low = band_energy(&s, 70.0, 160.0) / band_energy(&s, 600.0, 2000.0);
        let low_in = band_energy(&s_in, 70.0, 160.0) / band_energy(&s_in, 600.0, 2000.0);
        assert!(low < low_in * 0.5, "radio {t}: lows {low_in:.3} -> {low:.3}");
    }
}

#[test]
fn walkie_talkie_squelches_after_you_stop_talking() {
    let mut x = vowel(150.0, 0.6);
    x.extend(vec![0.0; (SR * 0.6) as usize]);
    let y = run(&[(p::RADIO, 2.0)], x);
    let after = &y[(SR * 0.62) as usize..(SR * 0.8) as usize];
    let later = &y[(SR * 1.05) as usize..(SR * 1.15) as usize];
    assert!(rms(after) > 0.01 && rms(later) < 0.001, "squelch burst {:.4}, later {:.4}", rms(after), rms(later));
}

#[test]
fn everything_on_at_once_is_finite_and_real_time() {
    let x = vowel(150.0, 2.0);
    let set = [
        (p::PITCH, 7.0), (p::FORMANT, -6.0), (p::HARMONICS, 0.8), (p::OCTAVE, -0.6), (p::DETUNE, 0.5), (p::BREATH, 0.5),
        (p::TUNE, 1.0), (p::VOCODER, 0.5), (p::CRUSH, 0.4), (p::MOD_TYPE, 3.0), (p::MOD_MIX, 0.6), (p::DRIVE, 0.5),
        (p::DRIVE_TYPE, 1.0), (p::RADIO, 3.0), (p::ECHO_MIX, 0.5), (p::ECHO_STYLE, 2.0), (p::VERB_MIX, 0.4), (p::ROBOT_MIX, 0.4),
    ];
    let t = std::time::Instant::now();
    let y = run(&set, x);
    let secs = t.elapsed().as_secs_f64();
    assert!(y.iter().all(|v| v.is_finite() && v.abs() <= 4.0));
    // 2 s of audio must take well under 2 s (one channel, with room for others). The limit
    // leaves slack for slow shared build machines; on a normal PC it takes about 0.1 s.
    assert!(secs < 1.0, "2 s of audio took {secs:.2} s");
}
