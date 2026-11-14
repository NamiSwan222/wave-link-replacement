using System.Diagnostics;
using System.IO;
using Microsoft.Win32;

namespace WaveFix;

/// <summary>
/// Finds and removes the Equalizer APO setup that WaveFix used before it had its own engine
/// (versions up to 0.8.0). Nothing else in WaveFix uses Equalizer APO any more.
/// </summary>
public static class EqualizerApo
{
    const string EapoEfx = "{EC1CC9CE-FAED-4822-828A-82A81A6F018F}";
    const string EapoSfx = "{EACD2258-FCAC-4FF4-B36D-419E924A6D79}";
    const string FxClsid = "{d04e05a6-594b-4fb6-a80d-01af5eed7d1d}";
    const string FxModes = "{d3993a3f-99c2-4402-b5ec-a92a0367664b}";
    static readonly int[] Slots = { 1, 2, 5, 6, 7 };

    static RegistryKey Hklm => RegistryKey.OpenBaseKey(RegistryHive.LocalMachine, RegistryView.Registry64);

    public static string? InstallDir
    {
        get
        {
            try { if (Hklm.OpenSubKey(@"SOFTWARE\EqualizerAPO")?.GetValue("InstallPath") is string p && p.Length > 0) return p; } catch { }
            var guess = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "EqualizerAPO");
            return Directory.Exists(guess) ? guess : null;
        }
    }

    public static bool Installed => InstallDir is { } d && File.Exists(Path.Combine(d, "EqualizerAPO.dll"));

    /// <summary>The RNNoise plugin WaveFix put in Equalizer APO's folder (reused when switching).</summary>
    public static string? NoisePlugin => InstallDir is { } d ? Path.Combine(d, "VSTPlugins", "rnnoise", "rnnoise_mono.dll") : null;

    static bool IsEapo(object? v) => v switch
    {
        string s => s.Trim().Equals(EapoEfx, StringComparison.OrdinalIgnoreCase) || s.Trim().Equals(EapoSfx, StringComparison.OrdinalIgnoreCase),
        string[] list => list.Any(IsEapo),
        _ => false,
    };

    static bool OnFx(RegistryKey? fx) =>
        fx != null && (Slots.Any(i => IsEapo(fx.GetValue($"{FxClsid},{i}"))) || new[] { 13, 14, 15 }.Any(i => IsEapo(fx.GetValue($"{FxClsid},{i}"))));

    static string FxPath(string flow, string guid) => $@"SOFTWARE\Microsoft\Windows\CurrentVersion\MMDevices\Audio\{flow}\{{{guid.Trim('{', '}')}}}\FxProperties";
    static string Guid(string endpointIdOrGuid) => endpointIdOrGuid[(endpointIdOrGuid.LastIndexOf('.') + 1)..].Trim('{', '}');

    /// <summary>Equalizer APO is in this mic's effects.</summary>
    public static bool IsOnMic(string endpointId)
    {
        try { using var fx = Hklm.OpenSubKey(FxPath("Capture", Guid(endpointId))); return OnFx(fx); }
        catch { return false; }
    }

    /// <summary>
    /// Equalizer APO is on some device other than this mic (e.g. someone's speaker EQ): then it's
    /// only taken off the mic, not uninstalled, so those devices keep working.
    /// </summary>
    public static bool UsedElsewhere(string micGuid)
    {
        var mic = Guid(micGuid);
        foreach (var flow in new[] { "Capture", "Render" })
        {
            try
            {
                using var devices = Hklm.OpenSubKey($@"SOFTWARE\Microsoft\Windows\CurrentVersion\MMDevices\Audio\{flow}");
                if (devices == null) continue;
                foreach (var name in devices.GetSubKeyNames())
                {
                    if (flow == "Capture" && name.Trim('{', '}').Equals(mic, StringComparison.OrdinalIgnoreCase)) continue;
                    using var fx = devices.OpenSubKey(name + @"\FxProperties");
                    if (OnFx(fx)) return true;
                }
            }
            catch { }
        }
        return false;
    }

    /// <summary>Equalizer APO's record of the mic's original effects (written when it was attached).</summary>
    static RegistryKey? ChildBackup(string guid) => Hklm.OpenSubKey($@"SOFTWARE\EqualizerAPO\Child APOs\{{{Guid(guid)}}}");

    /// <summary>
    /// The mic's original endpoint effect before Equalizer APO, for WaveFix's own undo backup
    /// (null when there was none).
    /// </summary>
    public static string? OriginalEndpointEffect(string guid)
    {
        using var child = ChildBackup(guid);
        return child?.GetValue($"{FxClsid},7") is string v && v != "!VALUE" && !IsEapo(v) ? v : null;
    }

    /// <summary>
    /// SYSTEM step: takes Equalizer APO off the mic, putting back the original effects from its own
    /// backup ("!VALUE" means there was none). Without a backup, its entries are just removed.
    /// </summary>
    public static void RemoveFromMic(RegistryKey fx, string guid)
    {
        using var child = ChildBackup(guid);
        RemoveFromMic(fx, child);
    }

    /// <summary>Same, with Equalizer APO's record passed in (tests use a scratch key).</summary>
    public static void RemoveFromMic(RegistryKey fx, RegistryKey? child)
    {
        foreach (var i in Slots)
        {
            var name = $"{FxClsid},{i}";
            var original = child?.GetValue(name) as string;
            if (child != null && original != null)
            {
                if (original == "!VALUE" || IsEapo(original)) fx.DeleteValue(name, false);
                else fx.SetValue(name, original, RegistryValueKind.String);
            }
            else if (IsEapo(fx.GetValue(name))) fx.DeleteValue(name, false);
        }
        foreach (var i in new[] { 13, 14, 15 })
        {
            var name = $"{FxClsid},{i}";
            if (fx.GetValue(name) is string[] list && list.Any(IsEapo))
            {
                var rest = list.Where(c => !IsEapo(c)).ToArray();
                if (rest.Length > 0) fx.SetValue(name, rest, RegistryValueKind.MultiString);
                else fx.DeleteValue(name, false);
            }
        }
        // WaveFix set the stream slot's modes when it used the stream slot; nothing uses them now.
        if (fx.GetValue($"{FxClsid},5") == null) fx.DeleteValue($"{FxModes},5", false);
    }

    /// <summary>Admin helper: runs Equalizer APO's own uninstaller silently and waits for it.</summary>
    public static void Uninstall(Action<string> log)
    {
        var dir = InstallDir;
        string? uninstaller = null;
        try
        {
            using var k = Hklm.OpenSubKey(@"SOFTWARE\Microsoft\Windows\CurrentVersion\Uninstall\EqualizerAPO");
            uninstaller = (k?.GetValue("UninstallString") as string)?.Trim('"');
        }
        catch { }
        if (uninstaller == null && dir != null) uninstaller = Path.Combine(dir, "Uninstall.exe");
        if (uninstaller == null || !File.Exists(uninstaller)) { log("Equalizer APO's uninstaller wasn't found"); return; }
        // NSIS: /S = silent; _?= runs in place so we can wait for it to finish.
        var args = dir != null ? $"/S _?={dir}" : "/S";
        using var p = Process.Start(new ProcessStartInfo(uninstaller, args) { UseShellExecute = false, CreateNoWindow = true });
        if (p == null || !p.WaitForExit(180_000)) { log("Equalizer APO's uninstaller didn't finish in time"); return; }
        log($"Equalizer APO uninstaller exit code {p.ExitCode}");
        // Running in place leaves the uninstaller and any files Windows audio still had open.
        // Only ever a folder that is really Equalizer APO's (the path comes from the registry).
        if (dir != null && Path.GetFileName(Path.TrimEndingDirectorySeparator(dir)).Equals("EqualizerAPO", StringComparison.OrdinalIgnoreCase)
            && Path.GetDirectoryName(Path.TrimEndingDirectorySeparator(dir)) is { Length: > 3 })
            try { Directory.Delete(dir, true); } catch (Exception ex) { log($"Some Equalizer APO files stay until restart: {ex.Message}"); }
    }
}
