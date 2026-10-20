using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.VisualTree;
using DotCharter.Presentation.Contracts.PlayMode;
using DotCharter.Tone.UI.Controls;
using DotCharter.UI.Input;

namespace DotCharter.UI.Views.PlayMode.Redesign;

/// <summary>
///     Play Mode &gt; Song Select, bound to <see cref="ISongSelectScreen" /> and nothing else.
///     The code here is the list's plumbing the contract cannot express, the same as the
///     Library's: the search box's keys (<see cref="SearchBoxKeys" />), covers streamed for
///     the rows on screen only and filter counts computed when a funnel opens (both through
///     the optional <see cref="ISongSelectViewHooks" />), and double-click to play.
/// </summary>
public partial class SongSelectScreenView : UserControl
{
    public SongSelectScreenView()
    {
        InitializeComponent();
        SongSelectRedesignList.ContainerPrepared += (_, e) =>
        {
            if (e.Container.DataContext is ISongRow row) Hooks?.RowShown(row);
        };
        SongSelectRedesignList.ContainerClearing += (_, e) =>
        {
            if (e.Container.DataContext is ISongRow row) Hooks?.RowHidden(row);
        };
        // Covers released while hidden come back for the rows on screen when the view shows again.
        _ = new EffectiveVisibilityWatcher(this, visible =>
        {
            if (!visible || Hooks is not { } hooks) return;
            foreach (var container in SongSelectRedesignList.GetRealizedContainers())
                if (container.DataContext is ISongRow row) hooks.RowShown(row);
        });
        // Tunnel: the search box usually holds focus, and a TextBox would otherwise keep Ctrl+F.
        AddHandler(KeyDownEvent, OnPageKeyDown, RoutingStrategies.Tunnel);
    }

    private ISongSelectViewHooks? Hooks => DataContext as ISongSelectViewHooks;

    /// <summary>Ctrl+F focuses the search; Esc empties it (an empty box lets Esc reach the shell); Down hands over to the list.</summary>
    private void OnPageKeyDown(object? sender, KeyEventArgs e)
    {
        if (e.Handled || !IsEffectivelyVisible) return;
        var box = SongSelectRedesignSearchBox;
        if (SearchBoxKeys.IsFocusSearchChord(e.Key, e.KeyModifiers))
        {
            box.Focus();
            box.SelectAll();
            e.Handled = true;
            return;
        }

        if (!ReferenceEquals(TopLevel.GetTopLevel(this)?.FocusManager?.GetFocusedElement(), box)) return;
        switch (SearchBoxKeys.Resolve(e.Key, e.KeyModifiers, !string.IsNullOrEmpty(box.Text)))
        {
            case SearchBoxKeyAction.ClearText:
                box.Text = "";
                e.Handled = true;
                break;
            case SearchBoxKeyAction.FocusList:
                var list = SongSelectRedesignList;
                var target = SearchBoxKeys.SelectionForFocusHandover(list.SelectedIndex, list.ItemCount);
                if (target < 0) return;
                list.SelectedIndex = target;
                if (list.ContainerFromIndex(target) is { } container) container.Focus();
                else list.Focus();
                e.Handled = true;
                break;
        }
    }

    private void OnFilterOpening(object? sender, EventArgs e) => Hooks?.FiltersOpening();

    /// <summary>Plays the row that was double-clicked, not whichever was selected before.</summary>
    private void OnRowsDoubleTapped(object? sender, TappedEventArgs e)
    {
        if (e.Source is not Control source || DataContext is not ISongSelectScreen screen) return;
        var container = source as ListBoxItem ?? source.GetSelfAndVisualAncestors().OfType<ListBoxItem>().FirstOrDefault();
        if (container?.DataContext is not ISongRow row) return;
        screen.SelectedRow = row;
        if (screen.PlayCommand.CanExecute(null)) screen.PlayCommand.Execute(null);
        e.Handled = true;
    }
}
