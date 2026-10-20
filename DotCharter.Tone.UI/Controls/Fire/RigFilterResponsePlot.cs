using Avalonia;
using Avalonia.Media;
using DotCharter.Tone.UI.Controls.Dsp;

namespace DotCharter.Tone.UI.Controls.Fire;

/// <summary>
///     The cabinet's low/high-cut response guide (design 6.4): a log-frequency
///     grid from 20 Hz to 20 kHz over 0 … −24 dB, and the filter curve for the
///     two cut frequencies (<see cref="CabinetFilterMath.FilterGainDb" />). Drawn,
///     never interactive: the knobs it reflects sit beside it.
/// </summary>
public sealed class RigFilterResponsePlot : FireDrawnControl
{
    public static readonly StyledProperty<double> LowCutHzProperty =
        AvaloniaProperty.Register<RigFilterResponsePlot, double>(nameof(LowCutHz), 60);

    public static readonly StyledProperty<double> HighCutHzProperty =
        AvaloniaProperty.Register<RigFilterResponsePlot, double>(nameof(HighCutHz), 8000);

    public double LowCutHz { get => GetValue(LowCutHzProperty); set => SetValue(LowCutHzProperty, value); }
    public double HighCutHz { get => GetValue(HighCutHzProperty); set => SetValue(HighCutHzProperty, value); }

    static RigFilterResponsePlot()
    {
        IsHitTestVisibleProperty.OverrideDefaultValue<RigFilterResponsePlot>(false);
        AffectsRender<RigFilterResponsePlot>(LowCutHzProperty, HighCutHzProperty);
    }

    /// <summary>The curve's points (x fraction 0..1 across the log axis, gain dB), for tests.</summary>
    public IReadOnlyList<(double X, double GainDb)> Curve()
    {
        var points = new List<(double, double)>(101);
        for (var i = 0; i <= 100; i++)
        {
            var f = 20.0 * Math.Pow(1000.0, i / 100.0);
            points.Add((i / 100.0, CabinetFilterMath.FilterGainDb(f, LowCutHz, HighCutHz)));
        }
        return points;
    }

    public override void Render(DrawingContext context)
    {
        base.Render(context);
        if (Bounds.Width < 20 || Bounds.Height < 20) return;

        var plot = new Rect(Bounds.Size).Deflate(new Thickness(4, 6, 4, 6));
        var grid = Paint.Pen(FireUnitState.At(Paint.Color(this, "FireHardwareHighlight"), 0.28), 0.5);
        var edge = Paint.Pen(FireUnitState.At(Paint.Color(this, "FireHardwareHighlight"), 0.5), 1);
        context.DrawRectangle(Paint.Brush(FireUnitState.At(Paint.Color(this, "FireHardwareShadow"), 0.5)), edge, plot);

        // vertical grid at the decades and the 2/5 steps, horizontal every 6 dB
        foreach (var f in new[] { 50.0, 100, 200, 500, 1000, 2000, 5000, 10000 })
        {
            var x = plot.X + plot.Width * Math.Log(f / 20.0) / Math.Log(1000.0);
            context.DrawLine(grid, new Point(x, plot.Y), new Point(x, plot.Bottom));
        }
        for (var db = -6; db >= -24; db -= 6)
        {
            var y = plot.Y + plot.Height * (-db / 24.0);
            context.DrawLine(grid, new Point(plot.X, y), new Point(plot.Right, y));
        }

        var geometry = new StreamGeometry();
        using (var ctx = geometry.Open())
        {
            var first = true;
            foreach (var (fx, gain) in Curve())
            {
                var p = new Point(plot.X + fx * plot.Width, plot.Y + plot.Height * Math.Min(1, Math.Max(0, -gain / 24.0)));
                if (first) { ctx.BeginFigure(p, false); first = false; }
                else
                {
                    ctx.LineTo(p);
                }
            }
            ctx.EndFigure(false);
        }

        var accent = Paint.Color(this, "FireUnitCabinets");
        context.DrawGeometry(null, Paint.Pen(FireUnitState.At(accent, 0.30), 6, PenLineCap.Round), geometry);
        context.DrawGeometry(null, Paint.Pen(accent, 2.2, PenLineCap.Round), geometry);
    }
}
