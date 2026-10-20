namespace DotCharter.Tone.Audio.Tuner;

/// <summary>
///     One smoothed display frame (see <see cref="TunerDisplaySmoother"/>).
///     <see cref="WithinBand"/> — the smoothed value currently sits inside the
///     in-tune band (entered at ±<see cref="TunerDisplaySmoother.InTuneCents"/>,
///     left at ±<see cref="TunerDisplaySmoother.ReleaseCents"/> — a Schmitt
///     trigger, so a needle parked on the edge does not strobe);
///     <see cref="InTune"/> — it has stayed there continuously for the
///     stability hold ("in tune" = within ±X cents for Y ms).
/// </summary>
public readonly record struct SmoothedTunerReading(
    bool HasValue,
    double Cents,
    bool WithinBand,
    bool InTune);

/// <summary>
///     Display smoother between a raw cents source (pitch engine or
///     <see cref="VirtualStrobe"/>) and a tuner needle/lamp: median-of-N
///     (one octave/harmonic glitch can never twitch the needle) followed by
///     exponential smoothing (a calm needle), plus stability-hold detection.
///     <para>
///     Pure and clock-free — time is INJECTED as <c>nowMs</c> on every call
///     (tests drive a synthetic clock). Silence must be reported via
///     <see cref="ProcessSilence"/>: it clears the history (the player may
///     have turned the peg meanwhile) and restarts the stability hold.
///     Allocation-free per call after construction.
///     </para>
///     <para>
///     Managed slice of the 0.18 tuner suite; reference implementation for
///     the Phase-0 native graduation (see <see cref="IPitchDetector"/>).
///     </para>
/// </summary>
public sealed class TunerDisplaySmoother
{
    private readonly double[] _ring;
    private readonly double[] _sorted;
    private int _count;
    private int _next;
    private double _smoothed;
    private bool _hasSmoothed;
    private double _bandEnteredMs = double.NaN;

    /// <summary>EMA weight of each new median value (1 = no smoothing).</summary>
    public double SmoothingAlpha { get; init; } = 0.35;

    /// <summary>±X cents band that counts as in tune (the ENTRY threshold).</summary>
    public double InTuneCents { get; init; } = 5.0;

    /// <summary>
    ///     ±X cents at which a needle already inside the band LEAVES it — the
    ///     release half of the Schmitt trigger. Defaults to 1.5 ×
    ///     <see cref="InTuneCents"/>.
    ///     <para>
    ///     Without it the band has ONE threshold, and a string parked on it
    ///     cannot ever read in tune: every reading that steps a fraction of a
    ///     cent outside clears <c>_bandEnteredMs</c>, so the stability hold
    ///     restarts from zero and <see cref="SmoothedTunerReading.InTune"/> never
    ///     latches — the lamp strobes at exactly the tuning the player is trying
    ///     to hold. (The median→EMA chain damps most jitter, so this only bites
    ///     within a few tenths of a cent of the edge — which is precisely where
    ///     someone tuning ends up.) Every physical tuner has this hysteresis.
    ///     </para>
    /// </summary>
    public double ReleaseCents
    {
        get => _releaseCents ?? InTuneCents * 1.5;
        init => _releaseCents = value;
    }

    private readonly double? _releaseCents;

    /// <summary>Y ms the smoothed value must stay inside the band
    /// continuously before <see cref="SmoothedTunerReading.InTune"/>.</summary>
    public double StabilityHoldMs { get; init; } = 750.0;

    /// <summary>Median window length (readings).</summary>
    public int MedianWindow { get; }

    public TunerDisplaySmoother(int medianWindow = 5)
    {
        if (medianWindow is < 1 or > 63)
            throw new ArgumentOutOfRangeException(nameof(medianWindow));

        MedianWindow = medianWindow;
        _ring = new double[medianWindow];
        _sorted = new double[medianWindow];
    }

    /// <summary>Clears all history (target change, dialog reopen).</summary>
    public void Reset()
    {
        _count = 0;
        _next = 0;
        _hasSmoothed = false;
        _bandEnteredMs = double.NaN;
    }

    /// <summary>Feeds one raw cents reading at injected time
    /// <paramref name="nowMs"/> (monotonic, any origin).</summary>
    public SmoothedTunerReading Process(double cents, double nowMs)
    {
        _ring[_next] = cents;
        _next = (_next + 1) % MedianWindow;
        if (_count < MedianWindow) _count++;

        Array.Copy(_ring, _sorted, _count);
        Array.Sort(_sorted, 0, _count);
        var median = _count % 2 == 1
            ? _sorted[_count / 2]
            : 0.5 * (_sorted[_count / 2 - 1] + _sorted[_count / 2]);

        _smoothed = _hasSmoothed
            ? SmoothingAlpha * median + (1.0 - SmoothingAlpha) * _smoothed
            : median;
        _hasSmoothed = true;

        // Schmitt trigger: enter the band at InTuneCents, leave it at ReleaseCents.
        var alreadyWithin = !double.IsNaN(_bandEnteredMs);
        var withinBand = Math.Abs(_smoothed) <= (alreadyWithin ? ReleaseCents : InTuneCents);
        if (withinBand)
        {
            if (double.IsNaN(_bandEnteredMs)) _bandEnteredMs = nowMs;
        }
        else
        {
            _bandEnteredMs = double.NaN;
        }

        var inTune = withinBand && nowMs - _bandEnteredMs >= StabilityHoldMs;
        return new SmoothedTunerReading(true, _smoothed, withinBand, inTune);
    }

    /// <summary>Feeds one silent tick: history and the stability hold reset —
    /// stale readings must not smooth into the next pluck.</summary>
    public SmoothedTunerReading ProcessSilence(double nowMs)
    {
        Reset();
        return new SmoothedTunerReading(false, 0, false, false);
    }
}
