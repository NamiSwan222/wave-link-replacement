//! Dev tool: builds a chain from a config file and runs noise through it.
//! cargo run --release --example check_config -- <config.json> [plugin root]
use std::path::Path;
fn main() {
    let args: Vec<String> = std::env::args().collect();
    let text = std::fs::read_to_string(&args[1]).expect("read config");
    let cfg = wavefix_engine::chain::Config::parse(&text).expect("parse");
    let root = args.get(2).map(Path::new);
    let mut chain = wavefix_engine::chain::Chain::build(&cfg, 2, 48_000.0, 480, root);
    println!("stages: {}", cfg.stages.len());
    for w in &chain.warnings { println!("warning: {w}"); }
    let mut seed = 1u32;
    let mut buf: Vec<f32> = (0..48_000 * 2).map(|_| { seed = seed.wrapping_mul(1664525).wrapping_add(1013904223); (seed as f32 / u32::MAX as f32 - 0.5) * 0.2 }).collect();
    chain.process(&mut buf);
    println!("finite: {}, peak: {:.3}", buf.iter().all(|v| v.is_finite()), buf.iter().fold(0f32, |m, v| m.max(v.abs())));
}
