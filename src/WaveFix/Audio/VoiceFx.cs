using System.IO;

namespace WaveFix;

/// <summary>
/// WaveFix's own effects (voice polish, voice changer, soundboard, peak protection), from
/// native/wavefix-fx. They're built into WaveFix's audio engine; this class turns the app's
/// settings into their parameter values.
/// </summary>
public static class VoiceFx
{
    /// <summary>ProgramData\WaveFix\Engine: the engine's config and status, and the soundboard bridge.</summary>
    public static string Folder => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData), "WaveFix", "Engine");

    /// <summary>This build includes WaveFix's audio engine (and so its effects).</summary>
    public static bool Available => WaveFixEngine.Available;

    // ---- Parameters: same names, ranges and order as native/wavefix-fx/src/params.rs ----

    public sealed record Param(string Name, double Min, double Max);

    public static readonly Param ExpOn = new("ExpOn", 0, 1), ExpThresh = new("ExpThresh", -80, -20), ExpRatio = new("ExpRatio", 1, 8);
    public static readonly Param DeEssOn = new("DeEssOn", 0, 1), DeEssFreq = new("DeEssFreq", 3000, 10000),
        DeEssThresh = new("DeEssThresh", -50, 0), DeEssRange = new("DeEssRange", 0, 18);
    public static readonly Param CompOn = new("CompOn", 0, 1), CompThresh = new("CompThresh", -50, 0), CompRatio = new("CompRatio", 1, 20),
        CompAttack = new("CompAttack", 0.5, 100), CompRelease = new("CompRelease", 10, 1000), CompMakeup = new("CompMakeup", 0, 24);
    public static readonly Param VoiceOn = new("VoiceOn", 0, 1), Pitch = new("Pitch", -12, 12),
        RobotMix = new("RobotMix", 0, 1), RobotFreq = new("RobotFreq", 20, 400),
        Wobble = new("Wobble", 0, 1), WobbleRate = new("WobbleRate", 0.5, 12), Drive = new("Drive", 0, 1),
        LowCut = new("LowCut", 20, 2000), HighCut = new("HighCut", 500, 20000),
        EchoMix = new("EchoMix", 0, 1), EchoTime = new("EchoTime", 20, 1000), EchoFeedback = new("EchoFeedback", 0, 0.9),
        VerbMix = new("VerbMix", 0, 1), VerbSize = new("VerbSize", 0, 1);
    public static readonly Param LimOn = new("LimOn", 0, 1), LimCeiling = new("LimCeiling", -24, 0);
    public static readonly Param SbOn = new("SbOn", 0, 1), RvcOn = new("RvcOn", 0, 1), RvcMute = new("RvcMute", 0, 1);
    public static readonly Param Formant = new("Formant", -12, 12), Harmonics = new("Harmonics", -1, 1), Octave = new("Octave", -1, 1),
        Detune = new("Detune", 0, 1), Breath = new("Breath", 0, 1), Tune = new("Tune", 0, 1), Vocoder = new("Vocoder", 0, 1),
        Crush = new("Crush", 0, 1), ModType = new("ModType", 0, 3), ModMix = new("ModMix", 0, 1), ModRate = new("ModRate", 0.05, 8),
        DriveType = new("DriveType", 0, 2), EchoStyle = new("EchoStyle", 0, 3), Radio = new("Radio", 0, 4);
    // Voice changer (native/wavefix-fx/src/classic.rs).
    public static readonly Param VmOn = new("VmOn", 0, 1), VmPitch = new("VmPitch", -12, 12),
        VmRobot = new("VmRobot", 0, 1), VmRobotHz = new("VmRobotHz", 20, 400), VmChorus = new("VmChorus", 0, 1),
        VmDrive = new("VmDrive", 0, 1), VmRadio = new("VmRadio", 0, 1), VmEcho = new("VmEcho", 0, 1),
        VmEchoTime = new("VmEchoTime", 50, 1000), VmEchoFb = new("VmEchoFb", 0, 0.95), VmVerb = new("VmVerb", 0, 1),
        VmRoom = new("VmRoom", 0, 1), VmDamp = new("VmDamp", 0, 1), VmGain = new("VmGain", -12, 12);

    /// <summary>Every parameter in the plugin's order (used to check the plugin matches this table).</summary>
    public static readonly Param[] All =
    {
        ExpOn, ExpThresh, ExpRatio, DeEssOn, DeEssFreq, DeEssThresh, DeEssRange,
        CompOn, CompThresh, CompRatio, CompAttack, CompRelease, CompMakeup,
        VoiceOn, Pitch, RobotMix, RobotFreq, Wobble, WobbleRate, Drive, LowCut, HighCut,
        EchoMix, EchoTime, EchoFeedback, VerbMix, VerbSize, LimOn, LimCeiling, SbOn, RvcOn, RvcMute,
        Formant, Harmonics, Octave, Detune, Breath, Tune, Vocoder, Crush, ModType, ModMix, ModRate, DriveType, EchoStyle, Radio,
        VmOn, VmPitch, VmRobot, VmRobotHz, VmChorus, VmDrive, VmRadio, VmEcho, VmEchoTime, VmEchoFb, VmVerb, VmRoom, VmDamp, VmGain,
    };

    sealed class Builder
    {
        public readonly Dictionary<string, float> Map = new();
        public Builder Set(Param p, double value)
        {
            Map[p.Name] = (float)Math.Clamp((value - p.Min) / (p.Max - p.Min), 0, 1);
            return this;
        }
        public Builder On(Param p) => Set(p, 1);
    }

    /// <summary>
    /// Config line for the main instance (expander, de-esser, leveler, voice changer), or null
    /// when none of them is on. It runs after the tone and the user's plugins, before mic boost.
    /// </summary>
    /// <summary>
    /// Settings (name -> 0..1) for the main effects stage, or null when none of its effects is on.
    /// </summary>
    public static Dictionary<string, float>? MainParams(PolishSettings polish, VoiceSettings voice, bool aiVoice = false, bool aiSilent = false)
    {
        var b = new Builder();
        bool any = false;
        if (aiVoice)
        {
            // AI voice (RVC): the engine sends the voice to WaveFix and plays it back converted.
            any = true;
            b.On(RvcOn);
            if (aiSilent) b.On(RvcMute);
        }
        if (polish.Silencer)
        {
            any = true;
            // Sensitivity 0..100: higher turns down louder background sound.
            b.On(ExpOn).Set(ExpThresh, -66 + 0.3 * polish.SilencerSensitivity).Set(ExpRatio, 3);
        }
        if (polish.DeEsser)
        {
            any = true;
            var a = polish.DeEsserAmount / 100.0;
            b.On(DeEssOn).Set(DeEssFreq, 6000).Set(DeEssThresh, -18 - 22 * a).Set(DeEssRange, 4 + 10 * a);
        }
        if (polish.Leveler)
        {
            any = true;
            var a = polish.LevelerAmount / 100.0;
            double thr = -12 - 24 * a, ratio = 2 + 4 * a;
            // Win back roughly the level the leveler takes from normal speech.
            double makeup = 0.4 * -thr * (1 - 1 / ratio);
            b.On(CompOn).Set(CompThresh, thr).Set(CompRatio, ratio).Set(CompAttack, 5).Set(CompRelease, 160).Set(CompMakeup, makeup);
        }
        if (voice.Enabled && !voice.IsNeutral)
        {
            any = true;
            b.On(VmOn).Set(VmPitch, voice.Pitch).Set(VmRobot, voice.Robot / 100).Set(VmRobotHz, voice.RobotFreq)
             .Set(VmChorus, voice.Chorus / 100).Set(VmDrive, voice.Drive / 100).Set(VmRadio, voice.RadioMix / 100)
             .Set(VmEcho, voice.Echo / 100).Set(VmEchoTime, voice.EchoTime).Set(VmEchoFb, voice.EchoFeedback)
             .Set(VmVerb, voice.Room / 100).Set(VmRoom, voice.RoomSize).Set(VmDamp, voice.Damping).Set(VmGain, voice.Gain);
        }
        return any ? b.Map : null;
    }

    /// <summary>
    /// Config line for the last instance (after mic boost), or null: the soundboard is mixed in
    /// there (so the voice changer doesn't pitch it), then peak protection.
    /// </summary>
    /// <summary>Settings for the last instance (soundboard, peak protection), or null when both are off.</summary>
    public static Dictionary<string, float>? OutputParams(PolishSettings polish, SoundboardSettings soundboard)
    {
        if (!polish.Limiter && !soundboard.Enabled) return null;
        var b = new Builder();
        if (soundboard.Enabled) b.On(SbOn);
        if (polish.Limiter) b.On(LimOn).Set(LimCeiling, polish.LimiterCeilingDb);
        return b.Map;
    }
}

