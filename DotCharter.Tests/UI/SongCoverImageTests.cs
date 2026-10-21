using Avalonia;
using DotCharter.UI.ViewModels.Panels;

namespace DotCharter.Tests.UI;

/// <summary>
///     Song cover art is a file the CHARTER shipped, and both song-library
///     surfaces used to retain it verbatim: <c>PsarcSongListItem</c> decoded with
///     <c>new Bitmap(path)</c> (a 1024² cover is ~4 MB of RGBA for a 32/52 px
///     cell), and Play Mode's list wired only <c>ContainerPrepared</c> — every
///     row ever scrolled past pinned its bitmap for the session, the exact
///     accumulation the browser's <c>ContainerClearing</c> release exists to
///     prevent.
///     <para>
///         Same split as <c>ProfileAvatarImageTests</c>: the pixel work needs a
///         Skia platform the default gate deliberately does not boot, so the
///         POLICY is tested as a pure function and the wiring is a source lint.
///     </para>
/// </summary>
public sealed class SongCoverImageTests
{
    [Fact]
    public void TheCapCoversTheLargestCoverSurfaceAtHighDpi() =>
        // The biggest surface that shows a cover is the browser detail pane at
        // 278 logical px; the cap has to cover it at a 2× display scale.
        Assert.True(SongCoverImage.MaxDecodeWidth >= 278 * 2);

    [Theory]
    [InlineData(4096, 640)] // pathological CDLC art
    [InlineData(1024, 640)]
    [InlineData(641, 640)]
    public void AnOversizedCoverIsDownscaled(int sourceWidth, int expected)
        => Assert.Equal(expected, SongCoverImage.DecodeWidthFor(sourceWidth));

    [Theory]
    [InlineData(640)]
    [InlineData(512)]
    [InlineData(256)] // typical Rocksmith art — must stay verbatim
    [InlineData(64)]
    public void ATypicalCoverIsKeptVerbatim(int sourceWidth) =>
        // Never upscale: Bitmap.DecodeToWidth resizes in BOTH directions, so a
        // bare cap-width decode would have TRIPLED a normal 256² cover's
        // retained bytes. The null verdict is what keeps Load off that path.
        Assert.Null(SongCoverImage.DecodeWidthFor(sourceWidth));

    [Theory]
    [InlineData(2000, 1000, 640, 320)] // wide banner keeps its ratio
    [InlineData(1000, 1000, 640, 640)] // square
    [InlineData(800, 2400, 640, 1920)] // tall keeps its ratio, never clipped
    public void TheDownscaleKeepsTheAspectRatio(int w, int h, int target, int expected)
        => Assert.Equal(expected, SongCoverImage.ScaledHeightFor(new PixelSize(w, h), target));

    [Fact]
    public void ADegeneratePixelSizeNeverProducesAZeroHeightBitmap()
    {
        Assert.Equal(1, SongCoverImage.ScaledHeightFor(new PixelSize(0, 0), 640));
        Assert.Equal(1, SongCoverImage.ScaledHeightFor(new PixelSize(4000, 1), 640));
    }

    [Fact]
    public void TheCoverDecodeIsCapped_NotAFullResolutionBitmap()
    {
        // Source lint — see the class summary for why the decode itself cannot
        // run in the default gate.
        var item = ReadSource("DotCharter.UI", "ViewModels", "Panels", "PsarcSongListItem.cs");
        Assert.DoesNotContain("new Bitmap(path)", item, StringComparison.Ordinal);
        Assert.Contains("SongCoverImage.Load(", item, StringComparison.Ordinal);

        // …and the one decode site never upscales: the size verdict comes first.
        var helper = ReadSource("DotCharter.UI", "ViewModels", "Panels", "SongCoverImage.cs");
        Assert.Contains("DecodeWidthFor(full.PixelSize.Width) is not { } width) return full", helper,
            StringComparison.Ordinal);
    }

