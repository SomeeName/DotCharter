using DotCharter.Presentation.Contracts.Editor;
using DotCharter.Presentation.Dialogs;
using DotCharter.Core.Domain.Guitar;
using DotCharter.Core.Domain.Project;
using DotCharter.Core.Utilities;
using DotCharter.Services.Editing.ChordShapes;
using DotCharter.Services.Editing.Selection;
using DotCharter.Services.State;

namespace DotCharter.Presentation.Screens.Editor;

// The card verbs (place, select uses, name, No name, fix, merge, delete), the one confirmation
// bar they share, and Clean up over the whole list.
public sealed partial class ShapesPanelViewModel
{
    /// <summary>A merge waiting for its yes: <see cref="Delete" /> says the merge also deletes the source.</summary>
    private sealed record PendingVerb(ChordTemplate Source, ChordTemplate Target, bool Delete);

    // The bulk verbs' seams: they share this panel's bar, status rail and commit.
    IReadOnlyList<ShapePanelRowViewModel> IShapesBulkHost.AllRows => _allRows;

    bool IShapesBulkHost.Commit(ChordShapeEditPlan plan) => Commit(plan);

    void IShapesBulkHost.Say(string text, StatusSeverity severity) => Say(text, severity);

    void IShapesBulkHost.ShowSelection(string text)
    {
        SelectionText = text;
        HasSelection = text.Length > 0;
    }

    void IShapesBulkHost.ShowPending(string text, string confirmLabel, bool destructive)
    {
        _pending = null;
        _mergeSource = null;
        HasPending = true;
        PendingText = text;
        PendingConfirmLabel = confirmLabel;
        IsPendingDestructive = destructive;
        RefreshMergeState();
    }

    void IShapesBulkHost.HidePending()
    {
        SetPending(null);
        _mergeSource = null;
        RefreshMergeState();
    }

    void IShapesBulkHost.DeleteOne(ShapePanelRowViewModel row) => ((IShapeRowOwner)this).Delete(row);

    void IShapeRowOwner.SelectionChanged() => _bulk.Refresh();

    ShapesDragVerbs IShapeRowOwner.Drag => _drag;

    // The drag-to-merge seams: a drag picks its source like Merge… does and ends in the same bar.
    ShapePanelRowViewModel? IShapesDragHost.MergeSource => _mergeSource;

    void IShapesDragHost.Show(ShapePanelRowViewModel? source, ShapePanelRowViewModel? over)
    {
        SetPending(null);
        _mergeSource = source;
        _dragOver = over;
        RefreshMergeState();
    }

    void IShapesDragHost.MergeOrRefuse(ShapePanelRowViewModel source, ShapePanelRowViewModel target)
    {
        if (_editor.ActiveArrangement is not { } arrangement) return;
        var plan = ChordShapeEdits.PlanMerge(arrangement, source.Template, target.Template, _editor);
        if (plan.Action is not null) AskMerge(source.Template, target.Template, delete: false);
        else Commit(plan);
    }

    // The placement service writes its own outcome to the status rail.
    void IShapeRowOwner.Edit(ShapePanelRowViewModel row) => _ = _dialogs.ShowAsync(new ShapeEditRequest(row.Template, null));

    void IShapeRowOwner.Place(ShapePanelRowViewModel row) => _placement.PlaceShape(row.Template, _transport.GridDivisor);

    private void RepeatPreviousChord() => _placement.RepeatPreviousChord(_transport.GridDivisor);

    void IShapeRowOwner.SelectUses(ShapePanelRowViewModel row)
    {
        var level = _state.ActiveLevel;
        if (level is null || row.Entry is not { } entry) return;

        var id = row.Template.Id;
        var chords = level.Chords.Where(c => c.TemplateId == id).ToList();
        var shapes = level.HandShapes.Where(h => h.TemplateId == id).ToList();
        var selection = _editor.Selection;
        selection.ClearSelections();
        SelectionBulkOps.ReplaceWith(selection.SelectedChords, chords);
        SelectionBulkOps.ReplaceWith(selection.SelectedHandShapes, shapes);
        selection.SelectedChord = chords.FirstOrDefault();
        _editor.RequestRedraw();

        if (chords.Count == 0 && shapes.Count == 0)
        {
            Say(_localization["ChordShapes_Status_NoUseOnLevel"], StatusSeverity.Warning);
            return;
        }

        var selected = row.UsesOf(chords.Count, shapes.Count);
        var elsewhere = entry.Uses.OnOtherLevels;
        Say(elsewhere == 0
            ? _localization.Format("ChordShapes_Status_Selected", selected)
            : _localization.Format("ChordShapes_Status_SelectedElsewhere", selected,
                _localization.Plural("ChordShapes_UseCountOne", "ChordShapes_UseCount", elsewhere)));
    }

