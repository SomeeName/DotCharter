using DotCharter.Presentation.Shared.Library;
using DotCharter.Services.Gameplay.Scoring;
using DotCharter.Services.Library;

namespace DotCharter.Tests.Presentation.Library;

/// <summary>
///     The one filter engine both song lists run (#651), over plain rows: every facet, the
///     search (folding included), the lessons and record legs, and the per-option counts a
///     popover prints. The view-model tests (<c>SongBrowserFilterPassTests</c>,
///     <c>PlaySongFilterTests</c>, <c>SongLibraryRepublishTests</c>) cover the same rules
///     through each screen's own rows.
/// </summary>
public sealed class LibraryQueryTests
{
    private static readonly LibraryFilter None = new("");

    private static IReadOnlyCollection<string> Keys(params string[] keys) => keys;

    /// <summary>Every facet, one row in the selected bucket, one in another, one unknown —
    /// the selected keeps only its own; ticking "" adds the unknown one.</summary>
    public static TheoryData<string, LibraryTestSong, LibraryTestSong, LibraryTestSong, string> Facets() => new()
    {
        { LibraryFacets.Tuning, new() { TuningSummary = "D A D G B E" }, new() { TuningSummary = "E A D G B E" }, new() { TuningSummary = "" }, "D A D G B E" },
        { LibraryFacets.Capo, new() { Capo = 2 }, new() { Capo = 0 }, new() { Capo = null }, "2" },
        { LibraryFacets.Path, new() { ArrangementBadges = ["Bass"] }, new() { ArrangementBadges = ["Lead"] }, new() { ArrangementBadges = [] }, "Bass" },
        { LibraryFacets.Year, new() { Year = 1994 }, new() { Year = 2004 }, new() { Year = 0 }, "1990" },
        { LibraryFacets.Difficulty, new() { Difficulty = 8 }, new() { Difficulty = 2 }, new() { Difficulty = 0 }, "hard" },
        { LibraryFacets.Length, new() { LengthSeconds = 400 }, new() { LengthSeconds = 100 }, new() { LengthSeconds = 0 }, "long" },
        { LibraryFacets.Origin, new() { Origin = LibraryOrigin.Custom }, new() { Origin = LibraryOrigin.Stock }, new() { Origin = null }, "custom" },
        { LibraryFacets.Genre, new() { Genre = "Metal" }, new() { Genre = "Pop" }, new() { Genre = null }, "metal" }
    };

    [Theory]
    [MemberData(nameof(Facets))]
    public void EveryFacet_KeepsItsSelectedBucket_AndOffersTheUnknownOne(
        string facet, LibraryTestSong selected, LibraryTestSong other, LibraryTestSong unknown, string key)
    {
        var filter = None.With(facet, Keys(key));
        Assert.True(LibraryQuery.Matches(selected, filter));
        Assert.False(LibraryQuery.Matches(other, filter));
        Assert.False(LibraryQuery.Matches(unknown, filter));

        var withUnknown = None.With(facet, Keys(key, ""));
        Assert.True(LibraryQuery.Matches(unknown, withUnknown));
        Assert.True(LibraryQuery.Matches(selected, withUnknown));
        Assert.False(LibraryQuery.Matches(other, withUnknown));
    }

    [Fact]
    public void TheStatusAndPlayedFacets_HaveNoUnknownBucket()
    {
        var noAudio = new LibraryTestSong { CanLoad = false };
        Assert.True(LibraryQuery.Matches(noAudio, None with { Statuses = Keys("noaudio") }));
        Assert.False(LibraryQuery.Matches(new LibraryTestSong(), None with { Statuses = Keys("noaudio") }));

        Assert.True(LibraryQuery.Matches(LibraryTestSong.Played("p", 10), None with { PlayedStates = Keys("played") }));
        Assert.False(LibraryQuery.Matches(new LibraryTestSong(), None with { PlayedStates = Keys("played") }));
        // A catalogue row carries no record, so it is unplayed.
        Assert.True(LibraryQuery.Matches(new LibraryCatalogueSong("a", "t"), None with { PlayedStates = Keys("unplayed") }));
    }

