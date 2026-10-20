using DotCharter.Services.Editing.HandShapes;
using static DotCharter.Tests.Services.Editing.HandShapes.ChordHandShapePlannerTests;

namespace DotCharter.Tests.Services.Editing.HandShapes;

/// <summary>
///     The render loop's access to the suggestions (#491): the detector reads the whole level, so it
///     runs once per EDIT, never per frame — the <c>RepeatChordLatch</c> contract.
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
        Assert.Single(first);
        Assert.Single(latch.Suggestions);
        Assert.Equal(latch.Suggestions[0].Position, first[0].Position);

        latch.Get(arr, arr.Levels[0], true, 2, 10_000); // the history moved: an in-place edit
        Assert.Equal(2, latch.ComputeCount);

        arr.Levels[0].HandShapes.Add(new HandShape { Position = Pos(0), EndPosition = Pos(4) });
        Assert.Empty(latch.Get(arr, arr.Levels[0], true, 2, 10_000)); // a collection count moved
        Assert.Equal(3, latch.ComputeCount);
    }

    [Fact]
    public void HiddenSuggestions_RunNoDetector_AndServeNothing()
    {
        var arr = Chart();
        var latch = new HandShapeSuggestionLatch();

        Assert.Empty(latch.Get(arr, arr.Levels[0], false, 1, 0));
        Assert.Empty(latch.Get(null, null, true, 1, 0));
        Assert.Empty(latch.Suggestions);
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
}
