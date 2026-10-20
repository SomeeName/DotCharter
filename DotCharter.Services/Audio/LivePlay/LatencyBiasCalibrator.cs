namespace DotCharter.Services.Audio.LivePlay;

/// <summary>
///     Identifies the audio setup a latency offset was learned for. A measured offset
///     is only valid for the device + buffer it was measured on: switch from ASIO at
///     128 frames to WASAPI at 512 and the true round-trip changes by tens of ms.
/// </summary>
public static class AudioDeviceKey
{
    /// <summary>
    ///     Stable, human-readable key. Uses the ACTUAL engine rate/buffer rather than
    ///     the configured ones, because the device is allowed to refuse what was asked
    ///     for (see <c>AutoFollowDeviceAudioSettings</c>) — keying on the request would
    ///     file two different setups under one key.
    ///     <para>
    ///         The drop tuner is part of the setup too: engaging it inserts a pitch
    ///         shifter in front of the capture ring, which moves the true input latency.
    ///         Without it in the key, a drop-tuner run learns an offset that is then
    ///         applied to ordinary runs — the pedal poisons the offset for everything
    ///         else. Folding it in also means the calibrator LEARNS the shifter's
    ///         latency by itself, rather than us having to model it.
    ///     </para>
    ///     <para>
    ///         The segment is only appended when the pedal is actually engaged, so an
    ///         ordinary run produces exactly the key it always did and every offset
    ///         users have already learned keeps matching.
    ///     </para>
    /// </summary>
    public static string From(
        string? driver,
        string? inputDevice,
        string? outputDevice,
        int sampleRate,
        int bufferFrames,
        int dropTuneSemitones = 0,
        int dropTuneMode = 0)
    {
        static string Clean(string? s) =>
            string.IsNullOrWhiteSpace(s) ? "?" : s.Trim().Replace('|', '/');

        var key = $"{Clean(driver)}|{Clean(inputDevice)}|{Clean(outputDevice)}|{sampleRate}|{bufferFrames}";
        if (dropTuneSemitones == 0) return key;
        return $"{key}|drop:{dropTuneSemitones}:{dropTuneMode}";
    }
}

/// <summary>What <see cref="LatencyBiasCalibrator" /> concluded about a finished run.</summary>
/// <param name="Adjusted">False when the run was not trustworthy enough to learn from.</param>
/// <param name="NewOffsetMs">The offset to store. Equals the current one when not adjusted.</param>
/// <param name="Reason">Why — for the log and the settings readout.</param>
public sealed record LatencyBiasDecision(bool Adjusted, double NewOffsetMs, string Reason);

/// <summary>
///     Refines the Live Play judging offset from how the player actually played, so
///     the one-shot loopback measurement stops drifting as the setup changes.
/// </summary>
/// <remarks>
///     Judged event times already have the offset subtracted, so a run's MEDIAN signed
///     error is exactly the residual the offset failed to remove: median late (+) means
///     the true latency is larger than we compensate for, so the offset should go up.
///     <para>
///         <b>The hazard this class exists to contain.</b> A player who genuinely drags
///         produces the same positive median as an under-compensated device. Fold that in
///         naively and the game quietly moves the goalposts to match a bad habit — the
///         player's timing error disappears from the scoring instead of being fixed. So:
///     </para>
///     <list type="bullet">
///         <item>
///             <b>Consistency gate.</b> A wide spread (<see cref="MaxSpreadMs" />) means
///             sloppy playing, not a systematic offset. Latency is a CONSTANT — it shows
///             up as a tight distribution sitting off-centre. Only a tight run teaches.
///         </item>
///         <item>
///             <b>Partial step.</b> One run is not proof; we move <see cref="Alpha" /> of
///             the way, so a genuine device change converges over a few songs while one
///             bad night barely moves it.
///         </item>
///         <item>
///             <b>Leash to the measurement.</b> The learned value can never drift more
///             than <see cref="MaxDriftFromBaselineMs" /> from the loopback-measured
///             baseline. Bad habits cannot walk the offset away indefinitely; a real
///             device change is well inside that band.
///         </item>
///     </list>
/// </remarks>
public static class LatencyBiasCalibrator
{
    /// <summary>Too few hits and the median is noise.</summary>
    public const int MinHits = 25;

    /// <summary>Above this spread the run says "inconsistent", not "biased".</summary>
    public const double MaxSpreadMs = 45.0;

    /// <summary>Ignore residuals this small — not worth rewriting config for.</summary>
    public const double MinShiftMs = 3.0;

    /// <summary>Fraction of the observed residual to take per run.</summary>
    public const double Alpha = 0.4;

    /// <summary>How far a learned offset may sit from the measured baseline.</summary>
    public const double MaxDriftFromBaselineMs = 40.0;

    public const double MinOffsetMs = 0.0;
    public const double MaxOffsetMs = 400.0;

    /// <summary>
    ///     Decides the new offset for this device after a run.
    /// </summary>
    /// <param name="currentOffsetMs">The offset the run was judged with.</param>
    /// <param name="baselineOffsetMs">The loopback-measured offset — the anchor the
    /// learned value is leashed to. Pass the current offset when nothing was measured.</param>
    /// <param name="timing">The run's timing report. Null/empty ⇒ no decision.</param>
    public static LatencyBiasDecision Evaluate(
        double currentOffsetMs, double baselineOffsetMs, LiveTimingReport? timing)
    {
        if (timing is null || timing.HitCount == 0)
            return new LatencyBiasDecision(false, currentOffsetMs, "no timed hits");

        if (timing.HitCount < MinHits)
        {
            return new LatencyBiasDecision(false, currentOffsetMs,
                $"too few timed hits ({timing.HitCount} < {MinHits})");
        }

        if (timing.StdDevMs > MaxSpreadMs)
        {
            return new LatencyBiasDecision(false, currentOffsetMs,
                $"timing too inconsistent to learn from (±{timing.StdDevMs:0} ms)");
        }

        var residual = timing.MedianMs;
        if (Math.Abs(residual) < MinShiftMs)
        {
            return new LatencyBiasDecision(false, currentOffsetMs,
                $"already centred ({residual:+0.#;-0.#;0} ms)");
        }

        // Late (+) => we are under-compensating => the offset must grow.
        var target = currentOffsetMs + (residual * Alpha);

        // Leash to the measurement, then to the absolute sane range.
        var low = Math.Max(MinOffsetMs, baselineOffsetMs - MaxDriftFromBaselineMs);
        var high = Math.Min(MaxOffsetMs, baselineOffsetMs + MaxDriftFromBaselineMs);
        if (high < low) (low, high) = (high, low); // defensive: a wild baseline can invert them

        var clamped = Math.Clamp(target, low, high);
        var rounded = Math.Round(clamped, 1);

        if (Math.Abs(rounded - currentOffsetMs) < 0.05)
            return new LatencyBiasDecision(false, currentOffsetMs, "at the limit of the measured baseline");

        return new LatencyBiasDecision(true, rounded,
            $"{residual:+0.#;-0.#;0} ms residual over {timing.HitCount} hits → offset {currentOffsetMs:0.#} → {rounded:0.#} ms");
    }
}
