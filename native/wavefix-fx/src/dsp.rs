//! The effects. Everything is allocated up front (sized for 192 kHz) so processing never
//! allocates, locks or panics: this code runs on the Windows audio engine's real-time thread.

use crate::bridge;
use crate::params::{self as p, COUNT, PARAMS};
use std::f32::consts::PI;

const MAX_RATE: f32 = 192_000.0;

fn db_to_lin(db: f32) -> f32 { 10f32.powf(db / 20.0) }
fn lin_to_db(x: f32) -> f32 { 20.0 * (x.max(1e-9)).log10() }
/// One-pole smoothing coefficient for a time constant in milliseconds.
fn coef(ms: f32, sr: f32) -> f32 { (-1.0 / (ms.max(0.01) * 0.001 * sr)).exp() }

#[derive(Clone, Copy, Default)]
struct Biquad { b0: f32, b1: f32, b2: f32, a1: f32, a2: f32, z1: f32, z2: f32 }

impl Biquad {
    fn set(&mut self, sr: f32, f: f32, q: f32, high: bool) {
        let w = 2.0 * PI * (f / sr).clamp(1e-5, 0.49);
        let (s, c) = w.sin_cos();
        let alpha = s / (2.0 * q);
        let a0 = 1.0 + alpha;
        let (b0, b1, b2) = if high { ((1.0 + c) / 2.0, -(1.0 + c), (1.0 + c) / 2.0) } else { ((1.0 - c) / 2.0, 1.0 - c, (1.0 - c) / 2.0) };
        self.b0 = b0 / a0; self.b1 = b1 / a0; self.b2 = b2 / a0;
        self.a1 = -2.0 * c / a0; self.a2 = (1.0 - alpha) / a0;
    }
    #[inline]
    fn run(&mut self, x: f32) -> f32 {
        let y = self.b0 * x + self.z1;
        self.z1 = self.b1 * x - self.a1 * y + self.z2;
        self.z2 = self.b2 * x - self.a2 * y;
        y
    }
    fn reset(&mut self) { self.z1 = 0.0; self.z2 = 0.0; }
}

/// A delay line whose length is a power of two, read with cubic interpolation.
struct Delay { buf: Vec<f32>, mask: usize, w: usize }

impl Delay {
    fn new(min_len: usize) -> Self {
        let len = min_len.next_power_of_two();
        Delay { buf: vec![0.0; len], mask: len - 1, w: 0 }
    }
    #[inline]
    fn push(&mut self, x: f32) { self.w = (self.w + 1) & self.mask; self.buf[self.w] = x; }
    /// The sample written `d` samples ago (0 = the latest), with fractional delays interpolated.
    #[inline]
    fn read(&self, d: f32) -> f32 {
        let d = d.clamp(1.0, (self.mask - 3) as f32);
        let di = d as usize;
        let t = d - di as f32;
        let m = self.mask;
        let i = self.w.wrapping_sub(di) & m;
        let xm1 = self.buf[(i + 1) & m];
        let x0 = self.buf[i];
        let x1 = self.buf[i.wrapping_sub(1) & m];
        let x2 = self.buf[i.wrapping_sub(2) & m];
        // Hermite between x0 (delay di) and x1 (delay di + 1).
        let c1 = 0.5 * (x1 - xm1);
        let c2 = xm1 - 2.5 * x0 + 2.0 * x1 - 0.5 * x2;
        let c3 = 0.5 * (x2 - xm1) + 1.5 * (x0 - x1);
        ((c3 * t + c2) * t + c1) * t + x0
    }
    #[inline]
    fn read_int(&self, d: usize) -> f32 { self.buf[self.w.wrapping_sub(d) & self.mask] }
    fn clear(&mut self) { self.buf.iter_mut().for_each(|s| *s = 0.0); }
}

/// Freeverb-style comb and allpass for the reverb.
struct Comb { buf: Vec<f32>, len: usize, i: usize, filt: f32 }
impl Comb {
    fn new() -> Self { Comb { buf: vec![0.0; 8192], len: 1, i: 0, filt: 0.0 } }
    #[inline]
    fn run(&mut self, x: f32, fb: f32, damp: f32) -> f32 {
        let y = self.buf[self.i];
        self.filt = y * (1.0 - damp) + self.filt * damp;
        self.buf[self.i] = x + self.filt * fb;
        self.i += 1;
        if self.i >= self.len { self.i = 0; }
        y
    }
}
struct Allpass { buf: Vec<f32>, len: usize, i: usize }
impl Allpass {
    fn new() -> Self { Allpass { buf: vec![0.0; 4096], len: 1, i: 0 } }
    #[inline]
    fn run(&mut self, x: f32) -> f32 {
        let b = self.buf[self.i];
        self.buf[self.i] = x + b * 0.5;
        self.i += 1;
        if self.i >= self.len { self.i = 0; }
        b - x
    }
}

