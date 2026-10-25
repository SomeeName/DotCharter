using DotCharter.Services.Editing.Improvement;
using DotCharter.Services.State;

namespace DotCharter.Services.Editing.ChordShapes;

/// <summary>
///     The Shapes panel's bulk verbs on the ticked shapes, each ONE undo node: merge them into the
///     one with the most uses, delete them (their uses move onto another shape with the same frets),
///     or edit them together so they all take one grip and become one shape. They reuse the single
///     shape's rules — the use move of <see cref="ChordShapeEdits" />, the in-place rewrite and the
///     projection of <see cref="ShapeEditPlanner" /> — and only decide which shape keeps the uses.
/// </summary>
public static class ShapeBulkEdits
{
    /// <summary>The ticked shapes of an arrangement, each once, in list order; the ones it does not hold are dropped.</summary>
    public static List<ChordTemplate> Normalize(Arrangement arrangement, IEnumerable<ChordTemplate> templates)
    {
        ArgumentNullException.ThrowIfNull(arrangement);
        ArgumentNullException.ThrowIfNull(templates);
        var wanted = new HashSet<ChordTemplate>(templates, ReferenceEqualityComparer.Instance);
        return [.. arrangement.ChordTemplates.Where(wanted.Contains)];
    }

    /// <summary>The edited shape first, then the other ticked shapes in list order.</summary>
    internal static List<ChordTemplate> GroupOf(Arrangement arrangement, ChordTemplate shape, IReadOnlyList<ChordTemplate>? together)
    {
        var group = Normalize(arrangement, together ?? []);
        group.RemoveAll(t => ReferenceEquals(t, shape));
        group.Insert(0, shape);
        return group;
    }

    /// <summary>The shape with the most uses (chords and hand shapes, every level); the earlier one on a tie.</summary>
    public static ChordTemplate? KeeperOf(Arrangement arrangement, IEnumerable<ChordTemplate> templates)
    {
        ArgumentNullException.ThrowIfNull(arrangement);
        var group = Normalize(arrangement, templates);
        return group.Count == 0 ? null : group.OrderByDescending(t => UseCount(arrangement, t)).First();
    }

    /// <summary>
    ///     Merges every ticked shape into the one with the most uses: their uses move onto it and the others are
    ///     removed, all in one undo node. The kept shape keeps its own fingers and name. Refused with fewer than two
    ///     shapes, or unless all have the same frets in the same arpeggio flavour (so no chord changes shape).
    /// </summary>
    public static ChordShapeEditPlan PlanMergeMany(Arrangement arrangement, IEnumerable<ChordTemplate> templates, IChartEditorSurface editor)
    {
        ArgumentNullException.ThrowIfNull(arrangement);
        ArgumentNullException.ThrowIfNull(editor);
        var group = Normalize(arrangement, templates);
        if (group.Count < 2) return ChordShapeEditPlan.Refused(ChordShapeEditRefusal.NeedsTwoShapes);

        var keeper = KeeperOf(arrangement, group)!;
        var sources = group.Where(t => !ReferenceEquals(t, keeper)).ToList();
        if (sources.Any(s => !s.HasFrets(keeper.Frets))) return ChordShapeEditPlan.Refused(ChordShapeEditRefusal.DifferentFrets);
        if (sources.Any(s => !ArpeggioFlavour.Of(s).Matches(keeper))) return ChordShapeEditPlan.Refused(ChordShapeEditRefusal.DifferentArpeggio);

        var move = ChordShapeEdits.MoveUses(arrangement, sources.Select(s => (s, keeper)), editor);
        var remove = new DeleteChordShapesAction(arrangement, sources, editor);
        var (chords, handShapes) = Moved(arrangement, sources);
        return new ChordShapeEditPlan(new CompositeAction([move, remove]) { LabelKey = ChordShapeEdits.MergeLabelKey },
            ChordShapeEditRefusal.None, chords, handShapes, TemplatesRemoved: sources.Count);
    }

    /// <summary>
    ///     Deletes every ticked shape in one undo node. A shape still in use hands its uses to the other shape with
    ///     the same frets that has the most uses (never another ticked shape); the notes stay. Refused, changing
    ///     nothing, while a used shape has no such shape.
    /// </summary>
    public static ChordShapeEditPlan PlanDeleteMany(Arrangement arrangement, IEnumerable<ChordTemplate> templates, IChartEditorSurface editor)
    {
        ArgumentNullException.ThrowIfNull(arrangement);
        ArgumentNullException.ThrowIfNull(editor);
        var group = Normalize(arrangement, templates);
        if (group.Count == 0) return ChordShapeEditPlan.Refused(ChordShapeEditRefusal.NotInArrangement);

        var used = LevelTemplateOrphans.UsedTemplates(arrangement);
        var pairs = new List<(ChordTemplate Source, ChordTemplate Target)>();
        foreach (var source in group.Where(used.Contains))
        {
            var target = arrangement.ChordTemplates
                .Where(t => !group.Contains(t) && ChordShapeCatalog.IsDuplicateOf(t, source))
                .OrderByDescending(t => UseCount(arrangement, t)).FirstOrDefault();
            if (target is null) return ChordShapeEditPlan.Refused(ChordShapeEditRefusal.NoMergeTarget);
            pairs.Add((source, target));
        }

        var actions = new List<IChartAction>();
        if (pairs.Count > 0) actions.Add(ChordShapeEdits.MoveUses(arrangement, pairs, editor));
        actions.Add(new DeleteChordShapesAction(arrangement, group, editor));
        var (chords, handShapes) = Moved(arrangement, pairs.Select(p => p.Source));
        return new ChordShapeEditPlan(new CompositeAction(actions) { LabelKey = ChordShapeEdits.DeleteLabelKey },
            ChordShapeEditRefusal.None, chords, handShapes, TemplatesRemoved: group.Count);
    }

