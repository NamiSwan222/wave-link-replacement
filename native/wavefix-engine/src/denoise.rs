//! "Strong" noise removal: DeepFilterNet 3 (https://github.com/Rikorose/DeepFilterNet, MIT or
//! Apache-2.0), with its model built into the engine.
//!
//! The network works on 10 ms frames (480 samples) of 48 kHz mono audio. The mic's blocks are
//! any size, so samples are queued: each full frame is cleaned, and the output is played one frame
//! later (10 ms, plus the network's own look-ahead). All buffers are made when the chain is built.

use df::tract::{DfParams, DfTract, RuntimeParams};
use ndarray::{ArrayView2, ArrayViewMut2};

pub struct Denoiser {
    df: DfTract,
    hop: usize,
    /// Mono input being collected for the next frame.
    inbuf: Vec<f32>,
    in_len: usize,
    /// Cleaned output waiting to be played (a ring of 2 frames).
    out: Vec<f32>,
    out_read: usize,
    out_len: usize,
    frame_out: Vec<f32>,
    /// Stops using the network after an error (the mic then passes through unchanged).
    failed: bool,
}

/// The rate DeepFilterNet is trained for.
pub const RATE: f32 = 48_000.0;

impl Denoiser {
    /// `limit_db`: how much noise may be removed at most (100 = all of it; 12-20 keeps some
    /// room sound, which can sound more natural).
    pub fn new(limit_db: f32) -> Result<Denoiser, String> {
        let rp = RuntimeParams::default_with_ch(1).with_atten_lim(limit_db.clamp(6.0, 100.0));
        let df = DfTract::new(DfParams::default(), &rp).map_err(|e| e.to_string())?;
        let hop = df.hop_size;
        let mut d = Denoiser {
            df, hop,
            inbuf: vec![0.0; hop], in_len: 0,
            out: vec![0.0; hop * 2], out_read: 0, out_len: hop, // one frame of silence = fixed latency
            frame_out: vec![0.0; hop],
            failed: false,
        };
        // Run once now, so the first real frame on the audio thread isn't the slow one.
        let zeros = vec![0.0; hop];
        let _ = d.run_frame_from(&zeros);
        Ok(d)
    }

    fn run_frame_from(&mut self, input: &[f32]) -> bool {
        let noisy = ArrayView2::from_shape((1, self.hop), input).unwrap();
        let enh = ArrayViewMut2::from_shape((1, self.hop), &mut self.frame_out[..]).unwrap();
        let df = &mut self.df;
        let r = std::panic::catch_unwind(std::panic::AssertUnwindSafe(|| df.process(noisy, enh)));
        matches!(r, Ok(Ok(_)))
    }

    /// Cleans the mic in place. `planes`: one buffer per channel; channel 0 is cleaned and the
    /// result goes to every channel (a mic is one voice).
    pub fn process(&mut self, planes: &mut [Vec<f32>], n: usize) {
        if self.failed || planes.is_empty() { return; }
        let cap = self.out.len();
        for f in 0..n {
            self.inbuf[self.in_len] = planes[0][f];
            self.in_len += 1;
            if self.in_len == self.hop {
                self.in_len = 0;
                let input = std::mem::take(&mut self.inbuf);
                let ok = self.run_frame_from(&input);
                self.inbuf = input;
                if !ok { self.failed = true; return; }
                for i in 0..self.hop {
                    let v = self.frame_out[i];
                    let w = (self.out_read + self.out_len) % cap;
                    self.out[w] = if v.is_finite() { v } else { 0.0 };
                    self.out_len += 1;
                }
            }
            // One sample out (there's always at least one queued: the queue starts a frame ahead).
            let v = if self.out_len > 0 {
                let v = self.out[self.out_read];
                self.out_read = (self.out_read + 1) % cap;
                self.out_len -= 1;
                v
            } else { 0.0 };
            for plane in planes.iter_mut() { plane[f] = v; }
        }
    }
}

#[cfg(test)]
mod tests {
    use super::*;

    /// Steady noise with a voice-like tone burst in the middle: the noise must drop a lot, the tone
    /// must survive, and odd block sizes must give a steady output (no gaps or clicks from queueing).
    #[test]
    fn removes_noise_and_keeps_the_voice() {
        let mut d = Denoiser::new(100.0).expect("model loads");
        let sr = 48_000usize;
        let mut seed = 1u32;
        let mut noise = || { seed = seed.wrapping_mul(1_664_525).wrapping_add(1_013_904_223); (seed >> 8) as f32 / 16_777_216.0 - 0.5 };
        let total = sr * 3;
        // Speech-like "voice" from 1 s to 2 s: a moving pitch (120-170 Hz) with 40 harmonics shaped by
        // vowel formants (about 600, 1200 and 2500 Hz), in syllables. A steady pure tone would count as
        // hum, which the network is right to remove.
        let mut phase = 0.0f32;
        let input: Vec<f32> = (0..total).map(|i| {
            let t = i as f32 / sr as f32;
            let f0 = 145.0 + 25.0 * (2.0 * std::f32::consts::PI * 2.5 * t).sin();
            phase += 2.0 * std::f32::consts::PI * f0 / sr as f32;
            let voice = if (1.0..2.0).contains(&t) {
                let env = (std::f32::consts::PI * (t - 1.0) * 4.0).sin().abs().powf(0.5);
                let mut v = 0.0;
                for h in 1..=40 {
                    let f = f0 * h as f32;
                    let formant = |c: f32, w: f32| (-((f - c) / w).powi(2)).exp();
                    let gain = (formant(600.0, 150.0) + 0.6 * formant(1200.0, 200.0) + 0.3 * formant(2500.0, 300.0) + 0.05) / h as f32;
                    v += gain * (phase * h as f32).sin();
                }
                0.25 * env * v
            } else { 0.0 };
            voice + 0.02 * noise()
        }).collect();
        let mut out = Vec::with_capacity(total);
        let (mut pos, mut k) = (0, 0);
        while pos < total {
            let n = [480usize, 441, 512, 1000][k % 4].min(total - pos);
            let mut planes = vec![input[pos..pos + n].to_vec(), input[pos..pos + n].to_vec()];
            d.process(&mut planes, n);
            assert_eq!(planes[0], planes[1]);
            out.extend_from_slice(&planes[0]);
            pos += n; k += 1;
        }
        assert!(out.iter().all(|v| v.is_finite()));
        let rms = |s: &[f32]| (s.iter().map(|v| v * v).sum::<f32>() / s.len() as f32).sqrt();
        let noise_in = rms(&input[sr / 4..sr * 3 / 4]);
        let noise_out = rms(&out[sr / 4..sr * 3 / 4]);
        let voice_in = rms(&input[sr + sr / 4..sr + sr * 3 / 4]);
        let voice_out = rms(&out[sr + sr / 4 + 2000..sr + sr * 3 / 4 + 2000]);
        let removed = 20.0 * (noise_in / noise_out.max(1e-9)).log10();
        let kept = 20.0 * (voice_out / voice_in).log10();
        assert!(removed > 20.0, "noise only down {removed:.1} dB");
        assert!(kept > -6.0, "voice down {kept:.1} dB");
    }
}
