//! Renders a recording through every voice changer voice, using the same Dsp the audio engine
//! runs, so the voices can be heard without installing anything.
//!
//! cargo run --release --example render_voices -- <input.wav> <output folder>
//! Input: 16-bit PCM WAV, mono or stereo (stereo is mixed to mono). Output: one WAV per voice.
//! The voice values must match VoicePreset.All in src/WaveFix/Audio/VoiceFx.cs.

use std::fs;
use std::path::Path;
use wavefix_fx::dsp::Dsp;
use wavefix_fx::params::{self as p, COUNT, PARAMS};

struct V { id: &'static str, pitch: f32, robot: f32, robot_hz: f32, chorus: f32, drive: f32, radio: f32,
           echo: f32, echo_ms: f32, echo_fb: f32, room: f32, size: f32, damp: f32, gain: f32 }

const fn v(id: &'static str) -> V {
    V { id, pitch: 0.0, robot: 0.0, robot_hz: 90.0, chorus: 0.0, drive: 0.0, radio: 0.0,
        echo: 0.0, echo_ms: 300.0, echo_fb: 0.5, room: 0.0, size: 0.5, damp: 0.5, gain: 0.0 }
}

fn voices() -> Vec<V> {
    vec![
        V { pitch: -5.0, ..v("deep") },
        V { pitch: -9.0, room: 25.0, size: 0.7, ..v("giant") },
        V { pitch: -11.0, drive: 30.0, room: 15.0, gain: -2.0, ..v("beast") },
        V { pitch: 7.0, ..v("squeaky") },
        V { robot: 85.0, robot_hz: 90.0, ..v("bot3000") },
        V { robot: 60.0, robot_hz: 140.0, pitch: -2.0, chorus: 40.0, gain: 2.0, ..v("android") },
        V { pitch: 4.0, robot: 45.0, robot_hz: 30.0, chorus: 50.0, gain: 4.0, ..v("martian") },
        V { radio: 100.0, drive: 35.0, gain: 4.0, ..v("walkie") },
        V { radio: 70.0, drive: 60.0, gain: 4.0, echo: 20.0, echo_ms: 120.0, echo_fb: 0.2, ..v("megaphone") },
        V { echo: 80.0, echo_ms: 450.0, echo_fb: 0.5, ..v("canyon") },
        V { room: 55.0, size: 0.92, damp: 0.3, ..v("cathedral") },
        V { pitch: -3.0, chorus: 80.0, room: 45.0, size: 0.85, echo: 25.0, echo_ms: 350.0, echo_fb: 0.35, ..v("phantom") },
        V { chorus: 100.0, ..v("duo") },
    ]
}

fn read_wav(path: &Path) -> (u32, Vec<f32>) {
    let b = fs::read(path).expect("read input");
    assert!(&b[0..4] == b"RIFF" && &b[8..12] == b"WAVE", "not a WAV file");
    let (mut i, mut ch, mut rate, mut bits, mut data) = (12usize, 1u16, 48_000u32, 16u16, None);
    while i + 8 <= b.len() {
        let id = &b[i..i + 4];
        let len = u32::from_le_bytes(b[i + 4..i + 8].try_into().unwrap()) as usize;
        let body = &b[i + 8..(i + 8 + len).min(b.len())];
        if id == b"fmt " {
            ch = u16::from_le_bytes(body[2..4].try_into().unwrap());
            rate = u32::from_le_bytes(body[4..8].try_into().unwrap());
            bits = u16::from_le_bytes(body[14..16].try_into().unwrap());
        } else if id == b"data" { data = Some(body); }
        i += 8 + len + (len & 1);
    }
    assert_eq!(bits, 16, "16-bit PCM only");
    let d = data.expect("no data chunk");
    let frames = d.len() / (2 * ch as usize);
    let mono = (0..frames).map(|f| {
        let s: f32 = (0..ch as usize).map(|c| { let o = (f * ch as usize + c) * 2; i16::from_le_bytes([d[o], d[o + 1]]) as f32 / 32768.0 }).sum();
        s / ch as f32
    }).collect();
    (rate, mono)
}

fn write_wav(path: &Path, rate: u32, x: &[f32]) {
    let mut out = Vec::with_capacity(44 + x.len() * 2);
    let data_len = (x.len() * 2) as u32;
    out.extend_from_slice(b"RIFF"); out.extend_from_slice(&(36 + data_len).to_le_bytes()); out.extend_from_slice(b"WAVEfmt ");
    out.extend_from_slice(&16u32.to_le_bytes()); out.extend_from_slice(&1u16.to_le_bytes()); out.extend_from_slice(&1u16.to_le_bytes());
    out.extend_from_slice(&rate.to_le_bytes()); out.extend_from_slice(&(rate * 2).to_le_bytes()); out.extend_from_slice(&2u16.to_le_bytes());
    out.extend_from_slice(&16u16.to_le_bytes()); out.extend_from_slice(b"data"); out.extend_from_slice(&data_len.to_le_bytes());
    for s in x { out.extend_from_slice(&((s.clamp(-1.0, 1.0) * 32767.0) as i16).to_le_bytes()); }
    fs::write(path, out).expect("write output");
}

fn main() {
    let args: Vec<String> = std::env::args().collect();
    assert!(args.len() >= 3, "usage: render_voices <input.wav> <output folder>");
    let (rate, input) = read_wav(Path::new(&args[1]));
    let out_dir = Path::new(&args[2]);
    fs::create_dir_all(out_dir).unwrap();
    for voice in voices() {
        let set = [
            (p::VM_ON, 1.0), (p::VM_PITCH, voice.pitch), (p::VM_ROBOT, voice.robot / 100.0), (p::VM_ROBOT_HZ, voice.robot_hz),
            (p::VM_CHORUS, voice.chorus / 100.0), (p::VM_DRIVE, voice.drive / 100.0), (p::VM_RADIO, voice.radio / 100.0),
            (p::VM_ECHO, voice.echo / 100.0), (p::VM_ECHO_TIME, voice.echo_ms), (p::VM_ECHO_FB, voice.echo_fb),
            (p::VM_VERB, voice.room / 100.0), (p::VM_ROOM, voice.size), (p::VM_DAMP, voice.damp), (p::VM_GAIN, voice.gain),
        ];
        let mut norm: [f32; COUNT] = std::array::from_fn(|i| PARAMS[i].default_norm());
        for (i, val) in set { norm[i] = PARAMS[i].to_norm(val); }
        let mut d = Dsp::new();
        d.set_sample_rate(rate as f32);
        d.reset();
        let mut x = input.clone();
        for c in x.chunks_mut(480) { d.process(&norm, c); }
        let file = out_dir.join(format!("voice-{}.wav", voice.id));
        write_wav(&file, rate, &x);
        println!("{}", file.display());
    }
}
