using System.Collections.ObjectModel;
using System.Globalization;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using DotCharter.Presentation.Dialogs;
using DotCharter.Presentation.Shared.Library;
using DotCharter.Services.Library.Prefetch;
using DotCharter.Tone.UI.ViewModels.Shell;
using DotCharter.UI.ViewModels.Editor;
using DotCharter.UI.ViewModels.PlayMode;
using DotCharter.Services.Session;
using DotCharter.Services.Library;

namespace DotCharter.UI.ViewModels.Panels;

public sealed partial class SongBrowserViewModel : ViewModelBase, IDisposable
{
    private readonly List<PsarcSongListItem> _allSongs = [];

    /// <summary>Memoised <see cref="CollapsedSongs" />; null = stale.</summary>
    private IReadOnlyList<PsarcSongListItem>? _collapsedSongs;

    private readonly ChartEditorViewModel _chartEditor;
    private readonly IConfigurationService _configService;
    private readonly ILibraryService _libraryService;
    private readonly ILibraryAudioPrefetcher? _audioPrefetcher;
    private readonly ILibrarySongPlayability? _playability;
    private readonly IAppLogger _logger;
    private readonly ILocalizationService _localization;

    /// <summary>The strings this browser was built with, for the Library screen that presents it.</summary>
    internal ILocalizationService Localization => _localization;

    /// <summary>The dialog host the Library Doctor opens on.</summary>
    private readonly DotCharter.Presentation.Dialogs.IDialogs _dialogs;
    private readonly IProjectSessionService _projectSession;
    private readonly ProjectState _projectState;
    private readonly IFileStorageService _storageService;
    private readonly TransportViewModel _transport;
    private int _coverLoadRevision;

    [ObservableProperty] private bool _isScanning;
    [ObservableProperty] private double _scanProgress;
    [ObservableProperty] private string _psarcLocation = "";
    [ObservableProperty] private string _searchText = "";
    [ObservableProperty] private PsarcSongListItem? _selectedSong;
    [ObservableProperty] private string _statusText = "";

    private CancellationTokenSource? _scanCts;

    /// <summary>Bumped (UI thread) by every <see cref="Scan"/>. A superseded scan
    /// keeps running until its await unwinds, so its catch/finally and its
    /// <see cref="Progress{T}"/> callbacks all still fire AFTER the new scan
    /// started — each one is gated on the generation it captured so it can never
    /// touch the live scan's status, progress or song list.</summary>
    private int _scanGeneration;

    /// <summary>The folder <c>_allSongs</c> came from: a scan of the SAME folder is a Refresh, merged in place.</summary>
    private string _listedFolder = "";

    /// <summary>The folder a scan is reading right now; another Refresh of it is ignored, not stacked.</summary>
    private string? _scanningFolder;

    /// <summary>What the last Refresh of the same folder changed ("Refreshed · 3 new, 1 removed"); see <see cref="LibraryRescanResult.Describe" />.</summary>
    [ObservableProperty] private string _refreshResultText = "";

    // ── Column facets (the datagrid filters) ───────────────────────────────
    // One BrowserFacet per filterable column, opened from the caption's funnel.
    // Selection changes run the pass; options are rebuilt from the scan
    // (BuildFacetOptions). The predicate reads Selected — O(1) per row.

    public BrowserFacet TuningFacet { get; }
    public BrowserFacet CapoFacet { get; }
    public BrowserFacet PathFacet { get; }
    public BrowserFacet YearFacet { get; }
    public BrowserFacet DifficultyFacet { get; }
    public BrowserFacet LengthFacet { get; }
    public BrowserFacet StatusFacet { get; }
    public BrowserFacet OriginFacet { get; }

    /// <summary>Every facet, in the strip's chip order (= column order).</summary>
    public IReadOnlyList<BrowserFacet> Facets { get; }

    // SortMode: 0 = Artist, 1 = Title, 2 = Year, 3 = Length, 4 = Pack/file,
    // 5 = Difficulty, 6 = Tuning (LibrarySort.Library maps the header's columns).
    [ObservableProperty] private int _sortMode;

    /// <summary>Flips the active sort's natural direction.</summary>
    [ObservableProperty] private bool _sortDescending;

    // A stock package can carry ~150 lesson/exercise entries (GE_*)
    // between the actual songs; they have no audio in the
    // archive and drown the list, so they're hidden by default.
    [ObservableProperty] private bool _hideLessons = true;

    /// <summary>"{shown} of {total}" summary under the list.</summary>
    [ObservableProperty] private string _resultCountText = "";

    // ── Active-filter chips ─────────────────────────────────────────
    // The same model Play Mode's Song Select carries: what is
    // narrowing the list, said out loud, each entry removable on its own. Built
    // by the pure BrowserFilterChips and refreshed from the ONE ApplyFilter
    // chokepoint, so a chip can never claim a filter the list is not applying.

    /// <summary>The filters currently narrowing the list, one removable chip
    /// each. Rebuilt inside <see cref="ApplyFilter" /> — never from a setter.</summary>
    public BulkObservableCollection<BrowserFilterChip> ActiveFilterChips { get; } = [];

    /// <summary>At least one filter (or the search box) is narrowing the list —
    /// gates the chips row and its "clear filters" button. Recomputed each pass
    /// as the chip COUNT, so the affordance and the chips cannot disagree.</summary>
    [ObservableProperty] private bool _hasActiveFilters;

    /// <summary>A bulk reset (<see cref="ClearFilters" />,
    /// <see cref="RemoveBrowserFilter" />) writes several observable properties in
    /// a row, each of whose partial hook would run its own pass; the guard
    /// collapses that to the ONE pass the caller runs afterwards.</summary>
    private bool _suspendFilter;

    // ── The detail pane (0.49) ─────────────────────────────────────────────
    // Everything here is derived from the SELECTED row's library entry. There is
    // no second data source and nothing is fabricated: an archive that carries
    // no note counts shows paths without numbers, and a file whose size cannot
    // be read shows no size rather than a zero.

