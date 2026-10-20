namespace DotCharter.Tests.Core.Utilities;

/// <summary>
///     <see cref="TimelineMath.FirstAfter{T}" /> / <see cref="TimelineMath.LastBefore{T}" />
///     replaced eight copies of
///     <c>list.Where(x =&gt; time(x) &gt; t).OrderBy(x =&gt; x.Position).FirstOrDefault()</c>
///     in the editor's seek and select-next/prev verbs — an O(n log n) sort of an
///     already-sorted list, plus an n-element buffer, per arrow-key press on the
///     UI thread (2.19 ms and 184 B on an 18,000-note chart; 0.002 ms and 0 B
///     after).
///     <para>
///         A faster answer is only allowed to be the SAME answer, so these compare
///         against the LINQ shapes verbatim — including the tie-break nobody would
///         think to look for: <c>OrderByDescending</c> is STABLE, so among sounds
///         sharing the greatest passing position it returned the FIRST in list
///         order (the low string of a chord), which is why
///         <see cref="TimelineMath.LastBefore{T}" /> walks back over the
///         equal-time run instead of stopping at the last passing index.
///     </para>
/// </summary>
public sealed class TimelineScanEquivalenceTests
{
    private static List<BeatMarker> Grid(int beats, double bpm = 120)
    {
        var beatMs = 60000.0 / bpm;
        var grid = new List<BeatMarker>(beats);
        for (var i = 0; i < beats; i++)
            grid.Add(new BeatMarker { Time = i * beatMs, Bpm = bpm, BeatsInMeasure = 4, IsMeasure = i % 4 == 0 });
        return grid;
    }

    /// <summary>
    ///     Notes ordered by position with deliberate equal-position runs — a chord's
    ///     worth of strings landing together is the case the tie-break is about.
    /// </summary>
    private static List<GuitarNote> Notes(int count, Random rng, IList<BeatMarker> beats)
    {
        var notes = new List<GuitarNote>(count);
        var beat = 0;
        while (notes.Count < count && beat < beats.Count - 1)
        {
            var together = Math.Min(count - notes.Count, 1 + rng.Next(4));
            var fraction = new Fraction(rng.Next(4), 4);
            for (var s = 0; s < together; s++)
            {
                notes.Add(new GuitarNote
                {
                    Position = new FractionalPosition(beat, fraction),
                    StringIndex = s,
                    Fret = notes.Count % 22
                });
            }

            beat += 1 + rng.Next(2);
        }

        return notes;
    }

    /// <summary>The shape <c>SelectNextNote</c> used, verbatim.</summary>
    private static GuitarNote? ReferenceFirstAfter(List<GuitarNote> notes, double cutoff, IList<BeatMarker> beats)
        => notes.Where(n => n.Position.GetPositionMs(beats) > cutoff)
            .OrderBy(n => n.Position).FirstOrDefault();

    /// <summary>The shape <c>SelectPrevNote</c> used, verbatim.</summary>
    private static GuitarNote? ReferenceLastBefore(List<GuitarNote> notes, double cutoff, IList<BeatMarker> beats)
        => notes.Where(n => n.Position.GetPositionMs(beats) < cutoff)
            .OrderByDescending(n => n.Position).FirstOrDefault();

    [Theory]
    [InlineData(3)]
    [InlineData(5)]
    [InlineData(11)]
    [InlineData(19)]
    public void FirstAfterAndLastBefore_matchTheLinqShapesTheyReplaced(int seed)
    {
        var rng = new Random(seed);
        var beats = Grid(400);
        var notes = Notes(600, rng, beats);
        Assert.NotEmpty(notes);

        // Sweep every element's own time plus the gaps around it, and both ends
        // of the grid — the cursor lands on a sound far more often than not
        // (the seek verbs run from the playhead, which they just parked on one).
        var cutoffs = new List<double> { double.MinValue / 2, -1, 0, double.MaxValue / 2 };
        foreach (var n in notes)
        {
            var t = n.Position.GetPositionMs(beats);
            cutoffs.Add(t - 0.5);
            cutoffs.Add(t);
            cutoffs.Add(t + 0.5);
        }

        foreach (var cutoff in cutoffs)
        {
            Assert.True(ReferenceEquals(ReferenceFirstAfter(notes, cutoff, beats),
                    TimelineMath.FirstAfter(notes, cutoff, beats)),
                $"FirstAfter disagreed with the LINQ shape at cutoff {cutoff}.");

            Assert.True(ReferenceEquals(ReferenceLastBefore(notes, cutoff, beats),
                    TimelineMath.LastBefore(notes, cutoff, beats)),
                $"LastBefore disagreed with the LINQ shape at cutoff {cutoff}.");
        }
    }

    [Fact]
    public void AnEmptyList_yieldsNothingFromEitherDirection()
    {
        var beats = Grid(8);
        var empty = new List<GuitarNote>();
        Assert.Null(TimelineMath.FirstAfter(empty, 0, beats));
        Assert.Null(TimelineMath.LastBefore(empty, 1_000, beats));
    }

    /// <summary>
    ///     The tie-break, stated on its own so a "simplification" of the walk-back
    ///     fails here and not only inside the sweep above.
    /// </summary>
    [Fact]
    public void LastBefore_returnsTheFirstOfAnEqualPositionRun_asTheStableSortDid()
    {
        var beats = Grid(16);
        var at = new FractionalPosition(4);
        var chord = new List<GuitarNote>
        {
            new() { Position = new FractionalPosition(1), StringIndex = 0, Fret = 1 },
            new() { Position = at, StringIndex = 5, Fret = 2 },
            new() { Position = at, StringIndex = 4, Fret = 3 },
            new() { Position = at, StringIndex = 3, Fret = 4 }
        };

        var cutoff = at.GetPositionMs(beats) + 1;
        var picked = TimelineMath.LastBefore(chord, cutoff, beats);
        Assert.Same(chord[1], picked);
        Assert.True(ReferenceEquals(ReferenceLastBefore(chord, cutoff, beats), picked));
    }

