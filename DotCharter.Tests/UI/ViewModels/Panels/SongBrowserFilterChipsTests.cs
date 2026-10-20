using DotCharter.Localization;
using DotCharter.Services.Editing.Selection;
using DotCharter.Services.Library;
using DotCharter.UI.ViewModels.Editor;
using DotCharter.UI.ViewModels.Panels;
using DotCharter.Services.Editing.Tracks;
using DotCharter.Services.Session;

namespace DotCharter.Tests.UI.ViewModels.Panels;

/// <summary>
///     The song browser's ACTIVE-FILTER CHIPS over the datagrid FACETS.
///     <para>
///         Three claims are worth a test each. The chips must be a projection of
///         NON-DEFAULT state only (an inert facet earns none; the one filter
///         whose default is ON — hide lessons — earns its chip in the FALSE
///         state). A chip removal, a facet clear or a clear-all must cost
///         exactly ONE filter pass. And the facet options must be derived from
///         the SCANNED library, with selections surviving a rescan only where
///         their value still exists.
///     </para>
/// </summary>
public sealed class SongBrowserFilterChipsTests
{
    // ── the pure builder ──────────────────────────────────────────────────

    /// <summary>Nothing is narrowing the list ⇒ no chips, and therefore no row.</summary>
    [Fact]
    public async Task TheRestingFilterState_EarnsNoChips()
    {
        var h = await Harness.Scanned();

        Assert.Empty(BrowserFilterChips.Build("", h.Vm.Facets, BrowserFilterChips.HideLessonsDefault, FakeLocalization.English()));

        // Whitespace is not a search: the predicate trims it, so a chip for it
        // would claim a filter the list is not applying.
        Assert.Empty(BrowserFilterChips.Build("   ", h.Vm.Facets, BrowserFilterChips.HideLessonsDefault, FakeLocalization.English()));
    }

    /// <summary>Each facet earns one chip PER SELECTED OPTION — multi-select
    /// means three ticked tunings are three chips, each removable alone — and
    /// the chip prints the option's own label, so the chip and the checkbox it
    /// stands for cannot read differently.</summary>
    [Theory]
    [InlineData("tuning")]
    [InlineData("capo")]
    [InlineData("path")]
    [InlineData("year")]
    [InlineData("difficulty")]
    [InlineData("length")]
    [InlineData("status")]
    [InlineData("origin")]
    public async Task EachFacet_EarnsOneChipPerSelectedOption(string key)
    {
        var h = await Harness.Scanned();
        var facet = h.Vm.Facets.Single(f => f.Key == key);
        Assert.True(facet.Options.Count > 0, $"the {key} facet derived no options from the fixture");

        facet.Options[0].IsChecked = true;

        var chip = Assert.Single(h.Vm.ActiveFilterChips);
        Assert.Equal(key, chip.Kind);
        Assert.Equal(facet.Options[0].Value, chip.Value);
        Assert.Equal(facet.Options[0].Label, chip.Label);
        Assert.False(string.IsNullOrWhiteSpace(chip.Label),
            $"the {key} chip printed nothing — an anonymous chip cannot be removed on purpose");

        if (facet.Options.Count > 1)
        {
            facet.Options[1].IsChecked = true;
            Assert.Equal(2, h.Vm.ActiveFilterChips.Count);
        }
    }

    /// <summary>The "lessons shown" chip is a real string, not the key name a
    /// missing resource resolves to.</summary>
    [Fact]
    public async Task TheLessonsChip_ResolvesARealString()
    {
        var h = await Harness.Scanned();
        h.Vm.HideLessons = false;

        var chip = Assert.Single(h.Vm.ActiveFilterChips);
        Assert.Equal(BrowserFilterChips.LessonsKind, chip.Kind);
        Assert.Equal(LocalizationService.Instance["SongBrowser_Chip_LessonsShown"], chip.Label);
        Assert.NotEqual("SongBrowser_Chip_LessonsShown", chip.Label);
    }