    void IShapeRowOwner.NextUse(ShapePanelRowViewModel row)
    {
        var level = _state.ActiveLevel;
        if (level is null) return;

        var id = row.Template.Id;
        var beats = _editor.Beats;
        var uses = level.Chords.Where(c => c.TemplateId == id).Select(c => ((ITimedElement)c, c.GetTimeMs(beats)))
            .Concat(level.HandShapes.Where(h => h.TemplateId == id).Select(h => ((ITimedElement)h, h.GetTimeMs(beats))))
            .OrderBy(u => u.Item2)
            .ToList();
        if (uses.Count == 0)
        {
            Say(_localization["ChordShapes_Status_NoUseOnLevel"], StatusSeverity.Warning);
            return;
        }

        var now = _state.CurrentTimeMs;
        var index = uses.FindIndex(u => u.Item2 > now + 0.5);
        if (index < 0) index = 0;
        var (element, timeMs) = uses[index];

        var selection = _editor.Selection;
        selection.ClearSelections();
        if (element is Chord chord)
        {
            selection.SelectedChords.Add(chord);
            selection.SelectedChord = chord;
        }
        else if (element is HandShape shape)
        {
            selection.SelectedHandShapes.Add(shape);
        }

        _editor.ExecuteSeek(timeMs);
        Say(_localization.Format("ChordShapes_Status_NextUse", index + 1, uses.Count));
    }

    void IShapeRowOwner.Split(ShapePanelRowViewModel row)
    {
        if (_editor.ActiveArrangement is not { } arrangement) return;
        var selection = _editor.Selection;
        var plan = ChordShapeEdits.PlanSplit(arrangement, row.Template, selection.SelectedChords,
            selection.SelectedHandShapes, _editor);
        if (Commit(plan))
            Say(_localization.Format("ChordShapes_Status_Split", row.UsesOf(plan.ChordsMoved, plan.HandShapesMoved)));
    }

    /// <summary>Marks the cards whose shape the selection holds a chord or hand shape of: they offer Split off selection.</summary>
    private void UpdateSplitAvailability()
    {
        var selection = _editor.Selection;
        var ids = new HashSet<int>();
        foreach (var chord in selection.SelectedChords) ids.Add(chord.TemplateId);
        foreach (var shape in selection.SelectedHandShapes) ids.Add(shape.TemplateId);
        foreach (var row in _allRows) row.CanSplitSelection = ids.Contains(row.Template.Id);
    }

    void IShapeRowOwner.CommitName(ShapePanelRowViewModel row, string name)
    {
        if (_editor.ActiveArrangement is not { } arrangement) return;
        var unnamed = row.Template.Unnamed && name.Trim().Length == 0;
        var plan = ChordShapeEdits.PlanSetName(arrangement, row.Template, name, unnamed, _editor);
        if (plan.Refusal == ChordShapeEditRefusal.SameName) return;
        if (Commit(plan) && !row.Template.Unnamed && row.Template.Name.Length > 0)
            Say(_localization.Format("ChordShapes_Status_Renamed", row.Template.Name));
    }

    void IShapeRowOwner.SetNoName(ShapePanelRowViewModel row, bool value)
    {
        if (_editor.ActiveArrangement is not { } arrangement) return;
        // Taking No name back offers the suggested name while naming is on, like any new shape.
        var restored = value ? "" : NewChordNames.For(arrangement, row.Template.Frets);
        var plan = ChordShapeEdits.PlanSetName(arrangement, row.Template, restored, value, _editor);
        if (!Commit(plan)) return;
        Say(value
            ? _localization.Format("Shapes_Status_NoName", row.Title)
            : restored.Length > 0
                ? _localization.Format("ChordShapes_Status_Renamed", restored)
                : _localization.Format("Shapes_Status_NameFree", row.Title));
    }

