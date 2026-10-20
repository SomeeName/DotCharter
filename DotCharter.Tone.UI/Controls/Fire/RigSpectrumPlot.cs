using Avalonia;
using Avalonia.Media;

namespace DotCharter.Tone.UI.Controls.Fire;

/// <summary>
/// A node's pre/post spectrum: log-spaced bands from 20 Hz to 20 kHz
///     over <see cref="FloorDb" /> … 0 dBFS. <see cref="Bands" /> is the tap the
///     player picked, drawn bright; <see cref="ReferenceBands" /> is the other one,
///     drawn faint, so what the unit DOES is the gap between the two lines. Drawn,
///     never interactive.
/// </summary>
public sealed class RigSpectrumPlot : FireDrawnControl
{
    /// <summary>The bottom of the plot. A guitar phrase at -18 dBFS peak spends
    /// its energy between about -30 and -90 dB per band.</summary>
    public const double FloorDb = -96;

    public static readonly StyledProperty<double[]?> BandsProperty =
        AvaloniaProperty.Register<RigSpectrumPlot, double[]?>(nameof(Bands));

    public static readonly StyledProperty<double[]?> ReferenceBandsProperty =
        AvaloniaProperty.Register<RigSpectrumPlot, double[]?>(nameof(ReferenceBands));

    public double[]? Bands { get => GetValue(BandsProperty); set => SetValue(BandsProperty, value); }
    public double[]? ReferenceBands { get => GetValue(ReferenceBandsProperty); set => SetValue(ReferenceBandsProperty, value); }

    static RigSpectrumPlot()
    {
        IsHitTestVisibleProperty.OverrideDefaultValue<RigSpectrumPlot>(false);
        AffectsRender<RigSpectrumPlot>(BandsProperty, ReferenceBandsProperty);
    }

    /// <summary>Band <paramref name="index" /> of <paramref name="count" /> at
    /// <paramref name="db" /> as a point in <paramref name="plot" />.</summary>
    public static Point PointFor(Rect plot, int index, int count, double db)
    {
        var x = plot.X + plot.Width * (count <= 1 ? 0.5 : (double)index / (count - 1));
        var y = plot.Y + plot.Height * Math.Clamp(db / FloorDb, 0, 1);
        return new Point(x, y);
    }

    public override void Render(DrawingContext context)
    {
        base.Render(context);
        if (Bounds.Width < 20 || Bounds.Height < 20) return;

        var plot = new Rect(Bounds.Size).Deflate(new Thickness(4, 6, 4, 6));
        var grid = Paint.Pen(FireUnitState.At(Paint.Color(this, "FireHardwareHighlight"), 0.28), 0.5);
        var edge = Paint.Pen(FireUnitState.At(Paint.Color(this, "FireHardwareHighlight"), 0.5), 1);
        context.DrawRectangle(Paint.Brush(FireUnitState.At(Paint.Color(this, "FireHardwareShadow"), 0.5)), edge, plot);
        foreach (var f in new[] { 100.0, 1000, 10000 })
        {
            var x = plot.X + plot.Width * Math.Log(f / 20.0) / Math.Log(1000.0);
            context.DrawLine(grid, new Point(x, plot.Y), new Point(x, plot.Bottom));
        }
        for (var db = -24; db > FloorDb; db -= 24)
        {
            var y = plot.Y + plot.Height * (db / FloorDb);
            context.DrawLine(grid, new Point(plot.X, y), new Point(plot.Right, y));
        }

        var accent = Paint.Color(this, "FireUnitCabinets");
        if (Line(plot, ReferenceBands) is { } reference)
            context.DrawGeometry(null, Paint.Pen(FireUnitState.At(accent, 0.35), 1.2, PenLineCap.Round), reference);
        if (Line(plot, Bands) is { } bands)
            context.DrawGeometry(null, Paint.Pen(accent, 2, PenLineCap.Round), bands);
    }

    private static StreamGeometry? Line(Rect plot, double[]? bands)
    {
        if (bands is not { Length: > 1 }) return null;
        var geometry = new StreamGeometry();
        using var ctx = geometry.Open();
        ctx.BeginFigure(PointFor(plot, 0, bands.Length, bands[0]), false);
        for (var i = 1; i < bands.Length; i++) ctx.LineTo(PointFor(plot, i, bands.Length, bands[i]));
        ctx.EndFigure(false);
        return geometry;
    }
}
