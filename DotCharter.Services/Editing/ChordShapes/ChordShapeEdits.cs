using DotCharter.Services.State;

namespace DotCharter.Services.Editing.ChordShapes;

/// <summary>Why a Chord shapes edit was not built. <see cref="None" /> means it was.</summary>
public enum ChordShapeEditRefusal
{
    None,

    /// <summary>The template is not one of this arrangement's (by reference).</summary>
    NotInArrangement,

    /// <summary>Source and target are the same template, or share its id.</summary>
    SameTemplate,

    /// <summary>The two templates do not have the same frets: a merge would reshape chords.</summary>
    DifferentFrets,

    /// <summary>Same frets, different arpeggio flavour: a merge would change what the chords export as.</summary>
    DifferentArpeggio,

    /// <summary>Nothing uses the template, so there is nothing to move.</summary>
    Unused,

    /// <summary>No selected chord or hand shape uses the template.</summary>
    NothingSelected,

    /// <summary>The selection is every use: a split would only rename the template.</summary>
    SelectionIsEveryUse,

    /// <summary>The new name is the name the template already has.</summary>
    SameName
}

/// <summary>A planned Chord shapes edit: the ONE undoable action to commit, or why there is none.</summary>
public sealed record ChordShapeEditPlan(
    IChartAction? Action,
    ChordShapeEditRefusal Refusal,
    int ChordsMoved = 0,
    int HandShapesMoved = 0,
    ChordTemplate? Minted = null)
{
    public bool IsRefused => Action is null;

    public static ChordShapeEditPlan Refused(ChordShapeEditRefusal refusal) => new(null, refusal);
}

/// <summary>
///     The Chord shapes list's three template verbs, each built as ONE composite action so it is
///     one undo node. None of them mutates a shared template's shape: a merge and a split
///     repoint <c>TemplateId</c> (<see cref="SwitchTemplateAction" /> /
///     <see cref="CreateAndSwitchTemplateAction" />), and a rename changes a name only.
/// </summary>
public static class ChordShapeEdits
{
    public const string MergeLabelKey = "History_ChordShapes_Merge";
    public const string SplitLabelKey = "History_ChordShapes_Split";
    public const string RenameLabelKey = "History_ChordShapes_Rename";

    /// <summary>
    ///     Moves every chord and hand shape that uses <paramref name="source" />, on every level,
    ///     onto <paramref name="target" />. Refused unless the two have the same frets in the same
    ///     arpeggio flavour — the resolver's own key — so no chord changes shape or export name.
    ///     The source stays in the arrangement, unused.
    /// </summary>
    public static ChordShapeEditPlan PlanMerge(
        Arrangement arrangement, ChordTemplate source, ChordTemplate target, IChartEditorSurface editor)
    {
        ArgumentNullException.ThrowIfNull(arrangement);
        ArgumentNullException.ThrowIfNull(source);
        ArgumentNullException.ThrowIfNull(target);
        ArgumentNullException.ThrowIfNull(editor);

        if (!ChordTemplateCollectionOps.ContainsInstance(arrangement.ChordTemplates, source)
            || !ChordTemplateCollectionOps.ContainsInstance(arrangement.ChordTemplates, target))
        {
            return ChordShapeEditPlan.Refused(ChordShapeEditRefusal.NotInArrangement);
        }

        if (ReferenceEquals(source, target) || source.Id == target.Id)
            return ChordShapeEditPlan.Refused(ChordShapeEditRefusal.SameTemplate);
        if (!source.HasFrets(target.Frets))
            return ChordShapeEditPlan.Refused(ChordShapeEditRefusal.DifferentFrets);
        if (!ArpeggioFlavour.Of(source).Matches(target))
            return ChordShapeEditPlan.Refused(ChordShapeEditRefusal.DifferentArpeggio);

        var (chords, shapes) = UsesOf(arrangement, source);
        if (chords.Count == 0 && shapes.Count == 0)
            return ChordShapeEditPlan.Refused(ChordShapeEditRefusal.Unused);

        var action = new SwitchTemplateAction(
            chords, chords.Select(c => Repointed(c, target.Id)).ToList(),
            shapes, shapes.Select(h => Repointed(h, target.Id)).ToList(),
            editor);
        return new ChordShapeEditPlan(Composite(action, MergeLabelKey), ChordShapeEditRefusal.None,
            chords.Count, shapes.Count);
    }

