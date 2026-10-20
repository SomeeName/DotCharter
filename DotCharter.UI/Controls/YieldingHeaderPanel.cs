using Avalonia;
using Avalonia.Controls;

namespace DotCharter.UI.Controls;

/// <summary>
///     Two children stacked: a HEADER (a cover) over a BODY (the details). The body is measured
///     first and gets the height it needs, up to all of it; the header takes only what is left,
///     up to its own <c>MaxHeight</c>, and is dropped when that is under
///     <see cref="MinShownHeight" />. So a short window loses the cover before it loses anything
/// the body says (a fixed 278 px cover left one of four paths on screen at 1280×720).
///     <para>
///         The body should be a <see cref="ScrollViewer" />: measured against the full height it
///         asks for no more than that, and scrolls whatever still does not fit. A hidden header
///         takes nothing; the body then starts at the top.
///     </para>
/// </summary>
public sealed class YieldingHeaderPanel : Panel
{
    public static readonly StyledProperty<double> SpacingProperty =
        AvaloniaProperty.Register<YieldingHeaderPanel, double>(nameof(Spacing));

    public static readonly StyledProperty<double> MinShownHeightProperty =
        AvaloniaProperty.Register<YieldingHeaderPanel, double>(nameof(MinShownHeight), 96);

    static YieldingHeaderPanel() => AffectsMeasure<YieldingHeaderPanel>(SpacingProperty, MinShownHeightProperty);

    /// <summary>The gap between the header and the body when the header is shown.</summary>
    public double Spacing
    {
        get => GetValue(SpacingProperty);
        set => SetValue(SpacingProperty, value);
    }

    /// <summary>A header with less room than this is not shown at all: a sliver of a cover reads as a glitch.</summary>
    public double MinShownHeight
    {
        get => GetValue(MinShownHeightProperty);
        set => SetValue(MinShownHeightProperty, value);
    }

    /// <summary>The height the header was arranged at: 0 when it was dropped or is hidden.</summary>
    public double HeaderHeight { get; private set; }

    protected override Size MeasureOverride(Size availableSize)
    {
        if (Children.Count != 2) return base.MeasureOverride(availableSize);

        var header = Children[0];
        var body = Children[1];
        body.Measure(availableSize);
        var room = HeaderRoom(header, availableSize, body.DesiredSize.Height);
        header.Measure(new Size(availableSize.Width, room));
        var width = Math.Max(header.DesiredSize.Width, body.DesiredSize.Width);
        return new Size(width, body.DesiredSize.Height + (room > 0 ? room + Spacing : 0));
    }

    protected override Size ArrangeOverride(Size finalSize)
    {
        if (Children.Count != 2) return base.ArrangeOverride(finalSize);

        var header = Children[0];
        var body = Children[1];
        var room = HeaderRoom(header, finalSize, Math.Min(body.DesiredSize.Height, finalSize.Height));
        HeaderHeight = room;
        header.Arrange(new Rect(0, 0, finalSize.Width, room));
        var top = room > 0 ? room + Spacing : 0;
        body.Arrange(new Rect(0, top, finalSize.Width, Math.Max(0, finalSize.Height - top)));
        return finalSize;
    }

    /// <summary>What the body leaves for the header, capped at the header's MaxHeight; 0 below the minimum.</summary>
    private double HeaderRoom(Control header, Size size, double bodyHeight)
    {
        if (!header.IsVisible) return 0;

        var left = double.IsInfinity(size.Height) ? double.PositiveInfinity : size.Height - bodyHeight - Spacing;
        var room = Math.Min(left, header.MaxHeight);
        if (double.IsInfinity(room))
        {
            // Nothing bounds it (a measure with infinite height and no MaxHeight): its own size.
            header.Measure(new Size(size.Width, double.PositiveInfinity));
            room = header.DesiredSize.Height;
        }

        return room >= MinShownHeight ? room : 0;
    }
}
