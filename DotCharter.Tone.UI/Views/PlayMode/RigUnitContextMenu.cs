using Avalonia.Controls;
using Avalonia.Input;
using DotCharter.Tone.UI.ViewModels.PlayMode;

namespace DotCharter.Tone.UI.Views.PlayMode;

/// <summary>
///     The right-click menu on a unit in the routing map: switch it on or off, copy,
///     cut, paste after it, duplicate, move it along the chain, undo / redo, remove —
///     each item showing its keyboard shortcut (the same ones the rig view binds).
///     Built per open over the rig's own commands, with the unit as the parameter, so
///     nothing in it can drift from what the shortcuts do.
/// </summary>
public static class RigUnitContextMenu
{
    /// <summary>The shortcuts, in one place: the view's key bindings and the menu's
    /// gesture hints both read these.</summary>
    public static readonly KeyGesture Undo = new(Key.Z, KeyModifiers.Control);
    public static readonly KeyGesture Redo = new(Key.Y, KeyModifiers.Control);
    public static readonly KeyGesture RedoAlt = new(Key.Z, KeyModifiers.Control | KeyModifiers.Shift);
    public static readonly KeyGesture Copy = new(Key.C, KeyModifiers.Control);
    public static readonly KeyGesture Cut = new(Key.X, KeyModifiers.Control);
    public static readonly KeyGesture Paste = new(Key.V, KeyModifiers.Control);
    public static readonly KeyGesture Duplicate = new(Key.D, KeyModifiers.Control);
    public static readonly KeyGesture Delete = new(Key.Delete);
    public static readonly KeyGesture Bypass = new(Key.B, KeyModifiers.Control);

    public static ContextMenu Build(PedalboardViewModel rig, PedalNodeViewModel node) => Build(rig, node, rig.Localization);

    private static ContextMenu Build(PedalboardViewModel rig, PedalNodeViewModel node, ILocalizationService loc)
    {
        var index = rig.Nodes.IndexOf(node);
        var items = new List<Control>
        {
            Item(loc[node.Bypassed ? "Rig_Editor_EnableUnit" : "Rig_Editor_Bypass"], node.ToggleBypassCommand, null, Bypass),
            new Separator(),
            Item(loc["Menu_Copy"], rig.CopyCommand, node, Copy, enabled: !node.IsJunction),
            Item(loc["Menu_Cut"], rig.CutCommand, node, Cut, enabled: !node.IsJunction),
            Item(loc["Menu_Paste"], rig.PasteCommand, node, Paste, enabled: rig.CanPaste),
            Item(loc["Rig_Duplicate"], rig.DuplicateCommand, node, Duplicate, enabled: !node.IsJunction),
            new Separator(),
            Item(loc["Rig_Ctx_MoveLeft"], rig.MoveUpCommand, node, null, enabled: index > 0),
            Item(loc["Rig_Ctx_MoveRight"], rig.MoveDownCommand, node, null, enabled: index >= 0 && index < rig.Nodes.Count - 1),
            new Separator(),
            Item(loc["Menu_Undo"], rig.UndoCommand, null, Undo, enabled: rig.CanUndo),
            Item(loc["Menu_Redo"], rig.RedoCommand, null, Redo, enabled: rig.CanRedo),
            new Separator(),
            Item(loc["Rig_RemoveUnit"], rig.RemoveCommand, node, Delete, enabled: !node.IsJunction)
        };
        return new ContextMenu { ItemsSource = items };
    }

    private static MenuItem Item(string header, System.Windows.Input.ICommand command, object? parameter,
        KeyGesture? gesture, bool enabled = true) => new()
        {
            Header = header,
            Command = command,
            CommandParameter = parameter,
            InputGesture = gesture,
            IsEnabled = enabled
        };
}