    /// <summary>Facet option labels resolve — the unknown/unrated buckets and
    /// the band labels are localized keys, and a missing key falls back to its
    /// own name, which reads as an identifier in the popover.</summary>
    [Fact]
    public async Task TheFacetOptionLabels_ResolveRealStrings()
    {
        var h = await Harness.Scanned();

        foreach (var facet in h.Vm.Facets)
        {
            foreach (var option in facet.Options)
            {
                Assert.False(string.IsNullOrWhiteSpace(option.Label),
                    $"{facet.Key} option '{option.Value}' printed nothing");
                Assert.False(option.Label.StartsWith("SongBrowser_", StringComparison.Ordinal),
                    $"{facet.Key} option '{option.Value}' printed its own key name: {option.Label}");
            }
        }
    }

    // ── the facet options, derived from the scan ──────────────────────────

    /// <summary>Options come from the DATA: every tuning the fixture carries,
    /// the capo values present, decades newest-first, and no phantom unknown
    /// bucket where the data has none.</summary>
    [Fact]
    public async Task TheFacetOptions_AreDerivedFromTheLibrary()
    {
        var h = await Harness.Scanned();

        Assert.Equal(3, h.Vm.TuningFacet.Options.Count);          // three fixture tunings
        Assert.Equal(["0", "2"], h.Vm.CapoFacet.Options.Select(o => o.Value)); // none + fret 2
        Assert.Equal(["Lead", "Rhythm"], h.Vm.PathFacet.Options.Select(o => o.Value));
        Assert.Equal(["1990"], h.Vm.YearFacet.Options.Select(o => o.Value));   // all 1990, no unknown bucket
        Assert.Equal([""], h.Vm.DifficultyFacet.Options.Select(o => o.Value)); // all unrated
        Assert.Equal(["mid"], h.Vm.LengthFacet.Options.Select(o => o.Value));  // all 200 s
        Assert.Equal(["loadable"], h.Vm.StatusFacet.Options.Select(o => o.Value));
        Assert.Equal([""], h.Vm.OriginFacet.Options.Select(o => o.Value)); // unstamped fixture = unknown bucket
    }

    /// <summary>A selection survives a rescan when its value still exists, and
    /// is dropped (not silently kept invisible) when it does not.</summary>
    [Fact]
    public async Task AFacetSelection_SurvivesARescan_WhenItsValueStillExists()
    {
        var h = await Harness.Scanned();
        var dropD = h.Vm.TuningFacet.Options.Single(o => o.Value == "D A D G B E");
        dropD.IsChecked = true;
        Assert.True(h.Vm.TuningFacet.IsActive);

        await h.Vm.ScanCommand.ExecuteAsync(null);
        h.Drain();

        Assert.Contains("D A D G B E", h.Vm.TuningFacet.Selected);
        Assert.True(h.Vm.TuningFacet.Options.Single(o => o.Value == "D A D G B E").IsChecked);

        // …and a library that lost the tuning drops the selection with it.
        h.Library.Result = [Entry("only", "Solo", "One", tuning: "E A D G B E")];
        await h.Vm.ScanCommand.ExecuteAsync(null);
        h.Drain();

        Assert.False(h.Vm.TuningFacet.IsActive);
    }

    // ── the view model: one pass per gesture ──────────────────────────────

    /// <summary>The row is a projection of the pass, so it is rebuilt INSIDE
    /// <c>ApplyFilter</c> — not in the search box's setter.</summary>
    [Fact]
    public async Task TheChips_AreRefreshedByTheFilterPass_NotByTheSetter()
    {
        var h = await Harness.Scanned();
        Assert.Empty(h.Vm.ActiveFilterChips);
        Assert.False(h.Vm.HasActiveFilters);

        h.Vm.SearchText = "metallica";
        Assert.Empty(h.Vm.ActiveFilterChips); // the pass is queued below input

        h.Drain();

        var chip = Assert.Single(h.Vm.ActiveFilterChips);
        Assert.Equal(BrowserFilterChips.SearchKind, chip.Kind);
        Assert.Equal("metallica", chip.Label);
        Assert.True(h.Vm.HasActiveFilters);
    }

