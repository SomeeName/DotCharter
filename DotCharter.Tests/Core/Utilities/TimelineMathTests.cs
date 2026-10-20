namespace DotCharter.Tests.Core.Utilities;

public class TimelineMathTests
{
    private static List<BeatMarker> BuildBeats(double bpm, int count)
    {
        var beats = new List<BeatMarker>();
        var msPerBeat = 60000.0 / bpm;
        for (var i = 0; i < count; i++) beats.Add(new BeatMarker { Time = i * msPerBeat, Bpm = bpm });
        return beats;
    }

    [Fact]
    public void CalculateTimeMs_EmptyBeats_ReturnsZero() => Assert.Equal(0, TimelineMath.CalculateTimeMs(new FractionalPosition(3), []));

    [Fact]
    public void CalculateTimeMs_SingleBeat_UsesDefaultSpacing()
    {
        var beats = new List<BeatMarker> { new() { Time = 100 } };
        Assert.Equal(100 + 500 * 3.5,
            TimelineMath.CalculateTimeMs(new FractionalPosition(3, new Fraction(1, 2)), beats),
            3);
    }

    [Fact]
    public void CalculateTimeMs_WithinKnownBeats_InterpolatesLinearly()
    {
        var beats = BuildBeats(120, 4); // 500ms per beat
        var result = TimelineMath.CalculateTimeMs(new FractionalPosition(2, new Fraction(1, 4)), beats);
        Assert.Equal(1000 + 125, result, 3);
    }

    [Fact]
    public void CalculateTimeMs_BeyondLastBeat_ExtrapolatesFromLastInterval()
    {
        var beats = BuildBeats(120, 3); // beats 0,500,1000
        var result = TimelineMath.CalculateTimeMs(new FractionalPosition(5), beats);
        // Beat 5 = 5 * 500 = 2500
        Assert.Equal(2500, result, 3);
    }

    [Fact]
    public void PositionFromMs_EmptyBeats_ReturnsZero() => Assert.Equal(FractionalPosition.Zero, TimelineMath.PositionFromMs(1234, []));

    [Fact]
    public void PositionFromMs_RoundTripsApproximately()
    {
        var beats = BuildBeats(120, 8);
        var original = new FractionalPosition(3, new Fraction(1, 2));
        var ms = TimelineMath.CalculateTimeMs(original, beats);
        var recovered = TimelineMath.PositionFromMs(ms, beats);

        Assert.Equal(original.DoubleValue, recovered.DoubleValue, 2);
    }

    [Fact]
    public void BinarySearchBeats_EmptyList_ReturnsNegativeOne() => Assert.Equal(-1, TimelineMath.BinarySearchBeats([], 100));

    [Fact]
    public void BinarySearchBeats_FindsLastBeatAtOrBeforeTime()
    {
        var beats = BuildBeats(120, 5); // 0, 500, 1000, 1500, 2000
        Assert.Equal(0, TimelineMath.BinarySearchBeats(beats, -10));
        Assert.Equal(0, TimelineMath.BinarySearchBeats(beats, 499));
        Assert.Equal(1, TimelineMath.BinarySearchBeats(beats, 500));
        Assert.Equal(2, TimelineMath.BinarySearchBeats(beats, 1200));
        Assert.Equal(4, TimelineMath.BinarySearchBeats(beats, 5000));
    }

    [Fact]
    public void GetVisibleRange_EmptyList_ReturnsNegativeEnd()
    {
        var beats = BuildBeats(120, 4);
        TimelineMath.GetVisibleRange(new List<GuitarNote>(), 0, 1000, beats, 0, out var start, out var end);
        Assert.Equal(0, start);
        Assert.Equal(-1, end);
    }

    [Fact]
    public void GetVisibleRange_ReturnsIndicesCoveringRange()
    {
        var beats = BuildBeats(120, 10);
        var notes = new List<GuitarNote>
        {
            new() { Position = new FractionalPosition(0) }, // 0ms
            new() { Position = new FractionalPosition(2) }, // 1000ms
            new() { Position = new FractionalPosition(4) }, // 2000ms
            new() { Position = new FractionalPosition(6) }, // 3000ms
            new() { Position = new FractionalPosition(8) } // 4000ms
        };

        TimelineMath.GetVisibleRange(notes, 1500, 3200, beats, 0,
            out var start, out var end);

        Assert.InRange(start, 0, 2);
        Assert.InRange(end, 2, 4);
        // The visible notes (2000, 3000) must be within [start, end].
        Assert.True(start <= 2 && end >= 3);
    }

    // ─── Tempo changes: interpolation must use PER-BEAT spacing ───────────────

    /// <summary>A non-uniform grid: beat 0→1 spans 500 ms (120 BPM), beat 1→2
    /// spans 200 ms (300 BPM). A fraction inside each interval must scale by THAT
    /// interval's own length, not a global tempo.</summary>
    private static List<BeatMarker> TempoChangeBeats() =>
        [new() { Time = 0 }, new() { Time = 500 }, new() { Time = 700 }, new() { Time = 900 }];

