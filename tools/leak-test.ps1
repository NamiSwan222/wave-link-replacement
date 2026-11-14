# Records one app's audio the way Discord's Go Live does (Windows process loopback)
# and prints the loudness every half second. Used to prove whether an app leaks the mic.
# Usage: powershell -ExecutionPolicy Bypass -File leak-test.ps1 [-ProcessName Elgato.WaveLink] [-Seconds 15]
param([string]$ProcessName = 'Elgato.WaveLink', [int]$Seconds = 15, [switch]$Exclude, [string]$Wav)

Add-Type -TypeDefinition @'
using System; using System.Threading; using System.Runtime.InteropServices; using System.Collections.Generic;

[StructLayout(LayoutKind.Sequential)] struct ACT_PARAMS { public int ActivationType; public uint TargetProcessId; public int Mode; }
[StructLayout(LayoutKind.Sequential)] struct BLOB_PROPVARIANT { public ushort vt; public ushort r1, r2, r3; public uint cbSize; public IntPtr pBlobData; public IntPtr pad; }
[StructLayout(LayoutKind.Sequential, Pack = 2)] struct WAVEFORMATEX { public ushort wFormatTag, nChannels; public uint nSamplesPerSec, nAvgBytesPerSec; public ushort nBlockAlign, wBitsPerSample, cbSize; }