    /// <summary>A facet toggle is a discrete gesture: ONE immediate pass, and
    /// the list narrows on the same gesture.</summary>
    [Fact]
    public async Task ACheckboxToggle_IsOneImmediatePass_AndNarrowsTheList()
    {
        var h = await Harness.Scanned();
        var before = h.Vm.FilterPass.PassCount;

        h.Vm.TuningFacet.Options.Single(o => o.Value == "D A D G B E").IsChecked = true;

        Assert.Equal(before + 1, h.Vm.FilterPass.PassCount);
        Assert.False(h.Vm.FilterPass.IsPending);
        Assert.True(h.Vm.Songs.Count < Harness.DistinctSongs);
        Assert.All(h.Vm.Songs, s => Assert.Equal("D A D G B E", s.TuningSummary));

        // Multi-select widens within the facet: a second tuning ORs in.
        var narrowed = h.Vm.Songs.Count;
        h.Vm.TuningFacet.Options.Single(o => o.Value == "E A D G B E").IsChecked = true;
        Assert.True(h.Vm.Songs.Count > narrowed);
    }

    /// <summary>Every facet engaged plus search and lessons ⇒ one chip each; and
    /// the count is what gates the row, so the two can never disagree.</summary>
    [Fact]
    public async Task HasActiveFilters_IsTheChipCount()
    {
        var h = await Harness.Scanned();
        h.EngageEveryFilter();

        Assert.Equal(10, h.Vm.ActiveFilterChips.Count);
        Assert.True(h.Vm.HasActiveFilters);

        h.Vm.ClearFiltersCommand.Execute(null);

        Assert.Empty(h.Vm.ActiveFilterChips);
        Assert.False(h.Vm.HasActiveFilters);
    }

    /// <summary>A chip's ✕ deselects EXACTLY the option it stands for, in one
    /// pass — the rest of its facet's selection stays.</summary>
    [Fact]
    public async Task RemovingAChip_DeselectsExactlyThatOption_InOnePass()
    {
        var h = await Harness.Scanned();
        h.Vm.TuningFacet.Options[0].IsChecked = true;
        h.Vm.TuningFacet.Options[1].IsChecked = true;
        Assert.Equal(2, h.Vm.ActiveFilterChips.Count);

        var before = h.Vm.FilterPass.PassCount;
        var chip = h.Vm.ActiveFilterChips[0];
        h.Vm.RemoveBrowserFilterCommand.Execute(chip);

        Assert.Equal(before + 1, h.Vm.FilterPass.PassCount);
        Assert.False(h.Vm.FilterPass.IsPending);
        var left = Assert.Single(h.Vm.ActiveFilterChips);
        Assert.NotEqual(chip.Value, left.Value);
        Assert.DoesNotContain(chip.Value, h.Vm.TuningFacet.Selected);
        Assert.Contains(left.Value, h.Vm.TuningFacet.Selected);
    }

    /// <summary>…and the two non-facet chips reset their own controls.</summary>
    [Fact]
    public async Task TheSearchAndLessonsChips_ResetTheirOwnControls()
    {
        var h = await Harness.Scanned();
        h.Vm.SearchText = "track";
        h.Vm.HideLessons = false;
        h.Drain();
        Assert.Equal(2, h.Vm.ActiveFilterChips.Count);

        h.Vm.RemoveBrowserFilterCommand.Execute(
            h.Vm.ActiveFilterChips.Single(c => c.Kind == BrowserFilterChips.SearchKind));
        Assert.Equal("", h.Vm.SearchText);

        h.Vm.RemoveBrowserFilterCommand.Execute(
            h.Vm.ActiveFilterChips.Single(c => c.Kind == BrowserFilterChips.LessonsKind));
        Assert.True(h.Vm.HideLessons);
        Assert.Empty(h.Vm.ActiveFilterChips);
    }

    /// <summary>Clear-all is ONE pass, not one per facet — the whole point of
    /// the suspend guard. It resets the search box too, and leaves the sort
    /// alone: a sort narrows nothing, so it is not a filter.</summary>
    [Fact]
    public async Task ClearingEveryFilter_CostsExactlyOnePass_AndKeepsTheSort()
    {
        var h = await Harness.Scanned();
        h.EngageEveryFilter();
        h.Vm.SortMode = 1;
        h.Vm.SortDescending = true;

        var before = h.Vm.FilterPass.PassCount;
        h.Vm.ClearFiltersCommand.Execute(null);

        Assert.Equal(before + 1, h.Vm.FilterPass.PassCount);
        Assert.False(h.Vm.FilterPass.IsPending);
        h.Drain();
        Assert.Equal(before + 1, h.Vm.FilterPass.PassCount);

        Assert.Equal("", h.Vm.SearchText);
        Assert.All(h.Vm.Facets, f => Assert.False(f.IsActive));
        Assert.True(h.Vm.HideLessons);
        Assert.Equal(1, h.Vm.SortMode);
        Assert.True(h.Vm.SortDescending);

        // The list is genuinely back to the whole (de-duplicated) library.
        Assert.Equal(Harness.DistinctSongs, h.Vm.Songs.Count);
    }

