using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Input;
using Avalonia.Layout;
using Avalonia.Media;

namespace DotCharter.UI.Controls;

/// <summary>
///     A one-row tab strip that is either ALL labelled or ALL icons, and overflows into a "More"
/// button instead of wrapping. The decision is <see cref="TabStripLayout.Decide" />; this
///     panel feeds it MEASURED widths and lays out what it says.
///     <list type="bullet">
///         <item>Every child is a tab except the one marked <see cref="IsOverflowButtonProperty" />.
///         A tab with <c>IsVisible=false</c> does not exist (the Inspector hides a tab whose view
///         model the shell did not supply).</item>
///         <item>A tab's CAPTION is the <see cref="TextBlock" /> that is a direct child of its content
///         panel; anything else (the glyph, a count badge) never hides. In
///         <see cref="TabStripMode.IconsOnly" /> the panel raises <c>:icons</c> and the strip's
///         styles hide the caption of every unchecked tab. A glyph marked
///         <see cref="IsIconsOnlyGlyphProperty" /> is the other way round: the styles show it only there.</item>
///         <item>The width left over is shared equally between the shown tabs, so the strip never
///         leaves a gap at its end.</item>
///         <item>An overflowed tab stays in the tree (it is still measured, so the strip knows when it
///         fits again) but is arranged outside the clipped panel and taken out of the Tab order and
///         hit testing. The More button is reached instead and lists <see cref="OverflowTabs" />.</item>
///     </list>
/// </summary>
public sealed class OverflowTabStrip : Panel
{
    public static readonly StyledProperty<double> SpacingProperty =
        AvaloniaProperty.Register<OverflowTabStrip, double>(nameof(Spacing), 2);

    /// <summary>The hairline drawn in the gap before a tab marked <see cref="IsGroupStartProperty" />.</summary>
    public static readonly StyledProperty<IBrush?> DividerBrushProperty =
        AvaloniaProperty.Register<OverflowTabStrip, IBrush?>(nameof(DividerBrush));

    /// <summary>The room a group-start tab keeps before it (the divider is drawn in it).</summary>
    public static readonly StyledProperty<double> GroupGapProperty =
        AvaloniaProperty.Register<OverflowTabStrip, double>(nameof(GroupGap));

    /// <summary>Marks the first tab of a group: the strip keeps a gap before it and draws the divider in it.</summary>
    public static readonly AttachedProperty<bool> IsGroupStartProperty =
        AvaloniaProperty.RegisterAttached<OverflowTabStrip, Control, bool>("IsGroupStart");

    /// <summary>False for a strip of captions only (inspector design D): it never turns into icons, what does not
    /// fit goes into the More button (<see cref="TabStripLayout.Decide" />).</summary>
    public static readonly StyledProperty<bool> IconsFallbackProperty =
        AvaloniaProperty.Register<OverflowTabStrip, bool>(nameof(IconsFallback), true);

    /// <summary>False packs the tabs at the start and leaves the slack at the end (the Note tab's section strip,
    /// whose few entries would otherwise spread across the pane) — while nothing is in More: a strip that overflows
    /// shares its slack anyway, so it never ends in a gap beside the More button (the icons fallback sizes its slots
    /// for the widest caption, and the gap that leaves read as room for a tab it put into More).</summary>
    public static readonly StyledProperty<bool> ShareSlackProperty =
        AvaloniaProperty.Register<OverflowTabStrip, bool>(nameof(ShareSlack), true);

    public static readonly AttachedProperty<bool> IsOverflowButtonProperty =
        AvaloniaProperty.RegisterAttached<OverflowTabStrip, Control, bool>("IsOverflowButton");

    /// <summary>Marks a tab's glyph that the strip's styles show ONLY in <see cref="TabStripMode.IconsOnly" />, and
    /// only on an unchecked tab (design D's captions-only strip keeps its glyphs for the icons fallback). The strip
    /// counts it into a tab's icon width even while it is hidden, from its fixed <c>Width</c>.</summary>
    public static readonly AttachedProperty<bool> IsIconsOnlyGlyphProperty =
        AvaloniaProperty.RegisterAttached<OverflowTabStrip, Control, bool>("IsIconsOnlyGlyph");