    /// <summary>The selected archive's paths, each with its real note count.</summary>
    public BulkObservableCollection<PsarcArrangementChoice> ArrangementChoices { get; } = [];

    /// <summary>Which path the editor opens on after the load.</summary>
    [ObservableProperty] private PsarcArrangementChoice? _selectedArrangement;

    /// <summary>"18.4 MB" — how big the archive is on disk. The Library lists the
    /// arrangements as its path rows, so this line does not repeat them. The size is
    /// read once per selection; a path that cannot be stat'd contributes nothing instead of a 0.</summary>
    [ObservableProperty] private string _archiveDetailText = "";

    /// <summary>The footer's readiness line: which chart and which path pressing
    /// Load would open, or the reason it cannot.</summary>
    [ObservableProperty] private string _loadStatusText = "";

    public bool HasSelection => SelectedSong != null;

    /// <summary>The footer's readiness dot. Same predicate as the Load command's
    /// CanExecute, exposed as a property because a command's CanExecute is a
    /// METHOD and binding to it silently produces nothing.</summary>
    public bool IsSelectionLoadable => SelectedSong is { CanLoad: true };

    /// <summary>
    ///     LOAD CHART's tooltip: what it does when it is live, WHY it is off
    ///     when it is not (a bright primary that did nothing on
    ///     an empty library and never said so would be a trap). An Avalonia tooltip never
    ///     shows on a disabled control, so the view hangs this on a wrapper.
    /// </summary>
    public string LoadChartTip => IsSelectionLoadable
        ? _localization["SongBrowser_LoadChart_Tip"]
        : SelectedSong?.Block == LibrarySongBlock.AudioDecoderMissing
            ? _localization["SongBrowser_LoadChart_NeedsDecoder_Tip"]
            : _localization["SongBrowser_LoadChart_NoSelection_Tip"];

    /// <summary>A row for <paramref name="entry" />, told whether this computer can play it.</summary>
    private PsarcSongListItem NewItem(LibrarySongEntry entry) =>
        new(entry, _localization, BlockFor(entry));

    private LibrarySongBlock BlockFor(LibrarySongEntry entry) => _playability?.BlockFor(entry) ?? LibrarySongBlock.None;

    // ── Facet options, built from the scanned library ──────────────────────

    /// <summary>Re-derives every facet's option list from the master list
    /// (<see cref="LibraryFacetOptions" />): only values the library holds, the
    /// unknown bucket only where a row has one. A selection survives a rescan
    /// when its value still exists (a rescan must not silently widen the filter).</summary>
    private void BuildFacetOptions()
    {
        foreach (var facet in Facets)
            facet.ResetOptions(LibraryFacetOptions.For(facet.Key, _allSongs, _localization));
    }

    /// <summary>Rebuilds the detail pane for the current selection.</summary>
    private void RefreshSelectionDetail()
    {
        OnPropertyChanged(nameof(HasSelection));
        OnPropertyChanged(nameof(IsSelectionLoadable));
        OnPropertyChanged(nameof(LoadChartTip));

        var song = SelectedSong;
        if (song == null)
        {
            ArrangementChoices.ResetWith([]);
            ArchiveDetailText = "";
            LoadStatusText = "";
            SelectedArrangement = null;
            return;
        }

        // Several arrangements can share one PATH label (alternate cuts of the
        // same route); the pane lists paths, so the first one's count is the one
        // shown — the same collapse LibrarySongEntry.ArrangementSummary makes.
        // ONE change for the paths: a Clear plus an Add per path rebuilt the rail's list per event.
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        ArrangementChoices.ResetWith([.. song.Song.Arrangements
            .Where(entry => entry.PathLabel.Length > 0 && seen.Add(entry.PathLabel))
            .Select(entry => new PsarcArrangementChoice(_localization, entry.PathLabel, entry.ArrangementName, entry.NoteCount, entry.Tuning, entry.Capo))]);

        SelectedArrangement = ArrangementChoices.FirstOrDefault();
        ArchiveDetailText = "";
        _ = LoadArchiveDetailAsync(song);
        RefreshLoadStatus();
    }

    /// <summary>The archive's size is a disk read (the library is often on a second drive, asleep):
    /// off the UI thread, and dropped when the selection moved on meanwhile.</summary>
    private async Task LoadArchiveDetailAsync(PsarcSongListItem song)
    {
        var text = await Task.Run(() => BuildArchiveDetail(song));
        if (ReferenceEquals(SelectedSong, song)) ArchiveDetailText = text;
    }

    /// <summary>How big the archive is on disk, or "" when that is unknown.</summary>
    private static string BuildArchiveDetail(PsarcSongListItem song)
    {
        try
        {
            var info = new FileInfo(song.Song.PackagePath);
            if (info.Exists && info.Length > 0)
                return $"{info.Length / (1024.0 * 1024.0):0.#} MB";
        }
        catch
        {
            // An unreadable/removed archive contributes no size line. The row
            // itself already reports whether the song can be loaded.
        }

        return "";
    }

    private void RefreshLoadStatus()
    {
        var song = SelectedSong;
        if (song == null)
        {
            LoadStatusText = "";
            return;
        }

        var loc = _localization;
        if (!song.CanLoad)
        {
            LoadStatusText = loc.Format("SongBrowser_Load_Unavailable", song.Title);
            return;
        }

        var path = SelectedArrangement?.Label ?? "";
        LoadStatusText = path.Length == 0
            ? loc.Format("SongBrowser_Load_ReadyNoPath", song.Title)
            : loc.Format("SongBrowser_Load_Ready", song.Title, path);
    }

    partial void OnSelectedArrangementChanged(PsarcArrangementChoice? value)
    {
        _ = value;
        RefreshLoadStatus();
    }