    void IShapeRowOwner.Fix(ShapePanelRowViewModel row)
    {
        switch (row.Issue.Kind)
        {
            case ChordShapeIssueKind.SameFrets when row.Issue.Keeper is { } keeper:
                AskMerge(row.Template, keeper, delete: false);
                break;
            case ChordShapeIssueKind.NoFingering when _editor.ActiveArrangement is { } arrangement:
                if (Commit(ChordShapeEdits.PlanSuggestFingers(arrangement, row.Template, _editor)))
                    Say(_localization.Format("Shapes_Status_Fingered", row.Title));
                break;
        }
    }

    void IShapeRowOwner.BeginMerge(ShapePanelRowViewModel row)
    {
        if (ReferenceEquals(_mergeSource, row))
        {
            CancelPending();
            return;
        }

        if (!row.CanMerge) return;
        SetPending(null);
        _mergeSource = row;
        RefreshMergeState();
    }

    void IShapeRowOwner.MergeHere(ShapePanelRowViewModel row)
    {
        if (_mergeSource is { } source && row.CanMergeHere) AskMerge(source.Template, row.Template, delete: false);
    }

    void IShapeRowOwner.Delete(ShapePanelRowViewModel row)
    {
        if (_editor.ActiveArrangement is not { } arrangement) return;
        if (row.IsUnused)
        {
            if (Commit(ChordShapeEdits.PlanDelete(arrangement, row.Template, _editor)))
                Say(_localization.Format("ChordShapes_Status_Deleted", row.Title));
            return;
        }

        // A shape in use hands its uses to a copy with the same frets; with none, there is nowhere for them to go.
        if (_issues.MergeTargetFor(row.Template) is { } target) AskMerge(row.Template, target, delete: true);
        else Say(_localization["ChordShapes_Refused_InUse"], StatusSeverity.Warning);
    }

    // The dialog reads the arrangement itself and applies as one undo step; the history change refreshes the cards.
    private void CleanUp() => _ = _dialogs.ShowAsync<IShapeCleanupScreen, bool>();

    /// <summary>Puts a merge in the bar: what moves where, and what the kept shape keeps.</summary>
    private void AskMerge(ChordTemplate source, ChordTemplate target, bool delete)
    {
        if (_editor.ActiveArrangement is not { } arrangement) return;
        if (RowOf(source) is not { } from || RowOf(target) is null) return;
        _bulk.Drop();

        var count = arrangement.StringCount;
        var sourceName = ChordShapeNames.DisplayName(source, count);
        var targetName = ChordShapeNames.DisplayName(target, count);
        var (chords, shapes) = ChordShapeEdits.UsesOf(arrangement, source);
        var moved = from.UsesOf(chords.Count, shapes.Count);
        _mergeSource = from;
        _pending = new PendingVerb(source, target, delete);
        HasPending = true;
        IsPendingDestructive = delete;
        PendingText = delete
            ? _localization.Format("Shapes_Pending_Delete", sourceName, moved, targetName)
            : _localization.Format("Shapes_Pending_Merge", sourceName, targetName, moved);
        PendingConfirmLabel = delete
            ? _localization.Format("Shapes_Pending_DeleteConfirm",
                _localization.Plural("ChordShapes_UseCountOne", "ChordShapes_UseCount", chords.Count + shapes.Count))
            : _localization["Shapes_Pending_MergeConfirm"];
        RefreshMergeState();
    }

