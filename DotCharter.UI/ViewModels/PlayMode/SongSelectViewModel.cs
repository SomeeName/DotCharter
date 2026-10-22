using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using DotCharter.Presentation.Shared.Library;
using DotCharter.Services.Gameplay.Stats;
using DotCharter.Services.Library;
using DotCharter.Services.Parsing.Rscp;
using DotCharter.Tone.UI.ViewModels.Shell;
using DotCharter.UI.ViewModels.Panels;

namespace DotCharter.UI.ViewModels.PlayMode;

/// <summary>
///     Play Mode's Song Select list: the library load and scan, the column facets
///     and filters, search, sort, the removable filter chips and the artist group
///     headers. <see cref="PlayModeViewModel" /> owns one and exposes it as
///     <c>SongSelect</c>, so <c>SongSelectView</c> binds <c>SongSelect.…</c>.
///     <para>
///         The chosen song (<c>SelectedSong</c>) stays on Play Mode: Get Ready,
///         Pause, setlists and Home's recent-song entry all read it, and the detail
///         rail beside the list binds it there. Play Mode reaches back in through
///         <see cref="AllSongs" />, <see cref="EnsureLibrary" />,
///         <see cref="RefreshSongStats" /> and <see cref="LoadingChanged" />.
///     </para>
/// </summary>
public sealed partial class SongSelectViewModel : ObservableObject
{
    private readonly ILocalizationService _localization;
    private readonly IGameplayStatsService _stats;
    private readonly IConfigurationService _configService;
    private readonly IExportTargetWriter _files;
    private readonly SongBrowserViewModel? _songBrowser;

    /// <summary>Scan-time cache of the genre each OWN chart authors in its
    /// <c>.dcproj</c> header — an own chart's genre has no other real source. Warmed on
    /// a pool thread once per library scan by <see cref="RebuildFromBrowserAsync" />;
    /// read (lookup only, no I/O) while the song rows are built.</summary>
    private readonly IChartGenreIndex _chartGenres;

    private readonly List<PlaySongItem> _allSongs = [];
    private Task? _ensureLibraryTask;

    /// <summary>The column facets, built lazily — each closes over the filter pass.
    /// Selections are string SETS of canonical bucket keys, matched by the pure
    /// <c>LibraryQuery</c>.</summary>
    private BrowserFacet? _playedFacet;

    private BrowserFacet? _originFacet;
    private BrowserFacet? _genreFacet;
    private BrowserFacet? _pathFacet;
    private BrowserFacet? _tuningFacet;
    private BrowserFacet? _capoFacet;
    private BrowserFacet? _lengthFacet;
    private BrowserFacet? _difficultyFacet;
    private IReadOnlyList<BrowserFacet>? _songFacets;

    /// <summary>Set while <see cref="ClearSongFilters" /> resets every facet and
    /// special in one gesture, and while <see cref="BuildSongFacetOptions" /> refills
    /// the option lists — so the per-control hooks don't each run a full pass.</summary>
    private bool _suspendFilter;

    /// <summary>Lazy because it closes over an instance method, which a field
    /// initializer cannot do.</summary>
    private CoalescedFilterPass? _filterPass;

    public SongSelectViewModel(
        ILocalizationService localization,
        ILibraryService library,
        IGameplayStatsService stats,
        IConfigurationService configService,
        IExportTargetWriter files,
        IChartGenreIndex chartGenres,
        SongBrowserViewModel? songBrowser)
    {
        _localization = localization;
        Library = library;
        _stats = stats;
        _configService = configService;
        _files = files;
        _chartGenres = chartGenres;
        _songBrowser = songBrowser;
        RecordOptions = PlayRecordFilter.OptionLabels(localization);
    }

    /// <summary>Raised after <see cref="IsLibraryLoading" /> changes and the empty
    /// states follow it — Play Mode's launch screen prints the scan state.</summary>
    public event Action? LoadingChanged;

    /// <summary>Every scanned song, before filters — Play Mode's front door, launch
    /// screen and Home entry count and look up songs here.</summary>
    internal IReadOnlyList<PlaySongItem> AllSongs => _allSongs;

