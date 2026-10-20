using DotCharter.UI.Constants;
using DotCharter.UI.Input;

namespace DotCharter.Tests.UI.Input;

/// <summary>
///     Pure math tests for the decoupled 2D-timeline viewport (0.16 6.6):
///     anchor→view-start mapping, pan clamping to chart bounds, wheel-pan
///     direction and zoom-around-a-screen-pixel anchoring — plus the
///     ProjectState Follow-mode transition semantics the pan/zoom code
///     relies on (unlock freezes the view where it is; the effective anchor
///     is the ONE mapping seam shared by renderer and hit testing).
/// </summary>
public class TimelineViewMathTests(ITestOutputHelper output)
{
    private const double Ppm = 0.5; // px per ms
    private const double TotalMs = 100_000.0;

    // ---------------------------------------------------------------- view start

    [Fact]
    public void ViewStartMs_PlacesAnchorAtPlayheadColumn()
    {
        var start = TimelineViewMath.ViewStartMs(10_000, Ppm);
        Assert.Equal(10_000 - ChartEditorConstants.PlayheadX / Ppm, start, 9);
    }

    [Fact]
    public void ViewStartMs_MatchesFollowModeMapping()
    {
        // Follow ON must be pixel-identical to the historical locked view:
        // same formula the renderer + ChartHitTester.GetViewportBounds use.
        var currentTimeMs = 42_123.75;
        var expected = currentTimeMs - ChartEditorConstants.PlayheadX / Ppm;
        Assert.Equal(expected, TimelineViewMath.ViewStartMs(currentTimeMs, Ppm), 12);
    }

    // ---------------------------------------------------------------- clamping

    [Theory]
    [InlineData(50_000, 50_000)] // in range: untouched
    [InlineData(-2_500, 0)] // below start
    [InlineData(250_000, TotalMs)] // past end
    public void ClampAnchorMs_ClampsToChartBounds(double anchor, double expected) => Assert.Equal(expected, TimelineViewMath.ClampAnchorMs(anchor, TotalMs));

    [Fact]
    public void ClampAnchorMs_NoKnownLength_OnlyLowerBound()
    {
        Assert.Equal(0, TimelineViewMath.ClampAnchorMs(-100, 0));
        Assert.Equal(123_456, TimelineViewMath.ClampAnchorMs(123_456, 0));
        Assert.Equal(99, TimelineViewMath.ClampAnchorMs(99, -1));
    }

    // ---------------------------------------------------------------- panning

    [Fact]
    public void PanByPixels_ConvertsPixelsToMs() =>
        // +200 px at 0.5 px/ms = +400 ms (positive = show later content).
        Assert.Equal(10_400, TimelineViewMath.PanByPixels(10_000, 200, Ppm, TotalMs));

    [Fact]
    public void PanByPixels_ClampsAtBothEnds()
    {
        Assert.Equal(0, TimelineViewMath.PanByPixels(100, -10_000, Ppm, TotalMs));
        Assert.Equal(TotalMs, TimelineViewMath.PanByPixels(TotalMs - 100, 10_000, Ppm, TotalMs));
    }

    [Fact]
    public void PanByPixels_InvalidZoom_IsNoOp() => Assert.Equal(10_000, TimelineViewMath.PanByPixels(10_000, 200, 0, TotalMs));

    [Fact]
    public void PanByWheel_WheelUpScrollsEarlier()
    {
        var panned = TimelineViewMath.PanByWheel(10_000, +1, Ppm, TotalMs);
        Assert.True(panned < 10_000);
        Assert.Equal(10_000 - TimelineViewMath.WheelPanNotchPx / Ppm, panned, 9);

        var pannedDown = TimelineViewMath.PanByWheel(10_000, -1, Ppm, TotalMs);
        Assert.True(pannedDown > 10_000);
    }

    // ---------------------------------------------------------------- zoom anchoring

