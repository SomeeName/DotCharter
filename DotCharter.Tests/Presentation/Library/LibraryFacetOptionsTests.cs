using DotCharter.Presentation.Shared.Library;
using DotCharter.Services.Library;

namespace DotCharter.Tests.Presentation.Library;

/// <summary>
///     Each facet's options, derived from the rows (#651): only values present, a fixed order per
///     facet, and the unknown bucket ("") LAST exactly where a row is unknown — both song lists build
///     their popovers from this one catalogue.
/// </summary>
public sealed class LibraryFacetOptionsTests
{
    private static readonly FakeLocalization Loc = FakeLocalization.KeysOnly();

    private static List<string> Values(string facet, params ILibrarySong[] songs)
        => [.. LibraryFacetOptions.For(facet, songs, Loc).Select(o => o.Value)];

    /// <summary>Every distinct raw tuning is its own option, labelled with its name AND its
    /// letters — so two spellings of one tuning read apart and each is reachable.</summary>
    [Fact]
    public void Tunings_OfferEveryRawSpelling_LabelledWithNameAndLetters_UnknownLast()
    {
        var options = LibraryFacetOptions.For(LibraryFacets.Tuning,
        [
            new LibraryTestSong { TuningSummary = "E A D G B E" },
            new LibraryTestSong { TuningSummary = "Eb Ab Db Gb Bb Eb" },
            new LibraryTestSong { TuningSummary = "D# G# C# F# A# D#" },
            new LibraryTestSong { TuningSummary = "D A D G B E" },
            new LibraryTestSong { TuningSummary = "E A D G B E" },
            new LibraryTestSong { TuningSummary = " " }
        ], Loc);

        Assert.Equal(
        [
            ("D A D G B E", "Drop D (D A D G B E)"),
            ("E A D G B E", "E Standard (E A D G B E)"),
            ("D# G# C# F# A# D#", "Eb Standard (D# G# C# F# A# D#)"),
            ("Eb Ab Db Gb Bb Eb", "Eb Standard (Eb Ab Db Gb Bb Eb)"),
            ("", "SongBrowser_Facet_Unknown")
        ], options);
    }

    [Fact]
    public void ALibraryWithNoUnknowns_OffersNoUnknownBucket()
    {
        var song = new LibraryTestSong { Genre = "Rock" };
        string[] facets =
        [
            LibraryFacets.Tuning, LibraryFacets.Capo, LibraryFacets.Path, LibraryFacets.Year,
            LibraryFacets.Difficulty, LibraryFacets.Length, LibraryFacets.Origin, LibraryFacets.Genre
        ];
        foreach (var facet in facets) Assert.DoesNotContain("", Values(facet, song));
    }

    [Fact]
    public void Capos_OrderByFret_NoneFirst_UnknownLast()
    {
        var options = LibraryFacetOptions.For(LibraryFacets.Capo,
            [new LibraryTestSong { Capo = 10 }, new LibraryTestSong { Capo = null }, new LibraryTestSong { Capo = 0 }, new LibraryTestSong { Capo = 2 }], Loc);

        Assert.Equal(["0", "2", "10", ""], options.Select(o => o.Value));
        Assert.Equal("Dialog_SongBrowser_CapoNone", options[0].Label);
    }

    [Fact]
    public void Decades_NewestFirst_UnknownLast()
        => Assert.Equal(["2000", "1990", "1970", ""], Values(LibraryFacets.Year,
            new LibraryTestSong { Year = 1994 }, new LibraryTestSong { Year = 0 },
            new LibraryTestSong { Year = 2003 }, new LibraryTestSong { Year = 1975 }, new LibraryTestSong { Year = 1999 }));

    [Fact]
    public void Bands_KeepTheirFixedOrder_AndOnlyThoseRowsFallIn()
    {
        Assert.Equal(["easy", "hard", ""], Values(LibraryFacets.Difficulty,
            new LibraryTestSong { Difficulty = 9 }, new LibraryTestSong { Difficulty = 0 }, new LibraryTestSong { Difficulty = 1 }));
        Assert.Equal(["short", "long"], Values(LibraryFacets.Length,
            new LibraryTestSong { LengthSeconds = 500 }, new LibraryTestSong { LengthSeconds = 60 }));
        Assert.Equal(["stock", "custom", ""], Values(LibraryFacets.Origin,
            new LibraryTestSong { Origin = LibraryOrigin.Unknown }, new LibraryTestSong { Origin = LibraryOrigin.Custom },
            new LibraryTestSong { Origin = LibraryOrigin.Stock }));
    }

    [Fact]
    public void Paths_AreDistinctAcrossRows_WithUnknownForAPathlessRow()
        => Assert.Equal(["Bass", "Lead", "Rhythm", ""], Values(LibraryFacets.Path,
            new LibraryTestSong { ArrangementBadges = ["Lead", "Rhythm"] },
            new LibraryTestSong { ArrangementBadges = ["Bass", "Lead"] },
            new LibraryTestSong { ArrangementBadges = [] }));

    /// <summary>Both statuses on an empty library (the popover exists before the first scan);
    /// only the present ones after.</summary>
    [Fact]
    public void Statuses_AreBothOnAnEmptyLibrary_ThenOnlyThePresentOnes()
    {
        Assert.Equal(["loadable", "noaudio"], Values(LibraryFacets.Status));
        Assert.Equal(["loadable"], Values(LibraryFacets.Status, new LibraryTestSong()));
    }

    [Fact]
    public void Played_IsAlwaysBothOptions()
        => Assert.Equal(["played", "unplayed"], Values(LibraryFacets.Played));

    /// <summary>One option per genre whatever its casing; the label keeps the first authored one.</summary>
    [Fact]
    public void Genres_FoldCase_KeepTheFirstLabel_UnknownLast()
    {
        var options = LibraryFacetOptions.For(LibraryFacets.Genre,
        [
            new LibraryTestSong { Genre = " Metal " },
            new LibraryTestSong { Genre = "metal" },
            new LibraryTestSong { Genre = "Blues" },
            new LibraryCatalogueSong("a", "no genre")
        ], Loc);

        Assert.Equal([("blues", "Blues"), ("metal", "Metal"), ("", "SongBrowser_Facet_Unknown")], options);
    }

    [Fact]
    public void EveryFacet_HasATitle_AndAnUnknownFacetIsRefused()
    {
        string[] facets =
        [
            LibraryFacets.Tuning, LibraryFacets.Capo, LibraryFacets.Path, LibraryFacets.Year,
            LibraryFacets.Difficulty, LibraryFacets.Length, LibraryFacets.Status, LibraryFacets.Origin,
            LibraryFacets.Played, LibraryFacets.Genre
        ];
        foreach (var facet in facets)
        {
            Assert.False(string.IsNullOrWhiteSpace(FakeLocalization.English()[LibraryFacets.TitleKey(facet)]));
            _ = LibraryFacetOptions.For(facet, [], Loc);
        }

        Assert.Throws<ArgumentOutOfRangeException>(() => LibraryFacetOptions.For("nope", [], Loc));
        Assert.Throws<ArgumentOutOfRangeException>(() => LibraryFacets.TitleKey("nope"));
    }
}
