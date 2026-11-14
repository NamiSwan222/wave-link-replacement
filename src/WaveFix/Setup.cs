using System.Diagnostics;
using System.IO;
using System.IO.Compression;
using System.Net.Http;
using System.Security.AccessControl;
using System.Security.Cryptography;
using System.Security.Principal;
using System.ServiceProcess;
using Microsoft.Win32;

namespace WaveFix;

/// <summary>
/// One-click first-time setup. The same WaveFix.exe runs in three roles:
///   normal app    → shows progress, starts the admin helper with one UAC prompt
///   --setup       → admin helper: installs Equalizer APO + RNNoise, attaches, restarts audio
///   --system-attach → runs briefly as SYSTEM (the owner of the mic's effect settings) to write them
/// Progress goes to a log file as lines like "STEP 2 DONE message", which the app reads.
/// Every download is checked against a pinned SHA-256 before it's used.
/// </summary>
/// <summary>One row of the setup checklist.</summary>
public sealed class SetupStepRow : Observable
{
    public string Title { get; }
    public SetupStepRow(string title) => Title = title;

    string _state = "WAIT";
    public string State
    {
        get => _state;
        set { if (Set(ref _state, value)) { Raise(nameof(Glyph)); Raise(nameof(Color)); } }
    }

    string _message = "";
    public string Message { get => _message; set => Set(ref _message, value); }

    public string Glyph => _state switch { "RUN" => "", "DONE" or "SKIP" => "", "FAIL" => "", _ => "" };
    public string Color => _state switch { "RUN" => "#A5B4FC", "DONE" or "SKIP" => "#22C55E", "FAIL" => "#EF4444", _ => "#64748B" };
}

public static class Setup
{
    // Pinned downloads. To update a version, change the URL and its SHA-256 together.
    const string EapoUrl = "https://sourceforge.net/projects/equalizerapo/files/1.4.2/EqualizerAPO-x64-1.4.2.exe/download";
    const string EapoSha256 = "7403BE7427BBE1936A40DDED082829B6E217FC4F5990FEE5CBA501F0AE055AFA";
    const string RnnoiseUrl = "https://github.com/werman/noise-suppression-for-voice/releases/download/v1.10/win-rnnoise.zip";
    const string RnnoiseSha256 = "E4DCC186102850F8DCEBEB766AD1BF34C24F6280BD614EF84B72DAE4783FC517";
    const string RnnoiseEntry = "win-rnnoise/vst/rnnoise_mono.dll";
    const string RnnoiseDllSha256 = "7DEBDFC4843461C47B23AC7A736B4663FE581643563AE764CDE395E006004080";
    public const string RnnoiseReleasePage = "https://github.com/werman/noise-suppression-for-voice/releases/tag/v1.10";

