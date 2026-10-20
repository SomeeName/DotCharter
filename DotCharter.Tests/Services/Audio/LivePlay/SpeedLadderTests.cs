using DotCharter.Services.Audio.LivePlay;

namespace DotCharter.Tests.Services.Audio.LivePlay;

public sealed class SpeedLadderTests
{
    [Fact]
    public void StartsAtConfiguredSpeed()
    {
        var ladder = new SpeedLadder { StartSpeed = 0.6 };
        Assert.Equal(0.6, ladder.CurrentSpeed, 3);
        Assert.False(ladder.AtFullSpeed);
    }

    [Fact]
    public void CleanPass_StepsUp()
    {
        var ladder = new SpeedLadder { StartSpeed = 0.6, StepUp = 0.05 };
        var next = ladder.CompletePass(accuracy: 0.97, judgedSounds: 20);
        Assert.Equal(0.65, next, 3);
    }

    [Fact]
    public void MediocrePass_HoldsSpeed()
    {
        var ladder = new SpeedLadder { StartSpeed = 0.6 };
        var next = ladder.CompletePass(accuracy: 0.85, judgedSounds: 20);
        Assert.Equal(0.6, next, 3);
    }

    [Fact]
    public void FailedPass_StepsDown()
    {
        var ladder = new SpeedLadder { StartSpeed = 0.6, StepDown = 0.05 };
        var next = ladder.CompletePass(accuracy: 0.4, judgedSounds: 20);
        Assert.Equal(0.55, next, 3);
    }

    [Fact]
    public void EmptyPass_NeverMovesTheLadder()
    {
        var ladder = new SpeedLadder { StartSpeed = 0.6 };
        Assert.Equal(0.6, ladder.CompletePass(accuracy: 1.0, judgedSounds: 0), 3);
        Assert.Equal(0.6, ladder.CompletePass(accuracy: 0.0, judgedSounds: 0), 3);
    }

    [Fact]
    public void RampsFrom60To100_WithCleanPasses()
    {
        var ladder = new SpeedLadder { StartSpeed = 0.6, StepUp = 0.05 };
        for (var i = 0; i < 8; i++) ladder.CompletePass(1.0, 10);
        Assert.True(ladder.AtFullSpeed);
        Assert.Equal(1.0, ladder.CurrentSpeed, 3);

        // Further clean passes stay at 100% and count as exit signal.
        ladder.CompletePass(1.0, 10);
        Assert.Equal(1.0, ladder.CurrentSpeed, 3);
        Assert.Equal(1, ladder.CleanFullSpeedPasses);
    }

    [Fact]
    public void NeverDropsBelowMinSpeed()
    {
        var ladder = new SpeedLadder { StartSpeed = 0.3, StepDown = 0.1 };
        ladder.CompletePass(0.0, 10);
        ladder.CompletePass(0.0, 10);
        Assert.Equal(SpeedLadder.MinSpeed, ladder.CurrentSpeed, 3);
    }

    [Fact]
    public void Reset_RestoresStartSpeed()
    {
        var ladder = new SpeedLadder { StartSpeed = 0.6 };
        ladder.CompletePass(1.0, 10);
        ladder.Reset();
        Assert.Equal(0.6, ladder.CurrentSpeed, 3);
        Assert.Equal(0, ladder.CleanFullSpeedPasses);
    }
}
