namespace DotCharter.Services.Audio.LivePlay;

/// <summary>Timing bias of one chart section: how early/late the player's
/// verified hits landed inside it (signed ms, negative = early).</summary>
public sealed record LiveTimingSectionBias(
    string Name, double StartMs, double EndMs, int HitCount, double MeanMs, double MedianMs);

/// <summary>
///     Compact post-run hit-timing report: the signed early/late distribution
///     of a run's VERIFIED hits (misses carry no honest detection time and
///     are excluded). Consumed by the 0.16 results-screen mini version and
///     the 0.17 LAN-wide analytics dashboard. Negative values = early,
///     positive = late; all values in milliseconds.
/// </summary>
public sealed record LiveTimingReport(
    int HitCount,
    double MeanMs,
    double MedianMs,
    double StdDevMs,
    double MinMs,
    double MaxMs,
    double P10Ms,
    double P25Ms,
    double P75Ms,
    double P90Ms,
    int EarlyCount,
    int OnTimeCount,
    int LateCount,
    IReadOnlyList<int> Histogram,
    IReadOnlyList<LiveTimingSectionBias> Sections)
{
    /// <summary>Hits within ±this window count as "on time".</summary>
    public const double OnTimeWindowMs = 15.0;

    /// <summary>The histogram covers ±<see cref="HistogramRangeMs"/> in
    /// <see cref="HistogramBinWidthMs"/>-wide bins; deltas beyond the range
    /// clamp into the edge bins. Bin i spans
    /// [-range + i*width, -range + (i+1)*width).</summary>
    public const double HistogramRangeMs = 150.0;
    public const double HistogramBinWidthMs = 10.0;
    public static int HistogramBinCount => (int)(2 * HistogramRangeMs / HistogramBinWidthMs);

    public static readonly LiveTimingReport Empty = new(
        0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0,
        new int[HistogramBinCount], Array.Empty<LiveTimingSectionBias>());

    /// <summary>Overall verdict for HUD copy: negative mean = rushing,
    /// positive = dragging, near zero = in the pocket.</summary>
    public bool LeansEarly => HitCount > 0 && MeanMs < -OnTimeWindowMs;
    public bool LeansLate => HitCount > 0 && MeanMs > OnTimeWindowMs;
}

/// <summary>
///     Pure aggregation over a run's judged events → <see cref="LiveTimingReport"/>.
///     A SEPARATE consumer of <see cref="LivePlayEvent"/> data — it never
///     touches <see cref="LivePlayStats"/> / <see cref="LiveNoteMatcher"/>
///     scoring. Feed every judged event (e.g. from the session tick loop) via
///     <see cref="Add(in LivePlayEvent)"/>; misses AND untimed
///     (<see cref="LiveTimingTier.None"/>: CQT-confirmed, no attack onset) hits
///     are ignored, because for both the DetectedAtMs is the playhead rather
///     than a real detection. A TIMED CQT hit carries its attack onset and counts. No clocks, no I/O — fully deterministic and
///     unit-testable.
/// </summary>
public sealed class LiveTimingCollector
{
    private readonly List<(double SoundTimeMs, double DeltaMs)> _hits = [];

    /// <summary>Verified hits collected so far.</summary>
    public int Count => _hits.Count;

    /// <summary>
    ///     Records one judged event. Hits (including wrong-technique hits, which
    ///     are real detections) contribute their signed delta
    ///     (DetectedAtMs − chart TimeMs).
    ///     <para>
    ///         <b>Two kinds of event are skipped, and the second one used not to
    ///         be.</b> A MISS's <c>DetectedAtMs</c> is the window expiry, not a
    ///         detection. And an UNTIMED hit — <see cref="LiveTimingTier.None" />,
    ///         i.e. one the CQT chart-verifier confirmed that no reported attack
    ///         onset could time — is stamped with the PLAYHEAD at that moment,
    ///         because the CQT confirms WHAT was played and never WHEN. Its delta is therefore a
    ///         constant ≈ <c>+HitWindowLateMs</c> (≥ 230 ms in CQT mode) that has
    ///         nothing to do with the player.
    ///     </para>
    ///     <para>
    ///         Filtering on the VERDICT alone let every one of those in, and the
    ///         damage was not confined to a readout: a whole CQT run's deltas are
    ///         near-identical, so the report's <c>StdDevMs</c> came out SMALL and
    ///         sailed through <see cref="LatencyBiasCalibrator" />'s
    ///         consistency gate — which exists precisely to reject non-latency
    ///         signals — carrying a <c>MedianMs</c> of ≈ +230 ms. The auto-timing
    ///         calibrator then walked the device's learned offset to
    ///         <c>baseline + MaxDriftFromBaselineMs</c> and PERSISTED it, so a
    ///         corrupt reading from one CQT run degraded judging in every later
    ///         run on that device. The calibrator's own refusal message already
    ///         said "no timed hits"; this is what makes that word true.
    ///     </para>
    /// </summary>
    public void Add(in LivePlayEvent ev)
    {
        if (ev.Verdict == LivePlayVerdict.Miss) return;
        if (ev.Tier == LiveTimingTier.None) return;
        _hits.Add((ev.Sound.TimeMs, ev.DetectedAtMs - ev.Sound.TimeMs));
    }

