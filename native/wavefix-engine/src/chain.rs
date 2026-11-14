//! The effect chain: what the app asks for (a small JSON file per mic), built into processors.
//!
//! Building happens off the audio thread (it can load plugin DLLs and allocate); processing
//! never allocates, locks or panics.

use serde::Deserialize;
use std::collections::HashMap;
use std::f32::consts::PI;
use std::path::Path;

use crate::vst::{PluginState, VstInstance};
use wavefix_fx::dsp::Dsp;
use wavefix_fx::params::{COUNT, PARAMS};

/// The config file the app writes (one per mic).
#[derive(Deserialize, Default, Debug, Clone, PartialEq)]
pub struct Config {
    #[serde(default)]
    pub stages: Vec<Stage>,
}

#[derive(Deserialize, Debug, Clone, PartialEq)]
#[serde(tag = "type", rename_all = "lowercase")]
pub enum Stage {
    /// Biquad filter. kind: hp, lp, pk, ls, hs.
    Filter { kind: String, freq: f32, #[serde(default)] gain: f32, #[serde(default)] q: Option<f32> },
    /// Volume change in dB.
    Gain { db: f32 },
    /// WaveFix's own effects (voice polish, voice changer, soundboard, limiter), by parameter name.
    Fx { #[serde(default)] params: HashMap<String, f32> },
    /// Diagnostics: adds a sine tone (so a test can hear that the engine processes the mic, even
    /// a silent one). Not used by the app's normal settings.
    Tone { freq: f32, #[serde(default = "default_tone_db")] db: f32 },
    /// A VST2 plugin from WaveFix's protected plugin folder.
    Vst {
        path: String,
        #[serde(default)] chunk: Option<String>,
        #[serde(default)] params: Option<HashMap<String, f32>>,
    },
}

fn default_tone_db() -> f32 { -20.0 }

impl Config {
    pub fn parse(text: &str) -> Result<Config, String> {
        serde_json::from_str(text).map_err(|e| e.to_string())
    }
}

#[derive(Clone, Copy, Default)]
struct Biquad { b0: f32, b1: f32, b2: f32, a1: f32, a2: f32, z1: f32, z2: f32 }

impl Biquad {
    /// RBJ cookbook filters (the same shapes Equalizer APO's Filter lines describe).
    fn new(kind: &str, sr: f32, freq: f32, gain_db: f32, q: Option<f32>) -> Option<Biquad> {
        let w = 2.0 * PI * (freq / sr).clamp(1e-5, 0.49);
        let (s, c) = w.sin_cos();
        let a = 10f32.powf(gain_db / 40.0);
        let q = q.unwrap_or(0.707).max(0.05);
        let alpha = s / (2.0 * q);
        let (b0, b1, b2, a0, a1, a2) = match kind {
            "hp" => ((1.0 + c) / 2.0, -(1.0 + c), (1.0 + c) / 2.0, 1.0 + alpha, -2.0 * c, 1.0 - alpha),
            "lp" => ((1.0 - c) / 2.0, 1.0 - c, (1.0 - c) / 2.0, 1.0 + alpha, -2.0 * c, 1.0 - alpha),
            "pk" => (1.0 + alpha * a, -2.0 * c, 1.0 - alpha * a, 1.0 + alpha / a, -2.0 * c, 1.0 - alpha / a),
            "ls" | "hs" => {
                // Shelf slope S = 1 (the steepest without overshoot): alpha = sin(w)/2 * sqrt(2).
                let alpha = s / 2.0 * std::f32::consts::SQRT_2;
                let sq = 2.0 * a.sqrt() * alpha;
                if kind == "ls" {
                    (a * ((a + 1.0) - (a - 1.0) * c + sq), 2.0 * a * ((a - 1.0) - (a + 1.0) * c), a * ((a + 1.0) - (a - 1.0) * c - sq),
                     (a + 1.0) + (a - 1.0) * c + sq, -2.0 * ((a - 1.0) + (a + 1.0) * c), (a + 1.0) + (a - 1.0) * c - sq)
                } else {
                    (a * ((a + 1.0) + (a - 1.0) * c + sq), -2.0 * a * ((a - 1.0) + (a + 1.0) * c), a * ((a + 1.0) + (a - 1.0) * c - sq),
                     (a + 1.0) - (a - 1.0) * c + sq, 2.0 * ((a - 1.0) - (a + 1.0) * c), (a + 1.0) - (a - 1.0) * c - sq)
                }
            }
            _ => return None,
        };
        Some(Biquad { b0: b0 / a0, b1: b1 / a0, b2: b2 / a0, a1: a1 / a0, a2: a2 / a0, z1: 0.0, z2: 0.0 })
    }

    #[inline]
    fn run(&mut self, x: f32) -> f32 {
        let y = self.b0 * x + self.z1;
        self.z1 = self.b1 * x - self.a1 * y + self.z2;
        self.z2 = self.b2 * x - self.a2 * y;
        y
    }
}

enum Processor {
    /// One filter per channel.
    Filter(Vec<Biquad>),
    Gain(f32),
    /// One built-in effects instance per channel (it's mono), with its parameter values.
    Fx(Vec<Dsp>, Box<[f32; COUNT]>),
    /// Plugin instances covering all channels (a mono plugin gets one instance per channel).
    Vst(Vec<VstInstance>),
    /// Test tone: phase, phase step per sample, amplitude.
    Tone(f32, f32, f32),
}

/// A built chain for one channel count and sample rate.
pub struct Chain {
    channels: usize,
    procs: Vec<Processor>,
    /// Per-channel scratch, sized for `max_frames`.
    planes: Vec<Vec<f32>>,
    max_frames: usize,
    /// Problems found while building (shown in the app through the status file).
    pub warnings: Vec<String>,
}

impl Chain {
    /// Builds the chain. `plugin_root`: only plugins inside this folder are loaded.
    pub fn build(cfg: &Config, channels: usize, sr: f32, max_frames: usize, plugin_root: Option<&Path>) -> Chain {
        let channels = channels.max(1);
        let mut warnings = Vec::new();
        let mut procs = Vec::new();
        for stage in &cfg.stages {
            match stage {
                Stage::Filter { kind, freq, gain, q } => match Biquad::new(kind, sr, *freq, *gain, *q) {
                    Some(b) => procs.push(Processor::Filter(vec![b; channels])),
                    None => warnings.push(format!("unknown filter type {kind}")),
                },
                Stage::Gain { db } => procs.push(Processor::Gain(10f32.powf(db.clamp(-60.0, 40.0) / 20.0))),
                Stage::Tone { freq, db } => procs.push(Processor::Tone(0.0, 2.0 * PI * freq.clamp(20.0, 20_000.0) / sr, 10f32.powf(db.clamp(-80.0, -6.0) / 20.0))),
                Stage::Fx { params } => {
                    let mut norm: [f32; COUNT] = std::array::from_fn(|i| PARAMS[i].default_norm());
                    for (i, prm) in PARAMS.iter().enumerate() {
                        if let Some(v) = params.get(prm.name) { norm[i] = v.clamp(0.0, 1.0); }
                    }
                    for name in params.keys() {
                        if !PARAMS.iter().any(|p| p.name == name) { warnings.push(format!("unknown effect setting {name}")); }
                    }
                    let dsps = (0..channels).map(|_| { let mut d = Dsp::new(); d.set_sample_rate(sr); d.reset(); d }).collect();
                    procs.push(Processor::Fx(dsps, Box::new(norm)));
                }
                Stage::Vst { path, chunk, params } => {
                    let state = PluginState { chunk: chunk.clone(), params: params.clone() };
                    match crate::vst::load_for_channels(Path::new(path), plugin_root, channels, sr, max_frames, &state) {
                        Ok(v) => procs.push(Processor::Vst(v)),
                        Err(e) => warnings.push(format!("plugin {path} skipped: {e}")),
                    }
                }
            }
        }
        Chain { channels, procs, planes: vec![vec![0.0; max_frames]; channels], max_frames, warnings }
    }

    pub fn channels(&self) -> usize { self.channels }

    /// Processes interleaved audio in place.
    pub fn process(&mut self, interleaved: &mut [f32]) {
        let ch = self.channels;
        let total = interleaved.len() / ch;
        let mut done = 0;
        while done < total {
            let n = (total - done).min(self.max_frames);
            let block = &mut interleaved[done * ch..(done + n) * ch];
            for c in 0..ch {
                let plane = &mut self.planes[c][..n];
                for (f, s) in plane.iter_mut().enumerate() { *s = block[f * ch + c]; }
            }
            self.process_planes(n);
            for c in 0..ch {
                let plane = &self.planes[c][..n];
                for (f, s) in plane.iter().enumerate() {
                    block[f * ch + c] = if s.is_finite() { s.clamp(-4.0, 4.0) } else { 0.0 };
                }
            }
            done += n;
        }
    }

    fn process_planes(&mut self, n: usize) {
        for proc in &mut self.procs {
            match proc {
                Processor::Filter(fs) => {
                    for (f, plane) in fs.iter_mut().zip(self.planes.iter_mut()) {
                        for s in &mut plane[..n] { *s = f.run(*s); }
                    }
                }
                Processor::Gain(g) => {
                    for plane in self.planes.iter_mut() { for s in &mut plane[..n] { *s *= *g; } }
                }
                Processor::Fx(dsps, norm) => {
                    for (d, plane) in dsps.iter_mut().zip(self.planes.iter_mut()) { d.process(norm, &mut plane[..n]); }
                }
                Processor::Tone(phase, step, amp) => {
                    for f in 0..n {
                        let v = phase.sin() * *amp;
                        *phase += *step;
                        if *phase > 2.0 * PI { *phase -= 2.0 * PI; }
                        for plane in self.planes.iter_mut() { plane[f] += v; }
                    }
                }
                Processor::Vst(instances) => {
                    let mut first = 0;
                    for inst in instances.iter_mut() {
                        let take = inst.channels().min(self.planes.len() - first);
                        inst.process(&mut self.planes[first..first + take], n);
                        first += take;
                    }
                }
            }
        }
    }
}
