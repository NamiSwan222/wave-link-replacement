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

    /// <summary>The voice's feature index, when it has one.</summary>
    public RvcIndex? Index { get; private set; }
    string? _indexPath;

    public void LoadVoice(AiVoice v)
    {
        if (!(VoiceInfo?.File == v.File && Voice != null))
        {
            var s = new InferenceSession(v.File, Options(_gpu));
            Voice?.Dispose();
            Voice = s;
        }
        VoiceInfo = v;
        if (v.IndexFile != _indexPath)
        {
            _indexPath = v.IndexFile;
            Index = null;
            if (v.IndexFile != null && System.IO.File.Exists(v.IndexFile))
            {
                try { Index = RvcIndex.Load(v.IndexFile); }
                catch (Exception ex) { ChangeLog.Write($"AI voice: index {v.IndexFile} not used: {ex.Message}"); }
            }
        }
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
    /// <summary>Speech (1) or pause (0) for each sample of _inputWav, smoothed.</summary>
    readonly float[] _maskWav;
    double _floorDb = -60;
    bool _floorKnown;
    int _holdFrames;
    float _env;
    /// <summary>Learn the mic's background-noise level and treat anything not clearly above it as silence.</summary>
    public bool AutoGate { get; set; } = true;
    /// <summary>True when the newest block was silence (a safe moment to change the delay).</summary>
    public bool LastBlockWasPause { get; private set; }
    /// <summary>The threshold in use right now (dBFS), for the app to show.</summary>
    public double ThresholdDb { get; private set; } = -50;
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
        _maskWav = new float[_total];
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
        var mask = Gate(block);
        LastBlockWasPause = mask.Length == 0 || mask.Max() < 0.01f;

        Shift(_inputWav, block);
        Shift(_maskWav, mask);
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
        // Index: pull the newest frames toward the voice's own training features (as RVC does from
        // skip_head/2 on, in these 50-per-second frames).
        if (_m.Index is { } index && index.Dim == dim && voice.IndexRate > 0.001)
        {
            float rate = (float)Math.Clamp(voice.IndexRate, 0, 1);
            for (int fr = Math.Max(0, _extra / _zc / 2); fr < frames; fr++)
                index.Blend(feats.AsSpan(fr * dim, dim), rate);
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
            NamedOnnxValue.CreateFromTensor("sid", new DenseTensor<long>(new long[] { Math.Clamp(voice.SpeakerId, 0, Math.Max(0, voice.Speakers - 1)) }, new[] { 1 })),
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

        MatchLoudness(audio, _inputWav.AsSpan(_extra, Math.Min(outLen, _total - _extra)), Math.Clamp(voice.LoudnessMix, 0, 1));
        // Clean pauses: whatever the model makes of background noise between your words (the
        // "babbling") is faded out, using the same speech/pause decision as the input.
        for (int i = 0; i < audio.Length; i++)
        {
            int at = _extra + i;
            audio[i] *= at < _total ? _maskWav[at] : _maskWav[^1];
        }
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

    /// <summary>
    /// Decides speech or pause for each 10 ms of the block and silences the pauses (the model turns
    /// any background noise into babble). With AutoGate the threshold follows the mic's own
    /// background noise: its level is tracked as a floor that drops quickly and rises slowly (about
    /// 1 dB a second, so talking doesn't push it up), and speech must be 9 dB above it. The gate
    /// holds 200 ms after speech (word endings) and fades in 5 ms, out 60 ms. Returns the mask.
    /// </summary>
    float[] Gate(float[] block)
    {
        int frames = block.Length / _zc;
        var mask = new float[block.Length];
        float attack = 1f / Math.Max(1, (int)(0.005 * _sr)), release = 1f / Math.Max(1, (int)(0.06 * _sr));
        for (int f = 0; f < frames; f++)
        {
            double s = 0;
            for (int i = f * _zc; i < (f + 1) * _zc; i++) s += block[i] * block[i];
            double db = 10 * Math.Log10(s / _zc + 1e-12);
            if (!_floorKnown) { _floorDb = db; _floorKnown = true; }
            else if (db < _floorDb) _floorDb += (db - _floorDb) * 0.3;
            else _floorDb += Math.Min(db - _floorDb, 0.01);
            double thr = AutoGate ? Math.Max(GateDb, _floorDb + 9) : GateDb;
            ThresholdDb = Math.Min(thr, -15);
            if (db >= ThresholdDb) _holdFrames = 20;
            else if (_holdFrames > 0) _holdFrames--;
            float target = _holdFrames > 0 ? 1f : 0f;
            for (int i = f * _zc; i < (f + 1) * _zc; i++)
            {
                _env = _env < target ? Math.Min(target, _env + attack) : Math.Max(target, _env - release);
                mask[i] = _env;
                block[i] *= _env;
            }
        }
        return mask;
    }

    /// <summary>Gives the AI voice the loudness envelope of your real voice (RVC's volume mix at 0).</summary>
    void MatchLoudness(float[] audio, ReadOnlySpan<float> input, double mix)
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
            // mix 0: your loudness; 1: the model's own (RVC's "volume envelope" setting).
            gain[f] = (float)Math.Min(Math.Pow(ri / ro, 1 - mix), 8);
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
    /// <summary>Noise gate for the voice sent to the model (dBFS); applied from the next block.</summary>
    public volatile float GateDb = -50;
    public volatile bool AutoGate = true;

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
            var recent = new Queue<double>(); // the last few seconds of conversion times (ms)
            long roomSince = 0;
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
                    if (models.Index != null) ChangeLog.Write($"AI voice: using {v.Name}'s index ({models.Index.Count} features)");
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
                    SetStatus("Finding the shortest delay this PC can keep up with…");
                    bool gpu = models.Device == "graphics card";
                    (pipe, worstMs) = ChooseBlock(models, rate, gpu ? 1.5 : 0.75, bridge, sw);
                    if (pipe == null) continue; // stopping
                    delay = DelayFor(pipe.BlockFrame, worstMs, rate);
                    recent.Clear();
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
                pipe.GateDb = GateDb;
                pipe.AutoGate = AutoGate;
                sw.Restart();
                var outBlock = pipe.Process(block);
                sw.Stop();
                bridge.WriteRvcOutput(pos, outBlock);
                bridge.RvcDelay = delay;
                bridge.RvcOutputWritten = pos + pipe.BlockFrame;
                bridge.RvcEngineBeat();

                // Late: the engine already played part of this block. A few in a row: more delay.
                long playing = bridge.RvcInputWritten - delay;
                if (playing > pos) { if (++late >= 3) { delay = Math.Min(delay + rate / 20, AudioBridge.RingLen / 2); late = 0; roomSince = 0; ChangeLog.Write($"AI voice: running late, delay now {delay * 1000 / rate} ms"); } }
                else late = Math.Max(0, late - 1);

                // Room to spare for 10 s (the PC got faster, e.g. a game closed): shorten the delay
                // to what's needed, but only during a pause, so the jump can't be heard.
                recent.Enqueue(sw.Elapsed.TotalMilliseconds);
                while (recent.Count > Math.Max(20, 6 * rate / pipe.BlockFrame)) recent.Dequeue();
                int needed = DelayFor(pipe.BlockFrame, recent.Max(), rate);
                if (delay > needed + rate * 30 / 1000)
                {
                    if (roomSince == 0) roomSince = Environment.TickCount64;
                    else if (Environment.TickCount64 - roomSince > 10_000 && pipe.LastBlockWasPause && late == 0)
                    {
                        delay = needed;
                        roomSince = 0;
                        ChangeLog.Write($"AI voice: running ahead, delay now {delay * 1000 / rate} ms");
                    }
                }
                else roomSince = 0;
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

    /// <summary>
    /// Picks the shortest block (so the shortest delay) this PC converts comfortably: each candidate
    /// is warmed up (a graphics card compiles its work on the first runs), then timed; the first
    /// whose slowest conversion takes at most 60% of the block wins. Returns null when stopping.
    /// </summary>
    (RvcPipeline? Pipe, double WorstMs) ChooseBlock(RvcModels models, int rate, double extraSec, AudioBridge bridge, Stopwatch sw)
    {
        double[] candidates = { 0.12, 0.16, 0.2, 0.25, 0.32, 0.4, 0.5, 0.65 };
        RvcPipeline? last = null;
        double lastWorst = 0;
        foreach (var sec in candidates)
        {
            if (_stop) return (null, 0);
            var pipe = new RvcPipeline(models, rate, sec, extraSec) { PitchShift = _voice!.Pitch };
            var silence = new float[pipe.BlockFrame];
            double worst = 0;
            for (int i = 0; i < 7 && !_stop; i++)
            {
                bridge.RvcEngineBeat();
                sw.Restart(); pipe.Process(silence); sw.Stop();
                if (i >= 3) worst = Math.Max(worst, sw.Elapsed.TotalMilliseconds);
            }
            last = pipe; lastWorst = worst;
            if (worst <= 0.6 * sec * 1000)
            {
                ChangeLog.Write($"AI voice: block {sec * 1000:0} ms keeps up (slowest conversion {worst:0} ms)");
                return (pipe, worst);
            }
        }
        ChangeLog.Write($"AI voice: this PC is slow for real time even with {candidates[^1] * 1000:0} ms blocks ({lastWorst:0} ms)");
        return (last, lastWorst);
    }

    /// <summary>Block, plus the time a conversion takes with headroom, plus the engine's own buffer.</summary>
    static int DelayFor(int block, double worstMs, int rate) =>
        block + (int)(rate * (worstMs * 1.4 + 40) / 1000);

    public void Dispose() => Stop();
}
