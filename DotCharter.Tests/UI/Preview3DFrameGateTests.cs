using DotCharter.UI.Controls;
using DotCharter.Core.Graphics;

namespace DotCharter.Tests.UI;

/// <summary>
///     0.14 P6 — pure idle-gate decision for the 3D preview's free-running
///     render thread. The invariants under test: a fully idle loop parks, any
///     single wake source renders immediately, and the parked loop still
///     trickles at ≤ 10 Hz as the safety net for state without a wake signal.
/// </summary>
public class Preview3DFrameGateTests
{
    private static bool Idle(double msSinceLastRender = 0) =>
        Preview3DFrameGate.ShouldRender(
            isPlaying: false, renderRequested: false, timeChanged: false,
            sizeChanged: false, cameraAnimating: false, msSinceLastRender: msSinceLastRender);

    [Fact]
    public void FullyIdle_Parks() => Assert.False(Idle());

    [Fact]
    public void Playing_RendersEveryIteration() => Assert.True(Preview3DFrameGate.ShouldRender(true, false, false, false, false, 0));

    [Fact]
    public void WakeRequest_RendersImmediately() => Assert.True(Preview3DFrameGate.ShouldRender(false, true, false, false, false, 0));

    [Fact]
    public void ClockMoved_RendersImmediately() => Assert.True(Preview3DFrameGate.ShouldRender(false, false, true, false, false, 0));

    [Fact]
    public void SizeChanged_RendersImmediately() => Assert.True(Preview3DFrameGate.ShouldRender(false, false, false, true, false, 0));

    [Fact]
    public void CameraStillEasing_KeepsRendering() => Assert.True(Preview3DFrameGate.ShouldRender(false, false, false, false, true, 0));

    [Fact]
    public void IdleTrickle_FiresAtTheThreshold_NotBefore()
    {
        Assert.False(Idle(Preview3DFrameGate.IdleTrickleMs - 1));
        Assert.True(Idle(Preview3DFrameGate.IdleTrickleMs));
    }

    [Fact]
    public void IdleTrickle_IsAtMostTenHz() => Assert.True(Preview3DFrameGate.IdleTrickleMs >= 100);

    [Fact]
    public void IdlePoll_IsFastEnoughForImmediateWake() =>
        // The "first frame after a change renders immediately" contract:
        // one poll interval must stay well under a display refresh period.
        Assert.InRange(Preview3DFrameGate.IdlePollMs, 1, 8);

    // ---- 0.14.2 fps cap + configurable idle trickle ----

    private static Preview3DFrameGate.FrameDecision Playing(double msSinceLastRender, int maxFps,
        int idleFps = GraphicsSettingsPolicy.DefaultIdleFps) =>
        Preview3DFrameGate.Decide(
            isPlaying: true, renderRequested: false, timeChanged: false,
            sizeChanged: false, cameraAnimating: false,
            msSinceLastRender: msSinceLastRender, maxFps: maxFps, idleFps: idleFps);

    [Fact]
    public void Uncapped_Playing_RendersEveryIteration() => Assert.Equal(Preview3DFrameGate.FrameDecision.Render, Playing(0, 0));

    [Fact]
    public void Cap_BlocksEarlyFrames_AndReleasesAtTheInterval()
    {
        // 60 fps cap = 16.67 ms interval.
        Assert.Equal(Preview3DFrameGate.FrameDecision.WaitForFpsCap, Playing(0, 60));
        Assert.Equal(Preview3DFrameGate.FrameDecision.WaitForFpsCap, Playing(16.0, 60));
        Assert.Equal(Preview3DFrameGate.FrameDecision.Render, Playing(1000.0 / 60, 60));
        Assert.Equal(Preview3DFrameGate.FrameDecision.Render, Playing(30, 60));
    }

    [Fact]
    public void CapWait_IsNotAPark_FullyIdleStillParks()
    {
        // A cap-blocked playing loop must report WaitForFpsCap (real frame
        // interval, wake flags stay armed) while a fully idle loop parks.
        Assert.Equal(Preview3DFrameGate.FrameDecision.WaitForFpsCap, Playing(1, 60));
        Assert.Equal(Preview3DFrameGate.FrameDecision.Park,
            Preview3DFrameGate.Decide(false, false, false, false, false, 1, 60));
    }

    [Fact]
    public void MsUntilNextCappedFrame_CountsDownToZero()
    {
        Assert.Equal(1000.0 / 60 - 10, Preview3DFrameGate.MsUntilNextCappedFrame(10, 60), 3);
        Assert.Equal(0, Preview3DFrameGate.MsUntilNextCappedFrame(100, 60));
        Assert.Equal(0, Preview3DFrameGate.MsUntilNextCappedFrame(0, 0)); // uncapped
    }

    [Fact]
    public void Cap_NeverStarvesTheIdleTrickle()
    {
        // The clamp floor (15 fps → 66.7 ms) is below every legal trickle
        // interval (min 1 Hz would be 1000 ms; max 10 Hz = 100 ms), so once
        // the trickle wants a frame the cap has always elapsed too — for
        // every legal combination.
        for (var idleFps = GraphicsSettingsPolicy.MinIdleFps; idleFps <= GraphicsSettingsPolicy.MaxIdleFps; idleFps++)
        {
            var trickleMs = 1000.0 / idleFps;
            Assert.Equal(Preview3DFrameGate.FrameDecision.Render,
                Preview3DFrameGate.Decide(false, false, false, false, false, trickleMs,
                    GraphicsSettingsPolicy.MinCappedFps, idleFps));
        }
    }

