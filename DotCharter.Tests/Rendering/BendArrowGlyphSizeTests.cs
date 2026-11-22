using DotCharter.Rendering.Text;
using DotCharter.Rendering.Vulkan;
using SkiaSharp;

namespace DotCharter.Tests.Rendering;

/// <summary>
///     The bend labels' arrows must be drawn as tall and as solid as the capitals beside them in both
///     glyph sheets (the 3D <c>FontAtlas</c> and the 2D <c>ChartFontAtlas</c>); a font-fallback arrow
///     was a faint speck, so these measure the INK each sheet really holds.
/// </summary>
public sealed class BendArrowGlyphSizeTests
{
    private const byte InkAlpha = 96;

    [Fact]
    public void The_3D_sheet_draws_each_arrow_as_tall_and_as_solid_as_a_capital()
    {
        var (pixels, glyphs, _, height) = FontAtlas.Rasterize("F↗↘", "Arial", 32f);
        (int Rows, int Cols, byte Peak) InkOf(char c)
        {
            var g = glyphs[c];
            return Ink(pixels, FontAtlas.AtlasWidth,
                (int)MathF.Round(g.U0 * FontAtlas.AtlasWidth), (int)MathF.Round(g.V0 * height),
                (int)MathF.Round(g.U1 * FontAtlas.AtlasWidth), (int)MathF.Round(g.V1 * height));
        }

        AssertArrowsMatchCapital(InkOf('F'), InkOf('↗'), InkOf('↘'));
    }

    [Fact]
    public void The_2D_sheet_draws_each_arrow_as_tall_and_as_solid_as_a_capital()
    {
        var atlas = new CapturingAtlas();
        atlas.SetChartFont("Default", 14f, isBold: true);
        atlas.SetRenderScale(3f);
        atlas.RebuildIfNeeded();
        var (pixels, width, height) = atlas.Sheet;
        (int Rows, int Cols, byte Peak) InkOf(char c)
        {
            var g = atlas.GlyphMap[c];
            return Ink(pixels, width,
                (int)MathF.Round(g.U1 * width), (int)MathF.Round(g.V1 * height),
                (int)MathF.Round(g.U2 * width), (int)MathF.Round(g.V2 * height));
        }

        AssertArrowsMatchCapital(InkOf('F'), InkOf('↗'), InkOf('↘'));
    }

    private static void AssertArrowsMatchCapital((int Rows, int Cols, byte Peak) cap,
        (int Rows, int Cols, byte Peak) bend, (int Rows, int Cols, byte Peak) release)
    {
        Assert.True(cap.Rows > 4, "the capital left no ink: the probe is vacuous");
        foreach (var arrow in new[] { bend, release })
        {
            Assert.True(arrow.Rows >= cap.Rows * 0.9f, $"arrow ink {arrow.Rows} rows vs capital {cap.Rows}");
            Assert.True(arrow.Cols >= cap.Rows * 0.8f, $"arrow ink {arrow.Cols} cols vs capital {cap.Rows}");
            Assert.True(arrow.Peak >= 240, $"arrow peak alpha {arrow.Peak}: not solid");
        }
    }

    private static (int Rows, int Cols, byte Peak) Ink(byte[] rgba, int stride, int x0, int y0, int x1, int y1)
    {
        int top = int.MaxValue, bottom = -1, left = int.MaxValue, right = -1;
        byte peak = 0;
        for (var y = y0; y < y1; y++)
        {
            for (var x = x0; x < x1; x++)
            {
                var a = rgba[(y * stride + x) * 4 + 3];
                if (a > peak) peak = a;
                if (a < InkAlpha) continue;
                top = Math.Min(top, y);
                bottom = Math.Max(bottom, y);
                left = Math.Min(left, x);
                right = Math.Max(right, x);
            }
        }

        return bottom < 0 ? (0, 0, peak) : (bottom - top + 1, right - left + 1, peak);
    }

    private sealed class CapturingAtlas : ChartFontAtlas
    {
        public (byte[] Bytes, int Width, int Height) Sheet { get; private set; } = ([], 0, 0);

        protected override void UploadAtlas(SKBitmap bitmap) =>
            Sheet = (bitmap.Bytes.ToArray(), bitmap.Width, bitmap.Height);

        public override void Dispose()
        {
        }
    }
}
