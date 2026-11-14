using System.IO;
using System.IO.MemoryMappedFiles;
using System.Runtime.InteropServices;
using System.Security.AccessControl;
using System.Security.Principal;
using NAudio.CoreAudioApi;
using NAudio.Wave;
using NAudio.Wave.SampleProviders;

namespace WaveFix;

/// <summary>
/// The app's end of the audio bridge: a memory-mapped file the WaveFix Voice FX plugin reads
/// inside the audio engine and mixes into the mic. Layout matches native/wavefix-fx/src/bridge.rs.
/// Nothing here plays the mic's sound through an app, so Discord's stream audio can't record it.
/// </summary>
public sealed unsafe class AudioBridge : IDisposable
{
    const uint Magic = 0x5242_4657, Version = 2;
    const int HeaderBytes = 128;
    public const int RingLen = 1 << 18; // 5.4 s at 48 kHz
    /// <summary>Soundboard ring, RVC input ring, RVC output ring.</summary>
    const int Rings = 4; // soundboard, RVC in, RVC out, plugin tap

    public static string FilePath => Path.Combine(VoiceFx.Folder, "bridge.bin");

    readonly MemoryMappedFile _mmf;
    readonly MemoryMappedViewAccessor _view;
    readonly byte* _p;

    AudioBridge(MemoryMappedFile mmf, MemoryMappedViewAccessor view, byte* p) { _mmf = mmf; _view = view; _p = p; }

    /// <summary>Creates (or reuses) the bridge file and lets the audio engine's account use it.</summary>
    public static AudioBridge Create()
    {
        Directory.CreateDirectory(VoiceFx.Folder);
        var fs = new FileStream(FilePath, FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.ReadWrite | FileShare.Delete);
        try
        {
            long size = HeaderBytes + Rings * 4L * RingLen;
            if (fs.Length != size) fs.SetLength(size);
            File.SetAttributes(FilePath, FileAttributes.Temporary); // keep it in memory, not on disk
        }
        catch (UnauthorizedAccessException) { /* made by another account earlier */ }
        GrantAudioEngine();

        var mmf = MemoryMappedFile.CreateFromFile(fs, null, 0, MemoryMappedFileAccess.ReadWrite, HandleInheritability.None, leaveOpen: false);
        var view = mmf.CreateViewAccessor();
        byte* p = null;
        view.SafeMemoryMappedViewHandle.AcquirePointer(ref p);
        var b = new AudioBridge(mmf, view, p);
        if (b.U32(0) != Magic || b.U32(4) != Version || b.U32(12) != RingLen)
        {
            new Span<byte>(p, HeaderBytes).Clear();
            *(uint*)(p + 12) = RingLen;
            *(uint*)(p + 4) = Version;
            Volatile.Write(ref *(uint*)p, Magic);
        }
        return b;
    }

    static readonly SecurityIdentifier LocalService = new(WellKnownSidType.LocalServiceSid, null);

    /// <summary>
    /// audiodg.exe runs as LOCAL SERVICE, which only inherits read access here (through Users);
    /// the plugin must also write its play position. Set by path: the open stream's handle
    /// doesn't carry the right to change permissions.
    /// </summary>
    static void GrantAudioEngine()
    {
        if (AudioEngineCanWrite()) return;
        try
        {
            var fi = new FileInfo(FilePath);
            var acl = fi.GetAccessControl();
            acl.AddAccessRule(new FileSystemAccessRule(LocalService, FileSystemRights.Read | FileSystemRights.Write, AccessControlType.Allow));
            fi.SetAccessControl(acl);
        }
        catch (Exception ex) { ChangeLog.Write($"Couldn't let the audio engine use the soundboard file: {ex.Message}"); }
    }

    /// <summary>True if LOCAL SERVICE has an explicit write grant on the bridge file.</summary>
    public static bool AudioEngineCanWrite()
    {
        try
        {
            var rules = new FileInfo(FilePath).GetAccessControl().GetAccessRules(true, false, typeof(SecurityIdentifier));
            return rules.OfType<FileSystemAccessRule>().Any(r => r.IdentityReference == LocalService
                && r.AccessControlType == AccessControlType.Allow && r.FileSystemRights.HasFlag(FileSystemRights.WriteData));
        }
        catch { return false; }
    }

    uint U32(int off) => Volatile.Read(ref *(uint*)(_p + off));
    ref long I64(int off) => ref *(long*)(_p + off);

