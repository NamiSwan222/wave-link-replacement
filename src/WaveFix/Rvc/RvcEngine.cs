using System.Diagnostics;
using Microsoft.ML.OnnxRuntime;
using Microsoft.ML.OnnxRuntime.Tensors;

namespace WaveFix.Rvc;

/// <summary>The loaded neural networks: the two shared helpers and the current voice.</summary>
public sealed class RvcModels : IDisposable
{
    public InferenceSession ContentVec { get; }
    public InferenceSession Rmvpe { get; }
    public InferenceSession? Voice { get; private set; }
    public AiVoice? VoiceInfo { get; private set; }
    /// <summary>"graphics card" or "processor" (what the models run on).</summary>
    public string Device { get; }
    bool _gpu;

    public RvcModels(bool preferGpu)
    {
        _gpu = preferGpu;
        string dev = "processor";
        InferenceSession Open(string file)
        {
            if (_gpu)
            {
                try { var s = new InferenceSession(file, Options(true)); dev = "graphics card"; return s; }
                catch (Exception ex) { ChangeLog.Write($"AI voices: graphics card not usable ({ex.Message}); using the processor"); _gpu = false; }
            }
            return new InferenceSession(file, Options(false));
        }
        ContentVec = Open(RvcHelpers.PathOf(RvcHelpers.All[0]));
        Rmvpe = Open(RvcHelpers.PathOf(RvcHelpers.All[1]));
        Device = dev;
    }

    static SessionOptions Options(bool gpu)
    {
        var o = new SessionOptions { GraphOptimizationLevel = GraphOptimizationLevel.ORT_ENABLE_ALL };
        if (gpu)
        {
            // DirectML: any DirectX 12 graphics card (NVIDIA, AMD, Intel).
            o.EnableMemoryPattern = false;
            o.ExecutionMode = ExecutionMode.ORT_SEQUENTIAL;
            o.AppendExecutionProvider_DML(0);
        }
        else
        {
            o.IntraOpNumThreads = Math.Max(1, Environment.ProcessorCount / 2);
        }
        return o;
    }

    public void LoadVoice(AiVoice v)
    {
        if (VoiceInfo?.File == v.File && Voice != null) { VoiceInfo = v; return; }
        var s = new InferenceSession(v.File, Options(_gpu));
        Voice?.Dispose();
        Voice = s;
        VoiceInfo = v;
    }

    public void Dispose() { ContentVec.Dispose(); Rmvpe.Dispose(); Voice?.Dispose(); }
}

/// <summary>
/// RVC's real-time conversion (as in RVC's own realtime GUI): each block of the mic is converted
/// together with a few seconds of what came before (for context), only the newest part is
/// synthesized, and blocks are joined by finding the best-matching overlap and crossfading.
/// </summary>
public sealed class RvcPipeline
{
    readonly RvcModels _m;
    readonly int _sr, _zc;
    public int BlockFrame { get; }
    readonly int _solaBuf, _solaSearch, _extra, _total, _total16, _block16;
    readonly float[] _inputWav, _input16;
    readonly Resampler _to16;
    readonly MelSpectrogram _mel = new();
    readonly long[] _cachePitch = new long[1024];
    readonly float[] _cachePitchF = new float[1024];
    float[] _solaBuffer;
    readonly float[] _fadeIn, _fadeOut;
    readonly Random _rng = new();
    Resampler? _fromModel;

    /// <summary>Semitones added to your pitch.</summary>
    public double PitchShift { get; set; }
    /// <summary>Quieter than this (dBFS) counts as silence (stops breathing and hum being "sung").</summary>
    public double GateDb { get; set; } = -50;