    /// <summary>The visible rows. A <see cref="BulkObservableCollection{T}" />
    /// because a republish is ONE <c>Reset</c>: the old <c>Clear()</c> + N
    /// <c>Add()</c> raised 2,160 <c>CollectionChanged</c> events on the owner's
    /// library, and — measured Release, real row template, ~15 realized rows —
    /// cost 117–129 ms whether or not a single row had changed. <c>ResetWith</c>
    /// early-outs on an identical sequence (0.2 ms), which is what the second half
    /// of a typed query is.</summary>
    public ObservableCollection<PlaySongItem> SongList { get; } = new BulkObservableCollection<PlaySongItem>();

    /// <summary>Typing coalesces into ONE pass; every other filter control is a
    /// discrete gesture and applies at once. Same rule, and the same type, as the
    /// editor's song browser.</summary>
    public CoalescedFilterPass FilterPass => _filterPass ??= new CoalescedFilterPass(ApplyFilter);

    [ObservableProperty] private string _searchText = "";

    /// <summary>0 = Artist, 1 = Title, 2 = Recently played, 3 = Best score.</summary>
    [ObservableProperty] private int _sortMode;

    [ObservableProperty] private bool _isLibraryLoading;
    [ObservableProperty] private string _songSelectHint = "";

    /// <summary>Where Song Select's rows load their covers from (SongSelectView).</summary>
    public ILibraryService Library { get; }

    // ── The column FACETS ───────────────────────────────────────────────────
    // The editor browser's datagrid filters: one BrowserFacet per filterable
    // column, opened from the caption row's funnels, multi-select, options
    // DERIVED from the library with explicit unknown buckets. RECORD keeps its
    // ordinal ladder combo in the RECORD column's popover.

    public BrowserFacet PlayedFacet => _playedFacet ??= NewSongFacet(LibraryFacets.Played);
    public BrowserFacet OriginFacet => _originFacet ??= NewSongFacet(LibraryFacets.Origin);
    public BrowserFacet GenreFacet => _genreFacet ??= NewSongFacet(LibraryFacets.Genre);
    public BrowserFacet PathFacet => _pathFacet ??= NewSongFacet(LibraryFacets.Path);
    public BrowserFacet TuningFacet => _tuningFacet ??= NewSongFacet(LibraryFacets.Tuning);
    public BrowserFacet CapoFacet => _capoFacet ??= NewSongFacet(LibraryFacets.Capo);
    public BrowserFacet LengthFacet => _lengthFacet ??= NewSongFacet(LibraryFacets.Length);
    public BrowserFacet DifficultyFacet => _difficultyFacet ??= NewSongFacet(LibraryFacets.Difficulty);

    /// <summary>Every facet, in the chips row's order.</summary>
    public IReadOnlyList<BrowserFacet> SongFacets =>
        _songFacets ??=
        [
            PlayedFacet, OriginFacet, GenreFacet, PathFacet,
            TuningFacet, CapoFacet, LengthFacet, DifficultyFacet
        ];

    private BrowserFacet NewSongFacet(string key)
    {
        var facet = new BrowserFacet(key, _localization[LibraryFacets.TitleKey(key)]);
        facet.SelectionChanged += () =>
        {
            RefreshFunnelStates();
            if (!_suspendFilter) FilterPass.RunImmediately();
        };
        return facet;
    }

    // ── Funnel lit-state, one bool per caption funnel ──────────────────────
    // The header funnels that host MORE than one facet (the browser's Status
    // shape) light on ANY of their residents being active — a funnel that only
    // lights for its first resident hides an applied filter, which is the
    // exact fault the caption-row design exists to remove.

    public bool HasSongIdentityFacetFilters =>
        PlayedFacet.IsActive || OriginFacet.IsActive || GenreFacet.IsActive;

    public bool HasTuningFacetFilters => TuningFacet.IsActive || CapoFacet.IsActive;

    public bool HasRecordPopoverFilters => RecordFilter > 0;

    public bool HasTimeFacetFilters => LengthFacet.IsActive || DifficultyFacet.IsActive;

