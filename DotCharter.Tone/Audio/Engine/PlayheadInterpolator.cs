using System.Diagnostics;

namespace DotCharter.Tone.Audio.Engine;

/// <summary>
///     The smoothed, extrapolating playhead behind <c>JuceAudioPlaybackService</c>'s
///     250 Hz position tick and its per-frame <c>CurrentTimeMs</c> read. Pure:
///     the clock is passed in as <see cref="Stopwatch.GetTimestamp" /> ticks.
///     <para>
///         The native audible position advances only when JUCE pumps a device
///         buffer (256 frames @ 48 kHz ≈ 5.3 ms of wall time, i.e. 10.7 song-ms at
///         2×), so a consumer that drew it raw would see a staircase. Every tick
///         extrapolates the previous report by <c>elapsed × speed</c>, then
///         low-passes toward the native reading (<see cref="LowPassGain" />) so
///         the residual quantization error is corrected over ~20 ticks instead
///         of as a step; a drift beyond <see cref="SnapThresholdMs" /> is a
///         seek/loop/scrub and snaps.
///     </para>
///     <para>
///         The state is ONE immutable sample published with a volatile write.
///         The two halves (<c>_lastInterpMs</c>,
///         <c>_lastSampleTicks</c>) are not kept in separate fields: a render-thread
///         extrapolation landing between the tick's two writes would pair the NEW
///         report with the OLD timestamp — one tick period of speed-scaled
///         overshoot, which the monotonic clamp would then hold for the next frames.
///         Ticks serialize under a lock (a 4 ms pool timer can overlap itself;
///         the lock is never taken on an audio thread), and an overlapped tick
///         whose timestamp predates the published sample extrapolates by zero
///         rather than backwards.
///     </para>
///     <para>
///         Every <see cref="Reset" /> moves <see cref="Epoch" />, and the sample
/// carries the epoch it was made in. A tick that read the native
///         position BEFORE a seek and reaches <see cref="TryTick" /> after it is
///         refused instead of publishing the pre-seek position as the new burst's
///         start, and <see cref="PlayheadFloor" /> uses the epoch to tell a
///         render read that straddled a seek from one that did not.
///     </para>
/// </summary>
public sealed class PlayheadInterpolator
{
    /// <summary>Drift beyond this is a seek, not quantization: hard-snap.</summary>
    public const double SnapThresholdMs = 50.0;

    /// <summary>Per-tick fraction of the residual drift that is corrected.</summary>
    public const double LowPassGain = 0.05;

    private sealed record Sample(double Ms, long Ticks, long Epoch);

    private readonly object _tickGate = new();
    private readonly double _msPerTick;
    private Sample? _last;
    private long _epoch;
    private int _snapCount;

    public PlayheadInterpolator() : this(Stopwatch.Frequency) { }

    /// <param name="ticksPerSecond">The resolution of the <c>nowTicks</c> values
    /// passed to <see cref="Tick" /> and <see cref="TryExtrapolate(long, double, out double)" />.</param>
    public PlayheadInterpolator(long ticksPerSecond)
    {
        if (ticksPerSecond <= 0) throw new ArgumentOutOfRangeException(nameof(ticksPerSecond));
        _msPerTick = 1000.0 / ticksPerSecond;
    }

    /// <summary>True between the first tick of a play burst and <see cref="Reset" />.</summary>
    public bool IsRunning => Volatile.Read(ref _last) != null;

    /// <summary>Snaps (drift beyond <see cref="SnapThresholdMs" />) since construction.
    /// Diagnostic; a steady-state play burst must not move it.</summary>
    public int SnapCount => Volatile.Read(ref _snapCount);

    /// <summary>Moves on every <see cref="Reset" />. Read it BEFORE reading the native
    /// position a tick will feed, and hand it to <see cref="TryTick" />.</summary>
    public long Epoch => Interlocked.Read(ref _epoch);

    /// <summary>Forget the burst — the next tick hard-syncs to native (pause, stop, seek) —
    /// and move <see cref="Epoch" />, so a tick already in flight cannot publish.</summary>
    public void Reset()
    {
        lock (_tickGate)
        {
            Interlocked.Increment(ref _epoch);
            Volatile.Write(ref _last, null);
        }
    }

    /// <summary>
    ///     One position tick. Returns the song time to report for
    ///     <paramref name="nowTicks" />: the native reading on the first tick of
    ///     a burst, otherwise the extrapolated-then-corrected value, never behind
    ///     the previous report except across a snap.
    /// </summary>
    public double Tick(double nativeMs, long nowTicks, double speed)
    {
        lock (_tickGate) return TickLocked(nativeMs, nowTicks, speed);
    }

    /// <summary>
    ///     <see cref="Tick" />, refused (false, nothing published) when a <see cref="Reset" />
    ///     has happened since <paramref name="epoch" /> was read: the native reading
    ///     the caller took may predate the seek that reset the burst.
    /// </summary>
    public bool TryTick(double nativeMs, long nowTicks, double speed, long epoch, out double reportMs)
    {
        lock (_tickGate)
        {
            if (epoch != Interlocked.Read(ref _epoch))
            {
                reportMs = 0;
                return false;
            }

            reportMs = TickLocked(nativeMs, nowTicks, speed);
            return true;
        }
    }

    private double TickLocked(double nativeMs, long nowTicks, double speed)
    {
        if (speed <= 0) speed = 1.0;
        var last = _last;
        double reportMs;
        if (last == null)
        {
            reportMs = nativeMs;
        }
        else
        {
            // An overlapped tick can hold a timestamp older than the sample
            // the previous tick just published; that is zero elapsed, not
            // a step backwards.
            var elapsedMs = (nowTicks - last.Ticks) * _msPerTick;
            if (elapsedMs < 0) { elapsedMs = 0; nowTicks = last.Ticks; }
            var extrap = last.Ms + elapsedMs * speed;
            var drift = nativeMs - extrap;
            if (Math.Abs(drift) > SnapThresholdMs)
            {
                reportMs = nativeMs;
                Interlocked.Increment(ref _snapCount);
            }
            else
            {
                reportMs = extrap + LowPassGain * drift;
                if (reportMs < last.Ms) reportMs = last.Ms;
            }
        }

        Volatile.Write(ref _last, new Sample(reportMs, nowTicks, Interlocked.Read(ref _epoch)));
        return reportMs;
    }

    /// <summary>
    ///     The song time at <paramref name="nowTicks" /> between ticks: the last
    ///     report advanced by <c>elapsed × speed</c>, from ONE consistent sample.
    ///     False while not running.
    /// </summary>
    public bool TryExtrapolate(long nowTicks, double speed, out double ms) =>
        TryExtrapolate(nowTicks, speed, out ms, out _);

    /// <summary><see cref="TryExtrapolate(long, double, out double)" />, plus the
    /// <see cref="Epoch" /> the sample it extrapolated from was made in.</summary>
    public bool TryExtrapolate(long nowTicks, double speed, out double ms, out long epoch)
    {
        var last = Volatile.Read(ref _last);
        if (last == null)
        {
            ms = 0;
            epoch = Epoch;
            return false;
        }

        if (speed <= 0) speed = 1.0;
        var elapsedMs = (nowTicks - last.Ticks) * _msPerTick;
        if (elapsedMs < 0) elapsedMs = 0;
        ms = last.Ms + elapsedMs * speed;
        epoch = last.Epoch;
        return true;
    }
}
