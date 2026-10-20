using DotCharter.Services.Gameplay.Scoring;
using DotCharter.Services.Gameplay.Stats;
using DotCharter.Services.Library;
using DotCharter.UI.ViewModels.Panels;
using DotCharter.UI.ViewModels.PlayMode;
using DotCharter.Presentation.Shared.Library;

namespace DotCharter.Tests.UI.ViewModels.PlayMode;

/// <summary>
///     Pure-predicate coverage for the UNIFIED Song Select filter (2026-08-15):
///     the metadata filters are facet SELECTION SETS of canonical bucket keys —
///     the editor browser's model — with unknown values as an explicit ""
///     bucket the user can select, replacing the old silent unknown-passes
///     rule. Covers each facet leg alone and combined, the key extractors at
///     their bucket boundaries, and the RECORD achievement exception.
///     No Avalonia instance required.
/// </summary>
public sealed class PlaySongFilterTests
{
    // A criteria with every filter disabled; individual tests flip one field via
    // `with` so intent stays obvious.
    private static readonly LibraryFilter None = new("");

    /// <summary>A facet selection, as the criteria carries one.</summary>
    private static IReadOnlyCollection<string> Keys(params string[] keys) => keys;

    private static PlaySongItem Owned(
        string title = "Song", string artist = "Artist", string album = "Album",
        double lengthSeconds = 200, int capo = 0, string tuning = "E Standard", int routeMask = 1,
        string songKey = "sk", string charter = "chr", string version = "1",
        bool hasBest = false, int masterId = 0, string? sku = null)
    {
        var entry = new LibrarySongEntry
        {
            Title = title, Artist = artist, Album = album, SongLength = lengthSeconds,
            CapoSummary = capo, TuningSummary = tuning, SongKey = songKey, Charter = charter, Version = version,
            Arrangements =
            [
                new LibraryArrangementEntry
                {
                    RouteMask = routeMask, SngPath = "a.sng", MasterId = masterId, Sku = sku
                }
            ]
        };
        var item = PlaySongItem.FromPsarc(new PsarcSongListItem(entry, FakeLocalization.English()));
        item.HasBest = hasBest;
        return item;
    }

    private static PlaySongItem OwnChart(string path = @"C:\charts\MyChart.dcp")
        => PlaySongItem.FromProject(path, false, FakeLocalization.English());

    // ---- empty criteria -------------------------------------------------------

    [Fact]
    public void NoFilters_EverythingPasses()
    {
        Assert.True(LibraryQuery.Matches(Owned(), None));
        Assert.True(LibraryQuery.Matches(OwnChart(), None));
        Assert.False(None.IsNarrowing);
    }

    // ---- search ----------------------------------------------------------------

    [Theory]
    [InlineData("song", true)]   // title
    [InlineData("ARTI", true)]   // artist, case-insensitive
    [InlineData("albu", true)]   // album
    [InlineData("nope", false)]
    public void Search_MatchesTitleArtistAlbum_Owned(string query, bool expected)
        => Assert.Equal(expected, LibraryQuery.Matches(Owned(), None with { Search = query }));

    /// <summary>
    ///     The bug the owner hit: songs are SHOWN and TYPED as "Artist - Title",
    ///     and the old per-field substring match found nothing for that string
    ///     because it is a substring of neither the title alone nor the artist
    ///     alone. Tokenized search over the combined haystack must match it, in
    ///     any order, with or without the dash.
    /// </summary>
    [Theory]
    [InlineData("Metallica - One", true)]   // exactly how it displays / how you'd type it
    [InlineData("metallica one", true)]     // no dash
    [InlineData("one metallica", true)]     // reversed
    [InlineData("metal on", true)]          // partial tokens spanning artist + title
    [InlineData("metallica two", false)]    // one token matches, the other does not → no match
    public void Search_MatchesArtistDashTitle(string query, bool expected)
        => Assert.Equal(expected,
            LibraryQuery.Matches(Owned(title: "One", artist: "Metallica"), None with { Search = query }));

    /// <summary>A query that is only separators must not match everything (or
    /// nothing must crash) — it carries no signal, so it should pass the search
    /// gate the way an empty query does.</summary>
    [Fact]
    public void Search_OnlySeparators_IsInert()
        => Assert.True(LibraryQuery.Matches(Owned(), None with { Search = " - " }));

