using Avalonia;
using DotCharter.Core.Graphics;
using DotCharter.Rendering.Data;
using DotCharter.UI.Input;

namespace DotCharter.Tests.Rendering;

/// <summary>The renderer and hit tester share the last anchor's finite span even while the view is panned.</summary>
public sealed class AnchorSpanTests
{
    [Theory]
    [InlineData(0)]
    [InlineData(15000)]
    public void TheSnapshotUsesTheSharedEnd_AndPanningPastItLeavesNoPinnedHead(double currentTimeMs)
    {
        var beats = Enumerable.Range(0, 16).Select(i => new BeatMarker { Time = i * 500, Bpm = 120 }).ToList();
        var anchor = new FretAnchor(new FractionalPosition(1), 7, 4);
        var snapshot = new FrameSnapshot();
        var context = new SnapshotUpdateContext { ActiveBeats = beats, ActiveAnchors = new[] { anchor } };
        FrameSnapshot.BuildSnapshot(0, 2400, currentTimeMs, snapshot, context, []);
        Assert.Equal(1, snapshot.AnchorCount);
        var endMs = AnchorSpan.EndTimeMs(500, null, currentTimeMs);
        Assert.Equal(endMs, snapshot.Anchors[0].EndTime);
        var pannedAnchorMs = endMs + 10000;
        Assert.Null(ChartHitTester.HitTestAnchorHead(new Point(4, 109), [anchor], pannedAnchorMs, 1, 2400,
            100, beats, _ => 16f, currentTimeMs));
        Assert.Null(ChartHitTester.HitTestAnchor(new Point(4, 109), [anchor], pannedAnchorMs, 1, 2400,
            100, beats, currentTimeMs));
    }

    [Fact]
    public void ConsecutiveAnchorsKeepTheirOwnEndsWhenPannedBeyondTheLastSpan()
    {
        var beats = Enumerable.Range(0, 16).Select(i => new BeatMarker { Time = i * 500, Bpm = 120 }).ToList();
        var anchors = new[] { new FretAnchor(new FractionalPosition(1), 3, 4), new FretAnchor(new FractionalPosition(3), 7, 4) };
        var snapshot = new FrameSnapshot();
        FrameSnapshot.BuildSnapshot(0, 2400, 0, snapshot,
            new SnapshotUpdateContext { ActiveBeats = beats, ActiveAnchors = anchors }, []);
        Assert.Equal(2, snapshot.AnchorCount);
        Assert.Equal(1500, snapshot.Anchors[0].EndTime);
        Assert.Equal(AnchorSpan.EndTimeMs(1500, null, 0), snapshot.Anchors[1].EndTime);
        var pan = snapshot.Anchors[1].EndTime + 10000;
        Assert.Null(ChartHitTester.HitTestAnchorHead(new Point(4, 109), anchors, pan, 1, 2400, 100, beats, _ => 16f));
    }
}
