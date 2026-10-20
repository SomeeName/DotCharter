using Avalonia.Media;
using DotCharter.Rendering.Text;
using DotCharter.Rendering.Timeline2D;

namespace DotCharter.Tests.Rendering;

/// <summary>
///     <b>Text is sharp only when a glyph is a 1:1 texel-to-pixel copy, and
///     under a fractional surface scale it was neither end of that.</b>
///
///     <para>
///         The batcher emits quads in LOGICAL units and the ortho multiplies by
///         the surface scale, so a glyph's real position is
///         <c>logical x renderScale</c>. Every label position is fractional
///         (centred text, a time-to-pixel mapping), and multiplying it by 1.30 —
///         the factor <c>AutoUiScale</c> derives on a 1440p panel, and the
///         DEFAULT since 2026-09-12 — leaves every glyph straddling two pixel
///         columns, which the sampler resolves by blending them. At the other
///         end the sheet was packed with <c>margin = 4 x renderScale</c> and
///         <c>texWidth = advance + margin</c>, both fractional, so each glyph's
///         SOURCE rectangle started mid-texel as well.
///     </para>
///
///     <para>
///         <b>This is the half surface alignment cannot reach.</b>
///         <c>ComposedSurfaceGeometry</c> puts the whole surface on the pixel
///         grid; a glyph inside a perfectly aligned surface is still resampled
///         if its own quad is not. Measured on a real capture at shell scale
///         1.30, the two fixes are worth roughly the same again each.
///     </para>
///
///     <para>
///         No GPU: the fault is entirely in the numbers handed to it, and a
///         blurred glyph renders perfectly and throws nothing.
///     </para>
/// </summary>
public class GlyphPixelSnapTests
{
    /// <summary>The shell factors <c>AutoUiScale</c>'s 0.05 grid produces, plus
    /// the 1.0 case that was never broken and must stay unbroken.</summary>
    public static TheoryData<float> Scales() => [1.0f, 1.05f, 1.15f, 1.30f, 1.45f, 2.0f];

    /// <summary>
    ///     Every glyph's source rectangle is a whole number of texels, at a
    ///     whole texel origin. Asserted through the metrics the batcher
    ///     consumes: <c>Width x renderScale</c> IS the texel count.
    /// </summary>
    [Theory]
    [MemberData(nameof(Scales))]
    public void TheSheet_PacksEveryGlyphOnWholeTexels(float renderScale)
    {
        var atlas = Build(renderScale);

        foreach (var c in "0123456789:CHORUSVERSE")
        {
            Assert.True(atlas.GlyphMap.TryGetValue(c, out var glyph), $"'{c}' is not packed");

            AssertWhole(glyph.Width * renderScale, $"width of '{c}' in texels");
            AssertWhole(glyph.Height * renderScale, $"height of '{c}' in texels");
            AssertWhole(glyph.U1 * ChartFontAtlas.AtlasSize, $"U origin of '{c}' in texels");
            AssertWhole(glyph.V1 * ChartFontAtlas.AtlasSize, $"V origin of '{c}' in texels");
        }
    }

    /// <summary>
    ///     Every glyph QUAD lands on a whole physical pixel, from a deliberately
    ///     fractional pen position. Both axes: the y is snapped once per call,
    ///     the x per glyph (a fractional advance would otherwise walk the pen
    ///     off the grid again after the first character).
    /// </summary>
    [Theory]
    [MemberData(nameof(Scales))]
    public void EveryGlyphQuad_LandsOnAWholePhysicalPixel(float renderScale)
    {
        var atlas = Build(renderScale);
        var batcher = new TimelineBatcher { FontAtlas = atlas };

        // 411,37 / 108,63 is the shape a time label really gets: a time→pixel
        // mapping and a centred label produce nothing round.
        batcher.AddText("00:05", 411.37f, 108.63f, Colors.White);

        var quads = ReadQuadOrigins(batcher);
        Assert.Equal(5, quads.Count);

        foreach (var (x, y, w, h) in quads)
        {
            AssertWhole(x * renderScale, "glyph quad x in physical pixels");
            AssertWhole(y * renderScale, "glyph quad y in physical pixels");
            // …and the extent, so the far edge is on the grid too: an integer
            // origin with a fractional width is still a resample.
            AssertWhole(w * renderScale, "glyph quad width in physical pixels");
            AssertWhole(h * renderScale, "glyph quad height in physical pixels");
        }
    }

