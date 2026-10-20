using DotCharter.Rendering.Text;
using SkiaSharp;

namespace DotCharter.Tests.Rendering;

/// <summary>
///     <see cref="ChartFontAtlas" /> is the shared 2D glyph-atlas base: the
///     Vulkan subclass only uploads, so every metric the batcher consumes is
///     computed here and 2D text geometry is a pure function of this class.
///     It had no test — the only atlas coverage was the 3D
///     <c>FontAtlas.Rasterize</c> and the GPU-gated goldens.
///     <para>
///         Rasterization is pure SkiaSharp, so a subclass with a no-op
///         <c>UploadAtlas</c> exercises the whole thing with no GPU and no
///         Avalonia app.
///     </para>
/// </summary>
public sealed class ChartFontAtlasMetricsTests
{
    [Fact]
    public void Every_glyph_lands_inside_the_sheet_with_a_non_degenerate_UV_rect()
    {
        var atlas = Build();

        Assert.NotEmpty(atlas.GlyphMap);
        foreach (var (c, g) in atlas.GlyphMap)
        {
            Assert.InRange(g.U1, 0f, 1f);
            Assert.InRange(g.V1, 0f, 1f);
            Assert.InRange(g.U2, 0f, 1f);
            Assert.InRange(g.V2, 0f, 1f);
            Assert.True(g.U2 > g.U1, $"degenerate U range for '{c}'");
            Assert.True(g.V2 > g.V1, $"degenerate V range for '{c}'");
        }
    }

    [Fact]
    public void The_ascii_fast_path_mirrors_the_glyph_map()
    {
        // AddText reads AsciiGlyphs/AsciiValid for chars < 128 and GlyphMap
        // above; a divergence would render ASCII with the wrong UVs.
        var atlas = Build();

        for (var c = (char)0; c < (char)128; c++)
        {
            if (!atlas.AsciiValid[c])
            {
                Assert.False(atlas.GlyphMap.ContainsKey(c), $"'{c}' is in GlyphMap but not the ASCII fast path");
                continue;
            }

            var fast = atlas.AsciiGlyphs[c];
            var map = atlas.GlyphMap[c];
            Assert.Equal(map.U1, fast.U1);
            Assert.Equal(map.V1, fast.V1);
            Assert.Equal(map.Advance, fast.Advance);
        }
    }

    [Fact]
    public void MeasureTextWidth_is_the_sum_of_the_advances()
    {
        var atlas = Build();
        const string text = "A12b#";

        var expected = text.Sum(ch => atlas.GlyphMap[ch].Advance);

        Assert.Equal(expected, atlas.MeasureTextWidth(text), 3);
    }

    [Fact]
    public void MeasureTextWidth_skips_unregistered_characters_instead_of_throwing()
    {
        var atlas = Build();

        // U+2603 is not in the default set and was never registered; AddText
        // skips it, so the measurement must skip it too or centred text drifts.
        Assert.Equal(atlas.MeasureTextWidth("AB"), atlas.MeasureTextWidth("A☃B"), 3);
    }

    [Fact]
    public void RegisterCharacters_is_idempotent_for_characters_already_present()
    {
        var atlas = Build();
        var buildsAfterFirst = atlas.Builds;

        atlas.RegisterCharacters("ABC123");
        atlas.RebuildIfNeeded();

        Assert.Equal(buildsAfterFirst, atlas.Builds);
    }

    [Fact]
    public void RegisterCharacters_triggers_exactly_one_rebuild_for_a_new_glyph()
    {
        var atlas = Build();
        var before = atlas.Builds;

        atlas.RegisterCharacters("ÄÖÜ");
        atlas.RebuildIfNeeded();
        atlas.RebuildIfNeeded();

        Assert.Equal(before + 1, atlas.Builds);
        Assert.True(atlas.GlyphMap.ContainsKey('Ä'));
    }

