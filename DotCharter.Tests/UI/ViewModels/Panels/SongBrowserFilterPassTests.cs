using System.Globalization;
using DotCharter.Services.Editing.Selection;
using DotCharter.Services.Library;
using DotCharter.UI.ViewModels.Editor;
using DotCharter.UI.ViewModels.Panels;
using DotCharter.Services.Editing.Tracks;
using DotCharter.Services.Session;
using DotCharter.Presentation.Shared.Library;

namespace DotCharter.Tests.UI.ViewModels.Panels;

/// <summary>
///     The song browser's per-keystroke cost, pinned by STRUCTURE.
///     <para>
///         The owner reported search and filtering as laggy on a 2,160-song
///         library. Measured (Release, headless, the real <c>SongBrowserView</c>
///         attached, same probe for both legs — <c>Perf/SongLibrarySearchProfileUiPerfSmokeTests</c>):
///         typing "metallica" cost <b>416–624 ms</b> of UI-thread work spread
///         across the burst, with single keystrokes as slow as <b>349 ms</b>,
///         because every keystroke ran a whole filter+sort+republish pass out of
///         the property setter. Coalescing it takes the burst to <b>2.4–3.1 ms</b>
///         over the same nine keystrokes and one pass at idle.
///     </para>
///     <para>
///         Nothing here asserts a duration — the profiler logs those. These assert
///         the ratio (N keystrokes ⇒ 1 pass), that a discrete gesture is NOT
///         deferred, and — the one that matters most — that the coalesced result is
///         byte-identical to filtering on every keystroke.
///     </para>
/// </summary>
public sealed class SongBrowserFilterPassTests
{
    private const string Query = "metallica";

    /// <summary>N keystrokes, ONE pass. The old shape ran nine.</summary>
    [Fact]
    public async Task NineKeystrokes_RunOneFilterPass_NotNine()
    {
        var h = await Harness.Scanned();

        var before = h.Vm.FilterPass.PassCount;
        for (var i = 1; i <= Query.Length; i++) h.Vm.SearchText = Query[..i];

        // Still showing the unfiltered list: the pass is queued BELOW input, so a
        // burst of keystrokes drains first. This is the assertion that fails if
        // OnSearchTextChanged goes back to calling ApplyFilter directly.
        Assert.Equal(before, h.Vm.FilterPass.PassCount);
        Assert.Equal(9, h.Vm.FilterPass.RequestCount - h.RequestsAfterScan);

        h.Drain();

        Assert.Equal(before + 1, h.Vm.FilterPass.PassCount);
        Assert.All(h.Vm.Songs, song => Assert.Contains(
            "metallica", $"{song.Title} {song.Artist} {song.Album}", StringComparison.CurrentCultureIgnoreCase));
    }

    /// <summary>The browser republishes DIFFERENTIALLY (SyncWith): a narrowing
    /// pass is the same ordering minus the filtered rows, so it lands as per-item
    /// Removes — the realized rows that stay are never re-realized. The fallback
    /// shapes (reorder ⇒ one Reset, big delta ⇒ one Reset, no change ⇒ nothing)
    /// are pinned in BulkObservableCollectionTests and SongLibraryRepublishTests.</summary>
    [Fact]
    public async Task ANarrowingPass_PublishesOnlyTheRowsThatLeft()
    {
        var h = await Harness.Scanned();

        var events = new List<System.Collections.Specialized.NotifyCollectionChangedAction>();
        ((System.Collections.Specialized.INotifyCollectionChanged)h.Vm.Songs).CollectionChanged +=
            (_, e) => events.Add(e.Action);

        h.Vm.SearchText = "metallica";
        h.Drain();

        Assert.Equal(5, h.Vm.Songs.Count);
        Assert.Equal(25, events.Count);
        Assert.All(events,
            e => Assert.Equal(System.Collections.Specialized.NotifyCollectionChangedAction.Remove, e));
    }

    /// <summary>
    ///     THE BEHAVIOUR GUARD. A faster search that changes which songs appear is
    ///     a regression, so the coalesced view model is compared row for row
    ///     against one that filters on every keystroke — across the search box,
    ///     both filter chips, the capo/audio filters, every sort mode and both sort
    ///     directions.
    /// </summary>
    [Fact]
    public async Task TheCoalescedResult_IsIdenticalToFilteringOnEveryKeystroke()
    {
        var eager = await Harness.Scanned(eager: true);
        var lazy = await Harness.Scanned();

        // The sort is set in the OUTER loop and the query in the inner one, so each
        // comparison after the first is driven by the search box ALONE. That
        // matters: a chip/sort change runs a pass immediately, and if the inner
        // step also touched one it would paper over a typed pass that never ran.
        foreach (var sort in new[] { 0, 1, 2, 3, 4, 5, 6 })
        {
            foreach (var descending in new[] { false, true })
            {
                eager.Vm.SortMode = lazy.Vm.SortMode = sort;
                eager.Vm.SortDescending = lazy.Vm.SortDescending = descending;
                lazy.Drain();

                foreach (var query in new[] { "", "m", "me", "metallica", "album 1", "  ", "ZZZ", "rush track" })
                {
                    eager.Vm.SearchText = query;
                    lazy.Vm.SearchText = query;
                    lazy.Drain();

                    AssertSameRows(eager, lazy, $"query='{query}' sort={sort} desc={descending}");
                }
            }
        }

        // …and with each FACET engaged (exclusively, then one multi-select
        // shape per facet), which is the other half of the report.
        foreach (var key in new[] { "tuning", "capo", "path", "year", "difficulty", "length", "status", "origin" })
        {
            var eagerFacet = eager.Vm.Facets.Single(f => f.Key == key);
            var lazyFacet = lazy.Vm.Facets.Single(f => f.Key == key);

            for (var option = 0; option < eagerFacet.Options.Count; option++)
            {
                ExclusiveCheck(eagerFacet, option);
                ExclusiveCheck(lazyFacet, option);
                lazy.Drain();
                AssertSameRows(eager, lazy, $"{key} option {option}");
            }

            if (eagerFacet.Options.Count > 1)
            {
                eagerFacet.Options[0].IsChecked = true; // second value ORs in
                lazyFacet.Options[0].IsChecked = true;
                lazy.Drain();
                AssertSameRows(eager, lazy, $"{key} multi-select");
            }

            eagerFacet.Clear();
            lazyFacet.Clear();
            lazy.Drain();
        }

        foreach (var hideLessons in new[] { false, true })
        {
            eager.Vm.HideLessons = hideLessons;
            lazy.Vm.HideLessons = hideLessons;
            lazy.Drain();
            AssertSameRows(eager, lazy, $"hideLessons {hideLessons}");
        }
    }

