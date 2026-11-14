using System.Runtime.InteropServices;
using System.Text;

namespace WaveFix;

/// <summary>
/// The saved settings of one plugin: plugins that save "chunks" use ChunkData (base64), the others
/// a map of parameter name → value (0..1). WaveFix's engine applies them the same way.
/// </summary>
public sealed class PluginState
{
    public string? ChunkData { get; set; }
    public Dictionary<string, float>? Params { get; set; }

    public bool IsEmpty => string.IsNullOrEmpty(ChunkData) && (Params == null || Params.Count == 0);

    public bool SameAs(PluginState? o) =>
        o != null && ChunkData == o.ChunkData &&
        (Params?.Count ?? 0) == (o.Params?.Count ?? 0) &&
        (Params == null || Params.All(kv => o.Params!.TryGetValue(kv.Key, out var v) && Math.Abs(v - kv.Value) < 1e-6f));
}

/// <summary>
/// Minimal VST2 host: loads a plugin DLL so its settings window (or its parameters) can be edited.
/// WaveFix never sends audio through it; the audio engine runs its own copy with the saved state.
/// One plugin per process (the editor runs in its own WaveFix process).
/// </summary>
public sealed unsafe class VstPlugin : IDisposable
{
    [DllImport("kernel32", SetLastError = true, CharSet = CharSet.Unicode)] static extern IntPtr LoadLibraryW(string path);
    [DllImport("kernel32", CharSet = CharSet.Ansi)] static extern IntPtr GetProcAddress(IntPtr h, string name);

    const int kEffectMagic = 0x56737450; // 'VstP'
    const int effFlagsHasEditor = 1 << 0, effFlagsProgramChunks = 1 << 5;
    const int effOpen = 0, effClose = 1, effGetParamLabel = 6, effGetParamDisplay = 7, effGetParamName = 8,
              effSetSampleRate = 10, effSetBlockSize = 11, effMainsChanged = 12, effEditGetRect = 13, effEditOpen = 14,
              effEditClose = 15, effEditIdle = 19, effGetChunk = 23, effSetChunk = 24, effGetEffectName = 45,
              effGetVendorString = 47, effGetProductString = 48;

    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    delegate IntPtr HostCallback(IntPtr effect, int opcode, int index, IntPtr value, IntPtr ptr, float opt);
    static readonly HostCallback Callback = OnHost; // kept alive for the process lifetime
    static readonly IntPtr CallbackPtr = Marshal.GetFunctionPointerForDelegate(Callback);

    /// <summary>The plugin asked to resize its window (width, height in pixels).</summary>
    public static Action<int, int>? SizeWindowRequested;
    /// <summary>The plugin changed a parameter itself (knob turned in its window).</summary>
    public static Action? Automated;

    readonly IntPtr _effect;
    readonly delegate* unmanaged[Cdecl]<IntPtr, int, int, IntPtr, IntPtr, float, IntPtr> _dispatch;
    readonly delegate* unmanaged[Cdecl]<IntPtr, int, float, void> _setParam;
    readonly delegate* unmanaged[Cdecl]<IntPtr, int, float> _getParam;

    VstPlugin(IntPtr effect)
    {
        _effect = effect;
        _dispatch = (delegate* unmanaged[Cdecl]<IntPtr, int, int, IntPtr, IntPtr, float, IntPtr>)Marshal.ReadIntPtr(effect, 8);
        _setParam = (delegate* unmanaged[Cdecl]<IntPtr, int, float, void>)Marshal.ReadIntPtr(effect, 24);
        _getParam = (delegate* unmanaged[Cdecl]<IntPtr, int, float>)Marshal.ReadIntPtr(effect, 32);
    }

    public static VstPlugin Load(string path)
    {
        var lib = LoadLibraryW(path);
        if (lib == IntPtr.Zero) throw new InvalidOperationException($"Windows couldn't load the plugin (error {Marshal.GetLastWin32Error()}).");
        var entry = GetProcAddress(lib, "VSTPluginMain");
        if (entry == IntPtr.Zero) entry = GetProcAddress(lib, "main");
        if (entry == IntPtr.Zero) throw new InvalidOperationException("This file isn't a VST2 plugin.");
        var main = (delegate* unmanaged[Cdecl]<IntPtr, IntPtr>)entry;
        var effect = main(CallbackPtr);
        if (effect == IntPtr.Zero || Marshal.ReadInt32(effect) != kEffectMagic) throw new InvalidOperationException("The plugin didn't start.");
        var p = new VstPlugin(effect);
        p.Dispatch(effOpen);
        p.Dispatch(effSetSampleRate, opt: 48000f);
        p.Dispatch(effSetBlockSize, value: 512);
        return p;
    }

    IntPtr Dispatch(int op, int index = 0, nint value = 0, IntPtr ptr = default, float opt = 0f) =>
        _dispatch(_effect, op, index, value, ptr, opt);

