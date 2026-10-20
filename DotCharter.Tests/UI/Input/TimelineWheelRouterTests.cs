using DotCharter.UI.Input;

namespace DotCharter.Tests.UI.Input;

/// <summary>
///     The wheel mapping is the OWNER'S (2026-08-11): a bare notch runs the
///     edit-adjust chain (<see cref="TimelineWheelAdjuster" />) — the charting
///     workflow he has muscle memory for. PAN is the opt-in, on Alt, which keeps
///     Alt the one coherent VIEW modifier (Alt+wheel pans exactly like
///     Alt+left-drag). A bare notch the chain does not claim does NOTHING —
///     it panned until 2026-08-22; see <see cref="AnUnclaimedBareNotch_DoesNothing" />.
///     The 0.59 audit briefly flipped this (plain = pan, Alt = edit); the owner
///     reversed it the same day. Do not re-flip without an explicit owner
///     decision. Ctrl stays zoom; Shift is deliberately not consulted
///     (precision touchpads report Shift-wheel as a horizontal delta, so a
///     Shift verb would be device-dependent).
/// </summary>
public sealed class TimelineWheelRouterTests
{
    [Fact]
    public void APlainNotchAdjusts_TheOwnersMapping() => Assert.Equal(TimelineWheelRoute.Adjust, TimelineWheelRouter.Classify(ctrl: false, alt: false));

    [Fact]
    public void CtrlZooms_AndWinsOverAlt()
    {
        Assert.Equal(TimelineWheelRoute.Zoom, TimelineWheelRouter.Classify(ctrl: true, alt: false));
        // A user holding both asked for two things; zoom is the non-destructive one.
        Assert.Equal(TimelineWheelRoute.Zoom, TimelineWheelRouter.Classify(ctrl: true, alt: true));
    }

    [Fact]
    public void AltIsTheOnlyRouteIntoPan()
    {
        Assert.Equal(TimelineWheelRoute.Pan, TimelineWheelRouter.Classify(ctrl: false, alt: true));

        // Exhaustive inverse: no other modifier combination reaches Pan — the
        // bare notch belongs to the edit chain.
        foreach (var ctrl in new[] { false, true })
        {
            Assert.True(
                TimelineWheelRouter.Classify(ctrl, alt: false) != TimelineWheelRoute.Pan,
                "Pan must be reachable ONLY through Alt — the bare wheel is the owner's edit gesture.");
        }
    }

    /// <summary>
    ///     Source lint — the router only helps if the wheel handler routes
    ///     through it, and the ADJUST case must keep its pan FALLBACK (a notch
    ///     the chain does not claim moves the view — the pre-0.59 behavior the
    ///     owner asked to keep).
    /// </summary>
    [Fact]
    public void AnUnclaimedBareNotch_DoesNothing() =>
        // Owner, 2026-08-22, and the reason is EOF: the wheel is reserved for
        // LENGTH (hand shape, note sustain, lyric) and never moves the view.
        // The fallback used to pan, so the same gesture edited or scrolled
        // depending on where the pointer sat — a notch that missed the note by
        // one lane scrolled the chart away instead. Panning is Alt+wheel.
        Assert.Equal(TimelineWheelFallback.None, TimelineWheelRouter.UnclaimedBareNotch);

    [Fact]
    public void TheWheelHandlerRoutesThroughTheRouter_ForBothTheModifierAndTheFallback()
    {
        var source = UiSourceTree.ReadAllText(RepoPath("DotCharter.UI/Views/MainWindow.Input.cs"));
        var start = source.IndexOf("private void Timeline_PointerWheelChanged", StringComparison.Ordinal);
        Assert.True(start >= 0, "Timeline_PointerWheelChanged has moved — repoint this lint.");
        var next = source.IndexOf("private void ", start + 1, StringComparison.Ordinal);
        var body = next < 0 ? source[start..] : source[start..next];

        Assert.Contains("TimelineWheelRouter.Classify", body);

        // The Adjust case may only pan by CONSULTING the seam above — never
        // unconditionally. Anything else re-buries the decision in the view.
        Assert.Contains("TimelineWheelRouter.UnclaimedBareNotch", body);
        var adjust = body.IndexOf("case TimelineWheelRoute.Adjust", StringComparison.Ordinal);
        var defaultCase = body.IndexOf("default:", StringComparison.Ordinal);
        Assert.True(adjust >= 0 && defaultCase > adjust, "The wheel switch has been restructured — repoint this lint.");
        var adjustCase = body[adjust..defaultCase];
        if (adjustCase.Contains("PanByWheel", StringComparison.Ordinal))
            Assert.Contains("UnclaimedBareNotch", adjustCase);
    }

    // ── a notch with no vertical delta is not a notch (2026-09-04) ───────────
    //
    // The handler read `e.Delta.Y > 0 ? 1 : -1`, so a delta of exactly 0 was a
    // notch DOWNWARD. A precision touchpad reports a horizontal two-finger
    // scroll as (±x, 0) — and, per the table this class pins, a Shift+wheel on
    // one too, which is the whole reason Shift is deliberately not consulted.
    // Every sideways flick over the lanes therefore shortened the selected
    // sustain (or the hovered hand shape, or the lyric) by a grid division, and
    // Ctrl+sideways zoomed OUT.

    [Theory]
    [InlineData(1.0, 1)]
    [InlineData(0.25, 1)]
    [InlineData(-1.0, -1)]
    [InlineData(-0.25, -1)]
    public void AVerticalDeltaIsOneNotchInItsOwnDirection(double deltaY, int expected) => Assert.Equal(expected, TimelineWheelRouter.NotchesFrom(deltaY));

    [Fact]
    public void AHorizontalOnlyScrollIsNotANotchAtAll()
    {
        Assert.Equal(0, TimelineWheelRouter.NotchesFrom(0));
        Assert.Equal(0, TimelineWheelRouter.NotchesFrom(-0.0));
        Assert.Equal(0, TimelineWheelRouter.NotchesFrom(double.NaN));
    }

    [Fact]
    public void TheWheelHandlerAsksTheRouterHowManyNotchesItGot()
    {
        // Source lint: the seam only helps if the handler uses it, and it must
        // RETURN on zero rather than fall through to a branch.
        var source = UiSourceTree.ReadAllText(RepoPath("DotCharter.UI/Views/MainWindow.Input.cs"));
        var start = source.IndexOf("private void Timeline_PointerWheelChanged", StringComparison.Ordinal);
        Assert.True(start >= 0, "Timeline_PointerWheelChanged has moved — repoint this lint.");
        var next = source.IndexOf("private void ", start + 1, StringComparison.Ordinal);
        var body = next < 0 ? source[start..] : source[start..next];

        Assert.Contains("TimelineWheelRouter.NotchesFrom(e.Delta.Y)", body);
        Assert.DoesNotContain("e.Delta.Y > 0", body);
    }

    private static string RepoPath(string relative) =>
        Path.Combine(TestPaths.RepoRoot(), relative.Replace('/', Path.DirectorySeparatorChar));
}
