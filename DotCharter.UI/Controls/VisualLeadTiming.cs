namespace DotCharter.UI.Controls;

/// <summary>
///     The render-clock LEAD: how far ahead of the audio playhead the picture is
///     drawn, so that after presentation latency (frame submit → compositor/DWM
///     commit → scanout) it lands on glass in sync with the sound. It is the exact
///     mirror of the audio-latency-compensated playhead
///     (JuceAudioPlaybackService's drift-corrected clock → AudioPlaybackCoordinator):
///     audio is compensated backwards, video forwards, and they meet.
///
///     <para>A constant delay is fully compensable this way; JITTER is not — which
///     is why the present pump reports interval jitter as a first-class diagnostic
///     next to the mean.</para>
///
///     <para><b>Both presentation paths use this now.</b> It began as a composed-path
///     fix, on the theory that only the composed pipeline adds presentation latency.
///     That is wrong in degree, not in kind: the native swapchain still queues a
///     frame, waits for vblank under FIFO, and scans out — it is *less* latency, not
///     none, and it was being compensated by exactly zero while the composed path was
///     compensated properly. The difference is that the composed path can MEASURE its
///     latency (the compositor tells it when the frame committed) and the native path
///     cannot. So:</para>
///     <list type="bullet">
///         <item><b>Composed:</b> lead = measured EMA + the user's trim.</item>
///         <item><b>Native:</b> nothing publishes a measurement, so
///               <see cref="MeasuredPresentLatencyMs"/> stays 0 and the lead is just
///               the user's trim — a manual knob, which is the honest answer when
///               there is nothing to measure.</item>
///     </list>
///     <para>The 2D timeline is Avalonia-composited and therefore eats the same DWM
///     latency, so it applies the same lead.</para>
///
///     <para>Static-with-setters channel like <see cref="Preview3DPacing" />: the UI
///     thread (present pump / the Settings slider) writes, the render threads read
///     once per frame.</para>
/// </summary>
public static class VisualLeadTiming
{
    /// <summary>Upper bound for the applied lead — a runaway measurement
    /// (compositor stall) must never yank the scene seconds ahead.</summary>
    public const double MaxLeadMs = 50;

    private const double EmaAlpha = 0.1;

    private static double _measuredPresentLatencyMs;
    private static long _leadOverrideBits = BitConverter.DoubleToInt64Bits(-1.0);
    private static long _leadTrimBits;

    /// <summary>EMA of the measured frame-submit → composition-commit
    /// latency, written by the present pump per completed present. The DWM
    /// scanout adds up to one more refresh interval on top — that remainder
    /// is what <see cref="LeadTrimMs" /> is for.</summary>
    public static double MeasuredPresentLatencyMs =>
        Volatile.Read(ref _measuredPresentLatencyMs);

    /// <summary>Owner trim (ms, may be negative) added on top of the
    /// measured/override lead — live-tunable while playing.</summary>
    public static double LeadTrimMs
    {
        get => BitConverter.Int64BitsToDouble(Volatile.Read(ref _leadTrimBits));
        set => Volatile.Write(ref _leadTrimBits, BitConverter.DoubleToInt64Bits(value));
    }

    /// <summary>&lt; 0 (default) = auto: use the measured latency. ≥ 0 =
    /// fixed lead in ms (0 disables the lead entirely).</summary>
    public static double LeadOverrideMs
    {
        get => BitConverter.Int64BitsToDouble(Volatile.Read(ref _leadOverrideBits));
        set => Volatile.Write(ref _leadOverrideBits, BitConverter.DoubleToInt64Bits(value));
    }

    /// <summary>The lead the render thread applies this frame.</summary>
    public static double EffectiveLeadMs =>
        ComputeLeadMs(MeasuredPresentLatencyMs, LeadOverrideMs, LeadTrimMs);

    /// <summary>Folds one present-latency sample into the EMA (pump, UI
    /// thread).</summary>
    internal static void PublishMeasuredPresentLatency(double sampleMs)
    {
        var current = Volatile.Read(ref _measuredPresentLatencyMs);
        var next = current <= 0 ? sampleMs : current + (sampleMs - current) * EmaAlpha;
        Volatile.Write(ref _measuredPresentLatencyMs, next);
    }

    /// <summary>
    ///     Test seam: clears the EMA and both owner knobs. This class is
    ///     PROCESS-GLOBAL mutable state, so a test that reads
    ///     <see cref="EffectiveLeadMs" /> inherits whatever ran before it — and the
    ///     EMA does not move at all when the incoming sample already equals it, which
    ///     made the idle-gate tests pass or fail purely on their execution order.
    /// </summary>
    internal static void ResetForTests()
    {
        Volatile.Write(ref _measuredPresentLatencyMs, 0);
        LeadOverrideMs = -1.0;
        LeadTrimMs = 0.0;
    }

    /// <summary>Pure lead rule (unit-tested in
    /// <c>VisualLeadTimingTests</c>): override ≥ 0 replaces the
    /// measurement, the trim applies on top of either, and the result clamps
    /// to [0, <see cref="MaxLeadMs" />] — a lead can never turn negative
    /// (rendering BEHIND the playhead) or run away on a compositor stall.</summary>
    /// <summary>
    ///     What to multiply a WALL-ms lead by before adding it to a SONG-ms
    ///     clock. The lead compensates presentation latency: a frame drawn now
    ///     reaches the glass <c>lead</c> wall-ms later, by which time the song has
    ///     advanced <c>lead × speed</c> song-ms. Unscaled, 2× drew half a lead
    ///     behind the audio and 0.5× half a lead ahead — a constant A/V offset,
    ///     not jitter, and the same wall-vs-song confusion
    ///     <c>TimelineViewMath.SpeedFactor</c> fixed for the lag budget one
    ///     expression earlier. Linear (a slower song wants a SMALLER lead), 1 for
    ///     anything that is not a positive finite speed.
    /// </summary>
    public static double LeadScale(double playbackSpeed) =>
        double.IsFinite(playbackSpeed) && playbackSpeed > 0 ? playbackSpeed : 1.0;

    public static double ComputeLeadMs(double measuredMs, double overrideMs, double trimMs)
    {
        var baseLead = overrideMs >= 0 ? overrideMs : Math.Max(0, measuredMs);
        return Math.Clamp(baseLead + trimMs, 0, MaxLeadMs);
    }
}