    /// <summary>PATHS is multi-valued: ANY selected path keeps a row.</summary>
    [Fact]
    public void ThePathsFacet_KeepsARowWithAnySelectedPath()
    {
        var leadAndBass = new LibraryTestSong { ArrangementBadges = ["Lead", "Bass"] };
        Assert.True(LibraryQuery.Matches(leadAndBass, None with { Paths = Keys("Bass") }));
        Assert.True(LibraryQuery.Matches(leadAndBass, None with { Paths = Keys("Rhythm", "Lead") }));
        Assert.False(LibraryQuery.Matches(leadAndBass, None with { Paths = Keys("Rhythm") }));
    }

    /// <summary>A whitespace-only tuning is UNKNOWN: it falls in the "" bucket its option offers.</summary>
    [Fact]
    public void AWhitespaceTuning_IsTheUnknownBucket()
    {
        var blank = new LibraryTestSong { TuningSummary = "  " };
        Assert.Equal("", LibraryFacets.TuningKeyOf(blank));
        Assert.True(LibraryQuery.Matches(blank, None with { Tunings = Keys("") }));
    }

    [Theory]
    [InlineData("metallica one", true)]
    [InlineData("one metallica", true)]
    [InlineData("metallica - one", true)]
    [InlineData("ONE", true)]
    [InlineData("metallica two", false)]
    public void TheSearch_NeedsEveryToken_InAnyOrder(string query, bool expected)
    {
        var row = new LibraryTestSong { Artist = "Metallica", Title = "One" };
        Assert.Equal(expected, LibraryQuery.Matches(row, None with { Search = query }));
    }

    /// <summary>Both sides are folded: an unaccented query finds the accented row, and the
    /// other way round.</summary>
    [Theory]
    [InlineData("motorhead", "Motörhead")]
    [InlineData("Motörhead", "Motorhead")]
    [InlineData("bjork", "Björk")]
    [InlineData("MANA", "Maná")]
    public void TheSearch_FoldsCaseAndAccents_OnBothSides(string query, string artist)
        => Assert.True(LibraryQuery.Matches(new LibraryTestSong { Artist = artist }, None with { Search = query }));

    [Fact]
    public void TheSearch_ReadsTheCharterAndAlbumToo()
    {
        var row = new LibraryTestSong { Album = "Ride The Lightning", Charter = "Nitemare" };
        Assert.True(LibraryQuery.Matches(row, None with { Search = "nitemare lightning" }));
        Assert.False(LibraryQuery.Matches(row, None with { Search = "someone" }));
    }

    [Fact]
    public void HideLessons_DropsOnlyTheLessons()
    {
        var hide = None with { HideLessons = true };
        Assert.False(LibraryQuery.Matches(new LibraryTestSong { IsLesson = true }, hide));
        Assert.True(LibraryQuery.Matches(new LibraryTestSong(), hide));
        Assert.True(LibraryQuery.Matches(new LibraryTestSong { IsLesson = true }, None));
    }

    /// <summary>The RECORD ladder is an achievement filter: unplayed — or a row that carries no
    /// record at all — fails every step of it.</summary>
    [Fact]
    public void TheRecordLadder_FailsUnplayedRows_AndOrdersGrades()
    {
        var aOrBetter = 1 + PlayRecordFilter.GradesBestFirst.ToList().IndexOf(LetterGrade.A);
        var filter = None with { Record = aOrBetter };

        Assert.True(LibraryQuery.Matches(LibraryTestSong.Played("s", 10, LetterGrade.S), filter));
        Assert.True(LibraryQuery.Matches(LibraryTestSong.Played("a", 10, LetterGrade.A), filter));
        Assert.False(LibraryQuery.Matches(LibraryTestSong.Played("b", 10, LetterGrade.B), filter));
        Assert.False(LibraryQuery.Matches(new LibraryTestSong(), filter));
        Assert.False(LibraryQuery.Matches(new LibraryCatalogueSong("a", "t"), filter));

        var fullCombo = None with { Record = PlayRecordFilter.FullComboIndex };
        Assert.True(LibraryQuery.Matches(LibraryTestSong.Played("fc", 10, fullCombo: true), fullCombo));
        Assert.False(LibraryQuery.Matches(LibraryTestSong.Played("no", 10), fullCombo));
    }

