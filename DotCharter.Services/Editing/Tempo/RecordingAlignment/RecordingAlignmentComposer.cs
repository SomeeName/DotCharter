namespace DotCharter.Services.Editing.Tempo.RecordingAlignment;

/// <summary>
/// Turns an alignment and the charter's per-section choice into the new beat grid. Pure.
///     <para>A beat is TRUSTED when it is fixed (locked, or outside the range) or sits in an accepted
///     section; it takes its aligned time. Every other beat — a skipped section — keeps its place
///     RELATIVE to its trusted neighbours: its move is interpolated linearly (by its old time)
///     between theirs, and past the last trusted beat it moves with it. A skipped section is
///     therefore stretched between aligned neighbours, never left at a time the neighbours have
///     moved past, so the grid stays strictly ascending.</para>
///     <para>Only TIMING changes: bars, time signatures and anchors are copied, and each changed
///     interval's tempo is re-derived the way the rest of the app writes it
///     (<c>60000 / interval × 4 / denominator</c>).</para>
/// </summary>
public static class RecordingAlignmentComposer
{
    /// <summary>The new grid, or null when the result would not be strictly ascending.</summary>
    public static List<BeatMarker>? Compose(IReadOnlyList<BeatMarker> current, RecordingAlignmentPlan plan,
        IReadOnlyList<double> aligned, IReadOnlySet<int> acceptedSections)
    {
        var n = current.Count;
        if (n != plan.Beats.Length || aligned.Count != n) return null;

        var trusted = new bool[n];
        for (var s = 0; s < plan.Sections.Count; s++)
        {
            var span = plan.Sections[s];
            for (var i = span.StartBeat; i < span.EndBeat; i++) trusted[i] = acceptedSections.Contains(s);
        }

        for (var i = 0; i < n; i++) trusted[i] |= plan.Beats[i].IsFixed;

        var times = new double[n];
        var lastTrusted = -1;
        for (var i = 0; i < n; i++)
        {
            if (!trusted[i]) continue;
            times[i] = plan.Beats[i].IsFixed ? plan.OldTimes[i] : aligned[i];
            FillBetween(plan.OldTimes, times, lastTrusted, i);
            lastTrusted = i;
        }

        FillBetween(plan.OldTimes, times, lastTrusted, n);

        var result = new List<BeatMarker>(n);
        for (var i = 0; i < n; i++)
        {
            if (!double.IsFinite(times[i]) || (i > 0 && times[i] <= times[i - 1])) return null;
            var beat = current[i].Clone();
            beat.Time = times[i];
            result.Add(beat);
        }

        for (var i = 0; i < n; i++)
        {
            var moved = times[i] != plan.OldTimes[i] || (i + 1 < n && times[i + 1] != plan.OldTimes[i + 1]);
            if (!moved) continue;
            if (i + 1 < n) result[i].Bpm = BpmFor(times[i + 1] - times[i], result[i].NoteDenominator);
            else if (i > 0) result[i].Bpm = BpmFor(times[i] - times[i - 1], result[i].NoteDenominator);
        }

        return result;
    }

    /// <summary>Fills the untrusted beats strictly between <paramref name="from" /> and
    ///     <paramref name="to" /> (either may be outside the grid: −1 / n).</summary>
    private static void FillBetween(double[] old, double[] times, int from, int to)
    {
        if (to - from <= 1) return;
        var n = old.Length;
        var hasFrom = from >= 0;
        var hasTo = to < n;
        for (var i = from + 1; i < to; i++)
        {
            var startShift = hasFrom ? times[from] - old[from] : 0;
            var endShift = hasTo ? times[to] - old[to] : startShift;
            if (!hasFrom) startShift = endShift;
            var f = hasFrom && hasTo ? (old[i] - old[from]) / (old[to] - old[from]) : 0;
            var shift = startShift + f * (endShift - startShift);
            times[i] = old[i] + shift;
        }
    }

    private static double BpmFor(double intervalMs, int noteDenominator) =>
        60000.0 / intervalMs * 4.0 / (noteDenominator > 0 ? noteDenominator : 4);
}