    public int PluginRate => (int)U32(8);
    public long Consumer => Volatile.Read(ref I64(16));
    public long WritePos { get => Volatile.Read(ref I64(24)); set => Volatile.Write(ref I64(24), value); }
    public long PluginBeat => Volatile.Read(ref I64(32));
    public void AppBeat() => Volatile.Write(ref I64(40), (long)Environment.TickCount64);

    /// <summary>The plugin processed audio within the last second (it's in the chain and running).</summary>
    public bool PluginAlive => PluginRate > 0 && Environment.TickCount64 - PluginBeat < 1000;

    /// <summary>Writes soundboard samples at absolute positions [start, start + data.Length).</summary>
    public void Write(long start, ReadOnlySpan<float> data) => WriteRing(0, start, data);

    float* Ring(int n) => (float*)(_p + HeaderBytes + (long)n * 4 * RingLen);

    void WriteRing(int n, long start, ReadOnlySpan<float> data)
    {
        var ring = Ring(n);
        for (int i = 0; i < data.Length; i++) ring[(start + i) & (RingLen - 1)] = data[i];
    }

    // ---- AI voice (RVC) timeline: the plugin writes the voice in, the engine writes it back converted ----

    /// <summary>The plugin has written the voice up to here (absolute sample index).</summary>
    public long RvcInputWritten => Volatile.Read(ref I64(56));
    public long RvcOutputWritten { get => Volatile.Read(ref I64(64)); set => Volatile.Write(ref I64(64), value); }
    public void RvcEngineBeat() => Volatile.Write(ref I64(72), (long)Environment.TickCount64);
    /// <summary>Converted audio for position p plays at p + delay (samples).</summary>
    public int RvcDelay { get => (int)U32(80); set => Volatile.Write(ref *(uint*)(_p + 80), (uint)value); }

    /// <summary>Copies the voice the plugin sent for [start, start + dest.Length).</summary>
    public void ReadRvcInput(long start, Span<float> dest)
    {
        var ring = Ring(1);
        for (int i = 0; i < dest.Length; i++) dest[i] = ring[(start + i) & (RingLen - 1)];
    }

    public void WriteRvcOutput(long start, ReadOnlySpan<float> data) => WriteRing(2, start, data);

    // ---- Plugin tap: the engine copies the audio entering one stage here, for a settings window ----

    /// <summary>The stage (1-based) whose input the engine should copy; 0 = none.</summary>
    public int TapStage { get => (int)U32(84); set => Volatile.Write(ref *(uint*)(_p + 84), (uint)value); }
    public int TapRate => (int)U32(88);
    public long TapWritten => Volatile.Read(ref I64(96));
    /// <summary>Keeps the tap running (the engine stops it 2 s after the last beat).</summary>
    public void TapBeat() => Volatile.Write(ref I64(104), (long)Environment.TickCount64);

    public void ReadTap(long start, Span<float> dest)
    {
        var ring = Ring(3);
        for (int i = 0; i < dest.Length; i++) dest[i] = ring[(start + i) & (RingLen - 1)];
    }

    public void Dispose()
    {
        _view.SafeMemoryMappedViewHandle.ReleasePointer();
        _view.Dispose();
        _mmf.Dispose();
    }
}

/// <summary>Mixes the sounds that are playing; used for the mic feed and for "hear it myself".</summary>
sealed class SoundMixer
{
    /// <summary>A playing sound: preloaded samples (short sounds) or a file read as it plays (long ones).</summary>
    sealed class Voice
    {
        public float[]? Data;
        public SoundStream? Stream;
        public int Pos;
        public float Gain;
        public bool Done;
        float[] _tmp = Array.Empty<float>();

        /// <summary>Adds up to buf.Length samples (scaled) into buf.</summary>
        public void MixInto(Span<float> buf, float gain)
        {
            if (Data != null)
            {
                int n = Math.Min(buf.Length, Data.Length - Pos);
                for (int i = 0; i < n; i++) buf[i] += Data[Pos + i] * gain;
                Pos += n;
                Done = Pos >= Data.Length;
                return;
            }
            if (_tmp.Length < buf.Length) _tmp = new float[buf.Length];
            int got = Stream!.Read(_tmp.AsSpan(0, buf.Length));
            for (int i = 0; i < got; i++) buf[i] += _tmp[i] * gain;
            if (got < buf.Length) Done = true;
        }

        public void Dispose() => Stream?.Dispose();
    }

    readonly List<Voice> _voices = new();
    readonly object _lock = new();

    public bool Busy { get { lock (_lock) return _voices.Count > 0; } }

