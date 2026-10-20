using DotCharter.UI.Layout;

namespace DotCharter.Tests.UI;

/// <summary>
///     Restoring the saved timeline/dock split, which had two faults sharing one
///     expression.
///
///     <para>
///         <b>Mixed coordinate spaces.</b> <c>MainSplitGrid</c> lives inside
///         <c>MainWindow</c>'s <c>RootScaler</c> layout transform, so
///         <c>AppConfig.LayoutDockHeight</c> is measured in the grid's INNER,
///         scale-divided pixels — while <c>AppConfig.WindowHeight</c> is the
///         window's OUTER DIPs. The restore capped and subtracted one against the
///         other, so the UI-scale accessibility setting silently rewrote the
///         user's charting split on the next launch.
///     </para>
///     <para>
///         <b>A negative star value throws.</b> <c>WindowHeight</c> is used
///         unguarded here (the window's own size is checked with <c>&gt; 200</c>),
///         and <c>GridLength</c> refuses a negative — inside <c>MainWindow</c>'s
///         <c>Loaded</c> handler, whose FIRST statement this is, so the throw took
///         the dock-layout restore, the gameplay wiring, the focus anchor, the
///         prewarm pass and the autopilot arm with it.
///     </para>
/// </summary>
public sealed class DockRowStarsTests
{
    /// <summary>At 100 % nothing changes — the historical behaviour is the
    /// baseline every other case is measured against.</summary>
    [Fact]
    public void AtIdentityScale_TheSavedSplitIsRestoredAsItWas()
    {
        var (timeline, dock) = MainWindowLayoutController.DockRowStars(300, 900, 1.0);

        Assert.Equal(300, dock);
        Assert.Equal(900 - 6 - 300 - 60, timeline);
    }

    /// <summary>
    ///     The bug. The user arranged their split at 200 % — where the grid only
    ///     ever sees 450 inner px of a 900 DIP window — and the dock row measured
    ///     150. Restoring against the OUTER 900 makes the dock look tiny beside
    ///     it; the ratio must come out the same as the one that was saved.
    /// </summary>
    [Fact]
    public void AtADoubledUiScale_TheRatioIsThePersistedOne_NotAnEighthOfIt()
    {
        var (timeline, dock) = MainWindowLayoutController.DockRowStars(150, 900, 2.0);

        // 900 outer / 2.0 = 450 inner, shared as 450 - 6 - 150 - 60.
        Assert.Equal(150, dock);
        Assert.Equal(234, timeline);

        var restoredShare = dock / (dock + timeline);
        // The un-scaled reading produced 150 / (150 + 684) — barely half of it.
        Assert.True(restoredShare > 0.35,
            $"the dock kept only {restoredShare:P0} of the split it was saved with");
    }

    /// <summary>The cap is still a cap: a stale over-sized dock height may never
    /// starve the timeline, and it is applied in the grid's space too.</summary>
    [Fact]
    public void AnOversizedSavedDock_IsStillCappedAtHalfTheSharedSpace()
    {
        var (_, dock) = MainWindowLayoutController.DockRowStars(5000, 900, 2.0);
        Assert.Equal(225, dock); // 450 inner / 2
    }

    /// <summary>
    ///     The throw. A hand-edited or corrupt negative window height made the
    ///     half-window cap negative, and <c>new GridLength(negative, Star)</c>
    ///     throws inside <c>Loaded</c>. Both values must always be usable and
    ///     positive, whatever the config says.
    /// </summary>
    [Theory]
    [InlineData(300, -5000, 1.0)]
    [InlineData(300, 0, 1.0)]
    [InlineData(300, double.NaN, 1.0)]
    [InlineData(300, 900, 0)]
    [InlineData(300, 900, double.NaN)]
    [InlineData(double.NaN, 900, 1.0)]
    [InlineData(-40, 900, 1.0)]
    // A window genuinely too small to hold both floors: neither row may go
    // negative to make room for the other.
    [InlineData(300, 120, 1.0)]
    public void NoConfigValue_CanProduceANegativeOrNonFiniteStar(
        double savedDockPx, double windowHeight, double scale)
    {
        var (timeline, dock) = MainWindowLayoutController.DockRowStars(savedDockPx, windowHeight, scale);

        Assert.True(double.IsFinite(timeline) && timeline > 0,
            $"timeline star was {timeline} — GridLength would throw inside Loaded.");
        Assert.True(double.IsFinite(dock) && dock > 0,
            $"dock star was {dock} — GridLength would throw inside Loaded.");
    }

    /// <summary>The floors are the ones the rest of the shell uses, not
    /// invented here: the dock's own 80 px minimum and the timeline's 120.</summary>
    [Fact]
    public void TheFloorsAreTheShellsOwn()
    {
        var (timeline, dock) = MainWindowLayoutController.DockRowStars(1, 200, 1.0);
        Assert.Equal(MainWindowLayoutController.MinDockRowPx, dock);
        Assert.Equal(MainWindowLayoutController.MinTimelineRowPx, timeline);
    }
}