    /// <summary>Which <see cref="Arrangement" /> a chosen PATH refers to inside a
    /// freshly loaded project. Pure + static so the mapping is testable without a
    /// library: name first (the manifest name is what the choice carries), and
    /// nothing at all when it does not resolve — the caller then leaves the
    /// editor's own default first arrangement in place rather than guessing.</summary>
    public static Arrangement? ResolvePreferredArrangement(
        IReadOnlyList<Arrangement> arrangements, string? arrangementName)
    {
        if (arrangements.Count == 0 || string.IsNullOrWhiteSpace(arrangementName)) return null;
        return arrangements.FirstOrDefault(a =>
            string.Equals(a.Name, arrangementName, StringComparison.OrdinalIgnoreCase));
    }

    public SongBrowserViewModel(
        IFileStorageService storageService,
        IConfigurationService configService,
        ILibraryService libraryService,
        IAppLogger logger,
        IProjectSessionService projectSession,
        ProjectState projectState,
        ChartEditorViewModel chartEditor,
        TransportViewModel transport,
        DotCharter.Presentation.Dialogs.IDialogs dialogs,
        ILocalizationService localization,
        Action<Action>? filterPassScheduler = null,
        ILibraryAudioPrefetcher? audioPrefetcher = null,
        ILibrarySongPlayability? playability = null)
    {
        _audioPrefetcher = audioPrefetcher;
        _playability = playability;
        FilterPass = new CoalescedFilterPass(ApplyFilter, filterPassScheduler);
        _storageService = storageService;
        _configService = configService;
        _libraryService = libraryService;
        _logger = logger;
        _projectSession = projectSession;
        _projectState = projectState;
        _chartEditor = chartEditor;
        _transport = transport;
        _dialogs = dialogs;
        _localization = localization;
        Songs.CollectionChanged += (_, _) => OnPropertyChanged(nameof(IsEmptyStateVisible));

        // The column facets, titled with their captions' words. Every selection
        // gesture is discrete (a checkbox, a chip's ✕, a clear), so it runs the
        // pass at once — chips are refreshed inside the pass.
        TuningFacet = NewFacet(LibraryFacets.Tuning);
        CapoFacet = NewFacet(LibraryFacets.Capo);
        PathFacet = NewFacet(LibraryFacets.Path);
        YearFacet = NewFacet(LibraryFacets.Year);
        DifficultyFacet = NewFacet(LibraryFacets.Difficulty);
        LengthFacet = NewFacet(LibraryFacets.Length);
        StatusFacet = NewFacet(LibraryFacets.Status);
        OriginFacet = NewFacet(LibraryFacets.Origin);
        Facets = [TuningFacet, CapoFacet, PathFacet, YearFacet, DifficultyFacet, LengthFacet, StatusFacet, OriginFacet];
        foreach (var facet in Facets) facet.SelectionChanged += RunFilterNow;

        // Seed the facet option lists (empty until a scan, but the popovers
        // must exist before one).
        BuildFacetOptions();
    }

    private BrowserFacet NewFacet(string key) => new(key, _localization[LibraryFacets.TitleKey(key)]);

    private bool CanRunLibraryDoctor() => !IsScanning && _allSongs.Count > 0;

    /// <summary>The Library Doctor over the current scan result, on the dialog host (read-only, never mutates).</summary>
    [RelayCommand(CanExecute = nameof(CanRunLibraryDoctor))]
    private void RunLibraryDoctor()
    {
        var request = new DotCharter.Presentation.Contracts.Dialogs.LibraryDoctorRequest(
            [.. _allSongs.Select(item => item.Song)]);
        try
        {
            _dialogs.ShowAsync(request).Observe(_logger, "library doctor");
        }
        catch (InvalidOperationException ex)
        {
            // No host attached (a window that is not shown): nothing can open, and that is logged.
            _logger.Error(AppLogCategory.Import, "The library doctor dialog could not open.", ex);
        }
    }

    public ObservableCollection<PsarcSongListItem> Songs { get; } = new BulkObservableCollection<PsarcSongListItem>();

    /// <summary>Unfiltered master list (Play Mode's song select builds its own
    /// view over it instead of re-scanning the library).</summary>
    public IReadOnlyList<PsarcSongListItem> AllSongs => _allSongs;

    // Empty-state panel over the song list: visible until a scan has
    // produced results, so a fresh browser explains the next step.
    public bool IsEmptyStateVisible => !IsScanning && Songs.Count == 0;

    partial void OnIsScanningChanged(bool value)
    {
        _ = value;
        OnPropertyChanged(nameof(IsEmptyStateVisible));
        RunLibraryDoctorCommand.NotifyCanExecuteChanged();
    }

    public event Action? SongLoaded;

    // No [RelayCommand]: the qualified reachability pass would find the
    // generated ActivateCommand bound by nothing — it would read as
    // reachable only through a retired window's same-named
    // command. The method is live: MainWindowViewModel.OpenSongBrowser calls it
    // directly when the LIBRARY tab is already current.
    public async Task Activate()
    {
        var config = _configService.LoadAppConfig();
        PsarcLocation = config.PsarcLibraryPath;
        StatusText = "";

        if (!string.IsNullOrWhiteSpace(PsarcLocation) && _allSongs.Count == 0)
            await Scan();
    }

    [RelayCommand]
    private async Task Browse()
    {
        var folder =
            await _storageService.OpenFolderPickerAsync(_localization["Picker_SelectPsarcFolder"],
                PsarcLocation);
        if (string.IsNullOrWhiteSpace(folder)) return;

        PsarcLocation = folder;
        var config = _configService.LoadAppConfig();
        config.PsarcLibraryPath = folder;
        _configService.SaveAppConfig(config);

        await Scan();
    }

