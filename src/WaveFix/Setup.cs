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
///   normal app      -> shows progress, starts the admin helper with one UAC prompt
///   --setup         -> admin helper: installs WaveFix's audio engine + RNNoise, attaches, restarts audio
///   --system-attach -> runs briefly as SYSTEM (the owner of the mic's effect settings) to write them
/// Progress goes to a log file as lines like "STEP 2 DONE message", which the app reads.
/// Every download is checked against a pinned SHA-256 before it's used.
/// With --migrate (people who set WaveFix up on Equalizer APO before it had its own engine), the
/// same run also moves their plugins, takes Equalizer APO off the mic and uninstalls it.
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
    // Pinned download. To update a version, change the URL and its SHA-256 together.
    const string RnnoiseUrl = "https://github.com/werman/noise-suppression-for-voice/releases/download/v1.10/win-rnnoise.zip";
    const string RnnoiseSha256 = "E4DCC186102850F8DCEBEB766AD1BF34C24F6280BD614EF84B72DAE4783FC517";
    const string RnnoiseEntry = "win-rnnoise/vst/rnnoise_mono.dll";
    const string RnnoiseDllSha256 = "7DEBDFC4843461C47B23AC7A736B4663FE581643563AE764CDE395E006004080";
    public const string RnnoiseReleasePage = "https://github.com/werman/noise-suppression-for-voice/releases/tag/v1.10";

    /// <summary>The last setup run's log, kept so it can be copied and sent for help.</summary>
    public static string LastLogPath => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "WaveFix", "setup.log");

    const string TaskName = "WaveFix Setup";

    /// <summary>Switching an Equalizer APO setup (WaveFix 0.8.0 and older) to WaveFix's engine.</summary>
    public static bool Migrate { get; set; }

    public static string[] StepTitles => Migrate
        ? new[] { "Install the WaveFix audio engine", "Move noise removal and your plugins", "Switch your mic and remove Equalizer APO", "Restart Windows audio" }
        : new[] { "Install the WaveFix audio engine", "Add the noise removal plugin", "Connect the engine to your mic", "Restart Windows audio" };

    static string WorkDir => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData), "WaveFix", "Setup");
    static string NoisePlugin => WaveFixEngine.NoisePlugin;

    public static string GuidOf(string endpointId) => endpointId[(endpointId.LastIndexOf('.') + 1)..];

    /// <summary>What still needs doing for this mic (for the checklist and the status banner).</summary>
    public static bool[] DoneSteps(string endpointId) => new[]
    {
        WaveFixEngine.IsInstalled && !WaveFixEngine.NeedsUpdate,
        File.Exists(NoisePlugin) && !Migrate,
        WaveFixEngine.IsAttached(endpointId) && !EqualizerApo.IsOnMic(endpointId),
        false,
    };

    public static bool IsComplete(string endpointId) => DoneSteps(endpointId).Take(3).All(d => d);

    // ======================= Normal app: start the helper =======================

    /// <summary>Starts the admin helper (one UAC prompt) and reports "STEP n STATE message" lines.</summary>
    public static async Task<bool> RunAsync(string endpointId, IProgress<(int step, string state, string message)> progress, string? localPlugin = null,
        IReadOnlyList<string>? pluginsToMove = null, CancellationToken ct = default)
    {
        var log = LastLogPath;
        Directory.CreateDirectory(Path.GetDirectoryName(log)!);
        File.WriteAllText(log, "");
        var args = $"--setup {GuidOf(endpointId)} \"{log}\"" + (Migrate ? " --migrate" : "");
        if (pluginsToMove is { Count: > 0 })
        {
            // Plugins from the old shared folder, copied into Program Files by the admin helper.
            var list = Path.Combine(Path.GetDirectoryName(log)!, "plugins-to-move.txt");
            File.WriteAllLines(list, pluginsToMove);
            args += $" --plugins \"{list}\"";
        }
        if (localPlugin != null) args += $" \"{localPlugin}\"";
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

    public static int RunElevated(string micGuid, string logPath, string? localPlugin = null, string? pluginList = null)
    {
        using var w = new StreamWriter(new FileStream(logPath, FileMode.Append, FileAccess.Write, FileShare.ReadWrite));
        _log = w;
        try
        {
            if (!new WindowsPrincipal(WindowsIdentity.GetCurrent()).IsInRole(WindowsBuiltInRole.Administrator))
                throw new InvalidOperationException("The setup helper needs administrator rights.");
            Directory.CreateDirectory(WorkDir);
            Log($"LOG WaveFix {Updater.Current} setup, Windows {Environment.OSVersion.Version}, mic {micGuid}, migrate: {Migrate}, local plugin: {localPlugin ?? "none"}");
            using var http = new HttpClient { Timeout = Timeout.InfiniteTimeSpan }; // our own watchdogs handle stalls
            http.DefaultRequestHeaders.UserAgent.ParseAdd($"WaveFix-Setup/{Updater.Current}");
            bool changed = false;
            var guid = micGuid.Trim('{', '}');

            // 1. WaveFix's audio engine
            if (WaveFixEngine.IsInstalled && !WaveFixEngine.NeedsUpdate) Step(0, "SKIP", "Already installed");
            else
            {
                Step(0, "RUN", WaveFixEngine.IsInstalled ? "Updating the WaveFix audio engine…" : "Installing the WaveFix audio engine…");
                WaveFixEngine.Install();
                if (!WaveFixEngine.IsInstalled) throw new InvalidOperationException("Windows didn't accept the audio engine's registration.");
                Step(0, "DONE", "Installed");
                changed = true;
            }

            // 2. RNNoise (and, when switching, the user's own plugins) into Program Files\WaveFix\Plugins
            if (File.Exists(NoisePlugin) && pluginList == null) Step(1, "SKIP", "Already there");
            else
            {
                if (!File.Exists(NoisePlugin))
                {
                    if (localPlugin != null) { Step(1, "RUN", "Checking the file you picked…"); InstallNoisePlugin(localPlugin); }
                    else if (EqualizerApo.NoisePlugin is { } old && File.Exists(old) && TryInstallNoisePlugin(old))
                        Log("LOG reused the noise removal plugin from Equalizer APO's folder");
                    else
                    {
                        Step(1, "RUN", "Downloading the noise removal plugin…");
                        InstallNoisePlugin(Download(http, RnnoiseUrl, RnnoiseSha256, "win-rnnoise.zip", 1, "Downloading the noise removal plugin"));
                    }
                }
                if (pluginList != null && File.Exists(pluginList))
                {
                    Step(1, "RUN", "Moving your plugins…");
                    MovePlugins(File.ReadAllLines(pluginList));
                }
                Step(1, "DONE", pluginList != null ? "Moved" : "Added");
                changed = true;
            }

            // 3. Attach to the mic's endpoint effects (taking Equalizer APO off when switching)
            bool attached() => WaveFixEngine.IsAttached(guid) && !EqualizerApo.IsOnMic("x.{" + guid + "}");
            if (attached()) Step(2, "SKIP", "Already connected");
            else
            {
                Step(2, "RUN", Migrate ? "Switching your mic to WaveFix's engine…" : "Connecting the engine to your mic…");
                WaveFixEngine.BackupMic(guid, fromEqualizerApo: EqualizerApo.IsOnMic("x.{" + guid + "}"));
                RunAsSystem($"--system-attach {{{guid}}} \"{logPath}\"" + (Migrate ? " --migrate" : ""));
                if (!attached()) throw new InvalidOperationException("Windows didn't accept the mic setting.");
                changed = true;
                if (Migrate && EqualizerApo.Installed)
                {
                    if (EqualizerApo.UsedElsewhere(guid))
                        Log("LOG Equalizer APO is still used by another device, so it was left installed");
                    else
                    {
                        Step(2, "RUN", "Removing Equalizer APO…");
                        EqualizerApo.Uninstall(m => Log($"LOG {m}"));
                    }
                }
                Step(2, "DONE", Migrate ? (EqualizerApo.Installed ? "Switched (Equalizer APO kept for your other devices)" : "Switched") : "Connected");
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

    /// <summary>Copies plugins from the old shared folder into Program Files\WaveFix\Plugins (same file names).</summary>
    static void MovePlugins(IEnumerable<string> sources)
    {
        var oldFolder = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData), "WaveFix", "Plugins");
        Directory.CreateDirectory(WaveFixEngine.PluginDir);
        foreach (var src in sources.Where(l => l.Length > 0))
        {
            var full = Path.GetFullPath(src);
            // Only files from WaveFix's own old plugin folder, and only real 64-bit VST2 plugins.
            if (!full.StartsWith(oldFolder + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase) || !File.Exists(full))
            { Log($"LOG skipped plugin {src}"); continue; }
            if (PluginStore.Validate(full) is { } problem) { Log($"LOG skipped plugin {src}: {problem}"); continue; }
            File.Copy(full, Path.Combine(WaveFixEngine.PluginDir, Path.GetFileName(full)), overwrite: true);
            Log($"LOG moved plugin {Path.GetFileName(full)}");
        }
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
        { throw new InvalidOperationException($"Couldn't copy the plugin into place ({ex.Message}). Antivirus may be blocking it."); }
    }

    static bool TryInstallNoisePlugin(string source)
    {
        try { InstallNoisePlugin(source); return true; }
        catch (Exception ex) { Log($"LOG couldn't reuse {source}: {ex.Message}"); return false; }
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

    // ======================= Undo (WaveFix engine) =======================

    /// <summary>Starts the admin helper that takes the engine off this mic (one UAC prompt).</summary>
    public static async Task<bool> UndoAsync(string endpointId)
    {
        var log = LastLogPath;
        Directory.CreateDirectory(Path.GetDirectoryName(log)!);
        File.WriteAllText(log, "");
        try
        {
            using var p = Process.Start(new ProcessStartInfo(Environment.ProcessPath!, $"--setup-undo {GuidOf(endpointId)} \"{log}\"")
            { UseShellExecute = true, Verb = "runas", WindowStyle = ProcessWindowStyle.Hidden });
            if (p == null) return false;
            await p.WaitForExitAsync();
            return p.ExitCode == 0;
        }
        catch (System.ComponentModel.Win32Exception) { return false; } // UAC "No"
    }

    /// <summary>Admin helper: puts the mic's original effect settings back and restarts audio.</summary>
    public static int RunUndoElevated(string micGuid, string logPath)
    {
        using var w = new StreamWriter(new FileStream(logPath, FileMode.Append, FileAccess.Write, FileShare.ReadWrite));
        _log = w;
        try
        {
            var guid = micGuid.Trim('{', '}');
            Log($"LOG WaveFix {Updater.Current} undo, mic {guid}");
            RunAsSystem($"--system-attach {{{guid}}} \"{logPath}\" --undo");
            if (WaveFixEngine.IsAttached(guid)) throw new InvalidOperationException("Windows didn't accept the change.");
            WaveFixEngine.ForgetBackup(guid);
            RestartAudio();
            Log("RESULT OK");
            return 0;
        }
        catch (Exception ex) { Log($"LOG {ex}"); Log("RESULT FAIL"); return 1; }
        finally { _log = null; }
    }

    // ======================= Admin helper: plugins in the protected folder =======================

    /// <summary>
    /// Copies a plugin into Program Files\WaveFix\Plugins (admin-only, so nothing else can swap it
    /// for other code running inside Windows audio). Returns the installed path, or null if declined.
    /// </summary>
    public static async Task<string?> InstallPluginElevated(string source, string dest)
    {
        var log = Path.Combine(Path.GetTempPath(), $"wavefix-plugin-{Guid.NewGuid():N}.log");
        try
        {
            using var p = Process.Start(new ProcessStartInfo(Environment.ProcessPath!, $"--plugin-install \"{source}\" \"{dest}\" \"{log}\"")
            { UseShellExecute = true, Verb = "runas", WindowStyle = ProcessWindowStyle.Hidden });
            if (p == null) return null;
            await p.WaitForExitAsync();
            if (p.ExitCode != 0) throw new InvalidOperationException(File.Exists(log) ? File.ReadAllText(log).Trim() : "the copy failed");
            return dest;
        }
        catch (System.ComponentModel.Win32Exception) { return null; } // UAC "No"
        finally { try { File.Delete(log); } catch { } }
    }

    public static int PluginInstall(string source, string dest, string logPath)
    {
        try
        {
            if (!new WindowsPrincipal(WindowsIdentity.GetCurrent()).IsInRole(WindowsBuiltInRole.Administrator))
                throw new InvalidOperationException("needs administrator rights");
            var full = Path.GetFullPath(dest);
            if (!full.StartsWith(WaveFixEngine.PluginDir + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase))
                throw new InvalidOperationException("plugins can only go in WaveFix's plugin folder");
            if (PluginStore.Validate(source) is { } problem) throw new InvalidOperationException(problem);
            Directory.CreateDirectory(Path.GetDirectoryName(full)!);
            File.Copy(source, full, overwrite: true);
            return 0;
        }
        catch (Exception ex) { try { File.WriteAllText(logPath, ex.Message); } catch { } return 1; }
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
    /// Puts WaveFix's engine in the mic's endpoint effects (applied to every stream, including the
    /// raw mode Discord uses), or takes it off (undo). When switching from Equalizer APO, takes that
    /// off first. The key belongs to SYSTEM: grant write access, change, restore exactly.
    /// </summary>
    public static int SystemAttach(string guid, string logPath, bool undo = false, bool migrate = false)
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
            if (undo) WaveFixEngine.Detach(wk, guid);
            else
            {
                if (migrate) EqualizerApo.RemoveFromMic(wk, guid);
                WaveFixEngine.Attach(wk);
            }
            w.WriteLine(undo ? "SYSTEM detach done" : "SYSTEM attach done");
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
