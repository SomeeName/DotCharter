using SkiaSharp;

namespace DotCharter.Rendering.Text;

/// <summary>
///     The bend labels' two arrows ("↗" bend, "↘" release), drawn as VECTOR strokes into a glyph sheet
///     instead of taken from the font. Fonts that lack the glyph fall back to a symbol face whose arrow
///     is a faint speck beside the capitals; this one spans the text's cap height and is as solid as a
///     letter stem. Both atlases (2D timeline <see cref="ChartFontAtlas" />, 3D <c>Vulkan.FontAtlas</c>)
///     call it, so the two surfaces cannot drift.
/// </summary>
public static class BendArrowGlyph
{
    /// <summary>The characters <see cref="Draw" /> owns.</summary>
    public static bool IsArrow(char c) => c is '↗' or '↘';

    /// <summary>The font's capital height in pixels (a fraction of the size when the face reports none).</summary>
    public static float CapHeightOf(SKFont font)
    {
        var cap = font.Metrics.CapHeight;
        return cap > 0f ? cap : font.Size * 0.72f;
    }

    /// <summary>Pen advance of an arrow glyph: its square box plus a small gap before the amount text.</summary>
    public static float Advance(float capHeight) => capHeight * 1.15f;

    /// <summary>
    ///     Draws the arrow in white into the square box whose left edge is <paramref name="left" /> and whose
    ///     bottom sits on <paramref name="baselineY" />; the ink fills the box, so it is
    ///     <paramref name="capHeight" /> tall and wide.
    /// </summary>
    public static void Draw(SKCanvas canvas, char c, float left, float baselineY, float capHeight)
    {
        var stroke = MathF.Max(1.5f, capHeight * 0.16f);
        var inset = stroke * 0.5f;
        var head = capHeight * 0.5f;
        var up = c == '↗';
        var tail = new SKPoint(left + inset, up ? baselineY - inset : baselineY - capHeight + inset);
        var tip = new SKPoint(left + capHeight - inset, up ? baselineY - capHeight + inset : baselineY - inset);
        var vertical = new SKPoint(tip.X, up ? tip.Y + head : tip.Y - head);

        using var paint = new SKPaint
        {
            Color = SKColors.White,
            IsAntialias = true,
            Style = SKPaintStyle.Stroke,
            StrokeWidth = stroke,
            StrokeCap = SKStrokeCap.Round,
            StrokeJoin = SKStrokeJoin.Round
        };
        canvas.DrawLine(tail, tip, paint);
        canvas.DrawLine(new SKPoint(tip.X - head, tip.Y), tip, paint);
        canvas.DrawLine(tip, vertical, paint);
    }
}
