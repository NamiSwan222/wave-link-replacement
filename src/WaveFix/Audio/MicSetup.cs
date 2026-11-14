using NAudio.CoreAudioApi;
using NAudio.Wave;

namespace WaveFix;

/// <summary>What auto mic setup measured and recommends.</summary>
public sealed class MicSetupResult
{
    public double NoiseDb, SpeechDb, PeakDb, SpreadDb, CentroidHz, RumbleShare;
    public double VoicedShare;
    /// <summary>Change to the mic's Windows input level, in dB (0 = leave it).</summary>
    public double LevelChangeDb;
    public double BoostDb;
    public bool NoiseRemoval, Strong, LowCut, Leveler, Limiter;
    public double NoiseStrengthDb = 100;
    public string Tone = EqPresets.Flat;
    public List<(string What, string Why)> Changes { get; } = new();
    public string? Problem;
}

/// <summary>
/// Auto mic setup: records the raw mic (WaveFix's effects are switched off for the moment) for a
/// few seconds of quiet and a few seconds of normal talking, then picks the input level, boost,
/// noise removal, rumble filter, leveler and tone from what it measured.
/// </summary>
public static class MicSetup
{
    public const double QuietSeconds = 3, TalkSeconds = 7;

    /// <summary>Records both phases. <paramref name="level"/> gets the live peak (0..1) for a meter.</summary>
    public static async Task<(float[] Quiet, float[] Talk, int Rate)> RecordAsync(string micId, Action<string, double> phase, Action<float> level, CancellationToken ct)
    {
        using var en = new MMDeviceEnumerator();
        var dev = en.GetDevice(micId);
        using var cap = new WasapiCapture(dev, true, 20);
        if (cap.WaveFormat.Encoding != WaveFormatEncoding.IeeeFloat && !(cap.WaveFormat.Encoding == WaveFormatEncoding.Extensible && cap.WaveFormat.BitsPerSample == 32))
            cap.WaveFormat = WaveFormat.CreateIeeeFloatWaveFormat(cap.WaveFormat.SampleRate, cap.WaveFormat.Channels);
        int ch = cap.WaveFormat.Channels, rate = cap.WaveFormat.SampleRate;
        var all = new List<float>(rate * 12);
        object gate = new();
        cap.DataAvailable += (_, e) =>
        {
            int frames = e.BytesRecorded / 4 / ch;
            float peak = 0;
            lock (gate)
                for (int f = 0; f < frames; f++)
                {
                    float s = 0;
                    for (int c = 0; c < ch; c++) s += BitConverter.ToSingle(e.Buffer, (f * ch + c) * 4);
                    s /= ch;
                    all.Add(s);
                    peak = Math.Max(peak, Math.Abs(s));
                }
            level(peak);
        };
        cap.StartRecording();
        try
        {
            await Task.Delay(400, ct); // let the stream settle
            int quietStart; lock (gate) quietStart = all.Count;
            for (double t = 0; t < QuietSeconds; t += 0.1) { phase("quiet", t / QuietSeconds); await Task.Delay(100, ct); }
            int talkStart; lock (gate) talkStart = all.Count;
            for (double t = 0; t < TalkSeconds; t += 0.1) { phase("talk", t / TalkSeconds); await Task.Delay(100, ct); }
            int end; lock (gate) end = all.Count;
            lock (gate)
                return (all.GetRange(quietStart, talkStart - quietStart).ToArray(), all.GetRange(talkStart, end - talkStart).ToArray(), rate);
        }
        finally { cap.StopRecording(); }
    }

    static double Db(double power) => 10 * Math.Log10(power + 1e-12);

    static double[] FrameDb(float[] x, int frame)
    {
        var r = new double[x.Length / frame];
        for (int i = 0; i < r.Length; i++)
        {
            double s = 0;
            for (int j = i * frame; j < (i + 1) * frame; j++) s += x[j] * x[j];
            r[i] = Db(s / frame);
        }
        return r;
    }

    static double Percentile(IEnumerable<double> v, double p)
    {
        var a = v.OrderBy(x => x).ToArray();
        if (a.Length == 0) return double.NaN;
        return a[Math.Clamp((int)Math.Round(p * (a.Length - 1)), 0, a.Length - 1)];
    }

    /// <summary>Share of energy below 80 Hz, and the spectral centre (Hz), averaged over chunks.</summary>
    static (double LowShare, double Centroid) Spectrum(float[] x, int rate, Func<int, bool>? useChunk = null)
    {
        const int n = 4096;
        var fft = new Rvc.Fft(n);
        var re = new double[n]; var im = new double[n];
        double low = 0, total = 0, weighted = 0;
        int chunks = x.Length / n;
        for (int c = 0; c < chunks; c++)
        {
            if (useChunk != null && !useChunk(c * n)) continue;
            for (int i = 0; i < n; i++) { re[i] = x[c * n + i] * (0.5 - 0.5 * Math.Cos(2 * Math.PI * i / n)); im[i] = 0; }
            fft.Forward(re, im);
            for (int b = 1; b <= n / 2; b++)
            {
                double f = (double)b * rate / n, p = re[b] * re[b] + im[b] * im[b];
                total += p; weighted += p * f;
                if (f < 80) low += p;
            }
        }
        return total > 0 ? (low / total, weighted / total) : (0, 0);
    }

