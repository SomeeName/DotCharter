using System.Collections.ObjectModel;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Templates;
using Avalonia.Input;
using Avalonia.Threading;
using Avalonia.VisualTree;
using DotCharter.Presentation.Contracts.Editor;

namespace DotCharter.UI.Controls;

/// <summary>One row of cards. It stays in the list while the catalogue changes and only takes new cards.</summary>
public sealed class ShapeCardStrip(IReadOnlyList<IShapePanelRow> cards)
{
    public IReadOnlyList<IShapePanelRow> Cards { get; internal set; } = cards;
}

/// <summary>
///     Virtual rows of cards with the same adaptive columns as the Shapes panel, inside the pane's
///     own scroll viewer.
///     <para>
///         The list's items are STRIPS, one per row of cards, and they are updated in place: a new
///         catalogue hands each existing strip its new cards and only adds or removes strips at
///         the end. Replacing the items instead makes the virtualizing panel recycle and
///         re-realize every container in view, which cost more than the cards themselves.
///     </para>
///     <para>
///         It is a plain <see cref="ItemsControl" />, not a list box: a strip is not an item a user
///         selects, so there is no selection, no item chrome and no item for a screen reader, and
///         the list takes NO key (the editor's bare Home / End / arrow shortcuts pass through a
///         focused card). Tab alone gets help: see <see cref="OnKeyDown" />.
///     </para>
/// </summary>
public sealed class ShapeRowsList : ItemsControl
{
    public static readonly StyledProperty<IReadOnlyList<IShapePanelRow>?> SourceRowsProperty =
        AvaloniaProperty.Register<ShapeRowsList, IReadOnlyList<IShapePanelRow>?>(nameof(SourceRows));

    public static readonly StyledProperty<IDataTemplate?> CardTemplateProperty =
        AvaloniaProperty.Register<ShapeRowsList, IDataTemplate?>(nameof(CardTemplate));

    /// <summary>The narrowest a card is laid out: one more column per this much list width.</summary>
    internal const double MinCardWidth = 340;

    private readonly ObservableCollection<ShapeCardStrip> _strips = [];
    private IReadOnlyList<IShapePanelRow>? _lastRows;
    private int _columns;
    private bool _regroupPending;

    public ShapeRowsList() => ItemsSource = _strips;

    public IReadOnlyList<IShapePanelRow>? SourceRows
    {
        get => GetValue(SourceRowsProperty);
        set => SetValue(SourceRowsProperty, value);
    }

    /// <summary>What builds one card; every strip container gets it.</summary>
    public IDataTemplate? CardTemplate
    {
        get => GetValue(CardTemplateProperty);
        set => SetValue(CardTemplateProperty, value);
    }

    protected override Type StyleKeyOverride => typeof(ItemsControl);

    protected override Control CreateContainerForItemOverride(object? item, int index, object? recycleKey) =>
        new ShapeStripCards { CardTemplate = CardTemplate };

    protected override bool NeedsContainerOverride(object? item, int index, out object? recycleKey) =>
        NeedsContainer<ShapeStripCards>(item, out recycleKey);

    protected override void PrepareContainerForItemOverride(Control container, object? item, int index)
    {
        base.PrepareContainerForItemOverride(container, item, index);
        if (container is not ShapeStripCards strip) return;
        strip.Cards = (item as ShapeCardStrip)?.Cards;
        // Tab order: the panel reuses a container wherever it sits among its children, so the
        // strips' visual order is not their order in the list. Each strip is a tab group
        // ranked by its index, inside the list's own group (TabNavigation="Local" in the view).
        KeyboardNavigation.SetTabNavigation(strip, KeyboardNavigationMode.Local);
        KeyboardNavigation.SetTabIndex(strip, index);
    }

    protected override void ContainerIndexChangedOverride(Control container, int oldIndex, int newIndex)
    {
        base.ContainerIndexChangedOverride(container, oldIndex, newIndex);
        KeyboardNavigation.SetTabIndex(container, newIndex);
    }

    /// <summary>
    ///     The panel keeps the list's tab-once element realized: that is what keeps the focused
    ///     card alive while Tab or Page Up / Down scroll it out of view. It is let go when the
    ///     focus leaves the list, so a card the user has left recycles like any other.
    /// </summary>
    protected override void OnGotFocus(FocusChangedEventArgs e)
    {
        base.OnGotFocus(e);
        if ((e.Source as Visual)?.GetSelfAndVisualAncestors().OfType<ShapeStripCards>().FirstOrDefault() is { } strip)
            KeyboardNavigation.SetTabOnceActiveElement(this, strip);
    }

    protected override void ClearContainerForItemOverride(Control container)
    {
        base.ClearContainerForItemOverride(container);
        if (container is ShapeStripCards strip) strip.Cards = null;
    }

    protected override void OnPropertyChanged(AvaloniaPropertyChangedEventArgs change)
    {
        base.OnPropertyChanged(change);
        if (change.Property == SourceRowsProperty) Regroup();
        else if (change.Property == BoundsProperty) ScheduleRegroup();
        else if (change.Property == IsKeyboardFocusWithinProperty && !change.GetNewValue<bool>())
            KeyboardNavigation.SetTabOnceActiveElement(this, null);
    }