    [RelayCommand]
    private async Task Scan()
    {
        if (string.IsNullOrWhiteSpace(PsarcLocation)) return;

        // A Refresh while this folder is already being read asks for what is coming anyway: the owner's
        // log had 23 scans of one folder in 5 s. A different folder (Browse) supersedes it below.
        if (_scanningFolder is { } running && SameFolder(running, PsarcLocation)) return;

        // A rescan of the listed folder keeps the list, the selection, its path and the covers, and
        // merges the result in place; only a new folder starts from an empty list.
        var refresh = _allSongs.Count > 0 && SameFolder(_listedFolder, PsarcLocation);

        // Cancel any prior scan still in flight (e.g. user clicked Browse
        // during a long network-share scan). Do NOT dispose its CTS here — the
        // superseded call is still inside ScanAsync and holds the token; it
        // disposes its own CTS in its finally.
        var generation = ++_scanGeneration;
        _scanCts?.Cancel();
        var cts = new CancellationTokenSource();
        _scanCts = cts;
        var ct = cts.Token;
        _scanningFolder = PsarcLocation;

        IsScanning = true;
        ScanProgress = 0;
        StatusText = _localization["Status_PsarcScanning"];
        RefreshResultText = "";

        if (!refresh)
        {
            foreach (var song in _allSongs) song.DisposeCoverImage();

            Songs.Clear();
            _allSongs.Clear();
            InvalidateCollapsedSongs();
            // A pass queued by the search box would run against the list this scan is
            // about to replace, and the scan publishes its own result.
            FilterPass.Cancel();
            SelectedSong = null;
            _listedFolder = PsarcLocation;
        }

        // IProgress<T> dispatches via the SynchronizationContext captured
        // here on the UI thread, so the lambda runs on the UI thread without
        // an explicit Dispatcher hop.
        var progress = new Progress<LibraryScanProgress>(p =>
        {
            if (generation != _scanGeneration) return; // superseded scan's tail
            ScanProgress = p.Fraction;
            StatusText = _localization.Format(
                "Status_PsarcScanProgress",
                p.ProcessedFiles, p.TotalFiles, p.SongsFound);
        });

        // Streaming song-found callback: a FIRST scan appends each freshly parsed PSARC's songs to
        // the visible list as they come (never ApplyFilter per batch — a Reset per batch rebuilds
        // every visible container). A Refresh streams nothing: the list it already shows stays put
        // and the result is merged once, at the end.
        var songFound = new Progress<IReadOnlyList<LibrarySongEntry>>(batch =>
        {
            if (batch.Count == 0 || refresh) return;
            if (generation != _scanGeneration) return; // would append into the new scan's list
            var filter = CurrentFilter();
            var searchTokens = LibraryQuery.TokenizeFolded(filter.Search);
            foreach (var entry in batch)
            {
                var item = NewItem(entry);
                _allSongs.Add(item);
                if (LibraryQuery.Matches(item, filter, searchTokens))
                    Songs.Add(item);
            }

            InvalidateCollapsedSongs();
            SelectedSong ??= Songs.FirstOrDefault();
        });

        try
        {
            _logger.Info(AppLogCategory.Import, $"PSARC library scan started. Root='{PsarcLocation}', refresh={refresh}.");
            var songs = await _libraryService.ScanAsync(PsarcLocation, progress, songFound, ct);

            // A newer scan took over while we awaited (Browse/SelectFolder call
            // Scan() directly, bypassing the command's no-concurrency guard).
            // Everything below rebuilds _allSongs and the status — it belongs to
            // the live scan now, not to us.
            if (generation != _scanGeneration) return;

            // Into the scan's order, keeping every listed item whose entry is unchanged — and with
            // it its row, its selection and its decoded cover (LibraryRescan.Merge).
            var merge = LibraryRescan.Merge(_allSongs, songs, _localization, BlockFor);
            _allSongs.Clear();
            _allSongs.AddRange(merge.Songs);
            InvalidateCollapsedSongs();

            // The tuning/path dropdowns are derived from what the scan FOUND,
            // so they can never offer a filter the library cannot satisfy.
            BuildFacetOptions();
            FollowReplacedSelection(merge);
            merge.RemoveLeavingFrom(Songs);
            FilterPass.RunImmediately();
            foreach (var gone in merge.Removed.Concat(merge.Replaced.Keys))
                if (!ReferenceEquals(gone, SelectedSong)) gone.DisposeCoverImage();

            // A FIRST scan leaves no resting status line (the count readout is the
            // fact); a Refresh says what it changed, even when that is nothing.
            StatusText = "";
            if (refresh) RefreshResultText = merge.Describe(_localization);
            _logger.Info(AppLogCategory.Import,
                $"PSARC library scan completed. Root='{PsarcLocation}', songs={_allSongs.Count}, added={merge.Added}, removed={merge.Removed.Count}, updated={merge.Replaced.Count}.");
        }
        catch (OperationCanceledException)
        {
            _logger.Info(AppLogCategory.Import, $"PSARC library scan cancelled. Root='{PsarcLocation}'.");
            if (generation == _scanGeneration)
                StatusText = _localization["Status_PsarcScanCancelled"];
        }
        catch (Exception ex)
        {
            _logger.Error(AppLogCategory.Import, $"PSARC library scan failed. Root='{PsarcLocation}'.", ex);
            if (generation == _scanGeneration)
                StatusText = _localization.Format("Status_PsarcScanFailed", ex.Message);
        }
        finally
        {
            // A superseded scan must not clear IsScanning / reset the progress bar
            // out from under the scan that replaced it — nor drop the live scan's
            // CTS (CancelScan would then have nothing to cancel).
            if (generation == _scanGeneration)
            {
                IsScanning = false;
                ScanProgress = 0;
                _scanCts = null; // cleared before Dispose: CancelScan must never see a disposed CTS
                _scanningFolder = null;
            }

            cts.Dispose(); // ours alone; a superseding scan never disposes it
        }
    }