    /// <summary>
    ///     …and its mirror: <c>OrderBy</c> is stable too, so the FIRST of the
    ///     earliest passing run is what came back going forwards.
    /// </summary>
    [Fact]
    public void FirstAfter_returnsTheFirstOfAnEqualPositionRun_asTheStableSortDid()
    {
        var beats = Grid(16);
        var at = new FractionalPosition(4);
        var chord = new List<GuitarNote>
        {
            new() { Position = at, StringIndex = 5, Fret = 2 },
            new() { Position = at, StringIndex = 4, Fret = 3 },
            new() { Position = at, StringIndex = 3, Fret = 4 },
            new() { Position = new FractionalPosition(9), StringIndex = 0, Fret = 1 }
        };

        var cutoff = at.GetPositionMs(beats) - 1;
        var picked = TimelineMath.FirstAfter(chord, cutoff, beats);
        Assert.Same(chord[0], picked);
        Assert.True(ReferenceEquals(ReferenceFirstAfter(chord, cutoff, beats), picked));
    }

    /// <summary>
    ///     The shape <c>ChartEditorViewModel.SelectNearestToTime</c> used,
    ///     verbatim: a full pass keeping the first strictly-closer element. Its
    ///     two tie-breaks are the whole reason a "nearest" search cannot just be
    ///     a binary search — the EARLIER of two equidistant sounds wins (strict
    ///     <c>&lt;</c>), and among sounds sharing a time the first in LIST order
    ///     does, which for a chord's worth of notes is its low string.
    /// </summary>
    private static GuitarNote? ReferenceNearest(List<GuitarNote> notes, double timeMs, IList<BeatMarker> beats,
        out double distance)
    {
        GuitarNote? best = null;
        distance = double.MaxValue;
        foreach (var n in notes)
        {
            var d = Math.Abs(n.Position.GetPositionMs(beats) - timeMs);
            if (d < distance)
            {
                distance = d;
                best = n;
            }
        }

        return best;
    }

    [Theory]
    [InlineData(3)]
    [InlineData(5)]
    [InlineData(11)]
    [InlineData(19)]
    public void NearestTo_matchesTheLinearScanItReplaced(int seed)
    {
        var rng = new Random(seed);
        var beats = Grid(400);
        var notes = Notes(600, rng, beats);
        Assert.NotEmpty(notes);

        // The same sweep as the seek verbs, plus the MIDPOINTS between adjacent
        // sounds — the tie is only reachable from there, and it is the case the
        // two shapes are most likely to disagree on.
        //
        // The ends are ±1e9 ms (±11 days) rather than the ±double.MaxValue/2 the
        // seek sweep above uses, and that is a measured limit rather than
        // timidity: at 1e308 every |t(x) − target| rounds to the SAME double, so
        // "nearest" stops being a property of the data and the two shapes
        // tie-break an arbitrary answer differently (the scan keeps element 0,
        // the search keeps the last one). The seek verbs are unaffected — they
        // compare TIMES, not differences of them.
        var targets = new List<double> { -1e9, -1, 0, 1e9 };
        for (var i = 0; i < notes.Count; i++)
        {
            var t = notes[i].Position.GetPositionMs(beats);
            targets.Add(t - 0.5);
            targets.Add(t);
            targets.Add(t + 0.5);
            if (i + 1 < notes.Count)
                targets.Add((t + notes[i + 1].Position.GetPositionMs(beats)) / 2);
        }

        foreach (var target in targets)
        {
            var expected = ReferenceNearest(notes, target, beats, out var expectedDistance);
            var actual = TimelineMath.NearestTo(notes, target, beats, out var actualDistance);

            Assert.True(ReferenceEquals(expected, actual),
                $"NearestTo disagreed with the linear scan at {target}.");
            Assert.Equal(expectedDistance, actualDistance);
        }
    }

    [Fact]
    public void NearestTo_onAnEmptyList_yieldsNothing()
    {
        var beats = Grid(8);
        Assert.Null(TimelineMath.NearestTo(new List<GuitarNote>(), 0, beats, out var distance));
        Assert.Equal(double.MaxValue, distance);
    }

    /// <summary>
    ///     Both tie-breaks stated on their own, so a "simplification" of the
    ///     walk-back or of the strict comparison fails here and not only inside
    ///     the sweep.
    /// </summary>
    [Fact]
    public void NearestTo_prefersTheEarlierSound_andTheFirstOfAnEqualTimeRun()
    {
        var beats = Grid(16);
        var early = new FractionalPosition(4);
        var late = new FractionalPosition(6);
        var notes = new List<GuitarNote>
        {
            new() { Position = early, StringIndex = 5, Fret = 2 },
            new() { Position = early, StringIndex = 4, Fret = 3 },
            new() { Position = late, StringIndex = 3, Fret = 4 }
        };

        // Exactly between the two positions: equidistant, so the EARLIER one —
        // and of its equal-time run, the first in list order.
        var midpoint = (early.GetPositionMs(beats) + late.GetPositionMs(beats)) / 2;
        Assert.Same(notes[0], TimelineMath.NearestTo(notes, midpoint, beats, out _));

        // Landing exactly ON the run picks its first member too, not its last.
        Assert.Same(notes[0], TimelineMath.NearestTo(notes, early.GetPositionMs(beats), beats, out var onIt));
        Assert.Equal(0, onIt);
    }
}
