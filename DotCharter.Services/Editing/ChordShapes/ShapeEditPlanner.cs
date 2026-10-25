using DotCharter.Services.State;

namespace DotCharter.Services.Editing.ChordShapes;

/// <summary>How far one shape edit reaches. The charter picks it before anything is applied.</summary>
public enum ShapeEditScope
{
    /// <summary>The chord the editor was opened from.</summary>
    ThisChord,

    /// <summary>The selected chords (and hand shapes) that play the shape.</summary>
    Selection,

    /// <summary>Every chord and hand shape on the shape, on every level.</summary>
    EveryUse,

    /// <summary>Every use of every ticked shape: they all take the one grip typed, and become one shape.</summary>
    Together
}

/// <summary>What Apply will do, stated before it does it.</summary>
public enum ShapeEditOutcome
{
    /// <summary>Nothing differs from the shape as it is now, or the scope reaches nothing.</summary>
    Unchanged,

    /// <summary>The reached uses are every use: the shape itself is rewritten.</summary>
    ChangesShape,

    /// <summary>The reached uses leave others behind: they get a shape of their own.</summary>
    MakesNewShape,

    /// <summary>The edited grip already exists as another shape: the reached uses move onto it.</summary>
    JoinsExisting,

    /// <summary>The ticked shapes all take the grip and become one; the copies that end up unused are removed.</summary>
    MergesShapes
}

/// <summary>The grip as typed: frets and fingers by string index, plus the name.</summary>
public sealed record ShapeEditDraft(
    IReadOnlyDictionary<int, int> Frets, IReadOnlyDictionary<int, int> Fingers, string Name, bool Unnamed);

/// <summary>
///     What the editor was opened on: the shape, the chord it came from and the selection.
///     <paramref name="Together" /> holds the ticked shapes of an Edit together (the shape is one of them);
///     it is empty for the edit of one shape.
/// </summary>
public sealed record ShapeEditInput(
    Arrangement Arrangement, ChordTemplate Shape, Chord? Origin,
    IReadOnlyList<Chord> SelectedChords, IReadOnlyList<HandShape> SelectedHandShapes,
    IReadOnlyList<ChordTemplate>? Together = null)
{
    /// <summary>The ticked shapes, the shape itself first; just the shape when nothing is ticked.</summary>
    public IReadOnlyList<ChordTemplate> Group => ShapeBulkEdits.GroupOf(Arrangement, Shape, Together);
}

/// <summary>The chords and hand shapes one scope reaches.</summary>
public sealed record ShapeEditReach(IReadOnlyList<Chord> Chords, IReadOnlyList<HandShape> HandShapes, int Levels)
{
    public static readonly ShapeEditReach None = new([], [], 0);

    public int Count => Chords.Count + HandShapes.Count;
}

/// <summary>One scope's answer: what it reaches, what Apply does, and the ONE undoable action that does it.</summary>
public sealed record ShapeEditPlan(
    ShapeEditScope Scope,
    ShapeEditOutcome Outcome,
    ShapeEditReach Reach,
    int TotalChords,
    int TotalHandShapes,
    int HandShapesAffected,
    ChordTemplate? Match,
    int MatchUses,
    IChartAction? Action,
    int ShapesEmptied = 0)
{
    /// <summary>Chords on the shape the scope leaves alone.</summary>
    public int ChordsKept => TotalChords - Reach.Chords.Count;
}

/// <summary>
///     The scope-aware shape edit: one grip changes, and the scope says which uses follow. It is the
///     ONE rule the Edit shape dialog applies — the old "Selected" rung silently meant "every use" or
///     "a new shape" depending on what happened to be selected; here the outcome is derived from the
///     reach and stated first.
///     <list type="bullet">
///         <item>The reach covers every use: the shape itself changes (<see cref="ShapeEditOutcome.ChangesShape" />).</item>
///         <item>It leaves uses behind: the reached ones get a new shape (<see cref="ShapeEditOutcome.MakesNewShape" />).</item>
///         <item>The edited frets and fingers already exist as another shape: the reached uses JOIN it
///               (<see cref="ShapeEditOutcome.JoinsExisting" />) and keep its name; a shape it empties is
///               removed in the same undo node.</item>
///     </list>
///     Hand shapes over the reached chords follow the edit, or — with <c>syncHandShapes</c> off — keep
///     the old grip on a copy of it.
/// </summary>
public static class ShapeEditPlanner
{
    public const string EditLabelKey = "History_ChordShapes_Edit";