    public RvcPipeline(RvcModels models, int sampleRate, double blockSec, double extraSec)
    {
        _m = models;
        _sr = sampleRate;
        _zc = sampleRate / 100;
        BlockFrame = Math.Max(1, (int)Math.Round(blockSec * _sr / _zc)) * _zc;
        int crossfade = (int)Math.Round(0.05 * _sr / _zc) * _zc;
        _solaBuf = Math.Min(crossfade, 4 * _zc);
        _solaSearch = _zc;
        _extra = (int)Math.Round(extraSec * _sr / _zc) * _zc;
        _total = _extra + crossfade + _solaSearch + BlockFrame;
        _total16 = 160 * _total / _zc;
        _block16 = 160 * BlockFrame / _zc;
        _inputWav = new float[_total];
        _input16 = new float[_total16];
        _to16 = new Resampler(_sr, 16000);
        _solaBuffer = new float[_solaBuf];
        _fadeIn = new float[_solaBuf];
        _fadeOut = new float[_solaBuf];
        for (int i = 0; i < _solaBuf; i++)
        {
            double s = Math.Sin(0.5 * Math.PI * i / Math.Max(1, _solaBuf - 1));
            _fadeIn[i] = (float)(s * s);
            _fadeOut[i] = 1 - _fadeIn[i];
        }
    }

    /// <summary>Converts one block (BlockFrame samples at the mic's rate) and returns the same amount.</summary>
    public float[] Process(ReadOnlySpan<float> blockIn)
    {
        var voice = _m.VoiceInfo ?? throw new InvalidOperationException("No AI voice loaded");
        var block = blockIn.ToArray();
        Gate(block);

        Shift(_inputWav, block);
        Shift(_input16, _to16.Process(block));

        // ---- What's being said (ContentVec), 50 frames a second ----
        bool v2 = voice.Version == "v2";
        int dim = v2 ? 768 : 256;
        float[] feats; int frames;
        using (var r = _m.ContentVec.Run(new[] { NamedOnnxValue.CreateFromTensor("source", new DenseTensor<float>(_input16, new[] { 1, _total16 })) }))
        {
            var t = (DenseTensor<float>)r.First(x => x.Name == (v2 ? "v2" : "v1")).AsTensor<float>();
            frames = t.Dimensions[1];
            feats = t.Buffer.ToArray();
        }

        // ---- Your pitch (RMVPE) for the newest part, kept in a rolling cache ----
        int f0Len = _block16 + 800;
        f0Len = 5120 * ((f0Len - 1) / 5120 + 1) - 160;
        f0Len = Math.Min(f0Len, _total16);
        var mel = _mel.Compute(_input16.AsSpan(_total16 - f0Len), out int melFrames);
        int padded = 32 * ((melFrames - 1) / 32 + 1);
        var melIn = new float[MelSpectrogram.Mels * padded];
        for (int m = 0; m < MelSpectrogram.Mels; m++) Array.Copy(mel, m * melFrames, melIn, m * padded, melFrames);
        float[] f0;
        using (var r = _m.Rmvpe.Run(new[] { NamedOnnxValue.CreateFromTensor(_m.Rmvpe.InputMetadata.Keys.First(), new DenseTensor<float>(melIn, new[] { 1, MelSpectrogram.Mels, padded })) }))
        {
            var t = (DenseTensor<float>)r.First().AsTensor<float>();
            f0 = Pitch.Decode(t.Buffer.Span, melFrames);
        }
        Pitch.FillAndShift(f0, PitchShift);
        int shift = _block16 / 160;
        Array.Copy(_cachePitch, shift, _cachePitch, 0, _cachePitch.Length - shift);
        Array.Copy(_cachePitchF, shift, _cachePitchF, 0, _cachePitchF.Length - shift);
        int n = f0.Length, keep = n - 4;
        for (int i = 0; i < keep; i++)
        {
            _cachePitch[_cachePitch.Length - keep + i] = Pitch.Coarse(f0[3 + i]);
            _cachePitchF[_cachePitchF.Length - keep + i] = f0[3 + i];
        }

        // ---- Synthesize the newest frames in the AI voice ----
        int pLen = _total16 / 160;
        var phone = new float[pLen * dim];
        for (int j = 0; j < pLen; j++)
        {
            int src = Math.Min(j / 2, frames - 1);
            Array.Copy(feats, src * dim, phone, j * dim, dim);
        }
        var pitch = new long[pLen];
        var pitchf = new float[pLen];
        Array.Copy(_cachePitch, _cachePitch.Length - pLen, pitch, 0, pLen);
        Array.Copy(_cachePitchF, _cachePitchF.Length - pLen, pitchf, 0, pLen);
        var rnd = new float[192 * pLen];
        for (int i = 0; i < rnd.Length; i++) rnd[i] = Gaussian();
        int skipHead = _extra / _zc;
        int returnLength = (BlockFrame + _solaBuf + _solaSearch) / _zc;
        float[] audio;
        var inputs = new[]
        {
            NamedOnnxValue.CreateFromTensor("phone", new DenseTensor<float>(phone, new[] { 1, pLen, dim })),
            NamedOnnxValue.CreateFromTensor("phone_lengths", new DenseTensor<long>(new long[] { pLen }, new[] { 1 })),
            NamedOnnxValue.CreateFromTensor("pitch", new DenseTensor<long>(pitch, new[] { 1, pLen })),
            NamedOnnxValue.CreateFromTensor("pitchf", new DenseTensor<float>(pitchf, new[] { 1, pLen })),
            NamedOnnxValue.CreateFromTensor("sid", new DenseTensor<long>(new long[] { 0 }, new[] { 1 })),
            NamedOnnxValue.CreateFromTensor("rnd", new DenseTensor<float>(rnd, new[] { 1, 192, pLen })),
            NamedOnnxValue.CreateFromTensor("skip_head", new DenseTensor<long>(new long[] { skipHead }, new[] { 1 })),
            NamedOnnxValue.CreateFromTensor("return_length", new DenseTensor<long>(new long[] { returnLength }, new[] { 1 })),
        };
        using (var r = _m.Voice!.Run(inputs))
            audio = ((DenseTensor<float>)r.First().AsTensor<float>()).Buffer.ToArray();

        int outLen = returnLength * _zc;
        if (voice.SampleRate != _sr)
        {
            if (_fromModel == null || _fromModel.InRate != voice.SampleRate) _fromModel = new Resampler(voice.SampleRate, _sr);
            audio = _fromModel.Chunk(audio, outLen);
        }
        else if (audio.Length != outLen) Array.Resize(ref audio, outLen);

        MatchLoudness(audio, _inputWav.AsSpan(_extra, Math.Min(outLen, _total - _extra)));
        // Soft ceiling: the model can overshoot a little on loud syllables.
        for (int i = 0; i < audio.Length; i++)
        {
            float x = audio[i], ax = Math.Abs(x);
            if (ax > 0.9f) audio[i] = Math.Sign(x) * (0.9f + 0.1f * MathF.Tanh((ax - 0.9f) / 0.1f));
        }
        return Sola(audio);
    }