    [Fact]
    public void CalculateTimeMs_InterpolatesWithinEachTempoRegionIndependently()
    {
        var beats = TempoChangeBeats();
        // Slow region: half a beat = 250 ms.
        Assert.Equal(250, TimelineMath.CalculateTimeMs(new FractionalPosition(0, new Fraction(1, 2)), beats), 3);
        // Fast region: half a beat = 100 ms (500 + 100).
        Assert.Equal(600, TimelineMath.CalculateTimeMs(new FractionalPosition(1, new Fraction(1, 2)), beats), 3);
        // A whole beat lands exactly on the grid marker.
        Assert.Equal(700, TimelineMath.CalculateTimeMs(new FractionalPosition(2), beats), 3);
    }

    [Fact]
    public void CalculateTimeMs_BeyondGrid_ExtrapolatesWithTheLastInterval_NotTheFirst()
    {
        // Last interval is 200 ms (fast). Beat 5 = beats[2](700) + 3*200 = 1300 —
        // extrapolating with the SLOW 500 ms first interval would give 2500.
        var beats = TempoChangeBeats();
        Assert.Equal(1300, TimelineMath.CalculateTimeMs(new FractionalPosition(5), beats), 3);
    }

    [Fact]
    public void CalculateTimeMs_BeforeGrid_ExtrapolatesBackwardWithTheFirstInterval()
    {
        // A negative beat sits before the chart start; it extrapolates off the
        // first interval (500 ms), so beat -1 = -500 ms.
        var beats = TempoChangeBeats();
        Assert.Equal(-500, TimelineMath.CalculateTimeMs(new FractionalPosition(-1), beats), 3);
    }

    [Fact]
    public void PositionFromMs_IsMonotonicNonDecreasing_AcrossATempoChange()
    {
        var beats = TempoChangeBeats();
        var previous = double.NegativeInfinity;
        for (var ms = -100.0; ms <= 1000.0; ms += 13.0)
        {
            var value = TimelineMath.PositionFromMs(ms, beats).DoubleValue;
            Assert.True(value >= previous - 1e-9, $"position went backward at {ms} ms ({value} < {previous})");
            previous = value;
        }
    }

    [Fact]
    public void RoundTrip_MsToPositionToMs_HoldsAcrossATempoChange()
    {
        var beats = TempoChangeBeats();
        // One ms in each region; recovered time is within the 1/192-beat snap of
        // the local interval (≤ ~3 ms for a 500 ms beat).
        foreach (var ms in new[] { 120.0, 350.0, 560.0, 660.0, 820.0 })
        {
            var recovered = TimelineMath.CalculateTimeMs(TimelineMath.PositionFromMs(ms, beats), beats);
            // Recovered within one 1/192-beat snap cell of the local interval
            // (the slow 500 ms beat has the coarsest grid, 500/192 ≈ 2.6 ms).
            Assert.Equal(ms, recovered, tolerance: 3.0);
        }
    }

    // ─── Outside the grid: PositionFromMs must invert CalculateTimeMs ─────────

    [Fact]
    public void PositionFromMs_BeforeTheGrid_ReturnsANegativePosition()
    {
        // The fraction used to be clamped into [0, 0.999], so everything before
        // beat 0 landed ON beat 0 — a lead-in note and the downbeat collided.
        var beats = TempoChangeBeats();

        var preRoll = TimelineMath.PositionFromMs(-750, beats);
        Assert.Equal(-1.5, preRoll.DoubleValue, 3);

        var earlier = TimelineMath.PositionFromMs(-2000, beats);
        Assert.True(earlier < preRoll, "two distinct pre-roll times collapsed onto one position");
    }

    [Fact]
    public void PositionFromMs_FarPastTheGrid_DoesNotCollapseOntoTheLastBeat()
    {
        // Past the end the fraction was clamped to 0.999, which rolled over
        // exactly once — so every late sound stacked on beats.Count.
        var beats = TempoChangeBeats(); // 0, 500, 700, 900

        var a = TimelineMath.PositionFromMs(5300, beats);
        var b = TimelineMath.PositionFromMs(9300, beats);

        Assert.True(a > new FractionalPosition(beats.Count),
            $"{a} did not extrapolate past the end of the grid");
        Assert.True(b > a, "two distinct times past the grid collapsed onto one position");
    }

    [Fact]
    public void PositionFromMs_PastTheGrid_UsesTheLastInterval_NotTheFirst()
    {
        // CalculateTimeMs extrapolates with the LAST interval (200 ms); the old
        // fallback here used the FIRST (500 ms), so the two disagreed on every
        // chart with a tempo change.
        var beats = TempoChangeBeats();

        var pos = TimelineMath.PositionFromMs(1300, beats);

        Assert.Equal(5, pos.BeatId);
        Assert.Equal(Fraction.Zero, pos.Fraction);
    }

