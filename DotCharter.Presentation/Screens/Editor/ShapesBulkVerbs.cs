using CommunityToolkit.Mvvm.Input;
using DotCharter.Core.Domain.Guitar;
using DotCharter.Core.Domain.Project;
using DotCharter.Localization;
using DotCharter.Presentation.Contracts.Editor;
using DotCharter.Presentation.Dialogs;
using DotCharter.Services.Editing;
using DotCharter.Services.Editing.ChordShapes;
using DotCharter.Services.State;

namespace DotCharter.Presentation.Screens.Editor;

/// <summary>What the bulk verbs need from the panel that owns them.</summary>
internal interface IShapesBulkHost
{
    IReadOnlyList<ShapePanelRowViewModel> AllRows { get; }

    /// <summary>One history node for a built plan, then the list re-read.</summary>
    bool Commit(ChordShapeEditPlan plan);

    void Say(string text, StatusSeverity severity = StatusSeverity.Info);

    /// <summary>Puts the one confirmation bar up with this sentence.</summary>
    void ShowPending(string text, string confirmLabel, bool destructive);

    void HidePending();

    /// <summary>The ticked cards changed: the bar's headline ("3 shapes selected · 37 chords"), empty when none is ticked.</summary>
    void ShowSelection(string text);

    /// <summary>A single ticked shape's Delete is the card's own Delete.</summary>
    void DeleteOne(ShapePanelRowViewModel row);
}

/// <summary>
///     The Shapes panel's verbs on the ticked cards: Edit together, Merge into one and Delete. Merge and a delete
///     of used shapes say what moves where in the panel's confirmation bar first; each is ONE undo step through
///     <see cref="ShapeBulkEdits" />, and Edit together opens the shape editor on all the ticked shapes.
/// </summary>
internal sealed class ShapesBulkVerbs
{
    private sealed record BulkPending(bool Delete, IReadOnlyList<ChordTemplate> Shapes);

    private readonly IChartEditorSurface _editor;
    private readonly ILocalizationService _loc;
    private readonly IDialogs _dialogs;
    private readonly IShapesBulkHost _host;
    private BulkPending? _pending;
    private bool _clearing;

    public ShapesBulkVerbs(IChartEditorSurface editor, ILocalizationService loc, IDialogs dialogs, IShapesBulkHost host)
    {
        _editor = editor;
        _loc = loc;
        _dialogs = dialogs;
        _host = host;
        EditCommand = new RelayCommand(EditSelected, () => Picked().Count > 0);
        MergeCommand = new RelayCommand(AskMerge, () => Picked().Count > 1);
        DeleteCommand = new RelayCommand(AskDelete, () => Picked().Count > 0);
        ClearCommand = new RelayCommand(Clear, () => Picked().Count > 0);
    }

    public RelayCommand EditCommand { get; }

    public RelayCommand MergeCommand { get; }

    public RelayCommand DeleteCommand { get; }

    public RelayCommand ClearCommand { get; }

    public bool HasPending => _pending is not null;

    /// <summary>The ticks changed: re-arms the commands, drops a bar for another set of shapes and states the headline.</summary>
    public void Refresh()
    {
        if (_clearing) return;
        var picked = Picked();
        foreach (var command in new[] { EditCommand, MergeCommand, DeleteCommand, ClearCommand }) command.NotifyCanExecuteChanged();
        if (_pending is { } pending && !(pending.Shapes.Count == picked.Count
                                         && picked.All(r => pending.Shapes.Any(t => ReferenceEquals(t, r.Template)))))
        {
            _pending = null;
            _host.HidePending();
        }

        var chords = picked.Sum(r => r.Entry?.Uses.ChordsAll ?? 0);
        _host.ShowSelection(picked.Count == 0 ? "" : _loc.Format("Shapes_Selection",
            _loc.Plural("ChordShapes_ShapeCountOne", "ChordShapes_ShapeCount", picked.Count),
            _loc.Plural("ChordShapes_ChordsOne", "ChordShapes_Chords", chords)));
    }

    /// <summary>Drops a bar whose shapes an undo or another edit took out of the arrangement.</summary>
    public void DropIfStale(Arrangement arrangement)
    {
        if (_pending is null) return;
        if (_pending.Shapes.All(t => ChordTemplateCollectionOps.ContainsInstance(arrangement.ChordTemplates, t))) return;
        _pending = null;
        _host.HidePending();
    }

    public void Drop() => _pending = null;

    public void Clear()
    {
        _clearing = true;
        foreach (var row in _host.AllRows) row.IsSelected = false;
        _clearing = false;
        Refresh();
    }

