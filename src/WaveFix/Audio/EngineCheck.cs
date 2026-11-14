using System.Runtime.InteropServices;
using NAudio.CoreAudioApi;

namespace WaveFix;

/// <summary>
/// "Test my effects": records from the mic for a moment (so Windows runs its effects) and checks
/// the engine's own report. Adds no sound, so it's safe during a call. Only runs when asked (and
/// right after setup): recording makes Windows show its "mic in use" icon.
/// </summary>
public static class EngineCheck
{
    public sealed record Result(bool Good, string Title, string Detail, bool OfferTakeOff);

    public static Task<Result> RunAsync(string micId) => Task.Run(() => Run(micId));

    static Result Run(string micId)
    {
        if (!WaveFixEngine.IsInstalled)
            return new(false, "The audio engine isn't installed", "Click Set it up for me to install it.", false);
        if (!WaveFixEngine.IsAttached(micId))
            return new(false, "The engine isn't on this mic", "Click Set it up for me with this mic selected.", false);
        if (WaveFixEngine.HasJunk(micId))
            return new(false, "This mic's effect settings are damaged",
                "They contain entries that aren't effects (left over from Equalizer APO), so Windows runs none of this mic's effects. Click Set it up for me to repair them.", false);

        long started = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
        int buffers = 0;
        try
        {
            using var en = new MMDeviceEnumerator();
            using var dev = en.GetDevice(micId);
            using var cap = new WasapiCapture(dev);
            cap.DataAvailable += (_, _) => Interlocked.Increment(ref buffers);
            cap.StartRecording();
            // The engine reports as soon as it starts processing (and every 5 s while it runs).
            var until = DateTime.Now.AddSeconds(6);
            while (DateTime.Now < until)
            {
                Thread.Sleep(250);
                if (WaveFixEngine.ReadStatus(micId) is { } st && st.Time >= started - 6 && buffers > 0) break;
            }
            cap.StopRecording();
        }
        catch (Exception ex)
        {
            var hr = ex.HResult;
            if (hr == unchecked((int)0x80070005))
                return new(false, "Windows won't let WaveFix use the mic",
                    "Turn on Settings > Privacy > Microphone > Let desktop apps access your microphone, then test again.", false);
            return new(false, "Your mic can't be opened",
                $"Windows couldn't start recording from this mic (0x{hr:X8}: {ex.Message}). If that started after setting up WaveFix, take the engine off this mic to get the mic working again, and send the details for help.", true);
        }

        if (buffers == 0)
            return new(false, "The mic didn't send any audio", "Windows opened the mic but no sound came through. Check that it's plugged in and not disabled.", false);
        var status = WaveFixEngine.ReadStatus(micId);
        if (status == null || status.Time < started - 6)
            return new(false, "Effects aren't running on this mic",
                "Windows records from this mic without running its effects, so WaveFix can't change how it sounds. Some mics (often virtual or pro-audio drivers) don't support effects. Try another mic, or take the engine off this one.", true);
        if (status.Warnings.Length > 0)
            return new(false, "Effects are running, with a problem", string.Join("\n", status.Warnings), false);
        return new(true, "Your effects are working",
            $"Windows is running WaveFix's engine on this mic ({status.Channels} channel{(status.Channels == 1 ? "" : "s")}, {status.Rate:0} Hz), so everything you change in WaveFix reaches every app.", false);
    }
}