    [Fact]
    public void ZoomKeepingScreenX_TimeUnderPointerStaysPut()
    {
        const double anchor = 20_000;
        const double screenX = 900; // arbitrary pointer position
        const double oldPpm = 0.4;
        const double newPpm = 0.4 * 1.15;

        var timeUnderPointer = anchor + (screenX - ChartEditorConstants.PlayheadX) / oldPpm;
        var newAnchor = TimelineViewMath.ZoomKeepingScreenX(anchor, screenX, oldPpm, newPpm, TotalMs);
        var timeUnderPointerAfter = newAnchor + (screenX - ChartEditorConstants.PlayheadX) / newPpm;

        Assert.Equal(timeUnderPointer, timeUnderPointerAfter, 9);
    }

    [Fact]
    public void ZoomKeepingScreenX_AtAnchorColumn_IsExactNoOp()
    {
        var newAnchor = TimelineViewMath.ZoomKeepingScreenX(
            20_000, ChartEditorConstants.PlayheadX, 0.4, 0.8, TotalMs);
        Assert.Equal(20_000, newAnchor);
    }

    [Fact]
    public void ZoomKeepingScreenX_ResultIsClamped()
    {
        // Zooming out far with the pointer right of the anchor column pushes
        // the anchor negative — must clamp to the chart start.
        var newAnchor = TimelineViewMath.ZoomKeepingScreenX(
            100, ChartEditorConstants.PlayheadX + 550, 2.0, 0.01, TotalMs);
        Assert.Equal(0, newAnchor);
    }

    [Fact]
    public void ZoomKeepingScreenX_InvalidZoom_IsNoOp()
    {
        Assert.Equal(5_000, TimelineViewMath.ZoomKeepingScreenX(5_000, 100, 0, 0.5, TotalMs));
        Assert.Equal(5_000, TimelineViewMath.ZoomKeepingScreenX(5_000, 100, 0.5, 0, TotalMs));
    }

    [Fact]
    public void DefaultZoomAnchorScreenX_CenterWhenKnown_ElseAnchorColumn()
    {
        Assert.Equal(800, TimelineViewMath.DefaultZoomAnchorScreenX(1600));
        Assert.Equal(ChartEditorConstants.PlayheadX, TimelineViewMath.DefaultZoomAnchorScreenX(0));
    }

    // ---------------------------------------------------------------- follow-mode state

    [Fact]
    public void ProjectState_FollowDefaultsOn_EffectiveAnchorIsPlayhead()
    {
        var ps = new ProjectState { CurrentTimeMs = 7_500 };
        Assert.True(ps.TimelineFollowsPlayhead);
        Assert.Equal(7_500, ps.TimelineViewAnchorEffectiveMs);
    }

    [Fact]
    public void ProjectState_UnlockFreezesViewAtCurrentTime()
    {
        var ps = new ProjectState
        {
            CurrentTimeMs = 12_000, TimelineFollowsPlayhead = false
        };

        Assert.Equal(12_000, ps.TimelineViewAnchorMs);
        Assert.Equal(12_000, ps.TimelineViewAnchorEffectiveMs);

        // Playhead keeps moving (e.g. playback continues) — the view stays.
        ps.CurrentTimeMs = 15_000;
        Assert.Equal(12_000, ps.TimelineViewAnchorEffectiveMs);
    }

    [Fact]
    public void ProjectState_RelockSnapsEffectiveAnchorBackToPlayhead()
    {
        var ps = new ProjectState
        {
            CurrentTimeMs = 12_000, TimelineFollowsPlayhead = false,
            TimelineViewAnchorMs = 60_000
        };
        ps.CurrentTimeMs = 15_000;

        ps.TimelineFollowsPlayhead = true;
        Assert.Equal(15_000, ps.TimelineViewAnchorEffectiveMs);
    }

    // ------------------------------------------------- renderer ↔ hit-test anchor

