using Avalonia;
using Avalonia.Controls;

namespace DotCharter.UI.Controls;

/// <summary>
///     A body that is <see cref="PreferredHeight" /> tall when its panel has the room and takes
///     whatever it is given when it has not — a stated height that YIELDS.
///     <para>
///         A fixed <c>Height</c> cannot do that: it is a size, not a wish, so a fixed-height last child
///         of a <c>DockPanel</c> is arranged at its own height in a shorter slot, centred, and paints
/// over the docked head and footer on both sides (the Tone designer's 560 px body over
///         its title and its Done button in a 1280×720 window). A <c>MaxHeight</c> alone yields, but
///         then the body is only as tall as its content, so a dialog would change height as rows come
///         and go. This asks for <see cref="PreferredHeight" /> whatever the content, and measures the
///         child against no more than it was actually offered, so the lists inside keep their own
///         scrollbars at every size.
///     </para>
/// </summary>
public sealed class YieldingHeightDecorator : Decorator
{
    public static readonly StyledProperty<double> PreferredHeightProperty =
        AvaloniaProperty.Register<YieldingHeightDecorator, double>(nameof(PreferredHeight), double.NaN);

    static YieldingHeightDecorator() => AffectsMeasure<YieldingHeightDecorator>(PreferredHeightProperty);

    /// <summary>The height asked for when the room allows it; unset (NaN) asks for the child's own height.</summary>
    public double PreferredHeight
    {
        get => GetValue(PreferredHeightProperty);
        set => SetValue(PreferredHeightProperty, value);
    }

    /// <summary>The smaller of <see cref="PreferredHeight" /> and the height offered; infinite when neither bounds it.</summary>
    private static double HeightFor(double preferred, double offered) =>
        double.IsNaN(preferred) ? offered : Math.Min(preferred, offered);

    protected override Size MeasureOverride(Size availableSize)
    {
        var height = HeightFor(PreferredHeight, availableSize.Height);
        var child = Child;
        if (child is null) return new Size(0, double.IsInfinity(height) ? 0 : height);

        child.Measure(availableSize.WithHeight(height));
        return new Size(child.DesiredSize.Width, double.IsInfinity(height) || double.IsNaN(PreferredHeight)
            ? child.DesiredSize.Height
            : height);
    }
}
