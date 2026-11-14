using NAudio.CoreAudioApi;
using NAudio.Wave;

namespace WaveFix;

/// <summary>
/// "Hear myself": plays your mic, exactly as apps receive it (after every WaveFix effect, the AI
/// voice included), to your default speakers or headphones. Low delay: when audio piles up, the
/// backlog is skipped instead of growing.
///
/// This is ordinary app playback, so while it's on, a Discord screen share *with audio* includes
/// it (the page says so). The mic itself is unaffected.
/// </summary>
public sealed class MicMonitor : IDisposable
{
    WasapiCapture? _cap;
    WasapiOut? _out;
    Rvc.Resampler? _resample;
    readonly object _lock = new();
    // Mono ring of samples at the output rate.
    readonly float[] _ring = new float[1 << 17];
    long _write, _read;
    int _outRate = 48000, _inChannels = 1;

    /// <summary>0..1 (linear gain).</summary>
    public float Volume { get; set; } = 0.8f;
    public bool Running => _out != null;
    public string? Error { get; private set; }

    public void Start(string micId)
    {
        Stop();
        try
        {
            using var en = new MMDeviceEnumerator();
            var mic = en.GetDevice(micId);
            var spk = en.GetDefaultAudioEndpoint(DataFlow.Render, Role.Multimedia);
            var mix = spk.AudioClient.MixFormat;
            _outRate = mix.SampleRate;

            // Shared mode, like every other app: the mic's effects (WaveFix's engine) are applied.
            var cap = new WasapiCapture(mic, true, 10);
            var fmt = cap.WaveFormat;
            if (fmt.Encoding != WaveFormatEncoding.IeeeFloat && !(fmt.Encoding == WaveFormatEncoding.Extensible && fmt.BitsPerSample == 32))
                cap.WaveFormat = WaveFormat.CreateIeeeFloatWaveFormat(fmt.SampleRate, fmt.Channels);
            _inChannels = cap.WaveFormat.Channels;
            _resample = cap.WaveFormat.SampleRate == _outRate ? null : new Rvc.Resampler(cap.WaveFormat.SampleRate, _outRate);
            cap.DataAvailable += OnData;
            _write = _read = 0;

            var o = new WasapiOut(spk, AudioClientShareMode.Shared, true, 20);
            o.Init(new Output(this, mix.SampleRate, mix.Channels));
            cap.StartRecording();
            o.Play();
            _cap = cap; _out = o;
            Error = null;
        }
        catch (Exception ex)
        {
            Error = ex.Message;
            ChangeLog.Write($"Hear myself couldn't start: {ex.Message}");
            Stop();
        }
    }

    public void Stop()
    {
        try { _cap?.StopRecording(); } catch { }
        try { _cap?.Dispose(); } catch { }
        try { _out?.Stop(); _out?.Dispose(); } catch { }
        _cap = null; _out = null;
    }

    void OnData(object? sender, WaveInEventArgs e)
    {
        int frames = e.BytesRecorded / 4 / _inChannels;
        var mono = new float[frames];
        for (int f = 0; f < frames; f++)
        {
            float s = 0;
            for (int c = 0; c < _inChannels; c++) s += BitConverter.ToSingle(e.Buffer, (f * _inChannels + c) * 4);
            mono[f] = s / _inChannels;
        }
        var data = _resample?.Process(mono) ?? mono;
        lock (_lock)
        {
            foreach (var v in data) _ring[_write++ & (_ring.Length - 1)] = v;
            // Keep the delay small: if more than 120 ms is waiting, jump to the newest 30 ms.
            if (_write - _read > _outRate * 12 / 100) _read = _write - _outRate * 3 / 100;
        }
    }

    sealed class Output(MicMonitor m, int rate, int channels) : ISampleProvider
    {
        public WaveFormat WaveFormat { get; } = WaveFormat.CreateIeeeFloatWaveFormat(rate, channels);

        public int Read(float[] buffer, int offset, int count)
        {
            int frames = count / channels;
            float vol = Math.Clamp(m.Volume, 0, 2);
            lock (m._lock)
            {
                for (int f = 0; f < frames; f++)
                {
                    float v = m._read < m._write ? m._ring[m._read++ & (m._ring.Length - 1)] : 0f;
                    v = Math.Clamp(v * vol, -1f, 1f);
                    for (int c = 0; c < channels; c++) buffer[offset + f * channels + c] = v;
                }
            }
            return frames * channels;
        }
    }

    public void Dispose() => Stop();
}