[ComImport, Guid("72A22D78-CDE4-431D-B8CC-843A71199B6D"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
interface IActivateAudioInterfaceAsyncOperation { void GetActivateResult(out int hr, [MarshalAs(UnmanagedType.IUnknown)] out object iface); }
[ComImport, Guid("41D949AB-9862-444A-80F6-C261334DA5EB"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
interface IActivateAudioInterfaceCompletionHandler { void ActivateCompleted(IActivateAudioInterfaceAsyncOperation op); }
[ComImport, Guid("94ea2b94-e9cc-49e0-c0ff-ee64ca8f5b90"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
interface IAgileObject {}
[ComImport, Guid("1CB9AD4C-DBFA-4c32-B178-C2F568A703B2"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
interface IAudioClient {
  [PreserveSig] int Initialize(int shareMode, uint flags, long dur, long period, IntPtr fmt, IntPtr session);
  [PreserveSig] int GetBufferSize(out uint n); [PreserveSig] int GetStreamLatency(out long l); [PreserveSig] int GetCurrentPadding(out uint p);
  [PreserveSig] int IsFormatSupported(int m, IntPtr f, IntPtr c); [PreserveSig] int GetMixFormat(out IntPtr f); [PreserveSig] int GetDevicePeriod(out long a, out long b);
  [PreserveSig] int Start(); [PreserveSig] int Stop(); [PreserveSig] int Reset(); [PreserveSig] int SetEventHandle(IntPtr h);
  [PreserveSig] int GetService(ref Guid iid, [MarshalAs(UnmanagedType.IUnknown)] out object o); }
[ComImport, Guid("C8ADBD64-E71E-48a0-A4DE-185C395CD317"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
interface IAudioCaptureClient {
  [PreserveSig] int GetBuffer(out IntPtr data, out uint frames, out uint flags, out ulong pos, out ulong qpc);
  [PreserveSig] int ReleaseBuffer(uint frames); [PreserveSig] int GetNextPacketSize(out uint frames); }

class Handler : IActivateAudioInterfaceCompletionHandler, IAgileObject {
  public ManualResetEvent Done = new ManualResetEvent(false); public int Hr; public object Iface;
  public void ActivateCompleted(IActivateAudioInterfaceAsyncOperation op) { op.GetActivateResult(out Hr, out Iface); Done.Set(); } }

public static class LeakTest {
  [DllImport("Mmdevapi.dll", ExactSpelling = true, PreserveSig = false)]
  static extern void ActivateAudioInterfaceAsync([MarshalAs(UnmanagedType.LPWStr)] string path, ref Guid iid, IntPtr parms, IActivateAudioInterfaceCompletionHandler h, out IActivateAudioInterfaceAsyncOperation op);

  public static List<string> Run(uint pid, bool exclude, int seconds) {
    List<string> result = null; Exception err = null;
    var t = new Thread(() => { try { result = Capture(pid, exclude, seconds); } catch (Exception e) { err = e; } });
    t.SetApartmentState(ApartmentState.MTA); t.Start(); t.Join();
    if (err != null) throw err; return result; }

  static List<string> Capture(uint pid, bool exclude, int seconds) {
    var lines = new List<string>();
    var ap = new ACT_PARAMS { ActivationType = 1, TargetProcessId = pid, Mode = exclude ? 1 : 0 };
    IntPtr apPtr = Marshal.AllocHGlobal(Marshal.SizeOf(ap)); Marshal.StructureToPtr(ap, apPtr, false);
    var pv = new BLOB_PROPVARIANT { vt = 65, cbSize = (uint)Marshal.SizeOf(ap), pBlobData = apPtr };
    IntPtr pvPtr = Marshal.AllocHGlobal(Marshal.SizeOf(pv)); Marshal.StructureToPtr(pv, pvPtr, false);
    Guid iid = typeof(IAudioClient).GUID; var h = new Handler(); IActivateAudioInterfaceAsyncOperation op;
    ActivateAudioInterfaceAsync("VAD\\Process_Loopback", ref iid, pvPtr, h, out op);
    if (!h.Done.WaitOne(5000)) throw new Exception("activation timed out");
    if (h.Hr != 0) throw new Exception("activation failed 0x" + h.Hr.ToString("X8"));
    var ac = (IAudioClient)h.Iface;
    var wf = new WAVEFORMATEX { wFormatTag = 1, nChannels = 2, nSamplesPerSec = 48000, wBitsPerSample = 16, nBlockAlign = 4, nAvgBytesPerSec = 192000 };
    IntPtr wfPtr = Marshal.AllocHGlobal(Marshal.SizeOf(wf)); Marshal.StructureToPtr(wf, wfPtr, false);
    // LOOPBACK | EVENTCALLBACK | AUTOCONVERTPCM
    int hr = ac.Initialize(0, 0x00020000u | 0x00040000u | 0x80000000u, 2000000, 0, wfPtr, IntPtr.Zero);
    if (hr != 0) throw new Exception("Initialize failed 0x" + hr.ToString("X8"));
    var ev = new AutoResetEvent(false); ac.SetEventHandle(ev.SafeWaitHandle.DangerousGetHandle());
    Guid ccIid = typeof(IAudioCaptureClient).GUID; object cco; ac.GetService(ref ccIid, out cco); var cc = (IAudioCaptureClient)cco;
    var wav = new System.IO.MemoryStream();
    ac.Start();
    var start = DateTime.Now; var bucketStart = start; int peak = 0; double sumSq = 0; long count = 0;
    while ((DateTime.Now - start).TotalSeconds < seconds) {
      ev.WaitOne(100);
      uint n; cc.GetNextPacketSize(out n);
      while (n > 0) {
        IntPtr data; uint frames, flags; ulong p1, p2; cc.GetBuffer(out data, out frames, out flags, out p1, out p2);
        if ((flags & 2) != 0) { var z = new byte[frames * 4]; wav.Write(z, 0, z.Length); }
        if ((flags & 2) == 0) { // not silent
          var buf = new short[frames * 2]; Marshal.Copy(data, buf, 0, buf.Length);
          var bytes = new byte[frames * 4]; Buffer.BlockCopy(buf, 0, bytes, 0, bytes.Length); wav.Write(bytes, 0, bytes.Length);
          foreach (var s in buf) { int a = Math.Abs((int)s); if (a > peak) peak = a; sumSq += (double)s * s; } }
        count += frames * 2; cc.ReleaseBuffer(frames); cc.GetNextPacketSize(out n); }
      if ((DateTime.Now - bucketStart).TotalMilliseconds >= 500) {
        double rms = count > 0 ? Math.Sqrt(sumSq / count) : 0;
        double db = peak > 0 ? 20 * Math.Log10(peak / 32768.0) : -120;
        double rdb = rms > 0 ? 20 * Math.Log10(rms / 32768.0) : -120;
        lines.Add(string.Format("{0,5:0.0}s  peak {1,7:0.0} dB  rms {2,7:0.0} dB  {3}", (DateTime.Now - start).TotalSeconds, db, rdb, new string('#', Math.Max(0, (int)((db + 60) / 2)))));
        bucketStart = DateTime.Now; peak = 0; sumSq = 0; count = 0; } }
    ac.Stop();
    if (WavPath != null) {
      var d = wav.ToArray(); var f = new System.IO.BinaryWriter(System.IO.File.Create(WavPath));
      f.Write(new char[] {'R','I','F','F'}); f.Write(36 + d.Length); f.Write(new char[] {'W','A','V','E','f','m','t',' '});
      f.Write(16); f.Write((short)1); f.Write((short)2); f.Write(48000); f.Write(192000); f.Write((short)4); f.Write((short)16);
      f.Write(new char[] {'d','a','t','a'}); f.Write(d.Length); f.Write(d); f.Close(); lines.Add("Saved " + WavPath); }
    return lines; }
  public static string WavPath; }
'@

$p = Get-Process -Name $ProcessName -ErrorAction Stop | Select-Object -First 1
$mode = if ($Exclude) { 'EVERYTHING EXCEPT' } else { 'ONLY' }
"Recording $mode '$($p.Name)' (pid $($p.Id)) for $Seconds s ..."
if ($Wav) { [LeakTest]::WavPath = [IO.Path]::GetFullPath($Wav) }
[LeakTest]::Run([uint32]$p.Id, [bool]$Exclude, $Seconds)
