using DotCharter.Services.Editing.HandShapes;
using static DotCharter.Tests.Services.Editing.HandShapes.ChordHandShapePlannerTests;

namespace DotCharter.Tests.Services.Editing.HandShapes;

/// <summary>
///     The surfaces' access to the suggestions (#491): the detector reads the whole level, so it runs
///     once per EDIT, never per frame, and once for ALL the surfaces that read the level — the
///     timeline and each stacked row, the song map and the hand-shape tool share the editor's one latch
///     — the <c>RepeatChordLatch</c> contract.
/// </summary>
public sealed class HandShapeSuggestionLatchTests
{
    private static Arrangement Chart()
    {
        var arr = Arr();
        (int, int)[] c = [(1, 3), (2, 2), (3, 0), (4, 1), (5, 0), (4, 1)];
        for (var i = 0; i < c.Length; i++)
            arr.Levels[0].Notes.Add(new GuitarNote { Position = Pos(i * 0.5), StringIndex = c[i].Item1, Fret = c[i].Item2 });
        return arr;
    }

    [Fact]
    public void AnUnchangedLevel_IsServedFromTheLatch_AndAnEditRecomputes()
    {
        var arr = Chart();
        var latch = new HandShapeSuggestionLatch();

        var first = latch.Get(arr, arr.Levels[0], true, 1, 0);
        var again = latch.Get(arr, arr.Levels[0], true, 1, 10_000);

        Assert.Same(first, again);
        Assert.Equal(1, latch.ComputeCount);
        Assert.Single(first.Bars);
        Assert.Single(first.Suggestions);
        Assert.Equal(first.Suggestions[0].Position, first.Bars[0].Position);

        latch.Get(arr, arr.Levels[0], true, 2, 10_000); // the history moved: an in-place edit
        Assert.Equal(2, latch.ComputeCount);

        arr.Levels[0].HandShapes.Add(new HandShape { Position = Pos(0), EndPosition = Pos(4) });
        Assert.Empty(latch.Get(arr, arr.Levels[0], true, 2, 10_000).Bars); // a collection count moved
        Assert.Equal(3, latch.ComputeCount);
    }

    [Fact]
    public void HiddenSuggestions_RunNoDetector_AndServeNothing()
    {
        var arr = Chart();
        var latch = new HandShapeSuggestionLatch();

        Assert.Empty(latch.Get(arr, arr.Levels[0], false, 1, 0).Bars);
        Assert.Empty(latch.Get(null, null, true, 1, 0).Bars);
        Assert.Empty(latch.Get(null, null, true, 1, 0).Suggestions);
        Assert.Equal(0, latch.ComputeCount);
    }

    [Fact]
    public void AnUnversionedCaller_RecomputesOnlyAfterTheStalenessBound()
    {
        var arr = Chart();
        var latch = new HandShapeSuggestionLatch();

        latch.Get(arr, arr.Levels[0], true, -1, 0);
        latch.Get(arr, arr.Levels[0], true, -1, HandShapeSuggestionLatch.StaleAfterMs - 1);
        Assert.Equal(1, latch.ComputeCount);

        latch.Get(arr, arr.Levels[0], true, -1, HandShapeSuggestionLatch.StaleAfterMs);
        Assert.Equal(2, latch.ComputeCount);
    }

    /// <summary>The main timeline, the song map and the hand-shape tool read one level on one edit:
    /// ONE run, and every one of them holds the very same bars (the tool's hit-test index is the
    /// timeline's draw index).</summary>
    [Fact]
    public void ManyReadersOfOneEdit_ShareOneRun_AndOneSetOfBars()
    {
        var arr = Chart();
        var level = arr.Levels[0];
        var latch = new HandShapeSuggestionLatch();

        for (long edit = 1; edit <= 4; edit++)
        {
            var sets = new[]
            {
                latch.Get(arr, level, true, edit, 0), // the timeline
                latch.Get(arr, level, true, edit, 0), // the song map
                latch.Get(arr, level, true, edit, 0), // the hand-shape tool
                latch.Get(arr, level, true, edit, 0)  // a stacked row of the same level
            };

            Assert.All(sets, set => Assert.Same(sets[0], set));
            Assert.Equal((int)edit, latch.ComputeCount);
        }
    }

