//! The spectral voice engine: pitch that keeps your voice's character, formant (voice size),
//! harmonics, an octave layer, detune, breath, auto-tune and a vocoder.
//!
//! Each frame (about 21 ms, 4x overlap) is taken to the frequency domain. A phase vocoder gives
//! every bin its true frequency; cepstral smoothing splits the magnitudes into the voice's
//! spectral envelope (its formants: "who" is talking) and the fine structure (the harmonics:
//! "which note"). The harmonics are moved to the new pitch, reshaped, then the envelope is laid
//! back on, stretched if the formant is changed. So pitch and voice size move independently.
//! Auto-tune and the vocoder use a YIN pitch detector on a decimated copy of the input.
//!
//! Everything is allocated up front; processing never allocates, locks or panics.

use std::f32::consts::PI;

const TWO_PI: f32 = 2.0 * PI;
const MAX_N: usize = 4096;

/// Radix-2 complex FFT, in place, with tables made once.
pub struct Fft { n: usize, rev: Vec<usize>, cos: Vec<f32>, sin: Vec<f32> }

impl Fft {
    pub fn new(n: usize) -> Fft {
        let bits = n.trailing_zeros();
        let rev = (0..n).map(|i| i.reverse_bits() >> (usize::BITS - bits)).collect();
        let cos = (0..n / 2).map(|i| (TWO_PI * i as f32 / n as f32).cos()).collect();
        let sin = (0..n / 2).map(|i| (TWO_PI * i as f32 / n as f32).sin()).collect();
        Fft { n, rev, cos, sin }
    }

    /// Forward (e^-i) or inverse (e^+i, unscaled) transform.
    pub fn run(&self, re: &mut [f32], im: &mut [f32], inverse: bool) {
        let n = self.n;
        for i in 0..n {
            let j = self.rev[i];
            if j > i { re.swap(i, j); im.swap(i, j); }
        }
        let sign = if inverse { 1.0 } else { -1.0 };
        let mut size = 2;
        while size <= n {
            let half = size / 2;
            let step = n / size;
            let mut start = 0;
            while start < n {
                for k in 0..half {
                    let wr = self.cos[k * step];
                    let wi = sign * self.sin[k * step];
                    let a = start + k;
                    let b = a + half;
                    let tr = re[b] * wr - im[b] * wi;
                    let ti = re[b] * wi + im[b] * wr;
                    re[b] = re[a] - tr; im[b] = im[a] - ti;
                    re[a] += tr; im[a] += ti;
                }
                start += size;
            }
            size <<= 1;
        }
    }
}

/// What the voice engine should do (real units).
#[derive(Clone, Copy, PartialEq)]
pub struct Shape {
    /// Pitch change in semitones (the voice's character stays).
    pub pitch: f32,
    /// Formant change in semitones: below 0 = bigger/deeper voice, above 0 = smaller.
    pub formant: f32,
    /// -1..1: darker (fundamental-heavy) .. brighter (upper harmonics).
    pub harmonics: f32,
    /// -1..1: an extra voice an octave below (negative) or above (positive), this loud.
    pub octave: f32,
    /// 0..1: pulls the harmonics off their pitch (metallic, alien).
    pub detune: f32,
    /// 0..1: breath noise shaped like the voice.
    pub breath: f32,
    /// 0..1: auto-tune strength (snaps to the nearest note; 1 = hard, robotic).
    pub tune: f32,
    /// 0..1: vocoder mix (your words played by a chord that follows your note).
    pub vocoder: f32,
}

impl Shape {
    /// True when the engine would change the sound (otherwise it isn't run at all).
    pub fn active(&self) -> bool {
        self.pitch.abs() > 0.01 || self.formant.abs() > 0.01 || self.harmonics.abs() > 0.01 || self.octave.abs() > 0.01
            || self.detune > 0.005 || self.breath > 0.005 || self.tune > 0.005 || self.vocoder > 0.005
    }
}

