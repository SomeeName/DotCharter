using DotCharter.Services.Editing.Snapping;

namespace DotCharter.Tests.Services.Editing.Snapping;

/// <summary>
///     Seek to the previous / next GRID LINE — the verb the editor shipped
///     without, so <c>←</c>/<c>→</c> could only move a fixed number of
///     milliseconds and never lined up with the grid notes snap to.
/// </summary>
public class GridNavigationTests
{
    // One beat = 500 ms at 120 BPM, divisor 4 ⇒ a grid step is a quarter beat.
    private static readonly List<BeatMarker> Beats =
        Enumerable.Range(0, 8).Select(i => new BeatMarker { Time = i * 500, Bpm = 120 }).ToList();

    private static FractionalPosition Step(double timeMs, int divisor, int direction)
    {
        var tempo = new DotCharter.Services.Editing.Tempo.TempoService(new FakeAppLogger());
        var nearest = tempo.SnapToGrid(timeMs, Beats, divisor);
        return GridNavigation.Step(timeMs, nearest, nearest.GetPositionMs(Beats), divisor, direction);
    }

    [Fact]
    public void FromExactlyOnALineTheNextStepIsAWholeGridStepAway()
    {
        // 1000 ms = beat 2. Divisor 4 ⇒ +1/4 beat = 1125 ms.
        var next = Step(1000, 4, 1);

        Assert.Equal(new FractionalPosition(2, new Fraction(1, 4)), next);
        Assert.Equal(1125, next.GetPositionMs(Beats), 3);
    }

    [Fact]
    public void FromExactlyOnALineThePreviousStepIsAWholeGridStepBack()
    {
        var previous = Step(1000, 4, -1);

        Assert.Equal(new FractionalPosition(1, new Fraction(3, 4)), previous);
        Assert.Equal(875, previous.GetPositionMs(Beats), 3);
    }

    [Fact]
    public void FromBetweenLinesItLandsOnTheNextOneNotTwoAway()
    {
        // 1050 ms sits between 1000 and 1125; forward must be 1125, not 1250.
        Assert.Equal(1125, Step(1050, 4, 1).GetPositionMs(Beats), 3);
        Assert.Equal(1000, Step(1050, 4, -1).GetPositionMs(Beats), 3);
    }

    [Fact]
    public void ItNeverSeeksBeforeTheStartOfTheChart() => Assert.Equal(FractionalPosition.Zero, Step(0, 4, -1));

    [Fact]
    public void TheStepFollowsTheGridDivisor()
    {
        // Divisor 1 ⇒ a whole beat; divisor 16 ⇒ a sixteenth of a beat.
        Assert.Equal(1500, Step(1000, 1, 1).GetPositionMs(Beats), 3);
        Assert.Equal(1031.25, Step(1000, 16, 1).GetPositionMs(Beats), 3);
    }

    [Fact]
    public void ADivisorOfZeroDegradesToAWholeBeatRatherThanDividingByIt()
    {
        Assert.Equal(new FractionalPosition(3), GridNavigation.Step(
            1000, new FractionalPosition(2), 1000, 0, 1));
    }
}
