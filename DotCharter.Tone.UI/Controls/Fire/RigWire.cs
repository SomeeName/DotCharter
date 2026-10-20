using Avalonia;
using Avalonia.Media;

namespace DotCharter.Tone.UI.Controls.Fire;

/// <summary>
///     A wire on the rig board: a 2 px lead in the lane's colour with the
///     design's arrowhead at its end. The colour comes from the inherited
///     <see cref="FireUnitState.LaneTokenProperty" /> — a lane row sets it once
///     and every wire inside the lane picks it up, the trunk keeps the accent.
///     Width comes from the caller (a fixed lead) or the connector it fills.
/// </summary>
public sealed class RigWire : FireDrawnControl
{
    public static readonly StyledProperty<bool> ShowsArrowProperty =
        AvaloniaProperty.Register<RigWire, bool>(nameof(ShowsArrow), true);

    private const double Thickness = 2.0;
    private const double ArrowLength = 8.0;
    private const double ArrowHalfWidth = 4.5;

    static RigWire()
    {
        IsHitTestVisibleProperty.OverrideDefaultValue<RigWire>(false);
        AffectsRender<RigWire>(ShowsArrowProperty, FireUnitState.LaneTokenProperty);
    }

    /// <summary>Draw the arrowhead at the right end (a lead INTO a node).</summary>
    public bool ShowsArrow { get => GetValue(ShowsArrowProperty); set => SetValue(ShowsArrowProperty, value); }

    protected override Size MeasureOverride(Size availableSize)
        => new(double.IsFinite(availableSize.Width) ? 0 : 16, ArrowHalfWidth * 2 + 2);

    public override void Render(DrawingContext context)
    {
        base.Render(context);
        var width = Bounds.Width;
        if (width < 2) return;

        var colour = Paint.Color(this, FireUnitState.GetLaneToken(this));
        var y = Bounds.Height / 2;
        var lineEnd = ShowsArrow ? Math.Max(0, width - ArrowLength + 1) : width;
        context.DrawLine(Paint.Pen(colour, Thickness, PenLineCap.Round), new Point(0, y), new Point(lineEnd, y));
        if (!ShowsArrow) return;

        var head = new StreamGeometry();
        using (var ctx = head.Open())
        {
            ctx.BeginFigure(new Point(width - ArrowLength, y - ArrowHalfWidth), true);
            ctx.LineTo(new Point(width, y));
            ctx.LineTo(new Point(width - ArrowLength, y + ArrowHalfWidth));
            ctx.EndFigure(true);
        }
        context.DrawGeometry(Paint.Brush(colour), null, head);
    }
}
