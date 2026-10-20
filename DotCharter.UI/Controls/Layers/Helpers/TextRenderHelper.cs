using Avalonia.Media;
using DotCharter.Rendering.Timeline2D;
using DotCharter.Rendering.Text;
using DotCharter.UI.Constants;

namespace DotCharter.UI.Controls.Layers.Helpers;

public static class TextRenderHelper
{
    public static void DrawScaledText(TimelineBatcher batcher, string text, float x, float y, float scale, Color c,
        bool centerX = false, bool centerY = false)
    {
        var atlas = batcher.FontAtlas;
        if (atlas == null || string.IsNullOrEmpty(text)) return;

        if (centerX) x -= MeasureScaledTextWidth(batcher, text, scale) / 2f;
        if (centerY) y -= atlas.TextCenterYOffset * scale;

        var ascii = atlas.AsciiGlyphs;
        var valid = atlas.AsciiValid;
        var currentX = x;
        foreach (var ch in text)
        {
            GlyphInfo glyph;
            if (ch < 128)
            {
                if (!valid[ch]) continue;
                glyph = ascii[ch];
            }
            else if (!atlas.GlyphMap.TryGetValue(ch, out glyph))
            {
                continue;
            }

            // FIX: Removed Math.Round here as well to prevent "kerning jitter"
            // (letters wobbling apart from each other as they scroll).
            var renderX = currentX + glyph.OffsetX * scale;
            var gw = glyph.Width * scale;
            var gh = glyph.Height * scale;

            batcher.AddQuad(
                renderX, y,
                renderX, y + gh,
                renderX + gw, y + gh,
                renderX + gw, y,
                c, ChartEditorConstants.RenderMetrics.SdfFeathering, glyph.U1, glyph.V1, glyph.U2, glyph.V2);

            currentX += glyph.Advance * scale;
        }
    }

    public static float MeasureScaledTextWidth(TimelineBatcher batcher, string text, float scale)
    {
        var atlas = batcher.FontAtlas;
        if (atlas == null) return 0;
        float tw = 0;
        var ascii = atlas.AsciiGlyphs;
        var valid = atlas.AsciiValid;
        foreach (var ch in text)
        {
            if (ch < 128)
            {
                if (valid[ch]) tw += ascii[ch].Advance;
            }
            else if (atlas.GlyphMap.TryGetValue(ch, out var g))
            {
                tw += g.Advance;
            }
        }
        return tw * scale;
    }
}