    /// <summary>
    ///     Deliberately without the base call: an items control over a virtualizing panel moves
    ///     the focus (and scrolls) on Home, End, Page Up / Down and the arrow keys, which here
    ///     are the editor's shortcuts.
    ///     <para>
    ///         Tab is the one key the list helps along. Only the strips in view exist, so the Tab
    ///         that would leave the last (or, with Shift, the first) card in view first realizes
    ///         the strip beyond it; the window's own Tab handling then lands on that card.
    ///     </para>
    /// </summary>
    protected override void OnKeyDown(KeyEventArgs e)
    {
        if (e.Handled || e.Key != Key.Tab || e.KeyModifiers is not (KeyModifiers.None or KeyModifiers.Shift)) return;
        if (e.Source is not InputElement from) return;
        var strip = from.GetSelfAndVisualAncestors().OfType<ShapeStripCards>().FirstOrDefault();
        var back = e.KeyModifiers == KeyModifiers.Shift;
        var beyond = strip is null ? -1 : IndexFromContainer(strip) + (back ? -1 : 1);
        if (strip is null || beyond < 0 || beyond >= _strips.Count || ContainerFromIndex(beyond) is not null) return;
        // Only the strip's outermost tab stop leaves it. (The focus manager's FindNextElement walks
        // another order than the window's Tab, so the stops are read here.)
        var stops = strip.GetVisualDescendants().OfType<InputElement>()
            .Where(i => i.Focusable && KeyboardNavigation.GetIsTabStop(i) && i.IsEffectivelyVisible && i.IsEffectivelyEnabled);
        if (!ReferenceEquals(back ? stops.FirstOrDefault() : stops.LastOrDefault(), from)) return;
        ScrollIntoView(beyond);
    }

    private void ScheduleRegroup()
    {
        if (_regroupPending) return;
        _regroupPending = true;
        Dispatcher.UIThread.Post(() =>
        {
            _regroupPending = false;
            Regroup();
        }, DispatcherPriority.Loaded);
    }

    private void Regroup()
    {
        var rows = SourceRows;
        var columns = AdaptiveColumnsPanel.ColumnsFor(Bounds.Width, MinCardWidth, 0);
        if (ReferenceEquals(rows, _lastRows) && columns == _columns) return;
        _lastRows = rows;
        _columns = columns;
        var groups = GroupRows(rows ?? [], columns);
        for (var i = 0; i < groups.Count; i++)
        {
            if (i < _strips.Count) SetCards(i, groups[i]);
            else _strips.Add(new ShapeCardStrip(groups[i]));
        }

        // A removed strip's container may wait in the list's recycle pool: it must not keep rows alive.
        while (_strips.Count > groups.Count)
        {
            SetCards(_strips.Count - 1, []);
            _strips.RemoveAt(_strips.Count - 1);
        }
    }

    private void SetCards(int index, IReadOnlyList<IShapePanelRow> cards)
    {
        var strip = _strips[index];
        if (strip.Cards.SequenceEqual(cards, ReferenceEqualityComparer.Instance)) return;
        strip.Cards = cards;
        if (ContainerFromIndex(index) is ShapeStripCards container) container.Cards = cards;
    }

    /// <summary>The rows cut into strips of <paramref name="columns" /> cards, the last one as short as it comes out.</summary>
    internal static IReadOnlyList<IReadOnlyList<IShapePanelRow>> GroupRows(IReadOnlyList<IShapePanelRow> rows, int columns)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(columns, 1);
        var strips = new List<IReadOnlyList<IShapePanelRow>>();
        for (var start = 0; start < rows.Count; start += columns)
        {
            var cards = new IShapePanelRow[Math.Min(columns, rows.Count - start)];
            for (var i = 0; i < cards.Length; i++) cards[i] = rows[start + i];
            strips.Add(cards);
        }

        return strips;
    }
}

/// <summary>
///     One strip's cards. A strip keeps its card visuals: each slot only takes the next row as its
///     data context, so a replaced catalogue or a scroll step (the list recycles strip
///     containers) rebinds the cards in view instead of building them again. A strip waiting in
///     the list's recycle pool has no cards and its slots hold no row. It is the list's item
///     container: bare, with no margin, colour, focus or automation role of its own.
/// </summary>
public sealed class ShapeStripCards : Decorator
{
    public static readonly StyledProperty<IReadOnlyList<IShapePanelRow>?> CardsProperty =
        AvaloniaProperty.Register<ShapeStripCards, IReadOnlyList<IShapePanelRow>?>(nameof(Cards));

    public static readonly StyledProperty<IDataTemplate?> CardTemplateProperty =
        AvaloniaProperty.Register<ShapeStripCards, IDataTemplate?>(nameof(CardTemplate));

    private readonly AdaptiveColumnsPanel _slots = new() { MinColumnWidth = ShapeRowsList.MinCardWidth };

    public ShapeStripCards() => Child = _slots;

    public IReadOnlyList<IShapePanelRow>? Cards
    {
        get => GetValue(CardsProperty);
        set => SetValue(CardsProperty, value);
    }

    public IDataTemplate? CardTemplate
    {
        get => GetValue(CardTemplateProperty);
        set => SetValue(CardTemplateProperty, value);
    }

    /// <summary>How many card visuals this strip has built since it was created: the test hook.</summary>
    internal int CardsBuilt { get; private set; }

    protected override void OnPropertyChanged(AvaloniaPropertyChangedEventArgs change)
    {
        base.OnPropertyChanged(change);
        if (change.Property == CardTemplateProperty) _slots.Children.Clear();
        if (change.Property == CardsProperty || change.Property == CardTemplateProperty) Rebind();
    }

    private void Rebind()
    {
        var slots = _slots.Children;
        if (Cards is not { } cards || CardTemplate is not { } template)
        {
            foreach (var slot in slots) slot.DataContext = null;
            return;
        }

        if (slots.Count > cards.Count) slots.RemoveRange(cards.Count, slots.Count - cards.Count);
        for (var i = 0; i < cards.Count; i++)
        {
            if (i == slots.Count)
            {
                slots.Add(template.Build(cards[i]) ?? throw new InvalidOperationException("The Shapes card template built nothing."));
                CardsBuilt++;
            }

            slots[i].DataContext = cards[i];
        }
    }
}
