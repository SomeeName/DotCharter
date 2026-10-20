using DotCharter.Services.Gameplay;

namespace DotCharter.Tests.Services.Gameplay;

/// <summary>
///     0.21 dynamic backing mix math: a miss (streak 0) ducks to the floor, a
///     streak swells back to unity, and the smoothing step is frame-rate
///     independent and click-free.
/// </summary>
public sealed class StemDuckMathTests
{
    [Fact]
    public void TargetGain_MissDucksToFloor()
    {
        Assert.Equal(StemDuckMath.DuckFloor, StemDuckMath.TargetGain(0), 12);
        // A negative streak is nonsense but must not underflow past the floor.
        Assert.Equal(StemDuckMath.DuckFloor, StemDuckMath.TargetGain(-3), 12);
    }

    [Fact]
    public void TargetGain_FullSwellAtOrAboveSwellStreak()
    {
        Assert.Equal(1.0, StemDuckMath.TargetGain(StemDuckMath.SwellStreak), 12);
        Assert.Equal(1.0, StemDuckMath.TargetGain(StemDuckMath.SwellStreak + 20), 12);
    }

    [Fact]
    public void TargetGain_RisesMonotonicallyBetweenFloorAndUnity()
    {
        var previous = double.NegativeInfinity;
        for (var streak = 0; streak <= StemDuckMath.SwellStreak; streak++)
        {
            var gain = StemDuckMath.TargetGain(streak);
            Assert.InRange(gain, StemDuckMath.DuckFloor, 1.0);
            Assert.True(gain >= previous, $"gain fell at streak {streak}");
            previous = gain;
        }
    }

    [Fact]
    public void Approach_MovesTowardTargetWithoutOvershoot()
    {
        var current = StemDuckMath.DuckFloor;
        for (var i = 0; i < 200; i++)
            current = StemDuckMath.Approach(current, 1.0, dtMs: 20);

        Assert.True(current is > 0.99 and <= 1.0, $"did not converge: {current}");
    }

    [Fact]
    public void Approach_EachStepIsTinyAtControlRate_NoZipper()
    {
        // From the floor toward unity, one 20 ms step must move only a small
        // fraction of the gap — that is what keeps the ramp click-free.
        var step = StemDuckMath.Approach(StemDuckMath.DuckFloor, 1.0, dtMs: 20) - StemDuckMath.DuckFloor;
        Assert.InRange(step, 0.0, 0.1 * (1.0 - StemDuckMath.DuckFloor));
    }

    [Fact]
    public void Approach_NonPositiveDtHolds_NonPositiveTauSnaps()
    {
        Assert.Equal(0.5, StemDuckMath.Approach(0.5, 1.0, dtMs: 0), 12);
        Assert.Equal(0.5, StemDuckMath.Approach(0.5, 1.0, dtMs: -5), 12);
        Assert.Equal(1.0, StemDuckMath.Approach(0.5, 1.0, dtMs: 20, timeConstantMs: 0), 12);
    }

    [Fact]
    public void Approach_IsFrameRateIndependent()
    {
        // One 40 ms step lands close to two 20 ms steps (exponential, not linear).
        var oneBig = StemDuckMath.Approach(StemDuckMath.DuckFloor, 1.0, dtMs: 40);
        var twoSmall = StemDuckMath.Approach(
            StemDuckMath.Approach(StemDuckMath.DuckFloor, 1.0, dtMs: 20), 1.0, dtMs: 20);
        Assert.Equal(oneBig, twoSmall, 3);
    }
}
