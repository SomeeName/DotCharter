using DotCharter.Rendering.Text;
using DotCharter.Rendering.Vulkan;
using SkiaSharp;

namespace DotCharter.Tests.Rendering;

/// <summary>
///     Japanese lyrics in the 3D highway were a row of tofu boxes. The 3D
///     glyph sheet drew EVERY character with its one bold primary typeface, and
///     Skia draws a character the font lacks as that font's <c>.notdef</c> box —
///     the same box for every kana and kanji. These pin the per-character
///     fallback (<see cref="GlyphFontChain" />) and what it puts on the sheet,
///     all GPU-free; <c>Headless/HeadlessCjkLyricTests</c> pins the frame.
/// </summary>
public sealed class GlyphFontChainTests
{
    private const string Japanese = "あいうアイウ漢字歌";
    private const string Korean = "한국어";
    private const string Chinese = "中文歌词";

    private static char[] Ascii => Enumerable.Range(' ', '~' - ' ' + 1).Select(c => (char)c).ToArray();

    [Theory]
    [InlineData("こんにちは漢字", "ja")]
    [InlineData("漢字", "zh-Hans")]
    [InlineData("한국어", "ko")]
    [InlineData("漢 한", "ko")]
    [InlineData("Ünïcödé abc ♪", null)]
    [InlineData("", null)]
    public void TheFallbackLocaleFollowsTheScriptsInTheSet(string chars, string? expected) =>
        Assert.Equal(expected, GlyphFontChain.FallbackLocale(chars));

    [Fact]
    public void LatinAndAccentedLatinStayOnThePrimary()
    {
        using var typeface = Bold("Arial");
        using var chain = new GlyphFontChain(typeface, 64f, locale: null);

        foreach (var c in "AZaz09?éüßñ ")
            Assert.Same(chain.Primary, chain.For(c));
        Assert.Empty(chain.FallbackFamilies);
    }

    [Theory]
    [InlineData(Japanese, "ja")]
    [InlineData(Korean, "ko")]
    [InlineData(Chinese, "zh-Hans")]
    [InlineData("✓☃★", null)]
    public void ACharacterThePrimaryLacksIsDrawnWithAFontThatHasIt(string text, string? locale)
    {
        using var typeface = Bold("Arial");
        using var chain = new GlyphFontChain(typeface, 64f, locale);

        foreach (var c in text)
        {
            Assert.False(chain.Primary.ContainsGlyph(c), $"fixture: Arial covers '{c}', so it proves nothing");
            var font = chain.For(c);
            Assert.True(font.ContainsGlyph(c),
                $"'{c}' (U+{(int)c:X4}) is drawn with {font.Typeface.FamilyName}, which has no glyph for it — " +
                "it rasterizes as the .notdef tofu box.");
        }
    }

    /// <summary>
    ///     The sheet itself: every Japanese/Korean/Chinese cell carries ink, and
    ///     no two of them are the same picture. Tofu fails the second half — the
    ///     <c>.notdef</c> box is identical for every character it stands in for.
    ///     The family is the one the engine ships (<c>null</c> = the platform
    ///     default, bold).
    /// </summary>
    [Fact]
    public void CjkCellsOnTheSheetAreDistinctGlyphsNotTheMissingGlyphBox()
    {
        var chars = (Japanese + Korean + Chinese).Distinct().ToArray();
        var (pixels, glyphs, overflowed, height) = FontAtlas.Rasterize(Ascii.Concat(chars), null, 64f);

        Assert.Equal(0, overflowed);
        var cells = chars.ToDictionary(c => c, c => CellAlpha(pixels, height, glyphs[c]));
        foreach (var (c, cell) in cells)
            Assert.True(cell.Count(a => a > 64) > 40, $"'{c}' left no ink on the sheet");

        for (var i = 0; i < chars.Length; i++)
        {
            for (var j = i + 1; j < chars.Length; j++)
            {
                Assert.False(cells[chars[i]].SequenceEqual(cells[chars[j]]),
                    $"'{chars[i]}' and '{chars[j]}' rasterized to the SAME cell — the missing-glyph box.");
            }
        }
    }

    /// <summary>A kanji-heavy song outgrows the base sheet: it grows to the
    /// max height instead of dropping glyphs to '?', and a Latin-only set keeps
    /// the byte-identical 2048² sheet.</summary>
    [Fact]
    public void AKanjiHeavySetGrowsTheSheetInsteadOfDroppingGlyphs()
    {
        var kanji = Enumerable.Range(0x4E00, 600).Select(c => (char)c);

        var (_, _, latinOverflow, latinHeight) = FontAtlas.Rasterize(Ascii, null, 64f);
        var (pixels, glyphs, overflowed, height) = FontAtlas.Rasterize(Ascii.Concat(kanji), null, 64f);

        Assert.Equal((0, FontAtlas.AtlasHeight), (latinOverflow, latinHeight));
        Assert.Equal(0, overflowed);
        Assert.Equal(FontAtlas.MaxAtlasHeight, height);
        Assert.Equal(FontAtlas.AtlasWidth * height * 4, pixels.Length);
        Assert.All(glyphs.Values, m => Assert.InRange(m.V1, 0f, 1f));
    }

    /// <summary>An emoji is a surrogate PAIR on a sheet keyed by UTF-16 unit:
    /// it is not registered, and it shows as ONE '?' rather than two marks.</summary>
    [Fact]
    public void AnEmojiShowsAsOneQuestionMarkAndRegistersNothing()
    {
        var atlas = new FontAtlas(new FakeVulkanRenderService(), null, 32f);
        var before = atlas.RegisteredCharacterCount;

        const string emoji = "\U0001F3B8";
        atlas.RegisterCharacters(emoji);
        atlas.RebuildIfNeeded();

        Assert.Equal(before, atlas.RegisteredCharacterCount);
        Assert.True(atlas.TryGetGlyph(emoji[0], out var high));
        Assert.True(atlas.TryGetGlyph('?', out var question));
        Assert.Equal(question.U0, high.U0);
        Assert.False(atlas.TryGetGlyph(emoji[1], out _));
    }

    private static SKTypeface Bold(string family) =>
        SKTypeface.FromFamilyName(family, SKFontStyleWeight.Bold, SKFontStyleWidth.Normal, SKFontStyleSlant.Upright);

    private static byte[] CellAlpha(byte[] rgba, int height, GlyphMetrics m)
    {
        var x0 = (int)MathF.Round(m.U0 * FontAtlas.AtlasWidth);
        var x1 = (int)MathF.Round(m.U1 * FontAtlas.AtlasWidth);
        var y0 = (int)MathF.Round(m.V0 * height);
        var y1 = (int)MathF.Round(m.V1 * height);
        var cell = new byte[(x1 - x0) * (y1 - y0)];
        var i = 0;
        for (var y = y0; y < y1; y++)
        {
            for (var x = x0; x < x1; x++)
                cell[i++] = rgba[(y * FontAtlas.AtlasWidth + x) * 4 + 3];
        }

        return cell;
    }
}
