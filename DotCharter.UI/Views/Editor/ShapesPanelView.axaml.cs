using System.ComponentModel;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Threading;
using Avalonia.VisualTree;
using DotCharter.Presentation.Contracts.Editor;
using DotCharter.Presentation.Navigation;
using DotCharter.Tone.UI.Controls;
using DotCharter.UI.Controls;
using DotCharter.UI.Views.Shell;

namespace DotCharter.UI.Views.Editor;

/// <summary>
///     The Chord shapes tab (inspector design D). What the contract has no word for:
///     <list type="bullet">
///         <item>a list line pressed or focused becomes the screen's <see cref="IShapePanelScreen.CurrentRow" />,
///         and a press while the cards are paged one at a time (Tiny) brings the Shape page on screen;</item>
///         <item>a verb that waits for its yes brings the Checked page (its bar) on screen, and a merge target
///         being wanted brings the list on screen;</item>
///         <item>the name box applies its text when it loses focus, to the shape it was typed for.</item>
///     </list>
/// </summary>
public partial class ShapesPanelView : UserControl
{
    private const string CardClass = "ShapeCard";

    private readonly EffectiveVisibilityWatcher _visibility;
    private readonly ScreenViewPresence _presence = new();
    private readonly ShapeRowsList _rows;
    private readonly ScrollViewer _scroll;
    private readonly ColumnFlowPanel _cards;
    private IShapePanelScreen? _subscribed;
    private IShapePanelRow? _editingRow;

    public ShapesPanelView()
    {
        _visibility = new EffectiveVisibilityWatcher(this, _ => ReportVisibility());
        InitializeComponent();
        _rows = this.FindControl<ShapeRowsList>("ShapesRows")!;
        _scroll = this.FindControl<ScrollViewer>("ShapesScroll")!;
        _cards = this.FindControl<ColumnFlowPanel>("ShapesCards")!;
        _rows.AddHandler(GotFocusEvent, OnRowsGotFocus);
        _rows.AddHandler(PointerPressedEvent, OnRowsPointerPressed, RoutingStrategies.Tunnel, true);
        var name = this.FindControl<TextBox>("ShapeName")!;
        name.GotFocus += (_, _) => _editingRow = name.DataContext as IShapePanelRow;
        name.LostFocus += OnNameLostFocus;
    }

    internal ScrollViewer Scroll => _scroll;

    internal ShapeRowsList RowsList => _rows;

    internal ColumnFlowPanel Cards => _cards;

    protected override void OnAttachedToVisualTree(VisualTreeAttachmentEventArgs e)
    {
        base.OnAttachedToVisualTree(e);
        Subscribe(DataContext as IShapePanelScreen);
    }

    protected override void OnDetachedFromVisualTree(VisualTreeAttachmentEventArgs e)
    {
        Subscribe(null);
        base.OnDetachedFromVisualTree(e);
    }

    protected override void OnDataContextChanged(EventArgs e)
    {
        base.OnDataContextChanged(e);
        _editingRow = null;
        if (TopLevel.GetTopLevel(this) is not null) Subscribe(DataContext as IShapePanelScreen);
        ReportVisibility();
    }

    private void Subscribe(IShapePanelScreen? screen)
    {
        if (ReferenceEquals(screen, _subscribed)) return;
        _subscribed?.PropertyChanged -= OnScreenChanged;
        _subscribed = screen;
        _subscribed?.PropertyChanged += OnScreenChanged;
    }

    private void OnScreenChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (sender is not IShapePanelScreen screen) return;
        if (e.PropertyName == nameof(IShapePanelScreen.HasPending) && screen.HasPending) ShowPending();
        else if (e.PropertyName == nameof(IShapePanelScreen.IsChoosingMergeTarget) && screen.IsChoosingMergeTarget) ShowCard("ShapesListCard");
    }

    // The yes and the cancel close the Checked card, under the selection bar and the wrapped question, so a long
    // question (German) or a short card puts them below the fold: once the page is laid out, scroll to its end.
    private void ShowPending()
    {
        ShowCard("ShapesCheckedCard");
        var card = this.FindControl<ColumnFlowCard>("ShapesCheckedCard")!;
        Dispatcher.UIThread.Post(() => card.Scroller?.ScrollToEnd(), DispatcherPriority.Loaded);
    }

    private void ShowCard(string name)
    {
        if (this.FindControl<Control>(name) is { } card) _cards.ShowPage(card);
    }

    private void ReportVisibility() => _presence.Report(DataContext as IScreenLifecycle, _visibility.IsEffectivelyVisible);

    private static Border? CardOf(object? source) =>
        source is Visual visual
            ? visual.GetSelfAndVisualAncestors().OfType<Border>().FirstOrDefault(b => b.Classes.Contains(CardClass))
            : null;

    private void MakeCurrent(Border card)
    {
        if (DataContext is IShapePanelScreen screen && card.DataContext is IShapePanelRow row) screen.CurrentRow = row;
    }

    private void OnRowsGotFocus(object? sender, FocusChangedEventArgs e)
    {
        if (CardOf(e.Source) is { } card) MakeCurrent(card);
    }

    private void OnRowsPointerPressed(object? sender, PointerPressedEventArgs e)
    {
        if (CardOf(e.Source) is not { } card) return;
        MakeCurrent(card);
        var onTick = e.Source is Visual visual && visual.GetSelfAndVisualAncestors().OfType<CheckBox>().Any();
        if (!onTick && _cards.Tier == ColumnFlowTier.Tiny) ShowCard("ShapesShapeCard");
    }

    private void OnNameLostFocus(object? sender, RoutedEventArgs e)
    {
        var row = _editingRow ?? (sender as Control)?.DataContext as IShapePanelRow;
        _editingRow = null;
        row?.CommitNameCommand.Execute(null);
    }
}
