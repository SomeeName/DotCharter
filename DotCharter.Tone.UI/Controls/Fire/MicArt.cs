using Avalonia;
using Avalonia.Media;

namespace DotCharter.Tone.UI.Controls.Fire;

/// <summary>
///     The drawn hardware of the mic placement: a speaker seen from the front and
///     from the side, and the five microphones the native mic model knows — a
///     dynamic 57, a ribbon 121, a large dynamic 421, a small condenser and a
///     large-diaphragm tube condenser — each
///     from the front (on the cone) and from the side (on the distance ruler and
///     the picker cards). Every colour is a hardware token or a shade of one, so
///     the art follows the theme like the rest of the rig.
/// </summary>
internal static class MicArt
{
    /// <summary>The mic model's index → the drawn shape.</summary>
    public enum Mic { Dynamic57 = 0, Ribbon121 = 1, Dynamic421 = 2, Condenser = 3, TubeCondenser = 4 }

    public static Mic MicFor(int index) => index switch
    {
        1 => Mic.Ribbon121,
        2 => Mic.Dynamic421,
        3 => Mic.Condenser,
        4 => Mic.TubeCondenser,
        _ => Mic.Dynamic57
    };

    public readonly record struct Palette(Color Shadow, Color Edge, Color Highlight, Color Chrome, Color Grille, Color Accent, Color Ink);

    public static Color Mix(Color a, Color b, double t) => Color.FromArgb(
        (byte)(a.A + (b.A - a.A) * t), (byte)(a.R + (b.R - a.R) * t),
        (byte)(a.G + (b.G - a.G) * t), (byte)(a.B + (b.B - a.B) * t));

    public static Color Alpha(Color c, double a) => Color.FromArgb((byte)Math.Clamp(a * 255, 0, 255), c.R, c.G, c.B);

    private static RadialGradientBrush Radial(double cx, double cy, double radius, params (double Offset, Color Color)[] stops)
    {
        var brush = new RadialGradientBrush
        {
            Center = new RelativePoint(cx, cy, RelativeUnit.Relative),
            GradientOrigin = new RelativePoint(cx, cy, RelativeUnit.Relative),
            RadiusX = new RelativeScalar(radius, RelativeUnit.Relative),
            RadiusY = new RelativeScalar(radius, RelativeUnit.Relative)
        };
        foreach (var (offset, color) in stops) brush.GradientStops.Add(new GradientStop(color, offset));
        return brush;
    }

    private static LinearGradientBrush Linear(RelativePoint from, RelativePoint to, params (double Offset, Color Color)[] stops)
    {
        var brush = new LinearGradientBrush { StartPoint = from, EndPoint = to };
        foreach (var (offset, color) in stops) brush.GradientStops.Add(new GradientStop(color, offset));
        return brush;
    }

    private static RelativePoint Rel(double x, double y) => new(x, y, RelativeUnit.Relative);

    // ── the speaker, from the front ────────────────────────────────────────────