pub struct SpectralVoice {
    sr: f32,
    n: usize,
    hop: usize,
    half: usize,
    fft: Fft,
    window: Vec<f32>,
    in_fifo: Vec<f32>,
    out_fifo: Vec<f32>,
    accum: Vec<f32>,
    rover: usize,
    re: Vec<f32>,
    im: Vec<f32>,
    cre: Vec<f32>,
    cim: Vec<f32>,
    last_phase: Vec<f32>,
    sum_phase: Vec<f32>,
    car_phase: Vec<f32>,
    mag: Vec<f32>,
    freq: Vec<f32>,
    env: Vec<f32>,
    fine: Vec<f32>,
    car_mag: Vec<f32>,
    car_freq: Vec<f32>,
    // Peak-locked shifting: harmonic peaks, their regions, output spectrum, per-layer phases.
    peaks: Vec<usize>,
    bounds: Vec<usize>,
    acc_re: Vec<f32>,
    acc_im: Vec<f32>,
    phase2: Vec<f32>,
    stamp: Vec<u32>,
    stamp2: Vec<u32>,
    frame_no: u32,
    lifter: usize,
    rng: u32,
    // Pitch detection (YIN on a decimated copy).
    dec: usize,
    dec_acc: f32,
    dec_count: usize,
    pd: Vec<f32>,
    pd_w: usize,
    yin: Vec<f32>,
    /// Detected pitch in Hz (0 = not a voiced sound).
    pub f0: f32,
    tune_corr: f32,
    car_root: f32,
    started: bool,
}

const PD_LEN: usize = 640; // ~53 ms at the 12 kHz analysis rate

impl SpectralVoice {
    pub fn new() -> SpectralVoice {
        let mut v = SpectralVoice {
            sr: 48_000.0, n: 1024, hop: 256, half: 512,
            fft: Fft::new(1024),
            window: vec![0.0; MAX_N],
            in_fifo: vec![0.0; MAX_N], out_fifo: vec![0.0; MAX_N], accum: vec![0.0; 2 * MAX_N],
            rover: 0,
            re: vec![0.0; MAX_N], im: vec![0.0; MAX_N], cre: vec![0.0; MAX_N], cim: vec![0.0; MAX_N],
            last_phase: vec![0.0; MAX_N / 2 + 1], sum_phase: vec![0.0; MAX_N / 2 + 1], car_phase: vec![0.0; MAX_N / 2 + 1],
            mag: vec![0.0; MAX_N / 2 + 1], freq: vec![0.0; MAX_N / 2 + 1], env: vec![0.0; MAX_N / 2 + 1], fine: vec![0.0; MAX_N / 2 + 1],
            car_mag: vec![0.0; MAX_N / 2 + 1], car_freq: vec![0.0; MAX_N / 2 + 1],
            peaks: Vec::with_capacity(MAX_N / 2 + 2), bounds: Vec::with_capacity(MAX_N / 2 + 3),
            acc_re: vec![0.0; MAX_N / 2 + 1], acc_im: vec![0.0; MAX_N / 2 + 1], phase2: vec![0.0; MAX_N / 2 + 1],
            stamp: vec![0; MAX_N / 2 + 1], stamp2: vec![0; MAX_N / 2 + 1], frame_no: 0,
            lifter: 58, rng: 0x1234_5678,
            dec: 4, dec_acc: 0.0, dec_count: 0, pd: vec![0.0; PD_LEN], pd_w: 0, yin: vec![0.0; PD_LEN],
            f0: 0.0, tune_corr: 0.0, car_root: 110.0, started: false,
        };
        v.set_sample_rate(48_000.0);
        v
    }

    /// Delay the engine adds, in samples.
    pub fn latency(&self) -> usize { self.n - self.hop }

    pub fn set_sample_rate(&mut self, sr: f32) {
        self.sr = sr;
        // ~21 ms frames whatever the rate (1024 at 44.1/48 kHz).
        let mut n = 512;
        while (n as f32) < sr * 0.0205 && n < MAX_N { n *= 2; }
        self.n = n;
        self.hop = n / 4;
        self.half = n / 2;
        self.fft = Fft::new(n);
        for i in 0..n { self.window[i] = 0.5 - 0.5 * (TWO_PI * i as f32 / n as f32).cos(); }
        // Cepstral cut-off: keeps detail coarser than ~1.2 ms (formants), drops harmonics.
        self.lifter = ((0.0012 * sr) as usize).clamp(8, n / 4);
        self.dec = ((sr / 12_000.0).round() as usize).max(1);
        self.reset();
    }

    pub fn reset(&mut self) {
        for v in [&mut self.in_fifo, &mut self.out_fifo, &mut self.accum, &mut self.last_phase, &mut self.sum_phase, &mut self.car_phase, &mut self.pd, &mut self.phase2] {
            v.iter_mut().for_each(|x| *x = 0.0);
        }
        self.rover = self.latency();
        self.dec_acc = 0.0; self.dec_count = 0; self.pd_w = 0;
        self.f0 = 0.0; self.tune_corr = 0.0;
        self.started = false;
    }

