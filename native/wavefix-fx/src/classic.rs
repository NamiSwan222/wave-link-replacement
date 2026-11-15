//! The voice changer's effects (from the "Voice Shift" voice changer app): a two-head delay-line
//! pitch shifter, a robot (ring modulator plus a short metallic comb), chorus, drive, a radio
//! band, echo and a Freeverb-style reverb, then output gain and a soft limiter.
//!
//! Every effect works on mono f32 samples. Buffers are made in `set_sample_rate` (never on the
//! audio thread), and processing never allocates, locks or panics.

use std::f32::consts::PI;

/// One voice: which effects are on and how strong they are (real units).
#[derive(Clone, Copy, Debug, PartialEq)]
pub struct ClassicParams {
    /// Pitch shift in semitones (-12..12). 0 = off.
    pub pitch_semitones: f32,
    /// Robot mix 0..1.
    pub robot_mix: f32,
    /// Robot ring-modulator frequency, Hz.
    pub robot_freq: f32,
    /// Chorus mix 0..1.
    pub chorus_mix: f32,
    /// Drive 0..1 (1x..40x into a soft clipper).
    pub drive: f32,
    /// Radio band-pass amount 0..1.
    pub telephone: f32,
    /// Echo wet volume 0..1. 0 = echo off.
    pub echo_volume: f32,
    /// Echo time in seconds (0.05..1).
    pub echo_time: f32,
    /// Echo feedback 0..0.95.
    pub echo_feedback: f32,
    /// Reverb wet 0..1. 0 = reverb off.
    pub reverb_wet: f32,
    /// Reverb room size 0..1.
    pub reverb_room: f32,
    /// Reverb damping 0..1.
    pub reverb_damp: f32,
    /// Output gain in dB (-12..12).
    pub gain_db: f32,
}

impl Default for ClassicParams {
    fn default() -> Self {
        Self {
            pitch_semitones: 0.0, robot_mix: 0.0, robot_freq: 90.0, chorus_mix: 0.0, drive: 0.0, telephone: 0.0,
            echo_volume: 0.0, echo_time: 0.3, echo_feedback: 0.5, reverb_wet: 0.0, reverb_room: 0.5, reverb_damp: 0.5,
            gain_db: 0.0,
        }
    }
}

// ---------------------------------------------------------------- pitch

/// Two read heads sweep through a delay line at a speed set by the pitch ratio; each head fades
/// in and out with a sine window so the jumps back are never heard.
struct PitchShifter {
    buf: Vec<f32>,
    write: usize,
    phase: f32, // 0..1, position of head A in the grain
    grain: f32, // grain length in samples
}

impl PitchShifter {
    fn new(sr: f32) -> Self {
        let grain = (sr * 0.040).max(64.0); // 40 ms grains
        Self { buf: vec![0.0; (grain as usize) * 4 + 8], write: 0, phase: 0.0, grain }
    }

    fn read(&self, delay: f32) -> f32 {
        let n = self.buf.len();
        let pos = (self.write as f32 - delay).rem_euclid(n as f32);
        let i0 = pos.floor() as usize % n;
        let i1 = (i0 + 1) % n;
        let f = pos - pos.floor();
        self.buf[i0] * (1.0 - f) + self.buf[i1] * f
    }

    fn process(&mut self, x: f32, ratio: f32) -> f32 {
        let n = self.buf.len();
        self.buf[self.write] = x;
        self.write = (self.write + 1) % n;
        if (ratio - 1.0).abs() < 1e-4 {
            return x;
        }
        // The delay changes by (1 - ratio) samples per sample; over one grain a head moves `grain` samples.
        self.phase = (self.phase + (1.0 - ratio) / self.grain).rem_euclid(1.0);
        let pb = (self.phase + 0.5).rem_euclid(1.0);
        let da = 2.0 + self.phase * self.grain;
        let db = 2.0 + pb * self.grain;
        // Sine windows half a period apart: the two (mostly unrelated) heads' powers add to ~1.
        self.read(da) * (PI * self.phase).sin() + self.read(db) * (PI * pb).sin()
    }

    fn clear(&mut self) {
        self.buf.iter_mut().for_each(|s| *s = 0.0);
        self.phase = 0.0;
    }
}

// ---------------------------------------------------------------- filters and delay lines

#[derive(Default, Clone, Copy)]
struct Biquad { b0: f32, b1: f32, b2: f32, a1: f32, a2: f32, z1: f32, z2: f32 }

impl Biquad {
    fn bandpass(sr: f32, f0: f32, q: f32) -> Self {
        let w = 2.0 * PI * f0 / sr;
        let alpha = w.sin() / (2.0 * q);
        let a0 = 1.0 + alpha;
        Self { b0: alpha / a0, b1: 0.0, b2: -alpha / a0, a1: -2.0 * w.cos() / a0, a2: (1.0 - alpha) / a0, ..Default::default() }
    }
    fn process(&mut self, x: f32) -> f32 {
        let y = self.b0 * x + self.z1;
        self.z1 = self.b1 * x - self.a1 * y + self.z2;
        self.z2 = self.b2 * x - self.a2 * y;
        y
    }
    fn clear(&mut self) { self.z1 = 0.0; self.z2 = 0.0; }
}

