namespace WaveFix.Rvc;

/// <summary>
/// Sample rate conversion by an exact ratio (polyphase windowed sinc). Used as a stream (the mic
/// to 16 kHz, block after block) or on a whole chunk (a voice model's output to the mic's rate).
/// </summary>
public sealed class Resampler
{
    readonly int _up, _down;          // out/in = _up/_down (reduced)
    readonly int _half;               // input samples on each side of an output sample
    readonly float[][] _phases;       // [phase][2*_half] filter taps
    // Streaming state: input history and the next output's position (in 1/_up input samples).
    float[] _hist = Array.Empty<float>();
    long _histStart;                  // absolute input index of _hist[0]
    int _histLen;
    long _nextOut;                    // absolute index of the next output sample

    public int InRate { get; }
    public int OutRate { get; }

    public Resampler(int inRate, int outRate, int zeroCrossings = 16)
    {
        InRate = inRate; OutRate = outRate;
        int g = Gcd(inRate, outRate);
        _up = outRate / g; _down = inRate / g;
        double ratio = (double)outRate / inRate;
        double fc = Math.Min(1.0, ratio) * 0.94; // cutoff, as a fraction of the input's Nyquist
        _half = (int)Math.Ceiling(zeroCrossings / fc);
        _phases = new float[_up][];
        for (int p = 0; p < _up; p++)
        {
            // Output at input position base + p/_up; taps for inputs base-_half+1 .. base+_half.
            var taps = new float[2 * _half];
            double frac = (double)p / _up, sum = 0;
            for (int k = 0; k < taps.Length; k++)
            {
                double x = (k - _half + 1) - frac;              // input offset from the output's position
                double w = Kaiser(x / (_half + 1), 9.0);
                double s = x == 0 ? 1 : Math.Sin(Math.PI * fc * x) / (Math.PI * fc * x);
                taps[k] = (float)(fc * s * w);
                sum += taps[k];
            }
            // Unity gain at DC for every phase (no ripple from rounding).
            for (int k = 0; k < taps.Length; k++) taps[k] = (float)(taps[k] / sum);
            _phases[p] = taps;
        }
    }

    static int Gcd(int a, int b) { while (b != 0) (a, b) = (b, a % b); return a; }

    static double Kaiser(double x, double beta)
    {
        if (Math.Abs(x) >= 1) return 0;
        return I0(beta * Math.Sqrt(1 - x * x)) / I0(beta);
    }

    static double I0(double x)
    {
        double sum = 1, term = 1, y = x * x / 4;
        for (int k = 1; k < 50; k++) { term *= y / (k * k); sum += term; if (term < sum * 1e-12) break; }
        return sum;
    }

    /// <summary>Samples of delay the stream adds (in output samples, rounded up).</summary>
    public int StreamLatency => (int)Math.Ceiling((double)_half * _up / _down);

    /// <summary>Feeds input and returns every output sample that can now be computed.</summary>
    public float[] Process(ReadOnlySpan<float> input)
    {
        // Append to history.
        if (_histLen + input.Length > _hist.Length) Array.Resize(ref _hist, Math.Max(_hist.Length * 2, _histLen + input.Length + 4096));
        input.CopyTo(_hist.AsSpan(_histLen));
        _histLen += input.Length;
        long inEnd = _histStart + _histLen;

        var outList = new List<float>(input.Length * _up / _down + 2);
        while (true)
        {
            // Output n sits at input position n * _down / _up.
            long num = _nextOut * _down;
            long basePos = num / _up;
            int phase = (int)(num % _up);
            if (basePos + _half >= inEnd) break;          // needs future input
            outList.Add(Dot(basePos, phase));
            _nextOut++;
        }

        // Drop history no longer needed.
        long keepFrom = _nextOut * _down / _up - _half - 1;
        int drop = (int)Math.Clamp(keepFrom - _histStart, 0, _histLen);
        if (drop > 0)
        {
            Array.Copy(_hist, drop, _hist, 0, _histLen - drop);
            _histLen -= drop;
            _histStart += drop;
        }
        return outList.ToArray();
    }

    float Dot(long basePos, int phase)
    {
        var taps = _phases[phase];
        long first = basePos - _half + 1;
        float acc = 0;
        for (int k = 0; k < taps.Length; k++)
        {
            long i = first + k - _histStart;
            if (i >= 0 && i < _histLen) acc += _hist[i] * taps[k];
        }
        return acc;
    }