    private void RefreshFunnelStates()
    {
        OnPropertyChanged(nameof(HasSongIdentityFacetFilters));
        OnPropertyChanged(nameof(HasTuningFacetFilters));
        OnPropertyChanged(nameof(HasRecordPopoverFilters));
        OnPropertyChanged(nameof(HasTimeFacetFilters));
    }

    /// <summary>Runs <paramref name="reset" /> with the per-control passes held,
    /// then the ONE pass the gesture needs.</summary>
    private void ResetSuspended(Action reset)
    {
        _suspendFilter = true;
        try
        {
            reset();
        }
        finally
        {
            _suspendFilter = false;
        }

        FilterPass.RunImmediately();
    }

    /// <summary>Footer clear for the SONG caption's popover (what the row IS:
    /// played state, ownership, origin, genre).</summary>
    [RelayCommand]
    private void ClearSongIdentityFacets() => ResetSuspended(() =>
    {
        PlayedFacet.Clear();
        OriginFacet.Clear();
        GenreFacet.Clear();
    });

    /// <summary>Footer clear for the TUNING caption's popover — tuning and capo
    /// share it (same shape as the editor browser's).</summary>
    [RelayCommand]
    private void ClearTuningAndCapo() => ResetSuspended(() =>
    {
        TuningFacet.Clear();
        CapoFacet.Clear();
    });

    /// <summary>Footer clear for the RECORD caption's popover (the ordinal
    /// ladder).</summary>
    [RelayCommand]
    private void ClearRecordPopover() => ResetSuspended(() => RecordFilter = 0);

    /// <summary>Footer clear for the TIME caption's popover (length +
    /// difficulty).</summary>
    [RelayCommand]
    private void ClearTimeFacets() => ResetSuspended(() =>
    {
        LengthFacet.Clear();
        DifficultyFacet.Clear();
    });

    /// <summary>Record filter: an index into the <see cref="PlayRecordFilter" />
    /// ladder — 0 = any, 1..N = "played to at least grade X" (best first), last =
    /// full combo only. The ONE filter an unplayed song FAILS: a record you have
    /// not set is not unknown data, it is no record.</summary>
    [ObservableProperty] private int _recordFilter;

    /// <summary>The record combo's labels, in ladder order. Built ONCE from
    /// <see cref="PlayRecordFilter.OptionLabels" /> — the entries are fixed (the
    /// grade scale does not depend on the library).</summary>
    public IReadOnlyList<string> RecordOptions { get; }

    partial void OnRecordFilterChanged(int value)
    {
        _ = value;
        OnPropertyChanged(nameof(HasRecordPopoverFilters));
        if (!_suspendFilter) FilterPass.RunImmediately();
    }

    /// <summary>Pick/change the PSARC library folder right from song select
    /// (persists via the browser's config path) and rebuild the list.</summary>
    [RelayCommand]
    private async Task ChangeLibraryFolder()
    {
        if (_songBrowser == null) return;
        IsLibraryLoading = true;
        try
        {
            await _songBrowser.BrowseCommand.ExecuteAsync(null);
            await RebuildFromBrowserAsync();
        }
        finally
        {
            IsLibraryLoading = false;
        }
    }

    partial void OnSearchTextChanged(string value)
    {
        _ = value;
        FilterPass.Request();
    }

    partial void OnSortModeChanged(int value)
    {
        _ = value;
        RefreshSongSortGlyphs();
        FilterPass.RunImmediately();
    }

    /// <summary>Flips the active sort's natural direction.</summary>
    [ObservableProperty] private bool _sortDescending;

    partial void OnSortDescendingChanged(bool value)
    {
        _ = value;
        RefreshSongSortGlyphs();
        FilterPass.RunImmediately();
    }

    /// <summary>Datagrid-style header sort, ported from the editor browser:
    /// clicking a caption sorts by its column ascending, clicking again flips
    /// the direction; the sort ComboBox stays the accessible alternative over
    /// ONE state.</summary>
    [RelayCommand]
    private void SortSongsByColumn(string column)
    {
        if (LibrarySort.SongSelect.Click(column, SortMode, SortDescending) is not { } next) return;
        if (next.Mode == SortMode)
        {
            SortDescending = next.Descending; // its own hook runs the pass
            return;
        }

        ResetSuspended(() =>
        {
            SortMode = next.Mode;
            SortDescending = next.Descending;
        });
    }