    /// <summary>The uses <paramref name="scope" /> reaches; hand shapes over a reached chord are in it only while <paramref name="syncHandShapes" />.</summary>
    public static ShapeEditReach ReachOf(ShapeEditInput input, ShapeEditScope scope, bool syncHandShapes)
    {
        ArgumentNullException.ThrowIfNull(input);
        if (scope == ShapeEditScope.Together) return ShapeBulkEdits.ReachOfGroup(input);
        var (allChords, allShapes) = ChordShapeEdits.UsesOf(input.Arrangement, input.Shape);
        var chordSet = new HashSet<Chord>(allChords, ReferenceEqualityComparer.Instance);
        var shapeSet = new HashSet<HandShape>(allShapes, ReferenceEqualityComparer.Instance);
        List<Chord> chords;
        List<HandShape> handShapes;
        switch (scope)
        {
            case ShapeEditScope.EveryUse:
                chords = allChords;
                handShapes = allShapes;
                break;
            case ShapeEditScope.ThisChord:
                chords = input.Origin is { } origin && chordSet.Contains(origin) ? [origin] : [];
                handShapes = [];
                break;
            default:
                chords = [.. input.SelectedChords.Where(chordSet.Contains).Distinct(ReferenceEqualityComparer.Instance).Cast<Chord>()];
                handShapes = [.. input.SelectedHandShapes.Where(shapeSet.Contains).Distinct(ReferenceEqualityComparer.Instance).Cast<HandShape>()];
                break;
        }

        if (syncHandShapes && scope != ShapeEditScope.EveryUse)
            handShapes = [.. handShapes.Concat(HandShapesOver(input.Arrangement, chords, shapeSet)).Distinct(ReferenceEqualityComparer.Instance).Cast<HandShape>()];
        return new ShapeEditReach(chords, handShapes, LevelsOf(input.Arrangement, chords, handShapes));
    }

    /// <summary>The existing shape (not <paramref name="shape" />) that has exactly this grip, or null.</summary>
    public static ChordTemplate? FindMatch(Arrangement arrangement, ChordTemplate shape, ChordTemplate candidate)
    {
        ArgumentNullException.ThrowIfNull(arrangement);
        var flavour = ArpeggioFlavour.Of(candidate);
        return arrangement.ChordTemplates.FirstOrDefault(t =>
            !ReferenceEquals(t, shape) && t.Id != shape.Id && flavour.Matches(t) && t.HasSameShapeAs(candidate));
    }

    /// <summary>The shape as it will be once the draft is applied: <paramref name="shape" /> with the draft's frets, fingers and name.</summary>
    public static ChordTemplate Apply(ChordTemplate shape, ShapeEditDraft draft)
    {
        ArgumentNullException.ThrowIfNull(shape);
        ArgumentNullException.ThrowIfNull(draft);
        var result = shape.Clone();
        result.Frets = new Dictionary<int, int>(draft.Frets);
        result.Fingers = draft.Fingers.Where(f => draft.Frets.ContainsKey(f.Key)).ToDictionary(f => f.Key, f => f.Value);
        result.Name = draft.Unnamed ? "" : draft.Name.Trim();
        result.Unnamed = draft.Unnamed;
        if (result.Fingers.Count > 0) result.Fingerless = false;
        if (!SameGrip(shape, result))
        {
            // An authored grip: no automatic flow re-fingers or replaces it.
            result.FollowedFingering = false;
            result.Pinned = true;
        }

        return result;
    }

    /// <summary>True when frets and fingers are the same.</summary>
    public static bool SameGrip(ChordTemplate a, ChordTemplate b) => a.HasSameShapeAs(b);

    /// <summary>Plans the edit of <paramref name="input" />'s shape to <paramref name="draft" /> on <paramref name="scope" />.</summary>
    public static ShapeEditPlan Plan(
        ShapeEditInput input, ShapeEditScope scope, ShapeEditDraft draft, bool syncHandShapes, IChartEditorSurface editor)
    {
        ArgumentNullException.ThrowIfNull(input);
        ArgumentNullException.ThrowIfNull(draft);
        ArgumentNullException.ThrowIfNull(editor);
        if (scope == ShapeEditScope.Together) return ShapeBulkEdits.PlanTogether(input, draft, syncHandShapes, editor);

        var arrangement = input.Arrangement;
        var shape = input.Shape;
        var (allChords, allShapes) = ChordShapeEdits.UsesOf(arrangement, shape);
        var reach = ReachOf(input, scope, syncHandShapes);
        var candidate = Apply(shape, draft);
        var gripChanged = !SameGrip(shape, candidate);
        var match = gripChanged ? FindMatch(arrangement, shape, candidate) : null;
        var matchUses = match is null ? 0 : ChordShapeEdits.UsesOf(arrangement, match) is var (mc, ms) ? mc.Count + ms.Count : 0;

        bool Unchanged() => !gripChanged && candidate.Name == shape.Name && candidate.Unnamed == shape.Unnamed;
        ShapeEditPlan Refused() => new(scope, ShapeEditOutcome.Unchanged, reach, allChords.Count, allShapes.Count,
            reach.HandShapes.Count, match, matchUses, null);
        if (reach.Count == 0 || Unchanged()) return Refused();

        if (match is not null)
        {
            var moved = new SwitchTemplateAction(
                [.. reach.Chords], [.. reach.Chords.Select(c => Projected(c, match))],
                [.. reach.HandShapes], [.. reach.HandShapes.Select(h => Repointed(h, match.Id))], editor);
            return Planned(ShapeEditOutcome.JoinsExisting, StrandedShapes.Wrap(arrangement, Composite(moved), editor.RequestRedraw));
        }

        var coversEveryUse = reach.Chords.Count == allChords.Count
                             && (allChords.Count > 0 || reach.HandShapes.Count == allShapes.Count);
        if (coversEveryUse)
        {
            var rewrite = RewriteInPlace(arrangement, shape, candidate, allChords, allShapes, syncHandShapes, editor);
            return Planned(ShapeEditOutcome.ChangesShape, Composite(rewrite));
        }

        var minted = candidate.Clone();
        minted.Id = NextId(arrangement);
        var made = new CreateAndSwitchTemplateAction(arrangement.ChordTemplates, minted,
            [.. reach.Chords], [.. reach.Chords.Select(c => Projected(c, minted))],
            [.. reach.HandShapes], [.. reach.HandShapes.Select(h => Repointed(h, minted.Id))], editor);
        return Planned(ShapeEditOutcome.MakesNewShape, StrandedShapes.Wrap(arrangement, Composite(made), editor.RequestRedraw));

        ShapeEditPlan Planned(ShapeEditOutcome outcome, IChartAction action) =>
            new(scope, outcome, reach, allChords.Count, allShapes.Count,
                syncHandShapes ? reach.HandShapes.Count : HandShapesUnder(arrangement, reach.Chords, allShapes),
                match, matchUses, action);
    }

