namespace DotCharter.Tone.Audio.Diagnostics;

/// <summary>
///     Pure detector for audio-callback stalls (buffer under-runs / "xruns") — the
///     usual cause of audible dropouts, clicks and stutter. It needs NO native ABI
///     change: it watches the engine's render clock — the {frames rendered, QPC
///     tick of that block} pair the device callback publishes — against a managed
///     QPC "now". The time since the callback last published a block is the
///     STALENESS. A healthy callback publishes every block period, so a staleness
///     of several block periods means the audio thread missed its deadline and the
///     device under-ran.
///     <para>
///     This is a HEURISTIC, not a sample-accurate xrun counter (that would be an
///     atomic counter inside the native callback plus one ABI getter). It reliably
///     catches stalls that overlap a poll — i.e. the sustained or repeated
///     dropouts a user actually hears and asks about — and cannot see a single
///     sub-poll micro-glitch. Kept pure so <see cref="Observe" /> is unit-tested
///     with injected clock values.
///     </para>
/// </summary>
public sealed class AudioDropoutDetector
{
    /// <summary>A stall is flagged once the callback has not published for this
    /// many block periods…</summary>
    public const double GapBlockPeriods = 3.0;

    /// <summary>…but never below this floor, so a tiny buffer (sub-millisecond
    /// block period) does not false-positive on ordinary OS scheduler jitter.</summary>
    public const double GapFloorMs = 12.0;

    private bool _inGap;

    /// <summary>Outcome of one poll. <see cref="GapStarted" />/<see cref="GapEnded" />
    /// are the edges the monitor logs; <see cref="StalenessMs" /> is how long since
    /// the audio callback last published a block.</summary>
    public readonly record struct Poll(
        bool GapStarted,
        bool GapEnded,
        bool InGap,
        double StalenessMs,
        double ThresholdMs,
        double BlockPeriodMs);

    /// <summary>
    ///     Feed one observation. <paramref name="hostTicks" /> is the QPC tick the
    ///     render clock published; <paramref name="nowTicks" /> is a managed QPC
    ///     reading (<c>Stopwatch.GetTimestamp()</c>) and
    ///     <paramref name="ticksPerSecond" /> its frequency
    ///     (<c>Stopwatch.Frequency</c>) — on Windows both are the same QPC, so the
    ///     difference is a real elapsed time. An invalid clock (device closed /
    ///     between devices) clears any active gap and reports nothing.
    /// </summary>
    public Poll Observe(bool clockValid, long hostTicks, long nowTicks, long ticksPerSecond,
        int sampleRate, int blockFrames)
    {
        if (!clockValid || ticksPerSecond <= 0 || sampleRate <= 0 || blockFrames <= 0)
        {
            var wasInGap = _inGap;
            _inGap = false;
            return new Poll(false, wasInGap, false, 0, 0, 0);
        }

        var blockPeriodMs = blockFrames * 1000.0 / sampleRate;
        var thresholdMs = Math.Max(GapFloorMs, blockPeriodMs * GapBlockPeriods);
        var stalenessMs = (nowTicks - hostTicks) * 1000.0 / ticksPerSecond;
        if (stalenessMs < 0) stalenessMs = 0; // guard a tiny clock skew

        var stalling = stalenessMs > thresholdMs;
        var started = stalling && !_inGap;
        var ended = !stalling && _inGap;
        _inGap = stalling;
        return new Poll(started, ended, _inGap, stalenessMs, thresholdMs, blockPeriodMs);
    }

    /// <summary>Forget any in-progress gap (call when the device is (re)opened).</summary>
    public void Reset() => _inGap = false;
}