    [Fact]
    public void Search_SongWithNoArtistOrAlbum_DoesNotThrow()
    {
        var owned = Owned(title: "Instrumental", artist: "", album: "");
        Assert.True(LibraryQuery.Matches(owned, None with { Search = "instrumental" }));
        Assert.False(LibraryQuery.Matches(owned, None with { Search = "artist" }));
    }

    [Fact]
    public void Search_OwnChart_MatchesTitleOnly()
    {
        var chart = OwnChart(@"C:\charts\Nirvana Live.dcp");
        Assert.True(LibraryQuery.Matches(chart, None with { Search = "nirvana" }));
        Assert.False(LibraryQuery.Matches(chart, None with { Search = "artist" }));
    }

    // ---- the charter reaches free-text search ---------------------------------

    /// <summary>A library holds several charters' cuts of the same song and their
    /// names were printed nowhere search could reach — typing one found nothing.
    /// Own charts have none.</summary>
    [Fact]
    public void Search_MatchesTheCharter()
    {
        Assert.True(LibraryQuery.Matches(Owned(charter: "Nitemare"), None with { Search = "nitemare" }));
        // …and it still combines with the other fields, in any order.
        Assert.True(LibraryQuery.Matches(Owned(charter: "Nitemare", artist: "Metallica"),
            None with { Search = "metallica nitemare" }));
        Assert.False(LibraryQuery.Matches(Owned(charter: "Nitemare"), None with { Search = "someoneelse" }));
    }

    // ---- PATHS: the one multi-valued facet ------------------------------------

    [Fact]
    public void Paths_AnySelectedPathKeepsTheRow()
    {
        // routeMask 1 = Lead, 2 = Rhythm, 4 = Bass (badges via ArrangementSummary).
        Assert.True(LibraryQuery.Matches(Owned(routeMask: 1), None with { Paths = Keys("Lead") }));
        Assert.False(LibraryQuery.Matches(Owned(routeMask: 2), None with { Paths = Keys("Lead") }));
        Assert.True(LibraryQuery.Matches(Owned(routeMask: 4), None with { Paths = Keys("Bass") }));
        // Multi-select: either selection keeps it.
        Assert.True(LibraryQuery.Matches(Owned(routeMask: 2), None with { Paths = Keys("Lead", "Rhythm") }));
    }

    /// <summary>THE POLICY since the unified browser: unknown is an explicit ""
    /// bucket the user can tick, not a silent always-pass. An own chart (no
    /// arrangement summary) fails a "Lead" selection and passes an unknown
    /// selection.</summary>
    [Fact]
    public void Paths_UnknownIsAnExplicitBucket()
    {
        Assert.False(LibraryQuery.Matches(OwnChart(), None with { Paths = Keys("Lead") }));
        Assert.True(LibraryQuery.Matches(OwnChart(), None with { Paths = Keys("") }));
        Assert.True(LibraryQuery.Matches(OwnChart(), None with { Paths = Keys("Lead", "") }));
    }

    // ---- played / unplayed ----------------------------------------------------

    [Fact]
    public void PlayedStates_FilterOnBestScorePresence()
    {
        Assert.True(LibraryQuery.Matches(Owned(hasBest: true), None with { PlayedStates = Keys("played") }));
        Assert.False(LibraryQuery.Matches(Owned(hasBest: false), None with { PlayedStates = Keys("played") }));
        Assert.True(LibraryQuery.Matches(Owned(hasBest: false), None with { PlayedStates = Keys("unplayed") }));
        Assert.False(LibraryQuery.Matches(Owned(hasBest: true), None with { PlayedStates = Keys("unplayed") }));
        // Both ticked = no narrowing in effect.
        Assert.True(LibraryQuery.Matches(Owned(hasBest: true),
            None with { PlayedStates = Keys("played", "unplayed") }));
    }

    // ---- tuning ---------------------------------------------------------------

    [Fact]
    public void Tunings_ExactSummaryMatch_UnknownIsItsOwnBucket()
    {
        Assert.True(LibraryQuery.Matches(Owned(tuning: "Drop D"), None with { Tunings = Keys("Drop D") }));
        Assert.False(LibraryQuery.Matches(Owned(tuning: "E Standard"), None with { Tunings = Keys("Drop D") }));
        // Own-chart tuning is unknown ("" key): fails a real selection,
        // passes the unknown bucket.
        Assert.False(LibraryQuery.Matches(OwnChart(), None with { Tunings = Keys("Drop D") }));
        Assert.True(LibraryQuery.Matches(OwnChart(), None with { Tunings = Keys("") }));
    }

