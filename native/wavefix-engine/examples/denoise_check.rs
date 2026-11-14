//! Runs strong noise removal (DeepFilterNet) through the engine's chain on a raw f32 file, the way
//! Windows feeds the mic (uneven block sizes), and reports speed.
//! Usage: cargo run --release --example denoise_check -- <in.f32> <out.f32> [limit_db]
use std::time::Instant;
use wavefix_engine::chain::{Chain, Config};

fn main() {
    let args: Vec<String> = std::env::args().collect();
    let bytes = std::fs::read(&args[1]).unwrap();
    let input: Vec<f32> = bytes.chunks_exact(4).map(|b| f32::from_le_bytes([b[0], b[1], b[2], b[3]])).collect();
    let limit = args.get(3).map(|s| s.as_str()).unwrap_or("100");
    let cfg = Config::parse(&format!(r#"{{"stages":[{{"type":"denoise","limit":{limit}}}]}}"#)).unwrap();
    let t = Instant::now();
    let mut chain = Chain::build(&cfg, 2, 48_000.0, 1024, None);
    println!("built in {} ms, warnings {:?}", t.elapsed().as_millis(), chain.warnings);
    // Stereo interleaved, blocks of 480, 441, 512 and 1000 frames in turn.
    let sizes = [480usize, 441, 512, 1000];
    let mut out = Vec::with_capacity(input.len());
    let (mut pos, mut k, mut worst) = (0usize, 0usize, 0f64);
    let t = Instant::now();
    while pos < input.len() {
        let n = sizes[k % 4].min(input.len() - pos);
        let mut buf: Vec<f32> = input[pos..pos + n].iter().flat_map(|&s| [s, s]).collect();
        let b = Instant::now();
        chain.process(&mut buf);
        let ms = b.elapsed().as_secs_f64() * 1000.0;
        if k > 4 { worst = worst.max(ms / (n as f64 / 48.0)); }
        out.extend(buf.chunks_exact(2).map(|c| { assert_eq!(c[0], c[1]); c[0] }));
        pos += n; k += 1;
    }
    let secs = input.len() as f64 / 48_000.0;
    println!("{:.2} s of audio in {} ms: {:.1}% of one core on average, worst block {:.0}% of its duration",
        secs, t.elapsed().as_millis(), t.elapsed().as_secs_f64() / secs * 100.0, worst * 100.0);
    let mut o = Vec::with_capacity(out.len() * 4);
    for s in out { o.extend_from_slice(&s.to_le_bytes()); }
    std::fs::write(&args[2], o).unwrap();
}
