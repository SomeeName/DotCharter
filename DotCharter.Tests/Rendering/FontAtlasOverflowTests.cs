using DotCharter.Rendering.Vulkan;

namespace DotCharter.Tests.Rendering;

/// <summary>
///     The 3D glyph sheet's two silent failures, both GPU-free to reproduce.
///     <para>
///         <b>It dropped glyphs in total silence.</b> A glyph past the bottom of
///         the fixed 2048² sheet was skipped with a bare <c>break</c> — no
///         count, no log line, nothing. Packing is code-point-sorted, so the
///         glyphs that fall off the end are always the HIGHEST code points, i.e.
///         CJK: the shipped app rendered <c>'?'</c> for Japanese lyrics and
///         section names with no diagnostic anywhere. The 2D twin
///         (<c>Text.ChartFontAtlas</c>) has counted and warned about exactly this
///         since 0.44.
///     </para>
///     <para>
///         <b>And the rebuild freed the live sheet BEFORE building the new
///         one.</b> <c>Texture?.Dispose()</c> frees the image, the view, the
///         sampler and the descriptor set, and only then did it rasterize 16 MB
///         and upload. Any throw in between left <c>Texture</c> pointing at the
///         disposed object; <c>TextBatcher.Flush</c> only null-checks, so the
///         next frame bound a freed descriptor set. <c>NeedsRebuild</c> stayed
///         true, so it retried EVERY frame — one sustained VRAM shortage leaking
///         per retry, the amplifier <c>NativeResourceSet</c> exists to stop.
///     </para>
/// </summary>
public sealed class FontAtlasOverflowTests
{
    private static char[] Ascii =>
        Enumerable.Range(' ', '~' - ' ' + 1).Select(c => (char)c).ToArray();

    /// <summary>5,000 CJK code points at the shipped 32 px will not fit even the
    /// grown 2048x4096 sheet — and the count says so instead of nothing.</summary>
    [Fact]
    public void ASheetFullOfCjkReportsWhatItCouldNotFit()
    {
        var cjk = Enumerable.Range(0x4E00, 5000).Select(c => (char)c).ToArray();

        var (_, glyphs, overflowed, _) = FontAtlas.Rasterize(cjk, "Arial", 32f);

        Assert.True(overflowed > 0,
            $"5,000 CJK glyphs at 32 px fitted a {FontAtlas.AtlasWidth}x{FontAtlas.MaxAtlasHeight} sheet " +
            $"({glyphs.Count} packed) — this fixture no longer overflows, so the test is vacuous.");
        Assert.Equal(cjk.Length, glyphs.Count + overflowed);
    }

    /// <summary>…and the ASCII seed the ctor lays down must always fit, or the
    /// highway goes blank rather than merely losing a section name.</summary>
    [Fact]
    public void TheAsciiSeedNeverOverflows()
    {
        var (_, glyphs, overflowed, _) = FontAtlas.Rasterize(Ascii, "Arial", 32f);

        Assert.Equal(0, overflowed);
        Assert.Equal(Ascii.Length, glyphs.Count);
    }

    /// <summary>The report: said ONCE, and it names both what was lost and how
    /// many characters were registered — the number that tells the reader this
    /// is the unbounded-set fault.</summary>
    [Fact]
    public void TheOverflowWarningIsSaidOnceAndNamesWhatWasLost()
    {
        Assert.Null(FontAtlas.OverflowWarning(0, alreadyWarned: false, registeredCharacters: 4000));
        Assert.Null(FontAtlas.OverflowWarning(7, alreadyWarned: true, registeredCharacters: 4000));

        var warning = FontAtlas.OverflowWarning(7, alreadyWarned: false, registeredCharacters: 4000);

        Assert.NotNull(warning);
        Assert.Contains("7 glyph(s) skipped", warning);
        Assert.Contains("4000 distinct chars registered", warning);
    }

    /// <summary>The glyph sheet is uploaded WITHOUT mipmaps: averaging thin strokes
    /// down the mip chain fades the far fret numbers on the highway floor.</summary>
    [Fact]
    public void TheGlyphSheetIsNotMipmapped()
    {
        var service = new FakeVulkanRenderService();
        var atlas = new FontAtlas(service, "Arial", 32f);

        atlas.RebuildIfNeeded();

        Assert.NotEmpty(service.LoadTextureMipmapped);
        Assert.All(service.LoadTextureMipmapped, Assert.False);
    }

    /// <summary>The overflow reaches the ATLAS, not just the rasterizer — a
    /// count nothing publishes is a count nobody reads.</summary>
    [Fact]
    public void AnOverflowingRebuildPublishesItsCountAndWarning()
    {
        var service = new FakeVulkanRenderService();
        var atlas = new FontAtlas(service, "Arial", 32f);
        atlas.RegisterCharacters(new string(Enumerable.Range(0x4E00, 5000).Select(c => (char)c).ToArray()));

        atlas.RebuildIfNeeded();

        Assert.True(atlas.OverflowedGlyphCount > 0);
        Assert.NotNull(atlas.LastOverflowWarning);
        Assert.Contains("3D atlas sheet is full", atlas.LastOverflowWarning);
    }

    /// <summary>
    ///     <b>A failed rebuild must leave the live sheet BINDABLE.</b> The
    ///     identity of <c>Texture</c> is not enough to prove it — the old code
    ///     disposed in place and left the same reference — so this asserts the
    ///     texture was never disposed, and that its glyph metrics still resolve.
    /// </summary>
    [Fact]
    public void AFailedRebuildDoesNotFreeTheLiveSheet()
    {
        var service = new FakeVulkanRenderService();
        var atlas = new FontAtlas(service, "Arial", 32f);

        atlas.RebuildIfNeeded();
        var live = atlas.Texture;
        Assert.NotNull(live);
        Assert.True(atlas.TryGetGlyph('W', out var before));

        // VRAM pressure: the upload throws part-way through the next rebuild.
        service.LoadTextureThrows = new InvalidOperationException("out of device memory");
        atlas.RegisterCharacters("こんにちは");

        Assert.Throws<InvalidOperationException>(() => atlas.RebuildIfNeeded());

        Assert.Same(live, atlas.Texture);
        Assert.False(live.IsDisposed,
            "the rebuild freed the live sheet's image, view, sampler and descriptor set before " +
            "building its replacement — the next frame binds freed GPU memory, every frame, " +
            "because NeedsRebuild stays true and it retries.");

        // The metrics are the old sheet's, matching the texture that is bound.
        Assert.True(atlas.TryGetGlyph('W', out var after));
        Assert.Equal(before.U0, after.U0);
        Assert.Equal(before.V1, after.V1);
    }

    /// <summary>…and a rebuild that SUCCEEDS still frees the sheet it
    /// replaced, or every project switch leaks 16 MB of VRAM.</summary>
    [Fact]
    public void ASuccessfulRebuildDisposesTheSheetItReplaced()
    {
        var service = new FakeVulkanRenderService();
        var atlas = new FontAtlas(service, "Arial", 32f);

        atlas.RebuildIfNeeded();
        var first = atlas.Texture;
        Assert.NotNull(first);

        atlas.RegisterCharacters("こんにちは");
        atlas.RebuildIfNeeded();

        Assert.NotSame(first, atlas.Texture);
        Assert.True(first.IsDisposed, "the replaced glyph sheet was never freed.");
        Assert.False(atlas.Texture!.IsDisposed);
    }
}
