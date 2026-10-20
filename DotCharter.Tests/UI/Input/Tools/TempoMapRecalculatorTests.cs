using DotCharter.UI.Input.Tools;

namespace DotCharter.Tests.UI.Input.Tools;

public class TempoMapRecalculatorTests
{
    /// <summary>120 BPM → 500 ms per beat.</summary>
    private const double Ms120 = 500.0;
    private const double Ms60 = 1000.0;

    private static List<BeatMarker> MakeBeats(int count, double bpm, double startMs = 0.0)
    {
        var msPerBeat = 60000.0 / bpm;
        var list = new List<BeatMarker>(count);
        for (var i = 0; i < count; i++)
            list.Add(new BeatMarker { Time = startMs + i * msPerBeat, Bpm = bpm, IsAnchor = false });
        return list;
    }

    [Fact]
    public void Recalculate_DraggingBeatZero_ShiftsEntireTimelineByDelta()
    {
        var beats = MakeBeats(5, 120.0);
        var snap = TempoMapRecalculator.Snapshot(beats);

        TempoMapRecalculator.Recalculate(beats, snap, dragIndex: 0, newTime: 1000.0);

        Assert.Equal(1000.0, beats[0].Time, 6);
        Assert.Equal(1500.0, beats[1].Time, 6);
        Assert.Equal(3000.0, beats[4].Time, 6);
        Assert.All(beats, b => Assert.Equal(120.0, b.Bpm, 6));
        // Beat-0 drag must NOT promote anyone to anchor.
        Assert.All(beats, b => Assert.False(b.IsAnchor));
    }

    [Fact]
    public void Recalculate_DraggingMiddleBeat_PromotesItToAnchor()
    {
        var beats = MakeBeats(5, 120.0);
        var snap = TempoMapRecalculator.Snapshot(beats);

        TempoMapRecalculator.Recalculate(beats, snap, dragIndex: 2, newTime: 800.0);

        Assert.True(beats[2].IsAnchor);
        Assert.Equal(800.0, beats[2].Time, 6);
    }

    [Fact]
    public void Recalculate_DraggingMiddleBeat_RecomputesBpmInLeadingSegment()
    {
        // 5 beats at 120 BPM = 0, 500, 1000, 1500, 2000.
        // Drag beat 2 from 1000 → 800. Beat 0 is implicit anchor.
        // Leading segment spans 2 beats over 800 ms ⇒ 150 BPM.
        var beats = MakeBeats(5, 120.0);
        var snap = TempoMapRecalculator.Snapshot(beats);

        TempoMapRecalculator.Recalculate(beats, snap, dragIndex: 2, newTime: 800.0);

        Assert.Equal(150.0, beats[0].Bpm, 6);
        Assert.Equal(150.0, beats[1].Bpm, 6);
        Assert.Equal(400.0, beats[1].Time, 6); // halfway between 0 and 800
    }

    [Fact]
    public void Recalculate_RespectsBeatDenominatorWhenPricingDraggedSegment()
    {
        var beats = MakeBeats(4, 120.0);
        beats[0].NoteDenominator = 8;
        beats[1].NoteDenominator = 8;
        beats[2].NoteDenominator = 8;
        beats[3].NoteDenominator = 8;
        beats[1].Time = 250.0;
        beats[2].Time = 500.0;
        beats[3].Time = 750.0;
        var snap = TempoMapRecalculator.Snapshot(beats);

        TempoMapRecalculator.Recalculate(beats, snap, dragIndex: 2, newTime: 400.0);

        Assert.Equal(150.0, beats[0].Bpm, 6);
        Assert.Equal(200.0, beats[1].Time, 6);
        Assert.Equal(650.0, beats[3].Time, 6);
    }

    [Fact]
    public void Recalculate_PreservesTrailingBpmAndExtrudesForward()
    {
        // After dragging beat 2, beats 3 and 4 should keep their original
        // 120 BPM tempo and slide forward to maintain spacing.
        var beats = MakeBeats(5, 120.0);
        var snap = TempoMapRecalculator.Snapshot(beats);

        TempoMapRecalculator.Recalculate(beats, snap, dragIndex: 2, newTime: 800.0);

        Assert.Equal(120.0, beats[3].Bpm, 6);
        Assert.Equal(120.0, beats[4].Bpm, 6);
        Assert.Equal(800.0 + Ms120, beats[3].Time, 6);
        Assert.Equal(800.0 + 2 * Ms120, beats[4].Time, 6);
    }

