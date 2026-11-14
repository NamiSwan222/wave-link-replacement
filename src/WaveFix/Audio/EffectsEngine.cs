namespace WaveFix;

public static class EqPresets
{
    public const string Flat = "Flat";
    public const string Warm = "Warm";
    public const string Clear = "Clear";
    public const string Broadcast = "Broadcast";
    public const string Radio = "Radio";
    public const string Custom = "Custom";
    public static readonly string[] All = { Flat, Warm, Clear, Broadcast, Radio, Custom };

    /// <summary>One filter of a preset: kind is hp, lp, pk, ls or hs.</summary>
    public sealed record Filter(string Kind, double Hz, double Gain = 0, double? Q = null);

    public static IReadOnlyList<Filter> Filters(string preset) => preset switch
    {
        Warm => new Filter[] { new("ls", 180, 3), new("pk", 3500, -1.5, 1) },
        Clear => new Filter[] { new("pk", 300, -3, 1), new("pk", 3000, 2, 1), new("hs", 6000, 3) },
        Broadcast => new Filter[] { new("ls", 120, 2.5), new("pk", 400, -2.5, 1.2), new("pk", 2500, 2.5, 1), new("hs", 8000, 2) },
        Radio => new Filter[] { new("hp", 300), new("lp", 3400), new("pk", 1500, 4, 1) },
        _ => Array.Empty<Filter>(),
    };
}

/// <summary>
/// Sends the mic chain to WaveFix's audio engine, which runs it inside the Windows audio engine
/// (audiodg.exe). Nothing here touches audio samples: the app only writes the engine's config
/// file. Running there, not in an app, is what keeps the mic out of Discord's stream capture.
/// </summary>
public sealed class EffectsEngine
{
    public bool IsInstalled => WaveFixEngine.IsInstalled;
    public bool HasNoisePlugin => WaveFixEngine.HasNoisePlugin;
    public string NoisePlugin => WaveFixEngine.NoisePlugin;

    /// <summary>The engine is in this mic's endpoint effects (applied to every stream, raw mode included).</summary>
    public bool IsConnected(string endpointId) => WaveFixEngine.IsAttached(endpointId) && !WaveFixEngine.HasJunk(endpointId);

    public void Apply(AppSettings s, string micEndpointId)
    {
        // The engine watches its config file; it only runs once setup has installed it.
        if (WaveFixEngine.IsInstalled) WaveFixEngine.WriteConfig(micEndpointId, s);
    }
}
