using System.IO;
using System.Net.Http;
using System.Security.Cryptography;

namespace WaveFix.Rvc;

/// <summary>
/// The two models every AI voice needs besides the voice itself: ContentVec (what you're saying)
/// and RMVPE (your pitch). Too big for the installer (about 740 MB together), so they're
/// downloaded once, the first time AI voices are turned on, and checked by SHA-256.
/// </summary>
public static class RvcHelpers
{
    public sealed record Helper(string File, long Size, string Sha256, string[] Urls, string What);

    const string Mirror = "https://github.com/NamiSwan222/wave-link-replacement/releases/download/ai-voice-models-1/";

    public static readonly Helper[] All =
    {
        // ContentVec (MIT, lengyue233/content-vec-best) exported by tools/rvc/make_contentvec.py.
        new("contentvec.onnx", 378_472_269, "0bad321056ac25850717b0608e71275e1bdfd6ab6d6fdbc8ac2769a68376ae98",
            new[] { Mirror + "contentvec.onnx" }, "speech model"),
        // RMVPE (MIT), straight from RVC's own model repository.
        new("rmvpe.onnx", 361_688_443, "5370e71ac80af8b4b7c793d27efd51fd8bf962de3a7ede0766dac0befa3660fd",
            new[] { "https://huggingface.co/lj1995/VoiceConversionWebUI/resolve/main/rmvpe.onnx", Mirror + "rmvpe.onnx" }, "pitch model"),
    };

    public static string Folder => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "WaveFix", "AI");

    public static string PathOf(Helper h) => Path.Combine(Folder, h.File);

    public static long TotalSize => All.Sum(h => h.Size);

    /// <summary>Both helper models are downloaded (size checked; the hash was checked when downloading).</summary>
    public static bool Ready => All.All(h => new FileInfo(PathOf(h)) is { Exists: true } f && f.Length == h.Size);

    /// <summary>Downloads whatever is missing. Progress is 0..1 over everything.</summary>
    public static async Task DownloadAsync(IProgress<(double Fraction, string Text)> progress, CancellationToken ct)
    {
        Directory.CreateDirectory(Folder);
        using var http = new HttpClient { Timeout = Timeout.InfiniteTimeSpan };
        http.DefaultRequestHeaders.UserAgent.ParseAdd("WaveFix");
        long doneBefore = 0;
        foreach (var h in All)
        {
            var path = PathOf(h);
            if (new FileInfo(path) is { Exists: true } f && f.Length == h.Size) { doneBefore += h.Size; continue; }
            Exception? last = null;
            foreach (var url in h.Urls)
            {
                try
                {
                    await DownloadOne(http, url, path, h, doneBefore, progress, ct);
                    last = null;
                    break;
                }
                catch (OperationCanceledException) { throw; }
                catch (Exception ex) { last = ex; ChangeLog.Write($"AI voice download of {h.File} from {new Uri(url).Host} failed: {ex.Message}"); }
            }
            if (last != null) throw new IOException($"Couldn't download the {h.What}: {last.Message}", last);
            doneBefore += h.Size;
        }
        progress.Report((1, "Done"));
    }

    static async Task DownloadOne(HttpClient http, string url, string path, Helper h, long doneBefore,
        IProgress<(double, string)> progress, CancellationToken ct)
    {
        var part = path + ".part";
        using var resp = await http.GetAsync(url, HttpCompletionOption.ResponseHeadersRead, ct);
        resp.EnsureSuccessStatusCode();
        using var sha = SHA256.Create();
        await using (var src = await resp.Content.ReadAsStreamAsync(ct))
        await using (var dst = new FileStream(part, FileMode.Create, FileAccess.Write, FileShare.None, 1 << 16, useAsync: true))
        {
            var buf = new byte[1 << 16];
            long got = 0, lastReport = 0;
            int n;
            while ((n = await src.ReadAsync(buf, ct)) > 0)
            {
                await dst.WriteAsync(buf.AsMemory(0, n), ct);
                sha.TransformBlock(buf, 0, n, null, 0);
                got += n;
                if (got - lastReport > 1 << 20)
                {
                    lastReport = got;
                    double all = (double)(doneBefore + got) / TotalSize;
                    progress.Report((all, $"Downloading the {h.What}: {got / 1e6:0} of {h.Size / 1e6:0} MB"));
                }
            }
            sha.TransformFinalBlock(Array.Empty<byte>(), 0, 0);
            if (got != h.Size) throw new IOException($"the download stopped early ({got / 1e6:0} of {h.Size / 1e6:0} MB)");
        }
        var hash = Convert.ToHexString(sha.Hash!).ToLowerInvariant();
        if (hash != h.Sha256)
        {
            File.Delete(part);
            throw new IOException("the downloaded file didn't match (damaged or changed download)");
        }
        File.Move(part, path, overwrite: true);
    }
}
