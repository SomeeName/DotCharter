namespace DotCharter.UI.ViewModels.Editor.Inspector;

/// <summary>
///     Hand shapes that hold the same sounds as another: the same notes, on the same strings and frets, in the
///     same order. A shape's sounds are the notes and chord notes that start inside it; a shape over no sound matches
///     nothing.
/// </summary>
internal static class IdenticalHandShapes
{
    /// <summary>
    ///     The shapes of <paramref name="level" /> over the same sounds as <paramref name="source" /> that do not hold
    ///     its grip yet (template and ghost mask), leaving out <paramref name="exclude" />.
    /// </summary>
    public static IReadOnlyList<HandShape> Find(ChartLevel level, HandShape source, IEnumerable<HandShape> exclude)
    {
        var key = SoundsOf(level, source);
        if (key.Length == 0) return [];
        var skip = exclude.ToHashSet(ReferenceEqualityComparer.Instance);
        return [.. level.HandShapes.Where(shape => !skip.Contains(shape) &&
            (shape.TemplateId != source.TemplateId || shape.GhostMask != source.GhostMask) &&
            SoundsOf(level, shape).SequenceEqual(key))];
    }

    internal static (int StringIndex, int Fret)[] SoundsOf(ChartLevel level, HandShape shape)
    {
        var notes = level.Notes.Where(n => shape.Covers(n.Position))
            .Select(n => (n.Position, n.StringIndex, n.Fret));
        var chords = level.Chords.Where(c => shape.Covers(c.Position))
            .SelectMany(c => c.ChordNotes.Select(n => (c.Position, n.StringIndex, n.Fret)));
        return [.. notes.Concat(chords).OrderBy(s => s.Position).ThenBy(s => s.StringIndex).Select(s => (s.StringIndex, s.Fret))];
    }
}
