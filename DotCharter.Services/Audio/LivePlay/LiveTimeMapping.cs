namespace DotCharter.Services.Audio.LivePlay;

/// <summary>
///     Maps the WALL-CLOCK axis that input capture lives on onto the TRANSPORT axis
///     the chart lives on.
/// </summary>
/// <remarks>
///     These are two different clocks and they only tick together at full speed. The
///     transport advances at <c>speed × wall-clock</c> (the engine's own words: "at
///     PlaybackSpeed=2.0 the playhead jumps in twice-as-large ticks"), so at 60%
///     practice speed one real second is only 0.6 song-seconds.
///     <para>
///         Everything measured about the player's input is in wall-clock ms: how long
///         ago the attack happened (<c>ageMs</c> — the analyzer reports ~180–190 ms after
///         the fact since JuceSharp 0.9's finalize-lag fix; it was ~165 ms) and the calibrated round-trip latency. Subtracting those from a
///         transport-time playhead without scaling over-subtracts by
///         <c>(1 − speed)</c> of their sum: at 60% speed and a typical 180 ms analyzer
///         age plus a 50 ms offset, that is an 86 ms error — comparable to the entire
///         hit window. Practice speeds silently wrecked judging.
///     </para>
/// </remarks>
public static class LiveTimeMapping
{
    /// <summary>A sync older than this is stale (device stopped, thread starved) and
    /// must not fling events far into the past.</summary>
    public const double MaxEventAgeMs = 2000.0;

    /// <summary>
    ///     Transport time of an input event that occurred <paramref name="ageMs" />
    ///     wall-clock milliseconds before <paramref name="playheadMs" /> was read.
    /// </summary>
    /// <param name="playheadMs">Transport time read at the moment the event's age was
    /// measured against.</param>
    /// <param name="ageMs">Wall-clock milliseconds between the event occurring and the
    /// <paramref name="playheadMs" /> read.</param>
    /// <param name="latencyOffsetMs">Calibrated round trip (hear → play → captured),
    /// also wall-clock — so it scales too.</param>
    /// <param name="speed">Playback speed multiplier (e.g. 0.5 = half speed); scales the
    /// wall-clock offset back into transport time.</param>
    public static double ToTransportMs(double playheadMs, double ageMs, double latencyOffsetMs, double speed) =>
        playheadMs - ((ageMs + latencyOffsetMs) * NormalizeSpeed(speed));

    /// <summary>Transport time when the event's age is unknown — only the latency
    /// offset can be compensated, and it still has to be scaled.</summary>
    public static double ToTransportMs(double playheadMs, double latencyOffsetMs, double speed) =>
        playheadMs - (latencyOffsetMs * NormalizeSpeed(speed));

    /// <summary>
    ///     Capture-stream time, in seconds, of a CHART time — the inverse direction,
    ///     and the one the CQT actually needs.
    /// </summary>
    /// <remarks>
    ///     <para>
    ///         There is deliberately no <c>ToTransportSamples</c> here that scales a
    ///         capture-stream position onto the transport sample axis, so that the CQT
    ///         could be pushed "already mapped onto the transport axis". <b>That is not
    ///         implementable, and it would silently destroy note detection at every speed
    ///         other than 1.0.</b> Two independent reasons, both worth keeping written
    ///         down:
    ///     </para>
    ///     <para>
    ///         1. A CQT's position axis IS the sample axis of the audio pushed into it —
    ///         one position, one sample, by construction (<c>JuceConstantQ.Push</c>
    ///         advances its write cursor by <c>span.Length</c> and treats any other
    ///         position as a stream discontinuity, wiping every buffered frame). Handing
    ///         it N samples stamped with a position that advances by N×speed therefore
    ///         reset the ring on EVERY push, so it never accumulated the 4096 samples a
    ///         frame needs and stayed permanently empty. In CQT mode a pitched note has
    ///         no other evidence path, so 100% of them were judged Miss.
    ///     </para>
    ///     <para>
    ///         2. You cannot fix that by resampling the capture to transport rate either:
    ///         that would pitch-shift the player's guitar by 1/speed, and the CQT kernels
    ///         are defined in real Hz against the capture sample rate. <b>The player's
    ///         audio is inherently wall-clock — it is not a function of playback speed.</b>
    ///     </para>
    ///     <para>
    ///         So the CQT is indexed on the raw capture-stream axis, and the CHART time is
    ///         mapped into it here instead. <paramref name="anchorStreamPos" /> and
    ///         <paramref name="anchorTransportMs" /> pair a capture-stream sample with the
    ///         transport time the player was hearing when it was captured.
    ///     </para>
    /// </remarks>
    public static double ToCaptureSeconds(
        double transportMs,
        long anchorStreamPos,
        double anchorTransportMs,
        int sampleRate,
        double speed)
    {
        if (sampleRate <= 0) return 0.0;
        var captureSamples =
            anchorStreamPos +
            ((transportMs - anchorTransportMs) / 1000.0 * sampleRate / NormalizeSpeed(speed));
        return captureSamples / sampleRate;
    }

    /// <summary>Guards against a zero/negative/absurd speed collapsing the mapping.
    /// An unset or nonsense value behaves as real time rather than as a divide-by-zero
    /// or a time reversal.</summary>
    public static double NormalizeSpeed(double speed) => speed is > 0.05 and <= 4.0 ? speed : 1.0;
}
