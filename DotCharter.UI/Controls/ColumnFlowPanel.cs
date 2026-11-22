using Avalonia;
using Avalonia.Controls;
using Avalonia.Automation;
using Avalonia.Controls.Primitives;
using Avalonia.Input;
using Avalonia.Layout;
using Avalonia.Markup.Xaml.MarkupExtensions;
using DotCharter.Tone.UI.Localization;

namespace DotCharter.UI.Controls;

/// <summary>
/// The card body of an Inspector tab in design D: its VISIBLE children are cards, laid in one to four columns by
///     the body's width tier and height (<see cref="ColumnFlowTiers" />, <see cref="ColumnFlowPages" />). The first card
///     keeps the left column; cards that share a column one at a time get a <see cref="CardPager" /> over it, captioned
///     by each card's <see cref="TitleProperty" />.
///     <list type="bullet">
///         <item>A resize RE-PLACES the same children, it never re-parents or rebuilds one, and a card whose binding
///         hides it (another section's) simply is not a card. So a tab whose sections are IsVisible-gated cards can
///         host every section in ONE panel, and a section switch costs one measure.</item>
///         <item>A paged-away card stays in the tree and is still measured (so its first page switch lays out
///         nothing new), but is arranged outside the clipped panel and taken out of hit testing and keyboard
///         navigation. The panel never writes a card's <c>IsVisible</c>: that belongs to the card's own binding.</item>
///         <item>Cards stacked in one column share its height equally; each card scrolls its own content
///         (<see cref="ColumnFlowCard" />).</item>
///         <item>At the <see cref="ColumnFlowTier.Tiny" /> tier the pager row stands in for the card's title row (its
///         lit button names the card), so it also carries the card's "More below" cue at its right end and the card's
///         scroller keeps the card's whole height (<see cref="ColumnFlowCard.CueHosted" />).</item>
///     </list>
/// </summary>
public sealed class ColumnFlowPanel : Panel
{
    /// <summary>The caption of a card's page button.</summary>
    public static readonly AttachedProperty<string?> TitleProperty =
        AvaloniaProperty.RegisterAttached<ColumnFlowPanel, Control, string?>("Title");

    public static readonly StyledProperty<double> SpacingProperty =
        AvaloniaProperty.Register<ColumnFlowPanel, double>(nameof(Spacing), 6);

    /// <summary>The height of the page buttons' row (px), the design's 28 px target.</summary>
    public const double PagerHeight = 28;

    private const double PagerGap = 4;
    private const double OffStage = -100_000;

    private readonly CardPager _pager = new();
    private readonly Border _pagerCue = PagerCue();
    private readonly List<Control> _cards = [];
    private readonly HashSet<Control> _offStage = [];
    private ColumnFlowPlan _plan = ColumnFlowPlan.Empty;
    private bool _stageMoved;
    private bool _arranged;
    private Control? _current;
    private ColumnFlowCard? _cueCard;
    private bool _cueFits;

    static ColumnFlowPanel() => AffectsMeasure<ColumnFlowPanel>(SpacingProperty);

    public ColumnFlowPanel()
    {
        ClipToBounds = true;
        _pager.PagePicked += ShowPage;
        Children.Add(_pager);
        Children.Add(_pagerCue);
    }

    public double Spacing
    {
        get => GetValue(SpacingProperty);
        set => SetValue(SpacingProperty, value);
    }

    /// <summary>The tier and plan of the last measure — the test hook.</summary>
    internal ColumnFlowTier Tier { get; private set; } = ColumnFlowTier.Small;

    internal ColumnFlowPlan Plan => _plan;

    /// <summary>The page buttons (shown only while cards share a column).</summary>
    public CardPager Pager => _pager;

    /// <summary>The pager row's "More below" cue (the Tiny tier) — the test hook.</summary>
    internal Control PagerCueControl => _pagerCue;

    /// <summary>
    ///     A card came on screen or left it (a page pick, a tier change on resize): raised from the arrange pass that moved
    ///     it, so a host follows <see cref="ShownCards" /> without hooking every layout pass of the window.
    /// </summary>
    public event Action? ShownCardsChanged;

    /// <summary>The cards on screen now, column by column.</summary>
    public IReadOnlyList<Control> ShownCards => [.. _plan.Columns.SelectMany(c => c).Where(i => i < _cards.Count).Select(i => _cards[i])];

    public static string? GetTitle(Control control) => control.GetValue(TitleProperty);

    public static void SetTitle(Control control, string? value) => control.SetValue(TitleProperty, value);

    /// <summary>Brings a paged card on screen — what its page button does.</summary>
    public void ShowPage(Control card)
    {
        _current = card;
        InvalidateMeasure();
    }

