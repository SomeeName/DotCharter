using DotCharter.UI.Input.Tools.EventInteractionGestures;

namespace DotCharter.Tests.UI.Input.Tools.EventInteractionGestures;

public class BeatDragBoundsTests
{
    private static List<BeatMarker> Beats(params (double Time, bool Anchor)[] specs)
    {
        var result = new List<BeatMarker>();
        foreach (var (t, a) in specs)
            result.Add(new BeatMarker { Time = t, IsAnchor = a, Bpm = 120 });
        return result;
    }

    [Fact]
    public void ComputeBounds_FirstBeat_HasNoLowerBoundButUpperFromNextAnchor()
    {
        var beats = Beats((0, false), (500, false), (1000, true), (1500, false));
        var (lower, upper) = BeatDragBounds.ComputeBounds(beats, dragIndex: 0);

        Assert.Equal(double.NegativeInfinity, lower);
        // Next anchor is at index 2 (time 1000), 2 beats in front → 1000 - 10*2 = 980
        Assert.Equal(980.0, upper, 6);
    }

    [Fact]
    public void ComputeBounds_LastBeat_HasNoUpperBoundButLowerFromPrevAnchor()
    {
        var beats = Beats((0, true), (500, false), (1000, false), (1500, false));
        var (lower, upper) = BeatDragBounds.ComputeBounds(beats, dragIndex: 3);

        // Previous anchor is at index 0 (time 0), 3 beats behind → 0 + 10*3 = 30
        Assert.Equal(30.0, lower, 6);
        Assert.Equal(double.PositiveInfinity, upper);
    }

    [Fact]
    public void ComputeBounds_BetweenAnchors_UsesNearestAnchorsAsBounds()
    {
        var beats = Beats((0, true), (500, false), (1000, false), (1500, true), (2000, false));
        var (lower, upper) = BeatDragBounds.ComputeBounds(beats, dragIndex: 2);

        // Lower: anchor at 0, beats-before = 2 → 0 + 20 = 20
        // Upper: anchor at 1500, beats-after = 1 → 1500 - 10 = 1490
        Assert.Equal(20.0, lower, 6);
        Assert.Equal(1490.0, upper, 6);
    }

    [Fact]
    public void ComputeBounds_CustomEpsilon_ScalesGap()
    {
        var beats = Beats((0, true), (1000, false), (2000, true));
        var (lower, upper) = BeatDragBounds.ComputeBounds(beats, dragIndex: 1, epsilonMs: 50.0);

        Assert.Equal(50.0, lower, 6);
        Assert.Equal(1950.0, upper, 6);
    }

    [Fact]
    public void ComputeBounds_NoSurroundingAnchors_ClampsToTimelineEnds()
    {
        var beats = Beats((0, false), (500, false), (1000, false), (1500, false), (2000, false));
        var (lower, upper) = BeatDragBounds.ComputeBounds(beats, dragIndex: 2);

        // Falls back to beat 0 (lower) and last beat (upper)
        Assert.Equal(0 + 10.0 * 2, lower, 6);
        Assert.Equal(2000 - 10.0 * 2, upper, 6);
    }

    [Fact]
    public void ComputeBounds_OutOfRange_Throws()
    {
        var beats = Beats((0, false), (500, false));
        Assert.Throws<ArgumentOutOfRangeException>(() => BeatDragBounds.ComputeBounds(beats, -1));
        Assert.Throws<ArgumentOutOfRangeException>(() => BeatDragBounds.ComputeBounds(beats, 2));
    }

    [Fact]
    public void ComputeBounds_NegativeEpsilon_Throws()
    {
        var beats = Beats((0, false), (500, false));
        Assert.Throws<ArgumentOutOfRangeException>(() => BeatDragBounds.ComputeBounds(beats, 0, -1));
    }

    [Theory]
    [InlineData(50.0, 100.0, 200.0, 100.0)] // below lower
    [InlineData(150.0, 100.0, 200.0, 150.0)] // inside
    [InlineData(250.0, 100.0, 200.0, 200.0)] // above upper
    [InlineData(double.NegativeInfinity, 100.0, 200.0, 100.0)]
    public void Clamp_BehavesAsExpected(double value, double lower, double upper, double expected) => Assert.Equal(expected, BeatDragBounds.Clamp(value, lower, upper));
}