/// <summary>Voice polish settings (Microphone page). Everything starts off.</summary>
public sealed class PolishSettings
{
    /// <summary>Settings this version doesn't know (e.g. from a newer WaveFix): kept, so saving never drops them.</summary>
    [System.Text.Json.Serialization.JsonExtensionData] public Dictionary<string, System.Text.Json.JsonElement>? Extra { get; set; }

    public bool Leveler { get; set; }
    /// <summary>0 = gentle, 100 = strong.</summary>
    public double LevelerAmount { get; set; } = 50;
    public bool DeEsser { get; set; }
    public double DeEsserAmount { get; set; } = 50;
    public bool Silencer { get; set; }
    public double SilencerSensitivity { get; set; } = 50;
    public bool Limiter { get; set; }
    public double LimiterCeilingDb { get; set; } = -1;
}

/// <summary>Custom voice tone: five bands, in dB.</summary>
public sealed class CustomEq
{
    /// <summary>Settings this version doesn't know (e.g. from a newer WaveFix): kept, so saving never drops them.</summary>
    [System.Text.Json.Serialization.JsonExtensionData] public Dictionary<string, System.Text.Json.JsonElement>? Extra { get; set; }

    /// <summary>Filter type and frequency of each band (peaking bands use Q 1).</summary>
    public static readonly (string Label, string Hint, string Type, int Hz)[] Bands =
    {
        ("Bass", "100 Hz", "LS", 100),
        ("Body", "300 Hz", "PK", 300),
        ("Mids", "1 kHz", "PK", 1000),
        ("Presence", "3 kHz", "PK", 3000),
        ("Air", "10 kHz", "HS", 10000),
    };