/// Pitch shifting with two crossfading read heads on a delay line. Each time a head starts a
/// new grain, its start point is moved by up to one voice period so it lines up with the other
/// head (highest correlation). That keeps the crossfade in phase: no warbly cancellation.
struct PitchShifter {
    buf: Delay,
    /// Delay of each head in samples, and where it is in its grain (0..1).
    d: [f32; 2],
    phase: [f32; 2],
    ratio: f32,
    /// Grain sweep length, alignment search span and compare length, in samples.
    range: f32,
    search: usize,
    compare: usize,
    step: usize,
}

impl PitchShifter {
    fn new() -> Self {
        PitchShifter {
            buf: Delay::new((0.09 * MAX_RATE) as usize),
            d: [0.0; 2], phase: [0.0, 0.5], ratio: 1.0,
            range: 1920.0, search: 576, compare: 384, step: 1,
        }
    }

    fn reset(&mut self) {
        self.buf.clear();
        self.ratio = 0.0; // forces prepare() to place the heads again
    }

    fn start(&self, up: bool) -> f32 { if up { 2.0 + self.range } else { 2.0 } }

    fn prepare(&mut self, sr: f32, ratio: f32) {
        self.range = 0.04 * sr;
        self.search = (0.012 * sr) as usize;
        self.compare = (0.008 * sr) as usize;
        self.step = ((sr / 48_000.0) as usize).max(1);
        if (ratio - self.ratio).abs() < 1e-6 { return; }
        let first = self.ratio == 0.0 || (self.ratio > 1.0) != (ratio > 1.0);
        self.ratio = ratio;
        if first {
            // Head 0 starts a grain, head 1 is halfway through its own.
            let up = ratio > 1.0;
            let sweep = (1.0 - ratio) * self.grain_len();
            self.phase = [0.0, 0.5];
            self.d = [self.start(up), self.start(up) + 0.5 * sweep];
        }
    }

    /// Samples per grain: the time a head takes to sweep `range` at this pitch ratio.
    fn grain_len(&self) -> f32 { (self.range / (self.ratio - 1.0).abs().max(1e-3)).min(48_000.0 * 4.0) }

    #[inline]
    fn run(&mut self, x: f32) -> f32 {
        self.buf.push(x);
        let len = self.grain_len();
        let rate = 1.0 - self.ratio; // change of delay per sample
        let up = self.ratio > 1.0;
        let mut out = 0.0;
        for h in 0..2 {
            self.d[h] += rate;
            self.phase[h] += 1.0 / len;
            if self.phase[h] >= 1.0 {
                self.phase[h] -= 1.0;
                self.d[h] = self.aligned_start(up, self.d[1 - h]);
            }
            let g = (PI * self.phase[h]).sin();
            out += self.buf.read(self.d[h]) * g * g;
        }
        out
    }

    /// The grain start (within one search span) that best matches what the other head plays now.
    fn aligned_start(&self, up: bool, other: f32) -> f32 {
        let base = self.start(up);
        let other = other.max(1.0) as usize;
        let mut best = (f32::MIN, 0usize);
        let mut k = 0;
        while k <= self.search {
            let cand = base as usize + k;
            let (mut num, mut energy) = (0.0f32, 1e-9f32);
            let mut j = 0;
            while j < self.compare {
                let a = self.buf.read_int(cand + j);
                num += a * self.buf.read_int(other + j);
                energy += a * a;
                j += self.step;
            }
            let score = num / energy.sqrt();
            if score > best.0 { best = (score, k); }
            k += self.step;
        }
        base + best.1 as f32
    }
}

const COMB_LENS: [f32; 4] = [1116.0, 1188.0, 1277.0, 1356.0];
const ALLPASS_LENS: [f32; 2] = [556.0, 441.0];
/// Brings the reverb's wet signal to about the loudness of the dry voice.
const VERB_GAIN: f32 = 0.35;

/// Parameter values for one block, in real units.
#[derive(Clone, Copy, PartialEq)]
struct Settings([f32; COUNT]);

impl Settings {
    fn on(&self, i: usize) -> bool { self.0[i] >= 0.5 }
    fn v(&self, i: usize) -> f32 { self.0[i] }
}

pub struct Dsp {
    sr: f32,
    last: Option<Settings>,