    [Fact]
    public void Recalculate_PreservesExistingAnchors()
    {
        var beats = MakeBeats(6, 120.0);
        beats[4].IsAnchor = true;
        var snap = TempoMapRecalculator.Snapshot(beats);

        // Drag beat 2 → 800 ms. Beat 4 is an anchor and must not move.
        var beat4OriginalTime = snap[4].Time;
        TempoMapRecalculator.Recalculate(beats, snap, dragIndex: 2, newTime: 800.0);

        Assert.Equal(beat4OriginalTime, beats[4].Time, 6);
        Assert.True(beats[4].IsAnchor);
    }

    [Fact]
    public void Recalculate_BetweenTwoAnchors_DistributesEvenly()
    {
        var beats = MakeBeats(6, 120.0);
        beats[4].IsAnchor = true;
        var snap = TempoMapRecalculator.Snapshot(beats);

        // Drag beat 2 → 800. Now beats 2..4 form a segment of 2 intervals
        // covering (2000 - 800) = 1200 ms ⇒ 100 BPM, beat 3 lands at 1400.
        TempoMapRecalculator.Recalculate(beats, snap, dragIndex: 2, newTime: 800.0);

        Assert.Equal(100.0, beats[2].Bpm, 6);
        Assert.Equal(100.0, beats[3].Bpm, 6);
        Assert.Equal(1400.0, beats[3].Time, 6);
    }

    [Fact]
    public void Recalculate_ClampsTinySegmentToMinDuration()
    {
        // Drag beat 1 essentially on top of beat 0 — segment math would
        // otherwise divide by ~zero and produce a runaway BPM. The clamp
        // keeps the result finite at the 10 ms floor.
        var beats = MakeBeats(3, 120.0);
        var snap = TempoMapRecalculator.Snapshot(beats);

        TempoMapRecalculator.Recalculate(beats, snap, dragIndex: 1, newTime: 0.1);

        var expectedMaxBpm = 1.0 * 60000.0 / TempoMapRecalculator.MinSegmentDurationMs;
        Assert.True(beats[0].Bpm <= expectedMaxBpm + 0.001, $"BPM was {beats[0].Bpm}");
    }

    [Fact]
    public void Recalculate_RestoresBaselineBeforeRepricing_IsIdempotent()
    {
        var beats = MakeBeats(5, 120.0);
        var snap = TempoMapRecalculator.Snapshot(beats);

        TempoMapRecalculator.Recalculate(beats, snap, dragIndex: 2, newTime: 700.0);
        var afterFirst = TempoMapRecalculator.Snapshot(beats);

        // Calling again with the same arguments must produce the same map —
        // proves the function reads from `snap` only, never from the
        // already-mutated `beats`.
        TempoMapRecalculator.Recalculate(beats, snap, dragIndex: 2, newTime: 700.0);

        for (var i = 0; i < beats.Count; i++)
        {
            Assert.Equal(afterFirst[i].Time, beats[i].Time, 6);
            Assert.Equal(afterFirst[i].Bpm, beats[i].Bpm, 6);
            Assert.Equal(afterFirst[i].IsAnchor, beats[i].IsAnchor);
        }
    }

    [Fact]
    public void Recalculate_WithZeroBpmInOriginal_FallsBackTo120ForExtrudedBeats()
    {
        var beats = MakeBeats(4, 120.0);
        // Force beat 1's pre-drag BPM to 0 — extrusion must use the fallback.
        beats[1].Bpm = 0.0;
        var snap = TempoMapRecalculator.Snapshot(beats);

        TempoMapRecalculator.Recalculate(beats, snap, dragIndex: 0, newTime: 100.0);

        // Beat-0 drag preserves Bpm verbatim, so this checks the drag-zero
        // path doesn't inadvertently invoke fallback. (Fallback is for the
        // post-last-anchor extrude; covered separately below.)
        Assert.Equal(0.0, beats[1].Bpm, 6);
    }