    public double[] Gains { get; set; } = new double[Bands.Length];

    public IEnumerable<string> Lines()
    {
        for (int i = 0; i < Bands.Length && i < Gains.Length; i++)
        {
            if (Math.Abs(Gains[i]) < 0.05) continue;
            var (_, _, type, hz) = Bands[i];
            var q = type == "PK" ? " Q 1" : "";
            yield return FormattableString.Invariant($"Filter: ON {type} Fc {hz} Hz Gain {Gains[i]:0.0} dB{q}");
        }
    }
}

/// <summary>Voice changer settings. Sliders are shown in the app; the rest come from the preset.</summary>
public sealed class VoiceSettings
{
    /// <summary>Settings this version doesn't know (e.g. from a newer WaveFix): kept, so saving never drops them.</summary>
    [System.Text.Json.Serialization.JsonExtensionData] public Dictionary<string, System.Text.Json.JsonElement>? Extra { get; set; }

    public bool Enabled { get; set; }
    public string Preset { get; set; } = "deep";
    /// <summary>
    /// 2 = settings made for this voice changer. Missing (0) in settings from older versions, which
    /// had different effects; new settings start at 0 too and get the voice's values on first run.
    /// </summary>
    public int Version { get; set; }

    // Same meaning as before this voice changer, so older settings carry over.
    public double Pitch { get; set; } = -5;   // semitones
    public double Robot { get; set; }         // %
    public double RobotFreq { get; set; } = 90; // Hz
    public double Echo { get; set; }          // %
    public double EchoTime { get; set; } = 300; // ms
    public double EchoFeedback { get; set; } = 0.5; // 0..0.95
    public double Room { get; set; }          // reverb, %
    public double RoomSize { get; set; } = 0.5; // 0..1

