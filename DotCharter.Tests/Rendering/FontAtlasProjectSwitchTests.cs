using DotCharter.Rendering.Text;
using SkiaSharp;

namespace DotCharter.Tests.Rendering;

/// <summary>
///     Both glyph sheets — the 2D <see cref="ChartFontAtlas" /> and the 3D
///     <see cref="DotCharter.Rendering.Vulkan.FontAtlas" /> — hold a
///     <c>HashSet&lt;char&gt;</c> that was only ever ADDED to. Every lyric,
///     section name, phrase name and tone name of every song opened in the
///     session accumulated in it for the life of the process, on objects that
///     outlive a project (the 3D engine is an app-wide DI singleton; the 2D
///     backend lives with the timeline control).
///     <para>
///         It is a CORRECTNESS bug before it is a memory one. The sheet is a
///         fixed 2048², a glyph that no longer fits is skipped, and the batcher
///         draws an absent <c>GlyphMap</c> entry as zero-width — so a long
///         session silently starts losing text on the highway. The only report
///         of that was a <c>Debug.WriteLine</c>, which is compiled OUT of a
///         Release build.
///     </para>
///     <para>
///         Fixed the way every other project-scoped cache is
///         (<c>docs/agents/ui/project-lifecycle.md</c>): reset to the seed on the
///         <c>ActiveProject</c> notification.
///     </para>
/// </summary>
public sealed class FontAtlasProjectSwitchTests
{
    /// <summary>The 2D atlas is abstract only because the GPU upload is the
    /// subclass's job; every registration path is on the base.</summary>
    private sealed class HeadlessChartFontAtlas : ChartFontAtlas
    {
        protected override void UploadAtlas(SKBitmap bitmap) { }
        public override void Dispose() { }
    }

    /// <summary>Text a song contributes: section names, a phrase, a tone name,
    /// a lyric line — the four things ChartRenderer registers per frame. Each
    /// song brings characters of its OWN (a different CJK block per song), which
    /// is exactly why the set grew without bound: an English chart shares the
    /// seed, a Japanese one does not.</summary>
    private static IEnumerable<string> SongText(int song)
    {
        var own = new string([(char)(0x4E00 + song * 3), (char)(0x4E01 + song * 3), (char)(0x4E02 + song * 3)]);
        return
        [
            $"Intro §{song}", $"Verse §{song}", $"Chōrus §{song}",
            $"Gtr_Lead_{song}‼", $"song №{song} — {own}"
        ];
    }

    [Fact]
    public void The2dAtlas_ReturnsToItsSeedAfterAProjectSwitch()
    {
        var atlas = new HeadlessChartFontAtlas();
        Assert.Equal(ChartFontAtlas.SeedCharacterCount, atlas.RegisteredCharacterCount);

        // Twenty songs is an ordinary evening; the real defect is unbounded.
        for (var song = 0; song < 20; song++)
        {
            foreach (var text in SongText(song))
                atlas.RegisterCharacters(text);
        }

        Assert.True(atlas.RegisteredCharacterCount > ChartFontAtlas.SeedCharacterCount + 20,
            $"the songs registered nothing new ({atlas.RegisteredCharacterCount} chars) — " +
            "this test would pass vacuously");

        atlas.ResetToSeedCharacters();

        Assert.Equal(ChartFontAtlas.SeedCharacterCount, atlas.RegisteredCharacterCount);
    }