    protected override void ChildrenChanged(object? sender, System.Collections.Specialized.NotifyCollectionChangedEventArgs e)
    {
        base.ChildrenChanged(sender, e);
        foreach (var child in e.NewItems?.OfType<Control>() ?? [])
        {
            if (IsChrome(child)) continue;
            child.PropertyChanged += OnCardPropertyChanged;
            if (child is ColumnFlowCard card) card.MoreBelowChanged += OnMoreBelowChanged;
        }

        foreach (var child in e.OldItems?.OfType<Control>() ?? [])
        {
            child.PropertyChanged -= OnCardPropertyChanged;
            if (child is ColumnFlowCard card) card.MoreBelowChanged -= OnMoreBelowChanged;
            _offStage.Remove(child);
        }
    }

    // A card that shows or hides changes the plan; the panel is not told otherwise (a hidden child does not invalidate it).
    private void OnCardPropertyChanged(object? sender, AvaloniaPropertyChangedEventArgs e)
    {
        if (e.Property == IsVisibleProperty || e.Property == TitleProperty) InvalidateMeasure();
    }

    protected override Size MeasureOverride(Size availableSize)
    {
        _cards.Clear();
        _cards.AddRange(Children.Where(c => c.IsVisible && !IsChrome(c)));
        Tier = ColumnFlowTiers.ForWidth(availableSize.Width);
        _plan = ColumnFlowPages.Plan(Tier, ColumnFlowTiers.IsShort(availableSize.Height), _cards.Count, _current is null ? -1 : _cards.IndexOf(_current));

        var paged = _plan.Pages.Select(i => _cards[i]).ToList();
        _pager.IsVisible = paged.Count > 0;
        _pager.Show(paged, _plan.Current >= 0 ? _cards[_plan.Current] : null);

        var slots = Slots(availableSize);
        var width = 0.0;
        var height = 0.0;
        foreach (var (column, slot) in slots.Columns)
        {
            var columnHeight = 0.0;
            foreach (var card in column)
            {
                card.Measure(new Size(slot.Width, slot.Height));
                columnHeight += card.DesiredSize.Height + Spacing;
            }

            if (ReferenceEquals(column, slots.Paged))
            {
                _pager.Measure(new Size(slot.Width, PagerHeight));
                _pagerCue.Measure(new Size(slot.Width, PagerHeight));
                // the cue's own width even while it is hidden (a hidden control measures to nothing)
                _pagerCue.Child!.Measure(new Size(double.PositiveInfinity, PagerHeight));
                _cueFits = _pager.DesiredSize.Width + (2 * PagerGap) + _pagerCue.Child.DesiredSize.Width +
                    _pagerCue.Padding.Left + _pagerCue.Padding.Right <= slot.Width;
                columnHeight += PagerHeight + PagerGap;
            }

            width += slot.Width + Spacing;
            height = Math.Max(height, columnHeight - Spacing);
        }

        // the paged-away cards: measured at the paged slot, so their first page switch lays out nothing new.
        foreach (var card in paged.Except(ShownCards))
            card.Measure(slots.PagedSlot);

        var finiteWidth = double.IsInfinity(availableSize.Width) ? Math.Max(0, width - Spacing) : availableSize.Width;
        return new Size(finiteWidth, Math.Max(0, height));
    }

    protected override Size ArrangeOverride(Size finalSize)
    {
        var slots = Slots(finalSize);
        var x = 0.0;
        var shown = new HashSet<Control>();
        _stageMoved = false;
        foreach (var (column, slot) in slots.Columns)
        {
            var y = 0.0;
            if (ReferenceEquals(column, slots.Paged))
            {
                _pager.Arrange(new Rect(x, 0, slot.Width, PagerHeight));
                var cue = _pagerCue.DesiredSize;
                _pagerCue.Arrange(new Rect(x + Math.Max(0, slot.Width - cue.Width), Math.Max(0, (PagerHeight - cue.Height) / 2),
                    cue.Width, cue.Height));
                y = PagerHeight + PagerGap;
            }

            foreach (var card in column)
            {
                var cardHeight = double.IsInfinity(slot.Height) ? card.DesiredSize.Height : slot.Height;
                card.Arrange(new Rect(x, y, slot.Width, cardHeight));
                SetOnStage(card, true);
                shown.Add(card);
                y += cardHeight + Spacing;
            }

            x += slot.Width + Spacing;
        }

        if (slots.Paged is null)
        {
            _pager.Arrange(new Rect(OffStage, 0, 0, 0));
            _pagerCue.Arrange(new Rect(OffStage, 0, 0, 0));
        }

        // Where the page buttons leave no room for it, the card keeps its own cue.
        HostCue(Tier == ColumnFlowTier.Tiny && _cueFits ? slots.Paged?.OfType<ColumnFlowCard>().FirstOrDefault() : null);
        foreach (var card in _cards.Where(c => !shown.Contains(c)))
        {
            card.Arrange(new Rect(OffStage, 0, card.DesiredSize.Width, card.DesiredSize.Height));
            SetOnStage(card, false);
        }

        if (_stageMoved || !_arranged)
        {
            _arranged = true;
            ShownCardsChanged?.Invoke();
        }

        return finalSize;
    }

