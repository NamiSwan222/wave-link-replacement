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

    /// <summary>Equalizer APO filter lines for each preset.</summary>
    public static IEnumerable<string> Lines(string preset) => preset switch
    {
        Warm => new[]
        {
            "Filter: ON LS Fc 180 Hz Gain 3 dB",
            "Filter: ON PK Fc 3500 Hz Gain -1.5 dB Q 1",
        },
        Clear => new[]
        {
            "Filter: ON PK Fc 300 Hz Gain -3 dB Q 1",
            "Filter: ON PK Fc 3000 Hz Gain 2 dB Q 1",
            "Filter: ON HS Fc 6000 Hz Gain 3 dB",
        },
        Broadcast => new[]
        {
            "Filter: ON LS Fc 120 Hz Gain 2.5 dB",
            "Filter: ON PK Fc 400 Hz Gain -2.5 dB Q 1.2",
            "Filter: ON PK Fc 2500 Hz Gain 2.5 dB Q 1",
            "Filter: ON HS Fc 8000 Hz Gain 2 dB",
        },
        Radio => new[]
        {
            "Filter: ON HP Fc 300 Hz",
            "Filter: ON LP Fc 3400 Hz",
            "Filter: ON PK Fc 1500 Hz Gain 4 dB Q 1",
        },
        _ => Array.Empty<string>(),
    };
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

    public bool IsInstalled => File.Exists(Path.Combine(InstallDir, "EqualizerAPO.dll"));
    public bool HasNoisePlugin => File.Exists(NoisePluginPath);

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
