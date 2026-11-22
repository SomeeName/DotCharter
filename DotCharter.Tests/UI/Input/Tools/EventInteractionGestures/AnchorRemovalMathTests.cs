using DotCharter.UI.Input.Tools.EventInteractionGestures;

namespace DotCharter.Tests.UI.Input.Tools.EventInteractionGestures;

public class AnchorRemovalMathTests
{
    private static List<BeatMarker> MakeBeats(params (double Time, bool Anchor, double Bpm)[] specs)
    {
        var result = new List<BeatMarker>();
        foreach (var (t, a, bpm) in specs)
            result.Add(new BeatMarker { Time = t, IsAnchor = a, Bpm = bpm });
        return result;
    }

    [Fact]
    public void Compute_BeatZero_IsNoOp()
    {
        var beats = MakeBeats((0, true, 120), (500, false, 120));
        Assert.Null(AnchorRemovalMath.Compute(beats, 0));
    }

    [Fact]
    public void Compute_OutOfRange_IsNoOp()
    {
        var beats = MakeBeats((0, true, 120), (500, false, 120));
        Assert.Null(AnchorRemovalMath.Compute(beats, 2));
        Assert.Null(AnchorRemovalMath.Compute(beats, -1));
    }

    [Fact]
    public void Compute_RemovesAnchorFlagOnTarget()
    {
        var beats = MakeBeats((0, true, 120), (500, true, 120), (1000, false, 120));
        var result = AnchorRemovalMath.Compute(beats, 1);
        Assert.NotNull(result);
        Assert.False(result.Value.New[1].IsAnchor);
        Assert.True(result.Value.Old[1].IsAnchor); // old snapshot preserved
    }

    [Fact]
    public void Compute_RepricesSegmentBetweenSurroundingAnchors_ToUniformBpm()
    {
        // Anchors at 0 and 2000. Beat 2 (anchor) sits at 500 with weird timing.
        // Removing it should redistribute beats 1..3 evenly across 0..4 (5 beats).
        var beats = MakeBeats(
            (0, true, 60),
            (200, false, 60),
            (500, true, 60),
            (1700, false, 60),
            (2000, true, 60));

        var result = AnchorRemovalMath.Compute(beats, 2);
        Assert.NotNull(result);
        var (_, n) = result.Value;

        // Anchors preserved
        Assert.Equal(0.0, n[0].Time, 6);
        Assert.Equal(2000.0, n[4].Time, 6);
        // Span 2000ms / 4 beats = 500ms each at 120 BPM
        Assert.Equal(500.0, n[1].Time, 6);
        Assert.Equal(1000.0, n[2].Time, 6);
        Assert.Equal(1500.0, n[3].Time, 6);
        Assert.Equal(120.0, n[0].Bpm, 6);
        Assert.Equal(120.0, n[1].Bpm, 6);
        Assert.Equal(120.0, n[2].Bpm, 6);
        Assert.Equal(120.0, n[3].Bpm, 6);
    }

    [Fact]
    public void Compute_NoSurroundingAnchors_UsesTimelineEndpoints()
    {
        // Only the target itself is an anchor. After removal, prev=beat 0,
        // next=last beat. Span 4000 / 4 beats = 1000ms each.
        var beats = MakeBeats(
            (0, false, 60),
            (300, false, 60),
            (1000, true, 60),
            (3500, false, 60),
            (4000, false, 60));

        var (_, n) = AnchorRemovalMath.Compute(beats, 2)!.Value;

        Assert.Equal(0.0, n[0].Time, 6);
        Assert.Equal(1000.0, n[1].Time, 6);
        Assert.Equal(2000.0, n[2].Time, 6);
        Assert.Equal(3000.0, n[3].Time, 6);
        Assert.Equal(4000.0, n[4].Time, 6);
        Assert.False(n[2].IsAnchor);
    }

    [Fact]
    public void Compute_SixEightUsesQuarterNoteBpm()
    {
        var beats = Enumerable.Range(0, 7).Select(i => new BeatMarker
        {
            Time = i * 250,
            Bpm = 120,
            BeatsInMeasure = 6,
            NoteDenominator = 8,
            IsAnchor = i is 0 or 3 or 6
        }).ToList();
        beats[3].Time = 900;

        var (_, plan) = AnchorRemovalMath.Compute(beats, 3)!.Value;

        for (var i = 0; i < plan.Count; i++)
        {
            Assert.Equal(i * 250.0, plan[i].Time, 6);
            Assert.Equal(120.0, plan[i].Bpm, 6);
            Assert.Equal(8, plan[i].NoteDenominator);
        }

        Assert.False(plan[3].IsAnchor);
        Assert.Equal(900.0, beats[3].Time);
        Assert.True(beats[3].IsAnchor);
    }

    [Fact]
    public void Compute_MixedDenominatorsWeightEachInterval()
    {
        var beats = MakeBeats((0, true, 120), (400, false, 120), (700, true, 120),
            (1100, false, 120), (1500, true, 90));
        beats[1].NoteDenominator = 8;
        beats[2].NoteDenominator = 8;

        var (_, plan) = AnchorRemovalMath.Compute(beats, 2)!.Value;

        Assert.Equal(new[] { 0.0, 500.0, 750.0, 1000.0, 1500.0 }, plan.Select(b => b.Time).ToArray());
        Assert.All(plan.Take(4), b => Assert.Equal(120.0, b.Bpm, 6));
        Assert.Equal(90.0, plan[4].Bpm);
        Assert.Equal(8, plan[1].NoteDenominator);
        Assert.Equal(8, plan[2].NoteDenominator);
    }

    [Fact]
    public void Compute_DegenerateSpan_ClampsToMinSpan()
    {
        // Two surrounding anchors at the same time → span clamped, no NaN/Infinity.
        var beats = MakeBeats((100, true, 60), (100, true, 60), (100, true, 60));
        var (_, n) = AnchorRemovalMath.Compute(beats, 1, minSpanMs: 10.0)!.Value;

        // 2 beats / 0.01s = 12000 BPM → msPerBeat = 5
        Assert.False(double.IsNaN(n[0].Bpm));
        Assert.False(double.IsInfinity(n[0].Bpm));
        Assert.Equal(12000.0, n[0].Bpm, 3);
    }

    [Fact]
    public void Compute_ReturnsDeepCopies_OriginalListUnaffected()
    {
        var beats = MakeBeats((0, true, 120), (500, true, 120), (1000, true, 120));
        var (old, newPositions) = AnchorRemovalMath.Compute(beats, 1)!.Value;

        newPositions[1].Time = 99999;
        old[1].Time = 88888;

        Assert.Equal(500.0, beats[1].Time);
    }

    [Fact]
    public void Compute_NullBeats_Throws() => Assert.Throws<ArgumentNullException>(() => AnchorRemovalMath.Compute(null!, 1));

    [Fact]
    public void Compute_NonPositiveMinSpan_Throws()
    {
        var beats = MakeBeats((0, true, 60), (500, true, 60));
        Assert.Throws<ArgumentOutOfRangeException>(() => AnchorRemovalMath.Compute(beats, 1, 0));
        Assert.Throws<ArgumentOutOfRangeException>(() => AnchorRemovalMath.Compute(beats, 1, -10));
    }
}