    /// <summary>Draws a 12" speaker filling a circle of <paramref name="radius" />
    /// (the basket's outer rim) and returns the cone's radius — the circle the
    /// capsule position is measured against.</summary>
    public static double DrawSpeakerFront(DrawingContext context, Point c, double radius, Palette p)
    {
        // The basket's cast rim with its four mounting bolts.
        context.DrawEllipse(Radial(0.4, 0.3, 0.8, (0, Mix(p.Highlight, p.Chrome, 0.4)), (0.6, p.Edge), (1, p.Shadow)),
            new Pen(new SolidColorBrush(p.Shadow), 1.5), c, radius, radius);
        for (var i = 0; i < 4; i++)
        {
            var a = (45 + i * 90) * Math.PI / 180;
            var bolt = new Point(c.X + radius * 0.955 * Math.Sin(a), c.Y - radius * 0.955 * Math.Cos(a));
            var br = radius * 0.028;
            context.DrawEllipse(Radial(0.35, 0.3, 0.9, (0, p.Chrome), (1, p.Edge)), new Pen(new SolidColorBrush(p.Shadow), 1), bolt, br, br);
        }

        // The surround: a rubber roll, lit from the top left.
        var surroundOuter = radius * 0.91;
        var coneRadius = radius * 0.80;
        context.DrawEllipse(Radial(0.42, 0.36, 0.62,
                (0.80, Mix(p.Shadow, p.Edge, 0.55)), (0.88, Mix(p.Edge, p.Highlight, 0.25)), (0.96, Mix(p.Shadow, p.Edge, 0.35)), (1, p.Shadow)),
            null, c, surroundOuter, surroundOuter);

        // The paper cone: darker toward the centre, with pressed rings.
        context.DrawEllipse(Radial(0.44, 0.38, 0.66,
                (0, Mix(p.Shadow, p.Grille, 0.35)), (0.55, Mix(p.Shadow, p.Grille, 0.85)), (1, Mix(p.Grille, p.Edge, 0.35))),
            new Pen(new SolidColorBrush(p.Shadow), 1.2), c, coneRadius, coneRadius);
        var ring = new Pen(new SolidColorBrush(Alpha(p.Highlight, 0.10)), 1);
        foreach (var f in new[] { 0.45, 0.58, 0.71, 0.84, 0.95 })
            context.DrawEllipse(null, ring, c, coneRadius * f, coneRadius * f);

        // The dust cap: a lit dome.
        var cap = coneRadius * Dsp.MicPlacementMath.DustCapFraction;
        context.DrawEllipse(Radial(0.36, 0.30, 0.75,
                (0, Mix(p.Highlight, p.Chrome, 0.2)), (0.45, Mix(p.Edge, p.Grille, 0.5)), (1, p.Shadow)),
            new Pen(new SolidColorBrush(Alpha(p.Shadow, 0.9)), 1.2), c, cap, cap);
        context.DrawEllipse(new SolidColorBrush(Alpha(p.Chrome, 0.35)), null,
            new Point(c.X - cap * 0.32, c.Y - cap * 0.36), cap * 0.22, cap * 0.14);
        return coneRadius;
    }

    // ── a microphone, from the front (on the cone) ────────────────────────────

