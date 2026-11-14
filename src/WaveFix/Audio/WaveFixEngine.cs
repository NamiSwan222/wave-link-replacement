using System.IO;
using System.Security.AccessControl;
using System.Security.Cryptography;
using System.Security.Principal;
using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.Win32;

namespace WaveFix;

/// <summary>
/// WaveFix's own audio engine (native/wavefix-engine): a Windows audio effect (APO) that runs the
/// mic chain inside audiodg.exe, used instead of Equalizer APO on new installs.
///
/// Layout:
///   Program Files\WaveFix\Engine\wavefix-engine-&lt;hash&gt;.dll   the engine (admin-only folder)
///   Program Files\WaveFix\Plugins\...                          VST plugins it may load (admin-only)
///   ProgramData\WaveFix\Engine\config\&lt;mic&gt;.json              the chain the app asks for
///   ProgramData\WaveFix\Engine\status\&lt;mic&gt;.json              what the engine reports back
/// Code only ever loads from the admin-only folder; the app only writes data.
/// </summary>
public static class WaveFixEngine
{
    public const string Clsid = "{1F0E0A0C-41EE-4E1A-B2C4-0681D5344BDD}";
    /// <summary>IID_IAudioProcessingObject.</summary>
    const string IidApo = "{FD7F2B29-24D0-4B5C-B177-592C39F9CA10}";
    const string ResourceName = "WaveFix.Engine.dll";
    const string FilePrefix = "wavefix-engine-";

    const string FxClsid = "{d04e05a6-594b-4fb6-a80d-01af5eed7d1d}"; // PKEY_FX_*EffectClsid
    const string FxModes = "{d3993a3f-99c2-4402-b5ec-a92a0367664b}"; // PKEY_*_ProcessingModes_Supported_For_Streaming
    const string ModeDefault = "{C18E2F7E-933D-4965-B7D1-1EEF228D2AF3}";
    /// <summary>,7 = endpoint effect (EFX); ,15 = the composite list of endpoint effects (Windows 10+).</summary>
    const int Efx = 7, CompositeEfx = 15;
    const string BackupKey = @"SOFTWARE\WaveFix\Backup";

