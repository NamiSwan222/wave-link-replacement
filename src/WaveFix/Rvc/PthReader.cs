using System.IO;
using System.IO.Compression;
using System.Text;

namespace WaveFix.Rvc;

/// <summary>A tensor read from a .pth file, as float32 (row-major).</summary>
public sealed record PthTensor(long[] Shape, float[] Data);

/// <summary>
/// Reads RVC voice models (.pth, PyTorch's zip format) without Python and without running anything
/// from the file. A .pth is a zip holding a pickle (the structure) plus one raw file per tensor.
/// Pickles can name any Python function to call; this reader understands only the few that
/// PyTorch uses to describe tensors and dictionaries, and never calls anything, so a booby-trapped
/// model can't do harm (unlike loading it in Python with torch.load).
/// </summary>
public static class PthReader
{
    /// <summary>The top-level object of the .pth (for RVC: a dictionary with "weight", "config", ...).</summary>
    public static object? Load(string path, out Func<object?, PthTensor?> tensor)
    {
        var zip = ZipFile.OpenRead(path);
        var pkl = zip.Entries.FirstOrDefault(e => e.FullName.EndsWith("data.pkl", StringComparison.Ordinal))
            ?? throw new InvalidDataException(
                "This isn't a PyTorch model file WaveFix can read (no data.pkl inside). Very old RVC models (saved before 2021) aren't supported; open and re-save the model in a recent RVC.");
        string prefix = pkl.FullName[..^"data.pkl".Length];
        byte[] pickle;
        using (var s = pkl.Open()) { using var ms = new MemoryStream(); s.CopyTo(ms); pickle = ms.ToArray(); }

        var root = new Unpickler(pickle).Run();
        tensor = obj =>
        {
            if (obj is not TensorRef t) return null;
            var entry = zip.GetEntry(prefix + "data/" + t.Key) ?? throw new InvalidDataException($"Tensor data {t.Key} is missing from the model file.");
            return t.Read(entry);
        };
        return root;
    }

    /// <summary>A dictionary from the pickle (string keys).</summary>
    public static Dictionary<string, object?>? Dict(object? o) => o is PickleDict d ? d.Items : null;

    /// <summary>A tensor storage reference: which file in the zip, its element type, and the view.</summary>
    sealed record TensorRef(string Key, string DType, long Offset, long[] Shape, long[] Stride)
    {
        public PthTensor Read(ZipArchiveEntry entry)
        {
            int size = DType switch { "HalfStorage" or "BFloat16Storage" => 2, "FloatStorage" => 4, "DoubleStorage" => 8,
                _ => throw new InvalidDataException($"Unsupported tensor type {DType}.") };
            byte[] raw;
            using (var s = entry.Open()) { using var ms = new MemoryStream(); s.CopyTo(ms); raw = ms.ToArray(); }
            long count = Shape.Aggregate(1L, (a, b) => a * b);
            var data = new float[count];
            var idx = new long[Shape.Length];
            for (long n = 0; n < count; n++)
            {
                long el = Offset;
                for (int d = 0; d < Shape.Length; d++) el += idx[d] * Stride[d];
                long b = el * size;
                if (b + size > raw.Length) throw new InvalidDataException("Tensor data is shorter than its shape says.");
                data[n] = DType switch
                {
                    "HalfStorage" => (float)BitConverter.ToHalf(raw, (int)b),
                    "BFloat16Storage" => BitConverter.Int32BitsToSingle(BitConverter.ToUInt16(raw, (int)b) << 16),
                    "FloatStorage" => BitConverter.ToSingle(raw, (int)b),
                    _ => (float)BitConverter.ToDouble(raw, (int)b),
                };
                for (int d = Shape.Length - 1; d >= 0; d--) { if (++idx[d] < Shape[d]) break; idx[d] = 0; }
            }
            return new PthTensor(Shape, data);
        }
    }

    sealed class PickleDict { public Dictionary<string, object?> Items { get; } = new(); }
    sealed record Global(string Module, string Name);
    sealed record Reduced(Global Func, object?[] Args);
    sealed class Mark { public static readonly Mark Instance = new(); }

    /// <summary>A data-only pickle interpreter (protocol 2+, the opcodes torch.save writes).</summary>
    sealed class Unpickler
    {
        readonly byte[] _b;
        int _p;
        readonly List<object?> _stack = new();
        readonly Dictionary<long, object?> _memo = new();