    /// <summary>Checks exactly one option of a facet, unchecking the rest.</summary>
    private static void ExclusiveCheck(BrowserFacet facet, int index)
    {
        for (var i = 0; i < facet.Options.Count; i++)
            facet.Options[i].IsChecked = i == index;
    }

    /// <summary>A chip is one gesture, not a burst — deferring it would only add a
    /// frame of nothing, and it must also consume the typed pass queued behind it.</summary>
    [Fact]
    public async Task AFilterChip_AppliesWithoutWaitingForIdle_AndConsumesThePendingTypedPass()
    {
        var h = await Harness.Scanned();
        var before = h.Vm.FilterPass.PassCount;

        h.Vm.SearchText = "metal";                                 // queued
        h.Vm.TuningFacet.Options[0].IsChecked = true;              // discrete

        Assert.Equal(before + 1, h.Vm.FilterPass.PassCount);
        Assert.False(h.Vm.FilterPass.IsPending);
        var rows = h.Vm.Songs.ToList();

        h.Drain(); // the queued callback finds nothing to do
        Assert.Equal(before + 1, h.Vm.FilterPass.PassCount);
        Assert.Equal(rows, h.Vm.Songs);
    }

    /// <summary>The de-duplicated library is MEMOISED, so a rescan has to
    /// invalidate it — a stale memo would leave the old library on screen forever.
    /// (Breaking the InvalidateCollapsedSongs call in the scan tail fails here.)</summary>
    [Fact]
    public async Task ARescan_RecollapsesTheLibrary_RatherThanReusingTheMemo()
    {
        var h = await Harness.Scanned();
        Assert.Equal(Harness.DistinctSongs, h.Vm.Songs.Count);

        h.Library.Result = [Entry("only-one", "Solo Artist", "Only Song")];
        await h.Vm.ScanCommand.ExecuteAsync(null);

        Assert.Single(h.Vm.Songs);
        Assert.Equal("only-one", h.Vm.Songs[0].Song.SongKey);
    }

    /// <summary>Duplicate collapse is unchanged by the memo: the byte-copies the
    /// fixture ships are still folded to one row each.</summary>
    [Fact]
    public async Task ByteCopiesOfTheSamePackage_StillCollapseToOneRow()
    {
        var h = await Harness.Scanned();

        Assert.Equal(Harness.TotalRows, h.Vm.AllSongs.Count);        // the doctor still sees them
        Assert.Equal(Harness.DistinctSongs, h.Vm.Songs.Count);       // the list does not
    }

    /// <summary>
    ///     …and neither does the COUNT readout. It printed "N of M" with M read
    ///     off the uncollapsed master, so an unfiltered library with byte-copies
    ///     announced "30 of 36 songs" — six rows apparently hidden by a filter the
    ///     user never set, and that no filter setting can reveal.
    /// </summary>
    [Fact]
    public async Task TheCountReadout_CountsAgainstWhatTheListCouldShow_NotTheUncollapsedMaster()
    {
        var h = await Harness.Scanned();

        Assert.Contains($"{Harness.DistinctSongs} of {Harness.DistinctSongs}", h.Vm.ResultCountText,
            StringComparison.Ordinal);
        Assert.DoesNotContain(Harness.TotalRows.ToString(CultureInfo.InvariantCulture), h.Vm.ResultCountText,
            StringComparison.Ordinal);
    }

    /// <summary>A real filter still moves only the numerator. (That fixture has no
    /// byte-copies, so its collapsed total IS its master count.)</summary>
    [Fact]
    public async Task TheCountReadouts_DenominatorIsStableAcrossAFilterPass()
    {
        var h = await SearchHarness();

        h.Vm.SearchText = "metallica";
        h.Drain();

        Assert.Contains($"2 of {h.Vm.AllSongs.Count}", h.Vm.ResultCountText, StringComparison.Ordinal);
    }

    /// <summary>
    ///     Every projection a song ROW binds is computed once per song, not once
    ///     per binding read. <c>ArrangementChips</c> is the load-bearing one: the
    ///     row's chips <c>ItemsControl</c> compares <c>ItemsSource</c> by
    ///     reference, so a fresh list per get made it discard and rebuild its
    ///     children every time a recycled row re-bound — and the list re-binds
    ///     every one of its realized rows on every filter pass.
    /// </summary>
    [Fact]
    public void EveryRowProjection_IsComputedOncePerSong_NotPerRead()
    {
        var item = new PsarcSongListItem(Entry("k", "Artist", "Title"), FakeLocalization.English());

        Assert.Same(item.ArrangementChips, item.ArrangementChips);
        Assert.Same(item.ArrangementSummary, item.ArrangementSummary);
        Assert.Same(item.Subtitle, item.Subtitle);
        Assert.Same(item.TuningCellLabel, item.TuningCellLabel);
        Assert.Same(item.TuningName, item.TuningName);
        Assert.Same(item.TuningDisplay, item.TuningDisplay);
        Assert.Same(item.SongLengthFormatted, item.SongLengthFormatted);
        Assert.Same(item.CapoSummary, item.CapoSummary);
        Assert.Same(item.FileName, item.FileName);
        Assert.Same(item.YearLabel, item.YearLabel);
        Assert.Same(item.DifficultyLabel, item.DifficultyLabel);
        Assert.Same(item.SearchHaystack, item.SearchHaystack);
    }

