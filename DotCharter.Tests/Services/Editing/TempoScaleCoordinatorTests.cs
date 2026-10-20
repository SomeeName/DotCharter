using DotCharter.Services.Editing;

namespace DotCharter.Tests.Services.Editing;

public class TempoScaleCoordinatorTests
{
    private static List<BeatMarker> MakeBeats()
    {
        // 4 measures, 4 beats each at 1000ms apart (= 4000ms per measure).
        var beats = new List<BeatMarker>();
        for (var m = 0; m < 4; m++)
        {
            for (var b = 0; b < 4; b++)
            {
                beats.Add(new BeatMarker
                {
                    Time = (m * 4 + b) * 1000.0,
                    IsMeasure = b == 0
                });
            }
        }

        return beats;
    }

    [Fact]
    public void SnapToMeasureBounds_ExpandsToEnclosingMeasures()
    {
        var beats = MakeBeats();

        var (start, end) = TempoScaleCoordinator.SnapToMeasureBounds(beats, startMs: 5500, endMs: 9500);

        // start snaps down to measure-1 boundary (4000), end snaps up to measure-3 boundary (12000).
        Assert.Equal(4000, start);
        Assert.Equal(12000, end);
    }

    [Fact]
    public void SnapToMeasureBounds_AlreadyAligned_NoChange()
    {
        var beats = MakeBeats();

        var (start, end) = TempoScaleCoordinator.SnapToMeasureBounds(beats, startMs: 4000, endMs: 8000);

        Assert.Equal(4000, start);
        Assert.Equal(8000, end);
    }

    [Fact]
    public void SnapToMeasureBounds_StartBeforeFirstMeasure_StaysAtInput()
    {
        var beats = MakeBeats();

        // No measure marker is <= -10, so snappedStart stays at the input -10.
        var (start, end) = TempoScaleCoordinator.SnapToMeasureBounds(beats, startMs: -10, endMs: 100);

        Assert.Equal(-10, start);
        Assert.Equal(4000, end);
    }

    [Fact]
    public void SnapToMeasureBounds_NoBeats_ReturnsInput()
    {
        var beats = new List<BeatMarker>();

        var (start, end) = TempoScaleCoordinator.SnapToMeasureBounds(beats, startMs: 100, endMs: 200);

        Assert.Equal(100, start);
        Assert.Equal(200, end);
    }

    [Fact]
    public void SnapToMeasureBounds_IgnoresNonMeasureBeats()
    {
        var beats = new List<BeatMarker>
        {
            new() { Time = 0, IsMeasure = true },
            new() { Time = 500, IsMeasure = false },
            new() { Time = 1000, IsMeasure = false },
            new() { Time = 2000, IsMeasure = true },
        };

        var (start, end) = TempoScaleCoordinator.SnapToMeasureBounds(beats, startMs: 750, endMs: 1500);

        Assert.Equal(0, start);
        Assert.Equal(2000, end);
    }
}
