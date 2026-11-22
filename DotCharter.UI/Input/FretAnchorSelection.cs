using Avalonia.Input;

namespace DotCharter.UI.Input;

/// <summary>How an anchor press changes the content selection.</summary>
public enum FretAnchorSelectionChange
{
    None,
    Replace,
    Keep,
    Toggle,
    Extend
}

/// <summary>Shared anchor-click selection rules, independent of the shell and pointer event objects.</summary>
public static class FretAnchorSelection
{
    /// <summary>Only a plain click outside the selection clears the other note-lane collections.</summary>
    public static FretAnchorSelectionChange Resolve(bool hasHit, KeyModifiers modifiers, bool alreadySelected)
    {
        if (!hasHit) return FretAnchorSelectionChange.None;
        if (modifiers.HasFlag(KeyModifiers.Shift)) return FretAnchorSelectionChange.Extend;
        if (modifiers.HasFlag(KeyModifiers.Control)) return FretAnchorSelectionChange.Toggle;
        return alreadySelected ? FretAnchorSelectionChange.Keep : FretAnchorSelectionChange.Replace;
    }

    /// <summary>Applies the decision with bulk selection notifications and the highway's selection log.</summary>
    internal static void Apply(TrackEditorContext track, FretAnchor anchor, KeyModifiers modifiers,
        FractionalPosition? rangeStart, IAppLogger logger)
    {
        var selection = track.Selection;
        var alreadySelected = selection.SelectedAnchors.Contains(anchor);
        selection.ClearPreviews();
        var change = Resolve(true, modifiers, alreadySelected);
        if (change == FretAnchorSelectionChange.Keep)
        {
            SelectionBulkOps.ReplaceWith(selection.SelectedEventPoints, []);
            SelectionBulkOps.ReplaceWith(selection.SelectedTones, []);
        }
        switch (change)
        {
            case FretAnchorSelectionChange.Replace:
                SelectionBulkOps.ReplaceWith(selection.SelectedNotes, []);
                SelectionBulkOps.ReplaceWith(selection.SelectedChords, []);
                SelectionBulkOps.ReplaceWith(selection.SelectedHandShapes, []);
                SelectionBulkOps.ReplaceWith(selection.SelectedEventPoints, []);
                SelectionBulkOps.ReplaceWith(selection.SelectedTones, []);
                selection.SelectedLyric = null;
                SelectionBulkOps.ReplaceWith(selection.SelectedAnchors, [anchor]);
                break;
            case FretAnchorSelectionChange.Toggle:
                if (alreadySelected)
                {
                    SelectionBulkOps.ReplaceWith(selection.SelectedAnchors,
                        selection.SelectedAnchors.Where(a => !ReferenceEquals(a, anchor)).ToArray());
                }
                else
                {
                    SelectionBulkOps.AddRange(selection.SelectedAnchors, [anchor]);
                }
                break;
            case FretAnchorSelectionChange.Extend:
                if (rangeStart is { } start)
                {
                    var min = FractionalPosition.Min(start, anchor.Position);
                    var max = FractionalPosition.Max(start, anchor.Position);
                    SelectionBulkOps.AddRange(selection.SelectedAnchors,
                        track.Anchors.Where(a => a.Position >= min && a.Position <= max));
                }
                else
                {
                    SelectionBulkOps.AddRange(selection.SelectedAnchors, [anchor]);
                }
                break;
        }

        if (logger.IsEnabled(AppLogCategory.Input))
        {
            logger.Info(AppLogCategory.Input,
                $"Highway selection updated. Selected notes={selection.SelectedNotes.Count}, chords={selection.SelectedChords.Count}, handshapes={selection.SelectedHandShapes.Count}, anchors={selection.SelectedAnchors.Count}.");
        }
    }
}
