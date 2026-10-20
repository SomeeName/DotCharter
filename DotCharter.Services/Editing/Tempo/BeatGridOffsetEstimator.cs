namespace DotCharter.Services.Editing.Tempo;

/// <summary>The grid shift <see cref="BeatGridOffsetEstimator" /> suggests, and what it buys.</summary>
/// <param name="OffsetMs">Move every beat by this much (positive = later) to put the notes on the grid.</param>
/// <param name="MedianOffNowMs">Median distance of the note onsets to the nearest 1/4- or 1/3-beat line today.</param>
/// <param name="MedianOffAfterMs">The same median once the grid has moved by <paramref name="OffsetMs" />.</param>
/// <param name="OnsetCount">How many distinct onsets the medians were taken over.</param>
public sealed record BeatGridOffsetEstimate(double OffsetMs, double MedianOffNowMs, double MedianOffAfterMs, int OnsetCount);

/// <summary>
/// Measures how far a chart's beat grid sits from its own notes, the way
///     the probe that found the 121 ms CDLC did: the shift that minimises the MEDIAN
///     distance of the note onsets to the nearest 1/4- or 1/3-beat subdivision.
///     <para>
///         Shifting by a whole subdivision keeps 1/4-beat notes on a 1/4-beat grid,
///         so on straight sixteenths several shifts in range are equally good. Of the
///         shifts within <see cref="TieToleranceMs" /> of the best median, the one that
///         puts the most onsets ON a beat wins (music lands on the beat more than on
///         its sixteenths — the probe's own evidence was the bar lines), then the
///         smaller median, then the smaller move. Triplets break the tie on their own:
///         a quarter-beat shift moves them off the 1/3 grid.
///     </para>
///     Pure and deterministic: a coarse search over ±<c>rangeMs</c> at
///     <see cref="CoarseStepMs" />, refined to <see cref="FineStepMs" />; at most
///     <see cref="MaxOnsets" /> onsets, sampled at an even stride.
/// </summary>
public static class BeatGridOffsetEstimator
{
    public const double DefaultRangeMs = 250.0;
    public const double CoarseStepMs = 1.0;
    public const double FineStepMs = 0.1;
    public const int MaxOnsets = 1024;

    /// <summary>Fewer onsets than this say nothing about the grid.</summary>
    public const int MinOnsets = 8;

    /// <summary>Shifts whose median is this close to the best one are treated as equally aligned.</summary>
    public const double TieToleranceMs = 1.0;

    /// <summary>An onset within this fraction of a beat of a beat line counts as ON the beat for the tie-break.</summary>
    private const double OnBeatToleranceBeats = 1.0 / 32.0;

    /// <summary>Distinct onsets closer than this are one sound (a level duplicate, a chord across arrangements).</summary>
    private const double SameOnsetMs = 0.5;

    /// <summary>Null when the chart has no grid or too few notes to measure.</summary>
    public static BeatGridOffsetEstimate? Estimate(ChartProject project, double rangeMs = DefaultRangeMs)
    {
        ArgumentNullException.ThrowIfNull(project);
        var beats = project.Beats.ToList();
        return Estimate(Onsets(project, beats), beats, rangeMs);
    }

    public static BeatGridOffsetEstimate? Estimate(IReadOnlyList<double> onsets, IList<BeatMarker> beats,
        double rangeMs = DefaultRangeMs)
    {
        ArgumentNullException.ThrowIfNull(onsets);
        ArgumentNullException.ThrowIfNull(beats);
        if (beats.Count < 2 || onsets.Count < MinOnsets || !(rangeMs >= 0)) return null;

        var scratch = new double[onsets.Count];
        var steps = (int)Math.Round(rangeMs / CoarseStepMs);
        var candidates = new List<(double Offset, double Median, int OnBeat)>(2 * steps + 1);
        var best = double.PositiveInfinity;
        for (var i = -steps; i <= steps; i++)
        {
            var offset = i * CoarseStepMs;
            var (median, onBeat) = Measure(onsets, beats, offset, scratch);
            candidates.Add((offset, median, onBeat));
            best = Math.Min(best, median);
        }

        var chosen = candidates
            .Where(c => c.Median <= best + TieToleranceMs)
            .OrderByDescending(c => c.OnBeat)
            .ThenBy(c => c.Median)
            .ThenBy(c => Math.Abs(c.Offset))
            .First();

        // Refine around the chosen coarse step, never past the searched range.
        var refined = chosen;
        var fineSteps = (int)Math.Round(CoarseStepMs / FineStepMs);
        for (var i = -fineSteps; i <= fineSteps; i++)
        {
            var offset = Math.Round(chosen.Offset + i * FineStepMs, 3);
            if (Math.Abs(offset) > rangeMs) continue;
            var (median, onBeat) = Measure(onsets, beats, offset, scratch);
            if (median < refined.Median - 1e-9 ||
                (Math.Abs(median - refined.Median) <= 1e-9 && Math.Abs(offset) < Math.Abs(refined.Offset)))
            {
                refined = (offset, median, onBeat);
            }
        }

        var now = Measure(onsets, beats, 0, scratch).Median;
        return new BeatGridOffsetEstimate(refined.Offset, now, refined.Median, onsets.Count);
    }

