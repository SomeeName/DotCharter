using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.VisualTree;
using DotCharter.Presentation.Contracts.Library;
using DotCharter.Tone.UI.Controls;
using DotCharter.UI.Input;
using DotCharter.UI.Views.Shell;

namespace DotCharter.UI.Views.Library;

/// <summary>
///     The Library, bound to <see cref="ILibraryScreen" /> and nothing else. The code here is the
///     list's plumbing the contract cannot express: the search box's keys (shared with Song Select
///     through <see cref="SearchBoxKeys" />), covers streamed for the rows on screen only, filter
///     counts computed when a funnel opens, and double-click to load.
/// </summary>
public partial class LibraryView : UserControl, IPrewarmableSurfaces
{
    public LibraryView()
    {
        InitializeComponent();
        LibraryRows.ContainerPrepared += (_, e) => Hooks?.RowShown((ILibraryRow)e.Container.DataContext!);
        LibraryRows.ContainerClearing += (_, e) =>
        {
            if (e.Container.DataContext is ILibraryRow row) Hooks?.RowHidden(row);
        };
        // Covers released while hidden come back for the rows on screen when the view shows again.
        _ = new EffectiveVisibilityWatcher(this, visible =>
        {
            if (!visible || Hooks is not { } hooks) return;
            foreach (var container in LibraryRows.GetRealizedContainers())
                if (container.DataContext is ILibraryRow row) hooks.RowShown(row);
        });
        // A search keystroke's cost, logged like a tab switch.
        _ = new SearchSettleTiming("Library", LibrarySearchBox, LibraryRows);
        // Tunnel: the search box usually holds focus, and a TextBox would otherwise keep Ctrl+F.
        AddHandler(KeyDownEvent, OnPageKeyDown, RoutingStrategies.Tunnel);
    }

    private ILibraryViewHooks? Hooks => DataContext as ILibraryViewHooks;

    /// <summary>The states the body swaps between, realized after the first frame so the first switch is cheap.</summary>
    public IReadOnlyList<Control> HiddenSurfaces() =>
    [
        .. new[] { "LibraryDetailBody", "LibraryFilterChips", "LibraryNoMatch", "LibraryFirstRun" }
            .Select(name => this.FindControl<Control>(name)).OfType<Control>()
    ];

    /// <summary>Ctrl+F focuses the search; Esc empties it (an empty box lets Esc reach the shell); Down hands over to the list.</summary>
    private void OnPageKeyDown(object? sender, KeyEventArgs e)
    {
        if (e.Handled || !IsEffectivelyVisible) return;
        if (SearchBoxKeys.IsFocusSearchChord(e.Key, e.KeyModifiers))
        {
            LibrarySearchBox.Focus();
            LibrarySearchBox.SelectAll();
            e.Handled = true;
            return;
        }

        if (TopLevel.GetTopLevel(this)?.FocusManager?.GetFocusedElement() is not TextBox box || !ReferenceEquals(box, LibrarySearchBox)) return;
        switch (SearchBoxKeys.Resolve(e.Key, e.KeyModifiers, !string.IsNullOrEmpty(box.Text)))
        {
            case SearchBoxKeyAction.ClearText:
                box.Text = "";
                e.Handled = true;
                break;
            case SearchBoxKeyAction.FocusList:
                var target = SearchBoxKeys.SelectionForFocusHandover(LibraryRows.SelectedIndex, LibraryRows.ItemCount);
                if (target < 0) return;
                LibraryRows.SelectedIndex = target;
                if (LibraryRows.ContainerFromIndex(target) is { } container) container.Focus();
                else LibraryRows.Focus();
                e.Handled = true;
                break;
        }
    }

    private void OnFilterOpening(object? sender, EventArgs e) => Hooks?.FiltersOpening();

    /// <summary>Loads the row that was double-clicked, not whichever was selected before.</summary>
    private void OnRowsDoubleTapped(object? sender, TappedEventArgs e)
    {
        if (e.Source is not Control source || DataContext is not ILibraryScreen screen) return;
        var container = source as ListBoxItem ?? source.GetSelfAndVisualAncestors().OfType<ListBoxItem>().FirstOrDefault();
        if (container?.DataContext is not ILibraryRow row) return;
        screen.SelectedRow = row;
        if (screen.LoadCommand.CanExecute(null)) screen.LoadCommand.Execute(null);
        e.Handled = true;
    }
}
