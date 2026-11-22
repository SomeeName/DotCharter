using Avalonia.Input;
using DotCharter.UI.ViewModels.Panels;

namespace DotCharter.UI.Views.Panels;

/// <summary>
///     The bend graph's keyboard, so a point can be made, picked and moved without a mouse:
///     Enter or Insert adds a point after the selected one, Home / End pick the first / last
///     point, Page Up / Page Down step to the previous / next point, the arrows move the
///     selected point (or, with none, pick the first or last), Delete removes it and Esc
///     clears a selection (with none it reaches the window). Except the arrows, keys act only
///     with no modifier held, so Ctrl/Alt chords reach the window. Every edit is one undo entry.
///     Pure over the view model, so the default gate drives it without a window.
/// </summary>
public static class BendEditorKeys
{
    /// <summary>Applies <paramref name="key" />; true when the key was the graph's to handle.</summary>
    public static bool Handle(BendEditorViewModel vm, Key key, KeyModifiers modifiers)
    {
        var plain = modifiers == KeyModifiers.None;
        vm.BeginTransaction();
        try
        {
            return key switch
            {
                Key.Left or Key.Right or Key.Up or Key.Down => Arrow(vm, key, modifiers),
                _ when !plain => false,
                Key.Escape => Clear(vm),
                Key.Home => vm.SelectEnd(false),
                Key.End => vm.SelectEnd(true),
                Key.PageUp => vm.SelectAdjacent(-1),
                Key.PageDown => vm.SelectAdjacent(+1),
                Key.Enter or Key.Insert => vm.InsertPointAfterSelection() is not null,
                Key.Delete or Key.Back => Delete(vm),
                _ => false
            };
        }
        finally
        {
            vm.EndTransaction();
        }
    }

    private static bool Clear(BendEditorViewModel vm)
    {
        if (vm.SelectedPoint is null) return false;
        vm.ClearSelection();
        return true;
    }

    private static bool Delete(BendEditorViewModel vm)
    {
        if (vm.SelectedPoint is null) return false;
        vm.DeleteSelected();
        return true;
    }

    /// <summary>Ctrl nudges by an eighth step without snapping; Shift jumps three slots.</summary>
    private static bool Arrow(BendEditorViewModel vm, Key key, KeyModifiers modifiers)
    {
        if (vm.SelectedPoint is null)
            return key is Key.Left or Key.Right && vm.SelectEnd(key == Key.Left);

        var fine = modifiers.HasFlag(KeyModifiers.Control);
        var dStep = fine ? BendEditorViewModel.FineStepQuantum : BendEditorViewModel.StepQuantum;
        var dSlot = modifiers.HasFlag(KeyModifiers.Shift) ? 3 : 1;
        var (slots, steps) = key switch
        {
            Key.Left => (-dSlot, 0.0),
            Key.Right => (dSlot, 0.0),
            Key.Up => (0, dStep),
            _ => (0, -dStep)
        };
        vm.NudgeSelected(slots, steps, fine);
        return true;
    }
}
