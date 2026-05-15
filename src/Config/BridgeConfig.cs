namespace RazerMixerBridge.Config;

public sealed class BridgeConfig
{
    public AudioConfig Audio { get; init; } = new();
}

public sealed class AudioConfig
{
    /// <summary>
    /// PA sink name for the post-mixer destination, where the Razer mixer's audio gets routed to after passing through the hardware. Null = use the PA default sink captured at plug-in time
    /// </summary>
    public string? OutputSink { get; init; }

    /// <summary>
    /// Description shown in pavucontrol for the virtual fader sinks
    /// Use {n} as a placeholder for the channel number (1–4)
    /// </summary>
    public string SinkDescriptionTemplate { get; init; } = "Razer-Ch-{n}";

    /// <summary>
    /// Per-application stream-rehoming overrides applied when the mixer is plugged in. Key = sink-input's application.name
    /// Value = target sink name. Apps not listed here land on razer_fader4
    /// </summary>
    public Dictionary<string, string> Routing { get; init; } =
        new(StringComparer.OrdinalIgnoreCase);
}