    // ---- capo -----------------------------------------------------------------

    [Fact]
    public void CapoKeyOf_IsTheInvariantFretOrUnknown()
    {
        Assert.Equal("0", LibraryFacets.CapoKeyOf(Owned(capo: 0)));
        Assert.Equal("3", LibraryFacets.CapoKeyOf(Owned(capo: 3)));
        Assert.Equal("", LibraryFacets.CapoKeyOf(OwnChart()));
    }

    [Fact]
    public void Capos_SelectExactFrets_UnknownIsItsOwnBucket()
    {
        Assert.True(LibraryQuery.Matches(Owned(capo: 0), None with { Capos = Keys("0") }));
        Assert.False(LibraryQuery.Matches(Owned(capo: 3), None with { Capos = Keys("0") }));
        Assert.True(LibraryQuery.Matches(Owned(capo: 3), None with { Capos = Keys("3") }));
        Assert.True(LibraryQuery.Matches(Owned(capo: 3), None with { Capos = Keys("0", "3") }));
        // Unknown capo (own chart) is the "" bucket, not a free pass.
        Assert.True(LibraryQuery.Matches(OwnChart(), None with { Capos = Keys("") }));
    }

    // ---- length buckets (boundaries live in the KEY extractor now) ------------

    [Theory]
    [InlineData(179, "short")]  // < 3 min
    [InlineData(180, "mid")]    // 180 is not < 180 — inclusive lower bound of mid
    [InlineData(300, "mid")]    // inclusive upper bound
    [InlineData(301, "long")]   // > 5 min
    [InlineData(0, "")]         // unknown length
    public void LengthKeyOf_BoundariesAreExact(int seconds, string expected)
        => Assert.Equal(expected, LibraryFacets.LengthKeyOf(Owned(lengthSeconds: seconds)));

    [Fact]
    public void Lengths_SelectBuckets_UnknownIsItsOwnBucket()
    {
        Assert.True(LibraryQuery.Matches(Owned(lengthSeconds: 100), None with { Lengths = Keys("short") }));
        Assert.False(LibraryQuery.Matches(Owned(lengthSeconds: 400), None with { Lengths = Keys("short") }));
        Assert.True(LibraryQuery.Matches(Owned(lengthSeconds: 400), None with { Lengths = Keys("short", "long") }));
        // Own charts report length 0 (unknown): the "" bucket, not a free pass.
        Assert.False(LibraryQuery.Matches(OwnChart(), None with { Lengths = Keys("short") }));
        Assert.True(LibraryQuery.Matches(OwnChart(), None with { Lengths = Keys("") }));
    }

    // ---- difficulty bands -----------------------------------------------------

    private static PlaySongItem Rated(double difficulty)
    {
        var item = Owned();
        item.Difficulty = difficulty;
        return item;
    }

    [Theory]
    [InlineData(2.0, "easy")]
    [InlineData(3.9, "easy")]
    [InlineData(4.0, "medium")]  // inclusive lower bound
    [InlineData(6.9, "medium")]
    [InlineData(7.0, "medium")]  // INCLUSIVE upper bound — the label says "Medium (4-7)"
    [InlineData(7.1, "hard")]    // …and "Hard (above 7)"
    [InlineData(0, "")]          // never rated
    public void DifficultyKeyOf_BandBoundariesAreExact(double rating, string expected)
        => Assert.Equal(expected, LibraryFacets.DifficultyKeyOf(Rated(rating)));

    /// <summary>
    ///     THE POINT of the two surfaces sharing a facet model: one library, one
    ///     filter, one song must give one answer. This read <c>&lt; 7</c> while
    ///     the editor browser's band key read
    ///     <c>&lt;= 7</c>, so a song rated exactly 7.0 was MEDIUM in the browser
    ///     and HARD in Play Mode — and Play Mode disagreed with the option label
    ///     both surfaces print, which is what settles it.
    /// </summary>
    [Theory]
    [InlineData(0)]
    [InlineData(3.999)]
    [InlineData(4.0)]
    [InlineData(6.999)]
    [InlineData(7.0)]
    [InlineData(7.001)]
    [InlineData(10.0)]
    public void TheDifficultyBands_AgreeWithTheEditorBrowsers(double rating)
    {
        var row = new PsarcSongListItem(new LibrarySongEntry
        {
            SongKey = "k", Title = "T", Artist = "A", Difficulty = rating,
            PackagePath = @"C:\lib\k_p.psarc"
        }, FakeLocalization.English());

        Assert.Equal(LibraryFacets.DifficultyKeyOf(row), LibraryFacets.DifficultyKeyOf(Rated(rating)));
    }