    [Fact]
    public void Packing_is_a_pure_function_of_the_character_SET_not_the_registration_order()
    {
        // The registration set is a HashSet, which enumerates in INSERTION
        // order. Packing straight off it made the sheet depend on which chart
        // the user opened first, so identical text got different UVs between
        // runs and the 2D golden-frame byte-stability claim only held inside a
        // single process.
        var a = Build();
        a.RegisterCharacters("éèê");
        a.RebuildIfNeeded();

        var b = Build();
        b.RegisterCharacters("êéè");
        b.RebuildIfNeeded();

        Assert.Equal(a.GlyphMap.Count, b.GlyphMap.Count);
        foreach (var (c, g) in a.GlyphMap)
        {
            var other = b.GlyphMap[c];
            Assert.Equal(g.U1, other.U1);
            Assert.Equal(g.V1, other.V1);
            Assert.Equal(g.U2, other.U2);
            Assert.Equal(g.V2, other.V2);
        }
    }

    [Fact]
    public void Metrics_are_scale_independent_the_sheet_is_rasterized_bigger_and_divided_back()
    {
        // The HiDPI contract: the sheet is rasterized at fontSize x renderScale
        // so a 150 % display gets real pixels, but every metric handed back is
        // divided back down so the batcher keeps emitting LOGICAL units. If the
        // divide-back is dropped, all 2D text doubles in size on a 2x monitor.
        var at1 = Build();
        var at2 = Build(renderScale: 2f);

        Assert.Equal(1f, at1.RenderScale);
        Assert.Equal(2f, at2.RenderScale);

        // Tolerance is ONE LOGICAL PIXEL, not equality: the row box quantizes
        // to whole RASTER pixels (floor(ascent)/ceil(descent)), so at 2x the
        // divided-back row can legitimately land half a logical pixel off. A
        // dropped divide-back would double these instead.
        AssertWithinOnePixel(at1.RowHeight, at2.RowHeight, nameof(ChartFontAtlas.RowHeight));
        AssertWithinOnePixel(at1.TextCenterYOffset, at2.TextCenterYOffset, nameof(ChartFontAtlas.TextCenterYOffset));
        AssertWithinOnePixel(at1.MeasureTextWidth("0123456789"), at2.MeasureTextWidth("0123456789"),
            "MeasureTextWidth(\"0123456789\")");

        foreach (var (c, g) in at1.GlyphMap)
        {
            var hi = at2.GlyphMap[c];
            AssertWithinOnePixel(g.Advance, hi.Advance, $"advance of '{c}'");
            AssertWithinOnePixel(g.Height, hi.Height, $"height of '{c}'");
            AssertWithinOnePixel(g.OffsetX, hi.OffsetX, $"offsetX of '{c}'");
        }
    }

    private static void AssertWithinOnePixel(float at1X, float at2X, string what) =>
        Assert.True(Math.Abs(at1X - at2X) <= 1f,
            $"{what}: {at1X} at renderScale 1 vs {at2X} at renderScale 2 — the HiDPI divide-back is wrong");

    [Fact]
    public void SetRenderScale_clamps_and_only_repacks_when_the_value_actually_changes()
    {
        var atlas = Build();
        var before = atlas.Builds;

        atlas.SetRenderScale(1f);
        atlas.RebuildIfNeeded();
        Assert.Equal(before, atlas.Builds);

        atlas.SetRenderScale(0.5f); // clamped back to 1 => still no repack
        atlas.RebuildIfNeeded();
        Assert.Equal(1f, atlas.RenderScale);
        Assert.Equal(before, atlas.Builds);

        atlas.SetRenderScale(99f); // clamped to the 4x ceiling
        atlas.RebuildIfNeeded();
        Assert.Equal(4f, atlas.RenderScale);
        Assert.Equal(before + 1, atlas.Builds);
    }

    // ---- fixture ------------------------------------------------------------

    private static ProbeAtlas Build(float renderScale = 1f)
    {
        var atlas = new ProbeAtlas();
        atlas.SetChartFont("Default", 14f, isBold: false);
        atlas.SetRenderScale(renderScale);
        atlas.RebuildIfNeeded();
        return atlas;
    }

    /// <summary>Upload-free subclass: the base does all the rasterization and
    /// all the metrics, so nothing here needs a GPU.</summary>
    private sealed class ProbeAtlas : ChartFontAtlas
    {
        public int Builds { get; private set; }

        protected override void UploadAtlas(SKBitmap bitmap) => Builds++;

        public override void Dispose()
        {
        }
    }
}