    [Fact]
    public void Recalculate_ZeroBpmAfterLastAnchor_UsesFallbackTempo()
    {
        // Original beat tempos: 60, 60, 0, 0, 0. Drag beat 1 → +250 ms
        // promotes it to last-anchor. The extrude loop reads
        // originalBeats[i-1].Bpm to price the gap to beat i — so:
        //   beat 2 spacing uses original[1].Bpm = 60 ⇒ +1000 ms.
        //   beat 3 spacing uses original[2].Bpm = 0  ⇒ fallback ⇒ +500 ms.
        //   beat 4 spacing uses original[3].Bpm = 0  ⇒ fallback ⇒ +500 ms.
        var beats = MakeBeats(5, bpm: 60.0);
        for (var i = 2; i < beats.Count; i++) beats[i].Bpm = 0.0;
        var snap = TempoMapRecalculator.Snapshot(beats);

        TempoMapRecalculator.Recalculate(beats, snap, dragIndex: 1, newTime: snap[1].Time + 250.0);

        var msPerFallbackBeat = 60000.0 / TempoMapRecalculator.FallbackBpm;
        Assert.Equal(beats[1].Time + Ms60, beats[2].Time, 6);
        Assert.Equal(beats[2].Time + msPerFallbackBeat, beats[3].Time, 6);
        Assert.Equal(beats[3].Time + msPerFallbackBeat, beats[4].Time, 6);
    }

    [Fact]
    public void Recalculate_EmptyInput_IsNoOp()
    {
        var beats = new List<BeatMarker>();
        TempoMapRecalculator.Recalculate(beats, Array.Empty<BeatMarker>(), 0, 0.0);
        Assert.Empty(beats);
    }

    [Fact]
    public void Recalculate_LengthMismatch_Throws()
    {
        var beats = MakeBeats(3, 120.0);
        Assert.Throws<ArgumentException>(() =>
            TempoMapRecalculator.Recalculate(beats, MakeBeats(4, 120.0), 0, 0.0));
    }

    [Theory]
    [InlineData(-1)]
    [InlineData(5)]
    public void Recalculate_DragIndexOutOfRange_Throws(int badIndex)
    {
        var beats = MakeBeats(3, 120.0);
        var snap = TempoMapRecalculator.Snapshot(beats);
        Assert.Throws<ArgumentOutOfRangeException>(() =>
            TempoMapRecalculator.Recalculate(beats, snap, badIndex, 100.0));
    }

    [Fact]
    public void Snapshot_DeepCopiesEveryField()
    {
        var src = MakeBeats(2, bpm: 90.0);
        src[0].IsAnchor = true;
        src[0].IsMeasure = true;
        src[1].BeatsInMeasure = 3;
        src[1].NoteDenominator = 8;

        var copy = TempoMapRecalculator.Snapshot(src);

        Assert.Equal(2, copy.Count);
        Assert.NotSame(src[0], copy[0]);
        Assert.True(copy[0].IsAnchor);
        Assert.True(copy[0].IsMeasure);
        Assert.Equal(3, copy[1].BeatsInMeasure);
        Assert.Equal(8, copy[1].NoteDenominator);
        Assert.Equal(90.0, copy[1].Bpm, 6);

        // Mutating original must not alter the copy.
        src[0].Time = 9999.0;
        Assert.Equal(0.0, copy[0].Time, 6);
    }

    [Fact]
    public void Recalculate_NonAnchoredMiddleBeats_StayBetweenSegmentEndpoints()
    {
        // Sanity: any non-anchor beat must fall between its surrounding
        // anchors after recompute (no inversions).
        var beats = MakeBeats(7, 120.0);
        beats[3].IsAnchor = true;
        var snap = TempoMapRecalculator.Snapshot(beats);

        TempoMapRecalculator.Recalculate(beats, snap, dragIndex: 5, newTime: snap[5].Time + 100.0);

        for (var i = 1; i < beats.Count; i++)
        {
            Assert.True(beats[i].Time >= beats[i - 1].Time,
                $"Inversion at idx {i}: {beats[i - 1].Time} → {beats[i].Time}");
        }
    }
}