    /// Processes one sample.
    #[inline]
    pub fn run(&mut self, x: f32, s: &Shape) -> f32 {
        // Decimated copy for the pitch detector (box average, enough for voice pitch).
        if s.tune > 0.005 || s.vocoder > 0.005 {
            self.dec_acc += x;
            self.dec_count += 1;
            if self.dec_count >= self.dec {
                self.pd[self.pd_w] = self.dec_acc / self.dec as f32;
                self.pd_w = (self.pd_w + 1) % PD_LEN;
                self.dec_acc = 0.0; self.dec_count = 0;
            }
        }
        let lat = self.latency();
        self.in_fifo[self.rover] = x;
        let y = self.out_fifo[self.rover - lat];
        self.rover += 1;
        if self.rover >= self.n {
            self.rover = lat;
            self.frame(s);
            self.in_fifo.copy_within(self.hop..self.n, 0);
        }
        y
    }

    fn rand(&mut self) -> f32 {
        // xorshift32 -> [0, 1)
        let mut r = self.rng;
        r ^= r << 13; r ^= r >> 17; r ^= r << 5;
        self.rng = r;
        (r >> 8) as f32 / 16_777_216.0
    }

    /// YIN pitch detection on the decimated buffer. 0 = no clear pitch.
    fn detect_pitch(&mut self) -> f32 {
        let fs = self.sr / self.dec as f32;
        let tau_min = (fs / 900.0) as usize;
        let tau_max = ((fs / 70.0) as usize).min(PD_LEN / 2);
        let w = PD_LEN - tau_max;
        let at = |i: usize, s: &Self| s.pd[(s.pd_w + i) % PD_LEN];
        let mut energy = 0.0;
        for i in 0..w { let v = at(i, self); energy += v * v; }
        if energy / (w as f32) < 1e-6 { return 0.0; } // about -60 dBFS: silence
        let mut running = 0.0;
        self.yin[0] = 1.0;
        for tau in 1..=tau_max {
            let mut d = 0.0;
            for i in 0..w { let e = at(i, self) - at(i + tau, self); d += e * e; }
            running += d;
            self.yin[tau] = if running > 0.0 { d * tau as f32 / running } else { 1.0 };
        }
        let mut tau = tau_min.max(2);
        while tau < tau_max {
            if self.yin[tau] < 0.15 {
                while tau + 1 < tau_max && self.yin[tau + 1] < self.yin[tau] { tau += 1; }
                let (a, b, c) = (self.yin[tau - 1], self.yin[tau], self.yin[tau + 1]);
                let den = a - 2.0 * b + c;
                let t = if den.abs() > 1e-9 { tau as f32 + 0.5 * (a - c) / den } else { tau as f32 };
                return fs / t;
            }
            tau += 1;
        }
        0.0
    }

    /// Adds one shifted copy of the voice (ratio, gain) to the output spectrum, peak by peak.
    /// `second` uses the octave layer's own phase memory.
    fn layer(&mut self, ratio: f32, gain: f32, second: bool, s: &Shape, bin_hz: f32, osamp: f32) {
        let half = self.half;
        let detune = s.detune.clamp(0.0, 1.0);
        let ref_bin = 500.0 / bin_hz;
        let frame = self.frame_no;
        for idx in 0..self.peaks.len() {
            let p = self.peaks[idx];
            let (a, b) = (self.bounds[idx], self.bounds[idx + 1]);
            let mut f_out = self.freq[p] * ratio;
            if detune > 0.0 {
                // Inharmonic stretch: higher partials go sharper (bell-like, metallic).
                f_out *= 1.0 + detune * 0.06 * (f_out * bin_hz / 200.0 + 1.0).log2();
            }
            let jp = f_out.round();
            if jp < 1.0 { continue; }
            if jp > half as f32 { break; }
            let jp = jp as usize;
            // The peak's output phase advances at its new frequency, once per frame per bin.
            let (phases, stamps) = if second { (&mut self.phase2, &mut self.stamp2) } else { (&mut self.sum_phase, &mut self.stamp) };
            if stamps[jp] != frame {
                stamps[jp] = frame;
                let mut ph = phases[jp] + TWO_PI * f_out / osamp;
                ph -= TWO_PI * (ph / TWO_PI).floor();
                phases[jp] = ph;
            }
            let peak_phase = phases[jp];
            let tilt = if s.harmonics.abs() > 0.01 { ((jp as f32) / ref_bin).powf(s.harmonics * 1.5) } else { 1.0 };
            let shift = jp as isize - p as isize;
            let base_in = self.last_phase[p];
            for k in a..b {
                let j = k as isize + shift;
                if j < 0 || j > half as isize { continue; }
                let j = j as usize;
                let m = self.fine[k] * gain * tilt;
                let ph = peak_phase + (self.last_phase[k] - base_in);
                self.acc_re[j] += m * ph.cos();
                self.acc_im[j] += m * ph.sin();
            }
        }
    }

