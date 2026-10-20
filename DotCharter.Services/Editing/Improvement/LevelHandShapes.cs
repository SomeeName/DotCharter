namespace DotCharter.Services.Editing.Improvement;

/// <summary>
/// Which of the hardest level's hand shapes a generated lower level keeps, from what
///     official ladders keep (<c>LocalChordLadderMeasurementTest</c>, 688 arrangements):
///     <list type="bullet">
///         <item>
///             <b>A bar over chords the level still plays stays</b>, on the source span. Over a
///             full chord it keeps its template (98–100 % officially); over nothing but REDUCED
///             grips of one template it points at that reduced template (99.5–100 % officially —
///             the official bar describes the grip actually played, not the full shape).
///         </item>
///         <item>
///             <b>A bar over single notes stays when it still describes a grip</b>: two or more
///             distinct (string, fret) notes left in its span. That is the let-ring / arpeggio
///             passage, and official ladders keep its bar at 61–96 % (two notes) and 70–100 %
///             (three or more) on every depth, against 0.3–38 % below the top fifth when one
///             note is left. In the top fifth one passage note keeps the bar (67 % officially) —
///             unless it is a chord's bass note: a chord that came down to its bass note loses
///             its bar at every depth (96–100 % officially).
///         </item>
///     </list>
///     The level must already be sorted by position.
/// </summary>
internal static class LevelHandShapes
{
    private const int DistinctNotesForAGrip = 2;

    /// <summary>From this depth one passage note left under a bar keeps it.</summary>
    private const double OneNoteKeepsTheBarFromDepth = 0.8;

    public static void Carry(ChartLevel source, ChartLevel level, double depth, IReadOnlySet<Chord> reducedChords,
        IReadOnlySet<GuitarNote> bassNotes)
    {
        foreach (var shape in source.HandShapes)
        {
            var chords = InSpan(level.Chords, shape, static c => c.Position);
            if (chords.Count > 0)
            {
                var clone = shape.Clone();
                if (chords.All(reducedChords.Contains))
                {
                    var templateIds = chords.Select(c => c.TemplateId).Distinct().ToList();
                    if (templateIds.Count == 1) clone.TemplateId = templateIds[0];
                }

                level.HandShapes.Add(clone);
                continue;
            }

            var notes = InSpan(level.Notes, shape, static n => n.Position);
            var grip = notes.Select(n => (n.StringIndex, n.Fret)).Distinct().Count();
            var keep = grip >= DistinctNotesForAGrip
                       || (grip == 1 && depth >= OneNoteKeepsTheBarFromDepth && !notes.All(bassNotes.Contains));
            if (keep) level.HandShapes.Add(shape.Clone());
        }
    }

    /// <summary>The items of a position-sorted list inside <paramref name="shape" />'s span.</summary>
    private static List<T> InSpan<T>(IList<T> sorted, HandShape shape, Func<T, FractionalPosition> position)
    {
        var lo = 0;
        var hi = sorted.Count;
        while (lo < hi)
        {
            var mid = (lo + hi) / 2;
            if (position(sorted[mid]) < shape.Position) lo = mid + 1;
            else hi = mid;
        }

        var result = new List<T>();
        for (var i = lo; i < sorted.Count && position(sorted[i]) < shape.EndPosition; i++)
            result.Add(sorted[i]);
        return result;
    }
}