    /// <summary>…and computing them once did not change what they SAY.</summary>
    [Fact]
    public void TheCachedProjections_StillPrintWhatTheEntrySays()
    {
        var entry = Entry("k", "Pink Floyd", "Time", tuning: "D A D G B E", capo: 2);
        var item = new PsarcSongListItem(entry, FakeLocalization.English());

        Assert.Equal(entry.ArrangementSummary, item.ArrangementSummary);
        Assert.Equal(entry.CanLoad, item.CanLoad);
        Assert.Equal(Path.GetFileName(entry.PackagePath), item.FileName);
        Assert.Equal("Pink Floyd | Album 1", item.Subtitle);
        Assert.Equal(FakeLocalization.English().Format("SongBrowser_Facet_CapoAt", 2), item.CapoSummary);
        Assert.True(item.HasCapo);
        Assert.True(item.TuningNeedsAttention);
        Assert.Equal(SongRowFacts.TuningCell(entry.TuningSummary, entry.CapoSummary, FakeLocalization.English()), item.TuningCellLabel);
        Assert.Equal(SongRowFacts.Chips(entry.ArrangementSummary).Count, item.ArrangementChips.Count);
        Assert.False(item.IsLesson);
        Assert.True(new PsarcSongListItem(Entry("GE_Scales", "x", "y"), FakeLocalization.English()).IsLesson);

        // YEAR / DIFF columns: an absent number prints NOTHING, never 0.
        Assert.Equal("1990", item.YearLabel);
        Assert.Equal("", item.DifficultyLabel); // the fixture is never rated
        var rated = new PsarcSongListItem(new LibrarySongEntry
        {
            SongKey = "r", Title = "t", Artist = "a", Difficulty = 6.75, Year = 0
        }, FakeLocalization.English());
        Assert.Equal("6.8", rated.DifficultyLabel);
        Assert.Equal("", rated.YearLabel);
    }

    // ── tokenized multi-word search (the shared Play Mode rule) ────────────────

    /// <summary>
    ///     "metallica one" — the way the song is SHOWN and the way you would type
    ///     it — matched nothing before, because the whole query was tested as one
    ///     contiguous substring of the title, the artist and the album separately.
    ///     Every token now has to appear somewhere in "artist title album", in any
    ///     order, case-insensitively.
    /// </summary>
    [Theory]
    [InlineData("metallica one")]
    [InlineData("one metallica")]
    [InlineData("METALLICA one")]
    [InlineData("  metallica   one  ")]
    [InlineData("metallica - one")]  // the " - " separator is punctuation, not a token
    public async Task AMultiWordQuery_MatchesTokensSpreadAcrossArtistAndTitle(string query)
    {
        var h = await SearchHarness();

        h.Vm.SearchText = query;
        h.Drain();

        Assert.Equal("one", Assert.Single(h.Vm.Songs).Song.SongKey);
    }

    /// <summary>Every token must match — one that matches nothing drops the row,
    /// so the tokenized search is not merely "any word wins".</summary>
    [Fact]
    public async Task ATokenThatMatchesNothing_FiltersTheRowOut()
    {
        var h = await SearchHarness();

        h.Vm.SearchText = "metallica xyzzy";
        h.Drain();

        Assert.Empty(h.Vm.Songs);
    }

    /// <summary>The haystack folds the ALBUM in too, so "artist album-word" and
    /// "title album-word" both narrow.</summary>
    [Theory]
    [InlineData("metallica justice", "one")]
    [InlineData("floyd moon", "time")]
    [InlineData("true metallica", "sad")]   // album == artist name on that row
    public async Task AlbumTokens_MatchAlongsideArtistAndTitle(string query, string expectedKey)
    {
        var h = await SearchHarness();

        h.Vm.SearchText = query;
        h.Drain();

        Assert.Equal(expectedKey, Assert.Single(h.Vm.Songs).Song.SongKey);
    }

    /// <summary>An empty or whitespace-only query is not a filter at all.</summary>
    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    public async Task AnEmptyQuery_StillMatchesEverything(string query)
    {
        var h = await SearchHarness();

        h.Vm.SearchText = query;
        h.Drain();

        Assert.Equal(3, h.Vm.Songs.Count);
    }

    // ── accent folding: one query, the same answer on both surfaces ──────────

    /// <summary>
    ///     Play Mode's Song Library folds accents (<c>LibraryQuery.Fold</c>) and
    ///     this browser did not: it kept a RAW haystack, the unfolded
    ///     <c>Tokenize</c> and a culture-aware <c>Contains</c>, so over ONE library
    ///     "motorhead" found Mötorhead's whole catalogue in one surface and
    ///     nothing at all in the other — the same cross-surface divergence class as
    ///     the charter-in-haystack and difficulty-band ones. The note in
    ///     <c>LibraryQuery.TokenizeFolded</c> called the asymmetry deliberate;
    ///     folding BOTH sides is what reaches an accent, folding one is what
    ///     misses it.
    /// </summary>
    [Theory]
    [InlineData("motorhead", "ace")]
    [InlineData("motörhead", "ace")]
    [InlineData("MOTORHEAD", "ace")]
    [InlineData("mana", "sol")]
    [InlineData("maná", "sol")]
    [InlineData("rayando mana", "sol")]
    public async Task AnUnaccentedQuery_FindsTheAccentedRow(string query, string expectedKey)
    {
        var h = await AccentHarness();

        h.Vm.SearchText = query;
        h.Drain();

        Assert.Equal(expectedKey, Assert.Single(h.Vm.Songs).Song.SongKey);
    }

