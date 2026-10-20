using SkiaSharp;

namespace DotCharter.Tests.Perf.Guide;

/// <summary>
///     Draws the usage guide's callouts onto a captured frame (#60): a thin outline round each
///     target, a numbered round marker beside it and a short leader from the marker to the
///     outline. Each marker is placed where it covers the least TEXT (the caller hands in every
///     visible text run's box), never over another marker, and inside the picture. Then the frame
///     is cut to <c>crop</c> and written as PNG. Pure drawing: it knows nothing of Avalonia.
/// </summary>
internal static class GuideAnnotator
{
    private const float Radius = 13f;
    private const float StrokeWidth = 2.5f;

    private static readonly SKColor Accent = new(0xFF, 0xB0, 0x20);
    private static readonly SKColor Ink = new(0x1A, 0x12, 0x06);
    private static readonly SKColor Halo = new(0x0B, 0x08, 0x10, 0xD0);

    private static readonly float[] Distances = [30f, 46f, 66f, 92f];

    public sealed record Target(int Number, SKRect Box, string? Side = null);

    public static byte[] Annotate(byte[] png, IReadOnlyList<Target> targets, IReadOnlyList<SKRect> text, SKRectI crop)
    {
        using var bitmap = SKBitmap.Decode(png);
        using var canvas = new SKCanvas(bitmap);
        var bounds = SKRect.Create(crop.Left + 2, crop.Top + 2, crop.Width - 4, crop.Height - 4);
        var placed = new List<SKRect>();
        var plans = new List<(Target Target, SKPoint Marker, SKPoint? Anchor)>();
        foreach (var target in targets)
        {
            var (marker, anchor) = Place(target.Box, targets, text, placed, bounds);
            placed.Add(Circle(marker, Radius + 4));
            plans.Add((target, marker, anchor));
        }

        foreach (var (target, _, _) in plans) DrawOutline(canvas, target.Box);
        foreach (var (_, marker, anchor) in plans)
        {
            if (anchor is { } end) DrawLeader(canvas, marker, end);
        }

        foreach (var (target, marker, _) in plans) DrawMarker(canvas, marker, target.Number);
        canvas.Flush();

        using var cut = new SKBitmap(crop.Width, crop.Height);
        bitmap.ExtractSubset(cut, crop);
        using var image = SKImage.FromBitmap(cut);
        using var data = image.Encode(SKEncodedImageFormat.Png, 100);
        return data.ToArray();
    }

    /// <summary>The marker's centre and, when it sits outside the box, the point its leader ends on.</summary>
    private static (SKPoint Marker, SKPoint? Anchor) Place(SKRect box, IReadOnlyList<Target> all, IReadOnlyList<SKRect> text,
        List<SKRect> placed, SKRect bounds)
    {
        var best = (Marker: new SKPoint(box.Left, box.Top), Anchor: (SKPoint?)null);
        var bestScore = double.MaxValue;
        var side = all.First(t => t.Box == box).Side;
        foreach (var (marker, anchor, extra) in Candidates(box, side))
        {
            var disc = Circle(marker, Radius + 2);
            if (!bounds.Contains(disc)) continue;
            var score = extra;
            foreach (var other in placed)
            {
                if (other.IntersectsWith(disc)) score += 1_000_000;
            }

            foreach (var run in text) score += Overlap(run, disc) * 4;
            foreach (var other in all)
            {
                if (other.Box != box) score += Overlap(other.Box, disc) * 0.5;
            }

            if (anchor is { } end)
            {
                foreach (var other in placed)
                {
                    if (Crosses(other, marker, end)) score += 5_000;
                }
            }

            if (score < bestScore)
            {
                bestScore = score;
                best = (marker, anchor);
            }
        }

        return best;
    }

