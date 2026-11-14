using System.IO;
using System.Text;

namespace WaveFix;

/// <summary>A user-added VST2 effect in the mic chain. Saved in settings.json.</summary>
public sealed class PluginEntry : Observable
{
    public string Name { get; set; } = "";
    public string Path { get; set; } = "";

    bool _enabled = true;
    public bool Enabled { get => _enabled; set => Set(ref _enabled, value); }

    /// <summary>Settings made in the plugin's window (null = the plugin's defaults).</summary>
    public PluginState? State { get; set; }
}

/// <summary>
/// Adds user VST plugins. Equalizer APO loads them inside audiodg.exe (running as LOCAL SERVICE),
/// which can't read files in a user's profile, so plugins are copied to a shared folder.
/// </summary>
public static class PluginStore
{
    public static string Folder => System.IO.Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData), "WaveFix", "Plugins");

    /// <summary>Checks the file is a 64-bit VST2 plugin. Returns an error message, or null if OK.</summary>
    public static string? Validate(string file)
    {
        if (file.EndsWith(".vst3", StringComparison.OrdinalIgnoreCase))
            return "That's a VST3 plugin. Windows' audio engine (through Equalizer APO) only runs VST2 plugins (.dll). Many plugins ship both: look for the .dll version.";
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

    /// <summary>Copies the plugin into the shared folder and returns the new entry.</summary>
    public static PluginEntry Install(string file)
    {
        Directory.CreateDirectory(Folder);
        var name = System.IO.Path.GetFileNameWithoutExtension(file);
        var dest = System.IO.Path.Combine(Folder, System.IO.Path.GetFileName(file));
        // Don't overwrite a different plugin that happens to share a file name.
        for (int i = 2; File.Exists(dest) && !SameFile(dest, file); i++)
            dest = System.IO.Path.Combine(Folder, $"{name} ({i}).dll");
        if (!File.Exists(dest)) File.Copy(file, dest);
        return new PluginEntry { Name = name, Path = dest, Enabled = true };
    }

    static bool SameFile(string a, string b)
    {
        var fa = new FileInfo(a); var fb = new FileInfo(b);
        return fa.Length == fb.Length && File.ReadAllBytes(a).AsSpan().SequenceEqual(File.ReadAllBytes(b));
    }

    /// <summary>Deletes copies that no plugin entry uses any more (left behind while they were still loaded).</summary>
    public static void CleanOrphans(IEnumerable<PluginEntry> inUse)
    {
        if (!Directory.Exists(Folder)) return;
        var used = new HashSet<string>(inUse.Select(p => p.Path), StringComparer.OrdinalIgnoreCase);
        foreach (var f in Directory.EnumerateFiles(Folder, "*.dll"))
            if (!used.Contains(f)) try { File.Delete(f); } catch { /* still loaded; try next start */ }
    }

    /// <summary>Deletes the copied file if no other entry uses it (best effort: it may be loaded).</summary>
    public static void Uninstall(PluginEntry p, IEnumerable<PluginEntry> remaining)
    {
        if (!p.Path.StartsWith(Folder, StringComparison.OrdinalIgnoreCase)) return;
        if (remaining.Any(r => string.Equals(r.Path, p.Path, StringComparison.OrdinalIgnoreCase))) return;
        try { File.Delete(p.Path); } catch { /* still loaded by the audio engine; harmless */ }
    }
}
