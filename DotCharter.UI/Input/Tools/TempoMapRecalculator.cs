namespace DotCharter.UI.Input.Tools;

/// <summary>
/// Pure tempo-map recomputation used while the user is dragging a beat
/// marker. Extracted from <see cref="EventInteractionTool"/> so the math
/// can be unit-tested without driving Avalonia pointer events.
/// </summary>
/// <remarks>
/// Anchor model:
/// <list type="bullet">
///   <item>Beat 0 is always treated as an implicit anchor.</item>
///   <item>Any beat with <see cref="BeatMarker.IsAnchor"/> true is preserved
///         in time and re-priced (BPM is recomputed from the spacing to the
///         next anchor).</item>
///   <item>Beats between two anchors are evenly spaced; their BPM is the
///         derived tempo of the enclosing segment.</item>
///   <item>Beats after the last anchor are extruded forward at the
///         pre-drag BPM of each preceding beat — the trailing tempo map is
///         therefore preserved verbatim.</item>
/// </list>
/// Drag of beat 0 is special-cased: the entire timeline shifts by the
/// time delta with no anchor or tempo changes.
/// </remarks>
public static class TempoMapRecalculator
{
    /// <summary>Lower bound on segment duration to avoid divide-by-zero / huge BPMs.</summary>
    public const double MinSegmentDurationMs = 10.0;

    /// <summary>Fallback tempo when the source map has a non-positive BPM.</summary>
    public const double FallbackBpm = 120.0;

    /// <summary>
    /// Recompute the tempo map after a beat marker has been dragged to
    /// <paramref name="newTime"/>. Mutates <paramref name="beats"/> in place.
    /// <paramref name="originalBeats"/> must be a snapshot taken at drag-start
    /// so anchor flags / pre-drag tempos can be restored before re-pricing.
    /// </summary>
    public static void Recalculate(
        IList<BeatMarker> beats,
        IReadOnlyList<BeatMarker> originalBeats,
        int dragIndex,
        double newTime)
    {
        if (beats.Count == 0) return;
        if (originalBeats.Count != beats.Count)
        {
            // ReSharper disable once LocalizableElement -- developer-facing exception message
            throw new ArgumentException(
                $"originalBeats length ({originalBeats.Count}) must match beats length ({beats.Count}).",
                nameof(originalBeats));
        }

        if (dragIndex < 0 || dragIndex >= beats.Count)
            throw new ArgumentOutOfRangeException(nameof(dragIndex));

        // Restore the pre-drag state so re-pricing always starts from the
        // same baseline, regardless of how many times we've been called
        // during the drag.
        for (var i = 0; i < beats.Count; i++)
        {
            beats[i].IsAnchor = originalBeats[i].IsAnchor;
            beats[i].Time = originalBeats[i].Time;
            beats[i].Bpm = originalBeats[i].Bpm;
        }

        // Beat 0 drag = whole-song shift. No anchor changes; everyone moves
        // by the same delta so tempo and structure are preserved.
        if (dragIndex == 0)
        {
            var delta = newTime - originalBeats[0].Time;
            for (var i = 0; i < beats.Count; i++) beats[i].Time += delta;
            return;
        }

        beats[dragIndex].Time = newTime;
        beats[dragIndex].IsAnchor = true;

        var anchorIndices = new List<int>();
        for (var i = 0; i < beats.Count; i++)
        {
            if (beats[i].IsAnchor)
                anchorIndices.Add(i);
        }

        // Beat 0 is always an implicit anchor for the segment math.
        if (!anchorIndices.Contains(0)) anchorIndices.Insert(0, 0);

        for (var a = 0; a < anchorIndices.Count - 1; a++)
        {
            var startIdx = anchorIndices[a];
            var endIdx = anchorIndices[a + 1];

            var timeSpan = beats[endIdx].Time - beats[startIdx].Time;
            if (timeSpan < MinSegmentDurationMs) timeSpan = MinSegmentDurationMs;

            var segmentUnits = 0.0;
            for (var i = startIdx; i < endIdx; i++)
                segmentUnits += GetQuarterNoteUnits(beats[i]);
            if (segmentUnits <= 0.0) segmentUnits = 1.0;

            var bpm = segmentUnits * 60000.0 / timeSpan;

            beats[startIdx].Bpm = bpm;

            for (var i = startIdx + 1; i < endIdx; i++)
            {
                beats[i].Time = beats[i - 1].Time + GetIntervalMs(beats[i - 1], bpm);
                beats[i].Bpm = bpm;
            }
        }

        // Beats past the last anchor keep their original tempos and are
        // simply slid forward to maintain spacing relative to the new
        // last-anchor time.
        var lastAnchor = anchorIndices.Last();
        beats[lastAnchor].Bpm = originalBeats[lastAnchor].Bpm;
        for (var i = lastAnchor + 1; i < beats.Count; i++)
        {
            var bpm = originalBeats[i - 1].Bpm;
            if (bpm <= 0) bpm = FallbackBpm;
            beats[i].Time = beats[i - 1].Time + GetIntervalMs(originalBeats[i - 1], bpm);
            beats[i].Bpm = originalBeats[i].Bpm;
        }
    }

    private static double GetQuarterNoteUnits(BeatMarker beat)
    {
        var denominator = beat.NoteDenominator > 0 ? beat.NoteDenominator : 4;
        return 4.0 / denominator;
    }

    private static double GetIntervalMs(BeatMarker beat, double bpm) => 60000.0 / bpm * GetQuarterNoteUnits(beat);

    /// <summary>
    /// Convenience: take a defensive copy of the beats so callers can keep
    /// a mutation-safe baseline.
    /// </summary>
    public static List<BeatMarker> Snapshot(IReadOnlyList<BeatMarker> beats)
    {
        var copy = new List<BeatMarker>(beats.Count);
        foreach (var b in beats)
        {
            copy.Add(new BeatMarker
            {
                BeatsInMeasure = b.BeatsInMeasure,
                Bpm = b.Bpm,
                IsAnchor = b.IsAnchor,
                IsMeasure = b.IsMeasure,
                NoteDenominator = b.NoteDenominator,
                Time = b.Time
            });
        }

        return copy;
    }
}
