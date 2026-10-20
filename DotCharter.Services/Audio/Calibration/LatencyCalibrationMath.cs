namespace DotCharter.Services.Audio.Calibration;

/// <summary>
///     Robust statistics over a set of tap ASYNCHRONIES (ms), where each value is
///     <c>tapTime − referenceBeatTime</c> on one shared monotonic clock (negative =
///     the player anticipated the beat, positive = lagged it).
///
///     <para>Outliers are rejected with the median / MAD (median absolute
///     deviation) rule rather than a mean / standard-deviation rule, because a
///     single fat-fingered double-tap or a missed beat is exactly the gross
///     outlier a mean-based filter fails to reject — the outlier inflates the very
///     σ that is meant to test it. MAD has a ~50% breakdown point, so one wild tap
///     in twelve cannot move the accepted set.</para>
/// </summary>
public readonly record struct TapAsyncStats(
    int Count,
    int UsedCount,
    double Mean,
    double Median,
    double StdDev,
    double ScaledMad,
    IReadOnlyList<double> Accepted)
{
    /// <summary>Below this many samples MAD rejection is skipped — too few points to
    /// tell an outlier from the signal.</summary>
    public const int MinForOutlierRejection = 4;

    public static readonly TapAsyncStats Empty =
        new(0, 0, 0, 0, 0, 0, Array.Empty<double>());

    /// <summary>Samples discarded as outliers.</summary>
    public int RejectedCount => Count - UsedCount;

    /// <summary>
    ///     Computes robust statistics and the accepted (inlier) subset.
    ///     <paramref name="madK"/> is the rejection threshold in scaled-MAD units
    ///     (3.0 ≈ 3σ for normal data). Rejection is applied only when there are
    ///     enough points (≥ <see cref="MinForOutlierRejection"/>) AND the spread is
    ///     non-degenerate, so a tight or tiny set keeps every sample; and it never
    ///     rejects everything.
    /// </summary>
    public static TapAsyncStats From(IReadOnlyList<double> asynchroniesMs, double madK = 3.0)
    {
        if (asynchroniesMs is null || asynchroniesMs.Count == 0) return Empty;

        var sorted = asynchroniesMs.OrderBy(static v => v).ToArray();
        var median = MedianOfSorted(sorted);

        // MAD = median(|x − median|), scaled (× 1.4826) to be a consistent σ
        // estimate for normally distributed data.
        var absDev = new double[sorted.Length];
        for (var i = 0; i < sorted.Length; i++) absDev[i] = Math.Abs(sorted[i] - median);
        Array.Sort(absDev);
        var scaledMad = 1.4826 * MedianOfSorted(absDev);

        List<double> accepted;
        if (sorted.Length < MinForOutlierRejection || scaledMad <= 1e-9)
        {
            accepted = [.. sorted];
        }
        else
        {
            accepted = new List<double>(sorted.Length);
            var limit = madK * scaledMad;
            foreach (var v in sorted)
                if (Math.Abs(v - median) <= limit) accepted.Add(v);
            if (accepted.Count == 0) accepted = [.. sorted]; // never reject all
        }

        var mean = accepted.Average();
        var acceptedMedian = MedianOfSorted(accepted); // accepted is already sorted (built from sorted)
        var std = StdDevOf(accepted, mean);
        return new TapAsyncStats(sorted.Length, accepted.Count, mean, acceptedMedian, std, scaledMad, accepted);
    }

    private static double MedianOfSorted(IReadOnlyList<double> sorted)
    {
        if (sorted.Count == 0) return 0;
        var mid = sorted.Count / 2;
        return sorted.Count % 2 == 1 ? sorted[mid] : (sorted[mid - 1] + sorted[mid]) / 2.0;
    }

    private static double StdDevOf(IReadOnlyList<double> values, double mean)
    {
        if (values.Count < 2) return 0;
        var sum = 0.0;
        foreach (var v in values)
        {
            var d = v - mean;
            sum += d * d;
        }

        return Math.Sqrt(sum / (values.Count - 1));
    }
}

/// <summary>Why an <see cref="AvOffsetResult"/> is not yet trustworthy.</summary>
public enum AvOffsetUntrusted
{
    None = 0,
    /// <summary>Fewer inlier taps than the minimum in one or both steps.</summary>
    NotEnoughTaps
}

/// <summary>Result of the two-tap A-V measurement. <see cref="AvOffsetMs"/> is
/// signed: positive = the picture is perceived LATER than the sound (picture
/// lags), negative = the picture leads.</summary>
public readonly record struct AvOffsetResult(
    bool Trusted,
    AvOffsetUntrusted Reason,
    double AvOffsetMs,
    TapAsyncStats Visual,
    TapAsyncStats Audio);

