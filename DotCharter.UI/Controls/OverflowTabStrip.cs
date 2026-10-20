using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Input;
using Avalonia.Layout;

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
///         styles hide the caption of every unchecked tab.</item>
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

    public static readonly AttachedProperty<bool> IsOverflowButtonProperty =
        AvaloniaProperty.RegisterAttached<OverflowTabStrip, Control, bool>("IsOverflowButton");

    private const double OffStage = -100_000;
    private const double Epsilon = 0.5;

    // The caption width each tab last MEASURED while its caption was shown, keyed by the text it
    // had then. A hidden caption measures as nothing, so this is what lets an icons-only strip
    // know when the labels fit again — and because it is the measured value, not an estimate, the
    // decision cannot flip back and forth at the threshold.
    private readonly Dictionary<TextBlock, (string Text, double Width)> _captionWidths = [];
    private readonly List<Control> _tabs = [];
    private TabStripPlan _plan = TabStripPlan.Empty;

    // The width the strip was last ARRANGED at, and the width the plan was decided for. A parent may
    // measure the strip wider than it then arranges it (the editor's dock at 1280x720 measures it at
    // 390.33 px and arranges it at 390), and a plan that only fitted the measure would overhang the
    // arranged strip; so the decision is taken for the narrower of the two, and an arrange at a
    // width the plan was not decided for asks for one more measure.
    private double _arrangedWidth = double.PositiveInfinity;
    private double _decidedWidth = double.NaN;

    static OverflowTabStrip() => AffectsMeasure<OverflowTabStrip>(SpacingProperty);

    public OverflowTabStrip()
    {
        ClipToBounds = true;
        // The checked tab is part of the decision (it is the one that keeps its caption and the one
        // that may not overflow), so a selection change re-decides even where no width moves.
        AddHandler(ToggleButton.IsCheckedChangedEvent, (_, _) => InvalidateMeasure(), handledEventsToo: true);
    }

    public double Spacing
    {
        get => GetValue(SpacingProperty);
        set => SetValue(SpacingProperty, value);
    }

    public static bool GetIsOverflowButton(Control control) => control.GetValue(IsOverflowButtonProperty);

    public static void SetIsOverflowButton(Control control, bool value) => control.SetValue(IsOverflowButtonProperty, value);

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
        _tabs.AddRange(Children.Where(c => c.IsVisible && !GetIsOverflowButton(c)));
        var more = OverflowButton;
        var widths = _tabs.Select(TabWidths).ToList();
        var active = _tabs.FindIndex(t => t is ToggleButton { IsChecked: true });
        _decidedWidth = Math.Min(availableSize.Width, _arrangedWidth);
        _plan = TabStripLayout.Decide(_decidedWidth, Spacing, widths, active, more?.DesiredSize.Width ?? 0);
        PseudoClasses.Set(":icons", _plan.Mode == TabStripMode.IconsOnly);

        var items = _plan.Shown.Select(i => _tabs[i].DesiredSize).ToList();
        if (_plan.HasOverflow && more is not null) items.Add(more.DesiredSize);
        var width = items.Sum(s => s.Width) + (Spacing * Math.Max(0, items.Count - 1));
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
                   + (Spacing * Math.Max(0, count - 1));
        // Whole device pixels: a layout-rounded child rounds a fractional width UP, and nine
        // of them overhung a 390 px strip by 7 px. What is left (under a pixel a tab) stays at the end.
        var scale = LayoutHelper.GetLayoutScale(this);
        var share = shown.Count == 0 ? 0 : Math.Floor(Math.Max(0, finalSize.Width - used) * scale / shown.Count) / scale;

        var x = 0.0;
        foreach (var tab in shown)
        {
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

    private TabStripTab TabWidths(Control tab)
    {
        var measured = tab.DesiredSize.Width;
        if ((tab as ContentControl)?.Content is not Panel content
            || content.Children.OfType<TextBlock>().FirstOrDefault() is not { } caption)
        {
            return new TabStripTab(measured, measured);
        }

        var gap = content is StackPanel stack ? stack.Spacing : 0;
        var captionWidth = CaptionWidth(caption);
        var iconWidth = caption.IsVisible ? measured - captionWidth - gap : measured;
        return new TabStripTab(iconWidth, iconWidth + captionWidth + gap);
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
