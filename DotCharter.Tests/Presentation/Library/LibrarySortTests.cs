using DotCharter.Presentation.Shared.Library;

namespace DotCharter.Tests.Presentation.Library;

/// <summary>
///     The one ordering both song lists use (#651): every column in both directions, the
///     unknown-LAST rule (never mirrored), the tie order (artist then title, in the direction
///     the user picked) and each screen's mode and caption table.
/// </summary>
public sealed class LibrarySortTests
{
    private static List<string> Titles(IEnumerable<LibraryTestSong> songs, LibrarySortKey key, bool descending)
        => [.. LibrarySort.Order(songs, key, descending).Select(s => s.Title)];

    /// <summary>Five rows distinct on every key, plus one unknown on that key, per column.</summary>
    public static TheoryData<LibrarySortColumn, string[]> AscendingOrders() => new()
    {
        { LibrarySortColumn.Artist, ["a", "b", "c"] },
        { LibrarySortColumn.Title, ["a", "b", "c"] },
        { LibrarySortColumn.Year, ["a", "b", "c", "unknown"] },
        { LibrarySortColumn.Length, ["a", "b", "c", "unknown"] },
        { LibrarySortColumn.Pack, ["a", "b", "c"] },
        { LibrarySortColumn.Difficulty, ["a", "b", "c", "unknown"] },
        { LibrarySortColumn.Tuning, ["a", "b", "c", "unknown"] },
        { LibrarySortColumn.RecentlyPlayed, ["a", "b", "c", "unknown"] },
        { LibrarySortColumn.BestScore, ["a", "b", "c", "unknown"] }
    };

    /// <summary>Each column, lowest first ▲ and highest first ▼, with the unknown row LAST in both.</summary>
    [Theory]
    [MemberData(nameof(AscendingOrders))]
    public void EveryColumn_SortsBothWays_AndKeepsTheUnknownLast(LibrarySortColumn column, string[] ascending)
    {
        var songs = RowsFor(column);
        var key = new LibrarySortKey(column);

        Assert.Equal(ascending, Titles(songs, key, descending: false));

        var known = ascending.Where(t => t != "unknown").Reverse();
        var expectedDescending = ascending.Contains("unknown") ? [.. known, "unknown"] : known.ToArray();
        Assert.Equal(expectedDescending, Titles(songs, key, descending: true));
    }

    /// <summary>A highest-first mode (Song Select's recent, best and difficulty) opens ▲ on the
    /// highest value; the unknown stays last either way.</summary>
    [Fact]
    public void AHighestFirstMode_OpensOnTheHighestValue_AndStillKeepsTheUnknownLast()
    {
        var songs = RowsFor(LibrarySortColumn.Difficulty);
        var hardestFirst = new LibrarySortKey(LibrarySortColumn.Difficulty, HighestFirst: true);

        Assert.Equal(["c", "b", "a", "unknown"], Titles(songs, hardestFirst, descending: false));
        Assert.Equal(["a", "b", "c", "unknown"], Titles(songs, hardestFirst, descending: true));
    }

    /// <summary>Equal values fall through to ARTIST then TITLE, in the user's direction — the
    /// same tie order on both screens for every value column.</summary>
    [Theory]
    [InlineData(LibrarySortColumn.Year)]
    [InlineData(LibrarySortColumn.Length)]
    [InlineData(LibrarySortColumn.Difficulty)]
    [InlineData(LibrarySortColumn.Tuning)]
    [InlineData(LibrarySortColumn.Pack)]
    [InlineData(LibrarySortColumn.RecentlyPlayed)]
    [InlineData(LibrarySortColumn.BestScore)]
    public void EqualValues_TieByArtistThenTitle_InTheUsersDirection(LibrarySortColumn column)
    {
        LibraryTestSong[] songs =
        [
            new() { Artist = "Beta", Title = "y" },
            new() { Artist = "Alpha", Title = "z" },
            new() { Artist = "Beta", Title = "x" },
            new() { Artist = "Alpha", Title = "w" }
        ];

        Assert.Equal(["w", "z", "x", "y"], Titles(songs, new(column), descending: false));
        Assert.Equal(["y", "x", "z", "w"], Titles(songs, new(column), descending: true));
        Assert.Equal(["w", "z", "x", "y"], Titles(songs, new(column, HighestFirst: true), descending: false));
    }

    /// <summary>The title column ties by artist; the artist column by title.</summary>
    [Fact]
    public void TheNameColumns_TieByTheOtherName()
    {
        LibraryTestSong[] songs =
        [
            new() { Artist = "Beta", Title = "One" },
            new() { Artist = "Alpha", Title = "One" },
            new() { Artist = "Alpha", Title = "Two" }
        ];

        Assert.Equal(["Alpha", "Beta", "Alpha"],
            LibrarySort.Order(songs, new(LibrarySortColumn.Title), false).Select(s => s.Artist));
        Assert.Equal(["One", "Two", "One"],
            LibrarySort.Order(songs, new(LibrarySortColumn.Artist), false).Select(s => s.Title));
    }

    /// <summary>Rows equal on every key keep their SOURCE order in both directions — a stable
    /// comparer, never a reversed list.</summary>
    [Fact]
    public void RowsEqualOnEveryKey_KeepTheirSourceOrder_InBothDirections()
    {
        var first = new LibraryTestSong { PackName = "first" };
        var second = new LibraryTestSong { PackName = "first" };
        LibraryTestSong[] songs = [first, second];

        foreach (var descending in new[] { false, true })
        {
            var ordered = LibrarySort.Order(songs, new(LibrarySortColumn.Year), descending).ToList();
            Assert.Same(first, ordered[0]);
            Assert.Same(second, ordered[1]);
        }
    }

