using System.IO;
using System.Reflection;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace WaveFix.Rvc;

/// <summary>AI voices page settings.</summary>
public sealed class AiVoiceSettings
{
    [System.Text.Json.Serialization.JsonExtensionData] public Dictionary<string, JsonElement>? Extra { get; set; }

    public bool Enabled { get; set; }
    public string? Selected { get; set; }
    public List<AiVoice> Voices { get; set; } = new();
    /// <summary>Run the models on the graphics card (DirectML) when it works.</summary>
    public bool UseGraphicsCard { get; set; } = true;
    /// <summary>When the AI voice isn't ready or WaveFix is closed, send silence instead of the real voice.</summary>
    public bool SilentWhenUnavailable { get; set; }
    /// <summary>Quieter than this (dBFS) is treated as silence before conversion.</summary>
    public double GateDb { get; set; } = -50;
    /// <summary>Learn the mic's background noise and ignore it (stops babbling in pauses).</summary>
    public bool AutoGate { get; set; } = true;

    public AiVoice? Current => Voices.FirstOrDefault(v => v.Id == Selected) ?? Voices.FirstOrDefault();
}

/// <summary>An AI voice the user added (an imported RVC model).</summary>
public sealed class AiVoice
{
    [System.Text.Json.Serialization.JsonExtensionData] public Dictionary<string, JsonElement>? Extra { get; set; }

    public string Id { get; set; } = Guid.NewGuid().ToString("N")[..12];
    public string Name { get; set; } = "";
    /// <summary>The runnable model WaveFix made from the .pth (in the AI voices folder).</summary>
    public string File { get; set; } = "";
    /// <summary>"v1" (256-dim speech features) or "v2" (768).</summary>
    public string Version { get; set; } = "v2";
    /// <summary>The model's own sample rate (32000, 40000 or 48000).</summary>
    public int SampleRate { get; set; } = 40000;
    /// <summary>Where it was imported from (shown to the user only).</summary>
    public string Source { get; set; } = "";
    /// <summary>Semitones; voices trained on another gender usually want +/-12.</summary>
    public double Pitch { get; set; }
    /// <summary>The model's feature index (.index) in the AI voices folder, or null.</summary>
    public string? IndexFile { get; set; }
    /// <summary>How strongly the index pulls the voice toward the original speaker (0..1).</summary>
    public double IndexRate { get; set; } = 0.5;
    /// <summary>Speakers in the model (0 = unknown, from before this was recorded).</summary>
    public int Speakers { get; set; }
    public int SpeakerId { get; set; }
    /// <summary>0 = the AI voice follows your loudness (RVC's default); 1 = the model's own loudness.</summary>
    public double LoudnessMix { get; set; }
    /// <summary>A picture for the voice's tile, in the AI voices folder, or null.</summary>
    public string? Image { get; set; }
}