    float Gaussian()
    {
        double u1 = 1.0 - _rng.NextDouble(), u2 = _rng.NextDouble();
        return (float)(Math.Sqrt(-2 * Math.Log(u1)) * Math.Cos(2 * Math.PI * u2));
    }

    static void Shift(float[] buf, ReadOnlySpan<float> add)
    {
        if (add.Length >= buf.Length) { add[^buf.Length..].CopyTo(buf); return; }
        Array.Copy(buf, add.Length, buf, 0, buf.Length - add.Length);
        add.CopyTo(buf.AsSpan(buf.Length - add.Length));
    }

    /// <summary>Silences 10 ms frames below the gate (and not next to a louder one).</summary>
    void Gate(float[] block)
    {
        int frames = block.Length / _zc;
        var loud = new bool[frames];
        double thr = Math.Pow(10, GateDb / 20);
        for (int f = 0; f < frames; f++)
        {
            double s = 0;
            for (int i = f * _zc; i < (f + 1) * _zc; i++) s += block[i] * block[i];
            loud[f] = Math.Sqrt(s / _zc) >= thr;
        }
        for (int f = 0; f < frames; f++)
        {
            bool keep = loud[f] || (f > 0 && loud[f - 1]) || (f + 1 < frames && loud[f + 1]);
            if (!keep) Array.Clear(block, f * _zc, _zc);
        }
    }