    /// <summary>Test/tool seam: records a raw (soundTimeMs, signed delta) pair.</summary>
    public void Add(double soundTimeMs, double deltaMs) => _hits.Add((soundTimeMs, deltaMs));

    public void Reset() => _hits.Clear();

    /// <summary>
    ///     Builds the compact report. <paramref name="sections"/> (typically
    ///     <see cref="LivePlaySessionReport.Sections"/>) adds the per-section
    ///     bias breakdown; hits outside every section are counted in the
    ///     overall numbers only.
    /// </summary>
    public LiveTimingReport BuildReport(IReadOnlyList<SectionResult>? sections = null)
    {
        if (_hits.Count == 0) return LiveTimingReport.Empty;

        var deltas = new double[_hits.Count];
        for (var i = 0; i < _hits.Count; i++) deltas[i] = _hits[i].DeltaMs;

        var sum = 0.0;
        var min = double.MaxValue;
        var max = double.MinValue;
        var early = 0;
        var late = 0;
        var histogram = new int[LiveTimingReport.HistogramBinCount];
        foreach (var delta in deltas)
        {
            sum += delta;
            if (delta < min) min = delta;
            if (delta > max) max = delta;
            if (delta < -LiveTimingReport.OnTimeWindowMs) early++;
            else if (delta > LiveTimingReport.OnTimeWindowMs) late++;
            histogram[HistogramBin(delta)]++;
        }

        var mean = sum / deltas.Length;
        var varianceSum = 0.0;
        foreach (var delta in deltas)
        {
            var diff = delta - mean;
            varianceSum += diff * diff;
        }

        var sorted = (double[])deltas.Clone();
        Array.Sort(sorted);

        return new LiveTimingReport(
            HitCount: deltas.Length,
            MeanMs: mean,
            MedianMs: Percentile(sorted, 0.50),
            StdDevMs: Math.Sqrt(varianceSum / deltas.Length),
            MinMs: min,
            MaxMs: max,
            P10Ms: Percentile(sorted, 0.10),
            P25Ms: Percentile(sorted, 0.25),
            P75Ms: Percentile(sorted, 0.75),
            P90Ms: Percentile(sorted, 0.90),
            EarlyCount: early,
            OnTimeCount: deltas.Length - early - late,
            LateCount: late,
            Histogram: histogram,
            Sections: BuildSectionBiases(sections));
    }

    private IReadOnlyList<LiveTimingSectionBias> BuildSectionBiases(IReadOnlyList<SectionResult>? sections)
    {
        if (sections == null || sections.Count == 0) return Array.Empty<LiveTimingSectionBias>();

        var result = new List<LiveTimingSectionBias>(sections.Count);
        var scratch = new List<double>();
        foreach (var section in sections)
        {
            scratch.Clear();
            var sum = 0.0;
            foreach (var (soundTimeMs, deltaMs) in _hits)
            {
                if (soundTimeMs < section.StartMs || soundTimeMs >= section.EndMs) continue;
                scratch.Add(deltaMs);
                sum += deltaMs;
            }

            if (scratch.Count == 0) continue; // unplayed section — no bias to report

            scratch.Sort();
            result.Add(new LiveTimingSectionBias(
                section.Name, section.StartMs, section.EndMs, scratch.Count,
                MeanMs: sum / scratch.Count,
                MedianMs: Percentile(scratch, 0.50)));
        }

        return result;
    }

    private static int HistogramBin(double deltaMs)
    {
        var clamped = Math.Clamp(deltaMs, -LiveTimingReport.HistogramRangeMs,
            LiveTimingReport.HistogramRangeMs);
        var bin = (int)Math.Floor((clamped + LiveTimingReport.HistogramRangeMs)
                                  / LiveTimingReport.HistogramBinWidthMs);
        return Math.Clamp(bin, 0, LiveTimingReport.HistogramBinCount - 1);
    }

    /// <summary>Linear-interpolated percentile over an ASCENDING list.</summary>
    private static double Percentile(IReadOnlyList<double> sorted, double p)
    {
        if (sorted.Count == 1) return sorted[0];
        var rank = p * (sorted.Count - 1);
        var lower = (int)Math.Floor(rank);
        var upper = (int)Math.Ceiling(rank);
        if (lower == upper) return sorted[lower];
        var fraction = rank - lower;
        return sorted[lower] + (sorted[upper] - sorted[lower]) * fraction;
    }
}
