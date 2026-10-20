using DotCharter.Services.Audio.Lyrics;

namespace DotCharter.Tests.Services.Audio.Lyrics;

/// <summary>
///     <see cref="LyricAutoSyncOptions.FromConfig" /> is the one seam through
///     which the two editor settings (span word / phrase gaps) and the
///     note-spacing rules reach auto-sync. A setting nobody reads is a dead
///     feature — the mapping AND the call site are both pinned here.
/// </summary>
public sealed class LyricAutoSyncOptionsFactoryTests
{
    [Fact]
    public void FromConfig_MapsBothSpanFlagsAndCarriesTheSpacingRules()
    {
        var config = new AppConfig { LyricSpanWordGap = false, LyricSpanPhraseGap = true };

        var options = LyricAutoSyncOptions.FromConfig(config);

        Assert.False(options.SpanWordEndToNextWord);
        Assert.True(options.SpanPhraseEndToNextPhrase);
        Assert.Same(config, options.SpacingConfig);
    }

    /// <summary>The product defaults, in one place: word gaps span, phrase gaps
    /// do not — a fresh config must behave exactly like
    /// <see cref="LyricAutoSyncOptions.Default" />.</summary>
    [Fact]
    public void FromConfig_OnAFreshConfig_MatchesTheProductDefaults()
    {
        var options = LyricAutoSyncOptions.FromConfig(new AppConfig());

        Assert.True(options.SpanWordEndToNextWord);
        Assert.False(options.SpanPhraseEndToNextPhrase);
    }

    /// <summary>
    ///     The settings must actually be READ where the sync is started. A
    ///     SOURCE LINT because <c>LyricsInspectorViewModel</c> needs most of the
    ///     app booted (same reason <c>ResizeLyricGestureMergeTests</c> lints the
    ///     wheel call site) — without this, reverting the call site to a bare
    ///     <c>LyricAutoSyncOptions.Default</c> would compile, pass every unit
    ///     test above and ship two settings that do nothing.
    /// </summary>
    [Fact]
    public void TheInspectorsAutoSyncCallSite_BuildsItsOptionsFromTheAppConfig()
    {
        var source = UiSourceTree.ReadAllText(UiSourceTree.Resolve(Path.Combine(
            TestPaths.RepoRoot(), "DotCharter.UI", "ViewModels", "Editor", "LyricsInspectorViewModel.cs")));

        Assert.Contains("LyricAutoSyncOptions.FromConfig(", source, StringComparison.Ordinal);
    }
}
