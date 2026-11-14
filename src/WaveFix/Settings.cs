using System.IO;
using System.Text.Json;

namespace WaveFix;

/// <summary>User settings, stored as JSON in %APPDATA%\WaveFix\settings.json.</summary>
public sealed class AppSettings
{
    public string? MicDeviceId { get; set; }
    public bool NoiseRemoval { get; set; } = true;
    public bool LowCut { get; set; } = true;
    public string EqPreset { get; set; } = EqPresets.Flat;
    public double VoiceBoostDb { get; set; }
    public bool StartWithWindows { get; set; }

    static string Folder => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "WaveFix");
    static string FilePath => Path.Combine(Folder, "settings.json");
    static readonly JsonSerializerOptions Json = new() { WriteIndented = true };

    public static AppSettings Load()
    {
        try
        {
            if (File.Exists(FilePath))
                return JsonSerializer.Deserialize<AppSettings>(File.ReadAllText(FilePath)) ?? new AppSettings();
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