/// <summary>How a measured A-V offset maps onto the config knobs.
/// <para><see cref="VisualLeadMs"/> is the value to write to
/// <c>AppConfig.VisualLeadMs</c> (renderer-only, cannot bias judging).
/// <see cref="AudioLeadsUncompensated"/> is set when the picture LEADS the sound:
/// correcting that on the video side would need a negative lead the renderer does
/// not represent, and the only knob that could — <c>AudioDelayMs</c> — shifts the
/// transport clock the Live Play matcher grades against, so we deliberately do NOT
/// apply it automatically and instead report the residual in
/// <see cref="UncompensatedAudioLeadMs"/>.</para></summary>
public readonly record struct CalibrationApplication(
    double VisualLeadMs,
    bool AudioLeadsUncompensated,
    double UncompensatedAudioLeadMs);

/// <summary>
///     The pure, Avalonia-free math behind the latency / A-V calibration wizard:
///     the two-tap common-mode subtraction, minimum-tap gating and the mapping of
///     the result onto the renderer-only <c>VisualLeadMs</c> knob. All the
///     load-bearing decisions live here so they can be unit-tested without a UI.
/// </summary>
public static class LatencyCalibrationMath
{
    /// <summary>Minimum inlier taps required PER step before a mean is trusted.
    /// Rhythm-game calibrators use ~8–16; fewer and one nervous tap dominates.</summary>
    public const int DefaultMinTaps = 8;

    /// <summary>VisualLeadMs is clamped to this ceiling on apply. A whole 200 ms of
    /// display lead is already implausible; a larger number means the measurement
    /// went wrong (a miscounted beat), not that the monitor is that slow.</summary>
    public const double MaxVisualLeadMs = 200.0;

    /// <summary>
    ///     Two-tap method. Each mean carries the SAME player reaction/anticipation
    ///     bias — it is present in the see-and-tap step and the hear-and-tap step
    ///     identically — so the subtraction cancels it (common-mode rejection) and
    ///     leaves only the machine's audio-vs-video skew.
    ///     <para>Convention: the visual taps are referenced against the flash's
    ///     present time, and the audio taps against the click's ACTUAL AUDIBLE time
    ///     (the scheduled-click raw axis). Under that convention the audio mean is
    ///     the player's pure bias and the difference isolates the display latency —
    ///     which is exactly what <c>VisualLeadMs</c> compensates, given the
    ///     transport is already latency-compensated to what the player hears.</para>
    /// </summary>
    public static AvOffsetResult ComputeAvOffset(
        IReadOnlyList<double> visualTaps,
        IReadOnlyList<double> audioTaps,
        int minTaps = DefaultMinTaps,
        double madK = 3.0)
    {
        var visual = TapAsyncStats.From(visualTaps, madK);
        var audio = TapAsyncStats.From(audioTaps, madK);

        if (visual.UsedCount < minTaps || audio.UsedCount < minTaps)
            return new AvOffsetResult(false, AvOffsetUntrusted.NotEnoughTaps, 0, visual, audio);

        // visualMean − audioMean > 0 ⇒ taps to the picture lag taps to the sound
        // ⇒ the picture is perceived LATER than the sound.
        var avOffset = visual.Mean - audio.Mean;
        return new AvOffsetResult(true, AvOffsetUntrusted.None, avOffset, visual, audio);
    }

    /// <summary>Maps a measured A-V offset (ms; positive = picture lags sound) onto
    /// the config knobs. Always prefers the renderer-only <c>VisualLeadMs</c>; see
    /// <see cref="CalibrationApplication"/>.</summary>
    public static CalibrationApplication MapAvOffsetToConfig(double avOffsetMs, double maxLeadMs = MaxVisualLeadMs)
    {
        if (avOffsetMs >= 0)
            // Picture lags sound: pull the renderer forward by that much so the note
            // visual meets the (already latency-compensated) audio. VisualLeadMs is
            // applied in the renderers only, so it can never bias Live Play judging —
            // which is why the video side is driven and AudioDelayMs is not.
            return new CalibrationApplication(Clamp(avOffsetMs, 0, maxLeadMs), false, 0);

        // Picture LEADS sound. Video-side correction would need a NEGATIVE lead
        // (render later) that VisualLeadMs does not represent; the only knob that
        // could is AudioDelayMs, and that shifts the transport clock the judging
        // matcher reads. Refuse to do that automatically: clamp the video lead at 0
        // and surface the residual for the player to dial in by hand if they insist.
        return new CalibrationApplication(0, true, -avOffsetMs);
    }

    private static double Clamp(double v, double lo, double hi) => v < lo ? lo : v > hi ? hi : v;
}
