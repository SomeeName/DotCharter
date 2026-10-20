using DotCharter.Rendering.Components;

namespace DotCharter.Tests.Rendering;

/// <summary>
///     The early/late depth nudge on a Live Play hit flash: the note travels
///     toward the player, so a rushed hit flashes ahead of the strike plane
///     (+z) and a dragged one behind it (−z).
///     The reason this is a saturating map and not the exact
///     <c>GetZDepthForTime</c> is the camera: the late hit window is 250 ms,
///     which at the real time→Z scale is −5 world units, and the camera sits at
///     −2.5 — the flash would land behind the viewer.
/// </summary>
public sealed class LivePlayFeedbackNudgeTests
{
    private const byte None = 0;
    private const byte Early = 1;
    private const byte OnTime = 2;
    private const byte Late = 3;

    [Fact]
    public void EarlyHit_FlashesBeyondTheStrikePlane()
    {
        // Struck 60 ms before the note arrived.
        var z = LivePlayFeedbackRenderComponent.TimingNudgeZ(Early, noteTimeMs: 1000, judgedAtMs: 940);
        Assert.True(z > 0f, $"an early hit should sit in front of the plane, got {z}");
    }

    [Fact]
    public void LateHit_FlashesBehindTheStrikePlane()
    {
        var z = LivePlayFeedbackRenderComponent.TimingNudgeZ(Late, noteTimeMs: 1000, judgedAtMs: 1060);
        Assert.True(z < 0f, $"a late hit should sit behind the plane, got {z}");
    }

    [Fact]
    public void OnTimeHit_SitsOnTheStrikePlane()
    {
        var z = LivePlayFeedbackRenderComponent.TimingNudgeZ(OnTime, noteTimeMs: 1000, judgedAtMs: 1000);
        Assert.Equal(0f, z, 3);
    }

    /// <summary>The nudge conveys magnitude — being 100 ms out reads as further
    /// off than being 30 ms out.</summary>
    [Fact]
    public void TheNudge_GrowsWithTheError()
    {
        var slight = LivePlayFeedbackRenderComponent.TimingNudgeZ(Late, 1000, 1030);
        var gross = LivePlayFeedbackRenderComponent.TimingNudgeZ(Late, 1000, 1100);
        Assert.True(Math.Abs(gross) > Math.Abs(slight));
    }

    /// <summary>A 250 ms late hit is legal (the late window IS 250 ms). Its raw
    /// depth would be −5 units, past the camera — the saturation is what stops
    /// the flash disappearing behind the viewer.</summary>
    [Theory]
    [InlineData(Late, 1000, 1250)]   // the extreme legal late hit
    [InlineData(Early, 1000, 850)]   // the extreme legal early hit
    [InlineData(Late, 1000, 5000)]   // absurd, defensive
    [InlineData(Early, 1000, -5000)]
    public void TheNudge_IsBoundedToTheBoard(byte direction, double noteTimeMs, double judgedAtMs)
    {
        var z = LivePlayFeedbackRenderComponent.TimingNudgeZ(direction, noteTimeMs, judgedAtMs);
        Assert.InRange(z,
            -LivePlayFeedbackRenderComponent.MaxTimingNudgeZ,
            LivePlayFeedbackRenderComponent.MaxTimingNudgeZ);
    }

    /// <summary>The load-bearing case. A miss and a CQT pull-verified hit are
    /// judged at the playhead when the window CLOSES, so their JudgedAtMs is a
    /// window expiry, not a detection. Nudging off that would shove every miss
    /// marker ~250 ms "late" — off the strike plane and toward the camera — for
    /// a player who simply never played the note.</summary>
    [Theory]
    [InlineData(1000, 1250)] // miss: judged at the late edge
    [InlineData(1000, 2000)] // CQT pull-verify long after the chart time
    public void UntimedJudgment_IsNeverNudged(double noteTimeMs, double judgedAtMs)
        => Assert.Equal(0f, LivePlayFeedbackRenderComponent.TimingNudgeZ(None, noteTimeMs, judgedAtMs), 5);
}