    /// <summary>Gives the AI voice the loudness envelope of your real voice (RVC's volume mix at 0).</summary>
    void MatchLoudness(float[] audio, ReadOnlySpan<float> input)
    {
        int frames = audio.Length / _zc;
        if (frames == 0) return;
        var gain = new float[frames];
        for (int f = 0; f < frames; f++)
        {
            int c = f * _zc + _zc / 2, a = Math.Max(0, c - 2 * _zc), b = Math.Min(audio.Length, c + 2 * _zc);
            double si = 0, so = 0;
            for (int i = a; i < b; i++) { so += audio[i] * audio[i]; if (i < input.Length) si += input[i] * input[i]; }
            double ri = Math.Sqrt(si / (b - a)), ro = Math.Max(Math.Sqrt(so / (b - a)), 1e-3);
            gain[f] = (float)Math.Min(ri / ro, 8);
        }
        for (int i = 0; i < audio.Length; i++)
        {
            double pos = (double)i / _zc - 0.5;
            int f0 = Math.Clamp((int)Math.Floor(pos), 0, frames - 1), f1 = Math.Min(f0 + 1, frames - 1);
            double t = Math.Clamp(pos - f0, 0, 1);
            audio[i] *= (float)(gain[f0] + (gain[f1] - gain[f0]) * t);
        }
    }

    /// <summary>Joins this block to the last: finds the best-matching offset, crossfades, keeps the tail.</summary>
    float[] Sola(float[] infer)
    {
        int best = 0; double bestScore = double.NegativeInfinity;
        for (int k = 0; k <= _solaSearch; k++)
        {
            double nom = 0, den = 0;
            for (int j = 0; j < _solaBuf; j++) { float x = infer[k + j]; nom += x * _solaBuffer[j]; den += x * x; }
            double score = nom / Math.Sqrt(den + 1e-8);
            if (score > bestScore) { bestScore = score; best = k; }
        }
        var outBlock = new float[BlockFrame];
        for (int i = 0; i < BlockFrame; i++)
        {
            float v = infer[best + i];
            if (i < _solaBuf) v = v * _fadeIn[i] + _solaBuffer[i] * _fadeOut[i];
            outBlock[i] = v;
        }
        _solaBuffer = infer.AsSpan(best + BlockFrame, _solaBuf).ToArray();
        return outBlock;
    }
}

/// <summary>
/// Runs AI voices in the background: reads your voice from the audio engine (the bridge file),
/// converts it, and writes it back for the engine to play a fixed delay later.
/// </summary>
public sealed class RvcEngine : IDisposable
{
    Thread? _thread;
    volatile bool _stop;
    volatile AiVoice? _voice;
    volatile bool _voiceChanged;
    readonly object _lock = new();

    public bool PreferGpu { get; set; } = true;
    public double BlockSeconds { get; set; } = 0.25;

    /// <summary>True while the app wants the engine to send your voice to it (the AI voice is on).</summary>
    public static volatile bool Active;

    public string Status { get; private set; } = "";
    public bool Running { get; private set; }
    public event Action? StatusChanged;
    /// <summary>The engine stopped by itself (an error). Active is already false.</summary>
    public event Action? Failed;

    void SetStatus(string s) { Status = s; StatusChanged?.Invoke(); }

    public void Start(AiVoice voice)
    {
        lock (_lock)
        {
            _voice = voice;
            _voiceChanged = true;
            if (_thread is { IsAlive: true }) return;
            _stop = false;
            Active = true;
            _thread = new Thread(Loop) { IsBackground = true, Name = "WaveFix AI voice", Priority = ThreadPriority.AboveNormal };
            _thread.Start();
        }
    }

    /// <summary>Switches voice (or its pitch) without stopping.</summary>
    public void SetVoice(AiVoice voice) { _voice = voice; _voiceChanged = true; }

    public void Stop()
    {
        lock (_lock)
        {
            Active = false;
            _stop = true;
            _thread?.Join(3000);
            _thread = null;
            Running = false;
        }
        SetStatus("");
    }