    private const double OffStage = -100_000;
    private const double Epsilon = 0.5;

    // The caption width each tab last MEASURED while its caption was shown, keyed by the text it
    // had then. A hidden caption measures as nothing, so this is what lets an icons-only strip
    // know when the labels fit again — and because it is the measured value, not an estimate, the
    // decision cannot flip back and forth at the threshold.
    private readonly Dictionary<TextBlock, (string Text, double Width)> _captionWidths = [];
    private readonly List<Control> _tabs = [];
    private TabStripPlan _plan = TabStripPlan.Empty;

    // The hairline before a group start: a child the strip owns, not a tab.
    private readonly Border _divider = new() { Width = 1, IsHitTestVisible = false, VerticalAlignment = VerticalAlignment.Stretch };

    // The width the strip was last ARRANGED at, and the width the plan was decided for. A parent may
    // measure the strip wider than it then arranges it (the editor's dock at 1280x720 measures it at
    // 390.33 px and arranges it at 390), and a plan that only fitted the measure would overhang the
    // arranged strip; so the decision is taken for the narrower of the two, and an arrange at a
    // width the plan was not decided for asks for one more measure.
    private double _arrangedWidth = double.PositiveInfinity;
    private double _decidedWidth = double.NaN;

    static OverflowTabStrip() => AffectsMeasure<OverflowTabStrip>(SpacingProperty, GroupGapProperty, IconsFallbackProperty, ShareSlackProperty);

    public OverflowTabStrip()
    {
        ClipToBounds = true;
        // The checked tab is part of the decision (it is the one that keeps its caption and the one
        // that may not overflow), so a selection change re-decides even where no width moves.
        _divider[!Border.BackgroundProperty] = this[!DividerBrushProperty];
        Children.Add(_divider);
        AddHandler(ToggleButton.IsCheckedChangedEvent, (_, _) => InvalidateMeasure(), handledEventsToo: true);
    }

    public double Spacing
    {
        get => GetValue(SpacingProperty);
        set => SetValue(SpacingProperty, value);
    }

    public double GroupGap
    {
        get => GetValue(GroupGapProperty);
        set => SetValue(GroupGapProperty, value);
    }

    public bool IconsFallback
    {
        get => GetValue(IconsFallbackProperty);
        set => SetValue(IconsFallbackProperty, value);
    }

    public bool ShareSlack
    {
        get => GetValue(ShareSlackProperty);
        set => SetValue(ShareSlackProperty, value);
    }

    public IBrush? DividerBrush
    {
        get => GetValue(DividerBrushProperty);
        set => SetValue(DividerBrushProperty, value);
    }

    /// <summary>The tabs standing in the strip that start a group, i.e. that a divider is drawn before.</summary>
    public IReadOnlyList<Control> GroupStarts => [.. ShownTabs.Skip(1).Where(t => IsGroupStart(t))];

    /// <summary>The divider's bounds, or null while no group start stands in the strip — the test hook.</summary>
    public Rect? DividerBounds => GroupStarts.Count > 0 && _divider.Bounds.X >= 0 ? _divider.Bounds : null;

    public static bool GetIsGroupStart(Control control) => control.GetValue(IsGroupStartProperty);

    public static void SetIsGroupStart(Control control, bool value) => control.SetValue(IsGroupStartProperty, value);

    public static bool GetIsOverflowButton(Control control) => control.GetValue(IsOverflowButtonProperty);

    public static void SetIsOverflowButton(Control control, bool value) => control.SetValue(IsOverflowButtonProperty, value);

    public static bool GetIsIconsOnlyGlyph(Control control) => control.GetValue(IsIconsOnlyGlyphProperty);

    public static void SetIsIconsOnlyGlyph(Control control, bool value) => control.SetValue(IsIconsOnlyGlyphProperty, value);

    public TabStripMode Mode => _plan.Mode;