    // New in this voice changer.
    public double Chorus { get; set; }        // %
    public double Drive { get; set; }         // %
    public double RadioMix { get; set; }      // %
    public double Damping { get; set; } = 0.5; // reverb damping 0..1
    public double Gain { get; set; }          // dB

    /// <summary>True when the settings wouldn't change the sound at all.</summary>
    [System.Text.Json.Serialization.JsonIgnore]
    public bool IsNeutral =>
        Math.Abs(Pitch) < 0.01 && Robot < 0.5 && Chorus < 0.5 && Drive < 0.5 && RadioMix < 0.5
        && Echo < 0.5 && Room < 0.5 && Math.Abs(Gain) < 0.05;

    public void CopyFrom(VoicePreset p)
    {
        Preset = p.Id;
        Pitch = p.Pitch; Robot = p.Robot; RobotFreq = p.RobotFreq; Chorus = p.Chorus; Drive = p.Drive; RadioMix = p.RadioMix;
        Echo = p.Echo; EchoTime = p.EchoTime; EchoFeedback = p.EchoFeedback;
        Room = p.Room; RoomSize = p.RoomSize; Damping = p.Damping; Gain = p.Gain;
    }
}

/// <summary>A voice: a mix of the voice changer's effects (native/wavefix-fx/src/classic.rs).</summary>
public sealed record VoicePreset(string Id, string Label, string Desc,
    double Pitch = 0, double Robot = 0, double RobotFreq = 90, double Chorus = 0, double Drive = 0, double RadioMix = 0,
    double Echo = 0, double EchoTime = 300, double EchoFeedback = 0.5,
    double Room = 0, double RoomSize = 0.5, double Damping = 0.5, double Gain = 0)
{
    public const string CustomId = "custom";

    public static readonly VoicePreset[] All =
    {
        new("deep", "Deep", "Lower voice", Pitch: -5),
        new("giant", "Giant", "Huge, in a big room", Pitch: -9, Room: 25, RoomSize: 0.7),
        new("beast", "Beast", "Low, gritty growl", Pitch: -11, Drive: 30, Room: 15, Gain: -2),
        new("squeaky", "Squeaky", "High and small", Pitch: 7),
        new("bot3000", "Bot 3000", "Classic robot", Robot: 85, RobotFreq: 90),
        new("android", "Android", "Robot with a lower voice", Robot: 60, RobotFreq: 140, Pitch: -2, Chorus: 40, Gain: 2),
        new("martian", "Martian", "High, buzzing alien", Pitch: 4, Robot: 45, RobotFreq: 30, Chorus: 50, Gain: 4),
        new("walkie", "Walkie", "Crunchy radio", RadioMix: 100, Drive: 35, Gain: 4),
        new("megaphone", "Megaphone", "Loud, with a slap of echo", RadioMix: 70, Drive: 60, Gain: 4, Echo: 20, EchoTime: 120, EchoFeedback: 0.2),
        new("canyon", "Canyon", "Long echoes", Echo: 80, EchoTime: 450, EchoFeedback: 0.5),
        new("cathedral", "Cathedral", "Huge reverb", Room: 55, RoomSize: 0.92, Damping: 0.3),
        new("phantom", "Phantom", "Ghostly and wide", Pitch: -3, Chorus: 80, Room: 45, RoomSize: 0.85, Echo: 25, EchoTime: 350, EchoFeedback: 0.35),
        new("duo", "Duo", "Two of you (chorus)", Chorus: 100),
    };

    public static VoicePreset? Find(string id) => All.FirstOrDefault(p => p.Id == id);
}