    public void Add(float[] data, float gain) { lock (_lock) _voices.Add(new Voice { Data = data, Gain = gain }); }
    public void Add(SoundStream stream, float gain) { lock (_lock) _voices.Add(new Voice { Stream = stream, Gain = gain }); }

    public void Clear()
    {
        lock (_lock)
        {
            foreach (var v in _voices) v.Dispose();
            _voices.Clear();
        }
    }

    /// <summary>Fills <paramref name="buf"/> (mono) with the mix and advances every sound.</summary>
    public void Read(Span<float> buf, float master)
    {
        buf.Clear();
        lock (_lock)
        {
            foreach (var v in _voices)
            {
                try { v.MixInto(buf, v.Gain * master); }
                catch { v.Done = true; } // a file that stops decoding just ends
            }
            Retire();
        }
    }

    /// <summary>Drops <paramref name="n"/> samples of every sound (time passed with nobody listening).</summary>
    public void Skip(int n)
    {
        lock (_lock)
        {
            Span<float> scratch = stackalloc float[1024];
            foreach (var v in _voices)
                for (int left = n; left > 0 && !v.Done; left -= scratch.Length)
                {
                    try { v.MixInto(scratch[..Math.Min(scratch.Length, left)], 0); }
                    catch { v.Done = true; }
                }
            Retire();
        }
    }

    void Retire()
    {
        foreach (var v in _voices) if (v.Done) v.Dispose();
        _voices.RemoveAll(v => v.Done);
    }
}

/// <summary>A sound file decoded to mono at a given rate while it plays.</summary>
public sealed class SoundStream : IDisposable
{
    readonly MediaFoundationReader _reader;
    readonly ISampleProvider _sp;
    float[] _buf = Array.Empty<float>();

    public SoundStream(string path, int rate)
    {
        _reader = new MediaFoundationReader(path);
        try { _sp = SoundboardEngine.ToMono(_reader.ToSampleProvider(), rate); }
        catch { _reader.Dispose(); throw; }
    }

    public TimeSpan Length => _reader.TotalTime;

    public int Read(Span<float> dest)
    {
        if (_buf.Length < dest.Length) _buf = new float[dest.Length];
        int total = 0;
        while (total < dest.Length)
        {
            int n = _sp.Read(_buf, 0, dest.Length - total);
            if (n <= 0) break;
            _buf.AsSpan(0, n).CopyTo(dest[total..]);
            total += n;
        }
        return total;
    }

    public void Dispose() => _reader.Dispose();
}

/// <summary>Plays the mixer to a local output device (so you hear your own sounds).</summary>
sealed class MonitorProvider : ISampleProvider
{
    readonly SoundMixer _mixer;
    readonly Func<float> _volume;
    float[] _mono = Array.Empty<float>();
    public WaveFormat WaveFormat { get; }

    public MonitorProvider(SoundMixer mixer, int rate, int channels, Func<float> volume)
    {
        _mixer = mixer; _volume = volume;
        WaveFormat = WaveFormat.CreateIeeeFloatWaveFormat(rate, channels);
    }

    public int Read(float[] buffer, int offset, int count)
    {
        int ch = WaveFormat.Channels, frames = count / ch;
        if (_mono.Length < frames) _mono = new float[frames];
        _mixer.Read(_mono.AsSpan(0, frames), _volume());
        for (int f = 0; f < frames; f++)
            for (int c = 0; c < ch; c++) buffer[offset + f * ch + c] = _mono[f];
        return count; // keep the stream going; silence when nothing plays
    }
}

/// <summary>
/// The soundboard: decodes sound files, mixes what's playing, and streams it to the plugin
/// through the bridge (and optionally to your own speakers or headphones).
/// </summary>
public sealed class SoundboardEngine : IDisposable
{
    [DllImport("winmm")] static extern uint timeBeginPeriod(uint ms);
    [DllImport("winmm")] static extern uint timeEndPeriod(uint ms);

    /// <summary>Longest sound you can add.</summary>
    public const int MaxMinutes = 15;
    /// <summary>Sounds up to this long are decoded once and kept in memory (instant start);
    /// longer ones are read from the file while they play.</summary>
    const double PreloadSeconds = 30;
    /// <summary>How far ahead of the audio engine the app writes. Covers Windows timer jitter.</summary>
    const double LeadSeconds = 0.06;

    readonly SoundMixer _mic = new(), _monitor = new();
    readonly Dictionary<(string Path, int Rate), float[]> _cache = new();
    readonly object _cacheLock = new();
    AudioBridge? _bridge;
    Thread? _thread;
    volatile bool _running;
    WasapiOut? _out;
    int _monitorRate = 48000;
    long _monitorIdleSince;

