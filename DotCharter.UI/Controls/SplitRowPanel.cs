using Avalonia;
using Avalonia.Controls;

namespace DotCharter.UI.Controls;

/// <summary>
///     A header row of two children: a LEAD at the left and a TRAIL at the right. When both fit side by side they share
///     one row, the trail right-aligned; when they do not, the trail moves to a second row under the lead (still
///     right-aligned), measured at the full width, so a trail that is itself a <see cref="WrapPanel" /> wraps further.
///     Nothing is hidden and nothing overlaps: a <c>*,Auto</c> grid would give the lead whatever the trail leaves, and a
///     long German trail left the lead a negative width that painted it under the trail (the Shape page's fret pager).
///     A hidden child takes no room.
/// </summary>
public sealed class SplitRowPanel : Panel
{
    public static readonly StyledProperty<double> SpacingProperty =
        AvaloniaProperty.Register<SplitRowPanel, double>(nameof(Spacing));

    public static readonly StyledProperty<double> RowSpacingProperty =
        AvaloniaProperty.Register<SplitRowPanel, double>(nameof(RowSpacing));

    static SplitRowPanel() => AffectsMeasure<SplitRowPanel>(SpacingProperty, RowSpacingProperty);

    /// <summary>The least gap between the lead and the trail on one row.</summary>
    public double Spacing
    {
        get => GetValue(SpacingProperty);
        set => SetValue(SpacingProperty, value);
    }

    /// <summary>The gap between the lead's row and the trail's when the trail wraps under it.</summary>
    public double RowSpacing
    {
        get => GetValue(RowSpacingProperty);
        set => SetValue(RowSpacingProperty, value);
    }

    /// <summary>Whether the last arrange put the trail on a second row.</summary>
    public bool IsWrapped { get; private set; }

    protected override Size MeasureOverride(Size availableSize)
    {
        if (Children.Count != 2) return base.MeasureOverride(availableSize);

        var (lead, trail) = (Children[0], Children[1]);
        var row = new Size(availableSize.Width, double.PositiveInfinity);
        lead.Measure(row);
        trail.Measure(row);
        var (l, t) = (lead.DesiredSize, trail.DesiredSize);
        return FitsOneRow(availableSize.Width)
            ? new Size(l.Width + Gap() + t.Width, Math.Max(l.Height, t.Height))
            : new Size(Math.Max(l.Width, t.Width), l.Height + RowGap() + t.Height);
    }

    protected override Size ArrangeOverride(Size finalSize)
    {
        if (Children.Count != 2) return base.ArrangeOverride(finalSize);

        var (lead, trail) = (Children[0], Children[1]);
        var (l, t) = (lead.DesiredSize, trail.DesiredSize);
        IsWrapped = !FitsOneRow(finalSize.Width);
        var trailWidth = Math.Min(t.Width, finalSize.Width);
        if (IsWrapped)
        {
            lead.Arrange(new Rect(0, 0, Math.Min(l.Width, finalSize.Width), l.Height));
            trail.Arrange(new Rect(finalSize.Width - trailWidth, l.Height + RowGap(), trailWidth, t.Height));
            return finalSize;
        }

        var height = Math.Max(l.Height, t.Height);
        lead.Arrange(new Rect(0, 0, l.Width, height));
        trail.Arrange(new Rect(finalSize.Width - trailWidth, 0, trailWidth, height));
        return finalSize;
    }

    private bool FitsOneRow(double width) =>
        Children[0].DesiredSize.Width + Gap() + Children[1].DesiredSize.Width <= width + 0.5;

    private double Gap() => Children[0].IsVisible && Children[1].IsVisible ? Spacing : 0;

    private double RowGap() => Children[0].IsVisible && Children[1].IsVisible ? RowSpacing : 0;
}