    [Fact]
    public void Difficulties_SelectBands_UnratedIsItsOwnBucket()
    {
        Assert.True(LibraryQuery.Matches(Rated(8.5), None with { Difficulties = Keys("hard") }));
        Assert.False(LibraryQuery.Matches(Rated(2.0), None with { Difficulties = Keys("hard") }));
        // Rating 0 = never rated (an own chart): the "" bucket.
        Assert.False(LibraryQuery.Matches(Rated(0), None with { Difficulties = Keys("easy") }));
        Assert.True(LibraryQuery.Matches(Rated(0), None with { Difficulties = Keys("") }));
    }

    [Fact]
    public void Difficulties_CombineWithOtherFacets()
    {
        var criteria = None with { Lengths = Keys("mid"), Difficulties = Keys("hard") };
        Assert.True(LibraryQuery.Matches(Rated(8.5), criteria));
        Assert.False(LibraryQuery.Matches(Rated(2.0), criteria));  // right length but easy
    }

    // ---- record: the ACHIEVEMENT filter, and its policy split -----------------

    /// <summary>A song with a recorded run: HasBest plus the raw grade and the
    /// sticky full-combo flag, exactly the three <c>ApplyStats</c> stamps.</summary>
    private static PlaySongItem WithRecord(LetterGrade grade, bool fullCombo = false)
    {
        var item = Owned();
        item.HasBest = true;
        item.Grade = grade;
        item.IsFullCombo = fullCombo;
        return item;
    }

    /// <summary>The filter's data comes from the REAL stats path, not from a test
    /// setter: <c>ApplyStats</c> must carry the raw grade and the full-combo flag
    /// through, or every assertion below is testing a fiction. (GradeLabel is a
    /// display string — the filter has to ORDER grades, and a letter is not an
    /// order.)</summary>
    [Fact]
    public void ApplyStats_CarriesTheRawGradeAndFullCombo_NotJustTheLabel()
    {
        var song = Owned();
        song.ApplyStats(new SongPlayStats("sk", "Lead", 3, 12345, 0.97, 80,
            LetterGrade.A, DateTime.UtcNow, "Song", FullCombo: true));

        Assert.True(song.HasBest);
        Assert.Equal(LetterGrade.A, song.Grade);
        Assert.True(song.IsFullCombo);
        Assert.Equal("A", song.GradeLabel);

        // …and a song with no stats reports no record at all.
        var unplayed = Owned();
        unplayed.ApplyStats(null);
        Assert.False(unplayed.HasBest);
        Assert.False(unplayed.IsFullCombo);
    }

    /// <summary>"Grade X or better" keeps everything at or above the floor, on the
    /// scale <see cref="GradeCalculator" /> defines (F worst → S best). Index 1 is
    /// the best grade, so the floors get LOOSER as the index rises.</summary>
    [Theory]
    [InlineData(1, LetterGrade.S, true)]    // S+ keeps S
    [InlineData(1, LetterGrade.A, false)]   // …and drops A
    [InlineData(2, LetterGrade.S, true)]    // A+ keeps S
    [InlineData(2, LetterGrade.A, true)]    // …and A
    [InlineData(2, LetterGrade.B, false)]   // …but not B
    [InlineData(3, LetterGrade.B, true)]    // B+ inclusive at its own floor
    [InlineData(6, LetterGrade.F, true)]    // the bottom floor keeps every played song
    public void Record_GradeFloor_IsInclusiveAndOrdered(int option, LetterGrade grade, bool expected)
        => Assert.Equal(expected, LibraryQuery.Matches(WithRecord(grade), None with { Record = option }));

    [Fact]
    public void Record_FullComboOnly_KeepsOnlyTheFullCombos()
    {
        var fc = PlayRecordFilter.FullComboIndex;
        Assert.True(LibraryQuery.Matches(WithRecord(LetterGrade.B, fullCombo: true), None with { Record = fc }));
        // A perfect GRADE is not a full combo — the two are different claims.
        Assert.False(LibraryQuery.Matches(WithRecord(LetterGrade.S), None with { Record = fc }));
    }