    /// <summary>Outside the box first, in eight directions at growing distances; inside its
    /// corners when it is big enough to hold a marker (a pane, the timeline).</summary>
    private static IEnumerable<(SKPoint Marker, SKPoint? Anchor, double Cost)> Candidates(SKRect box, string? side)
    {
        (float X, float Y)[] directions = side switch
        {
            "left" => [(-1, 0)],
            "right" => [(1, 0)],
            "top" => [(0, -1)],
            "bottom" => [(0, 1)],
            "inside" => [],
            _ => [(-1, -1), (0, -1), (1, -1), (1, 0), (1, 1), (0, 1), (-1, 1), (-1, 0)]
        };
        foreach (var distance in Distances)
        {
            foreach (var (dx, dy) in directions)
            {
                var anchor = new SKPoint(
                    dx < 0 ? box.Left : dx > 0 ? box.Right : box.MidX,
                    dy < 0 ? box.Top : dy > 0 ? box.Bottom : box.MidY);
                var length = dx != 0 && dy != 0 ? distance * 0.7071f : distance;
                var marker = new SKPoint(anchor.X + (dx * length), anchor.Y + (dy * length));
                yield return (marker, anchor, (distance * 6) + (dx != 0 && dy != 0 ? 60 : 0));
            }
        }

        if (side is not null and not "inside") yield break;

        // Along the middle of a tall or wide box (a rail, a strip) where its own content leaves room.
        if (Math.Min(box.Width, box.Height) >= (Radius * 2) + 4 && Math.Max(box.Width, box.Height) >= 60)
        {
            for (var f = 0.25f; f < 0.8f; f += 0.25f)
            {
                var point = box.Height >= box.Width
                    ? new SKPoint(box.MidX, box.Top + (box.Height * f))
                    : new SKPoint(box.Left + (box.Width * f), box.MidY);
                yield return (point, null, 240);
            }
        }

        if (box.Width < 120 || box.Height < 80) yield break;
        const float inset = Radius + 10;
        // Top-right first: a pane's own controls and labels usually start at its top-left, and what
        // the GPU draws inside a pane (the timeline, the highway) is invisible to the text scan.
        foreach (var (x, y, cost) in new[] { (box.Right - inset, box.Top + inset, 200), (box.Right - inset, box.Bottom - inset, 210),
                     (box.Left + inset, box.Top + inset, 220), (box.Left + inset, box.Bottom - inset, 230) })
        {
            yield return (new SKPoint(x, y), null, cost);
        }
    }

    private static void DrawOutline(SKCanvas canvas, SKRect box)
    {
        var frame = SKRect.Inflate(box, 2, 2);
        using var halo = new SKPaint { Color = Halo, Style = SKPaintStyle.Stroke, StrokeWidth = StrokeWidth + 2, IsAntialias = true };
        using var line = new SKPaint { Color = Accent, Style = SKPaintStyle.Stroke, StrokeWidth = 2f, IsAntialias = true };
        canvas.DrawRoundRect(frame, 5, 5, halo);
        canvas.DrawRoundRect(frame, 5, 5, line);
    }

    private static void DrawLeader(SKCanvas canvas, SKPoint marker, SKPoint end)
    {
        var dx = end.X - marker.X;
        var dy = end.Y - marker.Y;
        var length = MathF.Sqrt((dx * dx) + (dy * dy));
        if (length <= Radius + 2) return;
        var start = new SKPoint(marker.X + (dx / length * Radius), marker.Y + (dy / length * Radius));
        using var halo = new SKPaint { Color = Halo, StrokeWidth = StrokeWidth + 2.5f, IsAntialias = true, StrokeCap = SKStrokeCap.Round };
        using var line = new SKPaint { Color = Accent, StrokeWidth = StrokeWidth, IsAntialias = true, StrokeCap = SKStrokeCap.Round };
        canvas.DrawLine(start, end, halo);
        canvas.DrawLine(start, end, line);
        using var dot = new SKPaint { Color = Accent, IsAntialias = true };
        canvas.DrawCircle(end, 3.5f, dot);
    }

    private static void DrawMarker(SKCanvas canvas, SKPoint centre, int number)
    {
        using var shadow = new SKPaint { Color = Halo, IsAntialias = true };
        using var fill = new SKPaint { Color = Accent, IsAntialias = true };
        using var rim = new SKPaint { Color = Ink, Style = SKPaintStyle.Stroke, StrokeWidth = 1.5f, IsAntialias = true };
        canvas.DrawCircle(centre.X, centre.Y + 1, Radius + 2, shadow);
        canvas.DrawCircle(centre, Radius, fill);
        canvas.DrawCircle(centre, Radius, rim);

        using var typeface = SKTypeface.FromFamilyName("Segoe UI", SKFontStyle.Bold) ?? SKTypeface.Default;
        using var font = new SKFont(typeface, number >= 10 ? 13f : 15f);
        using var ink = new SKPaint { Color = Ink, IsAntialias = true };
        var label = number.ToString(System.Globalization.CultureInfo.InvariantCulture);
        font.MeasureText(label, out var glyphs);
        canvas.DrawText(label, centre.X - glyphs.MidX, centre.Y - glyphs.MidY, SKTextAlign.Left, font, ink);
    }

    private static SKRect Circle(SKPoint centre, float radius) =>
        new(centre.X - radius, centre.Y - radius, centre.X + radius, centre.Y + radius);

    private static double Overlap(SKRect a, SKRect b)
    {
        var cut = SKRect.Intersect(a, b);
        return cut.IsEmpty ? 0 : cut.Width * cut.Height;
    }

    /// <summary>Whether the segment from <paramref name="from" /> to <paramref name="to" /> passes through <paramref name="box" /> (sampled).</summary>
    private static bool Crosses(SKRect box, SKPoint from, SKPoint to)
    {
        for (var i = 1; i < 10; i++)
        {
            var t = i / 10f;
            if (box.Contains(from.X + ((to.X - from.X) * t), from.Y + ((to.Y - from.Y) * t))) return true;
        }

        return false;
    }
}
