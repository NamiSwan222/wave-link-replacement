using System.IO;
using System.Text;

namespace WaveFix;

/// <summary>A user-added VST2 effect in the mic chain. Saved in settings.json.</summary>
public sealed class PluginEntry : Observable
{
    /// <summary>Settings this version doesn't know (e.g. from a newer WaveFix): kept, so saving never drops them.</summary>
    [System.Text.Json.Serialization.JsonExtensionData] public Dictionary<string, System.Text.Json.JsonElement>? Extra { get; set; }

    public string Name { get; set; } = "";
    public string Path { get; set; } = "";

    bool _enabled = true;
    public bool Enabled { get => _enabled; set { if (Set(ref _enabled, value)) Raise(nameof(StatusText)); } }

    PluginState? _state;
    /// <summary>Settings made in the plugin's window (null = the plugin's defaults).</summary>
    public PluginState? State { get => _state; set { _state = value; Raise(); Raise(nameof(StatusText)); } }

    // ---- For the list only (not saved) ----
    [System.Text.Json.Serialization.JsonIgnore]
    public string StatusText => !Enabled ? "Off" : State == null || State.IsEmpty ? "On · default settings" : "On · your settings";

    bool _expanded;
    [System.Text.Json.Serialization.JsonIgnore] public bool Expanded { get => _expanded; set => Set(ref _expanded, value); }

    int _position;
    /// <summary>1-based place in the chain.</summary>
    [System.Text.Json.Serialization.JsonIgnore] public int Position { get => _position; set => Set(ref _position, value); }
}

/// <summary>
/// Adds user VST plugins. WaveFix's engine loads them inside audiodg.exe, and only from its
/// admin-only folder in Program Files, so plugins are copied there (one Windows prompt).
/// </summary>
public static class PluginStore
{
    /// <summary>Program Files\WaveFix\Plugins: admin-only, so nothing else can swap a plugin for other code.</summary>
    public static string Folder => WaveFixEngine.PluginDir;

    /// <summary>Checks the file is a 64-bit VST2 plugin. Returns an error message, or null if OK.</summary>
    public static string? Validate(string file)
    {
        if (file.EndsWith(".vst3", StringComparison.OrdinalIgnoreCase))
            return "That's a VST3 plugin. WaveFix's audio engine only runs VST2 plugins (.dll). Many plugins ship both: look for the .dll version.";
        byte[] bytes;
        try { bytes = File.ReadAllBytes(file); }
        catch (Exception ex) { return $"Couldn't read the file: {ex.Message}"; }

        if (bytes.Length < 0x40 || bytes[0] != 'M' || bytes[1] != 'Z') return "That file isn't a plugin (.dll).";
        int pe = BitConverter.ToInt32(bytes, 0x3C);
        if (pe <= 0 || pe + 6 > bytes.Length || Encoding.ASCII.GetString(bytes, pe, 4) != "PE\0\0") return "That file isn't a valid .dll.";
        ushort machine = BitConverter.ToUInt16(bytes, pe + 4);
        if (machine == 0x014C) return "That's a 32-bit plugin. WaveFix needs the 64-bit version (often in a folder named x64 or VST64).";
        if (machine != 0x8664) return "That plugin isn't built for this PC (it needs to be 64-bit Windows).";

        var text = Encoding.ASCII.GetString(bytes);
        if (!text.Contains("VSTPluginMain") && !text.Contains("\0main\0"))
            return "This .dll doesn't look like a VST2 plugin.";
        return null;
    }

