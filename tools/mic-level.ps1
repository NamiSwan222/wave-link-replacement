# Records a capture device (default: Mic In (Elgato Wave:3)) in normal shared mode, the way
# Discord and OBS do, and prints the level every half second. Effects in Windows' audio engine
# apply to this recording.
# Usage: powershell -ExecutionPolicy Bypass -File mic-level.ps1 [-DeviceId "{0.0.1.00000000}.{...}"] [-Seconds 5] [-Raw]
param([string]$DeviceId = '{0.0.1.00000000}.{d3da57cc-220c-47ef-ae6d-eba1169fcc04}', [int]$Seconds = 5, [switch]$Raw, [switch]$Comms, [switch]$LowLatency)

Add-Type -TypeDefinition @'
using System; using System.Threading; using System.Runtime.InteropServices; using System.Collections.Generic;
[ComImport, Guid("BCDE0395-E52F-467C-8E3D-C4579291692E")] class MMDE2 {}
[InterfaceType(ComInterfaceType.InterfaceIsIUnknown), Guid("A95664D2-9614-4F35-A746-DE8DB63617E6")]
interface IMMDeviceEnumerator2 { int a(); int b(); [PreserveSig] int GetDevice([MarshalAs(UnmanagedType.LPWStr)] string id, out IMMDevice2 d); }
[InterfaceType(ComInterfaceType.InterfaceIsIUnknown), Guid("D666063F-1587-4E43-81F1-B948E807363F")]
interface IMMDevice2 { [PreserveSig] int Activate(ref Guid iid, int ctx, IntPtr p, [MarshalAs(UnmanagedType.IUnknown)] out object o); }
[ComImport, Guid("7ED4EE07-8E67-4CD4-8C1A-2B7A5987AD42"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
interface IAudioClient2 { // actually IAudioClient3 (superset of IAudioClient2)
  [PreserveSig] int Initialize(int shareMode, uint flags, long dur, long period, IntPtr fmt, IntPtr session);
  [PreserveSig] int GetBufferSize(out uint n); [PreserveSig] int GetStreamLatency(out long l); [PreserveSig] int GetCurrentPadding(out uint p);
  [PreserveSig] int IsFormatSupported(int m, IntPtr f, IntPtr c); [PreserveSig] int GetMixFormat(out IntPtr f); [PreserveSig] int GetDevicePeriod(out long a, out long b);
  [PreserveSig] int Start(); [PreserveSig] int Stop(); [PreserveSig] int Reset(); [PreserveSig] int SetEventHandle(IntPtr h);
  [PreserveSig] int GetService(ref Guid iid, [MarshalAs(UnmanagedType.IUnknown)] out object o);
  [PreserveSig] int IsOffloadCapable(int cat, out int b); [PreserveSig] int SetClientProperties(IntPtr props);
  [PreserveSig] int GetBufferSizeLimits(IntPtr fmt, int evt, out long mn, out long mx);
  [PreserveSig] int GetSharedModeEnginePeriod(IntPtr fmt, out uint def, out uint fund, out uint min, out uint max);
  [PreserveSig] int GetCurrentSharedModeEnginePeriod(out IntPtr fmt, out uint cur);
  [PreserveSig] int InitializeSharedAudioStream(uint flags, uint periodFrames, IntPtr fmt, IntPtr session); }
[ComImport, Guid("C8ADBD64-E71E-48a0-A4DE-185C395CD317"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
interface IAudioCaptureClient2 {
  [PreserveSig] int GetBuffer(out IntPtr data, out uint frames, out uint flags, out ulong pos, out ulong qpc);
  [PreserveSig] int ReleaseBuffer(uint frames); [PreserveSig] int GetNextPacketSize(out uint frames); }
public static class MicLevel {
  public static bool LowLatency;
  public static List<string> Run(string id, int seconds, bool raw, bool comms) {
    List<string> r = null; Exception err = null;
    var t = new Thread(() => { try { r = Go(id, seconds, raw, comms); } catch (Exception e) { err = e; } });
    t.SetApartmentState(ApartmentState.MTA); t.Start(); t.Join(); if (err != null) throw err; return r; }
  static List<string> Go(string id, int seconds, bool raw, bool comms) {
    var lines = new List<string>();
    var en = (IMMDeviceEnumerator2)new MMDE2(); IMMDevice2 d; int hr = en.GetDevice(id, out d); if (hr != 0) throw new Exception("GetDevice 0x" + hr.ToString("X8"));
    Guid iid = typeof(IAudioClient2).GUID; object o; hr = d.Activate(ref iid, 23, IntPtr.Zero, out o); if (hr != 0) throw new Exception("Activate 0x" + hr.ToString("X8"));
    var ac = (IAudioClient2)o;
    if (raw || comms) { // AudioClientProperties { cbSize, bIsOffload, eCategory, Options=RAW }
      IntPtr p = Marshal.AllocHGlobal(16); Marshal.WriteInt32(p, 0, 16); Marshal.WriteInt32(p, 4, 0); Marshal.WriteInt32(p, 8, comms ? 3 : 0); Marshal.WriteInt32(p, 12, raw ? 1 : 0);
      hr = ac.SetClientProperties(p); lines.Add((raw ? "raw " : "") + (comms ? "communications " : "") + "request: 0x" + hr.ToString("X8")); }
    IntPtr fmt; ac.GetMixFormat(out fmt);
    int ch = Marshal.ReadInt16(fmt, 2); int bits = Marshal.ReadInt16(fmt, 14);
    if (LowLatency) { uint def, fund, min, max; ac.GetSharedModeEnginePeriod(fmt, out def, out fund, out min, out max);
      hr = ac.InitializeSharedAudioStream(0, min, fmt, IntPtr.Zero); lines.Add("low-latency period " + min + " frames (default " + def + "): 0x" + hr.ToString("X8"));
      if (hr != 0) throw new Exception("InitializeSharedAudioStream 0x" + hr.ToString("X8")); }
    else { hr = ac.Initialize(0, 0, 2000000, 0, fmt, IntPtr.Zero); if (hr != 0) throw new Exception("Initialize 0x" + hr.ToString("X8")); }
    Guid ccIid = typeof(IAudioCaptureClient2).GUID; object cco; ac.GetService(ref ccIid, out cco); var cc = (IAudioCaptureClient2)cco;
    ac.Start(); var start = DateTime.Now; var b0 = start; float peak = 0;
    while ((DateTime.Now - start).TotalSeconds < seconds) {
      Thread.Sleep(20); uint n; cc.GetNextPacketSize(out n);
      while (n > 0) { IntPtr data; uint frames, flags; ulong a1, a2; cc.GetBuffer(out data, out frames, out flags, out a1, out a2);
        if ((flags & 2) == 0 && bits == 32) { var buf = new float[frames * ch]; Marshal.Copy(data, buf, 0, buf.Length); foreach (var s in buf) { var a = Math.Abs(s); if (a > peak) peak = a; } }
        cc.ReleaseBuffer(frames); cc.GetNextPacketSize(out n); }
      if ((DateTime.Now - b0).TotalMilliseconds >= 500) { double db = peak > 0 ? 20 * Math.Log10(peak) : -120;
        lines.Add(string.Format("{0,5:0.0}s  peak {1,7:0.0} dB  {2}", (DateTime.Now - start).TotalSeconds, db, new string('#', Math.Max(0, (int)((db + 60) / 2))))); b0 = DateTime.Now; peak = 0; } }
    ac.Stop(); return lines; } }
'@
[MicLevel]::LowLatency = [bool]$LowLatency
[MicLevel]::Run($DeviceId, $Seconds, [bool]$Raw, [bool]$Comms)
