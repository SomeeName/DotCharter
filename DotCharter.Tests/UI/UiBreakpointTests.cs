using DotCharter.UI.Services;

namespace DotCharter.Tests.UI;

/// <summary>
///     The width -> layout-shape table, asserted without a window.
///     <para>
///         <c>UiBreakpointController</c> is split the way
///         <c>UiScaleController</c>/<c>AutoUiScale</c> are, and for the same
///         reason: the POLICY (thresholds, hysteresis, which width to read) is
///         pure and belongs in the default gate, while the wiring needs a real
///         window and lives in <c>Perf/UiBreakpointUiPerfSmokeTests</c>.
///     </para>
/// </summary>
public sealed class UiBreakpointTests
{
    // ── the table ─────────────────────────────────────────────────────────

    [Theory]
    // Below 1600 is Compact. A 1366-wide laptop, a half-snapped 1080p window.
    [InlineData(1024, UiBreakpoint.Compact)]
    [InlineData(1366, UiBreakpoint.Compact)]
    [InlineData(1599.9, UiBreakpoint.Compact)]
    // 1600..2560 inclusive is the DESIGN band — the shape everything was
    // authored against, and the ordinary 1080p and 1440p user's shape.
    [InlineData(1600, UiBreakpoint.Default)]
    [InlineData(1920, UiBreakpoint.Default)]
    [InlineData(2560, UiBreakpoint.Default)]
    // Above 2560 is Wide.
    [InlineData(2560.1, UiBreakpoint.Wide)]
    [InlineData(3440, UiBreakpoint.Wide)]
    [InlineData(3840, UiBreakpoint.Wide)]
    public void EachWidth_MapsToItsShape(double width, UiBreakpoint expected) =>
        Assert.Equal(expected, UiBreakpointController.For(width));

    /// <summary>A window mid-teardown reports zeroes and a detached one can
    /// report NaN. Neither may throw, and neither may be reported as a SHAPE —
    /// "we do not know" has to read as the design band, or a closing window
    /// would flip the whole app to Compact on its way out.</summary>
    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    [InlineData(double.NaN)]
    [InlineData(double.PositiveInfinity)]
    public void ANonsenseWidth_IsTheDesignShape(double width) =>
        Assert.Equal(UiBreakpoint.Default, UiBreakpointController.For(width));

    /// <summary>The design shape wears no class at all: the unqualified
    /// selector IS the default look, so nothing has to be written twice and a
    /// view cannot get it subtly wrong by styling `Window.Default`.</summary>
    [Fact]
    public void OnlyTheTwoEdgeShapes_HaveAClass()
    {
        Assert.Null(UiBreakpointController.ClassFor(UiBreakpoint.Default));
        Assert.Equal("Compact", UiBreakpointController.ClassFor(UiBreakpoint.Compact));
        Assert.Equal("Wide", UiBreakpointController.ClassFor(UiBreakpoint.Wide));
        Assert.Equal(["Compact", "Wide"], UiBreakpointController.AllClasses);
    }

    // ── hysteresis ────────────────────────────────────────────────────────

    /// <summary>
    ///     A width parked ON a threshold must not flap. Without the band a
    ///     window resting within a pixel of 2560 alternates on every delivered
    ///     size, and each alternation re-styles the whole tree AND re-resolves
    ///     every <c>DynamicResource</c> in it.
    /// </summary>
    [Theory]
    // Just past a threshold, but not past it by the margin: hold.
    [InlineData(1610, UiBreakpoint.Compact, UiBreakpoint.Compact)]
    [InlineData(1663, UiBreakpoint.Compact, UiBreakpoint.Compact)]
    [InlineData(1590, UiBreakpoint.Default, UiBreakpoint.Default)]
    [InlineData(2570, UiBreakpoint.Default, UiBreakpoint.Default)]
    [InlineData(2550, UiBreakpoint.Wide, UiBreakpoint.Wide)]
    // Past it by the margin: move.
    [InlineData(1664, UiBreakpoint.Compact, UiBreakpoint.Default)]
    [InlineData(1535, UiBreakpoint.Default, UiBreakpoint.Compact)]
    [InlineData(2625, UiBreakpoint.Default, UiBreakpoint.Wide)]
    [InlineData(2496, UiBreakpoint.Wide, UiBreakpoint.Default)]
    // Already there: no band involved.
    [InlineData(1920, UiBreakpoint.Default, UiBreakpoint.Default)]
    [InlineData(3840, UiBreakpoint.Wide, UiBreakpoint.Wide)]
    public void TheShapeIsSticky_UntilTheWidthHasLeftItsBand(
        double width, UiBreakpoint current, UiBreakpoint expected) =>
        Assert.Equal(expected, UiBreakpointController.Next(width, current));