    /// <summary>The status popover's one clear resets status, origin AND the
    /// lessons toggle, in one pass.</summary>
    [Fact]
    public async Task TheStatusPopoversClear_ResetsStatusOriginAndLessons_InOnePass()
    {
        var h = await Harness.Scanned();
        h.Vm.StatusFacet.Options[0].IsChecked = true;
        h.Vm.OriginFacet.Options[0].IsChecked = true;
        h.Vm.HideLessons = false;
        h.Drain();

        var before = h.Vm.FilterPass.PassCount;
        h.Vm.ClearStatusPopoverCommand.Execute(null);

        Assert.Equal(before + 1, h.Vm.FilterPass.PassCount);
        Assert.False(h.Vm.StatusFacet.IsActive);
        Assert.False(h.Vm.OriginFacet.IsActive);
        Assert.True(h.Vm.HideLessons);
    }

    /// <summary>The tuning popover's one clear resets BOTH its facets (tuning
    /// and capo share the column), in one pass.</summary>
    [Fact]
    public async Task TheTuningPopoversClear_ResetsTuningAndCapo_InOnePass()
    {
        var h = await Harness.Scanned();
        h.Vm.TuningFacet.Options[0].IsChecked = true;
        h.Vm.CapoFacet.Options[0].IsChecked = true;

        var before = h.Vm.FilterPass.PassCount;
        h.Vm.ClearTuningAndCapoCommand.Execute(null);

        Assert.Equal(before + 1, h.Vm.FilterPass.PassCount);
        Assert.False(h.Vm.TuningFacet.IsActive);
        Assert.False(h.Vm.CapoFacet.IsActive);
    }

    /// <summary>A chip for a filter the pass is not applying is the failure this
    /// whole shape exists to prevent: clearing one filter must widen the list.</summary>
    [Fact]
    public async Task RemovingAChip_WidensTheListItDescribed()
    {
        var h = await Harness.Scanned();
        h.Vm.TuningFacet.Options.Single(o => o.Value == "D A D G B E").IsChecked = true;
        var narrowed = h.Vm.Songs.Count;

        Assert.True(narrowed < Harness.DistinctSongs, "the tuning filter narrowed nothing — fixture problem");

        h.Vm.RemoveBrowserFilterCommand.Execute(Assert.Single(h.Vm.ActiveFilterChips));

        Assert.Equal(Harness.DistinctSongs, h.Vm.Songs.Count);
        Assert.Empty(h.Vm.ActiveFilterChips);
    }

    // ── the facet counts (the popover's "(n)" figures) ────────────────────

    /// <summary>An option's count is "the list with every OTHER filter applied,
    /// this facet counted per option" — so the counts still add up while the
    /// facet itself is engaged (the classic faceted-search contract).</summary>
    [Fact]
    public async Task TheFacetCounts_ApplyEveryOtherFilter_ButNotTheFacetItself()
    {
        var h = await Harness.Scanned();

        h.Vm.RefreshFacetCounts(h.Vm.TuningFacet);
        Assert.Equal(Harness.DistinctSongs, h.Vm.TuningFacet.Options.Sum(o => o.Count));

        // Narrow by SEARCH: tuning counts shrink with it…
        h.Vm.SearchText = "Track 1"; // Track 1 + Track 1x — tokenized
        h.Drain();
        h.Vm.RefreshFacetCounts(h.Vm.TuningFacet);
        Assert.Equal(h.Vm.Songs.Count, h.Vm.TuningFacet.Options.Sum(o => o.Count));

        // …and selecting a TUNING leaves the tuning counts themselves whole
        // (the facet being counted is skipped), while the YEAR counts narrow.
        h.Vm.SearchText = "";
        h.Drain();
        h.Vm.TuningFacet.Options.Single(o => o.Value == "D A D G B E").IsChecked = true;
        h.Vm.RefreshFacetCounts(h.Vm.TuningFacet);
        Assert.Equal(Harness.DistinctSongs, h.Vm.TuningFacet.Options.Sum(o => o.Count));

        h.Vm.RefreshFacetCounts(h.Vm.YearFacet);
        Assert.Equal(h.Vm.Songs.Count, h.Vm.YearFacet.Options.Sum(o => o.Count));
    }

