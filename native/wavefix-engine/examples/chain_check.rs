//! Runs a raw f32 mono 48 kHz file through any engine chain config (JSON) and writes the output.
//! Usage: cargo run --release --example chain_check -- <config.json> <in.f32> <out.f32>
use wavefix_engine::chain::{Chain, Config};

fn main() {
    let args: Vec<String> = std::env::args().collect();
    let cfg = Config::parse(&std::fs::read_to_string(&args[1]).unwrap()).unwrap();
    let bytes = std::fs::read(&args[2]).unwrap();
    let input: Vec<f32> = bytes.chunks_exact(4).map(|b| f32::from_le_bytes([b[0], b[1], b[2], b[3]])).collect();
    let mut chain = Chain::build(&cfg, 1, 48_000.0, 480, None);
    if !chain.warnings.is_empty() { println!("warnings: {:?}", chain.warnings); }
    let mut out = Vec::with_capacity(input.len());
    for block in input.chunks(480) { let mut b = block.to_vec(); chain.process(&mut b); out.extend(b); }
    let mut o = Vec::with_capacity(out.len() * 4);
    for s in out { o.extend_from_slice(&s.to_le_bytes()); }
    std::fs::write(&args[3], o).unwrap();
}
