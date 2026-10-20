using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.VisualTree;
using DotCharter.UI.ViewModels.Shell;

namespace DotCharter.UI.Views.Shell;

/// <summary>
/// Chart from audio's merge card, over the timeline's top-right corner beside the AI
///     review's. Hidden until a draft is held, so the shell prewarms it like any other gated surface
///     (<see cref="IPrewarmableSurfaces" />). While focus is on the card, ↑ / ↓ move the current row
///     and go to it, A merges it and R drops it — claimed in the TUNNEL so the editor's keybinds
///     never see them.
/// </summary>
public partial class DraftMergeReviewView : UserControl, IPrewarmableSurfaces
{
    private readonly ItemsControl _rows;
    private readonly Border _card;

    /// <summary>Creates the card.</summary>
    public DraftMergeReviewView()
    {
        InitializeComponent();
        _rows = this.FindControl<ItemsControl>("DraftMergeRows")!;
        _card = this.FindControl<Border>("DraftMergeCard")!;
        _card.AddHandler(KeyDownEvent, OnCardKeyDown, RoutingStrategies.Tunnel);
        _rows.AddHandler(GotFocusEvent, OnRowFocused, RoutingStrategies.Bubble);
    }

    /// <inheritdoc />
    public IReadOnlyList<Control> HiddenSurfaces() =>
        this.FindControl<Control>("DraftMergeCard") is { IsVisible: false } card ? [card] : [];

    /// <summary>Routes one key press; returns whether it was claimed. Internal so the headless tests drive
    /// the same path a real key takes.</summary>
    internal bool HandleKey(Key key, KeyModifiers modifiers)
    {
        if (modifiers != KeyModifiers.None || DataContext is not DraftMergeReviewViewModel vm || !vm.IsOpen) return false;
        switch (key)
        {
            case Key.Down or Key.Up:
                if (vm.Step(key == Key.Down ? 1 : -1) is { } row) FocusRow(row);
                return true;
            case Key.A:
                vm.MergeCurrent();
                KeepFocus();
                return true;
            case Key.R:
                vm.RejectCurrent();
                KeepFocus();
                return true;
            default:
                return false;
        }
    }

    private void OnCardKeyDown(object? sender, KeyEventArgs e)
    {
        if (!e.Handled && HandleKey(e.Key, e.KeyModifiers)) e.Handled = true;
    }

    private void OnRowFocused(object? sender, FocusChangedEventArgs e)
    {
        if (DataContext is DraftMergeReviewViewModel vm && e.Source is Control { DataContext: DraftMergeRowViewModel row })
            vm.CurrentRow = row;
    }

    /// <summary>The decided row's Go button left the tree with its row; the card takes focus so ↑ / ↓ /
    /// A / R keep landing here instead of falling to the window.</summary>
    private void KeepFocus()
    {
        if (_card.IsVisible && !_card.IsKeyboardFocusWithin) _card.Focus(NavigationMethod.Directional);
    }

    private void FocusRow(DraftMergeRowViewModel row)
    {
        if (_rows.ContainerFromItem(row) is not Control container) return;
        container.BringIntoView();
        container.GetVisualDescendants().OfType<Button>().FirstOrDefault(b => b.Name == "DraftMergeRowGo")
            ?.Focus(NavigationMethod.Directional);
    }
}