    // ── source lint: the markup, which no behaviour test in the gate sees ──

    /// <summary>
    ///     The chips ROW exists in the Library's markup, each chip is removable, every ✕ carries
    ///     a name AND a tooltip — and every filterable header carries its funnel over the
    ///     browser's facets (the adapter names each one), with the counts hook wired.
    /// </summary>
    [Fact]
    public void TheBrowserMarkup_CarriesTheChipRow_TheFunnels_AndTheirPopovers()
    {
        var markup = UiSourceTree.ReadAllText(Path.Combine(UiDirectory(), "Views", "Library", "LibraryView.axaml"));

        var open = markup.IndexOf("x:Name=\"LibraryFilterChips\"", StringComparison.Ordinal);
        Assert.True(open > 0, "the Library has no active-filter chips row — repoint this lint or restore it.");
        var close = markup.IndexOf("</WrapPanel>", markup.IndexOf("</ItemsControl>", open, StringComparison.Ordinal), StringComparison.Ordinal);
        Assert.True(close > open);
        var row = markup[open..close];

        Assert.Contains("IsVisible=\"{Binding HasActiveFilters}\"", row, StringComparison.Ordinal);
        Assert.Contains("ItemsSource=\"{Binding ActiveFilters}\"", row, StringComparison.Ordinal);
        Assert.Contains("Command=\"{Binding RemoveCommand}\"", row, StringComparison.Ordinal);
        Assert.Contains("AutomationProperties.Name=\"{Binding Label}\"", row, StringComparison.Ordinal);
        Assert.Contains("ToolTip.Tip=\"{localization:Loc PlayMode_SongSelect_RemoveFilter}\"", row, StringComparison.Ordinal);
        Assert.Contains("ClearFiltersCommand", row, StringComparison.Ordinal);

        // The funnel template: lit while filtering, its options the facet's, the counts refreshed on opening.
        Assert.Contains("Classes.on=\"{Binding IsFiltered}\"", markup, StringComparison.Ordinal);
        Assert.Contains("ItemsSource=\"{Binding FilterOptions}\"", markup, StringComparison.Ordinal);
        Assert.Contains("Opening=\"OnFilterOpening\"", markup, StringComparison.Ordinal);

        var screen = UiSourceTree.ReadAllText(Path.Combine(UiDirectory(), "ViewModels", "Panels", "LibraryScreen.cs"));
        foreach (var facet in new[]
                 { "PathFacet", "TuningFacet", "CapoFacet", "YearFacet", "DifficultyFacet", "LengthFacet", "StatusFacet", "OriginFacet" })
        {
            Assert.Contains($"browser.{facet}", screen, StringComparison.Ordinal);
        }

        Assert.Contains("SongBrowser_Facet_ShowLessons", screen, StringComparison.Ordinal);
    }

    // ── fixture ───────────────────────────────────────────────────────────

    private static string UiDirectory()
    {
        var dir = AppContext.BaseDirectory;
        while (dir != null && !Directory.Exists(Path.Combine(dir, "DotCharter.UI")))
            dir = Path.GetDirectoryName(dir);

        Assert.NotNull(dir);
        return Path.Combine(dir!, "DotCharter.UI");
    }

    private static LibrarySongEntry Entry(
        string key, string artist, string title, string tuning = "E A D G B E", int capo = 0,
        string album = "Album 1") => new()
        {
            SongKey = key,
            Title = title,
            Artist = artist,
            Album = album,
            Year = 1990,
            SongLength = 200,
            TuningSummary = tuning,
            CapoSummary = capo,
            PackagePath = $@"C:\lib\{key}_p.psarc",
            Arrangements =
        [
            new LibraryArrangementEntry { ArrangementName = "Lead", SngPath = $"s/{key}_lead.sng" },
            new LibraryArrangementEntry { ArrangementName = "Rhythm", SngPath = $"s/{key}_rhythm.sng" }
        ]
        };

    // ── the unknown bucket, per facet ─────────────────────────────────────────

