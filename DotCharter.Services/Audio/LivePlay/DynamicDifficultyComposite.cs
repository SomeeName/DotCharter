namespace DotCharter.Services.Audio.LivePlay;

/// <summary>
/// The level the 3D highway draws during a dynamic-difficulty run: for each phrase
///     iteration, the sounds of the level being JUDGED there, chosen by the matcher's own rule
///     (<see cref="LiveExpectedTimeline.LevelIndexFor" />) over the same [start, end) spans. Without it
///     the highway drew the editor's level (normally the hardest) while a subset was judged.
///     <para>Built off the frame path, at run start and whenever a phrase changes level, and
///     published whole: the result is never mutated after it is returned.</para>
/// </summary>
public static class DynamicDifficultyComposite
{
    /// <summary>The composite, or null when there is nothing to compose (one level, or no
    /// phrase iterations — the matcher then judges the top level, which the editor already shows).</summary>
    public static ChartLevel? Build(Arrangement arrangement, IList<BeatMarker> beats, LiveDifficultySelector selectLevel)
    {
        ArgumentNullException.ThrowIfNull(arrangement);
        ArgumentNullException.ThrowIfNull(selectLevel);
        if (arrangement.Levels.Count < 2) return null;
        var spans = LiveExpectedTimeline.BuildPhraseSpans(arrangement, beats);
        if (spans.Count == 0) return null;

        var composite = new ChartLevel { DifficultyIndex = -1 };
        for (var s = 0; s < spans.Count; s++)
        {
            var span = spans[s];
            var level = arrangement.Levels[LiveExpectedTimeline.LevelIndexFor(span, selectLevel, arrangement)];
            // Before the first phrase the matcher judges nothing; draw that stretch at the first
            // phrase's level so a count-in lead-in is not empty.
            var start = s == 0 ? double.NegativeInfinity : span.StartMs;
            AddWithin(level.Notes, composite.Notes, n => n.Position, beats, start, span.EndMs);
            AddWithin(level.Chords, composite.Chords, c => c.Position, beats, start, span.EndMs);
            AddWithin(level.Anchors, composite.Anchors, a => a.Position, beats, start, span.EndMs);
            AddWithin(level.HandShapes, composite.HandShapes, h => h.Position, beats, start, span.EndMs);
        }

        return composite;
    }

    private static void AddWithin<T>(IList<T> source, IList<T> target, Func<T, FractionalPosition> position,
        IList<BeatMarker> beats, double startMs, double endMs)
    {
        foreach (var item in source)
        {
            var ms = position(item).GetPositionMs(beats);
            if (ms >= startMs && ms < endMs) target.Add(item);
        }
    }
}
