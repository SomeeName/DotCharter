using DotCharter.Services.Editing.Fingering;
using DotCharter.Services.Editing.Improvement;
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
    SameName,

    /// <summary>A chord or hand shape on some level still uses the template: merge it into another first.</summary>
    InUse,

    /// <summary>Every template is in use: there is nothing to remove.</summary>
    NothingUnused,

    /// <summary>The template is pinned: an automatic fingering leaves it as it is.</summary>
    Pinned,

    /// <summary>The suggester has no fingering to offer that differs from the one the shape wears.</summary>
    NoSuggestion,

    /// <summary>A bulk verb needs at least two shapes.</summary>
    NeedsTwoShapes,

    /// <summary>A used shape has no other shape with the same frets to hand its uses to.</summary>
    NoMergeTarget
}

/// <summary>A planned Chord shapes edit: the ONE undoable action to commit, or why there is none.</summary>
public sealed record ChordShapeEditPlan(
    IChartAction? Action,
    ChordShapeEditRefusal Refusal,
    int ChordsMoved = 0,
    int HandShapesMoved = 0,
    ChordTemplate? Minted = null,
    int TemplatesRemoved = 0)
{
    public bool IsRefused => Action is null;

    public static ChordShapeEditPlan Refused(ChordShapeEditRefusal refusal) => new(null, refusal);
}

/// <summary>
///     The Chord shapes list's template verbs, each built as ONE composite action so it is one
///     undo node. None of them mutates a shared template's shape: a merge and a split repoint
///     <c>TemplateId</c> (<see cref="SwitchTemplateAction" /> /
///     <see cref="CreateAndSwitchTemplateAction" />), a rename changes a name only, and a delete
///     takes out only templates nothing uses (<see cref="DeleteChordShapesAction" />).
/// </summary>
public static class ChordShapeEdits
{
    public const string MergeLabelKey = "History_ChordShapes_Merge";
    public const string SplitLabelKey = "History_ChordShapes_Split";
    public const string RenameLabelKey = "History_ChordShapes_Rename";
    public const string DeleteLabelKey = "History_ChordShapes_Delete";
    public const string RemoveUnusedLabelKey = "History_ChordShapes_RemoveUnused";
    public const string FingerLabelKey = "History_ChordShapes_Finger";

    private static readonly FingeringGenerationService Fingering = new();

    /// <summary>
    ///     Deletes <paramref name="template" />. Refused while any chord, hand shape or notation-voice
    ///     chord on any level uses it: merge it into another shape first, so no chord loses its shape.
    /// </summary>
    public static ChordShapeEditPlan PlanDelete(Arrangement arrangement, ChordTemplate template, IChartEditorSurface editor)
    {
        ArgumentNullException.ThrowIfNull(arrangement);
        ArgumentNullException.ThrowIfNull(template);
        ArgumentNullException.ThrowIfNull(editor);

        if (!ChordTemplateCollectionOps.ContainsInstance(arrangement.ChordTemplates, template))
            return ChordShapeEditPlan.Refused(ChordShapeEditRefusal.NotInArrangement);
        if (LevelTemplateOrphans.UsedTemplates(arrangement).Contains(template))
            return ChordShapeEditPlan.Refused(ChordShapeEditRefusal.InUse);

        var action = new DeleteChordShapesAction(arrangement, [template], editor);
        return new ChordShapeEditPlan(Composite(action, DeleteLabelKey), ChordShapeEditRefusal.None, TemplatesRemoved: 1);
    }

    /// <summary>Deletes every template no chord, hand shape or notation-voice chord on any level uses.</summary>
    public static ChordShapeEditPlan PlanRemoveUnused(Arrangement arrangement, IChartEditorSurface editor)
    {
        ArgumentNullException.ThrowIfNull(arrangement);
        ArgumentNullException.ThrowIfNull(editor);

        var unused = UnusedTemplates(arrangement);
        if (unused.Count == 0) return ChordShapeEditPlan.Refused(ChordShapeEditRefusal.NothingUnused);

        var action = new DeleteChordShapesAction(arrangement, unused, editor);
        return new ChordShapeEditPlan(Composite(action, RemoveUnusedLabelKey), ChordShapeEditRefusal.None,
            TemplatesRemoved: unused.Count);
    }

    /// <summary>The templates of <paramref name="arrangement" /> nothing on any level uses, in list order.</summary>
    public static List<ChordTemplate> UnusedTemplates(Arrangement arrangement)
    {
        ArgumentNullException.ThrowIfNull(arrangement);
        var used = LevelTemplateOrphans.UsedTemplates(arrangement);
        return [.. arrangement.ChordTemplates.Where(t => !used.Contains(t))];
    }

    /// <summary>
    ///     Moves every chord and hand shape that uses <paramref name="source" />, on every level
    ///     (and every notation-voice chord), onto <paramref name="target" />, then takes the emptied
    ///     source out of the arrangement. Refused unless the two have the same frets in the same
    ///     arpeggio flavour — the resolver's own key — so no chord changes shape or export name.
    ///     The target keeps its own fingers and name. One undo node.
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
        if (chords.Count == 0 && shapes.Count == 0 && VoiceChordsOf(arrangement, source).Count == 0)
            return ChordShapeEditPlan.Refused(ChordShapeEditRefusal.Unused);