    /// <summary>Folding widens what matches; it must not make everything match.</summary>
    [Fact]
    public async Task FoldingDoesNotMakeUnrelatedRowsMatch()
    {
        var h = await AccentHarness();

        h.Vm.SearchText = "motorhead metallica";
        h.Drain();

        Assert.Empty(h.Vm.Songs);
    }

    /// <summary>The row's haystack is FOLDED, which is the half that has to move
    /// with the tokenizer — a folded token against a raw haystack matches less
    /// than either did before.</summary>
    [Fact]
    public async Task TheBrowsersHaystack_IsFoldedTheSameWayPlayModesIs()
    {
        var h = await AccentHarness();
        var row = Assert.Single(h.Vm.AllSongs, r => r.Song.SongKey == "ace");

        Assert.Equal(
            LibraryQuery.Fold($"{row.Artist} {row.Title} {row.Album} {row.Song.Charter}"),
            row.SearchHaystack);
    }

    /// <summary>
    ///     The facet counts are computed by a SECOND pass that had its own copy of
    ///     the search leg — folding one copy and not the other makes a popover's
    ///     numbers disagree with the list behind it. One row per tuning here, so
    ///     the counts must sum to exactly what the list shows.
    /// </summary>
    [Fact]
    public async Task FacetCounts_AgreeWithTheFilteredList_ForAnAccentFoldedQuery()
    {
        var h = await AccentHarness();

        h.Vm.SearchText = "motorhead";
        h.Drain();
        h.Vm.RefreshFacetCounts(h.Vm.TuningFacet);

        Assert.Single(h.Vm.Songs);
        Assert.Equal(h.Vm.Songs.Count, h.Vm.TuningFacet.Options.Sum(o => o.Count));
    }

    /// <summary>Three rows, two of them accented, and a distractor that shares no
    /// folded token with either.</summary>
    private static async Task<Harness> AccentHarness()
    {
        var h = await Harness.Scanned();
        h.Library.Result =
        [
            Entry("ace", "Motörhead", "Ace Of Spades", album: "Ace Of Spades"),
            Entry("sol", "Maná", "Rayando El Sol", tuning: "Eb Ab Db Gb Bb Eb", album: "Falta Amor"),
            Entry("one", "Metallica", "One", tuning: "D A D G B E", album: "And Justice For All")
        ];

        await h.Vm.ScanCommand.ExecuteAsync(null);
        h.Drain();
        return h;
    }

    // ── descending sort: one ordering pass, not an ascending pass reversed ─────

    /// <summary>Descending is a mirrored comparer now, not <c>Reverse()</c> over a
    /// buffered ascending list — and for every mode whose keys are distinct it must
    /// still land on exactly the reversed order. (Sort mode 2 is excluded on
    /// purpose: unknown years deliberately do NOT mirror — see
    /// <see cref="UnknownYears_StayLast_InBothSortDirections" />.)</summary>
    [Theory]
    [InlineData(0)] // artist
    [InlineData(1)] // title
    [InlineData(3)] // length
    [InlineData(4)] // file name
    public async Task DescendingSort_IsTheReverseOfAscending_ForEverySortModeWithDistinctKeys(int sortMode)
    {
        var h = await SortHarness();
        h.Vm.SortMode = sortMode;

        h.Vm.SortDescending = false;
        h.Drain();
        var ascending = h.Vm.Songs.Select(s => s.Song.SongKey).ToList();

        h.Vm.SortDescending = true;
        h.Drain();
        var descending = h.Vm.Songs.Select(s => s.Song.SongKey).ToList();

        Assert.Equal(5, ascending.Count);
        Assert.Equal(ascending.Count, ascending.Distinct().Count());  // not a vacuous comparison
        Assert.Equal(Enumerable.Reverse(ascending).ToList(), descending);
    }

    /// <summary>THE ONE DELIBERATE DIFFERENCE from reversing. A song with no year
    /// (0) is unknown data, not "year zero": it sorted last ascending and
    /// <c>Reverse()</c> then hoisted it to the TOP of the "newest first" list. It
    /// stays LAST in both directions now, and the known years still mirror.</summary>
    [Fact]
    public async Task UnknownYears_StayLast_InBothSortDirections()
    {
        var h = await SortHarness();
        h.Vm.SortMode = 2;

        h.Vm.SortDescending = false;
        h.Drain();
        Assert.Equal(new[] { 1995, 2001, 2010, 0, 0 }, h.Vm.Songs.Select(s => s.Song.Year));

        h.Vm.SortDescending = true;
        h.Drain();
        Assert.Equal(new[] { 2010, 2001, 1995, 0, 0 }, h.Vm.Songs.Select(s => s.Song.Year));
    }

    /// <summary>#651: the LENGTH sort keeps an unknown length (0) LAST in both
    /// directions, through the one <c>LibrarySort</c> Song Select uses. It had no
    /// unknown leg here, so "shortest first" opened on every song whose manifest
    /// carried no length.</summary>
    [Fact]
    public async Task UnknownLengths_StayLast_InBothSortDirections()
    {
        var h = await Harness.Scanned();
        h.Library.Result =
        [
            SortEntry("k1", "Alpha", "One", 1990, 0, @"C:\lib\a_p.psarc"),
            SortEntry("k2", "Beta", "Two", 1990, 300, @"C:\lib\b_p.psarc"),
            SortEntry("k3", "Gamma", "Three", 1990, 120, @"C:\lib\c_p.psarc")
        ];
        await h.Vm.ScanCommand.ExecuteAsync(null);
        h.Drain();

        h.Vm.SortByColumnCommand.Execute("time");                         // ascending
        Assert.Equal(new[] { 120d, 300d, 0d }, h.Vm.Songs.Select(s => s.Song.SongLength));

        h.Vm.SortByColumnCommand.Execute("time");                         // …and flipped
        Assert.Equal(new[] { 300d, 120d, 0d }, h.Vm.Songs.Select(s => s.Song.SongLength));
    }