    private static bool SameFolder(string a, string b) =>
        string.Equals(Path.TrimEndingDirectorySeparator(a.Trim()), Path.TrimEndingDirectorySeparator(b.Trim()), StringComparison.OrdinalIgnoreCase);

    /// <summary>A selected song whose entry changed is now a new item: select that one, on the same path.</summary>
    private void FollowReplacedSelection(LibraryRescanResult merge)
    {
        if (SelectedSong is not { } selected || !merge.Replaced.TryGetValue(selected, out var fresh)) return;

        var path = SelectedArrangement?.ArrangementName;
        SelectedSong = fresh;
        SelectedArrangement = ArrangementChoices.FirstOrDefault(c => c.ArrangementName == path) ?? SelectedArrangement;
    }

    [RelayCommand]
    private void CancelScan() => _scanCts?.Cancel();

    private bool CanLoadSelected() => SelectedSong is { CanLoad: true };

    partial void OnSelectedSongChanged(PsarcSongListItem? value)
    {
        _ = value;
        LoadSelectedCommand.NotifyCanExecuteChanged();
        RefreshSelectionDetail();
        PrefetchAudio(value);
    }

    /// <summary>Convert-on-select: once the selection settles, the song's
    /// audio is prepared in the background. Play Mode's song list feeds it too.</summary>
    public void PrefetchAudio(PsarcSongListItem? item) =>
        _audioPrefetcher?.SelectionChanged(item is { CanLoad: true } ? item.Song : null);

    [RelayCommand(CanExecute = nameof(CanLoadSelected))]
    private async Task LoadSelected()
    {
        if (SelectedSong == null) return;
        await LoadSongItem(SelectedSong, preferredArrangementName: SelectedArrangement?.ArrangementName);
    }

    /// <summary>Loads a PSARC song into the editor state. Public so Play Mode
    /// can reuse the exact same load path (chart, audio, song identity)
    /// without going through the browser's selection.</summary>
    public async Task<bool> LoadSongItem(PsarcSongListItem item, bool raiseSongLoaded = true,
        string? preferredArrangementName = null)
    {
        if (!item.CanLoad) return false;

        // Loading a library song REPLACES the open project:
        // ApplyProject(resetHistory: true) below clears the undo history, so the
        // previous project's unsaved edits become unreachable the instant it
        // runs. Guarded HERE rather than in the browser's command, because Play
        // Mode's song select reuses this exact method.
        if (!await _projectSession.MayReplaceOpenProjectAsync())
        {
            _logger.Info(AppLogCategory.Import,
                $"PSARC song load cancelled — the user kept the unsaved project. Title='{item.Title}'.");
            return false;
        }

        var loaded = false;
        // Whether the session was actually REPLACED. A failure before
        // ApplyProject leaves the previous project open with its own edits, and
        // the finally below must not speak for those.
        var sessionReplaced = false;

        IsScanning = true;
        StatusText = _localization.Format("Status_PsarcLoadingSong", item.Title);

        try
        {
            _logger.Info(AppLogCategory.Import,
                $"PSARC song load requested. Psarc='{item.Song.PackagePath}', title='{item.Title}', arrangements={item.ArrangementSummary}.");

            var clock = System.Diagnostics.Stopwatch.StartNew();
            var loadedSong = await _libraryService.LoadSongAsync(item.Song);
            var project = loadedSong.Project;

            // On the worker, exactly as ProjectSessionService.LoadFile does it:
            // the pass is CPU-bound over the whole project and nothing has been
            // applied yet, so no other thread can see this graph. Run inline it
            // is hundreds of milliseconds on a dense multi-arrangement song,
            // landing on the UI thread between the click and the first frame of
            // the newly opened chart. Settings ▸ Files' hardest-level-only
            // and the spacing pass, one arrangement per pool thread.
            var config = _configService.LoadAppConfig();
            var parsedAt = clock.Elapsed.TotalMilliseconds;
            await Task.Run(() => LibraryLevelImport.PrepareForEditor(project, config));
            var preparedAt = clock.Elapsed.TotalMilliseconds;
            _chartEditor.ApplyProject(project, resetHistory: true);
            sessionReplaced = true;

            // The detail pane's path pick is what the editor opens on. ApplyProject
            // has already parked us on the FIRST arrangement, so this only moves
            // when the pick resolves — an unresolvable name leaves that default
            // alone rather than guessing at an index.
            var preferred = ResolvePreferredArrangement(project.Arrangements, preferredArrangementName);
            if (preferred != null) _projectState.ActiveArrangement = preferred;

            _transport.Bpm = project.Bpm;
            _projectState.CurrentProjectPath = "";
            _projectState.CurrentSongIdentity = string.IsNullOrWhiteSpace(item.Song.SongKey)
                ? ""
                : LibrarySongIdentity.For(item.Song.SongKey);

            var appliedAt = clock.Elapsed.TotalMilliseconds;
            if (loadedSong.HasPlayableAudio)
            {
                // Use the absolute playable path so downstream services (stem
                // separation, audio reload after device-change) can locate the
                // file without needing a project directory. CurrentAudioFileName
                // accepts rooted paths � ResolveCurrentAudioPath and the stem
                // separation guard both fast-path Path.IsPathRooted.
                _projectState.CurrentAudioFileName = loadedSong.PlayableAudioPath;
                await _projectSession.LoadAudioFile(loadedSong.PlayableAudioPath, false, preferMemoryPlayback: true);
            }
            else
            {
                // The engine still holds the PREVIOUS song's source. Clearing the
                // state below only changes what the UI *says* is loaded — Play()
                // would keep playing the old audio underneath this song's chart
                // (and Live Play would score against it). Free it first.
                _projectSession.FreeAudio();

                _projectState.CurrentAudioFileName = "";
                // Audio-less library song (e.g. a chart-only import whose
                // audio lives in another package): drive a silent transport of the
                // chart's length so the playhead / scrubbing / count-in / Play
                // Mode still work. The silent WAV is loaded into the engine only,
                // never as the song identity, so stem separation stays disabled.
                var contentMs = GetProjectEndMs(project);
                if (contentMs > 0)
                {
                    await _projectSession.LoadSilentTransportAsync(contentMs);
                }
                else
                {
                    _projectState.TotalAudioLengthMs = contentMs;
                    _projectState.WaveformData = WaveformSamples.Empty;
                    _transport.SeekTo(0);
                }

                if (loadedSong.AudioExtracted)
                {
                    StatusText = _localization.Format("Status_PsarcAudioNeedsConverter",
                        loadedSong.ExtractedAudioPath);
                }
                else
                {
                    StatusText = _localization.Format("Status_PsarcNoAudio", item.Title);
                }

                _logger.Warning(AppLogCategory.Import,
                    $"PSARC song has no playable audio; playing silently. Title='{item.Title}', psarc='{item.Song.PackagePath}'.");
            }

            _chartEditor.RequestRedraw();
            _projectState.MainDisplayMessage =
                _localization.Format("Status_PsarcLoadedSong", item.Title);

            _logger.Info(AppLogCategory.Import,
                $"PSARC song loaded. Title='{project.Chart.SongName}', arrangements={project.Arrangements.Count}, beats={project.Beats.Count}, audioEntry='{loadedSong.AudioEntryPath}', playableAudio='{loadedSong.PlayableAudioPath}'. " +
                // where the open went — load (chart parse beside the audio extract,
                // worker), prepare (worker), apply (UI thread), audio (engine load).
                string.Create(CultureInfo.InvariantCulture,
                    $"ms: load={parsedAt:0} prepare={preparedAt - parsedAt:0} apply={appliedAt - preparedAt:0} audio={clock.Elapsed.TotalMilliseconds - appliedAt:0}."));

            loaded = true;
            if (raiseSongLoaded) SongLoaded?.Invoke();
        }
        catch (Exception ex)
        {
            _logger.Error(AppLogCategory.Import,
                $"PSARC song load failed. Psarc='{item.Song.PackagePath}', title='{item.Title}'.", ex);
            // A refused package says why in the user's language, and so does an
            // encrypted one read without the user's keys.
            StatusText = _localization.Format("Status_PsarcLoadFailed", ex switch
            {
                ILibraryReadError refused => _localization[refused.ResourceKey],
                _ => ex.Message
            });
        }
        finally
        {
            IsScanning = false;

            // A freshly loaded library song has no unsaved changes — and saying so
            // is REQUIRED, not cosmetic. ApplyProject(resetHistory: true) cleared
            // the history, which mints a new root node id, so without this the
            // dirty flag (current node id != baseline id) can never read clean
            // again: closing the app after merely browsing a song raised "The
            // project has unsaved changes", and the recovery snapshotter, which
            // watches the same flag, wrote a snapshot for a chart nobody edited —
            // which is why the restore prompt came back every launch.
            //
            // In the FINALLY because the chart is applied first and the AUDIO is
            // loaded after it — and this method's audio path is the one Play
            // Mode's song select drives, where preferMemoryPlayback reads the
            // whole song through File.ReadAllBytesAsync. A song whose audio will
            // not decode must not leave the declaration unreached, or exactly the
            // two symptoms above come back for a chart that loaded fine.
            // Guarded on sessionReplaced: before ApplyProject there is nothing new
            // to describe and the previous project's dirty state is still true.
            if (sessionReplaced) _projectSession.MarkProjectClean();
        }

        return loaded;
    }