        public Unpickler(byte[] b) { _b = b; }

        byte U8() => _b[_p++];
        int I32() { int v = BitConverter.ToInt32(_b, _p); _p += 4; return v; }
        string Str(int n) { var s = Encoding.UTF8.GetString(_b, _p, n); _p += n; return s; }
        string Line() { int e = Array.IndexOf(_b, (byte)'\n', _p); var s = Encoding.ASCII.GetString(_b, _p, e - _p); _p = e + 1; return s; }
        object? Pop() { var o = _stack[^1]; _stack.RemoveAt(_stack.Count - 1); return o; }
        object? Top => _stack[^1];
        List<object?> PopMark()
        {
            int m = _stack.FindLastIndex(o => ReferenceEquals(o, Mark.Instance));
            if (m < 0) throw new InvalidDataException("Damaged model file (pickle mark missing).");
            var items = _stack.GetRange(m + 1, _stack.Count - m - 1);
            _stack.RemoveRange(m, _stack.Count - m);
            return items;
        }

        public object? Run()
        {
            while (_p < _b.Length)
            {
                byte op = U8();
                switch (op)
                {
                    case 0x80: _p++; break;                                   // PROTO
                    case 0x95: _p += 8; break;                                // FRAME
                    case (byte)'.': return Pop();                             // STOP
                    case (byte)'}': _stack.Add(new PickleDict()); break;      // EMPTY_DICT
                    case (byte)']': _stack.Add(new List<object?>()); break;   // EMPTY_LIST
                    case (byte)')': _stack.Add(Array.Empty<object?>()); break; // EMPTY_TUPLE
                    case 0x8f: _stack.Add(new List<object?>()); break;        // EMPTY_SET
                    case (byte)'(': _stack.Add(Mark.Instance); break;         // MARK
                    case (byte)'N': _stack.Add(null); break;
                    case 0x88: _stack.Add(true); break;
                    case 0x89: _stack.Add(false); break;
                    case (byte)'J': _stack.Add((long)I32()); break;           // BININT
                    case (byte)'K': _stack.Add((long)U8()); break;            // BININT1
                    case (byte)'M': _stack.Add((long)BitConverter.ToUInt16(_b, (_p += 2) - 2)); break; // BININT2
                    case 0x8a:                                                // LONG1
                    {
                        int n = U8(); long v = 0;
                        for (int i = 0; i < n && i < 8; i++) v |= (long)_b[_p + i] << (8 * i);
                        if (n > 0 && n < 8 && (_b[_p + n - 1] & 0x80) != 0) v -= 1L << (8 * n);
                        _p += n; _stack.Add(v); break;
                    }
                    case (byte)'G':                                           // BINFLOAT (big endian)
                    {
                        var bytes = _b.AsSpan(_p, 8).ToArray(); Array.Reverse(bytes); _p += 8;
                        _stack.Add(BitConverter.ToDouble(bytes)); break;
                    }
                    case (byte)'X': { int n = I32(); _stack.Add(Str(n)); break; }       // BINUNICODE
                    case 0x8c: { int n = U8(); _stack.Add(Str(n)); break; }             // SHORT_BINUNICODE
                    case 0x8d: { long n = BitConverter.ToInt64(_b, _p); _p += 8; _stack.Add(Str((int)n)); break; } // BINUNICODE8
                    case (byte)'U': { int n = U8(); _stack.Add(Encoding.Latin1.GetString(_b, _p, n)); _p += n; break; } // SHORT_BINSTRING
                    case (byte)'T': { int n = I32(); _stack.Add(Encoding.Latin1.GetString(_b, _p, n)); _p += n; break; } // BINSTRING
                    case (byte)'C': { int n = U8(); _stack.Add(_b.AsSpan(_p, n).ToArray()); _p += n; break; } // SHORT_BINBYTES
                    case (byte)'B': { int n = I32(); _stack.Add(_b.AsSpan(_p, n).ToArray()); _p += n; break; } // BINBYTES
                    case (byte)'t': _stack.Add(PopMark().ToArray()); break;   // TUPLE
                    case 0x85: _stack.Add(new[] { Pop() }); break;            // TUPLE1
                    case 0x86: { var b = Pop(); var a = Pop(); _stack.Add(new[] { a, b }); break; }
                    case 0x87: { var c = Pop(); var b = Pop(); var a = Pop(); _stack.Add(new[] { a, b, c }); break; }
                    case (byte)'l': _stack.Add(PopMark()); break;             // LIST
                    case (byte)'a': { var v = Pop(); ((List<object?>)Top!).Add(v); break; } // APPEND
                    case (byte)'e': { var items = PopMark(); ((List<object?>)Top!).AddRange(items); break; } // APPENDS
                    case 0x90: { var items = PopMark(); if (Top is List<object?> l) l.AddRange(items); break; } // ADDITEMS
                    case (byte)'d': { var items = PopMark(); var d = new PickleDict(); SetItems(d, items); _stack.Add(d); break; } // DICT
                    case (byte)'s': { var v = Pop(); var k = Pop(); SetItems(Top, new List<object?> { k, v }); break; } // SETITEM
                    case (byte)'u': { var items = PopMark(); SetItems(Top, items); break; } // SETITEMS
                    case (byte)'q': _memo[U8()] = Top; break;                 // BINPUT
                    case (byte)'r': _memo[(uint)I32()] = Top; break;          // LONG_BINPUT
                    case 0x94: _memo[_memo.Count] = Top; break;               // MEMOIZE
                    case (byte)'h': _stack.Add(_memo[U8()]); break;           // BINGET
                    case (byte)'j': _stack.Add(_memo[(uint)I32()]); break;    // LONG_BINGET
                    case (byte)'c': { var m = Line(); var n = Line(); _stack.Add(new Global(m, n)); break; } // GLOBAL
                    case 0x93: { var n = (string)Pop()!; var m = (string)Pop()!; _stack.Add(new Global(m, n)); break; } // STACK_GLOBAL
                    case (byte)'Q': _stack.Add(PersistentLoad(Pop())); break;  // BINPERSID
                    case (byte)'R':                                            // REDUCE
                    {
                        var args = Pop() as object?[] ?? Array.Empty<object?>();
                        var f = Pop() as Global ?? throw new InvalidDataException("Damaged model file (bad call).");
                        _stack.Add(Reduce(f, args));
                        break;
                    }
                    case 0x81:                                                 // NEWOBJ
                    {
                        var args = Pop() as object?[] ?? Array.Empty<object?>();
                        var cls = Pop() as Global ?? throw new InvalidDataException("Damaged model file (bad object).");
                        _stack.Add(Reduce(cls, args));
                        break;
                    }
                    case (byte)'b': Pop(); break;                              // BUILD: state ignored (data only)
                    case (byte)'0': Pop(); break;                              // POP
                    case (byte)'1': PopMark(); break;                          // POP_MARK
                    case (byte)'2': _stack.Add(Top); break;                    // DUP
                    default:
                        throw new InvalidDataException($"The model file uses something WaveFix doesn't read (pickle opcode 0x{op:x2}).");
                }
            }
            throw new InvalidDataException("Damaged model file (pickle ended early).");
        }