    /// <summary>Runs the verb in the bar after its yes. False when no bulk verb waits.</summary>
    public bool Confirm()
    {
        if (_pending is not { } pending || _editor.ActiveArrangement is not { } arrangement) return false;
        _pending = null;
        _host.HidePending();
        var plan = pending.Delete
            ? ShapeBulkEdits.PlanDeleteMany(arrangement, pending.Shapes, _editor)
            : ShapeBulkEdits.PlanMergeMany(arrangement, pending.Shapes, _editor);
        var keeper = pending.Delete ? null : ShapeBulkEdits.KeeperOf(arrangement, pending.Shapes);
        var name = keeper is null ? "" : ChordShapeNames.DisplayName(keeper, arrangement.StringCount);
        var moved = ShapePanelRowViewModel.UsesWords(_loc, plan.ChordsMoved, plan.HandShapesMoved);
        if (!Run(plan)) return true;
        _host.Say(pending.Delete
            ? _loc.Format("Shapes_Status_DeletedMany", plan.TemplatesRemoved, moved)
            : _loc.Format("ChordShapes_Status_Merged", name, moved));
        Clear();
        return true;
    }

    private List<ShapePanelRowViewModel> Picked() => [.. _host.AllRows.Where(r => r.IsSelected)];

    private void EditSelected()
    {
        var picked = Picked();
        if (_editor.ActiveArrangement is not { } arrangement || picked.Count == 0) return;
        var shapes = picked.Select(r => r.Template).ToList();
        if (ShapeBulkEdits.KeeperOf(arrangement, shapes) is { } primary) _ = OpenTogether(new ShapeEditRequest(primary, null) { Together = shapes });
    }

    private async Task OpenTogether(ShapeEditRequest request)
    {
        if (await _dialogs.ShowAsync(request)) Clear();
    }

    private void AskMerge()
    {
        var picked = Picked();
        if (_editor.ActiveArrangement is not { } arrangement) return;
        var shapes = picked.Select(r => r.Template).ToList();
        var plan = ShapeBulkEdits.PlanMergeMany(arrangement, shapes, _editor);
        if (plan.Action is null)
        {
            Refuse(plan);
            return;
        }

        var keeper = ShapeBulkEdits.KeeperOf(arrangement, shapes)!;
        var keeperRow = picked.First(r => ReferenceEquals(r.Template, keeper));
        _pending = new BulkPending(false, shapes);
        var text = _loc.PluralWith("Shapes_Pending_MergeManyOne", "Shapes_Pending_MergeMany", plan.TemplatesRemoved,
            shapes.Count, keeperRow.Title, keeperRow.UsesText, keeperRow.UsesOf(plan.ChordsMoved, plan.HandShapesMoved), plan.TemplatesRemoved);
        _host.ShowPending(text, _loc["Shapes_Pending_MergeConfirm"], false);
    }

    private void AskDelete()
    {
        var picked = Picked();
        if (_editor.ActiveArrangement is not { } arrangement || picked.Count == 0) return;
        if (picked.Count == 1)
        {
            _host.DeleteOne(picked[0]);
            return;
        }

        var shapes = picked.Select(r => r.Template).ToList();
        var plan = ShapeBulkEdits.PlanDeleteMany(arrangement, shapes, _editor);
        if (plan.Action is null)
        {
            Refuse(plan);
            return;
        }

        if (picked.All(r => r.IsUnused))
        {
            if (!Run(plan)) return;
            _host.Say(_loc.Plural("ChordShapes_Status_RemovedUnusedOne", "ChordShapes_Status_RemovedUnused", plan.TemplatesRemoved));
            Clear();
            return;
        }

        _pending = new BulkPending(true, shapes);
        _host.ShowPending(_loc.Format("Shapes_Pending_DeleteMany", shapes.Count, picked[0].UsesOf(plan.ChordsMoved, plan.HandShapesMoved)),
            _loc.Format("Shapes_Pending_DeleteManyConfirm", shapes.Count), true);
    }

    private bool Run(ChordShapeEditPlan plan)
    {
        if (plan.Action is null)
        {
            Refuse(plan);
            return false;
        }

        return _host.Commit(plan);
    }

    private void Refuse(ChordShapeEditPlan plan) => _host.Say(_loc[plan.Refusal switch
    {
        ChordShapeEditRefusal.NeedsTwoShapes => "Shapes_Refused_NeedTwo",
        ChordShapeEditRefusal.DifferentFrets or ChordShapeEditRefusal.DifferentArpeggio => "Shapes_Refused_BulkFrets",
        ChordShapeEditRefusal.NoMergeTarget => "Shapes_Refused_NoTarget",
        _ => "ChordShapes_Refused_NotInArrangement"
    }], StatusSeverity.Warning);
}