    /// <summary>A mic seen along its axis, capsule centred on <paramref name="c" />,
    /// <paramref name="size" /> across. <paramref name="armDegrees" /> is the way its
    /// body and stand run off (the mic comes in from outside the speaker).</summary>
    public static void DrawMicFront(DrawingContext context, Mic mic, Point c, double size, double armDegrees,
        double shadowOffset, Palette p, bool lit)
    {
        var r = size / 2;
        var a = armDegrees * Math.PI / 180;
        var dir = new Vector(Math.Sin(a), -Math.Cos(a));

        // The shadow on the cone, falling away from the key light.
        using (context.PushOpacity(0.55))
        {
            context.DrawEllipse(new SolidColorBrush(p.Shadow), null,
                new Point(c.X + size * shadowOffset * 0.8, c.Y + size * shadowOffset), r * 1.05, r * 1.05);
        }

        // The body and the stand arm, running off behind the capsule.
        var tail = c + dir * (size * 2.4);
        context.DrawLine(new Pen(new SolidColorBrush(p.Shadow), r * 0.9, lineCap: PenLineCap.Round), c, tail);
        context.DrawLine(new Pen(new SolidColorBrush(Mix(p.Shadow, p.Edge, 0.6)), r * 0.62, lineCap: PenLineCap.Round), c, tail);
        context.DrawLine(new Pen(new SolidColorBrush(Alpha(p.Highlight, 0.25)), r * 0.14, lineCap: PenLineCap.Round),
            c + new Vector(-dir.Y, dir.X) * (r * 0.18), tail + new Vector(-dir.Y, dir.X) * (r * 0.18));

        var chromeRim = Radial(0.38, 0.30, 0.85, (0, p.Chrome), (0.55, p.Highlight), (1, p.Edge));
        var rimPen = new Pen(new SolidColorBrush(p.Shadow), 1.2);
        switch (mic)
        {
            case Mic.Dynamic57:
                // A black body ending in a ball of chrome mesh, seen head-on.
                context.DrawEllipse(chromeRim, rimPen, c, r, r);
                context.DrawEllipse(Radial(0.42, 0.36, 0.7, (0, Mix(p.Grille, p.Highlight, 0.35)), (1, p.Shadow)), null, c, r * 0.8, r * 0.8);
                Mesh(context, new Rect(c.X - r * 0.8, c.Y - r * 0.8, r * 1.6, r * 1.6), r * 0.8, c, r * 0.16, p);
                break;
            case Mic.Dynamic421:
                // The long black head with its horizontal slots.
                var head = new Rect(c.X - r * 1.15, c.Y - r * 0.8, r * 2.3, r * 1.6);
                context.DrawRectangle(Linear(Rel(0, 0), Rel(0, 1), (0, Mix(p.Shadow, p.Edge, 0.9)), (1, p.Shadow)), rimPen, head, r * 0.55, r * 0.55);
                var slot = new Pen(new SolidColorBrush(Alpha(p.Highlight, 0.45)), Math.Max(1, r * 0.08), lineCap: PenLineCap.Round);
                for (var i = -2; i <= 2; i++)
                    context.DrawLine(slot, new Point(c.X - r * 0.8, c.Y + i * r * 0.26), new Point(c.X + r * 0.8, c.Y + i * r * 0.26));
                break;
            case Mic.Ribbon121:
                // A tall chrome-framed box with vertical slots over the ribbon.
                var box = new Rect(c.X - r * 0.72, c.Y - r * 1.1, r * 1.44, r * 2.2);
                context.DrawRectangle(chromeRim, rimPen, box, r * 0.3, r * 0.3);
                var inner = box.Deflate(r * 0.16);
                context.DrawRectangle(new SolidColorBrush(Mix(p.Shadow, p.Grille, 0.5)), null, inner, r * 0.18, r * 0.18);
                var bar = new Pen(new SolidColorBrush(Alpha(p.Highlight, 0.35)), Math.Max(1, r * 0.07));
                for (var i = -3; i <= 3; i++)
                    context.DrawLine(bar, new Point(c.X + i * r * 0.16, inner.Top + r * 0.1), new Point(c.X + i * r * 0.16, inner.Bottom - r * 0.1));
                break;
            case Mic.TubeCondenser:
                // The large-diaphragm tube mic: a wide chrome headbasket, fine
                // mesh, the big diaphragm a dark disc behind it.
                context.DrawEllipse(chromeRim, rimPen, c, r * 1.08, r * 1.08);
                context.DrawEllipse(Radial(0.42, 0.36, 0.75, (0, Mix(p.Grille, p.Highlight, 0.3)), (1, p.Shadow)), null, c, r * 0.9, r * 0.9);
                Mesh(context, new Rect(c.X - r * 0.9, c.Y - r * 0.9, r * 1.8, r * 1.8), r * 0.9, c, r * 0.12, p);
                context.DrawEllipse(null, new Pen(new SolidColorBrush(Alpha(p.Accent, 0.55)), Math.Max(1, r * 0.06)), c, r * 0.55, r * 0.55);
                break;
            default:
                // The small pencil condenser: a chrome tube, the capsule a dark disc.
                context.DrawEllipse(chromeRim, rimPen, c, r * 0.62, r * 0.62);
                context.DrawEllipse(Radial(0.4, 0.35, 0.8, (0, Mix(p.Grille, p.Highlight, 0.5)), (1, p.Shadow)), null, c, r * 0.42, r * 0.42);
                context.DrawEllipse(new SolidColorBrush(Alpha(p.Chrome, 0.6)), null, new Point(c.X - r * 0.12, c.Y - r * 0.14), r * 0.1, r * 0.07);
                break;
        }

        if (lit)
            context.DrawEllipse(null, new Pen(new SolidColorBrush(p.Accent), 2), c, r * 1.28, r * 1.28);
    }

