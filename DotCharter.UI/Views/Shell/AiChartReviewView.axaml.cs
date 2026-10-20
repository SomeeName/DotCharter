using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.VisualTree;
using DotCharter.UI.ViewModels.Shell;

namespace DotCharter.UI.Views.Shell;

/// <summary>
/// The AI chart pass review card, over the timeline's top-right corner. Its
///     card is hidden until a review is held, so the shell prewarms it like any other
///     gated surface (<see cref="IPrewarmableSurfaces" />).
///     <para>
/// The keyboard, while focus is anywhere on the card: ↑ / ↓ move the current
///         row and go to it, A accepts it, R rejects it, Enter on a row applies. The keys are
///         claimed in the TUNNEL so a focused row button does not take Enter as its own click
///         and the editor's keybinds (the window's bubble fallback) never see them.
///     </para>
/// </summary>
public partial class AiChartReviewView : UserControl, IPrewarmableSurfaces
{
    private readonly ItemsControl _rows;

    /// <summary>Creates the card.</summary>
    public AiChartReviewView()
    {
        InitializeComponent();
        _rows = this.FindControl<ItemsControl>("AiChartReviewRows")!;
        this.FindControl<Border>("AiChartReviewCard")!.AddHandler(KeyDownEvent, OnCardKeyDown, RoutingStrategies.Tunnel);
        // Tabbing (or clicking) into a row makes it the keyboard's current row — without a seek.
        _rows.AddHandler(GotFocusEvent, OnRowFocused, RoutingStrategies.Bubble);
    }

    /// <inheritdoc />
    public IReadOnlyList<Control> HiddenSurfaces() =>
        this.FindControl<Control>("AiChartReviewCard") is { IsVisible: false } card ? [card] : [];

    /// <summary>Routes one key press the way the card's tooltips promise; returns whether it was claimed.
    /// Internal so the headless tests drive the same path a real key takes.</summary>
    internal bool HandleKey(Key key, KeyModifiers modifiers, object? source)
    {
        if (modifiers != KeyModifiers.None || DataContext is not AiChartReviewViewModel vm || !vm.IsOpen) return false;
        switch (key)
        {
            case Key.Down or Key.Up:
                if (vm.Step(key == Key.Down ? 1 : -1) is { } row) FocusRow(row);
                return true;
            case Key.A:
                vm.AcceptCurrent();
                return true;
            case Key.R:
                vm.RejectCurrent();
                return true;
            case Key.Enter when IsInRows(source):
                if (vm.ApplyCommand.CanExecute(null)) vm.ApplyCommand.Execute(null);
                return true;
            default:
                return false;
        }
    }

    private void OnCardKeyDown(object? sender, KeyEventArgs e)
    {
        if (!e.Handled && HandleKey(e.Key, e.KeyModifiers, e.Source)) e.Handled = true;
    }

    private void OnRowFocused(object? sender, FocusChangedEventArgs e)
    {
        if (DataContext is AiChartReviewViewModel vm && e.Source is Control { DataContext: AiChartReviewRowViewModel row })
            vm.CurrentRow = row;
    }

    private bool IsInRows(object? source) => source is Visual visual && (visual == _rows || _rows.IsVisualAncestorOf(visual));

    /// <summary>Puts focus on the row's Go to and scrolls it into view, so the next key lands on the card.</summary>
    private void FocusRow(AiChartReviewRowViewModel row)
    {
        if (_rows.ContainerFromItem(row) is not Control container) return;
        container.BringIntoView();
        container.GetVisualDescendants().OfType<Button>().FirstOrDefault(b => b.Name == "AiChartReviewRowGo")
            ?.Focus(NavigationMethod.Directional);
    }
}
