using System.IO;
using System.Net.Http;
using System.Text.Json;

namespace WaveFix;

/// <summary>A sound found online.</summary>
public sealed record OnlineSound(string Title, string Creator, string License, string LicenseUrl, double Seconds, string Source, string Url, string FileType, string Page)
{
    public string Details => $"by {Creator} · {License} · {Seconds:0.#} s · {Source}";
    /// <summary>The credit kept with the sound (CC BY licenses require it).</summary>
    public string Credit => $"\"{Title}\" by {Creator} ({License}, {Source})";
}

/// <summary>
/// Searches Openverse (openverse.org) for openly licensed sounds: Freesound, Wikimedia Commons and
/// more, each with its license. No account or key needed.
/// </summary>
public static class SoundCatalog
{
    static readonly HttpClient Http = new() { Timeout = TimeSpan.FromSeconds(30) };
    static SoundCatalog() => Http.DefaultRequestHeaders.UserAgent.ParseAdd("WaveFix");

    public static async Task<List<OnlineSound>> SearchAsync(string query, CancellationToken ct)
    {
        var url = "https://api.openverse.org/v1/audio/?page_size=20&length=shortest,short&q=" + Uri.EscapeDataString(query.Trim());
        using var resp = await Http.GetAsync(url, ct);
        resp.EnsureSuccessStatusCode();
        using var doc = JsonDocument.Parse(await resp.Content.ReadAsStringAsync(ct));
        var list = new List<OnlineSound>();
        foreach (var r in doc.RootElement.GetProperty("results").EnumerateArray())
        {
            string S(string k) => r.TryGetProperty(k, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() ?? "" : "";
            var file = S("url");
            if (file.Length == 0) continue;
            var lic = S("license").ToUpperInvariant();
            var ver = S("license_version");
            string license = lic switch { "CC0" => "CC0 (free for anything)", "PDM" => "Public domain", _ => $"CC {lic} {ver}".Trim() };
            double secs = r.TryGetProperty("duration", out var d) && d.ValueKind == JsonValueKind.Number ? d.GetDouble() / 1000 : 0;
            if (secs > 15 * 60) continue; // the soundboard plays up to 15 minutes
            list.Add(new OnlineSound(S("title").Length > 0 ? S("title") : "Untitled", S("creator").Length > 0 ? S("creator") : "unknown",
                license, S("license_url"), secs, S("source"), file, S("filetype"), S("foreign_landing_url")));
        }
        return list;
    }

    /// <summary>Downloads a sound to a temporary file named after it (the soundboard copies it in).</summary>
    public static async Task<string> DownloadAsync(OnlineSound s, CancellationToken ct)
    {
        var ext = s.FileType is { Length: > 0 } ft ? "." + ft.TrimStart('.').ToLowerInvariant() : Path.GetExtension(new Uri(s.Url).AbsolutePath);
        if (ext.Length == 0) ext = ".mp3";
        var safe = string.Concat(s.Title.Where(c => !Path.GetInvalidFileNameChars().Contains(c))).Trim();
        if (safe.Length == 0) safe = "sound";
        if (safe.Length > 60) safe = safe[..60];
        var dir = Path.Combine(Path.GetTempPath(), "WaveFix-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        var path = Path.Combine(dir, safe + ext);
        using var resp = await Http.GetAsync(s.Url, HttpCompletionOption.ResponseHeadersRead, ct);
        resp.EnsureSuccessStatusCode();
        if (resp.Content.Headers.ContentLength > 100_000_000) throw new IOException("the sound file is too big");
        await using (var src = await resp.Content.ReadAsStreamAsync(ct))
        await using (var dst = File.Create(path))
            await src.CopyToAsync(dst, ct);
        return path;
    }
}
