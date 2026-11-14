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
    [System.Text.Json.Serialization.JsonIgnore] public bool Existed { get; set; }
    public bool NoiseRemoval { get; set; } = true;
    /// <summary>"standard" (RNNoise plugin) or "strong" (DeepFilterNet, built into the engine).</summary>
    public string NoiseModel { get; set; } = "standard";
    /// <summary>Strong noise removal: the most it turns noise down, in dB (100 = all the way).</summary>
    public double NoiseStrengthDb { get; set; } = 100;
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
    public Rvc.AiVoiceSettings AiVoice { get; set; } = new();
    /// <summary>Play the mic (with every effect) to your speakers or headphones.</summary>
    public bool HearMyself { get; set; }
    public double HearMyselfVolume { get; set; } = 80;

    static string Folder => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "WaveFix");
    static string FilePath => Path.Combine(Folder, "settings.json");
    // Named literals (NaN, Infinity): a plugin can report a knob as NaN; that must never stop
    // WaveFix from saving (it used to fail silently, losing every later change).
    static readonly JsonSerializerOptions Json = new()
    {
        WriteIndented = true,
        NumberHandling = System.Text.Json.Serialization.JsonNumberHandling.AllowNamedFloatingPointLiterals,
    };

    /// <summary>The previous good settings (every save keeps one), used if settings.json is damaged.</summary>
    static string BackupPath => FilePath + ".bak";

    /// <summary>Settings this version doesn't know (e.g. from a newer WaveFix): kept, so saving never drops them.</summary>
    [System.Text.Json.Serialization.JsonExtensionData] public Dictionary<string, System.Text.Json.JsonElement>? Extra { get; set; }

    /// <summary>
    /// Loads settings.json, or its backup if it's damaged (e.g. WaveFix was closed in the middle of
    /// saving, as an update does). If both are unreadable, the damaged file is kept for rescue as
    /// settings.corrupt-&lt;time&gt;.json before starting fresh, so nothing is silently overwritten.
    /// </summary>
    public static AppSettings Load()
    {
        foreach (var path in new[] { FilePath, BackupPath })
        {
            try
            {
                if (!File.Exists(path)) continue;
                var text = File.ReadAllText(path);
                if (string.IsNullOrWhiteSpace(text)) throw new InvalidDataException("empty");
                var s = JsonSerializer.Deserialize<AppSettings>(text, Json) ?? throw new InvalidDataException("null");
                s.Existed = true;
                if (path == BackupPath) ChangeLog.Write("settings.json was damaged: restored the previous settings from settings.json.bak");
                return s;
            }
            catch (Exception ex) { ChangeLog.Write($"Couldn't read {Path.GetFileName(path)}: {ex.Message}"); }
        }
        if (File.Exists(FilePath))
        {
            var keep = Path.Combine(Folder, $"settings.corrupt-{DateTime.Now:yyyyMMdd-HHmmss}.json");
            try { File.Copy(FilePath, keep, overwrite: false); ChangeLog.Write($"Kept the unreadable settings as {keep}"); } catch { }
            return new AppSettings { Existed = true };
        }
        return new AppSettings();
    }

    /// <summary>
    /// Saves without ever leaving a half-written file: writes settings.json.tmp, then swaps it in
    /// and keeps the previous version as settings.json.bak.
    /// </summary>
    public void Save()
    {
        try
        {
            Directory.CreateDirectory(Folder);
            var tmp = FilePath + ".tmp";
            using (var fs = new FileStream(tmp, FileMode.Create, FileAccess.Write, FileShare.None, 4096, FileOptions.WriteThrough))
            using (var w = new StreamWriter(fs, new System.Text.UTF8Encoding(false)))
                w.Write(JsonSerializer.Serialize(this, Json));
            if (File.Exists(FilePath))
            {
                try { File.Replace(tmp, FilePath, BackupPath, ignoreMetadataErrors: true); }
                catch (IOException) { File.Copy(FilePath, BackupPath, overwrite: true); File.Move(tmp, FilePath, overwrite: true); }
            }
            else File.Move(tmp, FilePath);
        }
        catch (Exception ex)
        {
            // Never crash over settings, but say so: a save that keeps failing loses changes.
            if (Environment.TickCount64 - _lastSaveError > 60_000)
            {
                _lastSaveError = Environment.TickCount64;
                ChangeLog.Write($"Couldn't save settings: {ex.Message}");
            }
        }
    }

    static long _lastSaveError = -60_000;
}
