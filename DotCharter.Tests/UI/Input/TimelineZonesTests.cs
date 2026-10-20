using DotCharter.UI.Constants;
using DotCharter.UI.Input;

namespace DotCharter.Tests.UI.Input;

/// <summary>
///     The timeline's vertical band predicates (<see cref="TimelineZones" />) —
///     shared by <c>ToolCoordinator.DetermineTool</c>, the ruler seek route and
///     the gutter captions, so what a band is labeled, what the cursor promises
///     and what a click does can never disagree.
///     <para>
///         The ruler rule is the live-audit P0: a left press on the time ruler
///         must SEEK (DAW convention). Until 0.59 it fell through the routing —
///         the highway tool armed the marquee and the release cleared the
///         selection, and a click a few pixels above landed in the unlabeled
///         FHP band and silently placed an amber anchor ("clicking the ruler
///         created a persistent yellow region labeled 1").
///     </para>
/// </summary>
public sealed class TimelineZonesTests
{
    private const double H = 600; // viewport height
    private const double Bottom = 58; // UILayoutConstants.HighwayBottomMargin

    [Fact]
    public void TheRulerOwnsTheBottomStrip()
    {
        Assert.True(TimelineZones.IsTimeRuler(H - 1, H));
        Assert.True(TimelineZones.IsTimeRuler(H - TimelineZones.TimeRulerHeightPx, H));
        Assert.False(TimelineZones.IsTimeRuler(H - TimelineZones.TimeRulerHeightPx - 0.5, H));
        Assert.False(TimelineZones.IsTimeRuler(0, H));
        Assert.False(TimelineZones.IsTimeRuler(H - 1, 0)); // degenerate viewport
    }

    [Fact]
    public void TheRulerOverlapsNeitherEditingBand()
    {
        // Sweep the whole bottom margin at sub-pixel steps: no y may belong to
        // the ruler AND an editing band — that overlap is exactly how a click
        // aimed at the ruler silently placed an FHP anchor. (The hand-shape
        // and FHP bands legitimately touch at one inclusive boundary point;
        // DetermineTool's if-order gives the hand shape that pixel.)
        for (var y = H - Bottom; y <= H; y += 0.25)
        {
            if (!TimelineZones.IsTimeRuler(y, H)) continue;
            Assert.False(TimelineZones.IsHandShapeBand(y, H, Bottom),
                $"y={y}: the ruler and the hand-shape band both claim this pixel.");
            Assert.False(TimelineZones.IsFhpBand(y, H, Bottom),
                $"y={y}: the ruler and the FHP band both claim this pixel — the audit's yellow-region bug.");
        }
    }

    [Fact]
    public void TheBandPredicates_MatchTheHistoricRoutingNumbers()
    {
        var laneBottom = H - Bottom;
        var barY = laneBottom + ChartEditorConstants.RenderMetrics.HandShapeBarYOffset;
        var fhpY = laneBottom + ChartEditorConstants.RenderMetrics.FhpYOffset;

        Assert.True(TimelineZones.IsHandShapeBand(barY, H, Bottom));
        Assert.True(TimelineZones.IsHandShapeBand(
            barY + ChartEditorConstants.RenderMetrics.HandShapeActiveBarHeight + 4, H, Bottom));
        Assert.False(TimelineZones.IsHandShapeBand(
            barY + ChartEditorConstants.RenderMetrics.HandShapeActiveBarHeight + 4.5, H, Bottom));

        Assert.True(TimelineZones.IsFhpBand(fhpY, H, Bottom));
        Assert.True(TimelineZones.IsFhpBand(
            fhpY + ChartEditorConstants.RenderMetrics.AnchorTailHeight, H, Bottom));
        Assert.False(TimelineZones.IsFhpBand(
            fhpY + ChartEditorConstants.RenderMetrics.AnchorTailHeight + 0.5, H, Bottom));
    }

    /// <summary>
    ///     Source lint (<c>ToolCoordinator</c> takes a
    ///     <c>MainWindowViewModel</c>; same shape as
    ///     <c>PointerGestureBoundaryTests</c>): the press handler consults the
    ///     ruler zone BEFORE any tool is chosen, seeks through
    ///     <c>ExecuteSeek</c> — the deliberate-navigation funnel — and the band
    ///     routing goes through the shared predicates.
    /// </summary>
    [Fact]
    public void TheCoordinator_SeeksFromTheRuler_AndRoutesBandsThroughTheSharedZones()
    {
        var source = UiSourceTree.ReadAllText(RepoPath("DotCharter.UI/Input/Tools/ToolCoordinator.cs"));

        var press = source.IndexOf("public void HandlePointerPressed", StringComparison.Ordinal);
        var moved = source.IndexOf("public void HandlePointerMoved", StringComparison.Ordinal);
        Assert.True(press >= 0 && moved > press, "ToolCoordinator handlers moved — repoint this lint.");
        var pressBody = source[press..moved];

        var rulerCheck = pressBody.IndexOf("TimelineZones.IsTimeRuler", StringComparison.Ordinal);
        var toolChoice = pressBody.IndexOf("DetermineTool", StringComparison.Ordinal);
        Assert.True(rulerCheck >= 0, "The press handler must route ruler presses to a seek.");
        Assert.True(toolChoice < 0 || rulerCheck < toolChoice,
            "The ruler seek must be decided BEFORE a tool is chosen — after it, a tool already owns the press.");
        Assert.Contains("ExecuteSeek", pressBody);

        var determine = source.IndexOf("private IEditorTool DetermineTool", StringComparison.Ordinal);
        Assert.True(determine >= 0);
        var determineBody = source[determine..];
        Assert.Contains("TimelineZones.IsHandShapeBand", determineBody);
        Assert.Contains("TimelineZones.IsFhpBand", determineBody);
    }

    private static string RepoPath(string relative) =>
        Path.Combine(TestPaths.RepoRoot(), relative.Replace('/', Path.DirectorySeparatorChar));
}
