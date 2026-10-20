using Avalonia;
using Avalonia.Controls;
using Avalonia.Media;
using Avalonia.VisualTree;

namespace DotCharter.Tone.UI.Controls.Fire;

/// <summary>
///     The split region's fan: the design's curved wires from the Split node
///     out to every lane, or from every lane's mixer into the Merge node, each
///     in its lane's colour with an arrowhead at the end. It sits in the region
///     grid beside the lanes and finds each lane's anchor (the lane row's
///     <c>FireLaneStub</c> on its side) at render time, so the curves follow
///     the lanes however tall they grow.
/// </summary>
public sealed class RigFan : FireDrawnControl
{
    /// <summary>False: the split fan (one point on the left, a curve to each
    /// lane). True: the merge fan (each lane into one point on the right).</summary>
    public static readonly StyledProperty<bool> MergesProperty =
        AvaloniaProperty.Register<RigFan, bool>(nameof(Merges));

    private const double Thickness = 2.0;
    private const double ArrowLength = 8.0;
    private const double ArrowHalfWidth = 4.5;
    private const double MaxTail = 14.0;
    private const double MaxRadius = 12.0;

    static RigFan()
    {
        IsHitTestVisibleProperty.OverrideDefaultValue<RigFan>(false);
        AffectsRender<RigFan>(MergesProperty);
    }

    public bool Merges { get => GetValue(MergesProperty); set => SetValue(MergesProperty, value); }

    protected override Size MeasureOverride(Size availableSize) => new(48, 0);

    /// <summary>One entry per lane on this fan's side: its vertical centre in
    /// this control's coordinates and its colour. Exposed for tests.</summary>
    public IReadOnlyList<(double Y, Color Colour)> Anchors()
    {
        var anchors = new List<(double, Color)>();
        if (this.GetVisualParent() is not Visual region) return anchors;
        var side = Merges ? 2 : 0;
        foreach (var stub in region.GetVisualDescendants().OfType<Border>())
        {
            if (!stub.Classes.Contains("FireLaneStub") || Grid.GetColumn(stub) != side) continue;
            if (stub.Background is not ISolidColorBrush brush) continue;
            var point = stub.TranslatePoint(new Point(0, stub.Bounds.Height / 2), this);
            if (point is { } p) anchors.Add((p.Y, brush.Color));
        }
        return anchors;
    }

    public override void Render(DrawingContext context)
    {
        base.Render(context);
        var width = Bounds.Width;
        var middle = Bounds.Height / 2;
        if (width < 8) return;

        foreach (var (y, colour) in Anchors())
        {
            var from = Merges ? new Point(0, y) : new Point(0, middle);
            var to = Merges ? new Point(width, middle) : new Point(width, y);
            context.DrawGeometry(null, Paint.Pen(colour, Thickness, PenLineCap.Round), Elbow(from, to, ArrowLength - 1));

            var head = new StreamGeometry();
            using (var ctx = head.Open())
            {
                ctx.BeginFigure(new Point(to.X - ArrowLength, to.Y - ArrowHalfWidth), true);
                ctx.LineTo(to);
                ctx.LineTo(new Point(to.X - ArrowLength, to.Y + ArrowHalfWidth));
                ctx.EndFigure(true);
            }
            context.DrawGeometry(Paint.Brush(colour), null, head);
        }
    }

    /// <summary>
    ///     The prototype's wire: horizontal out of the port, a quarter-turn of
    ///     radius r, vertical, a quarter-turn, horizontal into the target — the
    ///     elbow sits at the midpoint, held clear of both ends. <paramref name="stop" />
    ///     is how far before the target the line ends (the arrowhead's length).
    /// </summary>
    internal static StreamGeometry Elbow(Point from, Point to, double stop)
    {
        var dx = to.X - from.X;
        var dy = to.Y - from.Y;
        var geometry = new StreamGeometry();
        using var ctx = geometry.Open();
        ctx.BeginFigure(from, false);
        if (Math.Abs(dy) < 0.5)
        {
            ctx.LineTo(new Point(to.X - stop, to.Y));
        }
        else
        {
            var sign = Math.Sign(dy);
            var tail = Math.Min(MaxTail, Math.Max(dx - 4, 0) / 2);
            var r = Math.Max(0, Math.Min(MaxRadius, Math.Min(Math.Abs(dy) / 2, (dx - tail) / 2)));
            var elbow = Math.Min(Math.Max((from.X + to.X) / 2, from.X + r), to.X - tail - r);
            ctx.LineTo(new Point(elbow - r, from.Y));
            ctx.QuadraticBezierTo(new Point(elbow, from.Y), new Point(elbow, from.Y + sign * r));
            ctx.LineTo(new Point(elbow, to.Y - sign * r));
            ctx.QuadraticBezierTo(new Point(elbow, to.Y), new Point(elbow + r, to.Y));
            ctx.LineTo(new Point(to.X - stop, to.Y));
        }
        ctx.EndFigure(false);
        return geometry;
    }

    // the lanes move when a card is added or a lane grows; redraw after every layout
    private void OnLayoutUpdated(object? sender, EventArgs e) => InvalidateVisual();

    protected override void OnAttachedToVisualTree(VisualTreeAttachmentEventArgs e)
    {
        base.OnAttachedToVisualTree(e);
        LayoutUpdated += OnLayoutUpdated;
    }

    protected override void OnDetachedFromVisualTree(VisualTreeAttachmentEventArgs e)
    {
        base.OnDetachedFromVisualTree(e);
        LayoutUpdated -= OnLayoutUpdated;
    }
}
