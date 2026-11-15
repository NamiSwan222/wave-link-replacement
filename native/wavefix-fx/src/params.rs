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
pub const RVC_ON: usize = 30;
pub const RVC_MUTE: usize = 31;
// Voice engine (spectral): see voice.rs.
pub const FORMANT: usize = 32;
pub const HARMONICS: usize = 33;
pub const OCTAVE: usize = 34;
pub const DETUNE: usize = 35;
pub const BREATH: usize = 36;
pub const TUNE: usize = 37;
pub const VOCODER: usize = 38;
// Sample-by-sample effects.
pub const CRUSH: usize = 39;
pub const MOD_TYPE: usize = 40;
pub const MOD_MIX: usize = 41;
pub const MOD_RATE: usize = 42;
pub const DRIVE_TYPE: usize = 43;
pub const ECHO_STYLE: usize = 44;
pub const RADIO: usize = 45;
// Voice changer (classic.rs). When VmOn is set it replaces the effects above in the voice step.
pub const VM_ON: usize = 46;
pub const VM_PITCH: usize = 47;
pub const VM_ROBOT: usize = 48;
pub const VM_ROBOT_HZ: usize = 49;
pub const VM_CHORUS: usize = 50;
pub const VM_DRIVE: usize = 51;
pub const VM_RADIO: usize = 52;
pub const VM_ECHO: usize = 53;
pub const VM_ECHO_TIME: usize = 54;
pub const VM_ECHO_FB: usize = 55;
pub const VM_VERB: usize = 56;
pub const VM_ROOM: usize = 57;
pub const VM_DAMP: usize = 58;
pub const VM_GAIN: usize = 59;
pub const COUNT: usize = 60;

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
    // AI voice: send the voice to the app's RVC engine and play back the converted voice.
    toggle("RvcOn"),
    // When the AI voice isn't available: silence (on) or the real voice (off).
    toggle("RvcMute"),
    // Voice size, independent of pitch: below 0 bigger/deeper, above 0 smaller.
    lin("Formant", -12.0, 12.0, 0.0, "st"),
    // Darker (-1) .. brighter (+1) harmonics.
    lin("Harmonics", -1.0, 1.0, 0.0, ""),
    // An extra voice an octave below (<0) or above (>0), this loud.
    lin("Octave", -1.0, 1.0, 0.0, ""),
    lin("Detune", 0.0, 1.0, 0.0, "%"),
    lin("Breath", 0.0, 1.0, 0.0, "%"),
    // Auto-tune strength (snap to the nearest note).
    lin("Tune", 0.0, 1.0, 0.0, "%"),
    lin("Vocoder", 0.0, 1.0, 0.0, "%"),
    // Bitcrusher: fewer bits and a lower sample rate.
    lin("Crush", 0.0, 1.0, 0.0, "%"),
    // 0 off, 1 chorus, 2 flanger, 3 phaser.
    lin("ModType", 0.0, 3.0, 0.0, ""),
    lin("ModMix", 0.0, 1.0, 0.0, "%"),
    lin("ModRate", 0.05, 8.0, 0.5, "Hz"),
    // 0 overdrive, 1 fuzz, 2 megaphone.
    lin("DriveType", 0.0, 2.0, 0.0, ""),
    // 0 echo, 1 slapback, 2 canyon, 3 rhythm (three taps).
    lin("EchoStyle", 0.0, 3.0, 0.0, ""),
    // 0 off, 1 old phone, 2 walkie-talkie, 3 police radio, 4 AM radio.
    lin("Radio", 0.0, 4.0, 0.0, ""),
    // Voice changer (classic.rs).
    toggle("VmOn"),
    lin("VmPitch", -12.0, 12.0, 0.0, "st"),
    lin("VmRobot", 0.0, 1.0, 0.0, "%"),
    lin("VmRobotHz", 20.0, 400.0, 90.0, "Hz"),
    lin("VmChorus", 0.0, 1.0, 0.0, "%"),
    lin("VmDrive", 0.0, 1.0, 0.0, "%"),
    lin("VmRadio", 0.0, 1.0, 0.0, "%"),
    lin("VmEcho", 0.0, 1.0, 0.0, "%"),
    lin("VmEchoTime", 50.0, 1000.0, 300.0, "ms"),
    lin("VmEchoFb", 0.0, 0.95, 0.5, "%"),
    lin("VmVerb", 0.0, 1.0, 0.0, "%"),
    lin("VmRoom", 0.0, 1.0, 0.5, "%"),
    lin("VmDamp", 0.0, 1.0, 0.5, "%"),
    lin("VmGain", -12.0, 12.0, 0.0, "dB"),
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