    /// <summary>▲ / ▼ beside the active sort column's caption; empty elsewhere.</summary>
    public string SongColumnSortGlyph => SongSortGlyphFor("song");

    public string TuningColumnSortGlyph => SongSortGlyphFor("tuning");
    public string RecordColumnSortGlyph => SongSortGlyphFor("record");
    public string TimeColumnSortGlyph => SongSortGlyphFor("time");

    private string SongSortGlyphFor(string caption)
        => LibrarySort.Glyph(SortMode, SortDescending, LibrarySort.SongSelect.ModeOf(caption));

    private void RefreshSongSortGlyphs()
    {
        OnPropertyChanged(nameof(SongColumnSortGlyph));
        OnPropertyChanged(nameof(TuningColumnSortGlyph));
        OnPropertyChanged(nameof(RecordColumnSortGlyph));
        OnPropertyChanged(nameof(TimeColumnSortGlyph));
    }

    /// <summary>Scans the library once; a second caller joins the running scan.</summary>
    internal Task EnsureLibrary()
    {
        if (_ensureLibraryTask is { IsCompleted: false }) return _ensureLibraryTask;
        if (_allSongs.Count > 0 || _songBrowser == null) return Task.CompletedTask;
        return _ensureLibraryTask = LoadLibraryAsync();
    }

    private async Task LoadLibraryAsync()
    {
        IsLibraryLoading = true;
        try
        {
            // Reuse the browser's scan (and its config-persisted library path).
            await _songBrowser!.Activate();
            await RebuildFromBrowserAsync();
        }
        finally
        {
            IsLibraryLoading = false;
        }
    }

    /// <summary>The own-chart project paths a scan considers: every project the
    /// practice store has scores for, plus the most recently opened one. There
    /// is no separate chart library, so this IS the set.
    /// <para>Split out of <see cref="RebuildFromBrowser" /> so the genre warm
    /// below can run against the same list BEFORE the rows are built, and so the
    /// config read happens once per scan rather than twice.</para></summary>
    private List<string> CollectOwnChartPaths()
    {
        var stats = _stats.GetStatistics();
        var ownPaths = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var key in stats.MostPlayed.Concat(stats.RecentlyPlayed).Select(m => m.SongKey))
        {
            if (LooksLikeProjectPath(key))
                ownPaths.Add(key);
        }

        var lastProject = _configService.LoadAppConfig().LastProjectPath;
        if (LooksLikeProjectPath(lastProject)) ownPaths.Add(lastProject!);