struct DelayLine { buf: Vec<f32>, pos: usize }

impl DelayLine {
    fn new(len: usize) -> Self { Self { buf: vec![0.0; len.max(2)], pos: 0 } }
    fn read(&self, delay: f32) -> f32 {
        let n = self.buf.len();
        let d = delay.clamp(1.0, (n - 2) as f32);
        let p = (self.pos as f32 - d).rem_euclid(n as f32);
        let i0 = p.floor() as usize % n;
        let i1 = (i0 + 1) % n;
        let f = p - p.floor();
        self.buf[i0] * (1.0 - f) + self.buf[i1] * f
    }
    fn push(&mut self, x: f32) {
        self.buf[self.pos] = x;
        self.pos = (self.pos + 1) % self.buf.len();
    }
    fn clear(&mut self) { self.buf.iter_mut().for_each(|s| *s = 0.0); }
}

// ---------------------------------------------------------------- reverb (Freeverb-style)

struct Comb { buf: Vec<f32>, i: usize, store: f32 }
impl Comb {
    fn process(&mut self, x: f32, feedback: f32, damp: f32) -> f32 {
        let out = self.buf[self.i];
        self.store = out * (1.0 - damp) + self.store * damp;
        self.buf[self.i] = x + self.store * feedback;
        self.i = (self.i + 1) % self.buf.len();
        out
    }
}

struct AllPass { buf: Vec<f32>, i: usize }
impl AllPass {
    fn process(&mut self, x: f32) -> f32 {
        let b = self.buf[self.i];
        let out = b - x;
        self.buf[self.i] = x + b * 0.5;
        self.i = (self.i + 1) % self.buf.len();
        out
    }
}

struct Reverb { combs: Vec<Comb>, allpasses: Vec<AllPass> }

impl Reverb {
    fn new(sr: f32) -> Self {
        // Freeverb's public tunings (given for 44.1 kHz), scaled to the stream rate.
        let scale = sr / 44_100.0;
        let comb_t = [1116, 1188, 1277, 1356, 1422, 1491, 1557, 1617];
        let ap_t = [556, 441, 341, 225];
        Self {
            combs: comb_t.iter().map(|&t| Comb { buf: vec![0.0; ((t as f32 * scale) as usize).max(1)], i: 0, store: 0.0 }).collect(),
            allpasses: ap_t.iter().map(|&t| AllPass { buf: vec![0.0; ((t as f32 * scale) as usize).max(1)], i: 0 }).collect(),
        }
    }
    fn process(&mut self, x: f32, room: f32, damp: f32) -> f32 {
        // Freeverb: feedback = room * 0.28 + 0.7, damping scaled by 0.4, input gain 0.015.
        let fb = room * 0.28 + 0.7;
        let d = damp * 0.4;
        let input = x * 0.015;
        let mut out = 0.0;
        for c in &mut self.combs { out += c.process(input, fb, d); }
        for a in &mut self.allpasses { out = a.process(out); }
        out * 3.0 // wet scale 3, so a wet of 1/3 is about unity
    }
    fn clear(&mut self) {
        for c in &mut self.combs { c.buf.iter_mut().for_each(|s| *s = 0.0); c.store = 0.0; }
        for a in &mut self.allpasses { a.buf.iter_mut().for_each(|s| *s = 0.0); }
    }
}

// ---------------------------------------------------------------- the chain

pub struct ClassicVoice {
    sr: f32,
    pitch: PitchShifter,
    robot_phase: f32,
    robot_comb: DelayLine,
    chorus: DelayLine,
    chorus_phase: f32,
    tele_lo: Biquad,
    tele_hi: Biquad,
    echo: DelayLine,
    reverb: Reverb,
    smoothed: ClassicParams,
}

impl ClassicVoice {
    pub fn new(sr: f32) -> Self {
        let sr = if sr.is_finite() && sr >= 8000.0 { sr } else { 48_000.0 };
        Self {
            sr,
            pitch: PitchShifter::new(sr),
            robot_phase: 0.0,
            robot_comb: DelayLine::new((sr * 0.05) as usize), // room for the lowest robot tone (20 Hz)
            chorus: DelayLine::new((sr * 0.05) as usize),
            chorus_phase: 0.0,
            tele_lo: Biquad::bandpass(sr, 1000.0, 0.9),
            tele_hi: Biquad::bandpass(sr, 1800.0, 1.2),
            echo: DelayLine::new((sr * 2.0) as usize),
            reverb: Reverb::new(sr),
            smoothed: ClassicParams::default(),
        }
    }

