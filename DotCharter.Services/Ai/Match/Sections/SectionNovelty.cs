namespace DotCharter.Services.Ai.Match.Sections;

/// <summary>One sample of the novelty curve: when, and how far the timbre moved there.</summary>
/// <param name="Time">The boundary the sample describes — the join between the two
/// windows that were compared.</param>
/// <param name="Value">The distance between them, in <c>IToneSimilarity</c> units.</param>
public readonly record struct NoveltySample(TimeSpan Time, double Value);

/// <summary>
///     The novelty curve and the peaks picked off it — <b>pure arithmetic over a
///     curve, with no audio, no extractor and no I/O anywhere in it</b>.
///     <para>
///         It is its own class for one reason: every interesting failure of a
///         segmenter is a failure of the PEAK PICKING, and a peak picker that can
///         only be exercised through a stem separation, a decode and 240 FFTs is a
///         peak picker nobody tests. Here a curve is a <c>double[]</c>, a case is
///         three lines, and the thresholds can be pinned against hand-written shapes
///         (a plateau, one spike, a ramp, noise) that no synthetic audio would
///         reliably produce.
///     </para>
///     <para>
///         <b>The threshold is adaptive AND floored, and it needs both halves.</b>
///         Adaptive alone (a fraction of the way from the median to the maximum)
///         finds "the biggest wiggle" in a curve that is pure noise, so a song that
///         never changes tone still gets boundaries. A fixed floor alone cannot cope
///         with the range between a clean/fuzz change and a clean/crunch one. The
///         floor says "this is a real timbre change at all" in the metric's own
///         units; the adaptive part then says "and it is one of the big ones in THIS
///         song".
///     </para>
/// </summary>
public static class SectionNovelty
{
    /// <summary>
    ///     How far up from the median, towards the curve's maximum, a sample must sit
    ///     before it is a candidate. Deliberately below half: a song with one enormous
    ///     change and three ordinary ones must still find the three.
    /// </summary>
    public const double DefaultPeakFactor = 0.45;

    /// <summary>
    ///     The absolute floor, in <c>ToneSimilarity</c> units. Calibrated off the
    ///     scale that metric documents: two performances through ONE rig land around
    ///     0.1–0.3, and a genuinely different rig is above that — so a "boundary" at
    ///     0.2 is the player changing what they played, not the engineer changing the
    ///     amp.
    /// </summary>
    public const double DefaultMinNovelty = 0.35;

    /// <summary>
    ///     The distance at which a change counts as a whole 1.0 of confidence — the
    ///     documented scale of "a clean amp against a fuzz on the same riff".
    ///     <para>
    ///         Confidence is normalised against THIS constant rather than against the
    ///         song's own maximum, on purpose: a song with one violent change would
    ///         otherwise push every other real change towards zero, and the number the
    ///         UI shows would mean something different in every song.
    ///     </para>
    /// </summary>
    public const double ConfidenceScale = 1.0;

    /// <summary>A peak: where the timbre changed, and by how much.</summary>
    /// <param name="Time">The boundary.</param>
    /// <param name="Height">The novelty there.</param>
    /// <param name="Confidence">
    /// <paramref name="Height" /> over <see cref="ConfidenceScale" />, clamped to 0..1.</param>
    public readonly record struct NoveltyPeak(TimeSpan Time, double Height, double Confidence);

    /// <summary>
    ///     The peaks of a novelty curve: local maxima above the adaptive threshold,
    ///     taken strongest-first and never closer together than
    ///     <paramref name="minGap" />, returned in TIME order.
    ///     <para>
    ///         <b>Strongest-first is what makes the minimum gap mean something.</b>
    ///         Walking the curve left to right and dropping anything too close to the
    ///         last accepted peak keeps the FIRST of a pair, which for a boundary
    ///         smeared over two samples is the weaker one — and then the real change
    ///         is the one that gets dropped.
    ///     </para>
    ///     <para>Deterministic: ties break on the earlier time.</para>
    /// </summary>
    /// <param name="curve">The novelty curve, in time order.</param>
    /// <param name="minGap">The shortest section that may be produced.</param>
    /// <param name="peakFactor">See <see cref="DefaultPeakFactor" />.</param>
    /// <param name="minNovelty">See <see cref="DefaultMinNovelty" />.</param>
    public static IReadOnlyList<NoveltyPeak> PickPeaks(
        IReadOnlyList<NoveltySample> curve,
        TimeSpan minGap,
        double peakFactor = DefaultPeakFactor,
        double minNovelty = DefaultMinNovelty)
    {
        ArgumentNullException.ThrowIfNull(curve);
        if (curve.Count < 3) return [];

        var threshold = Threshold(curve, peakFactor, minNovelty);

        var candidates = new List<NoveltySample>();
        for (var i = 1; i < curve.Count - 1; i++)
        {
            var v = curve[i].Value;
            if (v < threshold) continue;
            // Strictly greater on the left, greater-or-equal on the right: a flat top
            // yields its LEFT-most sample and never two adjacent peaks.
            if (v > curve[i - 1].Value && v >= curve[i + 1].Value) candidates.Add(curve[i]);
        }

        candidates.Sort(static (a, b) =>
        {
            var byHeight = b.Value.CompareTo(a.Value);
            return byHeight != 0 ? byHeight : a.Time.CompareTo(b.Time);
        });

        var kept = new List<NoveltyPeak>();
        foreach (var candidate in candidates)
        {
            var clash = false;
            foreach (var already in kept)
                if ((already.Time - candidate.Time).Duration() < minGap) { clash = true; break; }
            if (clash) continue;

            kept.Add(new NoveltyPeak(
                candidate.Time,
                candidate.Value,
                Math.Clamp(candidate.Value / ConfidenceScale, 0.0, 1.0)));
        }

        kept.Sort(static (a, b) => a.Time.CompareTo(b.Time));
        return kept;
    }

    /// <summary>
    ///     The threshold a sample must clear: the larger of the absolute floor and
    ///     "<paramref name="peakFactor" /> of the way from the curve's median to its
    ///     maximum". Exposed so a report can print the number it actually used — a
    ///     segmentation nobody can explain is a segmentation nobody trusts.
    /// </summary>
    public static double Threshold(
        IReadOnlyList<NoveltySample> curve,
        double peakFactor = DefaultPeakFactor,
        double minNovelty = DefaultMinNovelty)
    {
        ArgumentNullException.ThrowIfNull(curve);
        if (curve.Count == 0) return minNovelty;

        var values = curve.Select(static s => s.Value).ToArray();
        Array.Sort(values);
        var median = values.Length % 2 == 1
            ? values[values.Length / 2]
            : 0.5 * (values[values.Length / 2 - 1] + values[values.Length / 2]);
        var max = values[^1];

        return Math.Max(minNovelty, median + peakFactor * Math.Max(0, max - median));
    }
}