    /// <summary>Each column's cards and the size each of them gets: the column's share of the width, and an equal share
    /// of the height (less the pager row over the paged column); an unbounded height gives each card what it asks.</summary>
    private SlotPlan Slots(Size size)
    {
        var columns = _plan.Columns.Select(c => (IReadOnlyList<Control>)[.. c.Where(i => i < _cards.Count).Select(i => _cards[i])]).ToList();
        var weights = _plan.Weights.Sum();
        var room = Math.Max(0, size.Width - (Spacing * Math.Max(0, columns.Count - 1)));
        var result = new List<(IReadOnlyList<Control>, Size)>();
        IReadOnlyList<Control>? paged = null;
        var pagedSlot = new Size(room, size.Height);
        for (var i = 0; i < columns.Count; i++)
        {
            var width = double.IsInfinity(room) ? double.PositiveInfinity : Math.Floor(room * _plan.Weights[i] / weights);
            var height = size.Height;
            if (i == _plan.PagedColumn)
            {
                paged = columns[i];
                height = Math.Max(0, height - PagerHeight - PagerGap);
                pagedSlot = new Size(width, height);
            }

            var count = Math.Max(1, columns[i].Count);
            if (!double.IsInfinity(height)) height = Math.Max(0, (height - (Spacing * (count - 1))) / count);
            result.Add((columns[i], new Size(width, height)));
        }

        return new SlotPlan(result, paged, pagedSlot);
    }

    private bool IsChrome(Control child) => ReferenceEquals(child, _pager) || ReferenceEquals(child, _pagerCue);

    /// <summary>The card whose cue the pager row carries (null: every card draws its own).</summary>
    private void HostCue(ColumnFlowCard? card)
    {
        if (!ReferenceEquals(card, _cueCard))
        {
            _cueCard?.CueHosted = false;
            _cueCard = card;
            _cueCard?.CueHosted = true;
        }

        _pagerCue.IsVisible = _cueCard?.HasMoreBelow == true;
    }

    private void OnMoreBelowChanged(object? sender, EventArgs e)
    {
        if (sender is ColumnFlowCard card && ReferenceEquals(card, _cueCard)) _pagerCue.IsVisible = card.HasMoreBelow;
    }

    /// <summary>The cue the card template draws at its bottom edge, for the pager row: a chevron and "More below".</summary>
    private static Border PagerCue()
    {
        var icon = new PathIcon { Width = 8, Height = 8 };
        icon[!PathIcon.DataProperty] = new DynamicResourceExtension("IconChevronDown");
        icon[!TemplatedControl.ForegroundProperty] = new DynamicResourceExtension("FireAccentBright");
        var text = new TextBlock { VerticalAlignment = VerticalAlignment.Center };
        text.Bind(TextBlock.TextProperty, (Avalonia.Data.BindingBase)new LocExtension("Inspector_ColumnFlow_MoreBelow").ProvideValue(null!));
        text[!TextBlock.FontSizeProperty] = new DynamicResourceExtension("FireSizeXs");
        text[!TextBlock.ForegroundProperty] = new DynamicResourceExtension("FireAccentBright");
        var cue = new Border
        {
            Height = 20,
            IsVisible = false,
            IsHitTestVisible = false,
            Child = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 4, VerticalAlignment = VerticalAlignment.Center, Children = { icon, text } }
        };
        cue[!Border.BackgroundProperty] = new DynamicResourceExtension("FireSurfaceFooter");
        cue[!Border.PaddingProperty] = new DynamicResourceExtension("FireGapHSm");
        cue[!Border.CornerRadiusProperty] = new DynamicResourceExtension("FireRadiusMicro");
        cue[!AutomationProperties.NameProperty] = text[!TextBlock.TextProperty];
        return cue;
    }

    // Only what the panel took away is given back: a card's own hit testing and tab navigation are left alone.
    private void SetOnStage(Control card, bool onStage)
    {
        if (onStage)
        {
            if (!_offStage.Remove(card)) return;
            card.IsHitTestVisible = true;
            card.ClearValue(KeyboardNavigation.TabNavigationProperty);
            _stageMoved = true;
        }
        else if (_offStage.Add(card))
        {
            card.IsHitTestVisible = false;
            KeyboardNavigation.SetTabNavigation(card, KeyboardNavigationMode.None);
            _stageMoved = true;
        }
    }

    private sealed record SlotPlan(IReadOnlyList<(IReadOnlyList<Control> Column, Size Slot)> Columns, IReadOnlyList<Control>? Paged, Size PagedSlot);
}
