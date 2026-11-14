using System.Runtime.InteropServices;

namespace WaveFix;

/// <summary>
/// Per-app output device, the same mechanism as Windows Settings → Volume mixer → "Output device".
/// Uses the undocumented Windows.Media.Internal.AudioPolicyConfig factory (Windows 10 21H2+ / 11),
/// as EarTrumpet does. Every call is guarded: if Windows changes the interface, routing is simply
/// reported as unavailable instead of crashing.
/// </summary>
public static class AudioPolicyConfig
{
    const string RenderInterface = "{e6327cad-dcec-4949-ae8a-991e976a79d2}";
    const string DevicePrefix = @"\\?\SWD#MMDEVAPI#";

    [ComImport, Guid("ab3d4648-e242-459f-b02f-541c70306324"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    interface IAudioPolicyConfigFactory
    {
        // IInspectable
        void GetIids(); void GetRuntimeClassName(); void GetTrustLevel();
        // Methods we don't call, kept for vtable order
        void add_CtxVolumeChange(); void remove_CtxVolumeChanged();
        void add_RingerVibrateStateChanged(); void remove_RingerVibrateStateChange();
        void SetVolumeGroupGainForId(); void GetVolumeGroupGainForId();
        void GetActiveVolumeGroupForEndpointId(); void GetVolumeGroupsForEndpoint();
        void GetCurrentVolumeContext(); void SetVolumeGroupMuteForId(); void GetVolumeGroupMuteForId();
        void SetRingerVibrateState(); void GetRingerVibrateState();
        void SetPreferredChatApplication(); void ResetPreferredChatApplication(); void GetPreferredChatApplication();
        void GetCurrentChatApplications(); void add_ChatContextChanged(); void remove_ChatContextChanged();
        [PreserveSig] int SetPersistedDefaultAudioEndpoint(uint processId, int flow, int role, IntPtr deviceIdHString);
        [PreserveSig] int GetPersistedDefaultAudioEndpoint(uint processId, int flow, int role, out IntPtr deviceIdHString);
        [PreserveSig] int ClearAllPersistedApplicationDefaultEndpoints();
    }

    [DllImport("combase.dll", PreserveSig = false)]
    static extern void RoGetActivationFactory(IntPtr classId, [In] ref Guid iid, [MarshalAs(UnmanagedType.IUnknown)] out object factory);
    [DllImport("combase.dll", PreserveSig = false)]
    static extern void WindowsCreateString([MarshalAs(UnmanagedType.LPWStr)] string s, int length, out IntPtr hstring);
    [DllImport("combase.dll")]
    static extern int WindowsDeleteString(IntPtr hstring);
    [DllImport("combase.dll")]
    static extern IntPtr WindowsGetStringRawBuffer(IntPtr hstring, out uint length);

    static IAudioPolicyConfigFactory? _factory;
    static bool _failed;

    static IAudioPolicyConfigFactory? Factory
    {
        get
        {
            if (_factory != null || _failed) return _factory;
            try
            {
                const string name = "Windows.Media.Internal.AudioPolicyConfig";
                WindowsCreateString(name, name.Length, out var h);
                try
                {
                    var iid = typeof(IAudioPolicyConfigFactory).GUID;
                    RoGetActivationFactory(h, ref iid, out var f);
                    _factory = (IAudioPolicyConfigFactory)f;
                }
                finally { WindowsDeleteString(h); }
            }
            catch { _failed = true; }
            return _factory;
        }
    }

    public static bool IsAvailable => Factory != null;

    /// <summary>The MMDevice id (e.g. {0.0.0.00000000}.{guid}) an app is pinned to, or null for "Default".</summary>
    public static string? GetAppOutput(uint pid)
    {
        var f = Factory;
        if (f == null) return null;
        try
        {
            if (f.GetPersistedDefaultAudioEndpoint(pid, 0 /*eRender*/, 1 /*eMultimedia*/, out var h) != 0 || h == IntPtr.Zero) return null;
            try
            {
                var raw = Marshal.PtrToStringUni(WindowsGetStringRawBuffer(h, out var len), (int)len);
                if (string.IsNullOrEmpty(raw)) return null;
                // \\?\SWD#MMDEVAPI#{0.0.0.00000000}.{guid}#{interface}
                var start = raw.IndexOf("{0.0.", StringComparison.Ordinal);
                var end = raw.LastIndexOf('#');
                return start >= 0 && end > start ? raw[start..end] : null;
            }
            finally { WindowsDeleteString(h); }
        }
        catch { return null; }
    }

    /// <summary>Pins an app's output to a device; null puts it back on the Windows default.</summary>
    public static bool SetAppOutput(uint pid, string? deviceId)
    {
        var f = Factory;
        if (f == null) return false;
        IntPtr h = IntPtr.Zero;
        try
        {
            if (deviceId != null)
            {
                var full = DevicePrefix + deviceId + "#" + RenderInterface;
                WindowsCreateString(full, full.Length, out h);
            }
            int a = f.SetPersistedDefaultAudioEndpoint(pid, 0, 0 /*eConsole*/, h);
            int b = f.SetPersistedDefaultAudioEndpoint(pid, 0, 1 /*eMultimedia*/, h);
            return a == 0 && b == 0;
        }
        catch { return false; }
        finally { if (h != IntPtr.Zero) WindowsDeleteString(h); }
    }
}