    /// <summary>A row with no record sorts as unplayed under recent and best.</summary>
    [Fact]
    public void ARowWithoutARecord_SortsAsUnplayed()
    {
        ILibrarySong[] songs = [new LibraryCatalogueSong("a", "catalogue"), LibraryTestSong.Played("played", 5)];
        var key = new LibrarySortKey(LibrarySortColumn.BestScore, HighestFirst: true);

        Assert.Equal(["played", "catalogue"], LibrarySort.Order(songs, key, false).Select(s => s.Title));
        Assert.Equal(["played", "catalogue"], LibrarySort.Order(songs, key, true).Select(s => s.Title));
    }

    [Fact]
    public void EachScreen_MapsItsCaptionsAndModes()
    {
        string[] libraryCaptions = ["song", "title", "year", "time", "pack", "difficulty", "tuning"];
        Assert.Equal([0, 1, 2, 3, 4, 5, 6], libraryCaptions.Select(LibrarySort.Library.ModeOf));
        Assert.Equal(new LibrarySortKey(LibrarySortColumn.Length), LibrarySort.Library.KeyOf(3));
        Assert.Equal(new LibrarySortKey(LibrarySortColumn.Difficulty), LibrarySort.Library.KeyOf(5));

        string[] songSelectCaptions = ["song", "record", "tuning", "time"];
        Assert.Equal([0, 3, 5, 6], songSelectCaptions.Select(LibrarySort.SongSelect.ModeOf));
        Assert.Equal(new LibrarySortKey(LibrarySortColumn.RecentlyPlayed, true), LibrarySort.SongSelect.KeyOf(2));
        Assert.Equal(new LibrarySortKey(LibrarySortColumn.Difficulty, true), LibrarySort.SongSelect.KeyOf(4));
        Assert.Equal(new LibrarySortKey(LibrarySortColumn.Length), LibrarySort.SongSelect.KeyOf(6));

        Assert.Equal(-1, LibrarySort.Library.ModeOf("paths"));
        Assert.Equal(-1, LibrarySort.SongSelect.ModeOf("year"));
        Assert.Equal(-1, LibrarySort.Library.ModeOf(null));
        Assert.Equal(new LibrarySortKey(LibrarySortColumn.Artist), LibrarySort.Library.KeyOf(99));
    }

    /// <summary>A click sorts by its caption ascending; a second click on the same caption flips it.</summary>
    [Fact]
    public void AClick_SortsAscending_AndASecondClickFlips()
    {
        Assert.Equal((2, false), LibrarySort.Library.Click("year", 0, false));
        Assert.Equal((2, true), LibrarySort.Library.Click("year", 2, false));
        Assert.Equal((2, false), LibrarySort.Library.Click("year", 2, true));
        Assert.Equal((5, false), LibrarySort.Library.Click("difficulty", 2, true));
        Assert.Null(LibrarySort.Library.Click("paths", 0, false));
    }

    [Fact]
    public void TheGlyph_MarksOnlyTheActiveColumn()
    {
        Assert.Equal("▲", LibrarySort.Glyph(2, false, 2));
        Assert.Equal("▼", LibrarySort.Glyph(2, true, 2));
        Assert.Equal("", LibrarySort.Glyph(2, false, 3));
    }

    /// <summary>Rows a, b, c ascending on the column, plus an "unknown" one where the column has
    /// an unknown value. Everything else is equal, so only the column decides.</summary>
    private static LibraryTestSong[] RowsFor(LibrarySortColumn column) => column switch
    {
        LibrarySortColumn.Artist => [new() { Title = "b", Artist = "B" }, new() { Title = "c", Artist = "C" }, new() { Title = "a", Artist = "A" }],
        LibrarySortColumn.Title => [new() { Title = "b" }, new() { Title = "c" }, new() { Title = "a" }],
        LibrarySortColumn.Year => [new() { Title = "b", Year = 1995 }, new() { Title = "unknown", Year = 0 }, new() { Title = "c", Year = 2010 }, new() { Title = "a", Year = 1980 }],
        LibrarySortColumn.Length => [new() { Title = "b", LengthSeconds = 200 }, new() { Title = "unknown", LengthSeconds = 0 }, new() { Title = "c", LengthSeconds = 300 }, new() { Title = "a", LengthSeconds = 100 }],
        LibrarySortColumn.Pack => [new() { Title = "b", PackName = "b.psarc" }, new() { Title = "c", PackName = "c.psarc" }, new() { Title = "a", PackName = "a.psarc" }],
        LibrarySortColumn.Difficulty => [new() { Title = "b", Difficulty = 5 }, new() { Title = "unknown", Difficulty = 0 }, new() { Title = "c", Difficulty = 9 }, new() { Title = "a", Difficulty = 2 }],
        LibrarySortColumn.Tuning => [new() { Title = "b", TuningCellLabel = "E Standard" }, new() { Title = "unknown", TuningCellLabel = "" }, new() { Title = "c", TuningCellLabel = "Open G" }, new() { Title = "a", TuningCellLabel = "Drop D" }],
        LibrarySortColumn.RecentlyPlayed => [LibraryTestSong.Played("b", 1, daysAgo: 5), new() { Title = "unknown" }, LibraryTestSong.Played("c", 1, daysAgo: 1), LibraryTestSong.Played("a", 1, daysAgo: 9)],
        LibrarySortColumn.BestScore => [LibraryTestSong.Played("b", 500), new() { Title = "unknown" }, LibraryTestSong.Played("c", 900), LibraryTestSong.Played("a", 100)],
        _ => throw new ArgumentOutOfRangeException(nameof(column))
    };
}
