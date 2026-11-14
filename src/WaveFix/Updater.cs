using System.Diagnostics;
using System.IO;
using System.Net.Http;
using System.Reflection;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace WaveFix;

/// <summary>
/// Updates WaveFix from the GitHub releases of this repo. An update is only installed when the
/// downloaded installer comes from GitHub and matches the SHA-256 published with the release.
/// Updating doesn't interrupt your mic: effects run in the Windows audio engine, not in this app.
/// </summary>
public static class Updater
{
    public const string Repo = "NamiSwan222/wave-link-replacement";
    public static string ReleasesPage => $"https://github.com/{Repo}/releases";

    static readonly HttpClient Http = CreateClient();

    static HttpClient CreateClient()
    {
        var c = new HttpClient { Timeout = TimeSpan.FromMinutes(5) };
        c.DefaultRequestHeaders.UserAgent.ParseAdd($"WaveFix-Updater/{Current}");
        c.DefaultRequestHeaders.Accept.ParseAdd("application/vnd.github+json");
        return c;
    }

    public static Version Current
    {
        get
        {
            var v = Assembly.GetEntryAssembly()?.GetName().Version ?? new Version(0, 0, 0);
            return new Version(v.Major, v.Minor, Math.Max(0, v.Build));
        }
    }

    public sealed record Release(Version Version, string Tag, string InstallerName, string InstallerUrl, string ChecksumUrl, long Size);

    /// <summary>Returns the latest release if it's newer than this app, otherwise null.</summary>
    public static async Task<Release?> CheckAsync(CancellationToken ct = default)
    {
        using var resp = await Http.GetAsync($"https://api.github.com/repos/{Repo}/releases/latest", ct);
        if (resp.StatusCode == System.Net.HttpStatusCode.NotFound) return null; // no releases yet
        resp.EnsureSuccessStatusCode();
        using var doc = JsonDocument.Parse(await resp.Content.ReadAsStringAsync(ct));
        var root = doc.RootElement;
        if (root.TryGetProperty("draft", out var d) && d.GetBoolean()) return null;
        if (root.TryGetProperty("prerelease", out var p) && p.GetBoolean()) return null;

        var tag = root.GetProperty("tag_name").GetString() ?? "";
        var m = Regex.Match(tag, @"(\d+)\.(\d+)\.(\d+)");
        if (!m.Success) return null;
        var version = new Version(int.Parse(m.Groups[1].Value), int.Parse(m.Groups[2].Value), int.Parse(m.Groups[3].Value));
        if (version <= Current) return null;

        string? exeName = null, exeUrl = null, shaUrl = null; long size = 0;
        foreach (var a in root.GetProperty("assets").EnumerateArray())
        {
            var name = a.GetProperty("name").GetString() ?? "";
            var url = a.GetProperty("browser_download_url").GetString() ?? "";
            if (Regex.IsMatch(name, @"^WaveFix-Setup-[\d\.]+\.exe$", RegexOptions.IgnoreCase))
            { exeName = name; exeUrl = url; size = a.GetProperty("size").GetInt64(); }
            else if (name.EndsWith(".exe.sha256", StringComparison.OrdinalIgnoreCase)) shaUrl = url;
        }
        if (exeName == null || exeUrl == null || shaUrl == null) return null; // incomplete release: skip
        return new Release(version, tag, exeName, exeUrl, shaUrl, size);
    }

    static void RequireGitHub(string url)
    {
        var host = new Uri(url).Host;
        if (!(host == "github.com" || host.EndsWith(".githubusercontent.com", StringComparison.OrdinalIgnoreCase)))
            throw new InvalidOperationException($"Refusing to download from {host}.");
    }

    /// <summary>Downloads the installer and checks its SHA-256. Returns the local path.</summary>
    public static async Task<string> DownloadAsync(Release r, IProgress<double>? progress, CancellationToken ct = default)
    {
        RequireGitHub(r.InstallerUrl);
        RequireGitHub(r.ChecksumUrl);

        var shaText = await Http.GetStringAsync(r.ChecksumUrl, ct);
        var expected = Regex.Match(shaText, @"\b[0-9a-fA-F]{64}\b").Value;
        if (expected.Length != 64) throw new InvalidOperationException("The release's checksum file is unreadable.");

        var dir = Path.Combine(Path.GetTempPath(), "WaveFix-Update");
        Directory.CreateDirectory(dir);
        var file = Path.Combine(dir, r.InstallerName);

        using (var resp = await Http.GetAsync(r.InstallerUrl, HttpCompletionOption.ResponseHeadersRead, ct))
        {
            resp.EnsureSuccessStatusCode();
            var total = resp.Content.Headers.ContentLength ?? r.Size;
            await using var src = await resp.Content.ReadAsStreamAsync(ct);
            await using var dst = File.Create(file);
            var buf = new byte[81920]; long done = 0; int n;
            while ((n = await src.ReadAsync(buf, ct)) > 0)
            {
                await dst.WriteAsync(buf.AsMemory(0, n), ct);
                done += n;
                if (total > 0) progress?.Report((double)done / total);
            }
        }

        string actual;
        await using (var fs = File.OpenRead(file)) actual = Convert.ToHexString(await SHA256.HashDataAsync(fs, ct));
        if (!string.Equals(actual, expected, StringComparison.OrdinalIgnoreCase))
        {
            try { File.Delete(file); } catch { }
            throw new InvalidOperationException("The download didn't match the release's checksum, so it wasn't installed.");
        }
        return file;
    }

    /// <summary>Deletes downloaded installers once the update has finished (they're ~65 MB each).</summary>
    public static void CleanupDownloads()
    {
        var dir = Path.Combine(Path.GetTempPath(), "WaveFix-Update");
        // The installer may still be exiting; try a few times in the background.
        _ = Task.Run(async () =>
        {
            for (int i = 0; i < 10 && Directory.Exists(dir); i++)
            {
                try { Directory.Delete(dir, recursive: true); } catch { await Task.Delay(3000); }
            }
        });
    }

    /// <summary>Runs the installer silently; it closes WaveFix, updates, and starts the new version.</summary>
    public static void StartInstaller(string path) =>
        Process.Start(new ProcessStartInfo(path, "/VERYSILENT /SUPPRESSMSGBOXES /NORESTART /UPDATE") { UseShellExecute = true });
}
