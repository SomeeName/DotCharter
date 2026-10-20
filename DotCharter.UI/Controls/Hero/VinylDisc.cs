using Avalonia;
using Avalonia.Controls;
using Avalonia.Media;
using Avalonia.Media.Immutable;

namespace DotCharter.UI.Controls.Hero;

/// <summary>
///     The record peeking out from behind the hero's cover, as the mockup's <c>.vinyl</c> draws it:
///     grooves every 3 px (<c>#0c0d10</c> with a <c>#16181d</c> ring), the cover as a square label
///     34 % of the disc across, the spindle hole in the background colour, and a two-lobed sheen
///     (<c>.vinyl-spin</c>) turning once every 3.2 s. <see cref="HeroStage" /> sets
///     <see cref="SpinDegrees" /> from its clock.
/// </summary>
public sealed class VinylDisc : Control
{
    public static readonly StyledProperty<IImage?> LabelProperty =
        AvaloniaProperty.Register<VinylDisc, IImage?>(nameof(Label));

    public static readonly StyledProperty<double> SpinDegreesProperty =
        AvaloniaProperty.Register<VinylDisc, double>(nameof(SpinDegrees));

    private static readonly IBrush Groove = new ImmutableSolidColorBrush(Color.FromRgb(0x0c, 0x0d, 0x10));
    private static readonly IPen Ring = new ImmutablePen(new ImmutableSolidColorBrush(Color.FromRgb(0x16, 0x18, 0x1d)), 1);
    private static readonly IBrush Hole = new ImmutableSolidColorBrush(Color.FromRgb(17, 19, 23));

    static VinylDisc() => AffectsRender<VinylDisc>(LabelProperty, SpinDegreesProperty);

    public IImage? Label
    {
        get => GetValue(LabelProperty);
        set => SetValue(LabelProperty, value);
    }

    public double SpinDegrees
    {
        get => GetValue(SpinDegreesProperty);
        set => SetValue(SpinDegreesProperty, value);
    }

    public override void Render(DrawingContext context)
    {
        var size = Math.Min(Bounds.Width, Bounds.Height);
        if (size <= 0) return;
        var r = size / 2;
        var c = new Point(r, r);

        using (context.PushGeometryClip(new EllipseGeometry(new Rect(0, 0, size, size))))
        {
            context.DrawEllipse(Groove, null, c, r, r);
            for (var ring = 2.5; ring < r; ring += 3) context.DrawEllipse(null, Ring, c, ring, ring);

            if (Label is { } label)
            {
                var side = size * 0.34;
                var src = label is Avalonia.Media.Imaging.Bitmap b
                    ? new Rect(b.PixelSize.ToSize(1))
                    : new Rect(label.Size);
                context.DrawImage(label, src, new Rect(r - side / 2, r - side / 2, side, side));
            }

            // The hole: 3 % of the gradient ray, which for a circle is the distance to the box corner.
            var hole = 0.03 * r * Math.Sqrt(2);
            context.DrawEllipse(Hole, null, c, hole, hole);

            var sheen = new ConicGradientBrush
            {
                Angle = SpinDegrees,
                GradientStops =
                {
                    new GradientStop(Colors.Transparent, 0.20),
                    new GradientStop(Color.FromArgb(18, 255, 255, 255), 0.25),
                    new GradientStop(Colors.Transparent, 0.30),
                    new GradientStop(Colors.Transparent, 0.70),
                    new GradientStop(Color.FromArgb(18, 255, 255, 255), 0.75),
                    new GradientStop(Colors.Transparent, 0.80)
                }
            };
            context.DrawEllipse(sheen, null, c, r, r);
        }
    }
}
