using DotCharter.Presentation.Contracts.Shell;
using DotCharter.Presentation.Shared;

namespace DotCharter.Tests.Presentation;

/// <summary>
///     The wave-5 chrome rule (owner, 2026-09-22): charting screens carry the rail and the
///     editor chrome, play screens are full-bleed, and the Library, Settings and the rig
///     have a presentation in each over the same view models — chosen by where the user
///     came from, not by the surface.
/// </summary>
public sealed class ShellPresentationTests
{
    [Theory]
    [InlineData(ShellDestination.Play, ShellContext.Charting, ShellContext.Playing)]
    [InlineData(ShellDestination.Chart, ShellContext.Playing, ShellContext.Charting)]
    [InlineData(ShellDestination.Home, ShellContext.Charting, ShellContext.Charting)]
    [InlineData(ShellDestination.Home, ShellContext.Playing, ShellContext.Playing)]
    [InlineData(ShellDestination.Library, ShellContext.Playing, ShellContext.Playing)]
    [InlineData(ShellDestination.Settings, ShellContext.Charting, ShellContext.Charting)]
    public void OnlyPlayModeAndTheEditorSetTheContext(ShellDestination surface, ShellContext before, ShellContext after) =>
        Assert.Equal(after, ShellPresentation.ContextAfter(surface, before, inRun: false));

    /// <summary>A run is drawn on the editor's surface and is still play: it must not turn the context to charting.</summary>
    [Fact]
    public void ARunOnTheEditorSurface_StaysPlaying() =>
        Assert.Equal(ShellContext.Playing, ShellPresentation.ContextAfter(ShellDestination.Chart, ShellContext.Playing, inRun: true));

    [Theory]
    [InlineData(ShellDestination.Home)]
    [InlineData(ShellDestination.Play)]
    public void HomeAndPlayMode_AreAlwaysFullBleed(ShellDestination surface)
    {
        Assert.False(ShellPresentation.HasChrome(surface, ShellContext.Charting, inRun: false));
        Assert.False(ShellPresentation.HasChrome(surface, ShellContext.Playing, inRun: false));
        Assert.False(ShellPresentation.NeedsBackBar(surface, ShellContext.Playing, inRun: false));
    }

    [Fact]
    public void TheEditor_AlwaysCarriesTheChrome_ExceptDuringARun()
    {
        Assert.True(ShellPresentation.HasChrome(ShellDestination.Chart, ShellContext.Charting, inRun: false));
        Assert.True(ShellPresentation.HasChrome(ShellDestination.Chart, ShellContext.Playing, inRun: false));
        Assert.False(ShellPresentation.HasChrome(ShellDestination.Chart, ShellContext.Charting, inRun: true));
    }

    [Theory]
    [InlineData(ShellDestination.Library)]
    [InlineData(ShellDestination.Settings)]
    public void TheSharedSurfaces_FollowTheContext_AndCarryABackBarWhenFullBleed(ShellDestination surface)
    {
        Assert.True(ShellPresentation.HasChrome(surface, ShellContext.Charting, inRun: false));
        Assert.False(ShellPresentation.NeedsBackBar(surface, ShellContext.Charting, inRun: false));

        Assert.False(ShellPresentation.HasChrome(surface, ShellContext.Playing, inRun: false));
        Assert.True(ShellPresentation.NeedsBackBar(surface, ShellContext.Playing, inRun: false));
    }

    /// <summary>A player's round trip: Home → Library (full-bleed) → load a chart (editor, chrome) → Home → Library (chrome now).</summary>
    [Fact]
    public void APlayerWhoOpensTheEditor_BecomesACharter_UntilPlayModeAgain()
    {
        var context = ShellContext.Playing;
        context = ShellPresentation.ContextAfter(ShellDestination.Library, context, false);
        Assert.False(ShellPresentation.HasChrome(ShellDestination.Library, context, false));

        context = ShellPresentation.ContextAfter(ShellDestination.Chart, context, false);
        context = ShellPresentation.ContextAfter(ShellDestination.Home, context, false);
        context = ShellPresentation.ContextAfter(ShellDestination.Library, context, false);
        Assert.True(ShellPresentation.HasChrome(ShellDestination.Library, context, false));

        context = ShellPresentation.ContextAfter(ShellDestination.Play, context, false);
        context = ShellPresentation.ContextAfter(ShellDestination.Settings, context, false);
        Assert.True(ShellPresentation.NeedsBackBar(ShellDestination.Settings, context, false));
    }

    /// <summary>Home and Play Mode in a window had no minimise, maximise or close at all (owner,
    /// 2026-09-23): the window draws no OS caption, and hiding the title bar took the buttons with it.</summary>
    [Theory]
    [InlineData(ShellDestination.Home)]
    [InlineData(ShellDestination.Play)]
    public void AFullBleedSurfaceInAWindow_KeepsTheWindowCaption(ShellDestination surface)
    {
        var chrome = ShellPresentation.HasChrome(surface, ShellContext.Playing, inRun: false);
        var back = ShellPresentation.NeedsBackBar(surface, ShellContext.Playing, inRun: false);

        Assert.True(ShellPresentation.ShowsWindowCaption(chrome, back, fullScreen: false));
        Assert.Equal(ShellPresentation.WindowCaptionHeight, ShellPresentation.TopRowHeight(chrome, back, fullScreen: false));
    }

    [Fact]
    public void Fullscreen_DropsTheCaption_ButNeverTheTitleBarOrTheBackBar()
    {
        Assert.False(ShellPresentation.ShowsWindowCaption(false, false, fullScreen: true));
        Assert.Equal(0, ShellPresentation.TopRowHeight(false, false, fullScreen: true));
        Assert.Equal(ShellPresentation.TitleBarHeight, ShellPresentation.TopRowHeight(true, false, fullScreen: true));
        Assert.Equal(ShellPresentation.BackBarHeight, ShellPresentation.TopRowHeight(false, true, fullScreen: true));
    }

    [Fact]
    public void TheTitleBarAndTheBackBar_AreTheirOwnCaption()
    {
        Assert.False(ShellPresentation.ShowsWindowCaption(hasChrome: true, needsBackBar: false, fullScreen: false));
        Assert.False(ShellPresentation.ShowsWindowCaption(hasChrome: false, needsBackBar: true, fullScreen: false));
    }
}