    /// <summary>The band is SYMMETRIC: the same 64 DIPs of slack applies
    /// whichever way the window edge is being dragged. An asymmetric one would
    /// make a drag out and back leave the app in a different shape than it
    /// started, which is the bug the hysteresis exists to prevent.</summary>
    [Fact]
    public void ADragOutAndBack_EndsWhereItStarted()
    {
        var shape = UiBreakpoint.Default;
        foreach (var w in new[] { 1920.0, 2400, 2700, 3200, 3840, 3200, 2700, 2400, 1920 })
            shape = UiBreakpointController.Next(w, shape);

        Assert.Equal(UiBreakpoint.Default, shape);
    }

    /// <summary>
    ///     The coalescing claim, stated as a number. A drag-resize delivers a
    ///     size per frame; what must be bounded is not the number of
    ///     EVALUATIONS (a division and a compare) but the number of SHAPE
    ///     CHANGES, because each one re-styles the tree. A 1080p-to-4K drag in
    ///     20 px steps is 960 deliveries and must cost exactly ONE.
    /// </summary>
    [Fact]
    public void ADragAcrossOneThreshold_ChangesTheShapeExactlyOnce()
    {
        var shape = UiBreakpoint.Default;
        var changes = 0;
        for (var w = 1920.0; w <= 3840; w += 20)
        {
            var next = UiBreakpointController.Next(w, shape);
            if (next == shape) continue;
            changes++;
            shape = next;
        }

        Assert.Equal(1, changes);
        Assert.Equal(UiBreakpoint.Wide, shape);
    }

    /// <summary>…and a window jittering on the threshold costs NONE. Dragging
    /// the edge back and forth across 2560 by less than the margin is the exact
    /// gesture that would otherwise re-style the app once per frame.</summary>
    [Fact]
    public void AJitterOnTheThreshold_NeverChangesTheShape()
    {
        var shape = UiBreakpoint.Default;
        var changes = 0;
        for (var i = 0; i < 200; i++)
        {
            var next = UiBreakpointController.Next(2560 + (i % 2 == 0 ? 12 : -12), shape);
            if (next == shape) continue;
            changes++;
            shape = next;
        }

        Assert.Equal(0, changes);
    }

    // ── which width ───────────────────────────────────────────────────────

    /// <summary>
    ///     <b>The decision the whole mechanism rests on.</b> The shell lives
    ///     inside <c>MainWindow</c>'s <c>RootScaler</c>
    ///     <c>LayoutTransformControl</c>, so its content is laid out at
    ///     <c>ClientSize / scale</c> logical pixels and drawn at <c>scale</c>.
    ///     A breakpoint decides layout SHAPE, and shape is decided in the
    ///     coordinate space the content actually occupies — which is the inner
    ///     one. Every token in <c>Ladders.Wide.axaml</c> is a number in those
    ///     same inner DIPs.
    /// </summary>
    [Theory]
    [InlineData(3840, 1.0, 3840)]
    [InlineData(3840, 1.5, 2560)]
    [InlineData(2560, 1.0, 2560)]
    [InlineData(1920, 0.75, 2560)]
    public void TheWidthIsTheInnerOne(double clientWidth, double scale, double expected) =>
        Assert.Equal(expected, UiBreakpointController.LogicalWidthOf(clientWidth, scale), 6);

    /// <summary>
    ///     Stated as the case it exists for: a 4K window at 150 % is the DESIGN
    ///     shape, not the wide one. The user asked for bigger content, not a
    ///     different layout, and their content genuinely has 2560 logical
    ///     pixels. Keying on the outer width would hand a 1400 px reading
    ///     column to a 2560-DIP viewport.
    /// </summary>
    [Fact]
    public void A4KWindowAt150Percent_IsTheDesignShape_NotWide()
    {
        Assert.Equal(UiBreakpoint.Wide,
            UiBreakpointController.For(UiBreakpointController.LogicalWidthOf(3840, 1.0)));
        Assert.Equal(UiBreakpoint.Default,
            UiBreakpointController.For(UiBreakpointController.LogicalWidthOf(3840, 1.5)));
        // …and at 2.0 it is genuinely a small screen's worth of room.
        Assert.Equal(UiBreakpoint.Compact,
            UiBreakpointController.For(UiBreakpointController.LogicalWidthOf(3840, 2.6)));
    }

    /// <summary>A nonsense scale must not turn a real width into a NaN that
    /// then reads as "unknown" — the size-changed handler can run while the
    /// scale is mid-write.</summary>
    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    [InlineData(double.NaN)]
    public void ANonsenseScale_LeavesTheWidthAlone(double scale) =>
        Assert.Equal(1920, UiBreakpointController.LogicalWidthOf(1920, scale));

    /// <summary>The margin has to stay strictly inside half the narrowest band,
    /// or a shape could be unreachable. The design band is 960 DIPs wide, so 64
    /// has plenty of room — this guards a future edit to either number.</summary>
    [Fact]
    public void TheHysteresisMargin_CannotSwallowABand()
    {
        var narrowest = UiBreakpointController.WideAbove - UiBreakpointController.CompactBelow;
        Assert.True(UiBreakpointController.HysteresisMargin * 2 < narrowest,
            $"a margin of {UiBreakpointController.HysteresisMargin} on each side cannot fit inside a " +
            $"{narrowest} DIP band — some widths would have no reachable shape.");
    }
}