    // Expander
    exp_env: f32,
    exp_gain: f32,
    // De-esser
    /// Linkwitz-Riley crossover: the low and high bands add back up to a flat response.
    de_lp: [Biquad; 2],
    de_hp: [Biquad; 2],
    de_env: f32,
    de_gain: f32,
    // Compressor (gain reduction in dB)
    comp_gr: f32,
    pitch: PitchShifter,
    // Wobble (vibrato)
    wob: Delay,
    wob_phase: f32,
    // Robot (ring modulator)
    robot_phase: f32,
    // Phone/megaphone band
    low_cut: [Biquad; 2],
    high_cut: [Biquad; 2],
    // Echo
    echo: Delay,
    echo_lp: f32,
    // Reverb
    combs: [Comb; 4],
    allpasses: [Allpass; 2],
    // Limiter gain
    lim_gain: f32,
    // Soundboard (audio from the app)
    sb: bridge::Reader,
    // AI voice (audio to and from the app's RVC engine)
    rvc: bridge::RvcLink,
}

impl Dsp {
    pub fn new() -> Self {
        let mut d = Dsp {
            sr: 48_000.0,
            last: None,
            exp_env: 0.0, exp_gain: 1.0,
            de_lp: [Biquad::default(); 2], de_hp: [Biquad::default(); 2], de_env: 0.0, de_gain: 1.0,
            comp_gr: 0.0,
            pitch: PitchShifter::new(),
            wob: Delay::new((0.008 * MAX_RATE) as usize),
            wob_phase: 0.0,
            robot_phase: 0.0,
            low_cut: [Biquad::default(); 2],
            high_cut: [Biquad::default(); 2],
            echo: Delay::new((1.05 * MAX_RATE) as usize),
            echo_lp: 0.0,
            combs: [Comb::new(), Comb::new(), Comb::new(), Comb::new()],
            allpasses: [Allpass::new(), Allpass::new()],
            lim_gain: 1.0,
            sb: bridge::Reader::default(),
            rvc: bridge::RvcLink::default(),
        };
        d.set_sample_rate(48_000.0);
        d
    }

    pub fn set_sample_rate(&mut self, sr: f32) {
        self.sr = if sr.is_finite() && sr >= 8000.0 { sr.min(MAX_RATE) } else { 48_000.0 };
        let scale = self.sr / 44_100.0;
        for (c, base) in self.combs.iter_mut().zip(COMB_LENS) {
            c.len = ((base * scale) as usize).clamp(1, c.buf.len());
            c.i = 0;
        }
        for (a, base) in self.allpasses.iter_mut().zip(ALLPASS_LENS) {
            a.len = ((base * scale) as usize).clamp(1, a.buf.len());
            a.i = 0;
        }
        self.last = None; // recompute filters
    }

    /// Clears all tails (called when the host starts processing).
    pub fn reset(&mut self) {
        self.exp_env = 0.0; self.exp_gain = 1.0;
        for f in self.de_lp.iter_mut().chain(self.de_hp.iter_mut()) { f.reset(); }
        self.de_env = 0.0; self.de_gain = 1.0;
        self.comp_gr = 0.0;
        self.pitch.reset();
        self.wob.clear(); self.wob_phase = 0.0;
        self.robot_phase = 0.0;
        for f in self.low_cut.iter_mut().chain(self.high_cut.iter_mut()) { f.reset(); }
        self.echo.clear(); self.echo_lp = 0.0;
        for c in &mut self.combs { c.buf.iter_mut().for_each(|s| *s = 0.0); c.filt = 0.0; }
        for a in &mut self.allpasses { a.buf.iter_mut().for_each(|s| *s = 0.0); }
        self.lim_gain = 1.0;
    }

    /// Processes one block in place. `norm` holds the 0..1 parameter values.
    pub fn process(&mut self, norm: &[f32; COUNT], buf: &mut [f32]) {
        let sb_on = PARAMS[p::SB_ON].to_value(norm[p::SB_ON]) >= 0.5;
        let rvc_on = PARAMS[p::RVC_ON].to_value(norm[p::RVC_ON]) >= 0.5;
        let header = if sb_on || rvc_on { bridge::attach_rt() } else { None };
        self.process_with(norm, buf, header);
    }