    /// <summary>
    ///     Rewrites <paramref name="shape" /> into <paramref name="candidate" /> for every use it has. Hand shapes that
    ///     must not follow (<paramref name="syncHandShapes" /> off) keep the old grip on a copy of it.
    /// </summary>
    internal static List<IChartAction> RewriteInPlace(
        Arrangement arrangement, ChordTemplate shape, ChordTemplate candidate,
        List<Chord> chords, List<HandShape> handShapes, bool syncHandShapes, IChartEditorSurface editor)
    {
        var oldState = shape.Clone();
        var actions = new List<IChartAction>
        {
            new EditTemplateGlobalAction(shape, oldState, candidate, [.. chords],
                [.. chords.Select(c => Projected(c, candidate))], editor)
        };
        var keepers = syncHandShapes ? [] : handShapes;
        if (keepers.Count > 0)
        {
            var kept = oldState.Clone();
            kept.Id = NextId(arrangement);
            actions.Add(new CreateAndSwitchTemplateAction(arrangement.ChordTemplates, kept, [], [],
                [.. keepers], [.. keepers.Select(h => Repointed(h, kept.Id))], editor));
        }

        return actions;
    }

    internal static CompositeAction Composite(IChartAction action) => Composite([action]);

    internal static CompositeAction Composite(IEnumerable<IChartAction> actions) => new(actions) { LabelKey = EditLabelKey };

    private static int NextId(Arrangement arrangement) =>
        arrangement.ChordTemplates.Count == 0 ? 0 : arrangement.ChordTemplates.Max(t => t.Id) + 1;

    internal static Chord Projected(Chord chord, ChordTemplate template)
    {
        var clone = chord.Clone();
        clone.TemplateId = template.Id;
        clone.ChordNotes = ChordNoteProjection.OnTo(clone, template);
        return clone;
    }

    internal static HandShape Repointed(HandShape shape, int templateId)
    {
        var clone = shape.Clone();
        clone.TemplateId = templateId;
        return clone;
    }

    internal static int LevelsOf(Arrangement arrangement, IReadOnlyList<Chord> chords, IReadOnlyList<HandShape> handShapes)
    {
        var chordSet = new HashSet<Chord>(chords, ReferenceEqualityComparer.Instance);
        var shapeSet = new HashSet<HandShape>(handShapes, ReferenceEqualityComparer.Instance);
        return arrangement.Levels.Count(l => l.Chords.Any(chordSet.Contains) || l.HandShapes.Any(shapeSet.Contains));
    }

    /// <summary>The hand shapes of <paramref name="candidates" /> that cover one of <paramref name="chords" />, on the chord's own level.</summary>
    internal static List<HandShape> HandShapesOver(Arrangement arrangement, IReadOnlyList<Chord> chords, HashSet<HandShape> candidates)
    {
        var result = new List<HandShape>();
        var wanted = new HashSet<Chord>(chords, ReferenceEqualityComparer.Instance);
        foreach (var level in arrangement.Levels)
        {
            var onLevel = level.Chords.Where(wanted.Contains).ToList();
            if (onLevel.Count == 0) continue;
            result.AddRange(level.HandShapes.Where(h => candidates.Contains(h)
                                                        && onLevel.Any(c => c.Position >= h.Position && c.Position < h.EndPosition)));
        }

        return result;
    }

    internal static int HandShapesUnder(Arrangement arrangement, IReadOnlyList<Chord> chords, IEnumerable<HandShape> all) =>
        HandShapesOver(arrangement, chords, new HashSet<HandShape>(all, ReferenceEqualityComparer.Instance)).Count;
}