    /// <summary>Every use of every ticked shape, on every level.</summary>
    internal static ShapeEditReach ReachOfGroup(ShapeEditInput input)
    {
        var chords = new List<Chord>();
        var handShapes = new List<HandShape>();
        foreach (var shape in input.Group)
        {
            var (c, h) = ChordShapeEdits.UsesOf(input.Arrangement, shape);
            chords.AddRange(c);
            handShapes.AddRange(h);
        }

        return new ShapeEditReach(chords, handShapes, ShapeEditPlanner.LevelsOf(input.Arrangement, chords, handShapes));
    }

    /// <summary>
    ///     The Edit together plan: every ticked shape takes the grip of <paramref name="draft" /> and they become ONE.
    ///     The shape that keeps the uses is the one that already has exactly this grip (the edited shape first, then a
    ///     ticked one, then any, the most used) or, when none does, the edited shape rewritten in place. Every other
    ///     ticked shape hands its chords (and, while <paramref name="syncHandShapes" />, its hand shapes) to it and is
    ///     removed once nothing uses it, in the same undo node.
    /// </summary>
    internal static ShapeEditPlan PlanTogether(ShapeEditInput input, ShapeEditDraft draft, bool syncHandShapes, IChartEditorSurface editor)
    {
        var arrangement = input.Arrangement;
        var primary = input.Shape;
        var group = input.Group;
        var reach = ReachOfGroup(input);
        var (totalChords, totalShapes) = Moved(arrangement, group);
        var candidate = ShapeEditPlanner.Apply(primary, draft);
        if (group.Count < 2)
            return new ShapeEditPlan(ShapeEditScope.Together, ShapeEditOutcome.Unchanged, reach, totalChords, totalShapes, 0, null, 0, null);

        var flavour = ArpeggioFlavour.Of(candidate);
        var matches = arrangement.ChordTemplates.Where(t => flavour.Matches(t) && t.HasSameShapeAs(candidate)).ToList();
        var rewrite = matches.Count == 0;
        var keeper = matches.Find(m => ReferenceEquals(m, primary))
                     ?? matches.OrderByDescending(m => group.Contains(m) ? 1 : 0).ThenByDescending(m => UseCount(arrangement, m)).FirstOrDefault()
                     ?? primary;
        var grip = rewrite ? candidate : keeper;

        var actions = new List<IChartAction>();
        if (rewrite)
        {
            var (primaryChords, primaryShapes) = ChordShapeEdits.UsesOf(arrangement, primary);
            actions.AddRange(ShapeEditPlanner.RewriteInPlace(arrangement, primary, candidate, primaryChords, primaryShapes, syncHandShapes, editor));
        }
        else if (ReferenceEquals(keeper, primary) && (candidate.Name != primary.Name || candidate.Unnamed != primary.Unnamed))
        {
            actions.Add(new SetTemplateNameAction(primary, primary.Name, candidate.Name, editor, candidate.Unnamed));
        }

        var others = group.Where(t => !ReferenceEquals(t, keeper)).ToList();
        var chords = new List<Chord>();
        var handShapes = new List<HandShape>();
        var emptied = 0;
        foreach (var other in others)
        {
            var (otherChords, otherShapes) = ChordShapeEdits.UsesOf(arrangement, other);
            chords.AddRange(otherChords.Concat(ChordShapeEdits.VoiceChordsOf(arrangement, other)));
            if (syncHandShapes) handShapes.AddRange(otherShapes);
            if (syncHandShapes || otherShapes.Count == 0) emptied++;
        }

        actions.Add(new SwitchTemplateAction(chords, [.. chords.Select(c => ShapeEditPlanner.Projected(c, grip))],
            handShapes, [.. handShapes.Select(h => ShapeEditPlanner.Repointed(h, keeper.Id))], editor));
        actions.Add(new RemoveStrandedShapesAction(arrangement, others, editor.RequestRedraw));

        var outside = !rewrite && !group.Contains(keeper) ? keeper : null;
        var affected = syncHandShapes ? reach.HandShapes.Count : ShapeEditPlanner.HandShapesUnder(arrangement, reach.Chords, reach.HandShapes);
        return new ShapeEditPlan(ShapeEditScope.Together, ShapeEditOutcome.MergesShapes, reach, totalChords, totalShapes, affected,
            outside, outside is null ? 0 : UseCount(arrangement, outside), ShapeEditPlanner.Composite(actions), emptied);
    }

    private static int UseCount(Arrangement arrangement, ChordTemplate template)
    {
        var (chords, handShapes) = ChordShapeEdits.UsesOf(arrangement, template);
        return chords.Count + handShapes.Count;
    }

    /// <summary>The chords and the hand shapes the given shapes have between them.</summary>
    private static (int Chords, int HandShapes) Moved(Arrangement arrangement, IEnumerable<ChordTemplate> templates)
    {
        int chords = 0, handShapes = 0;
        foreach (var template in templates)
        {
            var (c, h) = ChordShapeEdits.UsesOf(arrangement, template);
            chords += c.Count;
            handShapes += h.Count;
        }

        return (chords, handShapes);
    }
}
