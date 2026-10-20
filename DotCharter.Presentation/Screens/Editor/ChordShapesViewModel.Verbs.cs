using DotCharter.Core.Domain.Guitar;
using DotCharter.Core.Utilities;
using DotCharter.Services.Editing.ChordShapes;
using DotCharter.Services.Editing.Selection;
using DotCharter.Services.State;

namespace DotCharter.Presentation.Screens.Editor;

// The row verbs: place the shape, select every use, step to the next one, rename, merge, split.
public sealed partial class ChordShapesViewModel
{
    // The placement service writes its own outcome to the status rail.
    void IChordShapeRowOwner.Place(ChordShapeRowViewModel row) => _placement.PlaceShape(row.Template, _transport.GridDivisor);

    private void RepeatPreviousChord() => _placement.RepeatPreviousChord(_transport.GridDivisor);

    void IChordShapeRowOwner.SelectUses(ChordShapeRowViewModel row)
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

        var selected = row.UsesText(chords.Count, shapes.Count);
        var elsewhere = entry.Uses.OnOtherLevels;
        Say(elsewhere == 0
            ? _localization.Format("ChordShapes_Status_Selected", selected)
            : _localization.Format("ChordShapes_Status_SelectedElsewhere", selected,
                _localization.Plural("ChordShapes_UseCountOne", "ChordShapes_UseCount", elsewhere)));
    }

    void IChordShapeRowOwner.NextUse(ChordShapeRowViewModel row)
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

    void IChordShapeRowOwner.Rename(ChordShapeRowViewModel row, string name)
    {
        if (_editor.ActiveArrangement is not { } arrangement) return;
        var plan = ChordShapeEdits.PlanRename(arrangement, row.Template, name, _editor);
        if (plan.Refusal == ChordShapeEditRefusal.SameName) return;
        if (Commit(plan)) Say(_localization.Format("ChordShapes_Status_Renamed", row.Template.Name));
    }

    void IChordShapeRowOwner.Merge(ChordShapeRowViewModel row, ChordTemplate target)
    {
        if (_editor.ActiveArrangement is not { } arrangement) return;
        var plan = ChordShapeEdits.PlanMerge(arrangement, row.Template, target, _editor);
        if (!Commit(plan)) return;

        var name = ChordShapeNames.DisplayName(target, arrangement.StringCount);
        Say(_localization.Format("ChordShapes_Status_Merged", name, row.UsesText(plan.ChordsMoved, plan.HandShapesMoved)));
    }

    void IChordShapeRowOwner.Split(ChordShapeRowViewModel row)
    {
        if (_editor.ActiveArrangement is not { } arrangement) return;
        var selection = _editor.Selection;
        var plan = ChordShapeEdits.PlanSplit(arrangement, row.Template, selection.SelectedChords,
            selection.SelectedHandShapes, _editor);
        if (Commit(plan))
            Say(_localization.Format("ChordShapes_Status_Split", row.UsesText(plan.ChordsMoved, plan.HandShapesMoved)));
    }

    /// <summary>One history node for a built plan; a refusal prints its reason instead.</summary>
    private bool Commit(ChordShapeEditPlan plan)
    {
        if (plan.Action is { } action)
        {
            _history.Do(action);
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
            _ => "ChordShapes_Refused_NotInArrangement"
        }], StatusSeverity.Warning);
        return false;
    }
}
