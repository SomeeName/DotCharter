namespace DotCharter.Tone.Audio.Diagnostics;

/// <summary>
///     The ONE output-latency figure the app prints: the calibrated measurement when there is
///     one, else the driver's estimate. It is OUTPUT latency — never call it round-trip, which
///     would include the input side. The buffer alone is a separate, smaller figure.
/// </summary>
public static class OutputLatency
{
    /// <summary>Milliseconds, or 0 when the engine has neither (a closed engine reports 0).</summary>
    public static double Ms(IAudioDeviceControl device) =>
        device.MeasuredOutputLatencyMs > 0 ? device.MeasuredOutputLatencyMs : Math.Max(0, device.EstimatedOutputLatencyMs);
}