    public static string InstallDir => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "WaveFix");
    public static string EngineDir => Path.Combine(InstallDir, "Engine");
    public static string PluginDir => Path.Combine(InstallDir, "Plugins");
    public static string NoisePlugin => Path.Combine(PluginDir, "rnnoise", "rnnoise_mono.dll");
    public static string DataDir => VoiceFx.Folder; // ProgramData\WaveFix\Engine (also holds the soundboard bridge)

    static string Name(string endpointIdOrGuid) => endpointIdOrGuid[(endpointIdOrGuid.LastIndexOf('.') + 1)..].Trim('{', '}').ToLowerInvariant();
    public static string ConfigPath(string endpointId) => Path.Combine(DataDir, "config", Name(endpointId) + ".json");
    public static string StatusPath(string endpointId) => Path.Combine(DataDir, "status", Name(endpointId) + ".json");

    // ---------------- The engine DLL inside WaveFix.exe ----------------

    static byte[]? _embedded;
    static bool _loaded;
    static byte[]? Embedded
    {
        get
        {
            if (_loaded) return _embedded;
            _loaded = true;
            using var s = typeof(WaveFixEngine).Assembly.GetManifestResourceStream(ResourceName);
            if (s == null) return null;
            using var ms = new MemoryStream();
            s.CopyTo(ms);
            return _embedded = ms.ToArray();
        }
    }

    /// <summary>This build includes the engine.</summary>
    public static bool Available => Embedded != null;
    static string? EmbeddedHash => Embedded is { } b ? Convert.ToHexString(SHA256.HashData(b))[..12].ToLowerInvariant() : null;

    static RegistryKey Hklm => RegistryKey.OpenBaseKey(RegistryHive.LocalMachine, RegistryView.Registry64);

    /// <summary>The DLL Windows will load (from the COM registration), if registered.</summary>
    public static string? RegisteredPath
    {
        get
        {
            try
            {
                using var k = Hklm.OpenSubKey($@"SOFTWARE\Classes\CLSID\{Clsid}\InprocServer32");
                return k?.GetValue(null) as string;
            }
            catch { return null; }
        }
    }

    public static bool IsInstalled
    {
        get
        {
            try
            {
                using var apo = Hklm.OpenSubKey($@"SOFTWARE\Classes\AudioEngine\AudioProcessingObjects\{Clsid}");
                return apo != null && RegisteredPath is { } p && File.Exists(p);
            }
            catch { return false; }
        }
    }

    /// <summary>Installed, but an older engine than the one in this WaveFix.exe.</summary>
    public static bool NeedsUpdate => IsInstalled && EmbeddedHash is { } h && !(RegisteredPath ?? "").Contains(FilePrefix + h, StringComparison.OrdinalIgnoreCase);

    public static bool HasNoisePlugin => File.Exists(NoisePlugin);

    // ---------------- Admin helper: install and register ----------------

    /// <summary>
    /// Copies the engine into Program Files, registers it with Windows and prepares the data
    /// folders. Runs in the elevated setup helper. A running audio engine may still have the old
    /// version loaded, so each version gets its own file name.
    /// </summary>
    public static void Install()
    {
        var bytes = Embedded ?? throw new InvalidOperationException("This copy of WaveFix doesn't include its audio engine.");
        Directory.CreateDirectory(EngineDir);
        var dll = Path.Combine(EngineDir, $"{FilePrefix}{EmbeddedHash}.dll");
        if (!File.Exists(dll) || !File.ReadAllBytes(dll).AsSpan().SequenceEqual(bytes))
        {
            var tmp = dll + ".tmp";
            File.WriteAllBytes(tmp, bytes);
            File.Move(tmp, dll, overwrite: true);
        }

        using (var clsid = Hklm.CreateSubKey($@"SOFTWARE\Classes\CLSID\{Clsid}"))
        {
            clsid.SetValue(null, "WaveFix Engine");
            using var inproc = clsid.CreateSubKey("InprocServer32");
            inproc.SetValue(null, dll);
            inproc.SetValue("ThreadingModel", "Both");
        }
        // The same values a driver's INF writes for its APOs (see Microsoft's sysvad sample).
        using (var apo = Hklm.CreateSubKey($@"SOFTWARE\Classes\AudioEngine\AudioProcessingObjects\{Clsid}"))
        {
            apo.SetValue("FriendlyName", "WaveFix Engine");
            apo.SetValue("Copyright", "WaveFix");
            apo.SetValue("MajorVersion", 1, RegistryValueKind.DWord);
            apo.SetValue("MinorVersion", 0, RegistryValueKind.DWord);
            apo.SetValue("Flags", 15, RegistryValueKind.DWord); // in place, channels/rate/bits must match
            apo.SetValue("MinInputConnections", 1, RegistryValueKind.DWord);
            apo.SetValue("MaxInputConnections", 1, RegistryValueKind.DWord);
            apo.SetValue("MinOutputConnections", 1, RegistryValueKind.DWord);
            apo.SetValue("MaxOutputConnections", 1, RegistryValueKind.DWord);
            apo.SetValue("MaxInstances", unchecked((int)0xFFFFFFFF), RegistryValueKind.DWord);
            apo.SetValue("NumAPOInterfaces", 1, RegistryValueKind.DWord);
            apo.SetValue("APOInterface0", IidApo);
        }
        PrepareDataFolders();
        // Older engine versions that nothing loads any more.
        foreach (var old in Directory.EnumerateFiles(EngineDir, FilePrefix + "*.dll"))
            if (!string.Equals(old, dll, StringComparison.OrdinalIgnoreCase)) try { File.Delete(old); } catch { }
    }

    /// <summary>
    /// ProgramData\WaveFix\Engine: users write the config (data only) and the soundboard bridge;
    /// the engine (LOCAL SERVICE) writes its status.
    /// </summary>
    static void PrepareDataFolders()
    {
        var users = new SecurityIdentifier(WellKnownSidType.BuiltinUsersSid, null);
        var localService = new SecurityIdentifier(WellKnownSidType.LocalServiceSid, null);
        var inherit = InheritanceFlags.ContainerInherit | InheritanceFlags.ObjectInherit;
        Grant(DataDir, users, FileSystemRights.Modify, inherit);
        Grant(Path.Combine(DataDir, "config"), users, FileSystemRights.Modify, inherit);
        Grant(Path.Combine(DataDir, "status"), localService, FileSystemRights.Modify, inherit);
        Directory.CreateDirectory(PluginDir);
    }

    static void Grant(string dir, SecurityIdentifier who, FileSystemRights rights, InheritanceFlags inherit)
    {
        var di = Directory.CreateDirectory(dir);
        var acl = di.GetAccessControl();
        acl.AddAccessRule(new FileSystemAccessRule(who, rights, inherit, PropagationFlags.None, AccessControlType.Allow));
        di.SetAccessControl(acl);
    }

    // ---------------- Attaching to a mic ----------------

    static string FxPath(string guid) => $@"SOFTWARE\Microsoft\Windows\CurrentVersion\MMDevices\Audio\Capture\{{{guid.Trim('{', '}')}}}\FxProperties";

    /// <summary>The engine is in this mic's endpoint effects (the composite list or the single slot).</summary>
    public static bool IsAttached(string endpointId)
    {
        try
        {
            using var k = Hklm.OpenSubKey(FxPath(Name(endpointId)));
            if (k == null) return false;
            if (k.GetValue($"{FxClsid},{CompositeEfx}") is string[] list) return list.Any(IsUs);
            return k.GetValue($"{FxClsid},{Efx}") is string one && IsUs(one);
        }
        catch { return false; }
    }

    static bool IsUs(string clsid) => string.Equals(clsid.Trim(), Clsid, StringComparison.OrdinalIgnoreCase);

    /// <summary>Saves the mic's original effect settings before the first change (admin helper).</summary>
    public static void BackupMic(string guid)
    {
        using var existing = Hklm.OpenSubKey($@"{BackupKey}\{Name(guid)}");
        if (existing != null) return;
        using var fx = Hklm.OpenSubKey(FxPath(guid));
        using var key = Hklm.CreateSubKey($@"{BackupKey}\{Name(guid)}");
        foreach (var name in new[] { $"{FxClsid},{Efx}", $"{FxClsid},{CompositeEfx}", $"{FxModes},{Efx}" })
        {
            var v = fx?.GetValue(name);
            if (v is string s) key.SetValue(name, s, RegistryValueKind.String);
            else if (v is string[] m) key.SetValue(name, m, RegistryValueKind.MultiString);
            else key.SetValue(name + " (none)", 1, RegistryValueKind.DWord);
        }
    }

    /// <summary>
    /// SYSTEM step: puts the engine last in the mic's endpoint effects, keeping any effects that
    /// were there (Windows runs the composite list in order). Endpoint effects apply to every
    /// stream, including the raw mode Discord uses.
    /// </summary>
    public static void Attach(RegistryKey fx)
    {
        var composite = fx.GetValue($"{FxClsid},{CompositeEfx}") as string[];
        var single = fx.GetValue($"{FxClsid},{Efx}") as string;
        var list = new List<string>();
        if (composite != null) list.AddRange(composite.Where(c => !string.IsNullOrWhiteSpace(c)));
        else if (!string.IsNullOrWhiteSpace(single) && !single.Trim('{', '}').All(c => c is '0' or '-')) list.Add(single);
        list.RemoveAll(IsUs);
        list.Add(Clsid);
        fx.SetValue($"{FxClsid},{CompositeEfx}", list.ToArray(), RegistryValueKind.MultiString);
        if (fx.GetValue($"{FxModes},{Efx}") == null)
            fx.SetValue($"{FxModes},{Efx}", new[] { ModeDefault }, RegistryValueKind.MultiString);
    }

    /// <summary>SYSTEM step for Undo: puts back exactly what the backup recorded.</summary>
    public static void Detach(RegistryKey fx, string guid)
    {
        using var backup = Hklm.OpenSubKey($@"{BackupKey}\{Name(guid)}");
        if (backup == null)
        {
            // No backup: just take the engine out of the list.
            if (fx.GetValue($"{FxClsid},{CompositeEfx}") is string[] list)
            {
                var rest = list.Where(c => !IsUs(c)).ToArray();
                if (rest.Length > 0) fx.SetValue($"{FxClsid},{CompositeEfx}", rest, RegistryValueKind.MultiString);
                else fx.DeleteValue($"{FxClsid},{CompositeEfx}", false);
            }
            return;
        }
        foreach (var name in new[] { $"{FxClsid},{Efx}", $"{FxClsid},{CompositeEfx}", $"{FxModes},{Efx}" })
        {
            var v = backup.GetValue(name);
            if (v is string s) fx.SetValue(name, s, RegistryValueKind.String);
            else if (v is string[] m) fx.SetValue(name, m, RegistryValueKind.MultiString);
            else fx.DeleteValue(name, false);
        }
    }

    public static void ForgetBackup(string guid)
    {
        try { Hklm.DeleteSubKeyTree($@"{BackupKey}\{Name(guid)}", false); } catch { }
    }

    // ---------------- The chain (app side) ----------------

    /// <summary>The engine config for the current settings, as JSON (see native/wavefix-engine/src/chain.rs).</summary>
    public static string BuildConfig(AppSettings s)
    {
        var stages = new JsonArray();
        void Filter(string kind, double freq, double gain = 0, double? q = null)
        {
            var o = new JsonObject { ["type"] = "filter", ["kind"] = kind, ["freq"] = freq, ["gain"] = gain };
            if (q != null) o["q"] = q;
            stages.Add(o);
        }
        static JsonObject Params(Dictionary<string, float> p) { var o = new JsonObject(); foreach (var (k, v) in p) o[k] = v; return o; }
        JsonObject Vst(string path, PluginState? state)
        {
            var o = new JsonObject { ["type"] = "vst", ["path"] = path };
            if (!string.IsNullOrEmpty(state?.ChunkData)) o["chunk"] = state.ChunkData;
            else if (state?.Params is { Count: > 0 } p) o["params"] = Params(p);
            return o;
        }

        // Same order as the Equalizer APO chain: rumble filter, noise removal, tone, the user's
        // plugins, voice polish and voice changer, boost, then soundboard and peak protection.
        if (s.LowCut) Filter("hp", 80);
        if (s.NoiseRemoval && HasNoisePlugin) stages.Add(Vst(NoisePlugin, s.NoiseState));
        if (s.EqPreset == EqPresets.Custom)
        {
            for (int i = 0; i < CustomEq.Bands.Length && i < s.CustomEq.Gains.Length; i++)
                if (Math.Abs(s.CustomEq.Gains[i]) >= 0.05)
                {
                    var (_, _, type, hz) = CustomEq.Bands[i];
                    Filter(type.ToLowerInvariant(), hz, s.CustomEq.Gains[i], type == "PK" ? 1 : null);
                }
        }
        else foreach (var f in EqPresets.Filters(s.EqPreset)) Filter(f.Kind, f.Hz, f.Gain, f.Q);
        foreach (var p in s.Plugins.Where(p => p.Enabled && File.Exists(p.Path)))
            stages.Add(Vst(p.Path, p.State));
        if (VoiceFx.MainParams(s.Polish, s.Voice) is { } main) stages.Add(new JsonObject { ["type"] = "fx", ["params"] = Params(main) });
        if (Math.Abs(s.VoiceBoostDb) > 0.05) stages.Add(new JsonObject { ["type"] = "gain", ["db"] = Math.Round(s.VoiceBoostDb, 1) });
        if (VoiceFx.OutputParams(s.Polish, s.Soundboard) is { } output) stages.Add(new JsonObject { ["type"] = "fx", ["params"] = Params(output) });

        return new JsonObject { ["stages"] = stages }.ToJsonString(new JsonSerializerOptions { WriteIndented = true });
    }

    public static void WriteConfig(string endpointId, AppSettings s)
    {
        var path = ConfigPath(endpointId);
        var json = BuildConfig(s);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        if (File.Exists(path) && File.ReadAllText(path) == json) return;
        var tmp = path + ".tmp";
        File.WriteAllText(tmp, json);
        File.Move(tmp, path, overwrite: true); // the engine never sees a half-written file
    }

    public sealed record Status(bool Running, string[] Warnings);

    /// <summary>What the engine last reported for this mic (it reports while the mic is in use).</summary>
    public static Status? ReadStatus(string endpointId)
    {
        try
        {
            var json = JsonNode.Parse(File.ReadAllText(StatusPath(endpointId)));
            var time = json?["time"]?.GetValue<long>() ?? 0;
            bool fresh = DateTimeOffset.UtcNow.ToUnixTimeSeconds() - time < 15;
            var warnings = json?["warnings"]?.AsArray().Select(w => w?.GetValue<string>() ?? "").Where(w => w.Length > 0).ToArray() ?? Array.Empty<string>();
            return new Status(fresh, warnings);
        }
        catch { return null; }
    }
}
