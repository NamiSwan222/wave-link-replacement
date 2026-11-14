using System.IO;

namespace WaveFix.Rvc;

/// <summary>
/// An RVC feature index (the ".index" file next to a voice model): the voice's training features,
/// grouped by FAISS for fast nearest-neighbour search. RVC blends each frame of your speech
/// features toward the closest training features, which makes the result sound more like the
/// original speaker (accent, timbre).
///
/// Reads FAISS's IndexIVFFlat file format (what RVC's training writes): a coarse quantizer
/// (IndexFlatL2 centroids) and inverted lists holding the vectors. Data only; nothing in the file
/// is run. Search matches FAISS: squared L2 distance, probing the file's nprobe nearest lists.
/// </summary>
public sealed class RvcIndex
{
    public int Dim { get; }
    public long Count { get; }
    readonly int _nprobe;
    readonly float[] _centroids;      // [nlist * dim]
    readonly float[][] _lists;        // per list: [n * dim]
    readonly int _nlist;

    RvcIndex(int dim, long count, int nprobe, float[] centroids, float[][] lists)
    {
        Dim = dim; Count = count; _nprobe = Math.Max(1, nprobe); _centroids = centroids; _lists = lists; _nlist = lists.Length;
    }

    static uint FourCC(string s) => (uint)(s[0] | s[1] << 8 | s[2] << 16 | s[3] << 24);

    public static RvcIndex Load(string path)
    {
        using var f = File.OpenRead(path);
        using var r = new BinaryReader(f);
        uint h = r.ReadUInt32();
        if (h != FourCC("IwFl"))
            throw new InvalidDataException(h == FourCC("IwPQ") || h == FourCC("IwSh") || h == FourCC("IwSQ")
                ? "This index uses a compressed format WaveFix doesn't read. Use the \"added_...\" index RVC makes with its default settings."
                : "This doesn't look like an RVC feature index (.index).");
        var (dim, ntotal, metric) = Header(r);
        if (metric != 1) throw new InvalidDataException("This index doesn't use the distance RVC uses.");
        long nlist = r.ReadInt64();
        long nprobe = r.ReadInt64();

        // Coarse quantizer: an IndexFlatL2 holding one centroid per list.
        uint qh = r.ReadUInt32();
        if (qh != FourCC("IxF2") && qh != FourCC("IxFl")) throw new InvalidDataException("Unsupported index layout (quantizer).");
        var (qd, qn, _) = Header(r);
        if (qd != dim || qn != nlist) throw new InvalidDataException("The index's parts don't match.");
        var centroids = ReadFloatVector(r, qn * qd);

        // Direct map (not needed for search): type byte + id array (+ hash table).
        byte dmType = r.ReadByte();
        long dmCount = r.ReadInt64();
        f.Seek(dmCount * 8, SeekOrigin.Current);
        if (dmType == 2) { long hc = r.ReadInt64(); f.Seek(hc * 16, SeekOrigin.Current); }

        // Inverted lists (ArrayInvertedLists).
        uint ih = r.ReadUInt32();
        if (ih != FourCC("ilar")) throw new InvalidDataException("Unsupported index layout (lists).");
        long nl = r.ReadInt64();
        long codeSize = r.ReadInt64();
        if (nl != nlist || codeSize != dim * 4L) throw new InvalidDataException("Unsupported index layout (codes).");
        uint listType = r.ReadUInt32();
        long sizesLen = r.ReadInt64();
        var sizes = new long[nlist];
        if (listType == FourCC("full"))
        {
            if (sizesLen != nlist) throw new InvalidDataException("Damaged index (list sizes).");
            for (int i = 0; i < nlist; i++) sizes[i] = r.ReadInt64();
        }
        else if (listType == FourCC("sprs"))
        {
            for (long i = 0; i < sizesLen / 2; i++) { long li = r.ReadInt64(); long n = r.ReadInt64(); if (li < 0 || li >= nlist) throw new InvalidDataException("Damaged index."); sizes[li] = n; }
        }
        else throw new InvalidDataException("Unsupported index layout (list type).");

        var lists = new float[nlist][];
        long total = 0;
        for (int i = 0; i < nlist; i++)
        {
            long n = sizes[i];
            if (n < 0 || n > ntotal) throw new InvalidDataException("Damaged index (list size).");
            lists[i] = n == 0 ? Array.Empty<float>() : ReadFloats(r, n * dim);
            if (n > 0) f.Seek(n * 8, SeekOrigin.Current); // ids: not needed (vectors are kept with their list)
            total += n;
        }
        if (total != ntotal) throw new InvalidDataException("Damaged index (count).");
        return new RvcIndex(dim, ntotal, (int)nprobe, centroids, lists);
    }