    [Fact]
    public void EachLevelRunsOnce_PerEdit_HoweverManyRowsReadIt()
    {
        var arrangements = new[] { Chart(), Chart(), Chart() };
        var latch = new HandShapeSuggestionLatch();

        for (long edit = 1; edit <= 3; edit++)
        {
            for (var reader = 0; reader < 5; reader++)
            {
                foreach (var arr in arrangements) latch.Get(arr, arr.Levels[0], true, edit, 0);
            }

            Assert.Equal(3 * (int)edit, latch.ComputeCount);
        }
    }

    [Fact]
    public void ReadersOnSeveralThreads_WaitForOneRun()
    {
        var arr = Chart();
        var latch = new HandShapeSuggestionLatch();
        var sets = new HandShapeSuggestionSet[16];

        Parallel.For(0, sets.Length, new ParallelOptions { MaxDegreeOfParallelism = sets.Length },
            i => sets[i] = latch.Get(arr, arr.Levels[0], true, 1, 0));

        Assert.Equal(1, latch.ComputeCount);
        Assert.All(sets, set => Assert.Same(sets[0], set));
    }

    [Fact]
    public void AWarmRead_AllocatesNothing()
    {
        var arr = Chart();
        var latch = new HandShapeSuggestionLatch();
        latch.Get(arr, arr.Levels[0], true, 1, 0);

        AllocationProbe.AssertAllocationFree("a warm suggestion read",
            () => latch.Get(arr, arr.Levels[0], true, 1, 0));
    }

    /// <summary>The cost of one run on a 5,000-sound chart, in bytes (never milliseconds): the keyed-map
    /// merge with a probe template per candidate took ~4.5 MB. A COLD run (no grip judged yet, the first
    /// after a chart opens) stays under 1.5 MB and a WARM one (the memo of judged grips filled, every
    /// edit after) under 300 KB; a regression to per-sound collections lands several times over.</summary>
    [Fact]
    public void OneRunOnA5kSoundChart_StaysUnderItsAllocationBudget()
    {
        var arr = SuggesterCharts.Build(7, 5700);
        var level = arr.Levels[0];
        Assert.True(level.Notes.Count + level.Chords.Count > 5000, "the fixture is thinner than 5k sounds");

        var cold = AllocationProbe.MeasureCheapest(() =>
        {
            HandShapeGripMemo.Shared.Clear();
            return () => HandShapeSuggester.Suggest(arr, level);
        });
        var warm = AllocationProbe.MeasureCheapest(() => () => HandShapeSuggester.Suggest(arr, level));
        var legacy = AllocationProbe.MeasureCheapest(() => () => LegacyHandShapeSuggester.Suggest(arr, level));

        Assert.True(cold <= ColdBudgetBytes, $"a cold run allocated {cold:N0} B (budget {ColdBudgetBytes:N0})");
        Assert.True(warm <= WarmBudgetBytes, $"a warm run allocated {warm:N0} B (budget {WarmBudgetBytes:N0})");
        // The yardstick is real: the old path is far over the cold budget on the same chart.
        Assert.True(legacy > 2 * ColdBudgetBytes, $"the old path allocated only {legacy:N0} B; the budget no longer tests anything");
    }

    [Fact]
    public void TheGripMemo_StartsOverPastItsLimit()
    {
        var memo = new HandShapeGripMemo(maxEntries: 4);
        for (ulong shape = 1; shape <= 4; shape++) memo.Store(shape, 0, 6, 3, held: shape % 2 == 0);

        Assert.True(memo.TryGet(2, 0, 6, 3, out var held) && held);
        Assert.False(memo.TryGet(2, 2, 6, 3, out _)); // another capo is another question
        Assert.Equal(4, memo.Count);

        memo.Store(5, 0, 6, 3, held: true);

        Assert.Equal(1, memo.Count);
        Assert.False(memo.TryGet(1, 0, 6, 3, out _));
        Assert.True(memo.TryGet(5, 0, 6, 3, out _));
    }

    private const long ColdBudgetBytes = 1_500_000;
    private const long WarmBudgetBytes = 300_000;
}
