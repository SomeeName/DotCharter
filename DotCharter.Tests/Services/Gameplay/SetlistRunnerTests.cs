using DotCharter.Services.Gameplay.Setlists;

namespace DotCharter.Tests.Services.Gameplay;

/// <summary>The pure setlist state (#646): current index, next, skip, end and a
/// missing entry.</summary>
public sealed class SetlistRunnerTests
{
    private static PlaySetlist ThreeSongs() => new()
    {
        Name = "Set",
        Entries =
        [
            new PlaySetlistEntry { SongKey = "a", Title = "A" },
            new PlaySetlistEntry { SongKey = "b", Title = "B" },
            new PlaySetlistEntry { SongKey = "c", Title = "C" }
        ]
    };

    [Fact]
    public void BeforeTheFirstSong_NothingIsCurrentAndTheFirstIsNext()
    {
        var run = new SetlistRunner(ThreeSongs());

        Assert.True(run.IsRunning);
        Assert.Equal(-1, run.CurrentIndex);
        Assert.Null(run.Current);
        Assert.Equal("a", run.PeekNext()!.SongKey);
    }

    [Fact]
    public void MoveNext_WalksTheListInOrderThenEnds()
    {
        var run = new SetlistRunner(ThreeSongs());

        Assert.Equal("a", run.MoveNext()!.SongKey);
        Assert.Equal("b", run.PeekNext()!.SongKey);
        Assert.Equal("b", run.MoveNext()!.SongKey);
        Assert.Equal("c", run.MoveNext()!.SongKey);
        Assert.Equal(2, run.CurrentIndex);
        Assert.False(run.HasNext);
        Assert.True(run.IsRunning, "the last song is still being played");

        Assert.Null(run.MoveNext());
        Assert.False(run.IsRunning);
        Assert.Null(run.Current);
    }

    [Fact]
    public void AMissingEntry_IsNamedOnceAndTheRunGoesOn()
    {
        var run = new SetlistRunner(ThreeSongs());
        run.MoveNext();
        run.MarkMissing();
        var next = run.MoveNext();

        Assert.Equal("b", next!.SongKey);
        Assert.Equal(["A"], run.TakePendingSkips().Select(e => e.Title));
        Assert.Empty(run.TakePendingSkips());
        Assert.Equal(["A"], run.Skipped.Select(e => e.Title));
    }

    [Fact]
    public void Completion_CountsEachSongOnceAcrossRetries()
    {
        var run = new SetlistRunner(ThreeSongs());
        run.MoveNext();
        run.MarkCompleted();
        run.MarkCompleted();
        run.MoveNext();
        run.MarkCompleted();

        Assert.Equal(2, run.CompletedCount);
    }

    [Fact]
    public void End_StopsTheRunWhereItIs()
    {
        var run = new SetlistRunner(ThreeSongs());
        run.MoveNext();

        run.End();

        Assert.False(run.IsRunning);
        Assert.False(run.HasNext);
        Assert.Null(run.MoveNext());
    }

    [Fact]
    public void TheRunPlaysACopy_SoEditingTheListMidRunMovesNothing()
    {
        var list = ThreeSongs();
        var run = new SetlistRunner(list);
        run.MoveNext();

        list.Entries.Insert(0, new PlaySetlistEntry { SongKey = "z", Title = "Z" });
        list.Entries[2].SongKey = "changed";

        Assert.Equal("a", run.Current!.SongKey);
        Assert.Equal("b", run.PeekNext()!.SongKey);
        Assert.Equal(3, run.Count);
    }

    [Fact]
    public void AnEmptyList_EndsOnTheFirstMove()
    {
        var run = new SetlistRunner(new PlaySetlist());

        Assert.Null(run.MoveNext());
        Assert.False(run.IsRunning);
    }
}