    /// <summary>The tabs standing in the strip, in strip order.</summary>
    public IReadOnlyList<Control> ShownTabs => [.. _plan.Shown.Where(i => i < _tabs.Count).Select(i => _tabs[i])];

    /// <summary>The tabs the More button lists, in strip order.</summary>
    public IReadOnlyList<Control> OverflowTabs => [.. _plan.Overflow.Where(i => i < _tabs.Count).Select(i => _tabs[i])];

    /// <summary>The More button, or null when the strip declares none.</summary>
    public Control? OverflowButton => Children.FirstOrDefault(GetIsOverflowButton);

    protected override Size MeasureOverride(Size availableSize)
    {
        var unbounded = new Size(double.PositiveInfinity, availableSize.Height);
        foreach (var child in Children) child.Measure(unbounded);

        _tabs.Clear();
        _tabs.AddRange(Children.Where(c => c.IsVisible && !GetIsOverflowButton(c) && !ReferenceEquals(c, _divider)));
        var more = OverflowButton;
        // A group start keeps its gap in the budget the plan is decided against, so the gap cannot overhang.
        var widths = _tabs.Select((t, i) => TabWidths(t, i > 0 && IsGroupStart(t) ? GroupGap : 0)).ToList();
        var active = _tabs.FindIndex(t => t is ToggleButton { IsChecked: true });
        _decidedWidth = Math.Min(availableSize.Width, _arrangedWidth);
        _plan = TabStripLayout.Decide(_decidedWidth, Spacing, widths, active, more?.DesiredSize.Width ?? 0, IconsFallback);
        PseudoClasses.Set(":icons", _plan.Mode == TabStripMode.IconsOnly);

        var items = _plan.Shown.Select(i => _tabs[i].DesiredSize).ToList();
        if (_plan.HasOverflow && more is not null) items.Add(more.DesiredSize);
        var width = items.Sum(s => s.Width) + (Spacing * Math.Max(0, items.Count - 1)) + GapsBefore(_plan.Shown);
        var height = Children.Where(c => c.IsVisible).Select(c => c.DesiredSize.Height).DefaultIfEmpty(0).Max();
        // A constrained strip asks for ALL of its width — it shares the slack between its tabs
        // anyway — so a parent that sizes to content arranges it at the width it was decided for
        // instead of shrinking it one decision at a time.
        return new Size(double.IsInfinity(_decidedWidth) ? width : _decidedWidth, height);
    }

    protected override Size ArrangeOverride(Size finalSize)
    {
        _arrangedWidth = finalSize.Width;
        if (!(Math.Abs(finalSize.Width - _decidedWidth) <= Epsilon)) InvalidateMeasure();

        var more = OverflowButton;
        var shown = _plan.Shown.Where(i => i < _tabs.Count).Select(i => _tabs[i]).ToList();
        var showMore = _plan.HasOverflow && more is not null;
        var count = shown.Count + (showMore ? 1 : 0);
        var used = shown.Sum(t => t.DesiredSize.Width) + (showMore ? more!.DesiredSize.Width : 0)
                   + (Spacing * Math.Max(0, count - 1)) + GapsBefore(_plan.Shown);
        // Whole device pixels: a layout-rounded child rounds a fractional width UP, and nine
        // of them overhung a 390 px strip by 7 px. What is left (under a pixel a tab) stays at the end.
        var scale = LayoutHelper.GetLayoutScale(this);
        var share = shown.Count == 0 || (!ShareSlack && !_plan.HasOverflow) ? 0 : Math.Floor(Math.Max(0, finalSize.Width - used) * scale / shown.Count) / scale;

        var x = 0.0;
        var dividerX = double.NaN;
        foreach (var tab in shown)
        {
            if (shown.IndexOf(tab) > 0 && IsGroupStart(tab))
            {
                dividerX = x + (GroupGap / 2);
                x += GroupGap;
            }

            var width = tab.DesiredSize.Width + share;
            tab.Arrange(new Rect(x, 0, width, finalSize.Height));
            SetOnStage(tab, true);
            x += width + Spacing;
        }

        if (showMore)
        {
            more!.Arrange(new Rect(x, 0, more.DesiredSize.Width, finalSize.Height));
            SetOnStage(more, true);
        }
        else if (more is not null)
        {
            ArrangeOffStage(more, finalSize.Height);
        }

        if (double.IsNaN(dividerX) || _divider.Background is null)
            _divider.Arrange(new Rect(OffStage, 0, 1, 0));
        else
            _divider.Arrange(new Rect(Math.Round(dividerX), finalSize.Height * 0.2, 1, finalSize.Height * 0.6));

        foreach (var index in _plan.Overflow.Where(i => i < _tabs.Count)) ArrangeOffStage(_tabs[index], finalSize.Height);
        return finalSize;
    }