    /// <summary>The ONE place a filter pass is asked for. TYPING coalesces (see
    /// <see cref="CoalescedFilterPass" />); every other trigger is a single
    /// discrete gesture and runs at once, which also drops any pending typed pass
    /// — <see cref="ApplyFilter" /> reads the live <see cref="SearchText" />, so
    /// the immediate pass already covers it.</summary>
    public CoalescedFilterPass FilterPass { get; }

    partial void OnSearchTextChanged(string value)
    {
        _ = value;
        RefreshResultText = ""; // it described the list before this search
        if (_suspendFilter) return;
        FilterPass.Request();
    }

    partial void OnSortModeChanged(int value)
    {
        _ = value;
        RefreshSortGlyphs();
        RunFilterNow();
    }

    partial void OnSortDescendingChanged(bool value)
    {
        _ = value;
        RefreshSortGlyphs();
        RunFilterNow();
    }

    partial void OnHideLessonsChanged(bool value)
    {
        _ = value;
        RunFilterNow();
    }

    /// <summary>A discrete gesture asks for its pass AT ONCE — unless a bulk
    /// reset is mid-flight, which runs exactly one pass of its own when it is
    /// done. Suppressing the REQUEST (rather than only the work inside
    /// <see cref="ApplyFilter" />) is what makes "clear all filters" one pass and
    /// not seven.</summary>
    private void RunFilterNow()
    {
        if (_suspendFilter) return;
        FilterPass.RunImmediately();
    }

    /// <summary>The status popover's clear — status and origin share it, so its
    /// one clear resets both (and the lessons toggle), in one pass.</summary>
    [RelayCommand]
    private void ClearStatusPopover()
    {
        _suspendFilter = true;
        try
        {
            StatusFacet.Clear();
            OriginFacet.Clear();
            HideLessons = BrowserFilterChips.HideLessonsDefault;
        }
        finally
        {
            _suspendFilter = false;
        }

        FilterPass.RunImmediately();
    }

    /// <summary>The tuning popover's clear — tuning and capo share the column
    /// and the popover, so its one clear resets both, in one pass.</summary>
    [RelayCommand]
    private void ClearTuningAndCapo()
    {
        _suspendFilter = true;
        try
        {
            TuningFacet.Clear();
            CapoFacet.Clear();
        }
        finally
        {
            _suspendFilter = false;
        }

        FilterPass.RunImmediately();
    }

