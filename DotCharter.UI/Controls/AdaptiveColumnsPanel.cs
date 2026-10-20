using Avalonia;
using Avalonia.Controls;

namespace DotCharter.UI.Controls;

/// <summary>
/// A list that becomes a grid when there is room: as many equal columns as fit at
///     <see cref="MinColumnWidth" />, children placed row-major, each row as tall as its tallest
///     child. One column at an inspector's usual width, two or more when the pane is dragged wide
///     or floated — so a wide pane shows more shapes instead of wider rows.
/// </summary>
public sealed class AdaptiveColumnsPanel : Panel
{
    public static readonly StyledProperty<double> MinColumnWidthProperty =
        AvaloniaProperty.Register<AdaptiveColumnsPanel, double>(nameof(MinColumnWidth), 320);

    public static readonly StyledProperty<double> ColumnSpacingProperty =
        AvaloniaProperty.Register<AdaptiveColumnsPanel, double>(nameof(ColumnSpacing));

    static AdaptiveColumnsPanel() => AffectsMeasure<AdaptiveColumnsPanel>(MinColumnWidthProperty, ColumnSpacingProperty);

    public double MinColumnWidth
    {
        get => GetValue(MinColumnWidthProperty);
        set => SetValue(MinColumnWidthProperty, value);
    }

    public double ColumnSpacing
    {
        get => GetValue(ColumnSpacingProperty);
        set => SetValue(ColumnSpacingProperty, value);
    }

    /// <summary>How many columns of at least <paramref name="minColumnWidth" /> fit in <paramref name="width" />.</summary>
    public static int ColumnsFor(double width, double minColumnWidth, double spacing)
    {
        if (double.IsInfinity(width) || double.IsNaN(width) || minColumnWidth <= 0) return 1;
        return Math.Max(1, (int)Math.Floor((width + spacing) / (minColumnWidth + spacing)));
    }

    protected override Size MeasureOverride(Size availableSize)
    {
        var columns = ColumnsFor(availableSize.Width, MinColumnWidth, ColumnSpacing);
        var columnWidth = ColumnWidth(availableSize.Width, columns);
        var height = 0.0;
        var rowHeight = 0.0;
        var widest = 0.0;
        for (var i = 0; i < Children.Count; i++)
        {
            var child = Children[i];
            child.Measure(new Size(columnWidth, double.PositiveInfinity));
            rowHeight = Math.Max(rowHeight, child.DesiredSize.Height);
            widest = Math.Max(widest, child.DesiredSize.Width);
            if (i % columns != columns - 1 && i != Children.Count - 1) continue;
            height += rowHeight;
            rowHeight = 0;
        }

        var width = double.IsInfinity(availableSize.Width) ? widest : availableSize.Width;
        return new Size(width, height);
    }

    protected override Size ArrangeOverride(Size finalSize)
    {
        var columns = ColumnsFor(finalSize.Width, MinColumnWidth, ColumnSpacing);
        var columnWidth = ColumnWidth(finalSize.Width, columns);
        var y = 0.0;
        for (var start = 0; start < Children.Count; start += columns)
        {
            var end = Math.Min(Children.Count, start + columns);
            var rowHeight = 0.0;
            for (var i = start; i < end; i++) rowHeight = Math.Max(rowHeight, Children[i].DesiredSize.Height);
            for (var i = start; i < end; i++)
            {
                var x = (i - start) * (columnWidth + ColumnSpacing);
                Children[i].Arrange(new Rect(x, y, columnWidth, rowHeight));
            }

            y += rowHeight;
        }

        return finalSize;
    }

    private double ColumnWidth(double width, int columns) =>
        double.IsInfinity(width) ? MinColumnWidth : Math.Max(0, (width - (columns - 1) * ColumnSpacing) / columns);
}