    /// <summary>
    ///     EVERY facet whose predicate can reject an unknown value must OFFER
    ///     that value, or the exclusion is both invisible and unreachable: the
    ///     row is dropped, no checkbox brings it back, and the chips row has
    ///     nothing to name. TUNING and PATHS had no unknown option at all —
    ///     they filtered the option list to non-empty values and never appended
    ///     the bucket the way YEAR, DIFFICULTY, LENGTH and Play Mode's own
    ///     twins do — so ticking any tuning silently swallowed every own chart
    ///     and metadata-less package.
    ///     <para>
    ///         Written per FACET rather than per case, because the same omission
    ///         in a ninth facet would otherwise be a ninth bug. The fixture's
    ///         songs all carry tunings and paths, so an unknown row is pushed in
    ///         by hand: an option that only appears when the data has unknowns
    ///         is exactly the contract.
    ///     </para>
    /// </summary>
    [Fact]
    public async Task EveryFacetThatCanRejectAnUnknown_OffersIt()
    {
        var h = await Harness.Scanned();
        h.Library.Result = [.. h.Library.Result, Harness.Unknown()];
        await h.Vm.ScanCommand.ExecuteAsync(null);
        h.Drain();

        foreach (var facet in h.Vm.Facets)
        {
            // Tick every option this facet offers. A row the facet can reject
            // must therefore survive — that is what "the buckets are complete"
            // means, and it is what a user clicking "select all" would expect.
            foreach (var option in facet.Options) option.IsChecked = true;
            h.Drain();

            Assert.True(h.Vm.Songs.Any(s => s.Song.SongKey == "unknown"),
                $"The '{facet.Key}' facet has no option that matches a row with no value for it — " +
                "such a row is dropped by the predicate with no checkbox that can bring it back.");

            facet.Clear();
            h.Drain();
        }
    }

    internal sealed class Harness
    {
        public const int DistinctSongs = 24;

        /// <summary>A row with no tuning, no capo, no paths, no year and no
        /// length — the own-chart / metadata-less package shape.</summary>
        public static LibrarySongEntry Unknown() => new()
        {
            SongKey = "unknown",
            Title = "Track unknown",
            Artist = "Metallica",
            Album = "Album 1",
            PackagePath = @"C:\lib\unknown_p.psarc",
            Arrangements = []
        };

        private readonly List<Action> _queue = [];

        private Harness()
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
                new DotCharter.Presentation.Dialogs.DialogsSlot(), DotCharter.Localization.LocalizationService.Instance,
                _queue.Add);
        }

        public StubLibraryService Library { get; } = new() { Result = BuildLibrary() };
        public SongBrowserViewModel Vm { get; }

        public static async Task<Harness> Scanned()
        {
            var h = new Harness();
            h.Vm.PsarcLocation = @"C:\lib";
            await h.Vm.ScanCommand.ExecuteAsync(null);
            h.Drain();
            return h;
        }

        /// <summary>Search + one option of every facet + the lessons toggle off
        /// its default, so the row shows nine chips.</summary>
        public void EngageEveryFilter()
        {
            Vm.SearchText = "track";
            foreach (var facet in Vm.Facets) facet.Options[0].IsChecked = true;
            Vm.HideLessons = false;
            Drain();

            Assert.Equal(10, Vm.ActiveFilterChips.Count);
        }

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
            string[] artists = ["Metallica", "Pink Floyd", "Rush", "Tool"];
            string[] tunings = ["E A D G B E", "Eb Ab Db Gb Bb Eb", "D A D G B E"];

            var songs = new List<LibrarySongEntry>();
            for (var i = 0; i < DistinctSongs; i++)
            {
                songs.Add(Entry(
                    $"song{i:D3}", artists[i % artists.Length], $"Track {i}",
                    tunings[i % tunings.Length], i % 3 == 0 ? 2 : 0,
                    $"Album {i % 4}"));
            }

            return songs;
        }
    }

    internal sealed class StubLibraryService : ILibraryService
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

        /// <summary>Song keys whose in-archive cover was asked for, in order (any thread).</summary>
        public System.Collections.Concurrent.ConcurrentQueue<string> CoverRequests { get; } = new();

        public Task<LibraryLoadedSong> LoadSongAsync(LibrarySongEntry song) => throw new NotSupportedException();

        public Task<string> LoadCoverImageAsync(LibrarySongEntry song)
        {
            CoverRequests.Enqueue(song.SongKey);
            return Task.FromResult("");
        }
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