    /// <summary>
    ///     The renderer resolves its playhead from the LIVE audio clock and adds
    ///     the visual lead; the coalesced <c>CurrentTimeMs</c> trails both. Until
    ///     0.35 hit-testers mapped through the coalesced value, so during playback
    ///     the two disagreed by up to ~100 ms — 100 px at 1 px/ms, roughly four
    ///     note heads — and clicks landed on the wrong chart time.
    /// </summary>
    [Fact]
    public void WhilePlaying_TheHitTesterMapsThroughTheAnchorTheRendererDrew()
    {
        const double ppm = 1.0;   // 1 px per ms: a ms of divergence IS a px of error
        const double screenX = 500.0;

        var ps = new ProjectState { CurrentTimeMs = 10_000 }; // coalesced, lagging
        Assert.True(ps.TimelineFollowsPlayhead);

        // What the frame actually resolves and draws…
        var drawn = TimelineViewMath.ResolveRenderPlayheadMs(
            uiTimeMs: 10_000, audioTimeMs: 10_030, isPlaying: true, visualLeadMs: 40);
        Assert.Equal(10_070, drawn, 9);

        // …published once per frame by ChartRenderer.
        ps.TimelineRenderedAnchorMs = drawn;

        static double TimeAtPointer(double anchorMs) =>
            TimelineViewMath.ViewStartMs(anchorMs, ppm) + screenX / ppm;

        // Renderer and hit-tester now answer the same question the same way.
        Assert.Equal(TimeAtPointer(drawn), TimeAtPointer(ps.TimelineViewAnchorEffectiveMs), 9);

        // And the gap that used to exist is real, not theoretical.
        Assert.Equal(70, TimeAtPointer(drawn) - TimeAtPointer(ps.CurrentTimeMs), 9);
    }

    [Fact]
    public void WithNoLiveFrame_TheEffectiveAnchorFallsBackToTheCoalescedPlayhead()
    {
        // Paused (or before the first frame) the renderer publishes NaN — the two
        // agree anyway, and a stale published value would survive a seek.
        var ps = new ProjectState
        {
            CurrentTimeMs = 4_200, TimelineRenderedAnchorMs = double.NaN
        };

        Assert.Equal(4_200, ps.TimelineViewAnchorEffectiveMs);
    }

    [Fact]
    public void WhilePanned_ThePanAnchorStillWinsOverAPublishedRenderAnchor()
    {
        var ps = new ProjectState
        {
            CurrentTimeMs = 12_000, TimelineFollowsPlayhead = false,
            TimelineViewAnchorMs = 60_000,
            TimelineRenderedAnchorMs = 12_050
        };

        Assert.Equal(60_000, ps.TimelineViewAnchorEffectiveMs);
        Assert.Equal(60_000,
            TimelineViewMath.ResolveViewAnchorMs(12_050, followsPlayhead: false, panAnchorMs: 60_000));
    }

    [Fact]
    public void ResolveRenderPlayheadMs_IgnoresTheLeadAndTheAudioClockWhenNotPlaying()
    {
        // A parked playhead must never be led — that would draw the wrong time.
        Assert.Equal(8_000, TimelineViewMath.ResolveRenderPlayheadMs(
            uiTimeMs: 8_000, audioTimeMs: 8_020, isPlaying: false, visualLeadMs: 40), 9);
    }

    [Fact]
    public void ResolveRenderPlayheadMs_DistrustsAnAudioClockThatIsFarFromTheUiValue()
    {
        // A gap this big means a seek is in flight; the UI value is the one the
        // rest of the app agrees on. The lead still applies — we are playing.
        Assert.Equal(8_040, TimelineViewMath.ResolveRenderPlayheadMs(
            uiTimeMs: 8_000, audioTimeMs: 25_000, isPlaying: true, visualLeadMs: 40), 9);
    }

