using System.IO;
using System.Net.Http;
using System.Text.Json;

namespace WaveFix.Rvc;

/// <summary>A voice model found online.</summary>
public sealed record OnlineVoice(string Id, string Name, string Author, string License, long Downloads, int Likes,
    string ModelFile, string? IndexFile, string? PictureFile)
{
    public string Page => $"https://huggingface.co/{Id}";
    public string Details => $"by {Author} · {License} · {Downloads:N0} downloads" + (IndexFile != null ? " · has index" : "");
}

/// <summary>
/// Finds RVC voice models on Hugging Face (models tagged "rvc"), the main place they're shared.
/// Only repositories with a usable voice .pth are listed; training checkpoints and the shared
/// helper models are skipped.
/// </summary>
public static class VoiceCatalog
{
    static readonly HttpClient Http = new() { Timeout = TimeSpan.FromSeconds(30) };

    static VoiceCatalog() => Http.DefaultRequestHeaders.UserAgent.ParseAdd("WaveFix");

    static bool IsVoicePth(string f)
    {
        var name = Path.GetFileName(f);
        if (!name.EndsWith(".pth", StringComparison.OrdinalIgnoreCase)) return false;
        // Training checkpoints (G_1000.pth, D_...), RVC's pretrained bases and helper models aren't voices.
        string[] skip = { "G_", "D_", "f0G", "f0D", "hubert", "rmvpe", "pretrained" };
        return !skip.Any(s => name.StartsWith(s, StringComparison.OrdinalIgnoreCase)) && !f.Contains("pretrained", StringComparison.OrdinalIgnoreCase);
    }

    public static async Task<List<OnlineVoice>> SearchAsync(string query, CancellationToken ct)
    {
        var url = "https://huggingface.co/api/models?filter=rvc&sort=downloads&direction=-1&limit=60&full=true"
                  + (string.IsNullOrWhiteSpace(query) ? "" : "&search=" + Uri.EscapeDataString(query.Trim()));
        using var resp = await Http.GetAsync(url, ct);
        resp.EnsureSuccessStatusCode();
        using var doc = JsonDocument.Parse(await resp.Content.ReadAsStringAsync(ct));
        var list = new List<OnlineVoice>();
        foreach (var m in doc.RootElement.EnumerateArray())
        {
            var id = m.GetProperty("id").GetString() ?? "";
            var files = m.TryGetProperty("siblings", out var sib) ? sib.EnumerateArray().Select(x => x.GetProperty("rfilename").GetString() ?? "").ToList() : new();
            var pth = files.Where(IsVoicePth).OrderBy(f => f.Count(c => c == '/')).FirstOrDefault();
            if (pth == null) continue;
            var index = files.Where(f => f.EndsWith(".index", StringComparison.OrdinalIgnoreCase) && !Path.GetFileName(f).StartsWith("trained", StringComparison.OrdinalIgnoreCase))
                             .OrderByDescending(f => Path.GetFileName(f).StartsWith("added", StringComparison.OrdinalIgnoreCase)).FirstOrDefault();
            var pic = files.FirstOrDefault(f => RvcModelImport.ImageExtensions.Contains(Path.GetExtension(f).ToLowerInvariant()));
            string license = "license not stated";
            if (m.TryGetProperty("tags", out var tags))
                foreach (var t in tags.EnumerateArray())
                    if (t.GetString() is { } s && s.StartsWith("license:")) license = s[8..] == "other" ? "custom license" : s[8..].ToUpperInvariant();
            var slash = id.IndexOf('/');
            string author = slash > 0 ? id[..slash] : id;
            string name = (slash > 0 ? id[(slash + 1)..] : id).Replace('_', ' ').Replace('-', ' ');
            list.Add(new OnlineVoice(id, name, author, license,
                m.TryGetProperty("downloads", out var d) && d.ValueKind == JsonValueKind.Number ? d.GetInt64() : 0,
                m.TryGetProperty("likes", out var l) && l.ValueKind == JsonValueKind.Number ? l.GetInt32() : 0,
                pth, index, pic));
        }
        return list;
    }

    static string FileUrl(OnlineVoice v, string file) =>
        $"https://huggingface.co/{v.Id}/resolve/main/{string.Join('/', file.Split('/').Select(Uri.EscapeDataString))}";

    /// <summary>Downloads the voice (model, index, picture) and imports it.</summary>
    public static async Task<AiVoice> InstallAsync(OnlineVoice v, IProgress<string> progress, CancellationToken ct)
    {
        var dir = Path.Combine(Path.GetTempPath(), "WaveFix-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        try
        {
            var pth = Path.Combine(dir, "model.pth");
            await Download(FileUrl(v, v.ModelFile), pth, "voice model", 600_000_000, progress, ct);
            string? index = null, pic = null;
            if (v.IndexFile != null)
            {
                index = Path.Combine(dir, "model.index");
                try { await Download(FileUrl(v, v.IndexFile), index, "index", 2_000_000_000, progress, ct); }
                catch (OperationCanceledException) { throw; }
                catch (Exception ex) { ChangeLog.Write($"Voice browser: {v.Id} index not downloaded: {ex.Message}"); index = null; }
            }
            if (v.PictureFile != null)
            {
                pic = Path.Combine(dir, "picture" + Path.GetExtension(v.PictureFile).ToLowerInvariant());
                try { await Download(FileUrl(v, v.PictureFile), pic, "picture", 20_000_000, progress, ct); } catch { pic = null; }
            }
            progress.Report("Converting the model…");
            return await Task.Run(() =>
            {
                var voice = RvcModelImport.Import(pth, progress);
                voice.Name = v.Name;
                voice.Source = v.Page;
                if (index != null) { try { RvcModelImport.AddIndex(voice, index); } catch (Exception ex) { ChangeLog.Write($"Voice browser: {v.Id} index not used: {ex.Message}"); } }
                if (pic != null) { try { RvcModelImport.SetImage(voice, pic); } catch { } }
                return voice;
            }, ct);
        }
        finally { try { Directory.Delete(dir, true); } catch { } }
    }

    static async Task Download(string url, string path, string what, long maxBytes, IProgress<string> progress, CancellationToken ct)
    {
        using var client = new HttpClient { Timeout = Timeout.InfiniteTimeSpan };
        client.DefaultRequestHeaders.UserAgent.ParseAdd("WaveFix");
        using var resp = await client.GetAsync(url, HttpCompletionOption.ResponseHeadersRead, ct);
        resp.EnsureSuccessStatusCode();
        long? total = resp.Content.Headers.ContentLength;
        if (total > maxBytes) throw new IOException($"the {what} is unexpectedly large ({total / 1e6:0} MB)");
        await using var src = await resp.Content.ReadAsStreamAsync(ct);
        await using var dst = File.Create(path);
        var buf = new byte[1 << 16];
        long got = 0, last = 0;
        int n;
        while ((n = await src.ReadAsync(buf, ct)) > 0)
        {
            await dst.WriteAsync(buf.AsMemory(0, n), ct);
            got += n;
            if (got > maxBytes) throw new IOException($"the {what} is unexpectedly large");
            if (got - last > 1 << 20)
            {
                last = got;
                progress.Report(total > 0 ? $"Downloading the {what}: {got / 1e6:0} of {total / 1e6:0} MB" : $"Downloading the {what}: {got / 1e6:0} MB");
            }
        }
    }
}