    [Fact]
    public void RoundTrip_PositionToMsToPosition_HoldsOutsideTheGrid()
    {
        var beats = TempoChangeBeats();

        foreach (var beatId in new[] { -6, -2, -1, 4, 9, 25 })
        {
            var original = new FractionalPosition(beatId);
            var recovered = TimelineMath.PositionFromMs(TimelineMath.CalculateTimeMs(original, beats), beats);
            Assert.Equal(original, recovered);
        }
    }

    [Fact]
    public void PositionFromMs_NonFiniteTime_ReturnsZeroInsteadOfGarbage()
    {
        var beats = TempoChangeBeats();

        Assert.Equal(FractionalPosition.Zero, TimelineMath.PositionFromMs(double.NaN, beats));
        Assert.Equal(FractionalPosition.Zero, TimelineMath.PositionFromMs(double.PositiveInfinity, beats));
    }

    [Fact]
    public void CalculateTimeMs_SingleBeat_UsesTheBeatsOwnBpm()
    {
        // A one-beat grid has no interval to interpolate over, so the marker's
        // own tempo is the only information there is. 120 was hardcoded.
        var beats = new List<BeatMarker> { new() { Time = 100, Bpm = 90 } };

        Assert.Equal(100 + 60000.0 / 90 * 2, TimelineMath.CalculateTimeMs(new FractionalPosition(2), beats), 3);
    }

    [Fact]
    public void PositionFromMs_JustBeforeABeat_SnapsForwardToTheNextBeat()
    {
        // The 1/192 snap rounds a fraction of 0.999 up to a whole beat and carries
        // (num == den ⇒ beatIdx++, fraction 0): 499.9 ms lands ON beat 1, not at
        // beat 0 + 191/192. Pins the carry branch so a refactor can't drop it.
        var beats = new List<BeatMarker> { new() { Time = 0 }, new() { Time = 500 }, new() { Time = 1000 } };
        var pos = TimelineMath.PositionFromMs(499.9, beats);
        Assert.Equal(1, pos.BeatId);
        Assert.Equal(Fraction.Zero, pos.Fraction);
    }

    // ---- #325: imported milliseconds round-trip --------------------------------

    [Theory]
    [InlineData(166.667, 167)]
    [InlineData(166.4, 166)]
    [InlineData(62.5, 62)]
    [InlineData(-62.5, -62)]
    [InlineData(63.5, 64)]
    [InlineData(0.0, 0)]
    public void ToWholeMs_RoundsToNearest_HalfToEven(double ms, int expected) =>
        Assert.Equal(expected, TimelineMath.ToWholeMs(ms));

    /// <summary>
    ///     Every whole ms from 1.5 s before the grid to 1.5 s past it, at tempos
    ///     whose beat is a whole ms and one (137 BPM, 437.956 ms) whose is not.
    ///     <see cref="TimelineMath.PositionFromMs" /> moved most of them by up to
    ///     1/384 beat.
    /// </summary>
    [Theory]
    [InlineData(40)]
    [InlineData(60)]
    [InlineData(120)]
    [InlineData(137)]
    public void PositionFromImportedMs_EveryWholeMs_WritesBackUnchanged(double bpm)
    {
        var beats = BuildBeats(bpm, 6);
        var last = (int)beats[^1].Time;
        var moved = new List<int>();
        for (var ms = -1500; ms <= last + 1500; ms++)
        {
            var pos = TimelineMath.PositionFromImportedMs(ms, beats);
            if (TimelineMath.ToWholeMs(TimelineMath.CalculateTimeMs(pos, beats)) != ms) moved.Add(ms);
        }

        Assert.True(moved.Count == 0, $"{moved.Count} ms values moved at {bpm} BPM, first {moved.FirstOrDefault()}");
    }

    /// <summary>
    ///     A grid point written as its rounded ms must come back as THE SAME grid
    ///     point, not a 1/1920 or 1/19200 neighbour — DotCharter's own exports and
    ///     every charter's 1/2…1/64 and triplet notes stay on their grid.
    /// </summary>
    [Theory]
    [InlineData(40)]
    [InlineData(120)]
    [InlineData(137)]
    public void PositionFromImportedMs_AWrittenGridPoint_ComesBackOnTheGrid(double bpm)
    {
        var beats = BuildBeats(bpm, 6);
        for (var tick = 0; tick < 192; tick++)
        {
            var gridPoint = new FractionalPosition(2, new Fraction(tick, 192));
            var writtenMs = TimelineMath.ToWholeMs(TimelineMath.CalculateTimeMs(gridPoint, beats));

            Assert.Equal(gridPoint, TimelineMath.PositionFromImportedMs(writtenMs, beats));
        }
    }

    [Fact]
    public void PositionFromImportedMs_NoGrid_IsZero() =>
        Assert.Equal(FractionalPosition.Zero, TimelineMath.PositionFromImportedMs(1003, []));
}