    [Fact]
    public void TheGuard_RefusesACoverPastTheSharedCeiling_BeforeAnyDecode()
    {
        // Real PNGs, one pixel either side of the ceiling: the decoder would take
        // both, so the verdict can only come from the header probe.
        using var fine = new MemoryStream(TinyPng.Encode(8192, 1));
        using var tooWide = new MemoryStream(TinyPng.Encode(8193, 1));
        using var tooTall = new MemoryStream(TinyPng.Encode(1, 8193));
        using var unknown = new MemoryStream(new byte[2048]);

        Assert.True(SongCoverImage.DecodeGuard(fine));
        Assert.False(SongCoverImage.DecodeGuard(tooWide));
        Assert.False(SongCoverImage.DecodeGuard(tooTall));
        Assert.False(SongCoverImage.DecodeGuard(unknown));
        Assert.Equal(0, tooWide.Position);
    }

    [Fact]
    public void Load_ConsultsTheGuardBeforeTheDecoder()
    {
        // Source lint - the decode needs a Skia platform the default gate does not
        // boot. The guard must come first, and refusing must be a null, not a throw.
        var helper = ReadSource("DotCharter.UI", "ViewModels", "Panels", "SongCoverImage.cs");
        var load = helper.IndexOf("public static Bitmap? Load(", StringComparison.Ordinal);
        Assert.True(load >= 0, "SongCoverImage.Load is gone");
        var body = helper[load..];
        var guard = body.IndexOf("if (!DecodeGuard(stream)) return null;", StringComparison.Ordinal);
        var decode = body.IndexOf("new Bitmap(stream)", StringComparison.Ordinal);
        Assert.True(guard >= 0, "Load no longer consults DecodeGuard");
        Assert.True(decode >= 0, "Load no longer decodes");
        Assert.True(guard < decode, "Load decodes before it consults DecodeGuard");
    }

    [Fact]
    public void PlayModesList_ReleasesCoversWhenRowsRecycle()
    {
        // The browser has always wired BOTH halves (ContainerPrepared loads,
        // ContainerClearing releases); Play Mode wired only the load half.
        var view = ReadSource("DotCharter.UI", "Views", "PlayMode", "Screens", "SongSelectView.axaml.cs");
        Assert.Contains("SongListBox.ContainerClearing", view, StringComparison.Ordinal);
        // Release means PARK since the cover pipeline (SongCoverPipelineTests
        // owns that contract) — the recycle hook must still exist.
        Assert.Contains("SongCoverCache.Shared.Park", view, StringComparison.Ordinal);
    }

    [Fact]
    public void BothSurfaces_RewarmRealizedRowsOnBecomingVisible()
    {
        // The two lists share PsarcSongListItem instances, so either surface can
        // release a cover the OTHER's already-realized (hidden) rows still bind —
        // and a realized row gets no ContainerPrepared when its screen comes
        // back. Each side re-ensures its realized containers on the visibility
        // flip.
        var playMode = ReadSource("DotCharter.UI", "Views", "PlayMode", "Screens", "SongSelectView.axaml.cs");
        var browser = ReadSource("DotCharter.UI", "Views", "Library", "LibraryView.axaml.cs");

        foreach (var (_, source) in new[] { ("SongSelectView", playMode), ("LibraryView", browser) })
        {
            // Through the WATCHER: a `nameof(IsEffectivelyVisible)` property-
            // changed branch never fires in Avalonia 12 (the property is
            // computed, not styled) — six surfaces shipped that dead pattern.
            Assert.Contains("new EffectiveVisibilityWatcher(", source, StringComparison.Ordinal);
            Assert.Contains("GetRealizedContainers()", source, StringComparison.Ordinal);
        }
    }

    private static string ReadSource(params string[] parts)
    {
        var path = Path.Combine(new[] { TestPaths.RepoRoot() }.Concat(parts).ToArray());
        Assert.True(File.Exists(path), $"Source file not found at {path}");
        return UiSourceTree.ReadAllText(path);
    }
}
