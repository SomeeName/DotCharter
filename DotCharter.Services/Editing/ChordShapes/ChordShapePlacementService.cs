using DotCharter.Localization;
using DotCharter.Services.Editing.Highway;
using DotCharter.Services.Editing.Pen;
using DotCharter.Services.State;

namespace DotCharter.Services.Editing.ChordShapes;

/// <summary>What a placement did, in the words the user reads.</summary>
public sealed record ChordShapePlacementResult(ChordShapePlacementRefusal Refusal, string Message, int Placed = 0)
{
    public bool Succeeded => Refusal == ChordShapePlacementRefusal.None;
}

/// <summary>
/// Places a whole chord shape in one gesture: the Chord shapes list's click and
///     "Repeat previous chord". Every placement is ONE undo node built by
///     <see cref="ChordShapePlacement" />.
/// </summary>
public interface IChordShapePlacementService
{
    /// <summary>
    ///     Places <paramref name="template" /> at the pen — or, with sounds selected, reshapes every
    ///     selected sound onto it. The selection wins, the way it does for every keyboard verb.
    /// </summary>
    ChordShapePlacementResult PlaceShape(ChordTemplate template, int gridDivisor);

    /// <summary>Places the shape of the nearest chord before the pen at the pen.</summary>
    ChordShapePlacementResult RepeatPreviousChord(int gridDivisor);

    /// <summary>The shapes placed most recently on <paramref name="arrangement" />, newest first.</summary>
    IReadOnlyList<ChordTemplate> RecentShapes(Arrangement arrangement);
}

/// <inheritdoc />
public sealed class ChordShapePlacementService : IChordShapePlacementService
{
    /// <summary>How many shapes the recent strip keeps.</summary>
    public const int RecentCapacity = 6;

    private readonly IStrictCollisionService _collisions;
    private readonly IHighwayEditingService _highway;
    private readonly IHistoryService _history;
    private readonly ILocalizationService _localization;
    private readonly IAppLogger _logger;
    private readonly IChartPenService _pen;
    private readonly List<ChordTemplate> _recent = [];
    private readonly IEditingScope _scope;
    private readonly ISelectionService _selection;
    private readonly ProjectState _state;

    public ChordShapePlacementService(
        ProjectState state,
        IEditingScope scope,
        IHistoryService history,
        ISelectionService selection,
        IStrictCollisionService collisions,
        IHighwayEditingService highway,
        IChartPenService pen,
        ILocalizationService localization,
        IAppLogger logger)
    {
        _state = state;
        _scope = scope;
        _history = history;
        _selection = selection;
        _collisions = collisions;
        _highway = highway;
        _pen = pen;
        _localization = localization;
        _logger = logger;
    }

    public ChordShapePlacementResult PlaceShape(ChordTemplate template, int gridDivisor)
    {
        ArgumentNullException.ThrowIfNull(template);
        if (Target() is not { } target) return Refuse(ChordShapePlacementRefusal.NoChart);

        // The selection wins, then the pen — the same ladder every keyboard verb climbs.
        var selected = SelectedPositions();
        if (selected.Count == 0)
        {
            var position = _pen.PenPosition(gridDivisor);
            return Commit(ChordShapePlacement.Plan(target, template, [position]), null,
                (name, _) => _localization.Format("ChordShapes_Status_Placed", name));
        }

        // Selected NOTES become chords, so the selection follows them onto the chords; selected
        // chords are reshaped in place and stay selected as they are.
        var promotesNotes = _selection.SelectedNotes.Any(_scope.Notes.Contains);
        return Commit(ChordShapePlacement.Plan(target, template, selected), promotesNotes ? selected : null,
            (name, count) => _localization.Plural("ChordShapes_Status_ReshapedOne", "ChordShapes_Status_Reshaped", count, name));
    }

    public ChordShapePlacementResult RepeatPreviousChord(int gridDivisor)
    {
        if (Target() is not { } target) return Refuse(ChordShapePlacementRefusal.NoChart);

        var position = _pen.PenPosition(gridDivisor);
        return Commit(ChordShapePlacement.PlanRepeatPrevious(target, position), null,
            (name, _) => _localization.Format("ChordShapes_Status_Repeated", name));
    }