    // ── datagrid header sort ──────────────────────────────────────────────────

    /// <summary>Clicking a caption sorts by its column ascending in ONE pass;
    /// clicking the SAME caption again flips the direction in one pass. The sort
    /// combo and the captions are one state.</summary>
    [Fact]
    public async Task ClickingAColumnCaption_SortsByIt_AndASecondClickFlipsTheDirection()
    {
        var h = await Harness.Scanned();
        var before = h.Vm.FilterPass.PassCount;

        h.Vm.SortByColumnCommand.Execute("year");

        Assert.Equal(2, h.Vm.SortMode);
        Assert.False(h.Vm.SortDescending);
        Assert.Equal(before + 1, h.Vm.FilterPass.PassCount);
        Assert.Equal("▲", h.Vm.YearSortGlyph);
        Assert.Equal("", h.Vm.SongSortGlyph);

        h.Vm.SortByColumnCommand.Execute("year");

        Assert.Equal(2, h.Vm.SortMode);
        Assert.True(h.Vm.SortDescending);
        Assert.Equal(before + 2, h.Vm.FilterPass.PassCount);
        Assert.Equal("▼", h.Vm.YearSortGlyph);

        // A DIFFERENT caption starts over ascending.
        h.Vm.SortByColumnCommand.Execute("difficulty");
        Assert.Equal(5, h.Vm.SortMode);
        Assert.False(h.Vm.SortDescending);
        Assert.Equal("▲", h.Vm.DifficultySortGlyph);
        Assert.Equal("", h.Vm.YearSortGlyph);
        Assert.Equal(before + 3, h.Vm.FilterPass.PassCount);
    }

    /// <summary>The difficulty sort keeps UNRATED (0) songs LAST in both
    /// directions — the year rule's twin: a missing rating must not hijack an
    /// "easiest first" list. The fixture's songs are all unrated, so the rated
    /// ones are pushed in by hand.</summary>
    [Fact]
    public async Task TheDifficultySort_KeepsUnratedSongsLast_InBothDirections()
    {
        var h = await Harness.Scanned();
        h.Vm.SortByColumnCommand.Execute("difficulty");

        // All fixture songs are unrated: the list must still be full and stable.
        Assert.Equal(Harness.DistinctSongs, h.Vm.Songs.Count);

        h.Vm.SortByColumnCommand.Execute("difficulty"); // descending
        Assert.Equal(Harness.DistinctSongs, h.Vm.Songs.Count);
        Assert.All(h.Vm.Songs, s => Assert.Equal("", s.DifficultyLabel));
    }

    /// <summary>The tuning sort groups equal tuning CELLS together, the way the
    /// column prints them.</summary>
    [Fact]
    public async Task TheTuningSort_GroupsEqualTuningCells()
    {
        var h = await Harness.Scanned();
        h.Vm.SortByColumnCommand.Execute("tuning");

        var cells = h.Vm.Songs.Select(s => s.TuningCellLabel).ToList();
        Assert.Equal(cells.OrderBy(c => c, StringComparer.CurrentCultureIgnoreCase), cells);
    }

    /// <summary>
    ///     …and a song with neither a tuning nor a capo prints an EMPTY cell, which
    ///     sorts FIRST ascending. Mode 6 had no unknown leg at all — in either
    ///     direction — while <c>song-browser.md</c> claimed it followed the rule,
    ///     so an A→Z tuning sort opened on a block of blank cells. The guard above
    ///     could not see it: on a fixture where every song has a tuning, ordered
    ///     IS ordered.
    /// </summary>
    [Fact]
    public async Task TheTuningSort_KeepsUnknownTuningsLast_InBothDirections()
    {
        var h = await Harness.Scanned();
        h.Library.Result =
        [
            Entry("known-e", "Artist", "E song", "E A D G B E"),
            Entry("unknown", "Artist", "No tuning", ""),
            Entry("known-d", "Artist", "D song", "D A D G B E")
        ];
        await h.Vm.ScanCommand.ExecuteAsync(null);
        h.Drain();

        h.Vm.SortByColumnCommand.Execute("tuning");                       // ascending
        Assert.Equal("", h.Vm.Songs[^1].TuningCellLabel);

        h.Vm.SortByColumnCommand.Execute("tuning");                       // …and flipped
        Assert.Equal("", h.Vm.Songs[^1].TuningCellLabel);
    }

    /// <summary>
    ///     The CHARTER is part of the search haystack on BOTH song surfaces —
    ///     a real library holds several charters' cuts of one song. It was added to
    ///     Play Mode's <c>PlaySongItem</c> only, and since both predicates require
    ///     EVERY token to hit the haystack, typing a charter's name found their
    ///     whole catalogue in Play Mode's Song Library and NOTHING here, over one
    ///     library.
    /// </summary>
    [Fact]
    public async Task TheBrowsersSearch_MatchesTheCHARTER_LikePlayModesDoes()
    {
        var h = await Harness.Scanned();
        h.Library.Result =
        [
            Charted("a", "One", "Firekorn"),
            Charted("b", "Battery", "Someone Else")
        ];
        await h.Vm.ScanCommand.ExecuteAsync(null);
        h.Drain();

        h.Vm.SearchText = "firekorn";
        h.Vm.FilterPass.RunImmediately();

        Assert.Equal(["One"], h.Vm.Songs.Select(s => s.Title));
    }

    // ── source lints: the wiring, which no behaviour test can see ──────────────

