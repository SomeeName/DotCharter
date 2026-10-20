using DotCharter.Services.Editing.Snapping;

namespace DotCharter.Tests.Services.Snapping;

public class SnapServiceTests
{
    private static BeatMarker Beat(double timeMs, bool isMeasure = false) => new() { Time = (float)timeMs, IsMeasure = isMeasure };

    private static IList<BeatMarker> FourOnTheFloor()
    {
        return
        [
            Beat(0, true),
            Beat(500),
            Beat(1000),
            Beat(1500),
            Beat(2000, true),
            Beat(2500),
            Beat(3000),
            Beat(3500)
        ];
    }

    [Fact]
    public void DivisorZero_IsIdentity()
    {
        var svc = new SnapService { Mode = SnapMode.Beat, Divisor = 0 };
        Assert.Equal(123.4, svc.SnapToGrid(123.4, FourOnTheFloor()));
    }

    [Fact]
    public void Milliseconds_SnapsToDivisorStep()
    {
        // Divisor=10 → step=0.1ms: round to nearest 0.1
        var svc = new SnapService { Mode = SnapMode.Milliseconds, Divisor = 10 };
        Assert.Equal(123.5, svc.SnapToGrid(123.47, FourOnTheFloor()), 3);
    }

    [Fact]
    public void Beat_DivisorOne_SnapsToBeat()
    {
        var svc = new SnapService { Mode = SnapMode.Beat, Divisor = 1 };
        Assert.Equal(500.0, svc.SnapToGrid(600.0, FourOnTheFloor()), 3); // closer to 500 than 1000
        Assert.Equal(1000.0, svc.SnapToGrid(800.0, FourOnTheFloor()), 3); // closer to 1000 than 500
    }

    [Fact]
    public void Beat_DivisorFour_SnapsToQuarterBeat()
    {
        var svc = new SnapService { Mode = SnapMode.Beat, Divisor = 4 };
        // Beat 0..500 → steps 0, 125, 250, 375, 500
        Assert.Equal(250.0, svc.SnapToGrid(240.0, FourOnTheFloor()), 3);
        Assert.Equal(375.0, svc.SnapToGrid(370.0, FourOnTheFloor()), 3);
    }

    [Fact]
    public void Beat_BeforeFirstBeat_UsesFirstBeatAsPrev()
    {
        var svc = new SnapService { Mode = SnapMode.Beat, Divisor = 1 };
        // Time before first beat: prev and next both degenerate to beat 0 → returns 0.
        Assert.Equal(0.0, svc.SnapToGrid(-100.0, FourOnTheFloor()));
    }

    [Fact]
    public void Measure_SnapsAtMeasureBoundaries()
    {
        var svc = new SnapService { Mode = SnapMode.Measure, Divisor = 1 };
        Assert.Equal(0.0, svc.SnapToGrid(900.0, FourOnTheFloor()), 3);
        Assert.Equal(2000.0, svc.SnapToGrid(1100.0, FourOnTheFloor()), 3);
    }

    [Fact]
    public void Measure_DivisorTwo_SnapsToMidMeasure()
    {
        var svc = new SnapService { Mode = SnapMode.Measure, Divisor = 2 };
        Assert.Equal(1000.0, svc.SnapToGrid(950.0, FourOnTheFloor()), 3);
    }

    [Fact]
    public void EmptyBeats_ReturnsInputUnchanged()
    {
        var svc = new SnapService { Mode = SnapMode.Beat, Divisor = 4 };
        Assert.Equal(777.0, svc.SnapToGrid(777.0, new List<BeatMarker>()));
    }
}