    /// Rebuilds the buffers for a new rate (not on the audio thread).
    pub fn set_sample_rate(&mut self, sr: f32) { *self = Self::new(sr); }

    pub fn reset(&mut self) {
        self.pitch.clear();
        self.robot_phase = 0.0;
        self.robot_comb.clear();
        self.chorus.clear();
        self.chorus_phase = 0.0;
        self.tele_lo.clear();
        self.tele_hi.clear();
        self.echo.clear();
        self.reverb.clear();
        self.smoothed = ClassicParams::default();
    }

    /// Processes one block in place.
    pub fn process(&mut self, block: &mut [f32], target: &ClassicParams) {
        if block.is_empty() { return; }
        // Glide every parameter toward its target so slider moves and voice changes don't click.
        let k = (block.len() as f32 / (self.sr * 0.03)).min(1.0);
        let s = &mut self.smoothed;
        let lerp = |a: &mut f32, b: f32| *a += (b - *a) * k;
        lerp(&mut s.robot_mix, target.robot_mix);
        lerp(&mut s.robot_freq, target.robot_freq);
        lerp(&mut s.chorus_mix, target.chorus_mix);
        lerp(&mut s.drive, target.drive);
        lerp(&mut s.telephone, target.telephone);
        lerp(&mut s.echo_volume, target.echo_volume);
        lerp(&mut s.echo_time, target.echo_time);
        lerp(&mut s.echo_feedback, target.echo_feedback);
        lerp(&mut s.reverb_wet, target.reverb_wet);
        lerp(&mut s.reverb_room, target.reverb_room);
        lerp(&mut s.reverb_damp, target.reverb_damp);
        lerp(&mut s.gain_db, target.gain_db);
        s.pitch_semitones = target.pitch_semitones; // the pitch heads move continuously already
        let p = *s;

        let ratio = 2f32.powf(p.pitch_semitones.clamp(-24.0, 24.0) / 12.0);
        let gain = 10f32.powf(p.gain_db / 20.0);
        let drive_gain = 1.0 + p.drive * 39.0; // 1x..40x
        let robot_inc = p.robot_freq / self.sr;
        let chorus_inc = 0.6 / self.sr; // 0.6 Hz sweep
        let echo_delay = (p.echo_time * self.sr).max(1.0);
        let comb_d = self.sr / p.robot_freq.max(20.0);

        for x in block.iter_mut() {
            let mut v = if x.is_finite() { *x } else { 0.0 };

            v = self.pitch.process(v, ratio);

            // Robot: ring modulation plus a short metallic comb tuned to the same tone.
            if p.robot_mix > 0.001 {
                self.robot_phase = (self.robot_phase + robot_inc).fract();
                let carrier = (2.0 * PI * self.robot_phase).sin();
                let metallic = v + self.robot_comb.read(comb_d) * 0.6;
                self.robot_comb.push(metallic);
                let robo = metallic * carrier * 1.4;
                v = v * (1.0 - p.robot_mix) + robo * p.robot_mix;
            }

            // Chorus: two taps around 15-20 ms, swept in opposite directions.
            self.chorus.push(v);
            if p.chorus_mix > 0.001 {
                self.chorus_phase = (self.chorus_phase + chorus_inc).fract();
                let m = (2.0 * PI * self.chorus_phase).sin();
                let d1 = self.sr * (0.015 + 0.005 * m);
                let d2 = self.sr * (0.020 - 0.005 * m);
                let wet = (self.chorus.read(d1) + self.chorus.read(d2)) * 0.5;
                v = v * (1.0 - 0.5 * p.chorus_mix) + wet * p.chorus_mix;
            }

            // Drive: saturate, then trim by sqrt(gain) so more drive doesn't mean a louder voice.
            if p.drive > 0.001 {
                v = (v * drive_gain).tanh() / drive_gain.sqrt();
            }

            // Radio: a narrow band around 1-1.8 kHz.
            if p.telephone > 0.001 {
                let band = self.tele_hi.process(self.tele_lo.process(v)) * 3.0;
                v = v * (1.0 - p.telephone) + band * p.telephone;
            }

            // Echo: a delay with feedback.
            let echoed = self.echo.read(echo_delay);
            self.echo.push(v + echoed * p.echo_feedback.min(0.95));
            v += echoed * p.echo_volume;

            if p.reverb_wet > 0.001 {
                let r = self.reverb.process(v, p.reverb_room, p.reverb_damp);
                v += r * p.reverb_wet;
            }

            v *= gain;
            let out = soft_limit(v);
            *x = if out.is_finite() { out } else { 0.0 };
        }
    }
}

/// Keeps peaks under 1.0 without hard clipping.
fn soft_limit(x: f32) -> f32 {
    if x.abs() < 0.8 { x } else { x.signum() * (0.8 + 0.2 * ((x.abs() - 0.8) / 0.2).tanh()) }
}
