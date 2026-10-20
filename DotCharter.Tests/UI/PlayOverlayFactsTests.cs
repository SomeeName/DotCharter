using DotCharter.UI.ViewModels.PlayMode;

namespace DotCharter.Tests.UI;

/// <summary>
///     The in-run overlays from <c>Play Overlays.dc.html</c>: the count-in's
///     beat pips and the pause header's run figures.
/// </summary>
public sealed class PlayOverlayFactsTests
{
    /// <summary>
    ///     The pips fill as the count runs down. Drawn from the SAME counter the
    ///     figure prints, so a change to the number of count-in steps moves both
    ///     — a hard-coded row of pips is decoration that eventually contradicts
    ///     the digit beside it.
    /// </summary>
    [Theory]
    [InlineData(3, 1)] // first beat: one lit
    [InlineData(2, 2)]
    [InlineData(1, 3)] // last beat: all lit
    [InlineData(0, 3)] // "GO" — the count is over, nothing un-lights
    public void TheBeatPipsFillAsTheCountRunsDown(int countdownValue, int expectedLit)
    {
        var pips = PlayModeViewModel.BuildCountInPips(countdownValue);

        Assert.Equal(PlayModeViewModel.CountdownSteps, pips.Count);
        Assert.Equal(expectedLit, pips.Count(p => p.IsLit));
    }

    /// <summary>The lit ones are a PREFIX — a gap in the middle would read as a
    /// skipped beat.</summary>
    [Theory]
    [InlineData(3)]
    [InlineData(2)]
    [InlineData(1)]
    public void TheLitPipsAreAContiguousPrefix(int countdownValue)
    {
        var lit = PlayModeViewModel.BuildCountInPips(countdownValue).Select(p => p.IsLit).ToList();
        var firstDark = lit.IndexOf(false);
        if (firstDark < 0) return;
        Assert.All(lit.Skip(firstDark), on => Assert.False(on));
    }

    /// <summary>
    ///     A pause with no judged run behind it prints NOTHING rather than
    ///     0 % / 0 / 0. The owner's rule is no stub data, and a zeroed run
    ///     readout is the most convincing stub there is — it looks like a
    ///     measurement.
    /// </summary>
    [Fact]
    public void AnEmptyPauseSnapshotAdvertisesThatItHasNoRun()
    {
        Assert.False(PausedRunFacts.Empty.HasRun);
        Assert.False(PausedRunFacts.Empty.HasSection);
        Assert.Equal("", PausedRunFacts.Empty.AccuracyLabel);
        Assert.Equal("", PausedRunFacts.Empty.ScoreLabel);
    }

    /// <summary>A run before the first section marker has no section, and says
    /// so — it does not guess "intro".</summary>
    [Fact]
    public void ASnapshotWithNoSection_HidesTheSectionLabel()
    {
        var facts = new PausedRunFacts { HasRun = true, ScoreLabel = "1,200" };
        Assert.True(facts.HasRun);
        Assert.False(facts.HasSection);

        Assert.True(new PausedRunFacts { HasRun = true, SectionLabel = "chorus" }.HasSection);
    }

    /// <summary>
    ///     Esc walks the pedalboard's three stacked layers down in the order
    ///     they were opened: the save modal, then the tone drawer, then the
    ///     board. Get the order wrong and Esc closes the whole board out from
    ///     under an open drawer, losing the browser's search and selection.
    /// </summary>
    [Fact]
    public void EscapeUnstacksTheBoardsLayersInOpeningOrder()
    {
        var body = ViewModelSource();
        var start = body.IndexOf("public bool TryCloseOverlay()", StringComparison.Ordinal);
        Assert.True(start > 0, "TryCloseOverlay moved — this guard is blind");
        var chain = body.Substring(start, 2000);

        var save = chain.IndexOf("IsSaveDialogOpen", StringComparison.Ordinal);
        var drawer = chain.IndexOf("IsBrowserOpen", StringComparison.Ordinal);
        var board = chain.IndexOf("if (ToneRig.IsPedalboardOpen) {", StringComparison.Ordinal);

        Assert.True(save > 0 && drawer > 0 && board > 0,
            "one of the three pedalboard layers is no longer handled by Esc");
        Assert.True(save < drawer, "the save modal must close before the tone drawer");
        Assert.True(drawer < board, "the tone drawer must close before the board itself");
    }

    /// <summary>
    ///     The WHOLE view model, not just its root file. <c>PlayModeViewModel</c>
    ///     is one class over many <c>PlayModeViewModel*.cs</c> partials, so
    ///     reading a single one leaves this guard blind the moment
    ///     <c>TryCloseOverlay</c> moves to a sibling — which it did.
    /// </summary>
    private static string ViewModelSource()
    {
        var folder = UiSourceTree.Resolve(Path.Combine(TestPaths.RepoRoot(), "DotCharter.UI", "ViewModels", "PlayMode"));
        var files = UiSourceTree.GetFiles(folder, "PlayModeViewModel*.cs")
            .OrderBy(f => f, StringComparer.Ordinal)
            .ToArray();
        Assert.True(files.Length > 0, "PlayModeViewModel*.cs moved — this guard is blind");
        return string.Join("\n", files.Select(UiSourceTree.ReadAllText));
    }
}