    [Fact]
    public void The3dAtlas_ReturnsToItsSeedAfterAProjectSwitch()
    {
        // The ctor only stores the render service; nothing here touches the GPU.
        var atlas = new DotCharter.Rendering.Vulkan.FontAtlas(null!, "Arial", 32f);
        Assert.Equal(DotCharter.Rendering.Vulkan.FontAtlas.SeedCharacterCount, atlas.RegisteredCharacterCount);

        for (var song = 0; song < 20; song++)
        {
            foreach (var text in SongText(song))
                atlas.RegisterCharacters(text);
        }

        Assert.True(
            atlas.RegisteredCharacterCount > DotCharter.Rendering.Vulkan.FontAtlas.SeedCharacterCount + 20,
            $"the songs registered nothing new ({atlas.RegisteredCharacterCount} chars) — " +
            "this test would pass vacuously");

        atlas.ResetToSeedCharacters();

        Assert.Equal(DotCharter.Rendering.Vulkan.FontAtlas.SeedCharacterCount, atlas.RegisteredCharacterCount);
    }

    /// <summary>A reset must not cost the seed itself — the highway would go
    /// blank rather than merely lose a section name.</summary>
    [Fact]
    public void AResetKeepsTheSeedAndStaysIdempotent()
    {
        var atlas = new HeadlessChartFontAtlas();
        atlas.RegisterCharacters("こんにちは");
        atlas.ResetToSeedCharacters();
        atlas.ResetToSeedCharacters();

        Assert.Equal(ChartFontAtlas.SeedCharacterCount, atlas.RegisteredCharacterCount);
        atlas.RebuildIfNeeded();
        // The ASCII the seed promises is packed and measurable again.
        Assert.True(atlas.MeasureTextWidth("Verse 1") > 0);
    }

    /// <summary>The overflow report: said ONCE, and it names both the count of
    /// skipped glyphs and how many characters were registered — which is the
    /// number that tells the reader this is the unbounded-set fault.</summary>
    [Fact]
    public void TheOverflowWarning_IsSaidOnceAndNamesWhatWasLost()
    {
        Assert.Null(ChartFontAtlas.OverflowWarning(0, alreadyWarned: false, registeredCharacters: 4000));
        Assert.Null(ChartFontAtlas.OverflowWarning(7, alreadyWarned: true, registeredCharacters: 4000));

        var warning = ChartFontAtlas.OverflowWarning(7, alreadyWarned: false, registeredCharacters: 4000);

        Assert.NotNull(warning);
        Assert.Contains("7 glyph(s) skipped", warning);
        Assert.Contains("4000 distinct chars registered", warning);
    }

    /// <summary>
    ///     The reset is only worth anything if something CALLS it. Both drivers
    ///     already subscribe to <c>ProjectState.PropertyChanged</c>; the wiring
    ///     is one branch each, and a source lint is the cheapest guard that
    ///     survives (neither control can be driven headlessly without a GPU).
    /// </summary>
    [Theory]
    [InlineData("DotCharter.UI/Controls/ChartRenderer.cs", "ResetToSeedCharacters")]
    [InlineData("DotCharter.UI/Controls/Preview3DRenderDriver.cs", "ResetTextRegistrations")]
    public void TheProjectSwitchHook_IsWiredInBothRenderers(string relativePath, string call)
    {
        // The 3D driver is a source SET since #656 (Preview3DRenderDriverSource);
        // the driver comes first, so the handler found is the driver's own.
        var source = relativePath.EndsWith("Preview3DRenderDriver.cs", StringComparison.Ordinal)
            ? Preview3DRenderDriverSource.ReadAllText()
            : UiSourceTree.ReadAllText(
                Path.Combine(TestPaths.RepoRoot(), relativePath.Replace('/', Path.DirectorySeparatorChar)));

        var handler = source.IndexOf("private void OnProjectStateChanged", StringComparison.Ordinal);
        Assert.True(handler >= 0, $"{relativePath}: OnProjectStateChanged has moved — repoint this lint.");

        var body = source[handler..];
        var branch = body.IndexOf("nameof(ProjectState.ActiveProject)", StringComparison.Ordinal);
        Assert.True(branch >= 0,
            $"{relativePath} no longer reacts to ActiveProject — its glyph sheet keeps every " +
            "song the session ever opened.");
        Assert.Contains(call, body[branch..(branch + 600)]);
    }
}
