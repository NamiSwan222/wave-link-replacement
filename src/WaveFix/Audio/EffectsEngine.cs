using System.IO;
using System.Text;
using Microsoft.Win32;

namespace WaveFix;

public static class EqPresets
{
    public const string Flat = "Flat";
    public const string Warm = "Warm";
    public const string Clear = "Clear";
    public const string Broadcast = "Broadcast";
    public const string Radio = "Radio";
    public const string Custom = "Custom";
    public static readonly string[] All = { Flat, Warm, Clear, Broadcast, Radio, Custom };

    /// <summary>One filter of a preset: kind is hp, lp, pk, ls or hs.</summary>
    public sealed record Filter(string Kind, double Hz, double Gain = 0, double? Q = null);

    /// <summary>The filters of each preset (for both Equalizer APO and the WaveFix engine).</summary>
    public static IReadOnlyList<Filter> Filters(string preset) => preset switch
    {
        Warm => new Filter[] { new("ls", 180, 3), new("pk", 3500, -1.5, 1) },
        Clear => new Filter[] { new("pk", 300, -3, 1), new("pk", 3000, 2, 1), new("hs", 6000, 3) },
        Broadcast => new Filter[] { new("ls", 120, 2.5), new("pk", 400, -2.5, 1.2), new("pk", 2500, 2.5, 1), new("hs", 8000, 2) },
        Radio => new Filter[] { new("hp", 300), new("lp", 3400), new("pk", 1500, 4, 1) },
        _ => Array.Empty<Filter>(),
    };

    /// <summary>Equalizer APO filter lines for each preset.</summary>
    public static IEnumerable<string> Lines(string preset) => Filters(preset).Select(f =>
        f.Kind is "hp" or "lp" ? FormattableString.Invariant($"Filter: ON {f.Kind.ToUpperInvariant()} Fc {f.Hz:0.###} Hz")
        : f.Q is { } q ? FormattableString.Invariant($"Filter: ON {f.Kind.ToUpperInvariant()} Fc {f.Hz:0.###} Hz Gain {f.Gain:0.###} dB Q {q:0.###}")
        : FormattableString.Invariant($"Filter: ON {f.Kind.ToUpperInvariant()} Fc {f.Hz:0.###} Hz Gain {f.Gain:0.###} dB"));
}

/// <summary>
/// Drives Equalizer APO, which runs our effects inside the Windows audio engine (audiodg.exe).
/// Nothing here touches audio samples: we only write config files that Equalizer APO reloads
/// on change. That is what keeps the mic out of Discord's stream capture.
/// </summary>
public sealed class EffectsEngine
{
    const string EapoEndpointEffect = "{EC1CC9CE-FAED-4822-828A-82A81A6F018F}";
    const string EapoStreamEffect = "{EACD2258-FCAC-4FF4-B36D-419E924A6D79}";
    const string MicFileName = "wavefix-mic.txt";

    public string InstallDir { get; }
    public string ConfigDir => Path.Combine(InstallDir, "config");
    public string NoisePluginPath => Path.Combine(InstallDir, @"VSTPlugins\rnnoise\rnnoise_mono.dll");

    /// <summary>Run the chain on WaveFix's own engine (new installs) instead of Equalizer APO.</summary>
    public bool UseWaveFixEngine { get; set; }

    public bool EapoInstalled => File.Exists(Path.Combine(InstallDir, "EqualizerAPO.dll"));
    public bool IsInstalled => UseWaveFixEngine ? WaveFixEngine.IsInstalled : EapoInstalled;
    public bool HasNoisePlugin => File.Exists(ActiveNoisePlugin);
    /// <summary>The RNNoise plugin the active engine loads.</summary>
    public string ActiveNoisePlugin => UseWaveFixEngine ? WaveFixEngine.NoisePlugin : NoisePluginPath;
    /// <summary>The name shown to users for the add-on that runs the effects.</summary>
    public string EngineName => UseWaveFixEngine ? "the WaveFix audio engine" : "Equalizer APO";

    /// <summary>Where the active engine stands on this mic (both engines use the endpoint slot).</summary>
    public SlotState Slot(string endpointId) =>
        UseWaveFixEngine ? (WaveFixEngine.IsAttached(endpointId) ? SlotState.Endpoint : SlotState.None) : GetSlot(endpointId);