    /// <summary>
    /// Decides the settings. Targets: typical speech (90th percentile of 20 ms loudness) at -18 dBFS
    /// with peaks below -3 dBFS, noise removal when the room is audible after that, the leveler
    /// when speech loudness varies a lot, a tone that balances dull or harsh mics.
    /// </summary>
    public static MicSetupResult Analyze(float[] quiet, float[] talk, int rate, double levelDb, double minDb, double maxDb, double currentBoost, bool strongAvailable)
    {
        var r = new MicSetupResult();
        int frame = rate / 50;
        var q = FrameDb(quiet, frame).Skip(5).ToArray();
        var t = FrameDb(talk, frame);
        r.NoiseDb = Percentile(q, 0.5);
        var voiced = t.Where(d => d > r.NoiseDb + 10).ToArray();
        r.VoicedShare = t.Length == 0 ? 0 : (double)voiced.Length / t.Length;
        if (q.Length == 0 || t.Length == 0) { r.Problem = "The recording didn't work. Check the mic is plugged in and try again."; return r; }
        if (r.VoicedShare < 0.15)
        {
            r.Problem = Percentile(t, 0.9) < -65
                ? "WaveFix couldn't hear your mic at all. Check it isn't muted (on the mic itself too) and try again."
                : "WaveFix couldn't tell your voice from the background. Try again, and talk a bit louder or closer to the mic.";
            return r;
        }
        r.SpeechDb = Percentile(voiced, 0.9);
        r.SpreadDb = Percentile(voiced, 0.9) - Percentile(voiced, 0.3);
        r.PeakDb = 20 * Math.Log10(talk.Max(Math.Abs) + 1e-9);

        // ---- Level: Windows input level first, then mic boost for what's left ----
        double want = -18 - r.SpeechDb;
        want = Math.Min(want, -3 - r.PeakDb);              // never push peaks past -3 dBFS
        double newLevel = Math.Clamp(levelDb + want, minDb, maxDb);
        r.LevelChangeDb = Math.Abs(newLevel - levelDb) < 1.5 ? 0 : newLevel - levelDb;
        double rest = want - r.LevelChangeDb;
        r.BoostDb = Math.Abs(rest) < 1 ? currentBoost : Math.Round(Math.Clamp(currentBoost + rest, -6, 12) * 2) / 2;
        double applied = r.LevelChangeDb + (r.BoostDb - currentBoost);
        if (Math.Abs(r.LevelChangeDb) >= 1.5)
            r.Changes.Add(("Input level " + (r.LevelChangeDb > 0 ? $"up {r.LevelChangeDb:0} dB" : $"down {-r.LevelChangeDb:0} dB"),
                r.LevelChangeDb > 0 ? "Your voice was quiet for most apps." : "Your voice was loud enough to risk distortion."));
        if (Math.Abs(r.BoostDb - currentBoost) >= 0.5)
            r.Changes.Add(($"Mic boost {(r.BoostDb > 0 ? "+" : "")}{r.BoostDb:0.#} dB", "The mic's own level couldn't go far enough on its own."));
        if (r.Changes.Count == 0) r.Changes.Add(("Volume: no change", "Your level is already good."));

        // ---- Background noise (as it will be after the level change) ----
        double noiseAfter = r.NoiseDb + applied;
        double snr = r.SpeechDb - r.NoiseDb;
        if (noiseAfter > -62)
        {
            r.NoiseRemoval = true;
            r.Strong = strongAvailable && (noiseAfter > -50 || snr < 30);
            r.NoiseStrengthDb = snr < 20 ? 100 : 60;
            r.Changes.Add(($"Noise removal: {(r.Strong ? "Strong" : "Standard")}" + (r.Strong && r.NoiseStrengthDb < 100 ? $", up to {r.NoiseStrengthDb:0} dB" : ""),
                $"Others would hear your background noise ({noiseAfter:0} dB)."));
        }
        else { r.NoiseRemoval = false; r.Changes.Add(("Noise removal: off", "Your room is quiet; it would only change your voice.")); }

        // ---- Rumble (desk bumps, hum, fans) ----
        var (lowShare, _) = Spectrum(quiet, rate);
        r.RumbleShare = lowShare;
        r.LowCut = true;
        r.Changes.Add(("Rumble filter: on", lowShare > 0.3 ? "There's low rumble in your background." : "Removes desk bumps and hum; voices don't go that low."));

        // ---- Loudness changes while talking ----
        r.Leveler = r.SpreadDb > 15;
        if (r.Leveler) r.Changes.Add(("Leveler: on", $"Your loudness varied a lot ({r.SpreadDb:0} dB) while talking."));

        // ---- Tone ----
        double noiseFloor = r.NoiseDb + 10;
        var (_, centroid) = Spectrum(talk, rate, start =>
        {
            int f0 = start / frame, f1 = Math.Min(t.Length, (start + 4096) / frame);
            return f1 > f0 && t[f0..f1].Average() > noiseFloor;
        });
        r.CentroidHz = centroid;
        if (centroid > 0 && centroid < 900) { r.Tone = EqPresets.Clear; r.Changes.Add(("Voice tone: Clear", "Your mic sounds a bit dull or muffled.")); }
        else if (centroid > 2600) { r.Tone = EqPresets.Warm; r.Changes.Add(("Voice tone: Warm", "Your mic sounds a bit thin or harsh.")); }
        else { r.Tone = EqPresets.Flat; r.Changes.Add(("Voice tone: Natural", "Your mic's tone is balanced.")); }

        // ---- Peak protection when boosting ----
        r.Limiter = r.BoostDb > 0;
        if (r.Limiter) r.Changes.Add(("Peak protection: on", "Stops loud moments distorting after the boost."));
        return r;
    }
}