    /// <summary>
    ///     THE POLICY SPLIT, restated for the facet era. Every metadata facet
    ///     files an unknown value under the "" bucket THE USER can include; the
    ///     record filter has no unknown bucket at all, because an unplayed
    ///     song's record is not unknown — it is KNOWN to be nothing. "Show me
    ///     what I have A-ranked" that quietly kept every song I have never
    ///     touched would be useless.
    /// </summary>
    [Fact]
    public void Record_UnplayedSongsFail_WhileMetadataFilesThemUnderUnknown()
    {
        var fc = PlayRecordFilter.FullComboIndex;
        // Rows whose METADATA is unknown too: an unreadable package and an own
        // chart. Each one fails every record option while the same
        // rows are REACHABLE through the metadata facets' unknown buckets —
        // that contrast IS the policy.
        foreach (var song in new[] { UnknownPackage(), OwnChart() })
        {
            Assert.False(LibraryQuery.Matches(song, None with { Record = 1 }));
            Assert.False(LibraryQuery.Matches(song, None with { Record = fc }));

            Assert.True(LibraryQuery.Matches(song, None with { Origins = Keys("") }));
            Assert.True(LibraryQuery.Matches(song, None with { Genres = Keys("") }));
        }

        // …and a perfectly readable, perfectly ordinary library song fails it too,
        // for the one reason that matters: nobody has played it.
        Assert.False(LibraryQuery.Matches(Owned(hasBest: false), None with { Record = 1 }));

        // THE BOTTOM FLOOR is where the HasBest guard earns its keep. "F or
        // better" is a grade comparison every song satisfies — an unplayed one
        // reads LetterGrade.F simply because that is the enum's zero — so without
        // the guard this option would quietly mean "every song in the library".
        Assert.False(LibraryQuery.Matches(Owned(hasBest: false),
            None with { Record = PlayRecordFilter.GradesBestFirst.Count }));
        Assert.True(LibraryQuery.Matches(WithRecord(LetterGrade.F),
            None with { Record = PlayRecordFilter.GradesBestFirst.Count }));

        // Option 0 is "any record" and narrows nothing at all.
        Assert.True(LibraryQuery.Matches(OwnChart(), None with { Record = 0 }));
        Assert.False((None with { Record = 0 }).IsNarrowing);
        Assert.True((None with { Record = 1 }).IsNarrowing);
    }

    // ---- origin ---------------------------------------------------------------

    /// <summary>Stock-shaped: no toolkit stamp, a small hand-assigned MasterID
    /// (the signals the plugin's origin classifier reads; this stand-in mirrors them).</summary>
    private static PlaySongItem StockPackage()
        => Owned(charter: "", version: "", masterId: 32701, sku: "RS2");

    /// <summary>A package nothing settled — no stamp, no ids, no SKU.</summary>
    private static PlaySongItem UnknownPackage() => Owned(charter: "", version: "");

    /// <summary>The plugin's rule in miniature: a stamp is custom, a master id without one is stock.</summary>
    private sealed class StandInOriginClassifier : ILibraryOriginClassifier
    {
        public LibraryOrigin Classify(LibrarySongEntry song) =>
            song.Charter.Length > 0 || song.Version.Length > 0 ? LibraryOrigin.Custom
            : song.Arrangements.Any(a => a.MasterId > 0) ? LibraryOrigin.Stock
            : LibraryOrigin.Unknown;
    }

    [Fact]
    public void Origins_SplitStockFromCustom()
    {
        using var scope = LibraryOrigins.UseForTesting(new StandInOriginClassifier());
        // The default Owned() carries a toolkit stamp, which IS the CDLC signal.
        Assert.Equal("custom", LibraryFacets.OriginKeyOf(Owned()));
        Assert.True(LibraryQuery.Matches(Owned(), None with { Origins = Keys("custom") }));
        Assert.False(LibraryQuery.Matches(Owned(), None with { Origins = Keys("stock") }));

        Assert.Equal("stock", LibraryFacets.OriginKeyOf(StockPackage()));
        Assert.True(LibraryQuery.Matches(StockPackage(), None with { Origins = Keys("stock") }));
        Assert.False(LibraryQuery.Matches(StockPackage(), None with { Origins = Keys("custom") }));
    }

