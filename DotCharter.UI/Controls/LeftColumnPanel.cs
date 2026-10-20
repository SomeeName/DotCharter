using Avalonia;
using Avalonia.Controls;

namespace DotCharter.UI.Controls;

/// <summary>
///     A reading column pinned to the LEFT: every child is arranged at
///     <c>min(available width, ColumnMaxWidth)</c> from x = 0.
///     <para>
///         Neither alignment can do this alone. A Stretch child that MaxWidth
///         clamps is centred by the layout pass, and a Left child only sizes to
///         its content, so a short tab came out narrower than the column. Giving
///         the child exactly the column's width is what makes both hold.
///     </para>
/// </summary>
public sealed class LeftColumnPanel : Panel
{
    public static readonly StyledProperty<double> ColumnMaxWidthProperty =
        AvaloniaProperty.Register<LeftColumnPanel, double>(nameof(ColumnMaxWidth), double.PositiveInfinity);

    static LeftColumnPanel() => AffectsMeasure<LeftColumnPanel>(ColumnMaxWidthProperty);

    public double ColumnMaxWidth
    {
        get => GetValue(ColumnMaxWidthProperty);
        set => SetValue(ColumnMaxWidthProperty, value);
    }

    protected override Size MeasureOverride(Size availableSize)
    {
        var width = Math.Min(availableSize.Width, ColumnMaxWidth);
        var height = 0.0;
        foreach (var child in Children)
        {
            child.Measure(new Size(width, availableSize.Height));
            height = Math.Max(height, child.DesiredSize.Height);
        }

        return new Size(double.IsInfinity(width) ? Children.Max(c => c.DesiredSize.Width) : width, height);
    }

    protected override Size ArrangeOverride(Size finalSize)
    {
        var width = Math.Min(finalSize.Width, ColumnMaxWidth);
        foreach (var child in Children)
            child.Arrange(new Rect(0, 0, width, finalSize.Height));
        return finalSize;
    }
}