/// <summary>
/// Turns an RVC .pth voice model into an ONNX model WaveFix can run, without Python: the app ships
/// RVC's synthesizer for each standard model type with its weights left empty (Rvc/Templates,
/// made by tools/rvc/make_templates.py), and this fills them in from the .pth.
/// </summary>
public static class RvcModelImport
{
    public static string Folder => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "WaveFix", "AI voices");

    sealed record Template(string Key, string Version, JsonArray Config, HashSet<string> Weights);

    static List<Template>? _templates;

    static Stream Resource(string name) =>
        Assembly.GetExecutingAssembly().GetManifestResourceStream("WaveFix.Rvc." + name)
        ?? throw new FileNotFoundException($"This copy of WaveFix is missing its AI voice templates ({name}).");

    static List<Template> Templates()
    {
        if (_templates != null) return _templates;
        using var s = Resource("templates.json");
        var json = JsonNode.Parse(s)!.AsObject();
        return _templates = json.Select(kv => new Template(kv.Key, (string)kv.Value!["version"]!, kv.Value["config"]!.AsArray(),
            kv.Value["weights"]!.AsArray().Select(w => (string)w!).ToHashSet())).ToList();
    }

    /// <summary>Imports a .pth (on a background thread; can take a few seconds).</summary>
    public static AiVoice Import(string pthPath, IProgress<string>? progress = null)
    {
        progress?.Report("Reading the model…");
        var root = PthReader.Dict(PthReader.Load(pthPath, out var tensor))
            ?? throw new InvalidDataException("This file doesn't look like an RVC voice model.");
        var weights = PthReader.Dict(root.GetValueOrDefault("weight"))
            ?? throw new InvalidDataException("This file doesn't look like an RVC voice model (it has no \"weight\" section). If it's a training checkpoint (G_*.pth), use the small model RVC exports instead (in its weights folder).");
        var config = root.GetValueOrDefault("config") as List<object?> ?? throw new InvalidDataException("This RVC model has no \"config\" section.");
        string version = root.GetValueOrDefault("version") as string ?? "v1";
        long f0 = root.GetValueOrDefault("f0") is long f ? f : 1;
        if (f0 != 1) throw new InvalidDataException("This voice model was trained without pitch, which WaveFix doesn't support. Most RVC models have pitch; pick one of those.");

        // Find the template with the same architecture (the speaker count may differ).
        var tpl = Templates().FirstOrDefault(t => t.Version == version && SameConfig(t.Config, config))
            ?? throw new InvalidDataException($"This voice model's layout ({version}, {Describe(config)}) isn't one of the standard RVC types WaveFix supports (v1/v2 at 32k, 40k or 48k).");

        progress?.Report("Converting the model…");
        var filled = new Dictionary<string, PthTensor>();
        foreach (var name in tpl.Weights)
        {
            var t = tensor(weights.GetValueOrDefault(name)) ?? throw new InvalidDataException($"The model is missing part of the voice ({name}).");
            filled[name] = t;
        }

        Directory.CreateDirectory(Folder);
        var voice = new AiVoice
        {
            Name = Path.GetFileNameWithoutExtension(pthPath),
            Version = version,
            SampleRate = Convert.ToInt32(config[^1]),
            Source = pthPath,
            // RVC gives every model 109 speaker slots by default, used or not: only a different count
            // means the model really was trained with several speakers.
            Speakers = filled["emb_g.weight"].Shape[0] is var n && n != 109 ? (int)n : 1,
        };
        voice.File = Path.Combine(Folder, voice.Id + ".onnx");
        using (var template = Resource(tpl.Key + ".onnx"))
        {
            var bytes = new MemoryStream();
            template.CopyTo(bytes);
            var tmp = voice.File + ".tmp";
            using (var o = System.IO.File.Create(tmp)) OnnxFill.Fill(bytes.ToArray(), filled, o, allowFirstDimChange: "emb_g.weight");
            System.IO.File.Move(tmp, voice.File, overwrite: true);
        }
        return voice;
    }

    static bool SameConfig(JsonArray tpl, List<object?> cfg)
    {
        if (tpl.Count != cfg.Count) return false;
        for (int i = 0; i < cfg.Count; i++)
        {
            if (i == cfg.Count - 3) continue;            // speaker count: varies per model
            if (i == 1 || i == 8) continue;              // segment size, dropout: training only
            if (!SameValue(tpl[i], cfg[i])) return false;
        }
        return true;
    }

    static bool SameValue(JsonNode? a, object? b)
    {
        switch (b)
        {
            case List<object?> l:
            {
                var arr = a as JsonArray;
                return arr != null && arr.Count == l.Count && arr.Zip(l).All(p => SameValue(p.First, p.Second));
            }
            case object?[] t:
            {
                var arr = a as JsonArray;
                return arr != null && arr.Count == t.Length && arr.Zip(t).All(p => SameValue(p.First, p.Second));
            }
            case string s: return a?.GetValueKind() == JsonValueKind.String ? (string)a! == s : a?.ToString() == s;
            case long n: return a?.GetValueKind() == JsonValueKind.Number && (double)a! == n;
            case double d: return a?.GetValueKind() == JsonValueKind.Number && Math.Abs((double)a! - d) < 1e-9;
            default: return false;
        }
    }

    static string Describe(List<object?> cfg) => cfg.Count > 0 ? $"{cfg[^1]} Hz" : "unknown";

    /// <summary>Deletes an imported voice's model file (and its index).</summary>
    public static void Delete(AiVoice v)
    {
        try { if (v.File.StartsWith(Folder, StringComparison.OrdinalIgnoreCase)) System.IO.File.Delete(v.File); } catch { }
        RemoveIndex(v);
        RemoveImage(v);
    }

    public static readonly string[] ImageExtensions = { ".png", ".jpg", ".jpeg", ".bmp", ".gif", ".webp" };

    /// <summary>Copies a picture into the AI voices folder for this voice (replacing any old one).</summary>
    public static void SetImage(AiVoice v, string imagePath)
    {
        var ext = Path.GetExtension(imagePath).ToLowerInvariant();
        if (!ImageExtensions.Contains(ext)) throw new InvalidDataException("Pick a PNG, JPG, BMP, GIF or WEBP picture.");
        if (new FileInfo(imagePath).Length > 20_000_000) throw new InvalidDataException("That picture is too big (over 20 MB).");
        Directory.CreateDirectory(Folder);
        RemoveImage(v);
        var dest = Path.Combine(Folder, $"{v.Id}-{Guid.NewGuid().ToString("N")[..6]}{ext}");
        System.IO.File.Copy(imagePath, dest, overwrite: true);
        v.Image = dest;
    }

    public static void RemoveImage(AiVoice v)
    {
        try { if (v.Image != null && v.Image.StartsWith(Folder, StringComparison.OrdinalIgnoreCase)) System.IO.File.Delete(v.Image); } catch { }
        v.Image = null;
    }

    /// <summary>Checks an .index file and copies it into the AI voices folder for this voice.</summary>
    public static void AddIndex(AiVoice v, string indexPath)
    {
        var idx = RvcIndex.Load(indexPath); // throws with a readable message if it's not usable
        int dim = v.Version == "v2" ? 768 : 256;
        if (idx.Dim != dim)
            throw new InvalidDataException($"This index is for a {(idx.Dim == 768 ? "v2" : idx.Dim == 256 ? "v1" : "different")} model, but {v.Name} is a {v.Version} model.");
        Directory.CreateDirectory(Folder);
        var dest = Path.Combine(Folder, v.Id + ".index");
        System.IO.File.Copy(indexPath, dest + ".tmp", overwrite: true);
        System.IO.File.Move(dest + ".tmp", dest, overwrite: true);
        v.IndexFile = dest;
    }

    public static void RemoveIndex(AiVoice v)
    {
        try { if (v.IndexFile != null && v.IndexFile.StartsWith(Folder, StringComparison.OrdinalIgnoreCase)) System.IO.File.Delete(v.IndexFile); } catch { }
        v.IndexFile = null;
    }
}

