using Chord = DotCharter.Core.Domain.Guitar.Chord;

namespace DotCharter.Services.Editing.HandShapes;

/// <summary>
///     "Which chords of this level does no hand shape cover?" — the question D11
///     (<c>ChordOutsideHandShapeRule</c>) asks after every history step, answered with
///     EXACTLY <see cref="HandShapeCoverageIndex" />'s predicate, so the rule and the
///     planner that fixes it can never disagree about a chord (a fix that leaves its
///     own finding behind is not a fixed point).
///     <para>
///         A chord with no chord notes describes no sound and is never reported (auto-fix
///         removes it; the planner skips it). Nor is a chord split into notes
///         (<see cref="Chord.SplitIntoNotes" />): it ships as single notes with no chord
/// frame, so it needs no shape, and the planner never draws one over it.
///     </para>
///     <para>
///         Validation re-runs constantly, so the common case — both collections in
///         position order, which every editing action keeps — is one merge sweep that
///         allocates nothing until it finds a chord. Anything else falls back to the
///         index.
///     </para>
/// </summary>
public static class ChordHandShapeCoverage
{
    /// <summary>Appends the uncovered chords of <paramref name="level" /> to
    /// <paramref name="into" /> (allocated on the first one) and returns it.</summary>
    public static List<Chord>? Uncovered(ChartLevel level, List<Chord>? into)
    {
        ArgumentNullException.ThrowIfNull(level);
        var chords = level.Chords;
        var shapes = level.HandShapes;
        if (chords.Count == 0) return into;

        if (!IsSorted(chords, static c => c.Position) || !IsSorted(shapes, static h => h.Position))
        {
            var index = new HandShapeCoverageIndex(shapes);
            for (var i = 0; i < chords.Count; i++)
            {
                var chord = chords[i];
                if (NeedsShape(chord) && !index.Covers(chord.Position))
                    (into ??= []).Add(chord);
            }

            return into;
        }

        // Shapes starting at or before the chord, with the furthest end among them
        // and the last start — which equals the chord's position exactly when some
        // shape starts on it (the zero-width case the index also counts).
        var next = 0;
        FractionalPosition? maxEnd = null;
        FractionalPosition? lastStart = null;
        for (var i = 0; i < chords.Count; i++)
        {
            var chord = chords[i];
            var position = chord.Position;
            while (next < shapes.Count && shapes[next].Position.CompareTo(position) <= 0)
            {
                var shape = shapes[next++];
                lastStart = shape.Position;
                if (maxEnd is not { } end || shape.EndPosition.CompareTo(end) > 0) maxEnd = shape.EndPosition;
            }

            if (!NeedsShape(chord)) continue;
            var covered = (lastStart is { } s && s.CompareTo(position) == 0) ||
                          (maxEnd is { } e && e.CompareTo(position) > 0);
            if (!covered) (into ??= []).Add(chord);
        }

        return into;
    }

    private static bool NeedsShape(Chord chord) => chord.ChordNotes.Count > 0 && !chord.SplitIntoNotes;

    private static bool IsSorted<T>(IList<T> items, Func<T, FractionalPosition> position)
    {
        for (var i = 1; i < items.Count; i++)
        {
            if (position(items[i - 1]).CompareTo(position(items[i])) > 0)
                return false;
        }

        return true;
    }
}