    /// <summary>The last setup run's log, kept so it can be copied and sent for help.</summary>
    public static string LastLogPath => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "WaveFix", "setup.log");

    const string EapoEfx = "{EC1CC9CE-FAED-4822-828A-82A81A6F018F}";
    const string EapoSfx = "{EACD2258-FCAC-4FF4-B36D-419E924A6D79}";
    const string ModeDefault = "{C18E2F7E-933D-4965-B7D1-1EEF228D2AF3}";
    const string FxClsid = "{d04e05a6-594b-4fb6-a80d-01af5eed7d1d}";   // PKEY_FX_*EffectClsid (,1 ,2 ,5 ,6 ,7)
    const string FxModes = "{d3993a3f-99c2-4402-b5ec-a92a0367664b}";   // PKEY_*_ProcessingModes_Supported_For_Streaming
    const string TaskName = "WaveFix Setup";

    public static readonly string[] StepTitles =
    {
        "Install Equalizer APO",
        "Add the noise removal plugin",
        "Connect the effects to your mic",
        "Restart Windows audio",
    };

    static string WorkDir => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData), "WaveFix", "Setup");
    static string EapoDir
    {
        get
        {
            try { if (Registry.LocalMachine.OpenSubKey(@"SOFTWARE\EqualizerAPO")?.GetValue("InstallPath") is string p && p.Length > 0) return p; } catch { }
            return Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "EqualizerAPO");
        }
    }
    static string NoisePlugin => Path.Combine(EapoDir, "VSTPlugins", "rnnoise", "rnnoise_mono.dll");

    public static string GuidOf(string endpointId) => endpointId[(endpointId.LastIndexOf('.') + 1)..];

    /// <summary>What still needs doing for this mic (for the checklist and the status banner).</summary>
    public static bool[] DoneSteps(string endpointId) => new[]
    {
        File.Exists(Path.Combine(EapoDir, "EqualizerAPO.dll")),
        File.Exists(NoisePlugin),
        EffectsEngine.GetSlot(endpointId) == EffectsEngine.SlotState.Endpoint,
        false,
    };

    public static bool IsComplete(string endpointId) => DoneSteps(endpointId).Take(3).All(d => d);

    // ======================= Normal app: start the helper =======================

    /// <summary>Starts the admin helper (one UAC prompt) and reports "STEP n STATE message" lines.</summary>
    public static async Task<bool> RunAsync(string endpointId, IProgress<(int step, string state, string message)> progress, string? localPlugin = null, CancellationToken ct = default)
    {
        var log = LastLogPath;
        Directory.CreateDirectory(Path.GetDirectoryName(log)!);
        File.WriteAllText(log, "");
        var args = $"--setup {GuidOf(endpointId)} \"{log}\"" + (localPlugin != null ? $" \"{localPlugin}\"" : "");
        Process? p;
        try
        {
            p = Process.Start(new ProcessStartInfo(Environment.ProcessPath!, args)
            { UseShellExecute = true, Verb = "runas", WindowStyle = ProcessWindowStyle.Hidden });
        }
        catch (System.ComponentModel.Win32Exception) // UAC "No"
        {
            progress.Report((0, "FAIL", "Windows permission was declined, so nothing was changed. Click Set it up for me to try again."));
            return false;
        }
        if (p == null) return false;

        int seen = 0; bool ok = false;
        while (true)
        {
            bool exited = p.HasExited;
            string[] lines;
            try { using var fs = new FileStream(log, FileMode.Open, FileAccess.Read, FileShare.ReadWrite); lines = new StreamReader(fs).ReadToEnd().Split('\n', StringSplitOptions.RemoveEmptyEntries); }
            catch { lines = Array.Empty<string>(); }
            for (; seen < lines.Length; seen++)
            {
                var parts = lines[seen].Trim().Split(' ', 4);
                if (parts.Length >= 3 && parts[0] == "STEP" && int.TryParse(parts[1], out var n))
                    progress.Report((n, parts[2], parts.Length > 3 ? parts[3] : ""));
                if (lines[seen].StartsWith("RESULT OK")) ok = true;
            }
            if (exited) break;
            await Task.Delay(400, ct);
        }
        return ok; // the log stays at LastLogPath for "Copy details"
    }

    // ======================= Admin helper (--setup) =======================

    static StreamWriter? _log;
    static void Log(string line) { _log?.WriteLine(line); _log?.Flush(); }
    static void Step(int n, string state, string msg = "") => Log($"STEP {n} {state} {msg}".TrimEnd());

    public static int RunElevated(string micGuid, string logPath, string? localPlugin = null)
    {
        using var w = new StreamWriter(new FileStream(logPath, FileMode.Append, FileAccess.Write, FileShare.ReadWrite));
        _log = w;
        try
        {
            if (!new WindowsPrincipal(WindowsIdentity.GetCurrent()).IsInRole(WindowsBuiltInRole.Administrator))
                throw new InvalidOperationException("The setup helper needs administrator rights.");
            Directory.CreateDirectory(WorkDir);
            Log($"LOG WaveFix {Updater.Current} setup, Windows {Environment.OSVersion.Version}, mic {micGuid}, local plugin: {localPlugin ?? "none"}");
            using var http = new HttpClient { Timeout = Timeout.InfiniteTimeSpan }; // our own watchdogs handle stalls
            http.DefaultRequestHeaders.UserAgent.ParseAdd($"WaveFix-Setup/{Updater.Current}");
            bool changed = false;

            // 1. Equalizer APO
            if (File.Exists(Path.Combine(EapoDir, "EqualizerAPO.dll"))) Step(0, "SKIP", "Already installed");
            else
            {
                Step(0, "RUN", "Downloading Equalizer APO…");
                var exe = Download(http, EapoUrl, EapoSha256, "EqualizerAPO-x64-1.4.2.exe", 0, "Downloading Equalizer APO");
                Step(0, "RUN", "Installing Equalizer APO…");
                using (var inst = Process.Start(new ProcessStartInfo(exe, "/S") { UseShellExecute = false }))
                {
                    if (inst == null) throw new InvalidOperationException("Equalizer APO's installer didn't start.");
                    // Its installer may open Device Selector at the end and wait for it. WaveFix picks
                    // the device itself, so close that window whenever it shows up.
                    var until = DateTime.Now.AddMinutes(5);
                    while (!inst.WaitForExit(1000))
                    {
                        foreach (var ds in Process.GetProcessesByName("DeviceSelector")) try { ds.Kill(); } catch { }
                        if (DateTime.Now > until) throw new InvalidOperationException("Equalizer APO's installer didn't finish.");
                    }
                }
                foreach (var ds in Process.GetProcessesByName("DeviceSelector")) try { ds.Kill(); } catch { }
                if (!File.Exists(Path.Combine(EapoDir, "EqualizerAPO.dll"))) throw new InvalidOperationException("Equalizer APO didn't install.");
                Step(0, "DONE", "Installed");
                changed = true;
            }

            // 2. RNNoise plugin
            if (File.Exists(NoisePlugin)) Step(1, "SKIP", "Already there");
            else
            {
                if (localPlugin != null)
                {
                    Step(1, "RUN", "Checking the file you picked…");
                    InstallNoisePlugin(localPlugin);
                }
                else
                {
                    Step(1, "RUN", "Downloading the noise removal plugin…");
                    InstallNoisePlugin(Download(http, RnnoiseUrl, RnnoiseSha256, "win-rnnoise.zip", 1, "Downloading the noise removal plugin"));
                }
                Step(1, "DONE", "Added");
                changed = true;
            }

            // 3. Attach to the mic's endpoint slot
            var guid = micGuid.Trim('{', '}');
            if (EffectsEngine.GetSlot("x." + "{" + guid + "}") == EffectsEngine.SlotState.Endpoint) Step(2, "SKIP", "Already connected");
            else
            {
                Step(2, "RUN", "Connecting the effects to your mic…");
                WriteChildBackup("{" + guid + "}");
                RunAsSystem($"--system-attach {{{guid}}} \"{logPath}\"");
                if (EffectsEngine.GetSlot("x.{" + guid + "}") != EffectsEngine.SlotState.Endpoint)
                    throw new InvalidOperationException("Windows didn't accept the mic setting.");
                Step(2, "DONE", "Connected");
                changed = true;
            }

            // 4. Restart Windows Audio (only if something changed)
            if (!changed) Step(3, "SKIP", "Not needed");
            else
            {
                Step(3, "RUN", "Restarting Windows audio (sound drops for a few seconds)…");
                RestartAudio();
                Step(3, "DONE", "Audio is back");
            }
            Log("RESULT OK");
            return 0;
        }
        catch (Exception ex)
        {
            Log($"LOG {ex}");
            Log($"STEP -1 FAIL {ex.Message}");
            Log("RESULT FAIL");
            return 1;
        }
        finally { _log = null; try { Directory.Delete(WorkDir, true); } catch { } }
    }

    /// <summary>
    /// Downloads with live progress, a stall watchdog (no data for 30 s = drop and retry) and up to
    /// three tries, then checks the SHA-256. HttpClient.Timeout doesn't cover reading the body once
    /// headers arrived, so without the watchdog a stalled connection would hang setup forever.
    /// </summary>
    static string Download(HttpClient http, string url, string sha256, string fileName, int step, string label)
    {
        var path = Path.Combine(WorkDir, fileName); // admin-only folder: can't be swapped by other users
        Exception? last = null;
        for (int attempt = 1; attempt <= 3; attempt++)
        {
            try
            {
                Task.Run(() => DownloadOnce(http, url, path, step, label, attempt)).GetAwaiter().GetResult();
                last = null;
                break;
            }
            catch (Exception ex)
            {
                last = ex;
                Log($"LOG download try {attempt} failed: {ex.GetType().Name}: {ex.Message}");
                if (attempt < 3) { Step(step, "RUN", $"Connection trouble, trying again ({attempt + 1} of 3)…"); Thread.Sleep(2000 * attempt); }
            }
        }
        if (last != null)
            throw new InvalidOperationException($"Couldn't download {label.ToLowerInvariant()}: {Friendly(last)}");

        string actual;
        using (var fs = File.OpenRead(path)) actual = Convert.ToHexString(SHA256.HashData(fs));
        if (!actual.Equals(sha256, StringComparison.OrdinalIgnoreCase))
        {
            File.Delete(path);
            throw new InvalidOperationException($"{fileName} didn't match its expected fingerprint, so it wasn't used.");
        }
        return path;
    }

    static async Task DownloadOnce(HttpClient http, string url, string path, int step, string label, int attempt)
    {
        using var overall = new CancellationTokenSource(TimeSpan.FromMinutes(15));
        using var stall = CancellationTokenSource.CreateLinkedTokenSource(overall.Token);
        stall.CancelAfter(TimeSpan.FromSeconds(30));
        using var resp = await http.GetAsync(url, HttpCompletionOption.ResponseHeadersRead, stall.Token);
        resp.EnsureSuccessStatusCode();
        long total = resp.Content.Headers.ContentLength ?? 0;
        await using var src = await resp.Content.ReadAsStreamAsync(stall.Token);
        await using var dst = File.Create(path);
        var buf = new byte[1 << 16];
        long done = 0; var lastReport = DateTime.MinValue;
        string tryText = attempt > 1 ? $" (try {attempt} of 3)" : "";
        while (true)
        {
            stall.CancelAfter(TimeSpan.FromSeconds(30)); // reset the watchdog for every chunk
            int n;
            try { n = await src.ReadAsync(buf, stall.Token); }
            catch (OperationCanceledException) when (!overall.IsCancellationRequested)
            { throw new System.TimeoutException("the download stopped receiving data for 30 seconds"); }
            if (n == 0) break;
            await dst.WriteAsync(buf.AsMemory(0, n), overall.Token);
            done += n;
            if (DateTime.Now - lastReport > TimeSpan.FromMilliseconds(700))
            {
                lastReport = DateTime.Now;
                Step(step, "RUN", total > 0
                    ? $"{label}… {done / 1048576.0:0.0} of {total / 1048576.0:0.0} MB{tryText}"
                    : $"{label}… {done / 1048576.0:0.0} MB{tryText}");
            }
        }
        if (total > 0 && done != total) throw new IOException($"the download ended early ({done} of {total} bytes)");
    }

    static string Friendly(Exception ex) => ex switch
    {
        System.TimeoutException t => t.Message + ". Your internet connection, a VPN or antivirus may be interrupting it",
        HttpRequestException h when h.StatusCode != null => $"the server answered {(int)h.StatusCode} {h.StatusCode}",
        HttpRequestException => "couldn't reach the website. Check your internet connection, VPN or firewall",
        TaskCanceledException => "it took too long",
        _ => ex.Message,
    };

    /// <summary>
    /// Puts rnnoise_mono.dll in place from a zip or dll (downloaded or user-picked). Whatever the
    /// source, the DLL itself must match its pinned SHA-256.
    /// </summary>
    static void InstallNoisePlugin(string source)
    {
        var staged = Path.Combine(WorkDir, "rnnoise_mono.dll");
        if (source.EndsWith(".zip", StringComparison.OrdinalIgnoreCase))
        {
            var copy = Path.Combine(WorkDir, "picked.zip");
            if (!string.Equals(source, copy, StringComparison.OrdinalIgnoreCase)) File.Copy(source, copy, true);
            using var archive = ZipFile.OpenRead(copy);
            var entry = archive.GetEntry(RnnoiseEntry)
                        ?? archive.Entries.FirstOrDefault(e => e.FullName.Replace('\\', '/').EndsWith("/vst/rnnoise_mono.dll", StringComparison.OrdinalIgnoreCase))
                        ?? throw new InvalidOperationException("That zip doesn't contain the noise removal plugin (vst/rnnoise_mono.dll). Use win-rnnoise.zip from the RNNoise v1.10 release.");
            entry.ExtractToFile(staged, overwrite: true);
        }
        else File.Copy(source, staged, true); // copy first so the checked file is the one we install

        string actual;
        using (var fs = File.OpenRead(staged)) actual = Convert.ToHexString(SHA256.HashData(fs));
        if (!actual.Equals(RnnoiseDllSha256, StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException("That plugin file isn't the expected RNNoise v1.10 version, so it wasn't used.");

        Directory.CreateDirectory(Path.GetDirectoryName(NoisePlugin)!);
        try { File.Copy(staged, NoisePlugin, overwrite: true); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        { throw new InvalidOperationException($"Couldn't copy the plugin into Equalizer APO's folder ({ex.Message}). Antivirus may be blocking it."); }
    }

    /// <summary>
    /// Saves the mic's original effect settings where Equalizer APO keeps them (so it can chain the
    /// original effects and restore them on uninstall), in the same format its Device Selector uses.
    /// </summary>
    static void WriteChildBackup(string guid)
    {
        using var existing = Registry.LocalMachine.OpenSubKey($@"SOFTWARE\EqualizerAPO\Child APOs\{guid}");
        if (existing != null) return; // already backed up (by Device Selector or an earlier setup)
        using var fx = Registry.LocalMachine.OpenSubKey($@"SOFTWARE\Microsoft\Windows\CurrentVersion\MMDevices\Audio\Capture\{guid}\FxProperties");
        using var key = Registry.LocalMachine.CreateSubKey($@"SOFTWARE\EqualizerAPO\Child APOs\{guid}");
        foreach (var i in new[] { 1, 2, 5, 6, 7 })
        {
            var name = $"{FxClsid},{i}";
            var v = fx?.GetValue(name) as string;
            if (v != null && (v.Equals(EapoEfx, StringComparison.OrdinalIgnoreCase) || v.Equals(EapoSfx, StringComparison.OrdinalIgnoreCase))) v = null;
            key.SetValue(name, v ?? "!VALUE");
        }
        key.SetValue("PreMixChild", "");
        key.SetValue("PostMixChild", "");
        key.SetValue("AllowSilentBufferModification", "false");
        key.SetValue("Version", "2");
    }

    /// <summary>Runs this exe once as SYSTEM through a temporary scheduled task, then deletes the task.</summary>
    static void RunAsSystem(string args)
    {
        var type = Type.GetTypeFromProgID("Schedule.Service") ?? throw new InvalidOperationException("Task Scheduler isn't available.");
        dynamic svc = Activator.CreateInstance(type)!;
        svc.Connect();
        dynamic folder = svc.GetFolder("\\");
        dynamic def = svc.NewTask(0);
        def.Settings.Enabled = true;
        def.Settings.Hidden = true;
        def.Settings.ExecutionTimeLimit = "PT2M";
        def.Principal.RunLevel = 1; // highest
        dynamic action = def.Actions.Create(0); // exec
        action.Path = Environment.ProcessPath!;
        action.Arguments = args;
        // 6 = create or update, 5 = run as service account (SYSTEM)
        dynamic task = folder.RegisterTaskDefinition(TaskName, def, 6, "SYSTEM", null, 5);
        try
        {
            task.Run(null);
            var until = DateTime.Now.AddSeconds(60);
            Thread.Sleep(1500);
            while (DateTime.Now < until && (int)task.State == 4 /* running */) Thread.Sleep(500);
        }
        finally { try { folder.DeleteTask(TaskName, 0); } catch { } }
    }

    static void RestartAudio()
    {
        using var sc = new ServiceController("Audiosrv");
        try { if (sc.Status != ServiceControllerStatus.Stopped) { sc.Stop(); sc.WaitForStatus(ServiceControllerStatus.Stopped, TimeSpan.FromSeconds(12)); } }
        catch (System.ServiceProcess.TimeoutException)
        {
            // Sometimes Windows Audio hangs while stopping: end its process and carry on.
            using var q = new System.Management.ManagementObjectSearcher("SELECT ProcessId FROM Win32_Service WHERE Name='Audiosrv'");
            foreach (System.Management.ManagementObject o in q.Get())
                if (Convert.ToInt32(o["ProcessId"]) is int pid and > 0) try { Process.GetProcessById(pid).Kill(); } catch { }
            Thread.Sleep(2000);
        }
        sc.Refresh();
        if (sc.Status != ServiceControllerStatus.Running) { sc.Start(); sc.WaitForStatus(ServiceControllerStatus.Running, TimeSpan.FromSeconds(20)); }
    }

    // ======================= SYSTEM step (--system-attach) =======================

    /// <summary>
    /// Puts Equalizer APO in the mic's endpoint effect slot (applied to every stream, including the
    /// raw mode Discord uses). The key belongs to SYSTEM: grant write access, change, restore exactly.
    /// </summary>
    public static int SystemAttach(string guid, string logPath)
    {
        using var w = new StreamWriter(new FileStream(logPath, FileMode.Append, FileAccess.Write, FileShare.ReadWrite));
        var path = $@"SOFTWARE\Microsoft\Windows\CurrentVersion\MMDevices\Audio\Capture\{guid}\FxProperties";
        using var hklm = RegistryKey.OpenBaseKey(RegistryHive.LocalMachine, RegistryView.Registry64);
        var check = RegistryKeyPermissionCheck.ReadWriteSubTree;
        RegistryKey? key = hklm.OpenSubKey(path, check, RegistryRights.ReadPermissions | RegistryRights.ChangePermissions);
        if (key == null)
        {
            // Some mics have no FxProperties yet: create it (SYSTEM can).
            using var parent = hklm.OpenSubKey(Path.GetDirectoryName(path)!, check, RegistryRights.CreateSubKey | RegistryRights.ReadKey);
            parent?.CreateSubKey("FxProperties")?.Dispose();
            key = hklm.OpenSubKey(path, check, RegistryRights.ReadPermissions | RegistryRights.ChangePermissions);
        }
        if (key == null) { w.WriteLine("SYSTEM couldn't open the mic's settings"); return 1; }
        var acl = key.GetAccessControl();
        var original = acl.GetSecurityDescriptorSddlForm(AccessControlSections.Access);
        try
        {
            acl.AddAccessRule(new RegistryAccessRule(WindowsIdentity.GetCurrent().User!, RegistryRights.SetValue | RegistryRights.QueryValues, AccessControlType.Allow));
            key.SetAccessControl(acl);
            using var wk = hklm.OpenSubKey(path, check, RegistryRights.SetValue | RegistryRights.QueryValues)!;
            wk.SetValue($"{FxClsid},7", EapoEfx, RegistryValueKind.String);
            wk.SetValue($"{FxModes},7", new[] { ModeDefault }, RegistryValueKind.MultiString);
            // Remove Equalizer APO from the stream slot so nothing runs twice.
            if (wk.GetValue($"{FxClsid},5") is string sfx && sfx.Equals(EapoSfx, StringComparison.OrdinalIgnoreCase))
            {
                wk.DeleteValue($"{FxClsid},5", false);
                wk.DeleteValue($"{FxModes},5", false);
            }
            w.WriteLine("SYSTEM attach done");
            return 0;
        }
        catch (Exception ex) { w.WriteLine($"SYSTEM attach failed: {ex.Message}"); return 1; }
        finally
        {
            var restore = key.GetAccessControl();
            restore.SetSecurityDescriptorSddlForm(original, AccessControlSections.Access);
            key.SetAccessControl(restore);
            key.Dispose();
        }
    }
}