    /// <summary>Resamples a whole chunk on its own (silence assumed outside it).</summary>
    public float[] Chunk(ReadOnlySpan<float> input, int outLength)
    {
        var o = new float[outLength];
        for (long n = 0; n < outLength; n++)
        {
            long num = n * _down;
            long basePos = num / _up;
            var taps = _phases[(int)(num % _up)];
            long first = basePos - _half + 1;
            float acc = 0;
            int k0 = (int)Math.Max(0, -first), k1 = (int)Math.Min(taps.Length, input.Length - first);
            for (int k = k0; k < k1; k++) acc += input[(int)(first + k)] * taps[k];
            o[n] = acc;
        }
        return o;
    }
}

/// <summary>The log-mel spectrogram RMVPE (the pitch detector) reads: 16 kHz, 1024-point FFT,
/// hop 160, 128 HTK mel bands 30 Hz to 8 kHz with Slaney area normalization, log(max(x, 1e-5)).
/// Matches RVC's MelSpectrogram (torch.stft, centered with reflect padding, periodic Hann).</summary>
public sealed class MelSpectrogram
{
    public const int Rate = 16000, NFft = 1024, Hop = 160, Mels = 128;
    readonly float[] _window = new float[NFft];
    readonly float[,] _basis; // [mel, bin]
    readonly int[] _binLo = new int[Mels], _binHi = new int[Mels];
    readonly Fft _fft = new(NFft);

    public MelSpectrogram()
    {
        for (int i = 0; i < NFft; i++) _window[i] = (float)(0.5 - 0.5 * Math.Cos(2 * Math.PI * i / NFft));
        _basis = MelBasis(Rate, NFft, Mels, 30, 8000);
        for (int m = 0; m < Mels; m++)
        {
            int lo = -1, hi = -1;
            for (int b = 0; b <= NFft / 2; b++) if (_basis[m, b] != 0) { if (lo < 0) lo = b; hi = b; }
            _binLo[m] = Math.Max(lo, 0); _binHi[m] = hi;
        }
    }

    /// <summary>librosa.filters.mel(sr, n_fft, n_mels, fmin, fmax, htk=True), norm="slaney".</summary>
    public static float[,] MelBasis(int sr, int nFft, int nMels, double fmin, double fmax)
    {
        static double HzToMel(double f) => 2595.0 * Math.Log10(1.0 + f / 700.0);
        static double MelToHz(double m) => 700.0 * (Math.Pow(10, m / 2595.0) - 1.0);
        int bins = nFft / 2 + 1;
        var melF = new double[nMels + 2];
        double mMin = HzToMel(fmin), mMax = HzToMel(fmax);
        for (int i = 0; i < melF.Length; i++) melF[i] = MelToHz(mMin + (mMax - mMin) * i / (nMels + 1));
        var w = new float[nMels, bins];
        for (int i = 0; i < nMels; i++)
        {
            double fd0 = melF[i + 1] - melF[i], fd1 = melF[i + 2] - melF[i + 1];
            double enorm = 2.0 / (melF[i + 2] - melF[i]);
            for (int b = 0; b < bins; b++)
            {
                double f = (double)sr / 2 * b / (bins - 1);
                double lower = (f - melF[i]) / fd0, upper = (melF[i + 2] - f) / fd1;
                w[i, b] = (float)(Math.Max(0, Math.Min(lower, upper)) * enorm);
            }
        }
        return w;
    }

    /// <summary>Frames = audio.Length / Hop + 1. Returns [mel * frames + frame] (mel-major, like [128, T]).</summary>
    public float[] Compute(ReadOnlySpan<float> audio, out int frames)
    {
        int n = audio.Length, pad = NFft / 2;
        frames = n / Hop + 1;
        var mel = new float[Mels * frames];
        var re = new double[NFft];
        var im = new double[NFft];
        var mag = new double[NFft / 2 + 1];
        for (int t = 0; t < frames; t++)
        {
            int start = t * Hop - pad;
            for (int i = 0; i < NFft; i++)
            {
                int j = start + i;
                // Reflect padding (no edge repeat), as torch.stft(center=True, pad_mode="reflect").
                if (j < 0) j = -j;
                if (j >= n) j = 2 * (n - 1) - j;
                j = Math.Clamp(j, 0, n - 1);
                re[i] = audio[j] * _window[i];
                im[i] = 0;
            }
            _fft.Forward(re, im);
            for (int b = 0; b <= NFft / 2; b++) mag[b] = Math.Sqrt(re[b] * re[b] + im[b] * im[b]);
            for (int m = 0; m < Mels; m++)
            {
                double s = 0;
                for (int b = _binLo[m]; b <= _binHi[m]; b++) s += _basis[m, b] * mag[b];
                mel[m * frames + t] = (float)Math.Log(Math.Max(s, 1e-5));
            }
        }
        return mel;
    }
}