    /// <summary>Every sortable caption is a Button on `SortByColumnCommand`; a
    /// caption that silently reverts to a plain TextBlock is a sort the UI can
    /// no longer reach (the combo still could — which is exactly why nothing
    /// else would notice).</summary>
    [Fact]
    public void TheSortableCaptions_AreWiredToTheHeaderSortCommand()
    {
        // The Library's header is data: each column carries its sort key, and its command is the browser's.
        var screen = UiSourceTree.ReadAllText(Path.Combine(UiDirectory(), "ViewModels", "Panels", "LibraryScreen.cs"));
        foreach (var column in new[] { "song", "tuning", "year", "difficulty", "time" })
            Assert.Contains($"\"{column}\"", screen, StringComparison.Ordinal);

        var parts = UiSourceTree.ReadAllText(Path.Combine(UiDirectory(), "ViewModels", "Panels", "LibraryScreenParts.cs"));
        Assert.Contains("SortByColumnCommand.Execute(sortKey)", parts, StringComparison.Ordinal);

        var markup = UiSourceTree.ReadAllText(Path.Combine(UiDirectory(), "Views", "Library", "LibraryView.axaml"));
        Assert.Contains("Command=\"{Binding SortCommand}\"", markup, StringComparison.Ordinal);
    }

    /// <summary>ONE tokenizer and ONE search leg for both song lists: the engine's.
    /// A second, hand-rolled split is how the two surfaces drifted apart in the
    /// first place; a second copy of the leg is how a popover's facet counts drift
    /// away from the list they count. Both compile, so this reads the sources —
    /// the engine and both view models, through <see cref="SongQuerySources" />.</summary>
    [Fact]
    public void BothSongLists_SearchThroughTheOneEngineLeg_RatherThanTheirOwnSplit()
    {
        var query = SongQuerySources.Engine("LibraryQuery.cs");
        var leg = MethodBody(query, "public static bool Matches");

        // FOLDED tokens against a folded haystack, compared ordinally.
        Assert.Contains("SearchHaystack", leg, StringComparison.Ordinal);
        Assert.Contains("StringComparison.Ordinal", leg, StringComparison.Ordinal);
        Assert.DoesNotContain(".Split(", leg, StringComparison.Ordinal);

        // Exactly ONE place reads a row's haystack, across the engine and both view
        // models: the counting pass goes through Matches instead of a copy of it.
        Assert.Equal(1, SongQuerySources.All().Sum(f => f.Source.Split(".SearchHaystack").Length - 1));
        var count = MethodBody(query, "public static Dictionary<string, int> CountFacet");
        Assert.Contains("Matches(", count, StringComparison.Ordinal);
        Assert.DoesNotContain("CurrentCultureIgnoreCase", count, StringComparison.Ordinal);

        // Each view model tokenizes through the engine and filters through it.
        var songSelect = SongQuerySources.SongSelectViewModel().Single(f => f.File == "SongSelectViewModel.cs").Source;
        foreach (var (name, source) in new[] { ("SongBrowserViewModel", SongQuerySources.LibraryViewModel()), ("SongSelect", songSelect) })
        {
            Assert.True(source.Contains("LibraryQuery.TokenizeFolded(", StringComparison.Ordinal), $"{name}: tokenizer");
            Assert.True(source.Contains("LibraryQuery.Matches(", StringComparison.Ordinal), $"{name}: predicate");
            Assert.False(source.Contains(".Split(", StringComparison.Ordinal), $"{name}: a split of its own");
        }
    }

    /// <summary>ONE ordering for both song lists. A view model that grows its own
    /// <c>ThenBy</c> chain is the twin #212 and #231 had to fix twice.</summary>
    [Fact]
    public void BothSongLists_OrderThroughTheOneEngineSort()
    {
        foreach (var (file, source) in SongQuerySources.ViewModels())
            Assert.False(source.Contains("ThenBy", StringComparison.Ordinal), $"{file}: a sort chain of its own");

        // The two files that held the twin orderings compare no names of their own either.
        var songSelect = SongQuerySources.SongSelectViewModel().Single(f => f.File == "SongSelectViewModel.cs").Source;
        Assert.DoesNotContain("CurrentCultureIgnoreCase", SongQuerySources.LibraryViewModel(), StringComparison.Ordinal);
        Assert.DoesNotContain("CurrentCultureIgnoreCase", songSelect, StringComparison.Ordinal);

        Assert.Contains("LibrarySort.Library.Order(", SongQuerySources.LibraryViewModel(), StringComparison.Ordinal);
        Assert.Contains(SongQuerySources.SongSelectViewModel(),
            f => f.Source.Contains("LibrarySort.SongSelect.Order(", StringComparison.Ordinal));
    }

    /// <summary>The body of the method whose signature starts with
    /// <paramref name="signature" />, up to its closing brace.</summary>
    private static string MethodBody(string source, string signature)
    {
        var start = source.IndexOf(signature, StringComparison.Ordinal);
        Assert.True(start > 0, $"'{signature}' moved — repoint this lint at its new home.");
        return source[start..source.IndexOf("\n    }", start, StringComparison.Ordinal)];
    }

    /// <summary>Both surfaces route TYPING through the coalescing pass. A direct
    /// <c>ApplyFilter()</c> in a search hook is the regression, and it compiles.</summary>
    [Fact]
    public void BothSearchBoxes_RequestACoalescedPass_RatherThanFilteringInTheSetter()
    {
        foreach (var (file, source) in SongQuerySources.ViewModels())
        {
            var hook = source.IndexOf("partial void OnSearchTextChanged", StringComparison.Ordinal);
            if (hook < 0) continue;

            var body = source[hook..source.IndexOf('}', hook)];
            Assert.True(body.Contains("FilterPass.Request()", StringComparison.Ordinal),
                $"{file}: OnSearchTextChanged must request a coalesced pass, not filter in the setter:\n{body}");
        }
    }

    /// <summary>The de-dupe is a pure function of the master list; <c>ApplyFilter</c>
    /// must read the memo, never rebuild it.</summary>
    [Fact]
    public void ApplyFilter_ReadsTheMemoisedCollapse_RatherThanRecollapsingPerPass()
    {
        var source = SongQuerySources.LibraryViewModel();
        var start = source.IndexOf("private void ApplyFilter()", StringComparison.Ordinal);
        Assert.True(start > 0, "ApplyFilter moved — repoint this lint at its new file.");

        var body = source[start..source.IndexOf("\n    }", start, StringComparison.Ordinal)];
        Assert.DoesNotContain("CollapseByIdentity", body, StringComparison.Ordinal);
        Assert.Contains("CollapsedSongs", body, StringComparison.Ordinal);
    }