    [Fact]
    public void IsNarrowing_IsTheSearchAFacetOrTheRecord_NeverTheLessonsToggle()
    {
        Assert.False(None.IsNarrowing);
        Assert.False((None with { HideLessons = true }).IsNarrowing);
        Assert.False((None with { Years = Keys() }).IsNarrowing);
        Assert.True((None with { Search = "x" }).IsNarrowing);
        Assert.True((None with { Years = Keys("1990") }).IsNarrowing);
        Assert.True((None with { Statuses = Keys("loadable") }).IsNarrowing);
        Assert.True((None with { Record = 1 }).IsNarrowing);
    }

    [Fact]
    public void FromFacets_TrimsTheSearch_AndFilesEachSelectionUnderItsKey()
    {
        var filter = LibraryFilter.FromFacets("  one  ",
            [(LibraryFacets.Year, Keys("1990")), (LibraryFacets.Path, Keys("Bass"))], record: 2, hideLessons: true);

        Assert.Equal("one", filter.Search);
        Assert.Equal(Keys("1990"), filter.Years);
        Assert.Equal(Keys("Bass"), filter.Paths);
        Assert.Equal(2, filter.Record);
        Assert.True(filter.HideLessons);
        Assert.Throws<ArgumentOutOfRangeException>(() => None.With("nope", Keys("x")));
    }

    /// <summary>A popover's counts are "the list with every OTHER filter applied": the facet
    /// being counted is inert, every other one (and the search) still narrows.</summary>
    [Fact]
    public void CountFacet_IgnoresItsOwnSelection_AndAppliesEveryOtherFilter()
    {
        LibraryTestSong[] songs =
        [
            new() { Artist = "A", TuningSummary = "D A D G B E", Year = 1991 },
            new() { Artist = "A", TuningSummary = "E A D G B E", Year = 1992 },
            new() { Artist = "B", TuningSummary = "E A D G B E", Year = 2005 },
            new() { Artist = "A", TuningSummary = "", Year = 1999 }
        ];
        var filter = None with { Search = "a", Tunings = Keys("D A D G B E"), Years = Keys("1990") };

        var counts = LibraryQuery.CountFacet(songs, filter, LibraryFacets.Tuning);

        Assert.Equal(1, counts["D A D G B E"]);
        Assert.Equal(1, counts["E A D G B E"]);
        Assert.Equal(1, counts[""]);
        Assert.Equal(3, counts.Values.Sum());
    }

    /// <summary>PATHS counts a row once under each of its paths, and a pathless row under "".</summary>
    [Fact]
    public void CountFacet_CountsEachPathOfARow_AndThePathlessUnderUnknown()
    {
        LibraryTestSong[] songs =
        [
            new() { ArrangementBadges = ["Lead", "Bass"] },
            new() { ArrangementBadges = ["Lead"] },
            new() { ArrangementBadges = [] }
        ];

        var counts = LibraryQuery.CountFacet(songs, None, LibraryFacets.Path);

        Assert.Equal(2, counts["Lead"]);
        Assert.Equal(1, counts["Bass"]);
        Assert.Equal(1, counts[""]);
    }

    [Theory]
    [InlineData(" - ", new string[0])]
    [InlineData("artist - title", new[] { "artist", "title" })]
    [InlineData("  a  b ", new[] { "a", "b" })]
    public void Tokenize_SplitsOnWhitespace_AndDropsALoneDash(string query, string[] expected)
        => Assert.Equal(expected, LibraryQuery.Tokenize(query));
}