    public float MicVolume { get; set; } = 0.8f;
    public float MonitorVolume { get; set; } = 0.6f;
    public bool Monitor { get; set; } = true;
    public string? Error { get; private set; }

    /// <summary>True while the audio engine is taking our sounds (plugin loaded and running).</summary>
    public bool Connected => _bridge?.PluginAlive == true;
    public bool Playing => _mic.Busy || _monitor.Busy;

    public void Start()
    {
        if (_running) return;
        try { _bridge ??= AudioBridge.Create(); Error = null; }
        catch (Exception ex) { Error = ex.Message; ChangeLog.Write($"Soundboard bridge couldn't start: {ex.Message}"); return; }
        _running = true;
        _thread = new Thread(Pump) { IsBackground = true, Name = "Soundboard", Priority = ThreadPriority.AboveNormal };
        _thread.Start();
    }

    public void Stop()
    {
        _running = false;
        _thread?.Join(500);
        _thread = null;
        StopAll();
        StopMonitor();
    }

    internal static ISampleProvider ToMono(ISampleProvider sp, int rate)
    {
        if (sp.WaveFormat.Channels > 1) sp = new DownmixToMono(sp);
        if (sp.WaveFormat.SampleRate != rate) sp = new WdlResamplingSampleProvider(sp, rate);
        return sp;
    }

    /// <summary>
    /// Checks a file can be played: it opens, decodes, has sound and isn't too long. Returns its
    /// length. Throws with a reason for the user otherwise.
    /// </summary>
    public static TimeSpan Check(string path)
    {
        using var s = new SoundStream(path, 48000);
        var len = s.Length;
        if (len > TimeSpan.FromMinutes(MaxMinutes)) throw new InvalidOperationException($"it's longer than {MaxMinutes} minutes.");
        var probe = new float[48000];
        if (s.Read(probe) == 0) throw new InvalidOperationException("there's no sound in it.");
        return len;
    }

    /// <summary>Loads and resamples a whole file to mono at <paramref name="rate"/> (cached). For short sounds.</summary>
    public float[] Decode(string path, int rate)
    {
        lock (_cacheLock) if (_cache.TryGetValue((path, rate), out var hit)) return hit;
        using var s = new SoundStream(path, rate);
        var data = new List<float>(rate * 4);
        var buf = new float[rate];
        int n;
        while ((n = s.Read(buf)) > 0) data.AddRange(buf.AsSpan(0, n).ToArray());
        var result = data.ToArray();
        lock (_cacheLock) _cache[(path, rate)] = result;
        return result;
    }

    public void Forget(string path)
    {
        lock (_cacheLock) foreach (var k in _cache.Keys.Where(k => k.Path == path).ToList()) _cache.Remove(k);
    }

    /// <summary>Plays a sound into the mic (and to you, if monitoring is on). Decoding happens off the UI thread.</summary>
    public Task Play(string path, float gain) => Task.Run(() =>
    {
        int rate = _bridge?.PluginRate is > 0 and var r ? r : 48000;
        bool preload;
        using (var probe = new SoundStream(path, rate)) preload = probe.Length.TotalSeconds <= PreloadSeconds;
        if (preload) _mic.Add(Decode(path, rate), gain);
        else _mic.Add(new SoundStream(path, rate), gain);
        if (Monitor)
        {
            EnsureMonitor();
            if (preload) _monitor.Add(Decode(path, _monitorRate), gain);
            else _monitor.Add(new SoundStream(path, _monitorRate), gain);
        }
    });

    public void StopAll() { _mic.Clear(); _monitor.Clear(); }

    void EnsureMonitor()
    {
        lock (_cacheLock)
        {
            _monitorIdleSince = 0;
            if (_out != null) return;
            try
            {
                using var en = new MMDeviceEnumerator();
                var dev = en.GetDefaultAudioEndpoint(DataFlow.Render, Role.Multimedia);
                var mix = dev.AudioClient.MixFormat;
                _monitorRate = mix.SampleRate;
                var o = new WasapiOut(dev, AudioClientShareMode.Shared, true, 50);
                o.Init(new MonitorProvider(_monitor, mix.SampleRate, mix.Channels, () => MonitorVolume));
                o.Play();
                _out = o;
            }
            catch (Exception ex) { ChangeLog.Write($"Soundboard couldn't play to your speakers: {ex.Message}"); }
        }
    }

    void StopMonitor()
    {
        lock (_cacheLock)
        {
            try { _out?.Stop(); _out?.Dispose(); } catch { }
            _out = null;
        }
    }