/// <summary>Radix-2 complex FFT (in place).</summary>
public sealed class Fft
{
    readonly int _n;
    readonly int[] _rev;
    readonly double[] _cos, _sin;

    public Fft(int n)
    {
        _n = n;
        int bits = (int)Math.Log2(n);
        _rev = new int[n];
        for (int i = 0; i < n; i++) { int r = 0; for (int b = 0; b < bits; b++) r |= ((i >> b) & 1) << (bits - 1 - b); _rev[i] = r; }
        _cos = new double[n / 2]; _sin = new double[n / 2];
        for (int i = 0; i < n / 2; i++) { _cos[i] = Math.Cos(-2 * Math.PI * i / n); _sin[i] = Math.Sin(-2 * Math.PI * i / n); }
    }

    public void Forward(double[] re, double[] im)
    {
        for (int i = 0; i < _n; i++)
        {
            int j = _rev[i];
            if (j > i) { (re[i], re[j]) = (re[j], re[i]); (im[i], im[j]) = (im[j], im[i]); }
        }
        for (int size = 2; size <= _n; size <<= 1)
        {
            int half = size / 2, step = _n / size;
            for (int start = 0; start < _n; start += size)
                for (int k = 0; k < half; k++)
                {
                    double wr = _cos[k * step], wi = _sin[k * step];
                    int a = start + k, b = a + half;
                    double tr = re[b] * wr - im[b] * wi, ti = re[b] * wi + im[b] * wr;
                    re[b] = re[a] - tr; im[b] = im[a] - ti;
                    re[a] += tr; im[a] += ti;
                }
        }
    }
}

/// <summary>Turns RMVPE's output (360 pitch bins per frame) into Hz, and Hz into the pitch inputs
/// an RVC model takes. Same maths as RVC's rmvpe.py decode and get_f0_post.</summary>
public static class Pitch
{
    static readonly double[] Cents = BuildCents();

    static double[] BuildCents()
    {
        var c = new double[368];
        for (int i = 0; i < 360; i++) c[i + 4] = 20 * i + 1997.3794084376191;
        return c;
    }

    /// <summary>salience: [frames * 360]. Returns Hz per frame (0 = unvoiced).</summary>
    public static float[] Decode(ReadOnlySpan<float> salience, int frames, float threshold = 0.03f)
    {
        var f0 = new float[frames];
        for (int t = 0; t < frames; t++)
        {
            var row = salience.Slice(t * 360, 360);
            int center = 0; float max = row[0];
            for (int i = 1; i < 360; i++) if (row[i] > max) { max = row[i]; center = i; }
            if (max <= threshold) { f0[t] = 0; continue; }
            double num = 0, den = 0;
            // Window of 9 bins around the peak (padded by 4 zero bins on each side).
            for (int k = -4; k <= 4; k++)
            {
                int i = center + k;
                double s = i >= 0 && i < 360 ? row[i] : 0;
                num += s * Cents[i + 4];
                den += s;
            }
            double cents = num / den;
            double hz = 10 * Math.Pow(2, cents / 1200);
            f0[t] = hz == 10 ? 0 : (float)hz;
        }
        return f0;
    }

    static readonly double MelMin = 1127 * Math.Log(1 + 50.0 / 700), MelMax = 1127 * Math.Log(1 + 1100.0 / 700);

    /// <summary>Hz to the model's coarse pitch (1..255).</summary>
    public static long Coarse(float f0)
    {
        double m = 1127 * Math.Log(1 + f0 / 700.0);
        if (m > 0) m = (m - MelMin) * 254 / (MelMax - MelMin) + 1;
        if (m <= 1) m = 1;
        if (m > 255) m = 255;
        return (long)Math.Round(m, MidpointRounding.ToEven);
    }

    /// <summary>Fills unvoiced frames by interpolating between voiced ones (as RVC does), then shifts.</summary>
    public static void FillAndShift(float[] f0, double semitones)
    {
        int n = f0.Length;
        int prev = -1;
        for (int i = 0; i < n; i++)
        {
            if (f0[i] <= 0) continue;
            if (prev < 0) for (int j = 0; j < i; j++) f0[j] = f0[i];
            else for (int j = prev + 1; j < i; j++) f0[j] = f0[prev] + (f0[i] - f0[prev]) * (j - prev) / (i - prev);
            prev = i;
        }
        if (prev >= 0) for (int j = prev + 1; j < n; j++) f0[j] = f0[prev];
        float factor = (float)Math.Pow(2, semitones / 12);
        for (int i = 0; i < n; i++) f0[i] *= factor;
    }
}