    /// <summary>
    ///     The median distance, in ms, of <paramref name="onsets" /> to the nearest 1/4-
    ///     or 1/3-beat line once every beat has moved by <paramref name="offsetMs" />.
    /// </summary>
    public static double MedianDistanceMs(IReadOnlyList<double> onsets, IList<BeatMarker> beats, double offsetMs)
    {
        ArgumentNullException.ThrowIfNull(onsets);
        ArgumentNullException.ThrowIfNull(beats);
        if (beats.Count < 2 || onsets.Count == 0) return double.NaN;
        return Measure(onsets, beats, offsetMs, new double[onsets.Count]).Median;
    }

    /// <summary>
    ///     Every sound's onset in ms — notes and chords of every level of every
    ///     arrangement, de-duplicated and sorted, then sampled down to
    ///     <see cref="MaxOnsets" /> at an even stride. Notation-only voices are left
    ///     out: they are never played.
    /// </summary>
    public static IReadOnlyList<double> Onsets(ChartProject project, IList<BeatMarker> beats)
    {
        ArgumentNullException.ThrowIfNull(project);
        ArgumentNullException.ThrowIfNull(beats);
        var all = new List<double>();
        if (beats.Count == 0) return all;
        foreach (var arrangement in project.Arrangements)
        {
            foreach (var level in arrangement.Levels)
            {
                foreach (var note in level.Notes) all.Add(note.Position.GetPositionMs(beats));
                foreach (var chord in level.Chords) all.Add(chord.Position.GetPositionMs(beats));
            }
        }

        all.Sort();
        var distinct = new List<double>(all.Count);
        foreach (var ms in all)
        {
            if (!double.IsFinite(ms)) continue;
            if (distinct.Count == 0 || ms - distinct[^1] >= SameOnsetMs) distinct.Add(ms);
        }

        if (distinct.Count <= MaxOnsets) return distinct;
        var sampled = new List<double>(MaxOnsets);
        for (var i = 0; i < MaxOnsets; i++) sampled.Add(distinct[(int)((long)i * distinct.Count / MaxOnsets)]);
        return sampled;
    }

    private static (double Median, int OnBeat) Measure(IReadOnlyList<double> onsets, IList<BeatMarker> beats,
        double offsetMs, double[] scratch)
    {
        var onBeat = 0;
        for (var i = 0; i < onsets.Count; i++)
        {
            // Moving the grid later by Δ is the onset moving earlier by Δ on the old grid.
            TimelineMath.TryBeatOffsetFromMs(onsets[i] - offsetMs, beats, out var index, out var offsetBeats);
            var beatLength = BeatLengthAt(beats, index);
            var within = offsetBeats - Math.Floor(offsetBeats);
            var toBeat = Math.Min(within, 1 - within);
            if (toBeat <= OnBeatToleranceBeats) onBeat++;
            scratch[i] = Math.Min(ToGrid(within, 4), ToGrid(within, 3)) * beatLength;
        }

        Array.Sort(scratch, 0, onsets.Count);
        var mid = onsets.Count / 2;
        var median = onsets.Count % 2 == 1 ? scratch[mid] : (scratch[mid - 1] + scratch[mid]) / 2;
        return (median, onBeat);
    }

    /// <summary>Distance, in beats, from a within-beat fraction to the nearest 1/<paramref name="division" /> line.</summary>
    private static double ToGrid(double within, int division)
    {
        var scaled = within * division;
        return Math.Abs(scaled - Math.Round(scaled)) / division;
    }

    /// <summary>The interval <see cref="TimelineMath.TryBeatOffsetFromMs" /> measured against.</summary>
    private static double BeatLengthAt(IList<BeatMarker> beats, int index)
    {
        var length = index + 1 < beats.Count
            ? beats[index + 1].Time - beats[index].Time
            : beats[^1].Time - beats[^2].Time;
        return length > 0 ? length : 500.0;
    }
}
