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
    const uint Magic = 0x5242_4657, Version = 1;
    const int HeaderBytes = 64;
    public const int RingLen = 1 << 18; // 5.4 s at 48 kHz

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
            long size = HeaderBytes + 4L * RingLen;
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

    /// <summary>Writes samples at absolute positions [start, start + data.Length).</summary>
    public void Write(long start, ReadOnlySpan<float> data)
    {
        var ring = (float*)(_p + HeaderBytes);
        for (int i = 0; i < data.Length; i++) ring[(start + i) & (RingLen - 1)] = data[i];
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
    sealed class Voice { public required float[] Data; public int Pos; public float Gain; }
    readonly List<Voice> _voices = new();
    readonly object _lock = new();

    public bool Busy { get { lock (_lock) return _voices.Count > 0; } }

    public void Add(float[] data, float gain) { lock (_lock) _voices.Add(new Voice { Data = data, Gain = gain }); }
    public void Clear() { lock (_lock) _voices.Clear(); }

    /// <summary>Fills <paramref name="buf"/> (mono) with the mix and advances every sound.</summary>
    public void Read(Span<float> buf, float master)
    {
        buf.Clear();
        lock (_lock)
        {
            foreach (var v in _voices)
            {
                int n = Math.Min(buf.Length, v.Data.Length - v.Pos);
                var g = v.Gain * master;
                for (int i = 0; i < n; i++) buf[i] += v.Data[v.Pos + i] * g;
                v.Pos += n;
            }
            _voices.RemoveAll(v => v.Pos >= v.Data.Length);
        }
    }

    /// <summary>Drops <paramref name="n"/> samples of every sound (time passed with nobody listening).</summary>
    public void Skip(int n)
    {
        lock (_lock)
        {
            foreach (var v in _voices) v.Pos += n;
            _voices.RemoveAll(v => v.Pos >= v.Data.Length);
        }
    }
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

    public const int MaxSeconds = 60;
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

    /// <summary>Loads and resamples a file to mono at <paramref name="rate"/> (cached). Throws on bad files.</summary>
    public float[] Decode(string path, int rate)
    {
        lock (_cacheLock) if (_cache.TryGetValue((path, rate), out var hit)) return hit;
        using var reader = new MediaFoundationReader(path);
        ISampleProvider sp = reader.ToSampleProvider();
        if (sp.WaveFormat.Channels > 1) sp = new DownmixToMono(sp);
        if (sp.WaveFormat.SampleRate != rate) sp = new WdlResamplingSampleProvider(sp, rate);
        var data = new List<float>(rate * 4);
        var buf = new float[rate];
        int n;
        while ((n = sp.Read(buf, 0, buf.Length)) > 0)
        {
            data.AddRange(buf.AsSpan(0, n).ToArray());
            if (data.Count > rate * MaxSeconds) throw new InvalidOperationException($"Sounds can be up to {MaxSeconds} seconds long.");
        }
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
        _mic.Add(Decode(path, rate), gain);
        if (Monitor)
        {
            EnsureMonitor();
            _monitor.Add(Decode(path, _monitorRate), gain);
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
    public string Id { get; set; } = Guid.NewGuid().ToString("N")[..10];
    public string Name { get; set; } = "";
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
    public bool Enabled { get; set; }
    /// <summary>How loud sounds are for everyone else, 0..100.</summary>
    public double Volume { get; set; } = 80;
    public bool HearMyself { get; set; } = true;
    public double MyVolume { get; set; } = 60;
    public List<SoundEntry> Sounds { get; set; } = new();

    public static string Folder => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "WaveFix", "Sounds");
}
