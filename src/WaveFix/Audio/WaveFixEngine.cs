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
/// mic chain inside audiodg.exe.
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
        RegisterEventSource();
        // Older engine versions that nothing loads any more.
        foreach (var old in Directory.EnumerateFiles(EngineDir, FilePrefix + "*.dll"))
            if (!string.Equals(old, dll, StringComparison.OrdinalIgnoreCase)) try { File.Delete(old); } catch { }
    }

    /// <summary>
    /// The engine writes diagnostics to the Application event log as "WaveFix Engine". .NET's
    /// message file shows each event's text as is.
    /// </summary>
    static void RegisterEventSource()
    {
        try
        {
            using var k = Hklm.CreateSubKey(@"SYSTEM\CurrentControlSet\Services\EventLog\Application\WaveFix Engine");
            k.SetValue("EventMessageFile", @"%SystemRoot%\Microsoft.NET\Framework64\v4.0.30319\EventLogMessages.dll", RegistryValueKind.ExpandString);
            k.SetValue("TypesSupported", 7, RegistryValueKind.DWord);
        }
        catch { /* events still get written, just without a pretty message */ }
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

    /// <summary>
    /// A real effect class ID. Equalizer APO's backups use markers like "!VALUE" and "!KEY" for
    /// "nothing was here"; if one of those ends up in a mic's settings, Windows can't load the
    /// mic's effects at all (ours included). The all-zero GUID also means "none".
    /// </summary>
    public static bool IsRealEffect(string? clsid) =>
        clsid != null && Guid.TryParse(clsid.Trim(), out var g) && g != Guid.Empty;

    /// <summary>The effect slots (single and composite) that hold effect class IDs.</summary>
    static readonly int[] SingleSlots = { 1, 2, 5, 6, 7 }, CompositeSlots = { 13, 14, 15 };

    /// <summary>This mic's effect settings contain something that isn't an effect (see IsRealEffect).</summary>
    public static bool HasJunk(string endpointId)
    {
        try
        {
            using var k = Hklm.OpenSubKey(FxPath(Name(endpointId)));
            if (k == null) return false;
            return SingleSlots.Any(i => k.GetValue($"{FxClsid},{i}") is string s && !IsRealEffect(s) && !IsNone(s))
                || CompositeSlots.Any(i => k.GetValue($"{FxClsid},{i}") is string[] l && l.Any(c => !IsRealEffect(c)));
        }
        catch { return false; }
    }

    /// <summary>An explicit "no effect" (all-zero GUID) in a single slot: valid, nothing to clean.</summary>
    static bool IsNone(string s) => Guid.TryParse(s.Trim(), out var g) && g == Guid.Empty;

    /// <summary>SYSTEM step: removes entries that aren't effects from a mic's effect settings.</summary>
    public static void RemoveJunk(RegistryKey fx)
    {
        foreach (var i in SingleSlots)
            if (fx.GetValue($"{FxClsid},{i}") is string s && !IsRealEffect(s) && !IsNone(s)) fx.DeleteValue($"{FxClsid},{i}", false);
        foreach (var i in CompositeSlots)
            if (fx.GetValue($"{FxClsid},{i}") is string[] l && l.Any(c => !IsRealEffect(c)))
            {
                var rest = l.Where(IsRealEffect).ToArray();
                if (rest.Length > 0) fx.SetValue($"{FxClsid},{i}", rest, RegistryValueKind.MultiString);
                else fx.DeleteValue($"{FxClsid},{i}", false);
            }
    }

    /// <summary>
    /// Saves the mic's original effect settings before the first change (admin helper). When the
    /// mic still has Equalizer APO on it, the original endpoint effect comes from Equalizer APO's
    /// own record, so undo never puts Equalizer APO back.
    /// </summary>
    public static void BackupMic(string guid, bool fromEqualizerApo = false)
    {
        using var existing = Hklm.OpenSubKey($@"{BackupKey}\{Name(guid)}");
        if (existing != null) return;
        using var fx = Hklm.OpenSubKey(FxPath(guid));
        using var key = Hklm.CreateSubKey($@"{BackupKey}\{Name(guid)}");
        foreach (var name in new[] { $"{FxClsid},{Efx}", $"{FxClsid},{CompositeEfx}", $"{FxModes},{Efx}" })
        {
            var v = fx?.GetValue(name);
            if (fromEqualizerApo && name == $"{FxClsid},{Efx}") v = EqualizerApo.OriginalEndpointEffect(guid);
            if (fromEqualizerApo && v is string[] list) v = list.Where(c => !c.Trim().Equals("{EC1CC9CE-FAED-4822-828A-82A81A6F018F}", StringComparison.OrdinalIgnoreCase)).ToArray() is { Length: > 0 } rest ? rest : null;
            // Never save junk (e.g. Equalizer APO's "!KEY" marker) as an "original" effect.
            if (name != $"{FxModes},{Efx}")
            {
                if (v is string one && !IsRealEffect(one)) v = null;
                if (v is string[] many) v = many.Where(IsRealEffect).ToArray() is { Length: > 0 } real ? real : null;
            }
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
        RemoveJunk(fx);
        var composite = fx.GetValue($"{FxClsid},{CompositeEfx}") as string[];
        var single = fx.GetValue($"{FxClsid},{Efx}") as string;
        var list = new List<string>();
        if (composite != null) list.AddRange(composite.Where(c => !string.IsNullOrWhiteSpace(c)));
        else if (IsRealEffect(single)) list.Add(single!);
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
            bool effect = name != $"{FxModes},{Efx}";
            if (v is string s && (!effect || IsRealEffect(s))) fx.SetValue(name, s, RegistryValueKind.String);
            else if (v is string[] m && (!effect || m.Any(IsRealEffect))) fx.SetValue(name, effect ? m.Where(IsRealEffect).ToArray() : m, RegistryValueKind.MultiString);
            else fx.DeleteValue(name, false);
        }
        RemoveJunk(fx);
    }

    /// <summary>Every mic (capture endpoint GUID, with braces) that has the engine in its effects.</summary>
    public static List<string> AttachedMics()
    {
        var list = new List<string>();
        try
        {
            using var capture = Hklm.OpenSubKey(@"SOFTWARE\Microsoft\Windows\CurrentVersion\MMDevices\Audio\Capture");
            foreach (var id in capture?.GetSubKeyNames() ?? Array.Empty<string>())
                if (IsAttached(id)) list.Add(id);
        }
        catch { }
        return list;
    }

    /// <summary>Removes the engine's registration, event source and mic backups (admin helper).</summary>
    public static void Unregister(Action<string> log)
    {
        foreach (var key in new[]
        {
            $@"SOFTWARE\Classes\CLSID\{Clsid}",
            $@"SOFTWARE\Classes\AudioEngine\AudioProcessingObjects\{Clsid}",
            @"SYSTEM\CurrentControlSet\Services\EventLog\Application\WaveFix Engine",
            @"SOFTWARE\WaveFix",
        })
        {
            try { Hklm.DeleteSubKeyTree(key, false); log($"removed {key}"); }
            catch (Exception ex) { log($"couldn't remove {key}: {ex.Message}"); }
        }
    }

    [System.Runtime.InteropServices.DllImport("kernel32", CharSet = System.Runtime.InteropServices.CharSet.Unicode, SetLastError = true)]
    static extern bool MoveFileEx(string existing, string? replacement, int flags);
    const int MOVEFILE_DELAY_UNTIL_REBOOT = 4;

    /// <summary>
    /// Deletes Program Files\WaveFix and WaveFix's data in ProgramData. Files Windows audio still
    /// has open are deleted at the next restart instead.
    /// </summary>
    public static void DeleteFiles(Action<string> log)
    {
        var data = Path.GetDirectoryName(DataDir)!; // ProgramData\WaveFix
        foreach (var dir in new[] { InstallDir, data })
        {
            if (!Directory.Exists(dir)) continue;
            // Only ever WaveFix's own folders.
            if (!Path.GetFileName(Path.TrimEndingDirectorySeparator(dir)).Equals("WaveFix", StringComparison.OrdinalIgnoreCase)) continue;
            try { Directory.Delete(dir, true); log($"deleted {dir}"); continue; }
            catch (Exception ex) { log($"{dir}: {ex.Message}; the rest is deleted at the next restart"); }
            foreach (var f in Directory.EnumerateFiles(dir, "*", SearchOption.AllDirectories))
                try { File.Delete(f); } catch { MoveFileEx(f, null, MOVEFILE_DELAY_UNTIL_REBOOT); }
            foreach (var d in Directory.EnumerateDirectories(dir, "*", SearchOption.AllDirectories).OrderByDescending(d => d.Length).Append(dir))
                try { Directory.Delete(d); } catch { MoveFileEx(d, null, MOVEFILE_DELAY_UNTIL_REBOOT); }
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
        static JsonObject Params(Dictionary<string, float> p) { var o = new JsonObject(); foreach (var (k, v) in p) if (float.IsFinite(v)) o[k] = v; return o; }
        JsonObject Vst(string path, PluginState? state)
        {
            var o = new JsonObject { ["type"] = "vst", ["path"] = path };
            if (!string.IsNullOrEmpty(state?.ChunkData)) o["chunk"] = state.ChunkData;
            else if (state?.Params is { Count: > 0 } p) o["params"] = Params(p);
            return o;
        }

        // Rumble filter, then noise removal on the cleanest signal, tone, the user's plugins, voice
        // polish and voice changer, boost, then soundboard and peak protection.
        if (s.LowCut) Filter("hp", 80);
        // Strong noise removal needs the current engine (an older one wouldn't know the stage and
        // would skip the whole config): until it's updated, the standard one runs instead.
        bool strong = s.NoiseModel == "strong" && !NeedsUpdate;
        if (s.NoiseRemoval && strong) stages.Add(new JsonObject { ["type"] = "denoise", ["limit"] = Math.Round(Math.Clamp(s.NoiseStrengthDb, 6, 100)) });
        else if (s.NoiseRemoval && HasNoisePlugin) stages.Add(Vst(NoisePlugin, s.NoiseState));
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
        bool ai = s.AiVoice.Enabled && s.AiVoice.Current != null && Rvc.RvcEngine.Active;
        if (VoiceFx.MainParams(s.Polish, s.Voice, ai, s.AiVoice.SilentWhenUnavailable) is { } main) stages.Add(new JsonObject { ["type"] = "fx", ["params"] = Params(main) });
        if (Math.Abs(s.VoiceBoostDb) > 0.05) stages.Add(new JsonObject { ["type"] = "gain", ["db"] = Math.Round(s.VoiceBoostDb, 1) });
        if (VoiceFx.OutputParams(s.Polish, s.Soundboard) is { } output) stages.Add(new JsonObject { ["type"] = "fx", ["params"] = Params(output) });

        return new JsonObject { ["stages"] = stages }.ToJsonString(new JsonSerializerOptions { WriteIndented = true });
    }

    /// <summary>The 1-based stage number of a plugin in the engine config for these settings, or 0.</summary>
    public static int StageOf(AppSettings s, string pluginPath)
    {
        try
        {
            var stages = JsonNode.Parse(BuildConfig(s))?["stages"]?.AsArray();
            for (int i = 0; i < (stages?.Count ?? 0); i++)
                if (stages![i]?["type"]?.GetValue<string>() == "vst" &&
                    string.Equals(stages[i]?["path"]?.GetValue<string>(), pluginPath, StringComparison.OrdinalIgnoreCase)) return i + 1;
        }
        catch { }
        return 0;
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

    public sealed record Status(bool Running, string[] Warnings, long Time = 0, int Channels = 0, double Rate = 0);

    /// <summary>What the engine last reported for this mic (it reports while the mic is in use).</summary>
    public static Status? ReadStatus(string endpointId)
    {
        try
        {
            var json = JsonNode.Parse(File.ReadAllText(StatusPath(endpointId)));
            var time = json?["time"]?.GetValue<long>() ?? 0;
            bool fresh = DateTimeOffset.UtcNow.ToUnixTimeSeconds() - time < 15;
            var warnings = json?["warnings"]?.AsArray().Select(w => w?.GetValue<string>() ?? "").Where(w => w.Length > 0).ToArray() ?? Array.Empty<string>();
            return new Status(fresh, warnings, time, json?["channels"]?.GetValue<int>() ?? 0, json?["rate"]?.GetValue<double>() ?? 0);
        }
        catch { return null; }
    }
}