    /// <summary>Where VST2 plugins usually live on Windows, plus the folder set in the registry.</summary>
    public static List<string> DefaultScanFolders()
    {
        var pf = Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles);
        var common = Environment.GetFolderPath(Environment.SpecialFolder.CommonProgramFiles);
        var list = new List<string>
        {
            System.IO.Path.Combine(pf, "VSTPlugins"),
            System.IO.Path.Combine(pf, "Steinberg", "VSTPlugins"),
            System.IO.Path.Combine(pf, "VST"),
            System.IO.Path.Combine(pf, "VST2"),
            System.IO.Path.Combine(pf, "Vstplugins64"),
            System.IO.Path.Combine(common, "VST2"),
            System.IO.Path.Combine(common, "Steinberg", "VST2"),
            System.IO.Path.Combine(pf, "EqualizerAPO", "VSTPlugins"),
        };
        try
        {
            using var key = Microsoft.Win32.Registry.LocalMachine.OpenSubKey(@"SOFTWARE\VST");
            if (key?.GetValue("VSTPluginsPath") is string reg && !string.IsNullOrWhiteSpace(reg)) list.Add(reg);
        }
        catch { }
        return list.Where(Directory.Exists).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
    }

    public sealed record Found(string Name, string Path);

    // ---- The last search's results, so the next one is instant ----

    public sealed record ScanCache(DateTime Time, List<Found> Plugins);

    static string CachePath => System.IO.Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "WaveFix", "plugin-scan.json");

    /// <summary>Plugins found by earlier searches that are still there, or null if none were saved.</summary>
    public static ScanCache? LoadCache()
    {
        try
        {
            if (!File.Exists(CachePath)) return null;
            var c = System.Text.Json.JsonSerializer.Deserialize<ScanCache>(File.ReadAllText(CachePath));
            if (c?.Plugins == null) return null;
            return c with { Plugins = c.Plugins.Where(f => !string.IsNullOrEmpty(f.Path) && File.Exists(f.Path)).ToList() };
        }
        catch { return null; }
    }

    /// <summary>Adds a search's results to the saved list (a full rescan replaces it).</summary>
    public static void SaveCache(IEnumerable<Found> found, bool replace)
    {
        try
        {
            var all = replace ? new List<Found>() : LoadCache()?.Plugins ?? new List<Found>();
            foreach (var f in found)
                if (!all.Any(a => string.Equals(a.Path, f.Path, StringComparison.OrdinalIgnoreCase))) all.Add(f);
            all = all.OrderBy(f => f.Name, StringComparer.OrdinalIgnoreCase).ToList();
            Directory.CreateDirectory(System.IO.Path.GetDirectoryName(CachePath)!);
            var tmp = CachePath + ".tmp";
            File.WriteAllText(tmp, System.Text.Json.JsonSerializer.Serialize(new ScanCache(DateTime.Now, all)));
            File.Move(tmp, CachePath, overwrite: true);
        }
        catch { /* only a convenience */ }
    }

    /// <summary>Finds usable (64-bit VST2) plugins in the given folders. Runs on a background thread.</summary>
    public static List<Found> Scan(IEnumerable<string> folders, IEnumerable<string> skipPaths, CancellationToken ct = default)
    {
        var skip = new HashSet<string>(skipPaths, StringComparer.OrdinalIgnoreCase);
        var seenNames = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var results = new List<Found>();
        var opts = new EnumerationOptions { RecurseSubdirectories = true, MaxRecursionDepth = 5, IgnoreInaccessible = true };
        foreach (var folder in folders)
        {
            IEnumerable<string> files;
            try { files = Directory.EnumerateFiles(folder, "*.dll", opts); }
            catch { continue; }
            foreach (var f in files)
            {
                ct.ThrowIfCancellationRequested();
                if (skip.Contains(f)) continue;
                try { if (new FileInfo(f).Length > 200_000_000) continue; } catch { continue; }
                if (Validate(f) != null) continue;
                var name = System.IO.Path.GetFileNameWithoutExtension(f);
                // The same plugin is often installed in two folders; list it once.
                if (!seenNames.Add(name)) continue;
                results.Add(new Found(name, f));
            }
        }
        return results.OrderBy(r => r.Name, StringComparer.OrdinalIgnoreCase).ToList();
    }

    /// <summary>
    /// Copies a plugin into Program Files by an admin helper (one Windows prompt) and returns its
    /// entry, or null if the prompt was declined.
    /// </summary>
    public static async Task<PluginEntry?> InstallAsync(string file)
    {
        var name = System.IO.Path.GetFileNameWithoutExtension(file);
        var dest = System.IO.Path.Combine(Folder, System.IO.Path.GetFileName(file));
        for (int i = 2; File.Exists(dest) && !SameFile(dest, file); i++)
            dest = System.IO.Path.Combine(Folder, $"{name} ({i}).dll");
        if (!File.Exists(dest) && await Setup.InstallPluginElevated(file, dest) == null) return null;
        return new PluginEntry { Name = name, Path = dest, Enabled = true };
    }

    static bool SameFile(string a, string b)
    {
        var fa = new FileInfo(a); var fb = new FileInfo(b);
        return fa.Length == fb.Length && File.ReadAllBytes(a).AsSpan().SequenceEqual(File.ReadAllBytes(b));
    }
}