    /// Same as `process`, with the app bridge given explicitly (tests use their own).
    pub fn process_with(&mut self, norm: &[f32; COUNT], buf: &mut [f32], bridge: Option<&bridge::Header>) {
        let mut vals = [0f32; COUNT];
        for i in 0..COUNT { vals[i] = PARAMS[i].to_value(norm[i]); }
        let s = Settings(vals);
        if self.last != Some(s) {
            self.update_filters(&s);
            self.last = Some(s);
        }
        let sr = self.sr;

        // Per-block constants.
        let exp = s.on(p::EXP_ON);
        let exp_thr = s.v(p::EXP_THRESH);
        let exp_ratio = s.v(p::EXP_RATIO).max(1.0);
        let (exp_att, exp_rel) = (coef(1.0, sr), coef(80.0, sr));
        let (exp_open, exp_close) = (coef(2.0, sr), coef(60.0, sr));

        let deess = s.on(p::DEESS_ON) && s.v(p::DEESS_RANGE) > 0.05;
        let de_thr = s.v(p::DEESS_THRESH);
        let de_range = s.v(p::DEESS_RANGE);
        let (de_att, de_rel) = (coef(0.5, sr), coef(40.0, sr));
        let (de_down, de_up) = (coef(1.0, sr), coef(30.0, sr));

        let comp = s.on(p::COMP_ON);
        let c_thr = s.v(p::COMP_THRESH);
        let c_slope = 1.0 - 1.0 / s.v(p::COMP_RATIO).max(1.0);
        let c_att = coef(s.v(p::COMP_ATTACK), sr);
        let c_rel = coef(s.v(p::COMP_RELEASE), sr);
        let c_makeup = db_to_lin(s.v(p::COMP_MAKEUP));
        const KNEE: f32 = 6.0;

        let voice = s.on(p::VOICE_ON);
        let semis = s.v(p::PITCH);
        let shift = voice && semis.abs() > 0.01;
        let ratio = 2f32.powf(semis / 12.0);
        if shift { self.pitch.prepare(sr, ratio); }

        let robot_mix = if voice { s.v(p::ROBOT_MIX) } else { 0.0 };
        let robot_step = 2.0 * PI * s.v(p::ROBOT_FREQ) / sr;

        let wobble = if voice { s.v(p::WOBBLE) } else { 0.0 };
        let wob_step = 2.0 * PI * s.v(p::WOBBLE_RATE) / sr;
        let wob_base = 0.0015 * sr;
        let wob_depth = 0.0045 * sr * wobble;

        let drive = if voice { s.v(p::DRIVE) } else { 0.0 };
        let pre = 1.0 + drive * 24.0;
        let post = 1.0 / pre.sqrt();

        let low_cut = voice && s.v(p::LOW_CUT) > 25.0;
        let high_cut = voice && s.v(p::HIGH_CUT) < 19_500.0;

        let echo_mix = if voice { s.v(p::ECHO_MIX) } else { 0.0 };
        let echo_d = ((s.v(p::ECHO_TIME) * 0.001 * sr) as usize).clamp(1, self.echo.mask - 1);
        let echo_fb = s.v(p::ECHO_FEEDBACK).clamp(0.0, 0.95);

        let verb_mix = if voice { s.v(p::VERB_MIX) } else { 0.0 };
        let verb_fb = 0.7 + 0.28 * s.v(p::VERB_SIZE);
        let verb_damp = 0.4 - 0.2 * s.v(p::VERB_SIZE);

        let lim = s.on(p::LIM_ON);
        let ceil = db_to_lin(s.v(p::LIM_CEILING));
        let lim_rel = coef(80.0, sr);

        for x in buf.iter_mut() {
            let mut y = if x.is_finite() { *x } else { 0.0 };

            if exp {
                let a = y.abs();
                let c = if a > self.exp_env { exp_att } else { exp_rel };
                self.exp_env = a + c * (self.exp_env - a);
                let lvl = lin_to_db(self.exp_env);
                let gr = if lvl < exp_thr { ((exp_thr - lvl) * (exp_ratio - 1.0)).min(60.0) } else { 0.0 };
                let target = db_to_lin(-gr);
                let c = if target > self.exp_gain { exp_open } else { exp_close };
                self.exp_gain = target + c * (self.exp_gain - target);
                y *= self.exp_gain;
            }

            if deess {
                let lo = self.de_lp[0].run(y);
                let lo = self.de_lp[1].run(lo);
                let hp = self.de_hp[0].run(y);
                let hp = self.de_hp[1].run(hp);
                let a = hp.abs();
                let c = if a > self.de_env { de_att } else { de_rel };
                self.de_env = a + c * (self.de_env - a);
                let over = lin_to_db(self.de_env) - de_thr;
                let target = db_to_lin(-(over * 0.8).clamp(0.0, de_range));
                let c = if target < self.de_gain { de_down } else { de_up };
                self.de_gain = target + c * (self.de_gain - target);
                // Only the hiss band is turned down; the rest of the voice is untouched.
                y = lo + hp * self.de_gain;
            }

            if comp {
                let lvl = lin_to_db(y.abs());
                let over = lvl - c_thr;
                let gr = if over <= -KNEE / 2.0 {
                    0.0
                } else if over >= KNEE / 2.0 {
                    over * c_slope
                } else {
                    let k = over + KNEE / 2.0;
                    c_slope * k * k / (2.0 * KNEE)
                };
                let c = if gr > self.comp_gr { c_att } else { c_rel };
                self.comp_gr = gr + c * (self.comp_gr - gr);
                y *= db_to_lin(-self.comp_gr) * c_makeup;
            }
            *x = y;
        }

        // AI voice (RVC): the polished voice goes to WaveFix's AI engine and comes back
        // converted, a fixed delay later. The voice changer effects then apply on top.
        if s.on(p::RVC_ON) {
            self.rvc.process(bridge, sr, buf, s.on(p::RVC_MUTE));
        }

        for x in buf.iter_mut() {
            let mut y = *x;
            if voice {
                if shift { y = self.pitch.run(y); }
                if wobble > 0.001 {
                    self.wob.push(y);
                    self.wob_phase += wob_step;
                    if self.wob_phase > 2.0 * PI { self.wob_phase -= 2.0 * PI; }
                    y = self.wob.read(1.0 + wob_base + wob_depth * (0.5 + 0.5 * self.wob_phase.sin()));
                }
                if robot_mix > 0.001 {
                    self.robot_phase += robot_step;
                    if self.robot_phase > 2.0 * PI { self.robot_phase -= 2.0 * PI; }
                    // Ring modulation; 1.4 keeps the loudness about the same.
                    y = y * (1.0 - robot_mix) + y * self.robot_phase.sin() * 1.4 * robot_mix;
                }
                if drive > 0.001 {
                    y = (y * pre).tanh() * post;
                }
                if low_cut { for f in &mut self.low_cut { y = f.run(y); } }
                if high_cut { for f in &mut self.high_cut { y = f.run(y); } }
                if echo_mix > 0.001 {
                    // Read the echo before writing this sample so a delay of N means N samples.
                    let e = self.echo.read_int(echo_d - 1);
                    self.echo_lp += 0.45 * (e - self.echo_lp);
                    self.echo.push(y + echo_fb * self.echo_lp);
                    y += echo_mix * e;
                }
                if verb_mix > 0.001 {
                    let input = y * 0.2;
                    let mut wet = 0.0;
                    for c in &mut self.combs { wet += c.run(input, verb_fb, verb_damp); }
                    for a in &mut self.allpasses { wet = a.run(wet); }
                    y = y * (1.0 - 0.5 * verb_mix) + wet * VERB_GAIN * verb_mix;
                }
            }

            *x = y;
        }

        // Soundboard and other app audio: after the voice changer (sounds aren't pitched),
        // before the limiter (so a loud clip can't clip the mic).
        if s.on(p::SB_ON) {
            if let Some(h) = bridge { self.sb.mix(h, sr as u32, buf, 1.0); }
        }

        for x in buf.iter_mut() {
            let mut y = *x;
            if lim {
                let a = y.abs();
                let need = if a > ceil { ceil / a } else { 1.0 };
                self.lim_gain = if need < self.lim_gain { need } else { need + lim_rel * (self.lim_gain - need) };
                y = (y * self.lim_gain).clamp(-ceil, ceil);
            }
            // Kill denormals and any runaway value before it reaches the audio engine.
            if !y.is_finite() || y.abs() < 1e-20 { y = 0.0; }
            *x = y.clamp(-4.0, 4.0);
        }

        // Keep feedback state free of denormals between blocks.
        flush(&mut self.echo_lp);
        flush(&mut self.de_env);
        flush(&mut self.exp_env);
        for c in &mut self.combs { flush(&mut c.filt); }
    }

    fn update_filters(&mut self, s: &Settings) {
        let sr = self.sr;
        for f in &mut self.de_lp { f.set(sr, s.v(p::DEESS_FREQ), 0.707, false); }
        for f in &mut self.de_hp { f.set(sr, s.v(p::DEESS_FREQ), 0.707, true); }
        // Two Butterworth-ish stages each: steep enough for a phone or megaphone sound.
        for f in &mut self.low_cut { f.set(sr, s.v(p::LOW_CUT), 0.707, true); }
        for f in &mut self.high_cut { f.set(sr, s.v(p::HIGH_CUT), 0.707, false); }
    }
}

fn flush(v: &mut f32) { if !v.is_finite() || v.abs() < 1e-20 { *v = 0.0; } }

impl Default for Dsp { fn default() -> Self { Self::new() } }
