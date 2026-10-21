namespace DotCharter.Services.Editing.Linking;

/// <summary>
///     Normalises the edges of runs that are about to be linked, so every copy starts from the same
///     footing: a hand shape crossing a run edge is split there, and the fret-hand position in
///     effect at each run start is pinned with an anchor. Shared by creating a group and adding a
///     placement to one; the caller's action applies and undoes the edits.
/// </summary>
internal static class LinkRunEdges
{
    /// <summary>A hand shape split at a run edge, or an anchor pinned at a run start.</summary>
    internal sealed record Edit(ChartLevel Level, HandShape? Trimmed, FractionalPosition Before, FractionalPosition After,
        List<HandShape> Added, FretAnchor? Anchor);

    public static List<Edit> Plan(IEnumerable<LinkPlacement> placements)
    {
        var edits = new List<Edit>();
        var edgesByLevel = new Dictionary<ChartLevel, SortedSet<FractionalPosition>>();
        foreach (var placement in placements)
        {
            if (placement.Arrangement!.Levels.MaxBy(l => l.DifficultyIndex) is not { } level) continue;
            if (!edgesByLevel.TryGetValue(level, out var edges)) edgesByLevel[level] = edges = [];
            edges.Add(placement.Position);
            edges.Add(placement.EndPosition);

            if (level.Anchors.Any(a => a.Position == placement.Position)) continue;
            var inEffect = level.Anchors.Where(a => a.Position < placement.Position).MaxBy(a => a.Position);
            if (inEffect != null)
                edits.Add(new Edit(level, null, default, default, [], new FretAnchor(placement.Position, inEffect.Fret, inEffect.Width)));
        }

        foreach (var (level, edges) in edgesByLevel)
        {
            foreach (var shape in level.HandShapes)
            {
                var cuts = edges.Where(e => e > shape.Position && e < shape.EndPosition).ToList();
                if (cuts.Count == 0) continue;
                var pieces = new List<HandShape>(cuts.Count);
                for (var i = 0; i < cuts.Count; i++)
                {
                    var piece = shape.Clone();
                    piece.Position = cuts[i];
                    piece.EndPosition = i + 1 < cuts.Count ? cuts[i + 1] : shape.EndPosition;
                    pieces.Add(piece);
                }

                edits.Add(new Edit(level, shape, shape.EndPosition, cuts[0], pieces, null));
            }
        }

        return edits;
    }

    public static void Apply(List<Edit> edits)
    {
        foreach (var edit in edits)
        {
            if (edit.Trimmed is { } trimmed) trimmed.EndPosition = edit.After;
            foreach (var shape in edit.Added) InsertSorted(edit.Level.HandShapes, shape, s => s.Position);
            if (edit.Anchor != null) InsertSorted(edit.Level.Anchors, edit.Anchor, a => a.Position);
        }
    }

    public static void Undo(List<Edit> edits)
    {
        for (var i = edits.Count - 1; i >= 0; i--)
        {
            var edit = edits[i];
            if (edit.Anchor != null) edit.Level.Anchors.Remove(edit.Anchor);
            foreach (var shape in edit.Added) edit.Level.HandShapes.Remove(shape);
            if (edit.Trimmed is { } trimmed) trimmed.EndPosition = edit.Before;
        }
    }

    private static void InsertSorted<T>(IList<T> collection, T item, Func<T, FractionalPosition> position)
    {
        var at = position(item);
        var index = collection.Count;
        while (index > 0 && position(collection[index - 1]) > at) index--;
        collection.Insert(index, item);
    }
}