    /// <summary>The list stays VIRTUALIZED. A real library is thousands of rows, and
    /// an <c>ItemsPanel</c> override on this ListBox would realize every one of them
    /// on every filter pass. (The behavioural half — that only a handful of
    /// containers exist after a layout — is
    /// <c>Perf/ShellConstructionUiPerfSmokeTests.Library_songRows_lineUpWithTheirCaptions_andStayVirtualized</c>,
    /// which needs the Avalonia session and so cannot live in the default gate.)</summary>
    [Fact]
    public void TheSongList_DeclaresNoItemsPanelOverride()
    {
        var markup = UiSourceTree.ReadAllText(Path.Combine(
            UiDirectory(), "Views", "Library", "LibraryView.axaml"));

        var list = System.Xml.Linq.XDocument.Parse(markup).Descendants()
            .SingleOrDefault(e => e.Attributes().Any(a => a.Name.LocalName == "Name" && a.Value == "LibraryRows"));
        Assert.True(list is not null, "the song list was renamed — repoint this lint.");

        Assert.DoesNotContain(list!.Elements(), e => e.Name.LocalName == "ListBox.ItemsPanel");
        Assert.DoesNotContain(list.Attributes(), a => a.Name.LocalName == "ItemsPanel");
    }

    // ── fixture ───────────────────────────────────────────────────────────────

    /// <summary>Three real-shaped rows whose artist / title / album words do not
    /// overlap, so a query naming any two of the three narrows to exactly one.</summary>
    private static async Task<Harness> SearchHarness()
    {
        var h = await Harness.Scanned();
        h.Library.Result =
        [
            Entry("one", "Metallica", "One", album: "And Justice For All"),
            Entry("sad", "Metallica", "Sad But True", album: "Metallica"),
            Entry("time", "Pink Floyd", "Time", album: "The Dark Side Of The Moon")
        ];

        await h.Vm.ScanCommand.ExecuteAsync(null);
        h.Drain();
        return h;
    }

    /// <summary>Five rows, DISTINCT on every sort key (artist, title, length, file
    /// name) so "descending == reversed ascending" is a real comparison and not a
    /// statement about tie order. Two carry no year — the unknown-data case.</summary>
    private static async Task<Harness> SortHarness()
    {
        var h = await Harness.Scanned();
        h.Library.Result =
        [
            SortEntry("s1", "Alpha", "Zulu", 2001, 300, @"C:\lib\c_p.psarc"),
            SortEntry("s2", "Beta", "Yankee", 1995, 100, @"C:\lib\a_p.psarc"),
            SortEntry("s3", "Charlie", "Xray", 0, 250, @"C:\lib\e_p.psarc"),
            SortEntry("s4", "Delta", "Whiskey", 2010, 200, @"C:\lib\b_p.psarc"),
            SortEntry("s5", "Echo", "Victor", 0, 150, @"C:\lib\d_p.psarc")
        ];

        await h.Vm.ScanCommand.ExecuteAsync(null);
        h.Drain();
        return h;
    }

    private static LibrarySongEntry SortEntry(
        string key, string artist, string title, int year, double length, string path) => new()
        {
            SongKey = key,
            Title = title,
            Artist = artist,
            Album = "Album",
            Year = year,
            SongLength = length,
            TuningSummary = "E A D G B E",
            PackagePath = path,
            Arrangements = [new LibraryArrangementEntry { ArrangementName = "Lead", SngPath = $"s/{key}_lead.sng" }]
        };

    private static void AssertSameRows(Harness eager, Harness lazy, string what)
    {
        Assert.False(lazy.Vm.FilterPass.IsPending, $"{what}: a pass was still queued");
        Assert.Equal(eager.Vm.Songs.Count, lazy.Vm.Songs.Count);
        Assert.Equal(eager.Vm.ResultCountText, lazy.Vm.ResultCountText);

        for (var i = 0; i < eager.Vm.Songs.Count; i++)
        {
            var a = eager.Vm.Songs[i];
            var b = lazy.Vm.Songs[i];
            Assert.True(a.Song.SongKey == b.Song.SongKey && a.Song.PackagePath == b.Song.PackagePath,
                $"{what}: row {i} is '{a.Song.SongKey}' eagerly and '{b.Song.SongKey}' coalesced");
            Assert.Equal(a.GroupHeader, b.GroupHeader);
        }
    }

    private static LibrarySongEntry Entry(
        string key, string artist, string title, string tuning = "E A D G B E", int capo = 0,
        string? path = null, string album = "Album 1") => new()
        {
            SongKey = key,
            Title = title,
            Artist = artist,
            Album = album,
            Year = 1990,
            SongLength = 200,
            TuningSummary = tuning,
            CapoSummary = capo,
            PackagePath = path ?? $@"C:\lib\{key}_p.psarc",
            Arrangements =
        [
            new LibraryArrangementEntry { ArrangementName = "Lead", SngPath = $"s/{key}_lead.sng" },
            new LibraryArrangementEntry { ArrangementName = "Rhythm", SngPath = $"s/{key}_rhythm.sng" }
        ]
        };

    /// <summary>One entry with a CHARTER — the field <see cref="Entry" /> leaves
    /// blank, and the one the browser's haystack used to omit.</summary>
    private static LibrarySongEntry Charted(string key, string title, string charter) => new()
    {
        SongKey = key,
        Title = title,
        Artist = "Metallica",
        Album = "Album 1",
        Charter = charter,
        Year = 1990,
        SongLength = 200,
        TuningSummary = "E A D G B E",
        PackagePath = $@"C:\lib\{key}_p.psarc",
        Arrangements = [new LibraryArrangementEntry { ArrangementName = "Lead", SngPath = $"s/{key}.sng" }]
    };

