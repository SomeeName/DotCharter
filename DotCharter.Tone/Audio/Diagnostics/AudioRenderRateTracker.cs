namespace DotCharter.Tone.Audio.Diagnostics;

/// <summary>
///     Pure tracker for the EFFECTIVE render rate — how many frames the device
///     callback actually produced per wall-clock second over the window that just
///     closed. It is the health monitor's only signal for a SUSTAINED under-run:
///     one the per-poll staleness check in <see cref="AudioDropoutDetector" />
///     cannot see, because the callback never stops long enough to look stale, it
///     just keeps falling behind real time.
///     <para>
///         <b>The whole point of the type is that "I could not read the clock" is
///         a distinct value from "I read zero".</b> The monitor folded the two
///         together: a heartbeat whose render-clock read failed (the engine went
///         away between the poll and the heartbeat, or the read threw
///         <see cref="ObjectDisposedException" /> between devices) stored the
///         window's frame count as <c>0</c>, and the NEXT heartbeat then measured
///         the engine's whole lifetime of frames across one 30 s window. On a
///         ten-minute-old 48 kHz engine that reads as ~960 kHz, which trips the
///         2 % check and prints a WARNING that the audio thread "is not keeping
///         up" when nothing at all had happened. A diagnostic that fabricates the
///         fault it exists to report is worse than no diagnostic.
///     </para>
///     <para>
///         A frame count that went BACKWARDS is an engine REBUILD (a fresh
///         engine's render clock starts near zero), not an under-run, and yields
///         no rate either — the next window measures the new engine cleanly.
///     </para>
/// </summary>
public sealed class AudioRenderRateTracker
{
    private const long Unknown = -1;

    private long _lastFrames = Unknown;

    /// <summary>The rate over one closed window. <see cref="HaveRate" /> false
    /// means "nothing measurable this time" — never a rate of zero.</summary>
    public readonly record struct Reading(bool HaveRate, double EffectiveSampleRate);

    /// <summary>
    ///     Closes a window. <paramref name="renderFrames" /> is the render clock's
    ///     absolute frame count, or <c>null</c> when it could not be read at all.
    /// </summary>
    public Reading Observe(long? renderFrames, double elapsedMs)
    {
        var previous = _lastFrames;
        // An unread clock leaves the tracker UNKNOWN, never 0 — see the summary.
        _lastFrames = renderFrames ?? Unknown;

        if (renderFrames is not { } frames) return default;
        if (previous < 0) return default;              // no previous reading to span
        if (frames < previous) return default;         // engine rebuilt under us
        if (elapsedMs <= 0) return default;
        return new Reading(true, (frames - previous) * 1000.0 / elapsedMs);
    }

    /// <summary>Drops the reference reading (device closed / monitor paused), so
    /// the next window reports nothing rather than spanning the gap.</summary>
    public void Forget() => _lastFrames = Unknown;

    /// <summary>Default tolerance before the effective rate is called an
    /// under-run. 2 % of 48 kHz is ~1 kHz, well outside poll jitter over a 30 s
    /// window and well inside anything a listener would notice.</summary>
    public const double DefaultTolerance = 0.02;

    /// <summary>Whether a reading is far enough off the device rate to report.
    /// Refuses to judge without a rate or without a device rate to judge against —
    /// that "no answer" is the honest one, and returning true there is exactly
    /// how the fabricated warning above got out.</summary>
    public static bool IsSustainedUnderRun(Reading reading, int deviceSampleRate,
        double tolerance = DefaultTolerance)
    {
        if (!reading.HaveRate || deviceSampleRate <= 0) return false;
        return Math.Abs(reading.EffectiveSampleRate - deviceSampleRate) / deviceSampleRate > tolerance;
    }
}