    private void ConfirmPending()
    {
        if (_bulk.Confirm()) return;
        if (_pending is not { } pending || _editor.ActiveArrangement is not { } arrangement) return;
        var count = arrangement.StringCount;
        var plan = ChordShapeEdits.PlanMerge(arrangement, pending.Source, pending.Target, _editor);
        var sourceName = ChordShapeNames.DisplayName(pending.Source, count);
        var targetName = ChordShapeNames.DisplayName(pending.Target, count);
        var moved = RowOf(pending.Source)?.UsesOf(plan.ChordsMoved, plan.HandShapesMoved) ?? "";
        CancelPending();
        if (!Commit(plan)) return;
        Say(pending.Delete
            ? _localization.Format("Shapes_Status_DeletedMoved", sourceName, moved, targetName)
            : _localization.Format("ChordShapes_Status_Merged", targetName, moved));
    }

    private void CancelPending()
    {
        _bulk.Drop();
        SetPending(null);
        _mergeSource = null;
        RefreshMergeState();
    }

    private void SetPending(PendingVerb? pending)
    {
        _pending = pending;
        HasPending = pending is not null;
        if (pending is not null) return;
        _bulk.Drop();
        PendingText = "";
        PendingConfirmLabel = "";
        IsPendingDestructive = false;
    }

    /// <summary>Drops a merge whose shapes an undo, a delete or another edit took out of the arrangement.</summary>
    private void DropStalePending(Arrangement arrangement)
    {
        var templates = arrangement.ChordTemplates;
        var stale = (_pending is { } p && !(ChordTemplateCollectionOps.ContainsInstance(templates, p.Source)
                                           && ChordTemplateCollectionOps.ContainsInstance(templates, p.Target)))
                    || (_mergeSource is { } s && !ChordTemplateCollectionOps.ContainsInstance(templates, s.Template));
        if (!stale) return;
        SetPending(null);
        _mergeSource = null;
    }

    /// <summary>Restates which card is the source, which is the target in the bar, and which can take the picked one.</summary>
    private void RefreshMergeState()
    {
        var source = _mergeSource;
        foreach (var row in _allRows)
        {
            row.IsMergeSource = ReferenceEquals(row, source);
            row.CanMergeHere = source is not null && _pending is null && !ReferenceEquals(row, source)
                               && ChordShapeCatalog.IsDuplicateOf(row.Template, source.Template);
            row.IsMergeTarget = (_pending is { } p && ReferenceEquals(p.Target, row.Template))
                                || (row.CanMergeHere && ReferenceEquals(row, _dragOver));
        }

        IsChoosingMergeTarget = source is not null && _pending is null;
        MergeHintText = IsChoosingMergeTarget && source is not null
            ? _localization.Format(_drag.IsDragging ? "Shapes_DragHint" : "Shapes_MergeHint", source.Title)
            : "";
    }

    private ShapePanelRowViewModel? RowOf(ChordTemplate template) =>
        _allRows.FirstOrDefault(r => ReferenceEquals(r.Template, template));

    /// <summary>One history node for a built plan, then the list re-read; a refusal prints its reason instead.</summary>
    private bool Commit(ChordShapeEditPlan plan)
    {
        if (plan.Action is { } action)
        {
            _history.Do(action);
            Refresh();
            return true;
        }

        Say(_localization[plan.Refusal switch
        {
            ChordShapeEditRefusal.DifferentFrets => "ChordShapes_Refused_DifferentFrets",
            ChordShapeEditRefusal.DifferentArpeggio => "ChordShapes_Refused_DifferentArpeggio",
            ChordShapeEditRefusal.SameTemplate => "ChordShapes_Refused_SameTemplate",
            ChordShapeEditRefusal.Unused => "ChordShapes_Refused_Unused",
            ChordShapeEditRefusal.NothingSelected => "ChordShapes_Refused_NothingSelected",
            ChordShapeEditRefusal.SelectionIsEveryUse => "ChordShapes_Refused_SelectionIsEveryUse",
            ChordShapeEditRefusal.InUse => "ChordShapes_Refused_InUse",
            ChordShapeEditRefusal.NothingUnused => "ChordShapes_Refused_NothingUnused",
            ChordShapeEditRefusal.Pinned => "Shapes_Refused_Pinned",
            ChordShapeEditRefusal.NoSuggestion => "Shapes_Refused_NoSuggestion",
            _ => "ChordShapes_Refused_NotInArrangement"
        }], StatusSeverity.Warning);
        return false;
    }
}