    private static string UiDirectory()
    {
        var dir = AppContext.BaseDirectory;
        while (dir != null && !Directory.Exists(Path.Combine(dir, "DotCharter.UI")))
            dir = Path.GetDirectoryName(dir);

        Assert.NotNull(dir);
        return Path.Combine(dir!, "DotCharter.UI");
    }

    private sealed class Harness
    {
        /// <summary>Six artists × five songs, plus a byte-copy of every fifth.</summary>
        public const int DistinctSongs = 30;

        public const int TotalRows = DistinctSongs + 6;

        private readonly List<Action> _queue = [];

        private Harness(bool eager)
        {
            var logger = new FakeAppLogger();
            var state = new ProjectState();
            var config = new FakeConfigurationService();
            var transport = new TransportViewModel(new FakeAudioPlaybackService(), state, config, FakeLocalization.KeysOnly());
            var selection = new SelectionService();
            var editor = new ChartEditorViewModel(
                state, transport, new HistoryService(logger), selection,
                new TempoService(logger), new TrackContextRegistry(state, selection), FakeLocalization.KeysOnly());

            Vm = new SongBrowserViewModel(
                new FakeFileStorageService(), config, Library, logger,
                new StubProjectSession(), state, editor, transport,
                new DotCharter.Presentation.Dialogs.DialogsSlot(), FakeLocalization.English(),
                eager ? action => action() : _queue.Add);
        }

        public StubLibraryService Library { get; } = new() { Result = BuildLibrary() };
        public SongBrowserViewModel Vm { get; }

        /// <summary>Requests the scan itself made, so a test can count only its own.</summary>
        public int RequestsAfterScan { get; private set; }

        public static async Task<Harness> Scanned(bool eager = false)
        {
            var h = new Harness(eager);
            h.Vm.PsarcLocation = @"C:\lib";
            await h.Vm.ScanCommand.ExecuteAsync(null);
            h.Drain();
            h.RequestsAfterScan = h.Vm.FilterPass.RequestCount;
            return h;
        }

        /// <summary>Runs whatever the view model posted, the way the dispatcher would
        /// once the UI goes idle.</summary>
        public void Drain()
        {
            while (_queue.Count > 0)
            {
                var jobs = _queue.ToList();
                _queue.Clear();
                foreach (var job in jobs) job();
            }
        }

        private static List<LibrarySongEntry> BuildLibrary()
        {
            string[] artists = ["Metallica", "Pink Floyd", "Rush", "Tool", "Opeth", "Muse"];
            string[] tunings = ["E A D G B E", "Eb Ab Db Gb Bb Eb", "D A D G B E"];

            var songs = new List<LibrarySongEntry>();
            for (var i = 0; i < DistinctSongs; i++)
            {
                songs.Add(Entry(
                    $"song{i:D3}", artists[i % artists.Length], $"Track {i}",
                    tunings[i % tunings.Length], i % 7 == 0 ? 2 : 0,
                    album: $"Album {i % 4}"));
            }

            // Byte-copies under a second file name — the rows LibraryDedupe folds.
            foreach (var s in songs.Where((_, i) => i % 5 == 0).ToList())
            {
                songs.Add(Entry(s.SongKey, s.Artist, s.Title, s.TuningSummary, s.CapoSummary,
                    $@"C:\lib\{s.SongKey}_copy_p.psarc", s.Album));
            }

            return songs;
        }
    }

    private sealed class StubLibraryService : ILibraryService
    {
        public IReadOnlyList<LibrarySongEntry> Result { get; set; } = [];

        public Task<IReadOnlyList<LibrarySongEntry>> ScanAsync(string rootPath) => Task.FromResult(Result);

        public Task<IReadOnlyList<LibrarySongEntry>> ScanAsync(
            string rootPath, IProgress<LibraryScanProgress>? progress, CancellationToken cancellationToken)
            => Task.FromResult(Result);

        public Task<IReadOnlyList<LibrarySongEntry>> ScanAsync(
            string rootPath, IProgress<LibraryScanProgress>? progress,
            IProgress<IReadOnlyList<LibrarySongEntry>>? songFound, CancellationToken cancellationToken)
            => Task.FromResult(Result);

        public Task<LibraryLoadedSong> LoadSongAsync(LibrarySongEntry song) => throw new NotSupportedException();
        public Task<string> LoadCoverImageAsync(LibrarySongEntry song) => Task.FromResult("");
    }

    private sealed class StubProjectSession : IProjectSessionService
    {
        public AppConfig AppConfig { get; } = new();
        public void Initialize() { }
        public void LoadAppConfig() { }
        public void SaveAppConfig() { }
        public void FreeAudio() { }
        public Task LoadFile(string filePath) => Task.CompletedTask;

        public Task LoadAudioFile(string filePath, bool generateBeats = true, bool preferMemoryPlayback = false)
            => Task.CompletedTask;

        public Task<bool> AttachAudioAsync(string sourcePath) => Task.FromResult(true);
        public Task LoadSilentTransportAsync(double projectLengthMs) => Task.CompletedTask;
        public Task OpenProject() => Task.CompletedTask;
        public Task<bool> SaveProjectAsync() => Task.FromResult(true);
        public Task<bool> SaveProjectAsAsync() => Task.FromResult(true);
        public IReadOnlyList<RecentProjectEntry> GetRecentProjects() => [];
        public event Action? RecentProjectsChanged { add { } remove { } }
        public bool IsProjectDirty => false;
        public Func<Task<bool>>? ConfirmDiscardAsync { get; set; }
        public Task<bool> MayReplaceOpenProjectAsync() => Task.FromResult(true);
        public void MarkProjectClean() { }
        public void MarkProjectDirty() { }
        public event Action? ProjectDirtyChanged { add { } remove { } }
        public void Dispose() { }
    }
}