        return ownPaths.OrderBy(Path.GetFileNameWithoutExtension).ToList();
    }

    /// <summary>Warms the own-chart genre cache on a POOL thread, then rebuilds
    /// the list on the UI thread.
    /// <para><b>The hop is the point.</b> A genre lives in the chart file's
    /// header, so knowing it means a disk read — and the only other place the
    /// song rows are assembled from is <c>BuildPlanRequest</c>, which runs on the
    /// UI thread every time a planner knob moves. Reading here, once per scan,
    /// next to the PSARC scan that already ran off-thread, is what keeps that
    /// promise: the planner's own path never touches a file.</para></summary>
    private async Task RebuildFromBrowserAsync()
    {
        var ownPaths = CollectOwnChartPaths();
        await WarmOwnChartGenresAsync(ownPaths);
        RebuildFromBrowser(ownPaths);
    }

    /// <summary>The one place a <c>.dcproj</c> header is read for a genre. Once
    /// per scan, off the UI thread, and a no-op when nothing changed since the
    /// last scan (<c>ChartGenreIndex</c> stats before it reads).</summary>
    private async Task WarmOwnChartGenresAsync(IReadOnlyList<string> ownPaths)
    {
        if (ownPaths.Count == 0) return;
        await Task.Run(() => _chartGenres.Warm(ownPaths)).ConfigureAwait(true);
    }

    /// <summary>Own-chart rows for the scanned paths, each stamped with the
    /// genre the warm above cached (lookup only — no I/O reaches here).</summary>
    private List<PlaySongItem> BuildOwnChartRows(IReadOnlyList<string> ownPaths)
    {
        var rows = new List<PlaySongItem>(ownPaths.Count);
        foreach (var path in ownPaths)
        {
            var song = PlaySongItem.FromProject(path, _files.FileExists(path), _localization);
            if (!song.CanPlay) continue;
            song.ApplyGenre(_chartGenres.GenreFor(path));
            song.ApplyStats(_stats.GetBestForSong(song.SongIdentity));
            rows.Add(song);
        }

        return rows;
    }

    private void RebuildFromBrowser(IReadOnlyList<string>? ownChartPaths = null)
    {
        _allSongs.Clear();
        // Collapse byte-copies of the same pack (same SongKey|Charter|Version
        // under different file names) to ONE row. The browser's AllSongs stays
        // uncollapsed for LibraryDoctor; we dedupe here so the chooser doesn't
        // list a song twice.
        foreach (var item in LibraryDedupe.CollapseByIdentity(_songBrowser!.AllSongs, static i => i.Song))
        {
            if (item.IsLesson || !item.CanLoad) continue;
            var song = PlaySongItem.FromPsarc(item);
            song.ApplyStats(_stats.GetBestForSong(song.SongIdentity));
            _allSongs.Add(song);
        }

        // Own charts: every project file the player has scores for, plus the
        // most recently opened project — there is no separate chart library.
        // They are also the ONLY songs that can carry a genre, which the scan
        // has already read into _chartGenres (lookup only from here on).
        _allSongs.AddRange(BuildOwnChartRows(ownChartPaths ?? CollectOwnChartPaths()));

        // FACET OPTIONS, derived from the library — the unified browser's
        // model: every facet offers exactly the values present, with an
        // explicit unknown bucket where the data has one, and a selection
        // survives the rebuild only while its value still exists.
        BuildSongFacetOptions();

        FilterPass.RunImmediately();
        SongSelectHint = _allSongs.Count == 0
            ? _localization["PlayMode_SongSelect_Empty"]
            : "";
    }

    /// <summary>Re-derives every facet's options from <c>_allSongs</c> through the
    /// same <see cref="LibraryFacetOptions" /> the editor browser uses. Runs
    /// suspended so the option-restore churn cannot fire passes; the caller's one
    /// <c>RunImmediately</c> covers it.</summary>
    private void BuildSongFacetOptions()
    {
        var wasSuspended = _suspendFilter;
        _suspendFilter = true;
        try
        {
            foreach (var facet in SongFacets)
                facet.ResetOptions(LibraryFacetOptions.For(facet.Key, _allSongs, _localization));
        }
        finally
        {
            _suspendFilter = wasSuspended;
        }

        // A rescan can DROP a selection: BrowserFacet.ResetOptions intersects the
        // selection with the values that survived, so the only "Drop C" going away
        // silently unticks it. That path raises IsActive and NOT SelectionChanged
        // — the editor browser binds IsActive per facet and is fine, but the
        // caption funnels here are computed properties over SEVERAL facets and
        // are notified only from RefreshFunnelStates. Without this call the chip
        // disappears (chips are rebuilt from the options each pass) while the
        // funnel stays LIT, naming a filter that does not exist.
        RefreshFunnelStates();
    }

    /// <summary>A rooted, existing project file — not a library key, not the
    /// untitled placeholder.</summary>
    internal bool LooksLikeProjectPath(string? key)
        => !string.IsNullOrWhiteSpace(key) && !LibrarySongIdentity.IsLibrary(key) &&
           key != "untitled" && Path.IsPathRooted(key) && _files.FileExists(key);

    /// <summary>
    ///     Re-reads every row's stats from the ACTIVE profile's store — run after
    ///     a run records and on a profile switch.
    ///     <para>
    ///         It then runs a filter pass UNCONDITIONALLY, not only for the
    ///         stats-driven SORTS: stats decide MEMBERSHIP too — the played facet
    ///         is <c>HasBest</c>, the record ladder is <c>HasBest</c>/<c>Grade</c>/
    ///         <c>IsFullCombo</c>, and the difficulty band is the rating
    ///         <see cref="PlaySongItem.ApplyStats" /> just re-stamped. Skipping it
    ///         leaves a profile switch with "Unplayed" ticked listing the OUTGOING
    ///         player's played songs, and a finished run's song sitting in an
    ///         "Unplayed" list.
    ///     </para>
    ///     <para>
    ///         The pass is cheap enough to be unconditional: it is the same one a
    ///         keystroke runs, and <c>SyncWith</c> publishes nothing when the result is identical —
    ///         which is the common case.
    ///     </para>
    /// </summary>
    internal void RefreshSongStats()
    {
        foreach (var song in _allSongs)
            song.ApplyStats(_stats.GetBestForSong(song.SongIdentity));
        FilterPass.RunImmediately();
    }

    private void ApplyFilter()
    {
        // A bulk reset (ClearSongFilters) sets twelve observable properties in a
        // row, each of whose partial-changed hook lands here; the guard collapses
        // that to the ONE pass the caller runs afterwards. BuildSongFacetOptions
        // holds it for the same reason.
        if (_suspendFilter) return;

        // Snapshot every filter control once, then run the pure predicate over
        // the backing list. The facet selections pass BY REFERENCE (they only
        // mutate on gestures that immediately run a fresh pass), so a snapshot
        // costs nothing per keystroke.
        var criteria = BuildCriteria();

        // Tokenized (and FOLDED) ONCE, not once per song.
        var tokens = LibraryQuery.TokenizeFolded(criteria.Search);
        var view = _allSongs.Where(s => LibraryQuery.Matches(s, criteria, tokens));

        // One ordering for both song lists; see LibrarySort for the unknown-last
        // and tie rules.
        var ordered = LibrarySort.SongSelect.Order(view, SortMode, SortDescending).ToList();

        // Artist group headers and the removable filter chips are both derived
        // from what this pass just decided, so they are stamped HERE — the one
        // chokepoint every mutation already goes through. Stamping before the
        // rows are published keeps a header from flickering onto the wrong row.
        RefreshSongListChrome(criteria, ordered);

        // Differential republish: a pass that trims or restores a FEW rows (a
        // refined query, a chip) applies just those edits so the list's other
        // realized containers stand untouched; a big or reordering change still
        // lands as the ONE Reset, and an unchanged result publishes nothing.
        ((BulkObservableCollection<PlaySongItem>)SongList).SyncWith(ordered);
        SongShownCountText = ordered.Count.ToString("N0", System.Globalization.CultureInfo.CurrentCulture);
        SongTotalCountText = _allSongs.Count.ToString("N0", System.Globalization.CultureInfo.CurrentCulture);
        HasActiveSongFilters = criteria.IsNarrowing;
        // Empty-state panel: shown instead of a bare empty list; suppressed
        // while the library scan is still running.
        ShowSongSelectEmptyState = ordered.Count == 0 && !IsLibraryLoading;
        // Two DIFFERENT answers wear the same empty list: "there is nothing in
        // your library yet" (a first run — say how to point us at the songs) and
        // "your filters hid everything" (say which escape hatch to pull). The
        // view picks one panel off these; it never has to work it out itself.
        ShowSongSelectFirstRun = ShowSongSelectEmptyState && _allSongs.Count == 0;
        ShowSongSelectNoMatch = ShowSongSelectEmptyState && _allSongs.Count > 0;
    }

    /// <summary>The one place the criteria snapshot is assembled — ApplyFilter
    /// and the facet-count refresh both go through it, so the counts can never
    /// disagree with the list.</summary>
    private LibraryFilter BuildCriteria() => LibraryFilter.FromFacets(
        SearchText, SongFacets.Select(facet => (facet.Key, (IReadOnlyCollection<string>)facet.Selected)), RecordFilter);

    /// <summary>
    ///     Refreshes one facet's per-option match counts — called when its
    ///     popover OPENS (never per pass): each option counts the rows that
    ///     pass the search and every OTHER filter (<see cref="LibraryQuery.CountFacet" />).
    /// </summary>
    public void RefreshSongFacetCounts(BrowserFacet facet)
        => facet.ApplyCounts(LibraryQuery.CountFacet(_allSongs, BuildCriteria(), facet.Key));

    /// <summary>Songs currently listed, and the library total, as separate
    /// formatted figures — the Song Select header prints them as "N of M songs"
    /// and gives the two numbers different weight.</summary>
    [ObservableProperty] private string _songShownCountText = "0";

    /// <inheritdoc cref="SongShownCountText" />
    [ObservableProperty] private string _songTotalCountText = "0";

    /// <summary>At least one Song Select filter (or the search box) is narrowing
    /// the list — gates the "clear filters" affordance.</summary>
    [ObservableProperty] private bool _hasActiveSongFilters;

    /// <summary>Empty list because the LIBRARY is empty (first run) — the panel
    /// that explains how to point DotCharter at a song folder. Defaults TRUE
    /// because that is the honest state of a freshly-constructed view model:
    /// no songs, no scan yet. Nothing recomputes these until the first
    /// <see cref="ApplyFilter" /> or scan, so a false default left the very
    /// screen this panel exists for showing a blank column.</summary>
    [ObservableProperty] private bool _showSongSelectFirstRun = true;

    /// <summary>Empty list because the FILTERS hid everything — the panel that
    /// offers to clear them.</summary>
    [ObservableProperty] private bool _showSongSelectNoMatch;

    /// <summary>
    ///     Empties the SEARCH BOX and nothing else — what the ✕ inside the search
    ///     field does. Clearing the other filters too would bring the list back
    ///     WIDER than the user left it, under a button labelled "clear search".
    ///     <para>
    ///         No explicit pass: writing <see cref="SearchText" /> lands in
    ///         <c>OnSearchTextChanged</c>, which requests the same
    ///         coalesced pass a keystroke does. Running one here as well would
    ///         filter the whole library twice for one click.
    ///     </para>
    /// </summary>
    [RelayCommand]
    private void ClearSearchText() => SearchText = "";

    /// <summary>Reset every Song Select filter and the search box in one step
    /// (the "clear filters" chip and the no-match empty state both call it).</summary>
    [RelayCommand]
    private void ClearSongFilters() => ResetSuspended(() =>
    {
        SearchText = "";
        foreach (var facet in SongFacets) facet.Clear();
        RecordFilter = 0;
    });

    /// <summary>Test seam: production fills <c>_allSongs</c> from the PSARC
    /// browser (<see cref="RebuildFromBrowser" />); a test swaps
    /// in a fixed library and runs the same filter+sort pipeline the UI does.</summary>
    internal void SetLibraryForTest(IEnumerable<PlaySongItem> songs)
    {
        _allSongs.Clear();
        _allSongs.AddRange(songs);
        // The facet options are derived from the library, so a seam that swaps
        // the library and skipped this would leave them describing the PREVIOUS
        // one — the same drift the production rebuild avoids.
        BuildSongFacetOptions();
        FilterPass.RunImmediately();
    }

    /// <summary>Test seam: the own-chart half of a scan — the SAME
    /// <see cref="WarmOwnChartGenresAsync" /> + <see cref="BuildOwnChartRows" />
    /// pair <see cref="RebuildFromBrowserAsync" /> runs, without a PSARC browser
    /// to stand up.</summary>
    internal async Task<List<PlaySongItem>> ScanOwnChartsForTest(IReadOnlyList<string> ownPaths)
    {
        await WarmOwnChartGenresAsync(ownPaths);
        return BuildOwnChartRows(ownPaths);
    }

    /// <summary>Whether Song Select shows an empty state at all (library empty
    /// or every song filtered out) — maintained by <see cref="ApplyFilter" />
    /// and re-evaluated when a library scan finishes (the scan's ApplyFilter
    /// runs while <see cref="IsLibraryLoading" /> is still true). Which of the
    /// two panels shows is <see cref="ShowSongSelectFirstRun" /> /
    /// <see cref="ShowSongSelectNoMatch" />; this stays the umbrella flag.
    /// Defaults TRUE for the same reason they do.</summary>
    [ObservableProperty] private bool _showSongSelectEmptyState = true;

    partial void OnIsLibraryLoadingChanged(bool value)
    {
        ShowSongSelectEmptyState = !value && SongList.Count == 0;
        // Keep the two SPECIFIC empty states in step with the general one — they
        // are what the view actually binds, so leaving them stale here would end
        // a finished scan on a blank panel instead of the first-run invitation.
        ShowSongSelectFirstRun = ShowSongSelectEmptyState && _allSongs.Count == 0;
        ShowSongSelectNoMatch = ShowSongSelectEmptyState && _allSongs.Count > 0;
        LoadingChanged?.Invoke();
    }

    // ---- The removable filter chips and the artist groups --------------------

    /// <summary>The active filters as removable chips — one chip per selected
    /// FACET OPTION (multi-select: three tunings ticked = three chips) plus the
    /// search and record specials, rebuilt by every <see cref="ApplyFilter" />
    /// pass so a chip can never claim a filter the list is not applying. Same
    /// <see cref="BrowserFilterChip" /> shape as the editor browser.</summary>
    public ObservableCollection<BrowserFilterChip> ActiveFilterChips { get; } = [];

    /// <summary>The artist runs of the visible list, as real group objects
    /// (<see cref="SongArtistGrouping.Build" />) rather than a decoration. The
    /// list itself stays FLAT — one <c>ListBox</c>, one selection model, one
    /// virtualized panel — and each run's header is stamped onto its first row;
    /// this collection is the same grouping as data, so the headers, the counts
    /// and anything else that needs the runs read one answer.</summary>
    public ObservableCollection<SongArtistGroup> SongGroups { get; } = [];

    /// <summary>Clears exactly the one filter a chip stands for: a facet chip
    /// deselects its one VALUE (the rest of that facet's selection stays), the
    /// specials reset their own control.</summary>
    [RelayCommand]
    private void RemoveSongFilter(BrowserFilterChip? chip)
    {
        if (chip == null) return;
        switch (chip.Kind)
        {
            case BrowserFilterChips.SearchKind:
                // The setter only REQUESTS a coalesced pass (it is tuned for
                // typing); a chip's ✕ is a discrete gesture, so run now — the
                // other cases run immediately through their own change hooks.
                SearchText = "";
                FilterPass.RunImmediately();
                break;
            case "record": RecordFilter = 0; break;
            default:
                SongFacets.FirstOrDefault(f => f.Key == chip.Kind)?.Deselect(chip.Value);
                break;
        }
    }

    /// <summary>Rebuilds <see cref="ActiveFilterChips" /> and stamps the artist
    /// group headers. Called from the single <see cref="ApplyFilter" /> chokepoint
    /// with the criteria it already built, so neither can drift from the list.</summary>
    private void RefreshSongListChrome(in LibraryFilter criteria, IReadOnlyList<PlaySongItem> ordered)
    {
        // Artist headers only make sense while the list is ORDERED by artist —
        // sort mode 0. Any other sort clears them rather than scattering
        // one-song "groups" down the list.
        var grouped = SortMode == 0;
        SongArtistGrouping.Stamp(ordered, grouped,
            (_, count) => _localization.Plural("PlayMode_SongSelect_GroupCountOne", "PlayMode_SongSelect_GroupCountFmt", count),
            _localization);

        SongGroups.Clear();
        if (grouped)
        {
            foreach (var group in SongArtistGrouping.Build(ordered, _localization))
                SongGroups.Add(group);
        }

        ActiveFilterChips.Clear();
        var search = criteria.Search;
        if (search.Length > 0)
            ActiveFilterChips.Add(new BrowserFilterChip(BrowserFilterChips.SearchKind, search, search));
        foreach (var facet in SongFacets)
        {
            foreach (var option in facet.Options)
            {
                if (option.IsChecked)
                    ActiveFilterChips.Add(new BrowserFilterChip(facet.Key, option.Value, option.Label));
            }
        }

        if (criteria.Record > 0)
            ActiveFilterChips.Add(new BrowserFilterChip("record", "", RecordOptions[criteria.Record]));
    }
}