    /// <summary>Resets exactly the one filter a chip stands for, then runs the
    /// single pass the gesture earns. Mirrors Play Mode's
    /// <c>RemoveSongFilter</c>.</summary>
    [RelayCommand]
    private void RemoveBrowserFilter(BrowserFilterChip? chip)
    {
        if (chip == null) return;

        _suspendFilter = true;
        try
        {
            switch (chip.Kind)
            {
                case BrowserFilterChips.SearchKind: SearchText = ""; break;
                case BrowserFilterChips.LessonsKind: HideLessons = BrowserFilterChips.HideLessonsDefault; break;
                default:
                    Facets.FirstOrDefault(f => f.Key == chip.Kind)?.Deselect(chip.Value);
                    break;
            }
        }
        finally
        {
            _suspendFilter = false;
        }

        FilterPass.RunImmediately();
    }

    /// <summary>Resets every filter and the search box in ONE pass (the chips
    /// row's "clear filters" button). The sort is deliberately NOT reset — it
    /// narrows nothing, so it is not a filter and carries no chip.</summary>
    [RelayCommand]
    private void ClearFilters()
    {
        _suspendFilter = true;
        try
        {
            SearchText = "";
            foreach (var facet in Facets) facet.Clear();
            HideLessons = BrowserFilterChips.HideLessonsDefault;
        }
        finally
        {
            _suspendFilter = false;
        }

        FilterPass.RunImmediately();
    }

    /// <summary>Every filter control, snapshotted for one pass of
    /// <see cref="LibraryQuery.Matches" />. The facets' selections pass by
    /// reference: they only change on a gesture that runs a fresh pass.</summary>
    private LibraryFilter CurrentFilter() => LibraryFilter.FromFacets(
        SearchText, Facets.Select(facet => (facet.Key, (IReadOnlyCollection<string>)facet.Selected)),
        hideLessons: HideLessons);

    /// <summary>
    ///     Refreshes one facet's per-option counts — called when its popover
    ///     OPENS, never per filter pass: the numbers are only visible inside the
    ///     open popover. Each option counts the rows that pass the search and
    ///     every OTHER facet (<see cref="LibraryQuery.CountFacet" />).
    /// </summary>
    public void RefreshFacetCounts(BrowserFacet facet)
        => facet.ApplyCounts(LibraryQuery.CountFacet(CollapsedSongs, CurrentFilter(), facet.Key));

    /// <summary>Collapse byte-copies of the same pack (same SongKey|Charter|Version
    /// under different file names) to ONE visible row, so the list agrees with Play
    /// Mode's chooser. The master <c>_allSongs</c> (and thus <see cref="AllSongs" />
    /// + LibraryDoctor's snapshot) stays uncollapsed — the doctor is the diagnosis
    /// surface and must keep reporting the duplicates.
    /// <para>
    ///     CACHED, because it is a pure function of <c>_allSongs</c> and nothing
    ///     else: rebuilding it on every keystroke (a dictionary and a
    ///     lower-cased three-part key per song — 0.8 ms per pass over 2,268 rows,
    ///     measured Release) would produce a list that could not have changed.
    ///     Every mutation of <c>_allSongs</c> must call
    ///     <see cref="InvalidateCollapsedSongs" />.
    /// </para></summary>
    private IReadOnlyList<PsarcSongListItem> CollapsedSongs =>
        _collapsedSongs ??= LibraryDedupe.CollapseByIdentity(_allSongs, static i => i.Song);

    private void InvalidateCollapsedSongs() => _collapsedSongs = null;

    private void ApplyFilter()
    {
        // A bulk reset writes several filter properties in a row; the caller runs
        // the one pass they add up to (see RunFilterNow).
        if (_suspendFilter) return;

        // Snapshotted and tokenized ONCE for the whole pass, never per row.
        var filter = CurrentFilter();
        var searchTokens = LibraryQuery.TokenizeFolded(filter.Search);
        var matching = CollapsedSongs.Where(song => LibraryQuery.Matches(song, filter, searchTokens));
        var filtered = LibrarySort.Library.Order(matching, SortMode, SortDescending).ToList();

        // ARTIST GROUP HEADERS, through the same grouper Play Mode's library
        // uses. The list itself stays FLAT — one ListBox, one selection model,
        // one virtualized panel — and each run's header is stamped onto its
        // first row. Only the artist sort is grouped; a title/year/length sort
        // must not sprout artist headers.
        SongArtistGrouping.Stamp(filtered, SortMode == 0,
            static song => song.Artist,
            static (song, header, _) => song.GroupHeader = header,
            static (_, _) => "",
            _localization);

        // Differential republish (see BulkObservableCollection.SyncWith): a pass
        // that trims or restores a FEW rows applies just those edits so the
        // list's other realized containers stand untouched; a big or reordering
        // change still lands as ONE Reset (never Clear() + N Adds — each event
        // forces the bound ListBox to rebuild containers), and an unchanged
        // result publishes nothing at all.
        ((BulkObservableCollection<PsarcSongListItem>)Songs).SyncWith(filtered);

        // "N of M songs", where M is what the list COULD show — the collapsed
        // total, not the uncollapsed master. `_allSongs` keeps every byte-copy of
        // a pack (the LibraryDoctor's diagnosis surface needs them) while the list
        // is built from CollapsedSongs, so reading M off the master printed
        // "30 of 36 songs" on an unfiltered library and invited the user to hunt
        // for the filter that was hiding six rows. There is none: those six cannot
        // be shown at any filter setting.
        ResultCountText = string.Format(
            CultureInfo.CurrentCulture,
            _localization["Dialog_SongBrowser_Count"],
            filtered.Count, CollapsedSongs.Count);

        RefreshFilterChips();

        // The selection survives a pass that keeps its song (the list's own transient null while it
        // follows the edit is ignored by LibraryScreen); a song filtered out falls to the first row.
        if (SelectedSong is null || !filtered.Contains(SelectedSong)) SelectedSong = Songs.FirstOrDefault();
        WarmInitialCovers();
    }