    int Flags => Marshal.ReadInt32(_effect, 56);
    public int NumParams => Marshal.ReadInt32(_effect, 44);
    public bool HasEditor => (Flags & effFlagsHasEditor) != 0;
    public bool UsesChunks => (Flags & effFlagsProgramChunks) != 0;

    string GetString(int op, int index = 0)
    {
        var buf = Marshal.AllocHGlobal(512);
        try
        {
            new Span<byte>((void*)buf, 512).Clear();
            Dispatch(op, index, 0, buf);
            var bytes = new ReadOnlySpan<byte>((void*)buf, 511);
            int len = bytes.IndexOf((byte)0);
            return Encoding.UTF8.GetString(bytes[..(len < 0 ? 511 : len)]).Trim();
        }
        finally { Marshal.FreeHGlobal(buf); }
    }

    public string Name
    {
        get
        {
            var n = GetString(effGetEffectName);
            return n.Length > 0 ? n : GetString(effGetProductString);
        }
    }
    public string Vendor => GetString(effGetVendorString);
    public string ParamName(int i) => GetString(effGetParamName, i);
    public string ParamDisplay(int i) => (GetString(effGetParamDisplay, i) + " " + GetString(effGetParamLabel, i)).Trim();
    public float GetParam(int i) => _getParam(_effect, i);
    public void SetParam(int i, float v) => _setParam(_effect, i, v);

    /// <summary>Applies saved settings, exactly like WaveFix's engine does before processing.</summary>
    public void WriteState(PluginState? s)
    {
        if (s == null || s.IsEmpty) return;
        if (UsesChunks)
        {
            if (string.IsNullOrEmpty(s.ChunkData)) return;
            var bytes = Convert.FromBase64String(s.ChunkData);
            fixed (byte* b = bytes) Dispatch(effSetChunk, 1, bytes.Length, (IntPtr)b);
        }
        else if (s.Params != null)
        {
            for (int i = 0; i < NumParams; i++)
                if (s.Params.TryGetValue(ParamName(i), out var v)) SetParam(i, v);
        }
    }

    /// <summary>Reads the current settings (chunk or parameters).</summary>
    public PluginState ReadState()
    {
        if (UsesChunks)
        {
            IntPtr chunk = IntPtr.Zero;
            int size = (int)Dispatch(effGetChunk, 1, 0, (IntPtr)(&chunk));
            if (size <= 0 || chunk == IntPtr.Zero) return new PluginState();
            return new PluginState { ChunkData = Convert.ToBase64String(new ReadOnlySpan<byte>((void*)chunk, size)) };
        }
        var map = new Dictionary<string, float>();
        for (int i = 0; i < NumParams; i++) map[ParamName(i)] = GetParam(i);
        return new PluginState { Params = map };
    }

    [StructLayout(LayoutKind.Sequential)] struct VstRect { public short Top, Left, Bottom, Right; }

    /// <summary>Opens the plugin's own window inside <paramref name="parent"/>; returns its size in pixels.</summary>
    public (int width, int height) OpenEditor(IntPtr parent)
    {
        VstRect* rect = null;
        Dispatch(effEditGetRect, 0, 0, (IntPtr)(&rect));
        Dispatch(effEditOpen, 0, 0, parent);
        Dispatch(effEditGetRect, 0, 0, (IntPtr)(&rect));
        return rect == null ? (400, 300) : (rect->Right - rect->Left, rect->Bottom - rect->Top);
    }

    public void EditorIdle() => Dispatch(effEditIdle);
    public void CloseEditor() => Dispatch(effEditClose);

    public void Dispose()
    {
        try { Dispatch(effMainsChanged, 0, 0); Dispatch(effClose); } catch { }
        // The DLL stays loaded: some plugins crash when unloaded, and this process exits soon anyway.
    }

    static IntPtr OnHost(IntPtr effect, int opcode, int index, IntPtr value, IntPtr ptr, float opt)
    {
        switch (opcode)
        {
            case 0: Automated?.Invoke(); return 0;                          // audioMasterAutomate
            case 1: return 2400;                                            // audioMasterVersion
            case 15: SizeWindowRequested?.Invoke(index, (int)value); return 1; // audioMasterSizeWindow
            case 16: return 48000;                                          // audioMasterGetSampleRate
            case 17: return 512;                                            // audioMasterGetBlockSize
            case 32: case 33:                                               // vendor / product string
                if (ptr != IntPtr.Zero) { var b = Encoding.ASCII.GetBytes("WaveFix\0"); Marshal.Copy(b, 0, ptr, b.Length); }
                return 1;
            case 37:                                                        // audioMasterCanDo
                var s = ptr != IntPtr.Zero ? Marshal.PtrToStringAnsi(ptr) : null;
                return s == "sizeWindow" ? 1 : 0;
            case 42: return 1;                                              // audioMasterUpdateDisplay
            default: return 0;
        }
    }
}
