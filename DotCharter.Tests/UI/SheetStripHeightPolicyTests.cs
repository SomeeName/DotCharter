using System.Text.RegularExpressions;
using DotCharter.UI.Views.Shell;

namespace DotCharter.Tests.UI;

/// <summary>
///     A gameplay presentation OVERRIDE may never be written back as the user's
///     preference. The sheet strip is capped to a compact band during a run, and
///     the close path used to persist that cap: hiding the notation strip mid-run
///     read the row's clamped <c>ActualHeight</c> and stored it in
///     <c>AppConfig.LayoutSheetHeight</c>, so one run permanently shrank a
///     480 px score view to 240 with no undo. Same class as the panel snapshot
///     and the minimap preference — save and restore, never absorb.
/// </summary>
public sealed class SheetStripHeightPolicyTests
{
    [Fact]
    public void TheEditorOpensAtTheSavedHeight() => Assert.Equal(480, SheetStripHeightPolicy.OpenHeightPx(480, gameplayPresentation: false));

    [Theory]
    [InlineData(null)]
    [InlineData(0d)]
    [InlineData(119d)]
    public void AnUnsetOrUnusableSavedHeight_FallsBackToTheDefault(double? saved)
    {
        Assert.Equal(SheetStripHeightPolicy.DefaultPx,
            SheetStripHeightPolicy.OpenHeightPx(saved, gameplayPresentation: false));
    }

    [Fact]
    public void ARunCapsTheStrip_ButNeverGrowsIt()
    {
        Assert.Equal(SheetStripHeightPolicy.GameplayMaxPx,
            SheetStripHeightPolicy.OpenHeightPx(480, gameplayPresentation: true));
        // Under the cap the run keeps whatever the user had — the cap is a
        // ceiling, not a fixed size.
        Assert.Equal(180, SheetStripHeightPolicy.OpenHeightPx(180, gameplayPresentation: true));
    }

    /// <summary>The bug itself.</summary>
    [Fact]
    public void ClosingTheStripDuringARun_PersistsNothing()
    {
        Assert.Null(SheetStripHeightPolicy.HeightToPersistPx(
            SheetStripHeightPolicy.GameplayMaxPx, gameplayPresentation: true));
        // …and the round trip a run used to destroy: open at 480, get capped to
        // 240 for the run, close the strip mid-run, and 480 must survive.
        var saved = 480d;
        var shown = SheetStripHeightPolicy.OpenHeightPx(saved, gameplayPresentation: true);
        Assert.Equal(SheetStripHeightPolicy.GameplayMaxPx, shown);
        if (SheetStripHeightPolicy.HeightToPersistPx(shown, gameplayPresentation: true) is { } px)
            saved = px;
        Assert.Equal(480, saved);
        Assert.Equal(480, SheetStripHeightPolicy.OpenHeightPx(saved, gameplayPresentation: false));
    }

    [Fact]
    public void ClosingTheStripInTheEditor_PersistsWhatTheUserActuallyHas()
    {
        Assert.Equal(412, SheetStripHeightPolicy.HeightToPersistPx(412, gameplayPresentation: false));
        // A collapsed / sub-minimum measurement is not a preference either — it
        // is a row that lost its fight with the MinHeights.
        Assert.Null(SheetStripHeightPolicy.HeightToPersistPx(0, gameplayPresentation: false));
        Assert.Null(SheetStripHeightPolicy.HeightToPersistPx(119, gameplayPresentation: false));
    }

    /// <summary>
    ///     The shell layout must ASK the policy on both halves. A bare
    ///     <c>ActualHeight</c> write on the close path is exactly the code this
    ///     fix removed.
    /// </summary>
    [Fact]
    public void TheShellLayout_ReadsBothHalvesFromThePolicy()
    {
        var source = Code("DotCharter.UI", "Views", "Shell", "MainWindowShellLayout.cs");
        Assert.Contains("SheetStripHeightPolicy.OpenHeightPx(", source);
        Assert.Contains("SheetStripHeightPolicy.HeightToPersistPx(", source);
        Assert.DoesNotMatch(
            new Regex(@"if\s*\(px\s*>=\s*120\)\s*vm\.AppConfig\.LayoutSheetHeight"), source);
    }

    private static string Code(params string[] parts)
    {
        var text = UiSourceTree.ReadAllText(Path.Combine(new[] { TestPaths.RepoRoot() }.Concat(parts).ToArray()));
        text = Regex.Replace(text, @"/\*.*?\*/", "", RegexOptions.Singleline);
        return Regex.Replace(text, @"//[^\r\n]*", "");
    }
}