/// <summary>
/// Writes an ONNX model with chosen initializers filled in, by rewriting the protobuf directly
/// (ModelProto.graph = field 7, GraphProto.initializer = field 5, TensorProto: dims 1,
/// data_type 2, name 8, raw_data 9). Everything else is copied unchanged.
/// </summary>
public static class OnnxFill
{
    public static void Fill(byte[] model, IReadOnlyDictionary<string, PthTensor> weights, Stream output, string? allowFirstDimChange = null)
    {
        var used = new HashSet<string>();
        var w = new ProtoWriter(output);
        foreach (var f in ProtoReader.Fields(model, 0, model.Length))
        {
            if (f.Number == 7 && f.WireType == 2)
            {
                var graph = new MemoryStream();
                var gw = new ProtoWriter(graph);
                foreach (var g in ProtoReader.Fields(model, f.Start, f.End))
                {
                    if (g.Number == 5 && g.WireType == 2)
                    {
                        var t = ReadTensor(model, g.Start, g.End);
                        if (weights.TryGetValue(t.Name, out var data))
                        {
                            CheckShape(t, data, allowFirstDimChange);
                            gw.Bytes(5, TensorBytes(t.Name, data));
                            used.Add(t.Name);
                            continue;
                        }
                    }
                    gw.Raw(model, g);
                }
                w.Bytes(7, graph.ToArray());
            }
            else w.Raw(model, f);
        }
        var missing = weights.Keys.Where(k => !used.Contains(k)).ToList();
        if (missing.Count > 0) throw new InvalidDataException($"The voice template has no slot for {missing[0]}.");
    }

    sealed record TensorInfo(string Name, long[] Dims, int DataType);