        static void SetItems(object? target, List<object?> items)
        {
            if (target is not PickleDict d) return;
            for (int i = 0; i + 1 < items.Count; i += 2) d.Items[items[i]?.ToString() ?? ""] = items[i + 1];
        }

        static object? PersistentLoad(object? pid)
        {
            // ('storage', <storage class>, key, location, numel)
            if (pid is object?[] t && t.Length >= 3 && t[0] is "storage" && t[1] is Global g && t[2] is string key)
                return new StorageRef(g.Name, key);
            throw new InvalidDataException("The model file has data WaveFix doesn't recognize.");
        }

        static object? Reduce(Global f, object?[] args)
        {
            string name = f.Module + "." + f.Name;
            switch (name)
            {
                case "collections.OrderedDict":
                    return new PickleDict();
                case "torch._utils._rebuild_tensor_v2":
                case "torch._utils._rebuild_tensor":
                {
                    if (args.Length < 4 || args[0] is not StorageRef s) throw new InvalidDataException("Damaged model file (tensor).");
                    long offset = Convert.ToInt64(args[1]);
                    var shape = ((object?[])args[2]!).Select(Convert.ToInt64).ToArray();
                    var stride = ((object?[])args[3]!).Select(Convert.ToInt64).ToArray();
                    return new TensorRef(s.Key, s.Type, offset, shape, stride);
                }
                case "torch._utils._rebuild_parameter":
                    return args.Length > 0 ? args[0] : null;
                default:
                    // Anything else (e.g. a custom class) is kept as an inert record and never called.
                    return new Reduced(f, args);
            }
        }

        sealed record StorageRef(string Type, string Key);
    }
}