    private static void Mesh(DrawingContext context, Rect bounds, double radius, Point c, double pitch, Palette p)
    {
        var dot = new SolidColorBrush(Alpha(p.Chrome, 0.35));
        for (var y = bounds.Top + pitch / 2; y < bounds.Bottom; y += pitch)
        {
            for (var x = bounds.Left + pitch / 2; x < bounds.Right; x += pitch)
            {
                var dx = x - c.X;
                var dy = y - c.Y;
                if (dx * dx + dy * dy < radius * radius * 0.85)
                    context.DrawEllipse(dot, null, new Point(x, y), pitch * 0.22, pitch * 0.22);
            }
        }
    }

    // ── a microphone, from the side (the distance ruler, the picker) ──────────

    /// <summary>A mic lying horizontally with its capsule's FRONT at
    /// <paramref name="front" /> (pointing left, at the speaker), <paramref name="height" />
    /// tall. Returns its drawn length.</summary>
    public static double DrawMicSide(DrawingContext context, Mic mic, Point front, double height, Palette p, bool lit)
    {
        var h = height;
        var cy = front.Y;
        var chrome = Linear(Rel(0, 0), Rel(0, 1), (0, p.Highlight), (0.45, p.Chrome), (1, p.Edge));
        var black = Linear(Rel(0, 0), Rel(0, 1), (0, Mix(p.Edge, p.Highlight, 0.2)), (0.4, Mix(p.Shadow, p.Edge, 0.7)), (1, p.Shadow));
        var edge = new Pen(new SolidColorBrush(p.Shadow), 1);
        double length;
        switch (mic)
        {
            case Mic.Dynamic57:
            {
                // grille (a rounded chrome nose), then a long tapering black body
                var grille = new Rect(front.X, cy - h * 0.42, h * 1.1, h * 0.84);
                context.DrawRectangle(chrome, edge, grille, h * 0.42, h * 0.42);
                Hatch(context, grille.Deflate(h * 0.08), p);
                var body = new StreamGeometry();
                using (var g = body.Open())
                {
                    g.BeginFigure(new Point(front.X + h * 0.95, cy - h * 0.36), true);
                    g.LineTo(new Point(front.X + h * 4.6, cy - h * 0.24));
                    g.LineTo(new Point(front.X + h * 4.6, cy + h * 0.24));
                    g.LineTo(new Point(front.X + h * 0.95, cy + h * 0.36));
                    g.EndFigure(true);
                }
                context.DrawGeometry(black, edge, body);
                length = h * 4.6;
                break;
            }
            case Mic.Dynamic421:
            {
                // a big flat head with slots and a fat body
                var head = new Rect(front.X, cy - h * 0.5, h * 1.9, h);
                context.DrawRectangle(black, edge, head, h * 0.3, h * 0.3);
                var slot = new Pen(new SolidColorBrush(Alpha(p.Highlight, 0.45)), Math.Max(1, h * 0.06));
                for (var i = 1; i <= 5; i++)
                    context.DrawLine(slot, new Point(front.X + h * 0.28 * i, cy - h * 0.34), new Point(front.X + h * 0.28 * i, cy + h * 0.34));
                context.DrawRectangle(black, edge, new Rect(front.X + h * 1.85, cy - h * 0.34, h * 2.3, h * 0.68), h * 0.1, h * 0.1);
                length = h * 4.15;
                break;
            }
            case Mic.Ribbon121:
            {
                // a flat box on its side with its slotted face toward the speaker
                var box = new Rect(front.X, cy - h * 0.5, h * 0.9, h);
                context.DrawRectangle(chrome, edge, box, h * 0.12, h * 0.12);
                var bar = new Pen(new SolidColorBrush(Alpha(p.Shadow, 0.6)), Math.Max(1, h * 0.05));
                for (var i = 1; i <= 3; i++)
                    context.DrawLine(bar, new Point(front.X + h * 0.22 * i, cy - h * 0.38), new Point(front.X + h * 0.22 * i, cy + h * 0.38));
                context.DrawRectangle(black, edge, new Rect(front.X + h * 0.88, cy - h * 0.2, h * 2.6, h * 0.4), h * 0.1, h * 0.1);
                length = h * 3.5;
                break;
            }
            case Mic.TubeCondenser:
            {
                // a wide rounded headbasket over a tapered tube body
                var basket = new Rect(front.X, cy - h * 0.55, h * 1.25, h * 1.1);
                context.DrawRectangle(chrome, edge, basket, h * 0.5, h * 0.5);
                Hatch(context, basket.Deflate(h * 0.12), p);
                var body = new StreamGeometry();
                using (var g = body.Open())
                {
                    g.BeginFigure(new Point(front.X + h * 1.2, cy - h * 0.42), true);
                    g.LineTo(new Point(front.X + h * 4.3, cy - h * 0.3));
                    g.LineTo(new Point(front.X + h * 4.3, cy + h * 0.3));
                    g.LineTo(new Point(front.X + h * 1.2, cy + h * 0.42));
                    g.EndFigure(true);
                }
                context.DrawGeometry(chrome, edge, body);
                length = h * 4.3;
                break;
            }
            default:
            {
                // a slim chrome pencil with a small capsule
                context.DrawRectangle(chrome, edge, new Rect(front.X, cy - h * 0.2, h * 0.5, h * 0.4), h * 0.12, h * 0.12);
                context.DrawRectangle(chrome, edge, new Rect(front.X + h * 0.45, cy - h * 0.16, h * 3.3, h * 0.32), h * 0.08, h * 0.08);
                context.DrawLine(new Pen(new SolidColorBrush(Alpha(p.Shadow, 0.5)), 1), new Point(front.X + h * 0.5, cy - h * 0.16), new Point(front.X + h * 0.5, cy + h * 0.16));
                length = h * 3.75;
                break;
            }
        }

        if (lit)
        {
            context.DrawLine(new Pen(new SolidColorBrush(p.Accent), 2, lineCap: PenLineCap.Round),
                new Point(front.X, cy + h * 0.62), new Point(front.X + length, cy + h * 0.62));
        }

        return length;
    }