    /// <summary>
    ///     Gives the selected uses of <paramref name="source" /> a template of their own: a copy
    ///     with the same frets, fingers, name and arpeggio flavour under a fresh id, so the
    ///     selection's fingering or name can change without touching the rest.
    /// </summary>
    public static ChordShapeEditPlan PlanSplit(
        Arrangement arrangement, ChordTemplate source,
        IEnumerable<Chord> selectedChords, IEnumerable<HandShape> selectedHandShapes,
        IChartEditorSurface editor)
    {
        ArgumentNullException.ThrowIfNull(arrangement);
        ArgumentNullException.ThrowIfNull(source);
        ArgumentNullException.ThrowIfNull(selectedChords);
        ArgumentNullException.ThrowIfNull(selectedHandShapes);
        ArgumentNullException.ThrowIfNull(editor);

        if (!ChordTemplateCollectionOps.ContainsInstance(arrangement.ChordTemplates, source))
            return ChordShapeEditPlan.Refused(ChordShapeEditRefusal.NotInArrangement);

        var (allChords, allShapes) = UsesOf(arrangement, source);
        var chordSet = new HashSet<Chord>(allChords, ReferenceEqualityComparer.Instance);
        var shapeSet = new HashSet<HandShape>(allShapes, ReferenceEqualityComparer.Instance);
        var chords = selectedChords.Distinct(ReferenceEqualityComparer.Instance).Cast<Chord>()
            .Where(chordSet.Contains).ToList();
        var shapes = selectedHandShapes.Distinct(ReferenceEqualityComparer.Instance).Cast<HandShape>()
            .Where(shapeSet.Contains).ToList();

        if (chords.Count == 0 && shapes.Count == 0)
            return ChordShapeEditPlan.Refused(ChordShapeEditRefusal.NothingSelected);
        if (chords.Count == allChords.Count && shapes.Count == allShapes.Count)
            return ChordShapeEditPlan.Refused(ChordShapeEditRefusal.SelectionIsEveryUse);

        var minted = source.Clone();
        minted.Id = arrangement.ChordTemplates.Count == 0 ? 0 : arrangement.ChordTemplates.Max(t => t.Id) + 1;
        var action = new CreateAndSwitchTemplateAction(
            arrangement.ChordTemplates, minted,
            chords, chords.Select(c => Repointed(c, minted.Id)).ToList(),
            shapes, shapes.Select(h => Repointed(h, minted.Id)).ToList(),
            editor);
        return new ChordShapeEditPlan(Composite(action, SplitLabelKey), ChordShapeEditRefusal.None,
            chords.Count, shapes.Count, minted);
    }

    /// <summary>Renames the shared template — every chord and hand shape that uses it reads the new name.</summary>
    public static ChordShapeEditPlan PlanRename(
        Arrangement arrangement, ChordTemplate template, string newName, IChartEditorSurface editor)
    {
        ArgumentNullException.ThrowIfNull(arrangement);
        ArgumentNullException.ThrowIfNull(template);
        ArgumentNullException.ThrowIfNull(editor);

        if (!ChordTemplateCollectionOps.ContainsInstance(arrangement.ChordTemplates, template))
            return ChordShapeEditPlan.Refused(ChordShapeEditRefusal.NotInArrangement);

        var name = (newName ?? "").Trim();
        if (string.Equals(name, template.Name, StringComparison.Ordinal))
            return ChordShapeEditPlan.Refused(ChordShapeEditRefusal.SameName);

        var action = new SetTemplateNameAction(template, template.Name, name, editor);
        return new ChordShapeEditPlan(Composite(action, RenameLabelKey), ChordShapeEditRefusal.None);
    }

    /// <summary>Every chord and hand shape of every level whose id resolves to <paramref name="template" />.</summary>
    public static (List<Chord> Chords, List<HandShape> HandShapes) UsesOf(Arrangement arrangement, ChordTemplate template)
    {
        ArgumentNullException.ThrowIfNull(arrangement);
        ArgumentNullException.ThrowIfNull(template);

        var chords = new List<Chord>();
        var shapes = new List<HandShape>();
        var byId = ChordTemplateResolver.IndexById(arrangement.ChordTemplates);
        if (!byId.TryGetValue(template.Id, out var resolved) || !ReferenceEquals(resolved, template))
            return (chords, shapes);

        foreach (var level in arrangement.Levels)
        {
            chords.AddRange(level.Chords.Where(c => c.TemplateId == template.Id));
            shapes.AddRange(level.HandShapes.Where(h => h.TemplateId == template.Id));
        }

        return (chords, shapes);
    }

    private static Chord Repointed(Chord chord, int templateId)
    {
        var clone = chord.Clone();
        clone.TemplateId = templateId;
        return clone;
    }

    private static HandShape Repointed(HandShape shape, int templateId)
    {
        var clone = shape.Clone();
        clone.TemplateId = templateId;
        return clone;
    }

    private static CompositeAction Composite(IChartAction action, string labelKey) =>
        new([action]) { LabelKey = labelKey };
}