    fn frame(&mut self, s: &Shape) {
        let (n, half, hop) = (self.n, self.half, self.hop);
        let osamp = (n / hop) as f32;
        let expect = TWO_PI * hop as f32 / n as f32;

        // ---- Analysis ----
        for i in 0..n { self.re[i] = self.in_fifo[i] * self.window[i]; self.im[i] = 0.0; }
        self.fft.run(&mut self.re, &mut self.im, false);
        for k in 0..=half {
            let (r, i) = (self.re[k], self.im[k]);
            self.mag[k] = (r * r + i * i).sqrt();
            let phase = i.atan2(r);
            let mut d = phase - self.last_phase[k] - k as f32 * expect;
            self.last_phase[k] = phase;
            d -= TWO_PI * (d / TWO_PI).round();
            self.freq[k] = k as f32 + d * osamp / TWO_PI; // true frequency, in bins
        }
        if !self.started {
            // First frame: no phase history yet, so start from the bin centres.
            for k in 0..=half { self.freq[k] = k as f32; self.sum_phase[k] = 0.0; }
            self.started = true;
        }

        // ---- Envelope (cepstral smoothing of the log magnitude) and fine structure ----
        for k in 0..=half { self.cre[k] = (self.mag[k] + 1e-6).ln(); self.cim[k] = 0.0; }
        for k in 1..half { self.cre[n - k] = self.cre[k]; self.cim[n - k] = 0.0; }
        self.fft.run(&mut self.cre, &mut self.cim, true);
        let l = self.lifter;
        for q in 0..n {
            let keep = q < l || q > n - l;
            self.cre[q] = if keep { self.cre[q] / n as f32 } else { 0.0 };
            self.cim[q] = 0.0;
        }
        self.fft.run(&mut self.cre, &mut self.cim, false);
        for k in 0..=half {
            self.env[k] = self.cre[k].clamp(-30.0, 30.0).exp();
            self.fine[k] = self.mag[k] / self.env[k].max(1e-9);
        }

        // ---- Pitch: shift, plus auto-tune correction ----
        let need_f0 = s.tune > 0.005 || s.vocoder > 0.005;
        if need_f0 { self.f0 = self.detect_pitch(); } else { self.f0 = 0.0; }
        let base = 2f32.powf(s.pitch / 12.0);
        if s.tune > 0.005 && self.f0 > 0.0 {
            let midi = 69.0 + 12.0 * (self.f0 * base / 440.0).log2();
            let want = (midi.round() - midi) * s.tune.min(1.0);
            // Strong settings snap fast (the robotic effect); gentle ones glide.
            let speed = 0.12 + 0.88 * s.tune * s.tune;
            self.tune_corr += (want - self.tune_corr) * speed;
        } else {
            self.tune_corr *= 0.9;
        }
        let ratio = base * 2f32.powf(self.tune_corr / 12.0);

        // ---- Harmonic peaks and their regions ----
        // Each peak (a harmonic, or a component of noise) owns the bins down to the lowest point
        // between it and its neighbours. Regions move as one block with their phases locked to
        // the peak (Laroche & Dolson's peak-locked pitch shifting), so a harmonic stays one clean
        // partial instead of smearing (the "phasey" sound) and keeps its level.
        let maxm = self.mag[..=half].iter().fold(0f32, |m, v| m.max(*v));
        self.peaks.clear();
        for k in 1..half {
            if self.mag[k] > self.mag[k - 1] && self.mag[k] >= self.mag[k + 1] && self.mag[k] > maxm * 1e-5 { self.peaks.push(k); }
        }
        self.bounds.clear();
        self.bounds.push(0);
        for i in 1..self.peaks.len() {
            let (lo, hi) = (self.peaks[i - 1], self.peaks[i]);
            let mut low = lo;
            for k in lo..=hi { if self.mag[k] < self.mag[low] { low = k; } }
            self.bounds.push(low.max(lo + 1));
        }
        self.bounds.push(half + 1);

        for j in 0..=half { self.acc_re[j] = 0.0; self.acc_im[j] = 0.0; }
        self.frame_no = self.frame_no.wrapping_add(1);
        let bin_hz = self.sr / n as f32;
        self.layer(ratio, 1.0, false, s, bin_hz, osamp);
        if s.octave.abs() > 0.01 {
            let r = ratio * if s.octave < 0.0 { 0.5 } else { 2.0 };
            self.layer(r, s.octave.abs().min(1.0), true, s, bin_hz, osamp);
        }

        // ---- Formant: lay the envelope back on, stretched ----
        let fr = 2f32.powf(s.formant / 12.0);
        let env_at = |me: &Self, j: usize| {
            let x = (j as f32 / fr).min(half as f32);
            let i = x as usize;
            let t = x - i as f32;
            let a = me.env[i];
            let b = me.env[(i + 1).min(half)];
            a + (b - a) * t
        };

        // ---- Vocoder carrier: a chord (root, fifth, octave) following your note ----
        let voc = s.vocoder.clamp(0.0, 1.0);
        if voc > 0.005 {
            if self.f0 > 0.0 {
                let midi = (69.0 + 12.0 * (self.f0 * base / 440.0).log2()).round();
                self.car_root = 440.0 * 2f32.powf((midi - 69.0) / 12.0);
            }
            for j in 0..=half { self.car_mag[j] = 0.0; self.car_freq[j] = j as f32; }
            for (note, g) in [(1.0f32, 1.0f32), (1.4983, 0.7), (2.0, 0.5)] {
                let fc = self.car_root * note;
                let mut h = 1.0f32;
                while fc * h < self.sr * 0.45 {
                    let b = fc * h / bin_hz;
                    let j = b.round() as usize;
                    if j > half { break; }
                    self.car_mag[j] += g / h.powf(0.6);
                    self.car_freq[j] = b;
                    h += 1.0;
                }
            }
            // Same harmonic energy as the voice, so the mix keeps its loudness.
            let (mut ev, mut ec) = (0.0, 0.0);
            for j in 0..=half { ev += self.acc_re[j] * self.acc_re[j] + self.acc_im[j] * self.acc_im[j]; ec += self.car_mag[j] * self.car_mag[j]; }
            let g = if ec > 1e-12 { (ev / ec).sqrt() } else { 0.0 };
            for j in 0..=half { self.car_mag[j] *= g; }
        }

        // ---- Loudness: the rebuilt voice gets the input frame's energy ----
        // (Moving harmonics under a fixed envelope changes their level; a voice shouldn't get
        // louder or quieter because its pitch changed.) Limited to +-12 dB.
        let (mut e_in, mut e_out) = (0.0f32, 0.0f32);
        for j in 0..=half {
            e_in += self.mag[j] * self.mag[j];
            let e = env_at(self, j);
            self.acc_re[j] *= e;
            self.acc_im[j] *= e;
            e_out += self.acc_re[j] * self.acc_re[j] + self.acc_im[j] * self.acc_im[j];
        }
        let level = if e_out > 1e-12 { (e_in / e_out).sqrt().clamp(0.25, 4.0) } else { 1.0 };
        for j in 0..=half { self.acc_re[j] *= level; self.acc_im[j] *= level; self.car_mag[j] *= level; }

        // ---- Synthesis ----
        let breath = s.breath.clamp(0.0, 1.0);
        for j in 0..=half {
            let e = env_at(self, j);
            let mut r = self.acc_re[j] * (1.0 - voc);
            let mut i = self.acc_im[j] * (1.0 - voc);
            if voc > 0.005 {
                self.car_phase[j] += TWO_PI * self.car_freq[j] / osamp;
                self.car_phase[j] -= TWO_PI * (self.car_phase[j] / TWO_PI).floor();
                let cm = self.car_mag[j] * e * voc;
                r += cm * self.car_phase[j].cos();
                i += cm * self.car_phase[j].sin();
            }
            if breath > 0.005 {
                // Noise with the voice's shape, a little stronger up high (where breath lives).
                let w = 0.35 + (j as f32 / half as f32).sqrt();
                let nm = breath * e * w * 0.9;
                let ph = TWO_PI * self.rand();
                r += nm * ph.cos();
                i += nm * ph.sin();
            }
            self.re[j] = r;
            self.im[j] = i;
        }
        self.re[0] = self.re[0].abs(); self.im[0] = 0.0;
        self.im[half] = 0.0;
        for k in 1..half { self.re[n - k] = self.re[k]; self.im[n - k] = -self.im[k]; }
        self.fft.run(&mut self.re, &mut self.im, true);

        // Overlap-add: Hann analysis and synthesis at 4x overlap sum to 1.5.
        let scale = 1.0 / (n as f32 * 1.5);
        for k in 0..n { self.accum[k] += self.window[k] * self.re[k] * scale; }
        self.out_fifo[..hop].copy_from_slice(&self.accum[..hop]);
        self.accum.copy_within(hop..hop + n, 0);
        for v in &mut self.accum[n..n + hop] { *v = 0.0; }
        for v in &mut self.out_fifo[..hop] { if !v.is_finite() { *v = 0.0; } }
    }
}

impl Default for SpectralVoice { fn default() -> Self { Self::new() } }