    private static void Hatch(DrawingContext context, Rect r, Palette p)
    {
        var pen = new Pen(new SolidColorBrush(Alpha(p.Shadow, 0.35)), 1);
        for (var x = r.Left + 3; x < r.Right; x += 3.5) context.DrawLine(pen, new Point(x, r.Top), new Point(x, r.Bottom));
    }

    // ── the speaker, from the side ─────────────────────────────────────────────

    /// <summary>A speaker's profile standing at <paramref name="baffleX" />: the
    /// baffle and grille cloth as a panel, the cone's curve behind it.</summary>
    public static void DrawSpeakerSide(DrawingContext context, double baffleX, double top, double bottom, Palette p)
    {
        var h = bottom - top;
        var cy = top + h / 2;
        // the cabinet's panel and the grille cloth in front of it
        context.DrawRectangle(Linear(Rel(0, 0), Rel(1, 0), (0, p.Shadow), (1, Mix(p.Shadow, p.Edge, 0.6))), null,
            new Rect(baffleX - h * 0.28, top, h * 0.24, h));
        context.DrawRectangle(new SolidColorBrush(Mix(p.Grille, p.Edge, 0.3)), null, new Rect(baffleX - h * 0.05, top, h * 0.05, h));
        // the cone's profile: a shallow bowl opening toward the mic
        var cone = new StreamGeometry();
        using (var g = cone.Open())
        {
            g.BeginFigure(new Point(baffleX - h * 0.05, top + h * 0.12), false);
            g.CubicBezierTo(new Point(baffleX - h * 0.2, cy - h * 0.15), new Point(baffleX - h * 0.2, cy + h * 0.15),
                new Point(baffleX - h * 0.05, bottom - h * 0.12));
            g.EndFigure(false);
        }
        context.DrawGeometry(null, new Pen(new SolidColorBrush(Alpha(p.Highlight, 0.45)), 1.5), cone);
        context.DrawEllipse(new SolidColorBrush(Alpha(p.Highlight, 0.35)), null, new Point(baffleX - h * 0.17, cy), h * 0.03, h * 0.08);
    }
}