    void Pump()
    {
        timeBeginPeriod(1);
        try
        {
            var buf = new float[AudioBridge.RingLen / 4];
            long lastTick = Environment.TickCount64;
            while (_running)
            {
                var b = _bridge!;
                b.AppBeat();
                long now = Environment.TickCount64;
                if (!b.PluginAlive)
                {
                    // Nobody is listening: let playing sounds run out in real time instead of
                    // piling up and all playing at once when the engine comes back.
                    _mic.Skip((int)Math.Min(int.MaxValue, (now - lastTick) * 48));
                }
                else
                {
                    int rate = b.PluginRate;
                    long consumer = b.Consumer;
                    long write = b.WritePos;
                    if (write < consumer || write > consumer + AudioBridge.RingLen / 2) write = consumer; // fell behind or reset
                    long target = consumer + (long)(rate * LeadSeconds);
                    while (write < target)
                    {
                        int n = (int)Math.Min(buf.Length, target - write);
                        _mic.Read(buf.AsSpan(0, n), MicVolume);
                        b.Write(write, buf.AsSpan(0, n));
                        write += n;
                        b.WritePos = write;
                    }
                }
                lastTick = now;

                // Close the local output a few seconds after the last sound.
                if (_out != null && !_monitor.Busy)
                {
                    if (_monitorIdleSince == 0) _monitorIdleSince = now;
                    else if (now - _monitorIdleSince > 3000) StopMonitor();
                }
                Thread.Sleep(2);
            }
        }
        catch (Exception ex) { Error = ex.Message; ChangeLog.Write($"Soundboard stopped: {ex.Message}"); }
        finally { timeEndPeriod(1); }
    }

    public void Dispose() { Stop(); _bridge?.Dispose(); _bridge = null; }

    sealed class DownmixToMono : ISampleProvider
    {
        readonly ISampleProvider _src;
        float[] _buf = Array.Empty<float>();
        public WaveFormat WaveFormat { get; }
        public DownmixToMono(ISampleProvider src) { _src = src; WaveFormat = WaveFormat.CreateIeeeFloatWaveFormat(src.WaveFormat.SampleRate, 1); }
        public int Read(float[] buffer, int offset, int count)
        {
            int ch = _src.WaveFormat.Channels;
            if (_buf.Length < count * ch) _buf = new float[count * ch];
            int got = _src.Read(_buf, 0, count * ch) / ch;
            for (int f = 0; f < got; f++)
            {
                float s = 0;
                for (int c = 0; c < ch; c++) s += _buf[f * ch + c];
                buffer[offset + f] = s / ch;
            }
            return got;
        }
    }
}

/// <summary>A sound on the soundboard (saved in settings.json).</summary>
public sealed class SoundEntry : Observable
{
    /// <summary>Settings this version doesn't know (e.g. from a newer WaveFix): kept, so saving never drops them.</summary>
    [System.Text.Json.Serialization.JsonExtensionData] public Dictionary<string, System.Text.Json.JsonElement>? Extra { get; set; }

    public string Id { get; set; } = Guid.NewGuid().ToString("N")[..10];
    public string Name { get; set; } = "";
    /// <summary>Where a sound from "Find sounds online" came from (title, creator, license).</summary>
    public string? Credit { get; set; }
    [System.Text.Json.Serialization.JsonIgnore] public string Tip => Credit == null ? "Play" : $"Play · {Credit}";
    /// <summary>Copy of the file in %APPDATA%\WaveFix\Sounds.</summary>
    public string File { get; set; } = "";
    public double Volume { get; set; } = 100;

    string _keyText = "";
    [System.Text.Json.Serialization.JsonIgnore]
    public string KeyText { get => _keyText; set => Set(ref _keyText, value); }

    public string ShortcutId => "sound:" + Id;
}

public sealed class SoundboardSettings
{
    /// <summary>Settings this version doesn't know (e.g. from a newer WaveFix): kept, so saving never drops them.</summary>
    [System.Text.Json.Serialization.JsonExtensionData] public Dictionary<string, System.Text.Json.JsonElement>? Extra { get; set; }

    public bool Enabled { get; set; }
    /// <summary>How loud sounds are for everyone else, 0..100.</summary>
    public double Volume { get; set; } = 80;
    public bool HearMyself { get; set; } = true;
    public double MyVolume { get; set; } = 60;
    public List<SoundEntry> Sounds { get; set; } = new();

    public static string Folder => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "WaveFix", "Sounds");
}