    [Fact]
    public void Origins_UnknownIsItsOwnBucket()
    {
        using var scope = LibraryOrigins.UseForTesting(new StandInOriginClassifier());
        // Two different flavours of "we cannot say": an unreadable package and
        // an own chart (which is not a package at all).
        // All carry the "" key — reachable only through the unknown option.
        foreach (var song in new[] { UnknownPackage(), OwnChart() })
        {
            Assert.Equal("", LibraryFacets.OriginKeyOf(song));
            Assert.False(LibraryQuery.Matches(song, None with { Origins = Keys("stock") }));
            Assert.False(LibraryQuery.Matches(song, None with { Origins = Keys("custom") }));
            Assert.True(LibraryQuery.Matches(song, None with { Origins = Keys("") }));
            Assert.True(LibraryQuery.Matches(song, None with { Origins = Keys("stock", "custom", "") }));
        }

        Assert.Null(OwnChart().Origin);
        Assert.Equal(LibraryOrigin.Unknown, UnknownPackage().Origin);
    }

    // ---- genre ----------------------------------------------------------------

    private static PlaySongItem WithGenre(string genre)
    {
        var chart = OwnChart();
        chart.ApplyGenre(genre);
        return chart;
    }

    /// <summary>The genre KEY is casefolded — "Metal" and "metal" are one
    /// authored genre, so both carry the same key and one facet option covers
    /// them (its label keeps the authored casing; the key does not).</summary>
    [Fact]
    public void Genres_MatchOnTheCasefoldedKey()
    {
        Assert.Equal("metal", LibraryFacets.GenreKeyOf(WithGenre("Metal")));
        Assert.Equal("metal", LibraryFacets.GenreKeyOf(WithGenre("METAL ")));
        Assert.True(LibraryQuery.Matches(WithGenre("Metal"), None with { Genres = Keys("metal") }));
        Assert.True(LibraryQuery.Matches(WithGenre("METAL"), None with { Genres = Keys("metal") }));
        Assert.False(LibraryQuery.Matches(WithGenre("Metal"), None with { Genres = Keys("jazz") }));
        Assert.True(LibraryQuery.Matches(WithGenre("Metal"), None with { Genres = Keys("jazz", "metal") }));
    }

    [Fact]
    public void Genres_NoGenreIsItsOwnBucket()
    {
        // Which is nearly the whole library: a Rocksmith package carries no genre
        // field and the LAN catalogue shares none, so only own charts have one.
        foreach (var song in new[] { Owned(), OwnChart() })
        {
            Assert.Equal("", LibraryFacets.GenreKeyOf(song));
            Assert.False(LibraryQuery.Matches(song, None with { Genres = Keys("Metal") }));
            Assert.True(LibraryQuery.Matches(song, None with { Genres = Keys("") }));
        }
    }

    // ---- combined + empty result ---------------------------------------------

    [Fact]
    public void Combined_EmptyResult_WhenNoSongSatisfies()
    {
        var songs = new[] { Owned(lengthSeconds: 400), OwnChart(), Owned(lengthSeconds: 420) };
        // "> 5 min" AND a search that matches none.
        var criteria = None with { Lengths = Keys("long"), Search = "no-such-song" };
        Assert.DoesNotContain(songs, s => LibraryQuery.Matches(s, criteria));
    }

    /// <summary>The unknown bucket composes with other facets like any value:
    /// "played state + unknown length" keeps exactly the own chart.</summary>
    [Fact]
    public void Combined_UnplayedWithUnknownLength_KeepsExactlyTheOwnChart()
    {
        var criteria = None with { PlayedStates = Keys("unplayed"), Lengths = Keys("") };
        Assert.False(LibraryQuery.Matches(Owned(lengthSeconds: 400), criteria)); // known length
        Assert.True(LibraryQuery.Matches(OwnChart(), criteria));                 // unplayed, unknown length
    }

    // ---- IsNarrowing ----------------------------------------------------------

    [Fact]
    public void IsNarrowing_TrueForAnyActiveControl_FalseForEmptySets()
    {
        Assert.False(None.IsNarrowing);
        Assert.False((None with { Paths = Array.Empty<string>() }).IsNarrowing); // empty set = inert
        Assert.True((None with { Search = "x" }).IsNarrowing);
        Assert.True((None with { Paths = Keys("Lead") }).IsNarrowing);
        Assert.True((None with { Tunings = Keys("") }).IsNarrowing);
        Assert.True((None with { Record = 1 }).IsNarrowing);
    }

    // ---- version comparison ---------------------------------------------------

}
