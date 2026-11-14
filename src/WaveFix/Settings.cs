using System.IO;
using System.Text.Json;

namespace WaveFix;

/// <summary>A short log of important setting changes and why they happened, for troubleshooting.</summary>
public static class ChangeLog
{
    static string Path => System.IO.Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "WaveFix", "changes.log");

    public static void Write(string what)
    {
        try
        {
            Directory.CreateDirectory(System.IO.Path.GetDirectoryName(Path)!);
            var fi = new FileInfo(Path);
            if (fi.Exists && fi.Length > 200_000) fi.Delete(); // keep it small
            File.AppendAllText(Path, $"{DateTime.Now:yyyy-MM-dd HH:mm:ss}  v{Updater.Current}  pid {Environment.ProcessId}  {what}{Environment.NewLine}");
        }
        catch { }
    }
}

/// <summary>User settings, stored as JSON in %APPDATA%\WaveFix\settings.json.</summary>
public sealed class AppSettings
{
    public string? MicDeviceId { get; set; }
    /// <summary>
    /// "wavefix" once the mic runs on WaveFix's own engine. "eapo" (or empty with Equalizer APO on
    /// the mic) means a setup from before the engine existed, which setup switches over.
    /// </summary>
    public string? Engine { get; set; }
    /// <summary>True when settings.json existed at startup (an existing WaveFix user).</summary>
    [System.Text.Json.Serialization.JsonIgnore] public bool Existed { get; private set; }
    public bool NoiseRemoval { get; set; } = true;
    public bool LowCut { get; set; } = true;
    public string EqPreset { get; set; } = EqPresets.Flat;
    public double VoiceBoostDb { get; set; }
    public bool StartWithWindows { get; set; }
    /// <summary>Download and install new GitHub releases automatically (checked every few hours).</summary>
    public bool AutoUpdate { get; set; } = true;
    /// <summary>Action id → hotkey text, e.g. "toggle-mute" → "Ctrl+Alt+M".</summary>
    public Dictionary<string, string> Shortcuts { get; set; } = new();
    /// <summary>Noise removal (RNNoise) settings made in its settings window; null = defaults.</summary>
    public PluginState? NoiseState { get; set; }
    /// <summary>User VST plugins, in chain order.</summary>
    public List<PluginEntry> Plugins { get; set; } = new();
    /// <summary>Leveler, de-esser, background silencer and peak protection.</summary>
    public PolishSettings Polish { get; set; } = new();
    /// <summary>Band gains used when the voice tone is Custom.</summary>
    public CustomEq CustomEq { get; set; } = new();
    public VoiceSettings Voice { get; set; } = new();
    public SoundboardSettings Soundboard { get; set; } = new();

    static string Folder => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "WaveFix");
    static string FilePath => Path.Combine(Folder, "settings.json");
    static readonly JsonSerializerOptions Json = new() { WriteIndented = true };

    public static AppSettings Load()
    {
        try
        {
            if (File.Exists(FilePath))
            {
                var s = JsonSerializer.Deserialize<AppSettings>(File.ReadAllText(FilePath)) ?? new AppSettings();
                s.Existed = true;
                return s;
            }
        }
        catch { /* corrupt file: fall back to defaults */ }
        return new AppSettings();
    }

    public void Save()
    {
        try
        {
            Directory.CreateDirectory(Folder);
            File.WriteAllText(FilePath, JsonSerializer.Serialize(this, Json));
        }
        catch { /* settings are a convenience; never crash over them */ }
    }
}
