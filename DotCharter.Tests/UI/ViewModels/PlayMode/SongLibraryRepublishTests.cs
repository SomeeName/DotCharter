using System.Collections.Specialized;
using DotCharter.Services.Audio.LivePlay;
using DotCharter.Services.Gameplay.Leaderboards;
using DotCharter.Services.Gameplay.Profiles;
using DotCharter.Services.Gameplay.Stats;
using DotCharter.Services.Library;
using DotCharter.Tone.Infrastructure.Storage;
using DotCharter.UI.ViewModels.Panels;
using DotCharter.UI.ViewModels.PlayMode;
using DotCharter.Presentation.Shared.Library;

namespace DotCharter.Tests.UI.ViewModels.PlayMode;

/// <summary>
///     Play Mode's Song Library republishes its rows on every filter pass. It used
///     to do it as <c>Clear()</c> + N <c>Add()</c> — 2,160 <c>CollectionChanged</c>
///     events on the owner's library — and, having no no-change early-out, it paid
///     for that even when the pass decided nothing had moved. Measured with the
///     real row template attached (Release,
///     <c>Perf/SongLibrarySearchProfileUiPerfSmokeTests</c>): 117–129 ms per
///     republish regardless of how many rows changed.
///     <para>
///         Structural, not timed: ONE notification per real change, ZERO for a
///         pass that changed nothing.
///     </para>
///     <para>
///         SINCE THE UNIFIED BROWSER (2026-08-15) the two song lists share the
///         row facts AND the filter MODEL (<c>BrowserFacet</c> selection sets
///         feeding ONE pure predicate, <c>LibraryQuery.Matches</c>, over the
///         <c>ILibrarySong</c> both row types implement).
///     </para>
/// </summary>
public sealed class SongLibraryRepublishTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "DotCharterTests", Guid.NewGuid().ToString("N"));

    public SongLibraryRepublishTests() => Directory.CreateDirectory(_dir);

    public void Dispose() => BestEffortDelete.Directory(_dir);

    [Fact]
    public void TheSongList_IsABulkCollection_SoARepublishIsOneNotification()
    {
        var vm = NewVm();
        Assert.IsType<BulkObservableCollection<PlaySongItem>>(vm.SongSelect.SongList);

        var events = new List<NotifyCollectionChangedAction>();
        ((INotifyCollectionChanged)vm.SongSelect.SongList).CollectionChanged += (_, e) => events.Add(e.Action);

        vm.SongSelect.SetLibraryForTest(Rows(40));

        Assert.Equal(40, vm.SongSelect.SongList.Count);
        Assert.Equal([NotifyCollectionChangedAction.Reset], events);
    }

    /// <summary>The half that costs the most on a real library: the SECOND pass over
    /// an unchanged result set must publish nothing at all. That is the shape the
    /// tail of every typed query has — "metalli", "metallic", "metallica" all match
    /// the same rows.</summary>
    [Fact]
    public void APassThatChangesNothing_RaisesNoNotificationAtAll()
    {
        var vm = NewVm();
        var rows = Rows(40);
        vm.SongSelect.SetLibraryForTest(rows);

        var events = 0;
        ((INotifyCollectionChanged)vm.SongSelect.SongList).CollectionChanged += (_, _) => events++;

        vm.SongSelect.SetLibraryForTest(rows);   // same songs, same order
        vm.SongSelect.SearchText = "song";       // matches every one of them
        vm.SongSelect.FilterPass.RunImmediately();

        Assert.Equal(40, vm.SongSelect.SongList.Count);
        Assert.Equal(0, events);
    }

    /// <summary>…and a pass that DOES change the rows still publishes them — as a
    /// DIFFERENTIAL since the SyncWith republish: a narrowed result is the same
    /// ordering minus the filtered rows, so it lands as per-item Removes (the
    /// realized rows that stay are never re-realized) and never as a Reset.</summary>
    [Fact]
    public void APassThatNarrowsTheList_PublishesOnlyTheRowsThatLeft()
    {
        var vm = NewVm();
        vm.SongSelect.SetLibraryForTest(Rows(40));

        var events = new List<NotifyCollectionChangedAction>();
        ((INotifyCollectionChanged)vm.SongSelect.SongList).CollectionChanged += (_, e) => events.Add(e.Action);

        // Tokenized, so this keeps Song 7 / 17 / 27 / 37 — four of the forty.
        vm.SongSelect.SearchText = "Song 7";
        vm.SongSelect.FilterPass.RunImmediately();

        Assert.Equal(4, vm.SongSelect.SongList.Count);
        Assert.Equal(36, events.Count);
        Assert.All(events, e => Assert.Equal(NotifyCollectionChangedAction.Remove, e));
    }

    /// <summary>A REORDER is not an edit script — flipping the sort direction must
    /// still land as the one Reset.</summary>
    [Fact]
    public void FlippingTheSortDirection_StillPublishesOneReset()
    {
        var vm = NewVm();
        vm.SongSelect.SetLibraryForTest(Rows(40));

        var events = new List<NotifyCollectionChangedAction>();
        ((INotifyCollectionChanged)vm.SongSelect.SongList).CollectionChanged += (_, e) => events.Add(e.Action);

        vm.SongSelect.SortDescending = true;

        Assert.Equal(40, vm.SongSelect.SongList.Count);
        Assert.Equal([NotifyCollectionChangedAction.Reset], events);
    }

    /// <summary>Typing coalesces here too — nine keystrokes, one pass.</summary>
    [Fact]
    public void NineKeystrokes_RequestNinePassesButRunNone_UntilTheUiGoesIdle()
    {
        var vm = NewVm();
        vm.SongSelect.SetLibraryForTest(Rows(40));

        var before = vm.SongSelect.FilterPass.PassCount;
        foreach (var _ in "metallica") vm.SongSelect.SearchText += "x";

        Assert.Equal(before, vm.SongSelect.FilterPass.PassCount);
        Assert.True(vm.SongSelect.FilterPass.IsPending);
    }

    // ── the tokenizer hoist ───────────────────────────────────────────────────

    /// <summary>Tokenizing moved OUT of the per-song predicate (it allocated a
    /// <c>string[]</c> and a fresh combined haystack for every row, on every
    /// keystroke). Both call shapes must agree on every row, or search results
    /// changed.</summary>
    [Theory]
    [InlineData("")]
    [InlineData("song")]
    [InlineData("artist 1 - Song 3")]
    [InlineData("  -  ")]
    [InlineData("SONG 12")]
    [InlineData("nothing matches this")]
    public void TheHoistedTokenizer_AgreesWithTokenizingInsideThePredicate(string search)
    {
        var criteria = new LibraryFilter(search);
        // FOLDED tokens: the haystack is folded, so this is the hoist the
        // predicate's own fallback performs.
        var tokens = LibraryQuery.TokenizeFolded(search);

        foreach (var song in Rows(40))
        {
            Assert.Equal(
                LibraryQuery.Matches(song, criteria),
                LibraryQuery.Matches(song, criteria, tokens));
        }
    }

    [Fact]
    public void Tokenize_DropsLonePunctuation_AndEmptyRuns()
    {
        Assert.Empty(LibraryQuery.Tokenize(""));
        Assert.Empty(LibraryQuery.Tokenize("   "));
        Assert.Equal(["artist", "song"], LibraryQuery.Tokenize("artist - song"));
        Assert.Equal(["a1", "b2"], LibraryQuery.Tokenize("  a1   b2 "));
        Assert.Equal(["--"], LibraryQuery.Tokenize("--")); // two chars: still a token
    }

    /// <summary>The cached haystack reads "artist title album charter" in that
    /// order — the first three are what makes "artist songname" (and
    /// "artist - songname") match, and the CHARTER was appended 2026-08-15 because a
    /// library holds several charters' cuts of one song and their names were
    /// searchable nowhere.</summary>
    [Fact]
    public void TheCachedHaystack_ReadsArtistThenTitleThenAlbumThenCharter()
    {
        var song = Rows(1)[0];

        Assert.True(Match(song, "Artist 0 Song 0 Album 0 Charter 0"));
        Assert.True(Match(song, "Song 0 Artist 0"));   // tokens, any order
        Assert.True(Match(song, "Artist 0 - Song 0")); // the lone dash is dropped
        Assert.True(Match(song, "Charter 0"));         // the charter alone
        Assert.False(Match(song, "Artist 1"));

        static bool Match(PlaySongItem song, string search) => LibraryQuery.Matches(
            song, new LibraryFilter(search));
    }

    // ── the genre facet is derived from the library ───────────────────────────

    /// <summary>The genre facet's options are DERIVED from what the scan found —
    /// one option per authored genre (deduped on the CASEFOLDED key, labelled
    /// with the first authored casing), sorted, plus the explicit unknown
    /// bucket — so the facet can never offer a genre no song has. Selecting one
    /// narrows through the same pure predicate everything else uses, and rows
    /// with NO genre are the "" bucket the user can include, not a silent
    /// pass.</summary>
    [Fact]
    public void TheGenreFacetOptions_AreTheGenresTheLibraryActuallyCarries()
    {
        var vm = NewVm();
        var metal = OwnChart(@"C:\charts\Ride.dcp", "Metal");
        var jazz = OwnChart(@"C:\charts\Blue.dcp", "Jazz");
        var alsoMetal = OwnChart(@"C:\charts\Master.dcp", "metal");
        var noGenre = Rows(1)[0];

        vm.SongSelect.SetLibraryForTest([metal, jazz, alsoMetal, noGenre]);

        // Jazz + Metal + the unknown bucket — ONE option per genre no matter
        // how many songs (or casings) carry it.
        Assert.Equal(3, vm.SongSelect.GenreFacet.Options.Count);
        Assert.Equal("jazz", vm.SongSelect.GenreFacet.Options[0].Value);
        Assert.Equal("Jazz", vm.SongSelect.GenreFacet.Options[0].Label);
        Assert.Equal("metal", vm.SongSelect.GenreFacet.Options[1].Value);
        Assert.Equal("Metal", vm.SongSelect.GenreFacet.Options[1].Label); // first authored casing
        Assert.Equal("", vm.SongSelect.GenreFacet.Options[2].Value);
        Assert.False(vm.SongSelect.GenreFacet.IsActive);
        Assert.Equal(4, vm.SongSelect.SongList.Count);

        vm.SongSelect.GenreFacet.Options[1].IsChecked = true; // Metal — BOTH casings
        Assert.Equal(2, vm.SongSelect.SongList.Count);
        Assert.DoesNotContain(vm.SongSelect.SongList, s => s.Genre == "Jazz");
        Assert.DoesNotContain(vm.SongSelect.SongList, s => s.Genre == null); // unknown is its own bucket now

        vm.SongSelect.GenreFacet.Options[2].IsChecked = true; // …which the user can add
        Assert.Equal(3, vm.SongSelect.SongList.Count);
        Assert.Contains(vm.SongSelect.SongList, s => s.Genre == null);
    }

    /// <summary>A rescan that still carries the selected genre keeps the
    /// selection (restore-by-VALUE through <c>ResetOptions</c>); one that no
    /// longer has it drops it instead of leaving a stale key narrowing the
    /// list against nothing.</summary>
    [Fact]
    public void TheGenrePick_SurvivesARescan_AndFallsBackWhenItsGenreIsGone()
    {
        var vm = NewVm();
        vm.SongSelect.SetLibraryForTest([OwnChart(@"C:\charts\a.dcp", "Jazz"), OwnChart(@"C:\charts\b.dcp", "Metal")]);
        vm.SongSelect.GenreFacet.Options.Single(o => o.Value == "metal").IsChecked = true;
        Assert.Single(vm.SongSelect.SongList);

        // Rescan that still has Metal: the selection survives the rebuild.
        vm.SongSelect.SetLibraryForTest([
            OwnChart(@"C:\charts\a.dcp", "Blues"), OwnChart(@"C:\charts\b.dcp", "Metal")
        ]);
        Assert.True(vm.SongSelect.GenreFacet.IsActive);
        Assert.True(vm.SongSelect.GenreFacet.Options.Single(o => o.Value == "metal").IsChecked);
        Assert.Single(vm.SongSelect.SongList);

        // Rescan without it at all: the selection is dropped, nothing narrows.
        vm.SongSelect.SetLibraryForTest([OwnChart(@"C:\charts\a.dcp", "Blues")]);
        Assert.False(vm.SongSelect.GenreFacet.IsActive);
        Assert.Single(vm.SongSelect.SongList);
    }

    // ── the filter chips are the pass's own answer ────────────────────────────

    /// <summary>Every active filter becomes exactly one removable chip — one per
    /// selected facet OPTION (three tunings ticked = three chips) plus the
    /// search / record / updated specials — and an all-defaults state produces
    /// none. The chips are rebuilt BY the filter pass, so a chip can never
    /// claim a filter the list is not applying.</summary>
    [Fact]
    public void FilterChips_AreOnePerActiveFilterAndNoneWhenNothingIsNarrowing()
    {
        var vm = NewVm();
        vm.SongSelect.SetLibraryForTest([.. Rows(4), OwnChart(@"C:\charts\Ride.dcp", "Metal")]);
        Assert.Empty(vm.SongSelect.ActiveFilterChips);

        vm.SongSelect.SearchText = "song";
        vm.SongSelect.FilterPass.RunImmediately();
        vm.SongSelect.GenreFacet.Options.Single(o => o.Value == "metal").IsChecked = true;
        vm.SongSelect.PlayedFacet.Options.Single(o => o.Value == "unplayed").IsChecked = true;
        vm.SongSelect.RecordFilter = 2;

        Assert.Equal(4, vm.SongSelect.ActiveFilterChips.Count);
        Assert.Contains(vm.SongSelect.ActiveFilterChips,
            c => c.Kind == BrowserFilterChips.SearchKind && c.Label == "song");
        // A facet chip carries the facet KEY (so ✕ knows whose value to
        // deselect) and the option's LABEL (the authored casing, not the key).
        Assert.Contains(vm.SongSelect.ActiveFilterChips, c => c.Kind == "genre" && c.Label == "Metal");
        Assert.Contains(vm.SongSelect.ActiveFilterChips, c => c.Kind == "played");
        // The record chip prints the combo's own entry for that index, so the
        // two can never describe different grade floors.
        Assert.Contains(vm.SongSelect.ActiveFilterChips,
            c => c.Kind == "record" && c.Label == vm.SongSelect.RecordOptions[2]);
    }

    /// <summary>Every chip's ✕ resets exactly the one control it stands for —
    /// otherwise the chip comes straight back on the next pass. Exercised for
    /// every chip the full filter state produces, through the real removal
    /// command.</summary>
    [Fact]
    public void EveryFilterChip_RoundTripsThroughItsRemoveCommand()
    {
        var vm = NewVm();
        vm.SongSelect.SetLibraryForTest([.. Rows(4), OwnChart(@"C:\charts\Ride.dcp", "Metal")]);

        vm.SongSelect.SearchText = "song";
        vm.SongSelect.FilterPass.RunImmediately();
        vm.SongSelect.GenreFacet.Options.Single(o => o.Value == "metal").IsChecked = true;
        vm.SongSelect.TuningFacet.Options[0].IsChecked = true;
        vm.SongSelect.RecordFilter = 2;
        Assert.Equal(4, vm.SongSelect.ActiveFilterChips.Count);

        while (vm.SongSelect.ActiveFilterChips.Count > 0)
        {
            var before = vm.SongSelect.ActiveFilterChips.Count;
            vm.SongSelect.RemoveSongFilterCommand.Execute(vm.SongSelect.ActiveFilterChips[0]);
            Assert.Equal(before - 1, vm.SongSelect.ActiveFilterChips.Count);
        }

        Assert.Equal("", vm.SongSelect.SearchText);
        Assert.Equal(0, vm.SongSelect.RecordFilter);
        Assert.All(vm.SongSelect.SongFacets, f => Assert.False(f.IsActive));
        Assert.Equal(5, vm.SongSelect.SongList.Count);
    }

    // ── the sort, in BOTH directions ──────────────────────────────────────────

    /// <summary>
    ///     Play Mode's descending sort was <c>ordered.Reverse()</c> over the
    ///     materialized list, which is not a comparer: it inverted the very
    ///     unknown-LAST legs the sort had just applied. A second click on TIME
    ///     ("longest first") opened on every song with NO known length. The
    ///     editor browser fixed exactly this and Play Mode never got it, while
    ///     <c>docs/agents/ui/song-browser.md</c> claimed both surfaces followed
    ///     the rule.
    /// </summary>
    [Fact]
    public void SortingByLength_KeepsUnknownLengthsLast_InBothDirections()
    {
        var vm = NewVm();
        vm.SongSelect.SetLibraryForTest([
            Row("a", length: 0),   // no length in the manifest
            Row("b", length: 120),
            Row("c", length: 400)
        ]);

        vm.SongSelect.SortSongsByColumnCommand.Execute("time");           // ascending
        Assert.Equal([120d, 400d, 0d], vm.SongSelect.SongList.Select(s => s.LengthSeconds));

        vm.SongSelect.SortSongsByColumnCommand.Execute("time");           // …and flipped
        Assert.Equal([400d, 120d, 0d], vm.SongSelect.SongList.Select(s => s.LengthSeconds));
    }

    /// <summary>The same rule on the RATING sort — an unrated song must not
    /// hijack the top of "easiest first" either.</summary>
    [Fact]
    public void SortingByDifficulty_KeepsUnratedLast_InBothDirections()
    {
        var vm = NewVm();
        vm.SongSelect.SetLibraryForTest([
            Row("a", difficulty: 0),   // unrated
            Row("b", difficulty: 2),
            Row("c", difficulty: 9)
        ]);

        vm.SongSelect.SortMode = 4;                   // hardest first
        Assert.Equal([9d, 2d, 0d], vm.SongSelect.SongList.Select(s => s.Difficulty));

        vm.SongSelect.SortDescending = true;          // easiest first
        Assert.Equal([2d, 9d, 0d], vm.SongSelect.SongList.Select(s => s.Difficulty));
    }

    /// <summary>
    ///     #651: Song Select ties the way the Library does, through the one
    ///     <c>LibrarySort</c>. Equal ratings (and the unplayed block under recent and
    ///     best) fall through to ARTIST then title — they tied by title alone — and
    ///     two songs of one title fall through to their artist instead of keeping
    ///     scan order.
    /// </summary>
    [Fact]
    public void Ties_FallThroughToArtistThenTitle_LikeTheLibrary()
    {
        var vm = NewVm();
        vm.SongSelect.SetLibraryForTest([
            Named("a", "Beta", "Alpha song"),
            Named("b", "Alpha", "Zulu song"),
            Named("c", "Delta", "One"),
            Named("d", "Charlie", "One")
        ]);

        vm.SongSelect.SortMode = 4;                   // difficulty: every row rated 5
        Assert.Equal(["Zulu song", "Alpha song", "One", "One"], vm.SongSelect.SongList.Select(s => s.Title));
        Assert.Equal(["Alpha", "Beta", "Charlie", "Delta"], vm.SongSelect.SongList.Select(s => s.Artist));

        vm.SongSelect.SortMode = 1;                   // title: the two "One"s by artist
        Assert.Equal(["Alpha song", "One", "One", "Zulu song"], vm.SongSelect.SongList.Select(s => s.Title));
        Assert.Equal(["Beta", "Charlie", "Delta", "Alpha"], vm.SongSelect.SongList.Select(s => s.Artist));
    }

    private static PlaySongItem Named(string key, string artist, string title)
    {
        var row = PlaySongItem.FromPsarc(new PsarcSongListItem(new LibrarySongEntry
        {
            SongKey = key,
            Title = title,
            Artist = artist,
            SongLength = 200,
            TuningSummary = "E A D G B E",
            PackagePath = $@"C:\lib\{key}_p.psarc",
            Arrangements = [new LibraryArrangementEntry { ArrangementName = "Lead", SngPath = $"s/{key}.sng" }]
        }, FakeLocalization.English()));
        row.Difficulty = 5;
        return row;
    }

    /// <summary>
    ///     A rescan that drops the last selected value must put the caption
    ///     FUNNEL out with it. `BrowserFacet.ResetOptions` intersects the
    ///     selection with the values that survived and raises `IsActive` only —
    ///     never `SelectionChanged` — and Play Mode's funnels are computed
    ///     properties over SEVERAL facets, notified from `RefreshFunnelStates`,
    ///     which only the `SelectionChanged` handler called. So the chip
    ///     disappeared (chips are rebuilt from the options each pass) while the
    ///     funnel stayed lit, naming a filter that no longer existed and offering
    ///     an empty popover as the explanation.
    /// </summary>
    /// <remarks>Asserted on the NOTIFICATION, not the value: the funnel is a
    /// computed property over the facets, so reading it always gives the truth —
    /// the binding just never hears that it changed, which is why nothing caught
    /// this.</remarks>
    [Fact]
    public void ARescanThatDropsTheLastSelectedValue_PutsTheFunnelOut()
    {
        var vm = NewVm();
        vm.SongSelect.SetLibraryForTest([Row("a", tuning: "D A D G B E"), Row("b")]);

        vm.SongSelect.TuningFacet.Options.Single(o => o.Value == "D A D G B E").IsChecked = true;
        Assert.True(vm.SongSelect.HasTuningFacetFilters);

        var raised = new List<string?>();
        vm.SongSelect.PropertyChanged += (_, e) => raised.Add(e.PropertyName);

        // A different library: the selected tuning is gone, so ResetOptions drops
        // the selection — and the funnel must be told.
        vm.SongSelect.SetLibraryForTest([Row("c"), Row("d")]);

        Assert.False(vm.SongSelect.TuningFacet.IsActive);
        Assert.Contains(nameof(vm.SongSelect.HasTuningFacetFilters), raised);
    }

    private static PlaySongItem OwnChart(string path, string genre)
    {
        var song = PlaySongItem.FromProject(path, false, FakeLocalization.English());
        song.ApplyGenre(genre);
        return song;
    }

    // ── fixture ───────────────────────────────────────────────────────────────

    /// <summary>One row with the sort keys under test spelled out; everything
    /// else matches <see cref="Rows" />'s fixture.</summary>
    private static PlaySongItem Row(
        string key, double length = 200, double difficulty = 0, string tuning = "E A D G B E")
    {
        var row = PlaySongItem.FromPsarc(new PsarcSongListItem(new LibrarySongEntry
        {
            SongKey = key,
            Title = $"Song {key}",
            Artist = $"Artist {key}",
            Album = "Album",
            Charter = "Charter",
            SongLength = length,
            TuningSummary = tuning,
            PackagePath = $@"C:\lib\{key}_p.psarc",
            Arrangements = [new LibraryArrangementEntry { ArrangementName = "Lead", SngPath = $"s/{key}.sng" }]
        }, FakeLocalization.English()));
        // The rating is stamped by ApplyStats in the product, not carried by the
        // manifest — set it directly rather than standing up a stats store.
        row.Difficulty = difficulty;
        return row;
    }

    private static List<PlaySongItem> Rows(int count)
    {
        var rows = new List<PlaySongItem>(count);
        for (var i = 0; i < count; i++)
        {
            rows.Add(PlaySongItem.FromPsarc(new PsarcSongListItem(new LibrarySongEntry
            {
                SongKey = $"key{i:D3}",
                Title = $"Song {i}",
                Artist = $"Artist {i % 5}",
                Album = $"Album {i % 3}",
                Charter = $"Charter {i % 7}",
                SongLength = 200,
                TuningSummary = "E A D G B E",
                PackagePath = $@"C:\lib\key{i:D3}_p.psarc",
                Arrangements = [new LibraryArrangementEntry { ArrangementName = "Lead", SngPath = $"s/{i}.sng" }]
            }, FakeLocalization.English())));
        }

        return rows;
    }

    private PlayModeViewModel NewVm()
    {
        var profiles = new PlayerProfileService(FakeLocalization.English(), logger: null,
            filePath: Path.Combine(_dir, "player-profiles.json"), defaultProfileName: "Tester");
        var store = new PracticeProfileService(profiles, logger: null,
            legacyFilePath: Path.Combine(_dir, "legacy.json"));
        return TestRigBuilders.PlayMode(profiles, new GameplayStatsService(store),
            new LeaderboardService(profiles), new ProjectState(), localization: FakeLocalization.English());
    }
}