    public EffectsEngine()
    {
        string? dir = null;
        try
        {
            using var key = Registry.LocalMachine.OpenSubKey(@"SOFTWARE\EqualizerAPO");
            dir = key?.GetValue("InstallPath") as string;
        }
        catch { }
        InstallDir = string.IsNullOrEmpty(dir) ? @"C:\Program Files\EqualizerAPO" : dir;
    }

    public enum SlotState { None, StreamOnly, Endpoint }

    /// <summary>
    /// Which Equalizer APO slot is attached to a capture endpoint. Only the endpoint slot is
    /// applied to raw-mode streams (Discord's default), so that is the one we want.
    /// </summary>
    public static SlotState GetSlot(string endpointId)
    {
        var guid = endpointId[(endpointId.LastIndexOf('.') + 1)..];
        try
        {
            using var key = Registry.LocalMachine.OpenSubKey(
                $@"SOFTWARE\Microsoft\Windows\CurrentVersion\MMDevices\Audio\Capture\{guid}\FxProperties");
            if (key == null) return SlotState.None;
            var efx = key.GetValue("{d04e05a6-594b-4fb6-a80d-01af5eed7d1d},7") as string;
            if (string.Equals(efx, EapoEndpointEffect, StringComparison.OrdinalIgnoreCase)) return SlotState.Endpoint;
            var sfx = key.GetValue("{d04e05a6-594b-4fb6-a80d-01af5eed7d1d},5") as string;
            if (string.Equals(sfx, EapoStreamEffect, StringComparison.OrdinalIgnoreCase)) return SlotState.StreamOnly;
        }
        catch { }
        return SlotState.None;
    }

    /// <summary>Writes config.txt (device routing) and wavefix-mic.txt (the effect chain).</summary>
    public void Apply(AppSettings s, string micEndpointId)
    {
        if (UseWaveFixEngine)
        {
            // The engine watches its config file; it runs only once setup has installed it.
            if (WaveFixEngine.IsInstalled) WaveFixEngine.WriteConfig(micEndpointId, s);
            return;
        }
        if (!IsInstalled) return;
        var guid = micEndpointId[(micEndpointId.LastIndexOf('.') + 1)..];

        var main = new StringBuilder()
            .AppendLine("# Managed by WaveFix. Changes here are overwritten by the app.")
            .AppendLine($"Device: {guid}")
            .AppendLine($"Include: {MicFileName}")
            .ToString();
        WriteIfChanged(Path.Combine(ConfigDir, "config.txt"), main);
        WriteIfChanged(Path.Combine(ConfigDir, MicFileName), BuildMicChain(s));
    }

    public string BuildMicChain(AppSettings s)
    {
        var sb = new StringBuilder().AppendLine("# WaveFix mic chain. Changes here are overwritten by the app.");
        // Order: rumble filter, then noise removal on the cleanest signal, then tone, the user's
        // plugins, voice polish and the voice changer, then level, then soundboard and peak protection.
        if (s.LowCut) sb.AppendLine("Filter: ON HP Fc 80 Hz");
        if (s.NoiseRemoval && HasNoisePlugin) sb.AppendLine(PluginState.ConfigLine(NoisePluginPath, s.NoiseState));
        var tone = s.EqPreset == EqPresets.Custom ? s.CustomEq.Lines() : EqPresets.Lines(s.EqPreset);
        foreach (var line in tone) sb.AppendLine(line);
        foreach (var p in s.Plugins.Where(p => p.Enabled && File.Exists(p.Path)))
            sb.AppendLine(PluginState.ConfigLine(p.Path, p.State));
        if (VoiceFx.MainLine(s.Polish, s.Voice) is { } fx) sb.AppendLine(fx);
        if (Math.Abs(s.VoiceBoostDb) > 0.05) sb.AppendLine(FormattableString.Invariant($"Preamp: {s.VoiceBoostDb:0.0} dB"));
        if (VoiceFx.OutputLine(s.Polish, s.Soundboard) is { } output) sb.AppendLine(output);
        return sb.ToString();
    }

    static void WriteIfChanged(string path, string content)
    {
        if (File.Exists(path) && File.ReadAllText(path) == content) return;
        File.WriteAllText(path, content, new UTF8Encoding(false));
    }
}