    static (int dim, long ntotal, int metric) Header(BinaryReader r)
    {
        int d = r.ReadInt32();
        long ntotal = r.ReadInt64();
        r.ReadInt64(); r.ReadInt64();       // two unused fields
        r.ReadByte();                       // is_trained
        int metric = r.ReadInt32();
        if (metric > 1) r.ReadSingle();     // metric_arg
        if (d <= 0 || d > 4096 || ntotal < 0) throw new InvalidDataException("Damaged index header.");
        return (d, ntotal, metric);
    }

    /// <summary>A stored vector: either a byte vector (newer FAISS) or a float vector (older).</summary>
    static float[] ReadFloatVector(BinaryReader r, long floats)
    {
        long n = r.ReadInt64();
        if (n == floats * 4) return ReadFloats(r, floats);
        if (n == floats) return ReadFloats(r, floats);
        throw new InvalidDataException("Damaged index (vector size).");
    }

    static float[] ReadFloats(BinaryReader r, long count)
    {
        if (count > int.MaxValue / 4) throw new InvalidDataException("This index is too large.");
        var bytes = r.ReadBytes((int)(count * 4));
        if (bytes.Length != count * 4) throw new InvalidDataException("The index file is cut short.");
        var f = new float[count];
        Buffer.BlockCopy(bytes, 0, f, 0, bytes.Length);
        return f;
    }

    static float L2(ReadOnlySpan<float> a, ReadOnlySpan<float> b)
    {
        int n = a.Length, i = 0;
        var acc = System.Numerics.Vector<float>.Zero;
        int w = System.Numerics.Vector<float>.Count;
        for (; i <= n - w; i += w)
        {
            var d = new System.Numerics.Vector<float>(a.Slice(i, w)) - new System.Numerics.Vector<float>(b.Slice(i, w));
            acc += d * d;
        }
        float s = System.Numerics.Vector.Dot(acc, System.Numerics.Vector<float>.One);
        for (; i < n; i++) { float d = a[i] - b[i]; s += d * d; }
        return s;
    }

    /// <summary>The k nearest stored vectors (squared L2), nearest first; fewer if not enough are searched.</summary>
    public List<(float Dist, int List, int Row)> Search(ReadOnlySpan<float> q, int k = 8)
    {
        // Nearest lists.
        var probes = new List<(float, int)>(_nlist);
        for (int c = 0; c < _nlist; c++) probes.Add((L2(q, _centroids.AsSpan(c * Dim, Dim)), c));
        probes.Sort((a, b) => a.Item1.CompareTo(b.Item1));
        var best = new List<(float Dist, int List, int Row)>(k + 1);
        for (int p = 0; p < Math.Min(_nprobe, probes.Count); p++)
        {
            int li = probes[p].Item2;
            var data = _lists[li];
            int n = data.Length / Dim;
            for (int j = 0; j < n; j++)
            {
                float d = L2(q, data.AsSpan(j * Dim, Dim));
                if (best.Count < k || d < best[^1].Dist)
                {
                    int at = best.FindIndex(b => b.Dist > d);
                    if (at < 0) at = best.Count;
                    best.Insert(at, (d, li, j));
                    if (best.Count > k) best.RemoveAt(k);
                }
            }
        }
        return best;
    }

    /// <summary>
    /// RVC's index blending for one frame: the weighted mean of the 8 nearest training features
    /// (weights 1/distance², normalised), mixed in by <paramref name="rate"/>. Returns false (frame
    /// unchanged) when fewer than 8 neighbours are found, as RVC does.
    /// </summary>
    public bool Blend(Span<float> frame, float rate)
    {
        var nn = Search(frame, 8);
        if (nn.Count < 8) return false;
        var mix = new float[Dim];
        double wsum = 0;
        var w = new double[nn.Count];
        for (int i = 0; i < nn.Count; i++) { double s = Math.Max(nn[i].Dist, 1e-12); w[i] = 1.0 / (s * s); wsum += w[i]; }
        for (int i = 0; i < nn.Count; i++)
        {
            var v = _lists[nn[i].List].AsSpan(nn[i].Row * Dim, Dim);
            float wi = (float)(w[i] / wsum);
            for (int d = 0; d < Dim; d++) mix[d] += v[d] * wi;
        }
        for (int d = 0; d < Dim; d++) frame[d] = mix[d] * rate + frame[d] * (1 - rate);
        return true;
    }
}