    static TensorInfo ReadTensor(byte[] b, int start, int end)
    {
        string name = ""; var dims = new List<long>(); int type = 0;
        foreach (var f in ProtoReader.Fields(b, start, end))
        {
            if (f.Number == 8 && f.WireType == 2) name = System.Text.Encoding.UTF8.GetString(b, f.Start, f.End - f.Start);
            else if (f.Number == 2 && f.WireType == 0) type = (int)f.Value;
            else if (f.Number == 1 && f.WireType == 0) dims.Add((long)f.Value);
            else if (f.Number == 1 && f.WireType == 2) { int p = f.Start; while (p < f.End) dims.Add((long)ProtoReader.Varint(b, ref p)); }
        }
        return new TensorInfo(name, dims.ToArray(), type);
    }

    static void CheckShape(TensorInfo t, PthTensor data, string? allowFirstDimChange)
    {
        bool ok = t.Dims.Length == data.Shape.Length;
        for (int i = 0; ok && i < t.Dims.Length; i++)
            if (t.Dims[i] != data.Shape[i] && !(i == 0 && t.Name == allowFirstDimChange)) ok = false;
        if (!ok) throw new InvalidDataException($"Part of the voice model ({t.Name}) has an unexpected size ({string.Join("x", data.Shape)}, expected {string.Join("x", t.Dims)}).");
        if (t.DataType != 1) throw new InvalidDataException($"Unexpected template slot type for {t.Name}.");
    }

    static byte[] TensorBytes(string name, PthTensor data)
    {
        var ms = new MemoryStream();
        var w = new ProtoWriter(ms);
        var dims = new MemoryStream();
        foreach (var d in data.Shape) ProtoWriter.WriteVarint(dims, (ulong)d);
        w.Bytes(1, dims.ToArray());
        w.Varint(2, 1); // FLOAT
        w.Bytes(8, System.Text.Encoding.UTF8.GetBytes(name));
        var raw = new byte[data.Data.Length * 4];
        Buffer.BlockCopy(data.Data, 0, raw, 0, raw.Length);
        w.Bytes(9, raw);
        return ms.ToArray();
    }
}

readonly record struct ProtoField(int Number, int WireType, int HeaderStart, int Start, int End, ulong Value);

static class ProtoReader
{
    public static ulong Varint(byte[] b, ref int p)
    {
        ulong v = 0; int shift = 0;
        while (true)
        {
            byte x = b[p++];
            v |= (ulong)(x & 0x7f) << shift;
            if (x < 0x80) return v;
            shift += 7;
            if (shift > 63) throw new InvalidDataException("Damaged model template.");
        }
    }

    public static IEnumerable<ProtoField> Fields(byte[] b, int start, int end)
    {
        int p = start;
        while (p < end)
        {
            int h = p;
            ulong key = Varint(b, ref p);
            int num = (int)(key >> 3), wt = (int)(key & 7);
            switch (wt)
            {
                case 0: { ulong v = Varint(b, ref p); yield return new ProtoField(num, wt, h, h, p, v); break; }
                case 1: p += 8; yield return new ProtoField(num, wt, h, p - 8, p, 0); break;
                case 5: p += 4; yield return new ProtoField(num, wt, h, p - 4, p, 0); break;
                case 2:
                {
                    int len = (int)Varint(b, ref p);
                    yield return new ProtoField(num, wt, h, p, p + len, 0);
                    p += len;
                    break;
                }
                default: throw new InvalidDataException("Damaged model template.");
            }
        }
    }
}

sealed class ProtoWriter(Stream s)
{
    public static void WriteVarint(Stream s, ulong v)
    {
        while (v >= 0x80) { s.WriteByte((byte)(v | 0x80)); v >>= 7; }
        s.WriteByte((byte)v);
    }

    public void Varint(int field, ulong v) { WriteVarint(s, (ulong)(field << 3)); WriteVarint(s, v); }

    public void Bytes(int field, byte[] data)
    {
        WriteVarint(s, (ulong)((field << 3) | 2));
        WriteVarint(s, (ulong)data.Length);
        s.Write(data);
    }

    /// <summary>Copies a field exactly as it was (header included).</summary>
    public void Raw(byte[] b, ProtoField f)
    {
        int end = f.WireType == 0 ? f.End : f.End;
        s.Write(b, f.HeaderStart, end - f.HeaderStart);
    }
}