        var repoint = MoveUses(arrangement, [(source, target)], editor);
        var remove = new DeleteChordShapesAction(arrangement, [source], editor);
        return new ChordShapeEditPlan(
            new CompositeAction([repoint, remove]) { LabelKey = MergeLabelKey }, ChordShapeEditRefusal.None,
            chords.Count, shapes.Count, TemplatesRemoved: 1);
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

    /// <summary>
    ///     Sets the shared template's name and whether it deliberately has none, as one undo node:
    ///     <paramref name="unnamed" /> with an empty name is No name, an empty name without it
    ///     leaves the shape free for an automatic name. Every use reads the result.
    /// </summary>
    public static ChordShapeEditPlan PlanSetName(
        Arrangement arrangement, ChordTemplate template, string newName, bool unnamed, IChartEditorSurface editor)
    {
        ArgumentNullException.ThrowIfNull(arrangement);
        ArgumentNullException.ThrowIfNull(template);
        ArgumentNullException.ThrowIfNull(editor);

        if (!ChordTemplateCollectionOps.ContainsInstance(arrangement.ChordTemplates, template))
            return ChordShapeEditPlan.Refused(ChordShapeEditRefusal.NotInArrangement);

        var name = unnamed ? "" : (newName ?? "").Trim();
        if (string.Equals(name, template.Name, StringComparison.Ordinal) && unnamed == template.Unnamed)
            return ChordShapeEditPlan.Refused(ChordShapeEditRefusal.SameName);

        var action = new SetTemplateNameAction(template, template.Name, name, editor, unnamed);
        return new ChordShapeEditPlan(Composite(action, RenameLabelKey), ChordShapeEditRefusal.None);
    }

    /// <summary>
    ///     Gives the template the fingering the suggester proposes for this arrangement, as one
    ///     undo node. A pinned template is left as it is, like every automatic fingering.
    /// </summary>
    public static ChordShapeEditPlan PlanSuggestFingers(
        Arrangement arrangement, ChordTemplate template, IChartEditorSurface editor)
    {
        ArgumentNullException.ThrowIfNull(arrangement);
        ArgumentNullException.ThrowIfNull(template);
        ArgumentNullException.ThrowIfNull(editor);

        if (!ChordTemplateCollectionOps.ContainsInstance(arrangement.ChordTemplates, template))
            return ChordShapeEditPlan.Refused(ChordShapeEditRefusal.NotInArrangement);
        if (template.IsPinned) return ChordShapeEditPlan.Refused(ChordShapeEditRefusal.Pinned);

        var suggested = Fingering.SuggestFingeringFor(arrangement, template);
        if (suggested is null) return ChordShapeEditPlan.Refused(ChordShapeEditRefusal.NoSuggestion);

        var action = new SetTemplateFingersAction(
            template, new Dictionary<int, int>(template.Fingers), new Dictionary<int, int>(suggested.Fingers), editor);
        return new ChordShapeEditPlan(Composite(action, FingerLabelKey), ChordShapeEditRefusal.None);
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

    /// <summary>
    ///     ONE action that points every use of each source at its target: the chords on every level, the hand
    ///     shapes, and the notation-voice chords, which are uses too — left on the source's id they would dangle
    ///     once the source is gone. The pairs have the same frets, so no chord's notes change.
    /// </summary>
    internal static SwitchTemplateAction MoveUses(
        Arrangement arrangement, IEnumerable<(ChordTemplate Source, ChordTemplate Target)> pairs, IChartEditorSurface editor)
    {
        var chords = new List<Chord>();
        var chordsTo = new List<Chord>();
        var shapes = new List<HandShape>();
        var shapesTo = new List<HandShape>();
        foreach (var (source, target) in pairs)
        {
            var (sourceChords, sourceShapes) = UsesOf(arrangement, source);
            foreach (var chord in sourceChords.Concat(VoiceChordsOf(arrangement, source)))
            {
                chords.Add(chord);
                chordsTo.Add(Repointed(chord, target.Id));
            }

            foreach (var shape in sourceShapes)
            {
                shapes.Add(shape);
                shapesTo.Add(Repointed(shape, target.Id));
            }
        }

        return new SwitchTemplateAction(chords, chordsTo, shapes, shapesTo, editor);
    }

    /// <summary>Every notation-voice chord whose id resolves to <paramref name="template" />.</summary>
    internal static List<Chord> VoiceChordsOf(Arrangement arrangement, ChordTemplate template)
    {
        var byId = ChordTemplateResolver.IndexById(arrangement.ChordTemplates);
        if (!byId.TryGetValue(template.Id, out var resolved) || !ReferenceEquals(resolved, template)) return [];
        return [.. arrangement.NotationVoices.SelectMany(v => v.Chords).Where(c => c.TemplateId == template.Id)];
    }

    internal static Chord Repointed(Chord chord, int templateId)
    {
        var clone = chord.Clone();
        clone.TemplateId = templateId;
        return clone;
    }

    internal static HandShape Repointed(HandShape shape, int templateId)
    {
        var clone = shape.Clone();
        clone.TemplateId = templateId;
        return clone;
    }

    private static CompositeAction Composite(IChartAction action, string labelKey) =>
        new([action]) { LabelKey = labelKey };
}