    /// <summary>
    ///     The snap is a SUB-PIXEL correction, not a re-layout: a label may not
    ///     visibly move, and its glyphs must keep their order and spacing. Half
    ///     a physical pixel is the whole budget.
    /// </summary>
    [Fact]
    public void TheSnap_MovesNoGlyphByAsMuchAsAWholePixel()
    {
        const float renderScale = 1.30f;
        var atlas = Build(renderScale);
        var batcher = new TimelineBatcher { FontAtlas = atlas };
        batcher.AddText("00:05", 411.37f, 108.63f, Colors.White);
        var quads = ReadQuadOrigins(batcher);

        // The unsnapped pen positions, computed the way the batcher used to.
        var penX = 411.37f;
        for (var i = 0; i < "00:05".Length; i++)
        {
            var glyph = atlas.GlyphMap["00:05"[i]];
            var unsnappedX = penX + glyph.OffsetX;
            Assert.True(Math.Abs(quads[i].X - unsnappedX) * renderScale <= 0.5f + 1e-4f,
                $"glyph {i} moved {(quads[i].X - unsnappedX) * renderScale} px — that is a re-layout, not a snap.");
            penX += glyph.Advance;
        }

        Assert.True(Math.Abs(quads[0].Y - 108.63f) * renderScale <= 0.5f + 1e-4f);
        // Still left-to-right, still monotonically advancing.
        for (var i = 1; i < quads.Count; i++) Assert.True(quads[i].X > quads[i - 1].X);
    }

    /// <summary>
    ///     <b>Proof the cases above are not vacuous.</b> The snap only means
    ///     something where the unsnapped pen was NOT already on the grid, so
    ///     this states the pre-fix arithmetic at every scale: over a handful of
    ///     realistic label positions, at least one lands mid-pixel and the snap
    ///     therefore moves it. (A single probe x is not enough — 411,37 x 1,05
    ///     happens to be whole, which is the sort of accident that makes a
    ///     guard pass while asserting nothing.)
    /// </summary>
    [Theory]
    [MemberData(nameof(Scales))]
    public void WithoutTheSnap_ThePenPositionIsFractional(float renderScale)
    {
        var atlas = Build(renderScale);
        var offset = atlas.GlyphMap['0'].OffsetX;

        var worst = 0f;
        foreach (var penX in new[] { 411.37f, 512.5f, 733.21f, 128.64f, 999.08f })
        {
            var unsnapped = (penX + offset) * renderScale;
            worst = MathF.Max(worst, Math.Abs(unsnapped - MathF.Round(unsnapped)));
        }

        Assert.True(worst > 0.1f,
            $"every probe position is already whole at scale {renderScale} (worst phase {worst}) — " +
            "these cases would pass with the snap removed.");
    }

    // ---- helpers --------------------------------------------------------------

    private static void AssertWhole(float value, string what) =>
        Assert.True(Math.Abs(value - MathF.Round(value)) < 1e-3f,
            $"{what} is {value}, which is not a whole number — that fraction is a bilinear blend.");

    /// <summary>The general stream is 9 floats per vertex, 6 vertices per quad,
    /// x/y first. Reads one origin + extent per quad.</summary>
    private static List<(float X, float Y, float W, float H)> ReadQuadOrigins(TimelineBatcher batcher)
    {
        var result = new List<(float, float, float, float)>();
        var buf = batcher.GeneralBuffer;
        for (var v = 0; v + 6 <= batcher.GeneralVertexCount; v += 6)
        {
            var x1 = buf[v * 9];
            var y1 = buf[v * 9 + 1];
            // Vertex 3 of the fan is the opposite corner (x3, y3).
            var x3 = buf[(v + 2) * 9];
            var y3 = buf[(v + 2) * 9 + 1];
            result.Add((x1, y1, x3 - x1, y3 - y1));
        }

        return result;
    }

    private static ProbeAtlas Build(float renderScale)
    {
        var atlas = new ProbeAtlas();
        atlas.SetChartFont("Default", 14f, isBold: false);
        atlas.SetRenderScale(renderScale);
        atlas.RebuildIfNeeded();
        return atlas;
    }

    /// <summary>Upload-free subclass — the base owns every metric the batcher
    /// reads, so nothing here needs a GPU.</summary>
    private sealed class ProbeAtlas : ChartFontAtlas
    {
        protected override void UploadAtlas(SkiaSharp.SKBitmap bitmap)
        {
        }

        public override void Dispose()
        {
        }
    }
}