    /// <summary>Rebuilds <see cref="ActiveFilterChips" /> from the live filter
    /// state. Called from the single <see cref="ApplyFilter" /> chokepoint — a
    /// per-setter refresh is how a chip row drifts from the list it describes.</summary>
    private void RefreshFilterChips()
    {
        var chips = BrowserFilterChips.Build(SearchText, Facets, HideLessons, _localization);

        // ONE change per pass (none when the chips are equal): a Clear plus an Add per chip made
        // the Library rebuild its chip row once per event.
        ActiveFilterChips.SyncWith(chips);

        // The COUNT, not a second pile of comparisons: the row and the chips in
        // it are then incapable of disagreeing.
        HasActiveFilters = chips.Count > 0;
    }

    /// <summary>
    ///     Datagrid-style header sort, the Library's one sort control: clicking a
    ///     caption sorts by it (ascending), clicking it again flips the direction.
    ///     The caption glyphs read <see cref="SortMode" /> + <see cref="SortDescending" />.
    /// </summary>
    [RelayCommand]
    private void SortByColumn(string column)
    {
        if (LibrarySort.Library.Click(column, SortMode, SortDescending) is not { } next) return;
        if (next.Mode == SortMode)
        {
            SortDescending = next.Descending; // its own hook runs the pass
            return;
        }

        // One pass, not two: writing SortMode + SortDescending separately would
        // run their hooks twice, so the pair is set suspended and run once.
        _suspendFilter = true;
        try
        {
            SortMode = next.Mode;
            SortDescending = next.Descending;
        }
        finally
        {
            _suspendFilter = false;
        }

        RunFilterNow();
    }

    /// <summary>▲ / ▼ beside the caption of the active sort column; empty on the
    /// rest. One string per sortable column, refreshed with the sort state.</summary>
    public string SongSortGlyph => GlyphFor("song");

    public string TuningSortGlyph => GlyphFor("tuning");
    public string YearSortGlyph => GlyphFor("year");
    public string DifficultySortGlyph => GlyphFor("difficulty");
    public string TimeSortGlyph => GlyphFor("time");
    public string TitleSortGlyph => GlyphFor("title");
    public string PackSortGlyph => GlyphFor("pack");

    private string GlyphFor(string caption)
        => LibrarySort.Glyph(SortMode, SortDescending, LibrarySort.Library.ModeOf(caption));

    private void RefreshSortGlyphs()
    {
        OnPropertyChanged(nameof(SongSortGlyph));
        OnPropertyChanged(nameof(TuningSortGlyph));
        OnPropertyChanged(nameof(YearSortGlyph));
        OnPropertyChanged(nameof(DifficultySortGlyph));
        OnPropertyChanged(nameof(TimeSortGlyph));
        OnPropertyChanged(nameof(TitleSortGlyph));
        OnPropertyChanged(nameof(PackSortGlyph));
    }

    // Warms only the first viewport so results appear with artwork without
    // blowing memory or CPU on large libraries. Items scrolled into view later
    // are loaded on demand via EnsureCoverLoaded, wired up in the code-behind
    // through ListBox.ContainerPrepared.
    private void WarmInitialCovers()
    {
        var revision = Interlocked.Increment(ref _coverLoadRevision);
        var initialBatch = Songs
            .Where(song => song.CoverImage == null && song.HasCoverSource)
            .Take(20)
            .ToList();

        if (initialBatch.Count == 0) return;

        // Concurrency now comes from the ONE global SongCoverCache.DecodeThrottle
        // inside LoadCoverImageAsync (this method carries no local
        // throttle of its own; two stacked ones would just add latency). The
        // revision still cancels a superseded warm before it queues work.
        _ = Task.Run(async () =>
        {
            var tasks = initialBatch.Select(async song =>
            {
                if (revision != _coverLoadRevision) return;
                await song.LoadCoverImageAsync(_libraryService);
            });

            await Task.WhenAll(tasks);
        });
    }

    // Called by the view when a list container is realized. Idempotent and
    // cheap when the cover is already loaded (guarded by the item's
    // Interlocked.Exchange flag in LoadCoverImageAsync). A parked bitmap comes
    // back off the LRU leash; a missing one starts the dwell-gated load.
    public void EnsureCoverLoaded(PsarcSongListItem item)
    {
        SongCoverCache.Shared.Unpark(item);
        if (item.CoverImage != null || !item.HasCoverSource) return;
        _ = item.LoadCoverImageAsync(_libraryService);
    }

    // Called by the view when a list container is recycled out of view. The
    // cover is PARKED (byte-capped LRU, see SongCoverCache), not disposed —
    // scrolling back inside the budget is free, and a dwell-pending load for a
    // row that merely scrolled past aborts before it touches the disk.
    public void ReleaseCover(PsarcSongListItem item)
    {
        if (item != SelectedSong) SongCoverCache.Shared.Park(item);
    }

    private static double GetProjectEndMs(ChartProject project)
        => DotCharter.Services.Audio.SilentTransport.SilentAudioTrack.DeriveProjectLengthMs(project);

    // The DI container treats this VM as a singleton, so it normally never
    // gets disposed during the app's lifetime. Implement IDisposable
    // explicitly so shell shutdown (or future scope tear-downs) can release
    // the cached cover bitmaps instead of holding the last scanned set in
    // memory until process exit.
    public void Dispose()
    {
        Interlocked.Increment(ref _coverLoadRevision);

        foreach (var song in _allSongs)
            song.DisposeCoverImage();

        _allSongs.Clear();
        InvalidateCollapsedSongs();
        FilterPass.Cancel();
        Songs.Clear();
        SelectedSong = null;
    }
}
