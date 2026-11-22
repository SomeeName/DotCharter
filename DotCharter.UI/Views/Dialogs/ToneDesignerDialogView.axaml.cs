using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Threading;
using Avalonia.VisualTree;
using DotCharter.Presentation.Contracts.Dialogs;

namespace DotCharter.UI.Views.Dialogs;

public partial class ToneDesignerDialogView : UserControl
{
    public ToneDesignerDialogView()
    {
        InitializeComponent();
        AddHandler(KeyDownEvent, OnUndoRedoKey);
        // A released pointer ends a drag or a pick (one undo step per gesture); leaving a field records a typed key.
        AddHandler(PointerReleasedEvent, (_, _) => (DataContext as IToneDesignerScreen)?.EndEditGesture(), RoutingStrategies.Tunnel | RoutingStrategies.Bubble, true);
        AddHandler(LostFocusEvent, (_, _) => (DataContext as IToneDesignerScreen)?.EndEditGesture(true), RoutingStrategies.Bubble, true);
    }

    /// <summary>
    ///     The main window's undo chord is blocked behind an open dialog, so the designer runs the
    ///     same history itself. A focused TextBox undoes its own text first and marks the key handled
    ///     so this only sees keys nothing else wanted.
    /// </summary>
    private void OnUndoRedoKey(object? sender, KeyEventArgs e)
    {
        if (DataContext is not IToneDesignerScreen screen || e.KeyModifiers.HasFlag(KeyModifiers.Alt)) return;
        // Delete on the project tone list removes the definition; in a TextBox / NumericUpDown it edits text
        // (those handle it first and mark it handled; the source check keeps every other control out of it).
        if (e.Key == Key.Delete && e.KeyModifiers == KeyModifiers.None && e.Source is Visual source
            && this.FindControl<ListBox>("ProjectToneList") is { } list && (source == list || list.IsVisualAncestorOf(source))
            && screen.DeleteToneCommand.CanExecute(null))
        {
            screen.DeleteToneCommand.Execute(null);
            e.Handled = true;
            RefocusList(list);
            return;
        }

        if (!e.KeyModifiers.HasFlag(KeyModifiers.Control)) return;
        var shift = e.KeyModifiers.HasFlag(KeyModifiers.Shift);
        var command = e.Key switch
        {
            Key.Z when !shift => screen.UndoCommand,
            Key.Z or Key.Y => screen.RedoCommand,
            _ => null
        };
        if (command is null || !command.CanExecute(null)) return;
        var inList = this.FindControl<ListBox>("ProjectToneList") is { } rows && e.Source is Visual from
                     && (from == rows || rows.IsVisualAncestorOf(from));
        command.Execute(null);
        e.Handled = true;
        if (inList) RefocusList(this.FindControl<ListBox>("ProjectToneList")!);
    }

    /// <summary>The focused row left the tree (a delete, or an undo that rebuilt the list); without focus the next key would never reach this view.</summary>
    private static void RefocusList(ListBox list) =>
        Dispatcher.UIThread.Post(() => list.ContainerFromIndex(Math.Max(list.SelectedIndex, 0))?.Focus(), DispatcherPriority.Loaded);
}
