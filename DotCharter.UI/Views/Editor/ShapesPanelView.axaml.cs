using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.VisualTree;
using DotCharter.Presentation.Contracts.Editor;

namespace DotCharter.UI.Views.Editor;

/// <summary>
///     The Shapes panel. Two things a card does that the contract has no word for: the last card
///     pressed or focused stays the CURRENT one (it keeps its actions showing after the pointer
///     leaves, like a selected row), and a name box applies its text when it loses focus.
/// </summary>
public partial class ShapesPanelView : UserControl
{
    private const string CardClass = "ShapeCard";
    private const string CurrentClass = "current";

    private Border? _current;

    public ShapesPanelView()
    {
        InitializeComponent();
        var rows = this.FindControl<ItemsControl>("ShapesRows")!;
        rows.AddHandler(GotFocusEvent, OnRowsGotFocus);
        rows.AddHandler(LostFocusEvent, OnRowsLostFocus);
        rows.AddHandler(PointerPressedEvent, OnRowsPointerPressed, RoutingStrategies.Tunnel, true);
    }

    private static Border? CardOf(object? source) =>
        source is Visual visual
            ? visual.GetSelfAndVisualAncestors().OfType<Border>().FirstOrDefault(b => b.Classes.Contains(CardClass))
            : null;

    private void MakeCurrent(Border card)
    {
        if (ReferenceEquals(card, _current)) return;
        _current?.Classes.Remove(CurrentClass);
        card.Classes.Add(CurrentClass);
        _current = card;
    }

    private void OnRowsGotFocus(object? sender, FocusChangedEventArgs e)
    {
        if (CardOf(e.Source) is { } card) MakeCurrent(card);
    }

    private void OnRowsPointerPressed(object? sender, PointerPressedEventArgs e)
    {
        if (CardOf(e.Source) is { } card) MakeCurrent(card);
    }

    private static void OnRowsLostFocus(object? sender, RoutedEventArgs e)
    {
        if (e.Source is TextBox { Name: "ShapeName", DataContext: IShapePanelRow row })
            row.CommitNameCommand.Execute(null);
    }
}