    private static void ArrangeOffStage(Control child, double height)
    {
        child.Arrange(new Rect(OffStage, 0, child.DesiredSize.Width, height));
        SetOnStage(child, false);
    }

    private static void SetOnStage(Control child, bool onStage)
    {
        if (child.IsHitTestVisible != onStage) child.IsHitTestVisible = onStage;
        if (KeyboardNavigation.GetIsTabStop(child) != onStage) KeyboardNavigation.SetIsTabStop(child, onStage);
    }

    private static bool IsGroupStart(Control tab) => GetIsGroupStart(tab);

    /// <summary>The gap a group start adds before itself, summed over the tabs shown (the first never has one).</summary>
    private double GapsBefore(IEnumerable<int> shown) =>
        shown.Where(i => i < _tabs.Count).Skip(1).Count(i => IsGroupStart(_tabs[i])) * GroupGap;

    private TabStripTab TabWidths(Control tab, double extra)
    {
        var measured = tab.DesiredSize.Width;
        if ((tab as ContentControl)?.Content is not Panel content
            || content.Children.OfType<TextBlock>().FirstOrDefault() is not { } caption)
        {
            return new TabStripTab(measured + extra, measured + extra);
        }

        // What the tab is without its caption and without an icons-only glyph (each takes its width plus one gap
        // while shown): labelled adds the caption back, icons the glyph.
        var gap = content is StackPanel stack ? stack.Spacing : 0;
        var captionWidth = CaptionWidth(caption);
        var glyph = content.Children.FirstOrDefault(GetIsIconsOnlyGlyph);
        var glyphWidth = glyph is null ? 0 : GlyphWidth(glyph);
        var core = measured - (caption.IsVisible ? captionWidth + gap : 0) - (glyph is { IsVisible: true } ? glyphWidth + gap : 0);
        var iconWidth = glyph is null ? core : core + glyphWidth + gap;
        return new TabStripTab(iconWidth + extra, core + captionWidth + gap + extra);
    }

    private static double GlyphWidth(Control glyph)
    {
        if (glyph.IsVisible) return glyph.DesiredSize.Width;
        var width = double.IsNaN(glyph.Width) ? 0 : glyph.Width;
        return width + glyph.Margin.Left + glyph.Margin.Right;
    }

    private double CaptionWidth(TextBlock caption)
    {
        var text = caption.Text ?? string.Empty;
        if (caption.IsVisible)
        {
            _captionWidths[caption] = (text, caption.DesiredSize.Width);
            return caption.DesiredSize.Width;
        }

        if (_captionWidths.TryGetValue(caption, out var known) && known.Text == text) return known.Width;

        // Never shown with this text (a language switch while it was hidden): measure a copy. The
        // first time the caption is shown, the measured width replaces this one.
        var probe = new TextBlock
        {
            Text = text,
            FontFamily = caption.FontFamily,
            FontSize = caption.FontSize,
            FontWeight = caption.FontWeight,
            FontStyle = caption.FontStyle,
            FontStretch = caption.FontStretch,
            LetterSpacing = caption.LetterSpacing,
            Padding = caption.Padding
        };
        probe.Measure(Size.Infinity);
        _captionWidths[caption] = (text, probe.DesiredSize.Width);
        return probe.DesiredSize.Width;
    }
}
