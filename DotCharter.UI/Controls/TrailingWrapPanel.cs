using Avalonia;
using Avalonia.Controls;

namespace DotCharter.UI.Controls;

/// <summary>
///     One row whose FIRST child stretches over whatever the others leave, down to
///     <see cref="StretchMinWidth" />; when the row cannot give it that much, the LAST child drops
///     to a second row of its own. The Inspector's work-queue bar: filter · stepper · keyboard
/// readout on one line in a wide pane, and the readout under them in a narrow one.
///     <para>
///         It replaces a <c>Grid ColumnDefinitions="*,Auto,Auto"</c> whose filter <c>ComboBox</c>
///         carried <c>MinWidth="150"</c>: below ~440 px the star cell was narrower than that
///         minimum, the ComboBox was still arranged at 150 px and overhung its cell to the LEFT
///         ("ny sound (240)"), and nothing else could give way. The floor lives HERE now, so a
///         child is never arranged wider than the slot it was given.
///     </para>
/// </summary>
public sealed class TrailingWrapPanel : Panel
{
    public static readonly StyledProperty<double> SpacingProperty =
        AvaloniaProperty.Register<TrailingWrapPanel, double>(nameof(Spacing));

    public static readonly StyledProperty<double> RowSpacingProperty =
        AvaloniaProperty.Register<TrailingWrapPanel, double>(nameof(RowSpacing));

    public static readonly StyledProperty<double> StretchMinWidthProperty =
        AvaloniaProperty.Register<TrailingWrapPanel, double>(nameof(StretchMinWidth));

    static TrailingWrapPanel() => AffectsMeasure<TrailingWrapPanel>(SpacingProperty, RowSpacingProperty, StretchMinWidthProperty);

    /// <summary>The gap between neighbours on a row.</summary>
    public double Spacing
    {
        get => GetValue(SpacingProperty);
        set => SetValue(SpacingProperty, value);
    }

    /// <summary>The gap between the first row and the wrapped last child.</summary>
    public double RowSpacing
    {
        get => GetValue(RowSpacingProperty);
        set => SetValue(RowSpacingProperty, value);
    }

    /// <summary>The narrowest the first child may be before the last child wraps.</summary>
    public double StretchMinWidth
    {
        get => GetValue(StretchMinWidthProperty);
        set => SetValue(StretchMinWidthProperty, value);
    }

    /// <summary>Whether the last child is on a row of its own — as of the last arrange.</summary>
    public bool IsWrapped { get; private set; }

    protected override Size MeasureOverride(Size availableSize)
    {
        var visible = Visible();
        if (visible.Count == 0) return default;

        var unbounded = new Size(double.PositiveInfinity, availableSize.Height);
        for (var i = 1; i < visible.Count; i++) visible[i].Measure(unbounded);

        var trailing = Fixed(visible, 1, visible.Count);
        var wraps = TrailingWrapLayout.Wraps(availableSize.Width, StretchMinWidth, trailing, Spacing, visible.Count);
        var rowEnd = wraps ? visible.Count - 1 : visible.Count;
        var rowFixed = Fixed(visible, 1, rowEnd);
        var stretchWidth = double.IsInfinity(availableSize.Width)
            ? double.PositiveInfinity
            : Math.Max(0, availableSize.Width - rowFixed - Gaps(rowEnd));
        visible[0].Measure(new Size(stretchWidth, availableSize.Height));

        var rowHeight = 0.0;
        for (var i = 0; i < rowEnd; i++) rowHeight = Math.Max(rowHeight, visible[i].DesiredSize.Height);
        var rowWidth = visible[0].DesiredSize.Width + rowFixed + Gaps(rowEnd);
        if (!wraps) return new Size(Math.Min(rowWidth, availableSize.Width), rowHeight);

        var last = visible[^1];
        last.Measure(availableSize);
        return new Size(Math.Min(Math.Max(rowWidth, last.DesiredSize.Width), availableSize.Width),
            rowHeight + RowSpacing + last.DesiredSize.Height);
    }

    protected override Size ArrangeOverride(Size finalSize)
    {
        var visible = Visible();
        if (visible.Count == 0) return finalSize;

        var trailing = Fixed(visible, 1, visible.Count);
        IsWrapped = TrailingWrapLayout.Wraps(finalSize.Width, StretchMinWidth, trailing, Spacing, visible.Count);
        var rowEnd = IsWrapped ? visible.Count - 1 : visible.Count;
        var rowFixed = Fixed(visible, 1, rowEnd);
        var rowHeight = 0.0;
        for (var i = 0; i < rowEnd; i++) rowHeight = Math.Max(rowHeight, visible[i].DesiredSize.Height);

        var x = 0.0;
        for (var i = 0; i < rowEnd; i++)
        {
            var width = i == 0 ? Math.Max(0, finalSize.Width - rowFixed - Gaps(rowEnd)) : visible[i].DesiredSize.Width;
            visible[i].Arrange(new Rect(x, 0, width, rowHeight));
            x += width + Spacing;
        }

        if (IsWrapped)
        {
            var last = visible[^1];
            var top = rowHeight + RowSpacing;
            last.Arrange(new Rect(0, top, Math.Min(last.DesiredSize.Width, finalSize.Width), Math.Max(0, finalSize.Height - top)));
        }

        return finalSize;
    }

    private List<Control> Visible() => [.. Children.Where(c => c.IsVisible)];

    private static double Fixed(List<Control> visible, int from, int to)
    {
        var width = 0.0;
        for (var i = from; i < to; i++) width += visible[i].DesiredSize.Width;
        return width;
    }

    private double Gaps(int count) => count > 1 ? (count - 1) * Spacing : 0;
}

/// <summary>The wrap decision behind <see cref="TrailingWrapPanel" />, pure so the gate pins it.</summary>
public static class TrailingWrapLayout
{
    /// <summary>
    ///     Whether the last of <paramref name="count" /> children must leave the row: true when
    ///     the first child would get less than <paramref name="stretchMinWidth" /> beside the
    ///     others (<paramref name="trailingWidth" /> together, plus a <paramref name="spacing" />
    ///     between each pair). One child never wraps, and neither does an unconstrained row.
    /// </summary>
    public static bool Wraps(double available, double stretchMinWidth, double trailingWidth, double spacing, int count)
    {
        if (count < 2 || double.IsInfinity(available) || double.IsNaN(available)) return false;
        return available - trailingWidth - ((count - 1) * spacing) < stretchMinWidth;
    }
}
