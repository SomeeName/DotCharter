using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Interactivity;
using Avalonia.LogicalTree;

namespace DotCharter.UI.Views.Shell.Popovers;

/// <summary>
///     A popover row that closes the flyout it sits in. A <see cref="Button" /> reads its bound
///     <c>Command</c> only after <c>Click</c> returns, and by then a closed flyout has taken the
///     row out of the tree and cleared the binding; so the row runs its command first, marks the
///     click handled and only then hides the flyout.
/// </summary>
internal static class PopoverRowClick
{
    public static void RunThenClose(Control popover, object? sender, RoutedEventArgs e)
    {
        if (sender is Button { Command: { } command } row && command.CanExecute(row.CommandParameter))
        {
            command.Execute(row.CommandParameter);
            e.Handled = true;
        }

        if (popover.FindLogicalAncestorOfType<Popup>() is { PlacementTarget: Button { Flyout: { } flyout } }) flyout.Hide();
    }
}
