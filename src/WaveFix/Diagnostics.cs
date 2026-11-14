using System.Diagnostics.Eventing.Reader;
using System.IO;
using System.Text;
using Microsoft.Win32;

namespace WaveFix;

/// <summary>
/// Everything needed to see why effects aren't reaching a mic, as text to paste into a message:
/// versions, the engine's registration, every active mic's effect settings, the config and status
/// files, the engine's own events from the Windows Event Log, and the last setup log. Read only.
/// </summary>
public static class Diagnostics
{
    public static string Collect(string? selectedMic)
    {
        var sb = new StringBuilder();
        void Line(string s = "") => sb.AppendLine(s);
        void Try(string title, Action a) { try { a(); } catch (Exception ex) { Line($"{title}: couldn't read ({ex.Message})"); } }

        Line($"WaveFix {Updater.Current} diagnostics, Windows {Environment.OSVersion.Version}, {DateTime.Now:yyyy-MM-dd HH:mm:ss}");
        Line($"Selected mic: {selectedMic ?? "(none)"}");
        Try("Engine", () =>
        {
            var dll = WaveFixEngine.RegisteredPath;
            Line($"Engine in this app: {(WaveFixEngine.Available ? "yes" : "NO")}; installed: {WaveFixEngine.IsInstalled}; needs update: {WaveFixEngine.NeedsUpdate}");
            Line($"Engine DLL: {dll ?? "(not registered)"} (exists: {(dll != null && File.Exists(dll))})");
            Line($"Noise removal plugin: {WaveFixEngine.HasNoisePlugin}; Equalizer APO installed: {EqualizerApo.Installed}");
            Line($"Loaded in Windows audio: {LoadedInAudio()}");
        });

        Line();
        Line("Active mics (effect settings):");
        Try("Mics", () =>
        {
            using var hklm = RegistryKey.OpenBaseKey(RegistryHive.LocalMachine, RegistryView.Registry64);
            using var capture = hklm.OpenSubKey(@"SOFTWARE\Microsoft\Windows\CurrentVersion\MMDevices\Audio\Capture");
            foreach (var id in capture?.GetSubKeyNames() ?? Array.Empty<string>())
            {
                using var dev = capture!.OpenSubKey(id);
                if (dev?.GetValue("DeviceState") is not int state || state != 1) continue;
                using var props = dev.OpenSubKey("Properties");
                var name = props?.GetValue("{a45c254e-df1c-4efd-8020-67d146a850e0},2") as string ?? "?";
                bool selected = selectedMic != null && selectedMic.EndsWith(id, StringComparison.OrdinalIgnoreCase);
                Line($"  {(selected ? "*" : " ")} {name} {id}{(EqualizerApo.IsOnMic("x." + id) ? "  [Equalizer APO on it]" : "")}{(WaveFixEngine.IsAttached(id) ? "  [WaveFix engine on it]" : "")}");
                using var fx = dev.OpenSubKey("FxProperties");
                foreach (var v in fx?.GetValueNames() ?? Array.Empty<string>())
                    if (v.StartsWith("{d04e05a6", StringComparison.OrdinalIgnoreCase) || v.StartsWith("{d3993a3f", StringComparison.OrdinalIgnoreCase))
                        Line($"      {v} = {(fx!.GetValue(v) is string[] a ? string.Join(", ", a) : fx!.GetValue(v))}");
            }
        });

        Line();
        Try("Files", () =>
        {
            foreach (var folder in new[] { "config", "status" })
            {
                var dir = Path.Combine(WaveFixEngine.DataDir, folder);
                Line($"{folder} folder: {(Directory.Exists(dir) ? "" : "(missing)")}");
                if (!Directory.Exists(dir)) continue;
                foreach (var f in Directory.EnumerateFiles(dir))
                {
                    Line($"  {Path.GetFileName(f)}  {File.GetLastWriteTime(f):yyyy-MM-dd HH:mm:ss}");
                    if (folder == "status" || (selectedMic != null && f == WaveFixEngine.ConfigPath(selectedMic)))
                        foreach (var l in File.ReadAllLines(f).Take(60)) Line("    " + l);
                }
            }
        });

        Line();
        Line("Engine events (newest first):");
        Try("Events", () =>
        {
            var query = new EventLogQuery("Application", PathType.LogName, "*[System/Provider[@Name='WaveFix Engine']]") { ReverseDirection = true };
            using var reader = new EventLogReader(query);
            int n = 0;
            for (var e = reader.ReadEvent(); e != null && n < 80; e = reader.ReadEvent(), n++)
                using (e) Line($"  {e.TimeCreated:MM-dd HH:mm:ss} {LevelName(e.Level)} {string.Join(" ", e.Properties.Select(p => p.Value))}");
            if (n == 0) Line("  (none: Windows hasn't started the engine since it was installed)");
        });

        Line();
        Line("Last setup log:");
        Try("Setup log", () => Line(File.Exists(Setup.LastLogPath) ? File.ReadAllText(Setup.LastLogPath).TrimEnd() : "(none)"));
        return sb.ToString();
    }

    static string LevelName(byte? level) => level switch { 2 => "ERROR", 3 => "WARN ", _ => "info " };

    /// <summary>Whether Windows' audio service has the engine loaded (needs admin to see; "unknown" otherwise).</summary>
    static string LoadedInAudio()
    {
        try
        {
            foreach (var p in System.Diagnostics.Process.GetProcessesByName("audiodg"))
                using (p)
                    foreach (System.Diagnostics.ProcessModule m in p.Modules)
                        if (m.ModuleName.StartsWith("wavefix-engine", StringComparison.OrdinalIgnoreCase)) return "yes";
            return "no";
        }
        catch { return "unknown (needs admin to check)"; }
    }
}
