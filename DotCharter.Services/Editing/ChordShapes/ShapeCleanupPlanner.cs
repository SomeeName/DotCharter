using DotCharter.Services.State;

namespace DotCharter.Services.Editing.ChordShapes;

/// <summary>A planned Clean up: the ONE undoable action (null when nothing is left to do) and what it does.</summary>
public sealed record ShapeCleanupPlan(IChartAction? Action, int ShapesRemoved, int ChordsMoved, int HandShapesMoved);

/// <summary>
///     Builds the one undo step that applies the proposals a charter ticked or decided. Every copy's chords,
///     hand shapes and notation-voice chords are repointed by ONE <see cref="SwitchTemplateAction" /> and every
///     emptied or unused shape is taken out by ONE <see cref="DeleteChordShapesAction" />: a chord and the hand
///     shape over it move together (they share a template), and the ids the removal renumbers are read after the
///     repoint, never before. Planning merges one by one would leave later repoints holding stale ids.
/// </summary>
public static class ShapeCleanupPlanner
{
    public const string CleanUpLabelKey = "History_ChordShapes_CleanUp";

    public static ShapeCleanupPlan Plan(
        Arrangement arrangement,
        IEnumerable<ShapeCleanupMerge> merges,
        IEnumerable<ChordTemplate> unused,
        IEnumerable<(ShapeCleanupNearDuplicate Pair, ShapeCleanupChoice Choice)> decisions,
        IChartEditorSurface editor)
    {
        ArgumentNullException.ThrowIfNull(arrangement);
        ArgumentNullException.ThrowIfNull(editor);

        var redirect = Redirects(merges, decisions);
        var chords = new List<Chord>();
        var chordsAfter = new List<Chord>();
        var shapes = new List<HandShape>();
        var shapesAfter = new List<HandShape>();
        var remove = new List<ChordTemplate>();
        var movedChords = 0;
        foreach (var (loser, winner) in redirect.Select(r => (r.Key, Root(redirect, r.Key))))
        {
            if (!InArrangement(arrangement, loser) || !InArrangement(arrangement, winner)) continue;
            var (loserChords, loserShapes) = ChordShapeEdits.UsesOf(arrangement, loser);
            var onLevels = loserChords.Count;
            loserChords.AddRange(ChordShapeEdits.VoiceChordsOf(arrangement, loser));
            // A shape nothing resolves to is not ours to delete through a merge.
            if (loserChords.Count == 0 && loserShapes.Count == 0) continue;
            movedChords += onLevels;
            chords.AddRange(loserChords);
            chordsAfter.AddRange(loserChords.Select(c => ChordShapeEdits.Repointed(c, winner.Id)));
            shapes.AddRange(loserShapes);
            shapesAfter.AddRange(loserShapes.Select(h => ChordShapeEdits.Repointed(h, winner.Id)));
            remove.Add(loser);
        }

        remove.AddRange((unused ?? []).Where(t => InArrangement(arrangement, t)));
        var actions = new List<IChartAction>();
        if (chords.Count + shapes.Count > 0)
            actions.Add(new SwitchTemplateAction(chords, chordsAfter, shapes, shapesAfter, editor));
        var distinct = remove.Distinct(ReferenceEqualityComparer.Instance).Cast<ChordTemplate>().ToList();
        if (distinct.Count > 0) actions.Add(new DeleteChordShapesAction(arrangement, distinct, editor));
        return actions.Count == 0
            ? new ShapeCleanupPlan(null, 0, 0, 0)
            : new ShapeCleanupPlan(new CompositeAction(actions) { LabelKey = CleanUpLabelKey }, distinct.Count, movedChords, shapes.Count);
    }

    /// <summary>Which shape each removed copy ends up on. A chain (a copy's keeper merged away) resolves to its last stop.</summary>
    private static Dictionary<ChordTemplate, ChordTemplate> Redirects(
        IEnumerable<ShapeCleanupMerge> merges,
        IEnumerable<(ShapeCleanupNearDuplicate Pair, ShapeCleanupChoice Choice)> decisions)
    {
        var redirect = new Dictionary<ChordTemplate, ChordTemplate>(ReferenceEqualityComparer.Instance);
        foreach (var merge in merges ?? [])
        {
            foreach (var copy in merge.Copies) redirect[copy] = merge.Keeper;
        }

        foreach (var (pair, choice) in decisions ?? [])
        {
            if (choice == ShapeCleanupChoice.KeepBoth) continue;
            var (winner, loser) = choice == ShapeCleanupChoice.UseFirst ? (pair.First, pair.Second) : (pair.Second, pair.First);
            var winnerRoot = Root(redirect, winner);
            var loserRoot = Root(redirect, loser);
            if (!ReferenceEquals(winnerRoot, loserRoot)) redirect[loserRoot] = winnerRoot;
        }

        return redirect;
    }

    private static ChordTemplate Root(Dictionary<ChordTemplate, ChordTemplate> redirect, ChordTemplate template)
    {
        while (redirect.TryGetValue(template, out var next)) template = next;
        return template;
    }

    private static bool InArrangement(Arrangement arrangement, ChordTemplate template) =>
        ChordTemplateCollectionOps.ContainsInstance(arrangement.ChordTemplates, template);
}