    [Fact]
    public void ProjectState_TimelineViewAnchorMs_RaisesChangeOnce()
    {
        var ps = new ProjectState();
        var raised = 0;
        ps.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName == nameof(ProjectState.TimelineViewAnchorMs)) raised++;
        };

        ps.TimelineViewAnchorMs = 5_000;
        ps.TimelineViewAnchorMs = 5_000; // same value: no re-raise
        Assert.Equal(1, raised);
    }

    // ------------------------------------------------- 2× playback jitter (2026-08-30)

    /// <summary>
    ///     Owner report 2026-08-30: at 2× in the chart editor the whole 2D
    ///     viewport jitters — notes, bars AND markers, i.e. the scroll anchor
    ///     itself. The anchor is <see cref="TimelineViewMath.ResolveRenderPlayheadMs" />,
    ///     which trusts the live audio clock only while it sits within
    ///     <see cref="TimelineViewMath.MaxAudioClockLagMs" /> of the coalesced UI
    ///     value. That budget is WALL time (≤60 Hz publish + dispatch) but both
    ///     clocks are SONG time, which at 2× runs twice as fast — so the ordinary
    ///     de-coalescing gap crossed it every few frames and the frame fell back
    ///     to the stale UI value: a ~50 song-ms step back, then forward again.
    ///     This drives the coordinator's publish rule and the renderer's frame
    ///     sampling with a deterministic clock and counts the backward steps.
    /// </summary>
    private static (int backwardSteps, int frames) SimulateDrawnPlayhead(double speed, double speedPassedToResolver)
    {
        const double tickMs = 4.0;            // the 250 Hz position clock
        const double tickCountResMs = 15.625; // Environment.TickCount64 resolution behind the ≤60 Hz gate
        const double dispatchMs = 3.0;        // Dispatcher.UIThread.Post latency, UI thread idle
        const double frameMs = 1000.0 / 60;
        const double durationMs = 10_000;

        var uiVisibleMs = 0.0;   // ProjectState.CurrentTimeMs as the frame sees it
        var pendingMs = 0.0;     // published, not yet on the UI thread
        var pendingAt = -1.0;    // wall time at which the pending value lands
        var lastGate = double.NegativeInfinity;

        var backward = 0;
        var frames = 0;
        var lastDrawn = double.NegativeInfinity;
        var nextFrame = 7.3; // arbitrary phase against the tick grid

        for (var t = 0.0; t < durationMs; t += tickMs)
        {
            // Frames that fell due before this tick.
            while (nextFrame <= t)
            {
                if (pendingAt >= 0 && pendingAt <= nextFrame) { uiVisibleMs = pendingMs; pendingAt = -1; }
                var audioClock = nextFrame * speed; // the service's interpolated CurrentTimeMs
                var drawn = TimelineViewMath.ResolveRenderPlayheadMs(
                    uiVisibleMs, audioClock, isPlaying: true, visualLeadMs: 0, playbackSpeed: speedPassedToResolver);
                if (drawn < lastDrawn) backward++;
                lastDrawn = drawn;
                frames++;
                nextFrame += frameMs;
            }

            if (pendingAt >= 0 && pendingAt <= t) { uiVisibleMs = pendingMs; pendingAt = -1; }

            // AudioPlaybackCoordinator.OnAudioPositionChanged's publish rule.
            var newTime = t * speed;
            var coarse = Math.Floor(t / tickCountResMs) * tickCountResMs;
            var jumped = Math.Abs(newTime - uiVisibleMs) > 50;
            if (jumped || coarse - lastGate >= 16)
            {
                lastGate = coarse;
                pendingMs = newTime;
                if (pendingAt < 0) pendingAt = t + dispatchMs; // one coalesced post in flight
            }
        }

        return (backward, frames);
    }

    [Fact]
    public void ResolveRenderPlayheadMs_AtDoubleSpeed_NeverDrawsTheViewportBehindThePreviousFrame()
    {
        var (before, framesBefore) = SimulateDrawnPlayhead(speed: 2.0, speedPassedToResolver: 1.0); // the old rule
        var (after, framesAfter) = SimulateDrawnPlayhead(speed: 2.0, speedPassedToResolver: 2.0);

        output.WriteLine($"2x: backward viewport steps over {framesBefore} frames (10 s): " +
                         $"old rule {before}, speed-scaled rule {after}");
        // The old rule is the bug, and this proves the simulation reproduces it.
        Assert.True(before > 0, "the simulation no longer reproduces the 2× fallback jitter");
        Assert.Equal(0, after);
        Assert.Equal(framesBefore, framesAfter);
    }

    [Fact]
    public void ResolveRenderPlayheadMs_AtNormalSpeed_WasAndIsSmooth()
    {
        // 1× was never reported; the speed factor must not change it.
        Assert.Equal(0, SimulateDrawnPlayhead(speed: 1.0, speedPassedToResolver: 1.0).backwardSteps);
        Assert.Equal(0, SimulateDrawnPlayhead(speed: 0.5, speedPassedToResolver: 0.5).backwardSteps);
    }

    [Fact]
    public void SpeedFactor_NeverShrinksTheBudgetBelowOneTimes()
    {
        Assert.Equal(1.0, TimelineViewMath.SpeedFactor(0.5));
        Assert.Equal(1.0, TimelineViewMath.SpeedFactor(1.0));
        Assert.Equal(2.0, TimelineViewMath.SpeedFactor(2.0));
        Assert.Equal(1.0, TimelineViewMath.SpeedFactor(double.NaN));
        Assert.Equal(1.0, TimelineViewMath.SpeedFactor(0));
    }

    /// <summary>The fix is inert unless the renderer hands the speed over —
    /// the parameter has a default of 1.0, so an omitted argument compiles.</summary>
    [Fact]
    public void ChartRenderer_PassesThePlaybackSpeedToTheResolver()
    {
        var path = UiSourceTree.Resolve(Path.Combine(TestPaths.RepoRoot(), "DotCharter.UI", "Controls", "ChartRenderer.cs"));
        var src = UiSourceTree.ReadAllText(path);
        var call = src.IndexOf("TimelineViewMath.ResolveRenderPlayheadMs(", StringComparison.Ordinal);
        Assert.True(call >= 0, "ChartRenderer no longer resolves the drawn playhead through TimelineViewMath");
        var close = src.IndexOf(");", call, StringComparison.Ordinal);
        var args = src.Substring(call, close - call);
        Assert.Contains("PlaybackSpeed", args);
    }

    // ---------------------------------------------------------------- visual lead vs speed

    /// <summary>
    ///     The visual lead is WALL ms (present latency) added to a SONG-ms clock,
    ///     and song time runs <c>speed</c> times faster than the wall: in
    ///     <c>lead</c> wall-ms the audio advances <c>lead × speed</c> song-ms.
    ///     Unscaled, 2× drew half a lead behind the audio — a constant A/V
    ///     offset, the same wall-vs-song confusion <c>SpeedFactor</c> fixed for
    ///     the lag budget one expression earlier. At 1× nothing changes.
    /// </summary>
    [Theory]
    [InlineData(1.0, 20.0)]
    [InlineData(2.0, 40.0)]
    [InlineData(0.5, 10.0)]
    [InlineData(1.5, 30.0)]
    public void ResolveRenderPlayheadMs_ScalesTheLeadByThePlaybackSpeed(double speed, double expectedLead)
    {
        const double clock = 10_000.0;
        var led = TimelineViewMath.ResolveRenderPlayheadMs(clock, clock, isPlaying: true, visualLeadMs: 20.0, playbackSpeed: speed);
        Assert.Equal(clock + expectedLead, led, 9);
    }

    [Theory]
    [InlineData(0.0)]
    [InlineData(-1.0)]
    [InlineData(double.NaN)]
    [InlineData(double.PositiveInfinity)]
    public void LeadScale_IsOneForAnythingButAPositiveFiniteSpeed(double speed) =>
        Assert.Equal(1.0, DotCharter.UI.Controls.VisualLeadTiming.LeadScale(speed));

    [Fact]
    public void LeadScale_IsLinear_UnlikeTheLagBudgetFactor()
    {
        Assert.Equal(0.5, DotCharter.UI.Controls.VisualLeadTiming.LeadScale(0.5));
        Assert.Equal(2.0, DotCharter.UI.Controls.VisualLeadTiming.LeadScale(2.0));
        // The lag budget floors at 1 on purpose; the lead must not.
        Assert.Equal(1.0, TimelineViewMath.SpeedFactor(0.5));
    }
}
