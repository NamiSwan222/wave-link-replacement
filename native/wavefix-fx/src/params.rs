//! The plugin's parameters. Equalizer APO stores them by name as 0..1 values, and WaveFix
//! (src/WaveFix/Audio/FxParams.cs) maps real values to 0..1 with the same table: keep both in sync.
//! Every default is "off", so a parameter missing from the config never changes the sound.

#[derive(Clone, Copy, PartialEq)]
pub enum Kind {
    /// On when the 0..1 value is 0.5 or more.
    Toggle,
    /// Linear from `min` to `max`.
    Linear,
}

pub struct Param {
    pub name: &'static str,
    pub min: f32,
    pub max: f32,
    pub default: f32,
    pub unit: &'static str,
    pub kind: Kind,
}

const fn toggle(name: &'static str) -> Param {
    Param { name, min: 0.0, max: 1.0, default: 0.0, unit: "", kind: Kind::Toggle }
}
const fn lin(name: &'static str, min: f32, max: f32, default: f32, unit: &'static str) -> Param {
    Param { name, min, max, default, unit, kind: Kind::Linear }
}

pub const EXP_ON: usize = 0;
pub const EXP_THRESH: usize = 1;
pub const EXP_RATIO: usize = 2;
pub const DEESS_ON: usize = 3;
pub const DEESS_FREQ: usize = 4;
pub const DEESS_THRESH: usize = 5;
pub const DEESS_RANGE: usize = 6;
pub const COMP_ON: usize = 7;
pub const COMP_THRESH: usize = 8;
pub const COMP_RATIO: usize = 9;
pub const COMP_ATTACK: usize = 10;
pub const COMP_RELEASE: usize = 11;
pub const COMP_MAKEUP: usize = 12;
pub const VOICE_ON: usize = 13;
pub const PITCH: usize = 14;
pub const ROBOT_MIX: usize = 15;
pub const ROBOT_FREQ: usize = 16;
pub const WOBBLE: usize = 17;
pub const WOBBLE_RATE: usize = 18;
pub const DRIVE: usize = 19;
pub const LOW_CUT: usize = 20;
pub const HIGH_CUT: usize = 21;
pub const ECHO_MIX: usize = 22;
pub const ECHO_TIME: usize = 23;
pub const ECHO_FEEDBACK: usize = 24;
pub const VERB_MIX: usize = 25;
pub const VERB_SIZE: usize = 26;
pub const LIM_ON: usize = 27;
pub const LIM_CEILING: usize = 28;
pub const SB_ON: usize = 29;
pub const COUNT: usize = 30;

/// Names stay at 15 characters or fewer and without spaces (hosts give small name buffers).
pub static PARAMS: [Param; COUNT] = [
    toggle("ExpOn"),
    lin("ExpThresh", -80.0, -20.0, -50.0, "dB"),
    lin("ExpRatio", 1.0, 8.0, 2.0, ":1"),
    toggle("DeEssOn"),
    lin("DeEssFreq", 3000.0, 10000.0, 6000.0, "Hz"),
    lin("DeEssThresh", -50.0, 0.0, -30.0, "dB"),
    lin("DeEssRange", 0.0, 18.0, 8.0, "dB"),
    toggle("CompOn"),
    lin("CompThresh", -50.0, 0.0, -20.0, "dB"),
    lin("CompRatio", 1.0, 20.0, 3.0, ":1"),
    lin("CompAttack", 0.5, 100.0, 5.0, "ms"),
    lin("CompRelease", 10.0, 1000.0, 150.0, "ms"),
    lin("CompMakeup", 0.0, 24.0, 0.0, "dB"),
    toggle("VoiceOn"),
    lin("Pitch", -12.0, 12.0, 0.0, "st"),
    lin("RobotMix", 0.0, 1.0, 0.0, "%"),
    lin("RobotFreq", 20.0, 400.0, 80.0, "Hz"),
    lin("Wobble", 0.0, 1.0, 0.0, "%"),
    lin("WobbleRate", 0.5, 12.0, 5.0, "Hz"),
    lin("Drive", 0.0, 1.0, 0.0, "%"),
    lin("LowCut", 20.0, 2000.0, 20.0, "Hz"),
    lin("HighCut", 500.0, 20000.0, 20000.0, "Hz"),
    lin("EchoMix", 0.0, 1.0, 0.0, "%"),
    lin("EchoTime", 20.0, 1000.0, 250.0, "ms"),
    lin("EchoFeedback", 0.0, 0.9, 0.35, "%"),
    lin("VerbMix", 0.0, 1.0, 0.0, "%"),
    lin("VerbSize", 0.0, 1.0, 0.5, "%"),
    toggle("LimOn"),
    lin("LimCeiling", -24.0, 0.0, -1.0, "dB"),
    // Mixes in audio the WaveFix app sends (soundboard), just before the limiter.
    toggle("SbOn"),
];

impl Param {
    pub fn to_value(&self, norm: f32) -> f32 {
        let n = if norm.is_finite() { norm.clamp(0.0, 1.0) } else { 0.0 };
        match self.kind {
            Kind::Toggle => if n >= 0.5 { 1.0 } else { 0.0 },
            Kind::Linear => self.min + n * (self.max - self.min),
        }
    }

    pub fn to_norm(&self, value: f32) -> f32 {
        if self.max <= self.min { return 0.0; }
        ((value - self.min) / (self.max - self.min)).clamp(0.0, 1.0)
    }

    pub fn default_norm(&self) -> f32 { self.to_norm(self.default) }

    /// Text shown next to the parameter (without the unit).
    pub fn display(&self, norm: f32) -> String {
        let v = self.to_value(norm);
        match (self.kind, self.unit) {
            (Kind::Toggle, _) => if v >= 0.5 { "On".into() } else { "Off".into() },
            (_, "%") => format!("{:.0}", 100.0 * norm.clamp(0.0, 1.0)),
            (_, "st") => format!("{:+.1}", v),
            (_, "Hz") if v >= 1000.0 => format!("{:.2}k", v / 1000.0),
            (_, "Hz") | (_, "ms") => format!("{:.0}", v),
            _ => format!("{:.1}", v),
        }
    }
}