    [Fact]
    public void SlowerIdleTrickle_DelaysTheSafetyNet()
    {
        // 2 Hz trickle = 500 ms interval: 100 ms (the old constant) no longer
        // trickles, 500 ms does.
        Assert.Equal(Preview3DFrameGate.FrameDecision.Park,
            Preview3DFrameGate.Decide(false, false, false, false, false, 100, 0, 2));
        Assert.Equal(Preview3DFrameGate.FrameDecision.Render,
            Preview3DFrameGate.Decide(false, false, false, false, false, 500, 0, 2));
    }

    // ---- #446: the trickle relaxes to 1 Hz once the loop has settled ----

    [Fact]
    public void IdleTrickle_KeepsTheConfiguredRate_InsideTheSettleWindow()
    {
        Assert.Equal(Preview3DFrameGate.FrameDecision.Render,
            Preview3DFrameGate.Decide(false, false, false, false, false, 100, 0, 10,
                msSinceLastWake: Preview3DFrameGate.TrickleSettleMs - 1));
    }

    [Fact]
    public void IdleTrickle_RelaxesToOneHz_OnceParkedPastTheSettleWindow()
    {
        var settled = Preview3DFrameGate.TrickleSettleMs;
        Assert.Equal(Preview3DFrameGate.FrameDecision.Park,
            Preview3DFrameGate.Decide(false, false, false, false, false, 100, 0, 10, msSinceLastWake: settled));
        Assert.Equal(Preview3DFrameGate.FrameDecision.Park,
            Preview3DFrameGate.Decide(false, false, false, false, false, 999, 0, 10, msSinceLastWake: settled));
        Assert.Equal(Preview3DFrameGate.FrameDecision.Render,
            Preview3DFrameGate.Decide(false, false, false, false, false, 1000, 0, 10, msSinceLastWake: settled));
    }

    [Fact]
    public void ASettledLoop_StillWakesImmediately()
    {
        var longAgo = Preview3DFrameGate.TrickleSettleMs * 10.0;
        Assert.Equal(Preview3DFrameGate.FrameDecision.Render,
            Preview3DFrameGate.Decide(false, true, false, false, false, 0, 0, 10, msSinceLastWake: longAgo));
        Assert.Equal(Preview3DFrameGate.FrameDecision.Render,
            Preview3DFrameGate.Decide(false, false, true, false, false, 0, 0, 10, msSinceLastWake: longAgo));
    }

    [Fact]
    public void TheTrickle_NeverRunsFasterThanConfigured()
    {
        for (var idleFps = GraphicsSettingsPolicy.MinIdleFps; idleFps <= GraphicsSettingsPolicy.MaxIdleFps; idleFps++)
        {
            Assert.Equal(1000.0 / idleFps, Preview3DFrameGate.TrickleIntervalMs(idleFps, 0));
            Assert.True(Preview3DFrameGate.TrickleIntervalMs(idleFps, 1e9) >= 1000.0 / idleFps);
        }
    }

    /// <summary>
    ///     #446, end to end over the loop's own bookkeeping: a paused editor
    ///     parked for a minute after one wake. Every trickle frame is a full
    ///     present, and the composed path snapshots each present into a NEW
    ///     pane-sized texture — at a steady 10 Hz that was ~600 texture creations
    ///     a minute for a picture that does not change. The clocks advance as
    ///     <c>Preview3DRenderDriver.RenderLoop</c> advances them: one
    ///     <see cref="Preview3DFrameGate.IdlePollMs" /> poll per park, both
    ///     restarted on a render, the settle clock only on a WOKEN one.
    /// </summary>
    [Fact]
    public void AParkedMinute_PresentsAboutOncePerSecond_NotTenTimes()
    {
        var renders = SimulateParkedLoop(seconds: 60, idleFps: GraphicsSettingsPolicy.DefaultIdleFps);

        // The wake's own frame, ~2 s at 10 Hz, then 1 Hz for the other ~58 s.
        Assert.InRange(renders, 58, 82);
    }

    private static int SimulateParkedLoop(int seconds, int idleFps)
    {
        double now = 0, lastRender = double.NegativeInfinity, lastWake = 0;
        var requested = true; // the one wake that starts the minute
        var renders = 0;
        while (now < seconds * 1000.0)
        {
            var decision = Preview3DFrameGate.Decide(false, requested, false, false, false,
                now - lastRender, 0, idleFps, now - lastWake);
            if (decision == Preview3DFrameGate.FrameDecision.Render)
            {
                if (Preview3DFrameGate.IsWake(false, requested, false, false, false)) lastWake = now;
                lastRender = now;
                requested = false;
                renders++;
            }

            now += Preview3DFrameGate.IdlePollMs;
        }

        return renders;
    }

    [Fact]
    public void ShouldRender_MatchesTheOriginalUncappedGate()
    {
        // The legacy entry point stays the uncapped 10 Hz gate.
        Assert.False(Idle(Preview3DFrameGate.IdleTrickleMs - 1));
        Assert.True(Idle(Preview3DFrameGate.IdleTrickleMs));
    }
}