    public IReadOnlyList<ChordTemplate> RecentShapes(Arrangement arrangement)
    {
        ArgumentNullException.ThrowIfNull(arrangement);
        return _recent.Where(t => ChordTemplateCollectionOps.ContainsInstance(arrangement.ChordTemplates, t))
            .Take(RecentCapacity)
            .ToList();
    }

    private ChordShapePlacementTarget? Target() =>
        _scope.Arrangement is { } arrangement && _scope.Level != null
            ? new ChordShapePlacementTarget(arrangement, _scope.Notes, _scope.Chords, _collisions, _highway.GetLinkNextUpdater())
            : null;

    /// <summary>One position per selected sound of the level being edited, in time order.</summary>
    private List<FractionalPosition> SelectedPositions() =>
        _selection.SelectedChords.Where(_scope.Chords.Contains).Select(c => c.Position)
            .Concat(_selection.SelectedNotes.Where(_scope.Notes.Contains).Select(n => n.Position))
            .Distinct()
            .Order()
            .ToList();

    /// <summary>
    ///     One history node for a built plan, then the shape remembered.
    ///     <para>
    ///         A chord placed at the PEN is deliberately NOT selected. The selection wins over
    ///         the pen, so selecting it would turn the next palette click into a reshape of that
    ///         chord instead of a second chord at the playhead — and a fresh chord selection
    ///         pulls the inspector off the Chord shapes tab onto the Sound tab.
    ///     </para>
    /// </summary>
    private ChordShapePlacementResult Commit(
        ChordShapePlacementPlan plan, IReadOnlyCollection<FractionalPosition>? reselect, Func<string, int, string> describe)
    {
        if (plan.Action is not { } action || plan.Shape is not { } shape) return Refuse(plan.Refusal);
        if (!_history.Do(action)) return Refuse(ChordShapePlacementRefusal.SameShape);

        if (reselect != null) SelectChordsAt(reselect);
        Remember(shape);
        var placed = plan.Created + plan.Reshaped;
        var message = describe(NameOf(shape), placed);
        _state.MainDisplayMessage = message;
        _logger.Info(AppLogCategory.Editing,
            $"Chord shape '{shape.Name}' (id {shape.Id}) placed: {plan.Created} created, {plan.Reshaped} reshaped.");
        return new ChordShapePlacementResult(ChordShapePlacementRefusal.None, message, placed);
    }

    private ChordShapePlacementResult Refuse(ChordShapePlacementRefusal refusal)
    {
        var message = _localization[refusal switch
        {
            ChordShapePlacementRefusal.NoChart => "ChordShapes_Refused_NoChart",
            ChordShapePlacementRefusal.EmptyShape => "ChordShapes_Refused_EmptyShape",
            ChordShapePlacementRefusal.StringOutOfRange => "ChordShapes_Refused_StringOutOfRange",
            ChordShapePlacementRefusal.BelowCapo => "ChordShapes_Refused_BelowCapo",
            ChordShapePlacementRefusal.OffNeck => "ChordShapes_Refused_OffNeck",
            ChordShapePlacementRefusal.SameShape => "ChordShapes_Refused_SameShape",
            ChordShapePlacementRefusal.NoPreviousChord => "ChordShapes_Refused_NoPreviousChord",
            _ => "ChordShapes_Refused_NotInArrangement"
        }];
        _state.MainDisplayMessage = message;
        _logger.Info(AppLogCategory.Editing, $"Chord shape placement refused: {refusal}.");
        return new ChordShapePlacementResult(refusal, message);
    }

    /// <summary>The chords now at <paramref name="positions" /> become the selection.</summary>
    private void SelectChordsAt(IReadOnlyCollection<FractionalPosition> positions)
    {
        var wanted = positions.ToHashSet();
        var chords = _scope.Chords.Where(c => wanted.Contains(c.Position)).ToList();
        _selection.ClearSelections();
        SelectionBulkOps.ReplaceWith(_selection.SelectedChords, chords);
        _selection.SelectedChord = chords.FirstOrDefault();
    }

    private void Remember(ChordTemplate shape)
    {
        _recent.RemoveAll(t => ReferenceEquals(t, shape));
        _recent.Insert(0, shape);
        if (_recent.Count > RecentCapacity * 4) _recent.RemoveRange(RecentCapacity * 4, _recent.Count - RecentCapacity * 4);
    }

    private string NameOf(ChordTemplate template) =>
        ChordShapeNames.DisplayName(template, _scope.Arrangement?.StringCount ?? 6);
}