    void Loop()
    {
        AudioBridge? bridge = null;
        RvcModels? models = null;
        try
        {
            SetStatus("Starting the AI voice…");
            bridge = AudioBridge.Create();
            models = new RvcModels(PreferGpu);
            RvcPipeline? pipe = null;
            int rate = 0;
            long pos = -1;
            int delay = 0;
            double worstMs = 0;
            int late = 0;
            var sw = new Stopwatch();
            long lastStatus = 0;
            while (!_stop)
            {
                bridge.RvcEngineBeat();
                if (_voiceChanged)
                {
                    _voiceChanged = false;
                    var v = _voice!;
                    if (models.VoiceInfo?.File != v.File) { SetStatus($"Loading {v.Name}…"); models.LoadVoice(v); pipe = null; }
                    else models.LoadVoice(v);
                    if (pipe != null) pipe.PitchShift = v.Pitch;
                }
                int pr = bridge.PluginRate;
                long written = bridge.RvcInputWritten;
                if (pr <= 0 || !bridge.PluginAlive)
                {
                    if (Environment.TickCount64 - lastStatus > 1000) { lastStatus = Environment.TickCount64; SetStatus("Waiting for your mic (it's used as soon as an app listens to it)."); Running = false; }
                    Thread.Sleep(20);
                    continue;
                }
                if (pipe == null || pr != rate)
                {
                    rate = pr;
                    SetStatus("Warming up…");
                    bool gpu = models.Device == "graphics card";
                    // The processor is 3-4x slower than a graphics card: bigger blocks keep up.
                    pipe = new RvcPipeline(models, rate, gpu ? BlockSeconds : Math.Max(BlockSeconds, 0.4), gpu ? 1.5 : 0.75) { PitchShift = _voice!.Pitch };
                    // Time a few conversions to choose a delay this PC can keep up with.
                    var silence = new float[pipe.BlockFrame];
                    worstMs = 0;
                    // The first runs are slow (the graphics card compiles its work); time the later ones.
                    for (int i = 0; i < 8 && !_stop; i++)
                    {
                        bridge.RvcEngineBeat();
                        sw.Restart(); pipe.Process(silence); sw.Stop();
                        if (i >= 4) worstMs = Math.Max(worstMs, sw.Elapsed.TotalMilliseconds);
                    }
                    delay = DelayFor(pipe.BlockFrame, worstMs, rate);
                    pos = -1;
                    late = 0;
                    ChangeLog.Write($"AI voice: {_voice!.Name} on the {models.Device}, block {pipe.BlockFrame * 1000 / rate} ms, conversion {worstMs:0} ms, delay {delay * 1000 / rate} ms");
                }

                // Follow the plugin's timeline; jump ahead if we fell far behind (or it restarted).
                if (pos < 0 || pos > written || written - pos > AudioBridge.RingLen / 2) pos = written;
                if (written - pos > 3L * pipe.BlockFrame) { pos = written - pipe.BlockFrame; late++; }
                if (written - pos < pipe.BlockFrame) { Thread.Sleep(2); continue; }

                var block = new float[pipe.BlockFrame];
                bridge.ReadRvcInput(pos, block);
                sw.Restart();
                var outBlock = pipe.Process(block);
                sw.Stop();
                bridge.WriteRvcOutput(pos, outBlock);
                bridge.RvcDelay = delay;
                bridge.RvcOutputWritten = pos + pipe.BlockFrame;
                bridge.RvcEngineBeat();

                // Late: the engine already played part of this block. A few in a row: more delay.
                long playing = bridge.RvcInputWritten - delay;
                if (playing > pos) { if (++late >= 3) { delay = Math.Min(delay + rate / 20, AudioBridge.RingLen / 2); late = 0; ChangeLog.Write($"AI voice: running late, delay now {delay * 1000 / rate} ms"); } }
                else late = Math.Max(0, late - 1);
                pos += pipe.BlockFrame;

                Running = true;
                if (Environment.TickCount64 - lastStatus > 1000)
                {
                    lastStatus = Environment.TickCount64;
                    double ms = sw.Elapsed.TotalMilliseconds, blockMs = pipe.BlockFrame * 1000.0 / rate;
                    string speed = ms > blockMs ? $" This PC is too slow for real time ({ms:0} ms per {blockMs:0} ms of voice)." : "";
                    SetStatus($"On, using your {models.Device}. Delay {delay * 1000 / rate} ms.{speed}");
                }
            }
        }
        catch (Exception ex)
        {
            ChangeLog.Write($"AI voice stopped: {ex}");
            Active = false;
            SetStatus($"The AI voice stopped: {ex.Message}");
            Failed?.Invoke();
        }
        finally
        {
            Running = false;
            models?.Dispose();
            bridge?.Dispose();
        }
    }

    /// <summary>Block, plus the time a conversion takes with headroom, plus the engine's own buffer.</summary>
    static int DelayFor(int block, double worstMs, int rate) =>
        block + (int)(rate * (worstMs * 1.4 + 40) / 1000);

    public void Dispose() => Stop();
}
