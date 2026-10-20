// The Library contract over the song browser: the scan, the filter pass, the covers and the load stay in SongBrowserViewModel.
using System.Collections.Specialized;
using System.ComponentModel;
using System.Runtime.CompilerServices;
using System.Windows.Input;
using CommunityToolkit.Mvvm.ComponentModel;
using DotCharter.Presentation.Contracts.Library;

namespace DotCharter.UI.ViewModels.Panels;

/// <summary>
///     <see cref="ILibraryScreen" /> over <see cref="SongBrowserViewModel" /> (wave 5). The song
///     browser keeps everything it is tested for — the scan, the coalesced filter pass, the
///     cover cache, the load and its guard — and this adapter only changes how it is presented.
///     Rows are wrapped once per song and reused, so a filter pass or a re-sort keeps selection
///     identity and a cover already decoded.
/// </summary>
public sealed class LibraryScreen : ObservableObject, ILibraryScreen, ILibraryViewHooks
{
    private readonly SongBrowserViewModel _browser;
    private readonly ConditionalWeakTable<PsarcSongListItem, LibraryRow> _rows = [];
    private readonly LibraryColumn[] _columns;
    private readonly BulkObservableCollection<ILibraryRow> _rowList = [];
    private IReadOnlyList<ILibraryFilterChip> _chips = [];
    private IReadOnlyList<ILibraryPath> _paths = [];
    private PsarcSongListItem? _watched;

    /// <summary>True while the rows follow an edit of the song list (see <see cref="OnSongsChanged" />).</summary>
    private bool _mirroring;

    public LibraryScreen(SongBrowserViewModel browser)
    {
        _browser = browser;
        var loc = browser.Localization;
        _columns =
        [
            new(browser, loc["Dialog_SongBrowser_SortArtist"], "song", () => browser.SongSortGlyph, []),
            new(browser, loc["SongBrowser_Col_Paths"], null, () => "", [(browser.PathFacet, null)]),
            new(browser, loc["SongBrowser_Col_Tuning"], "tuning", () => browser.TuningSortGlyph,
                [(browser.TuningFacet, null), (browser.CapoFacet, loc["SongBrowser_Facet_Capo"])], browser.ClearTuningAndCapoCommand),
            new(browser, loc["SongBrowser_Col_Year"], "year", () => browser.YearSortGlyph, [(browser.YearFacet, null)]),
            new(browser, loc["SongBrowser_Col_Difficulty"], "difficulty", () => browser.DifficultySortGlyph, [(browser.DifficultyFacet, null)]),
            new(browser, loc["SongBrowser_Col_Time"], "time", () => browser.TimeSortGlyph, [(browser.LengthFacet, null)]),
            new(browser, loc["SongBrowser_Col_Status"], null, () => "",
                [(browser.StatusFacet, null), (browser.OriginFacet, loc["SongBrowser_Facet_Origin"])], browser.ClearStatusPopoverCommand,
                new LibraryLessonsOption(browser, loc["SongBrowser_Facet_ShowLessons"])),
            new(browser, loc["Dialog_SongBrowser_SortTitle"], "title", () => browser.TitleSortGlyph, []),
            new(browser, loc["Dialog_SongBrowser_SortPack"], "pack", () => browser.PackSortGlyph, [])
        ];
        browser.Songs.CollectionChanged += OnSongsChanged;
        browser.ActiveFilterChips.CollectionChanged += (_, _) => RebuildChips();
        browser.ArrangementChoices.CollectionChanged += (_, _) => RebuildPaths();
        browser.PropertyChanged += OnBrowserChanged;
        RebuildRows();
        RebuildChips();
        RebuildPaths();
        // A selection restored from the last session is ALREADY set: no SelectedSong change will
        // come to ask for its cover (only the first viewport is warmed by the scan), so ask here.
        WatchSelectedCover();
        if (_browser.SelectedSong is { } selected) _browser.EnsureCoverLoaded(selected);
    }

    public string FolderPath => _browser.PsarcLocation;
    public ICommand ChooseLibraryFolderCommand => _browser.BrowseCommand;
    public ICommand RefreshCommand => _browser.ScanCommand;
    public bool IsScanning => _browser.IsScanning;
    public double ScanProgress => _browser.ScanProgress;
    public string ScanText => _browser.IsScanning ? _browser.StatusText : "";
    public ICommand CancelScanCommand => _browser.CancelScanCommand;
    public string StatusText => _browser.IsScanning ? "" : _browser.StatusText;
    public string RefreshResultText => _browser.IsScanning ? "" : _browser.RefreshResultText;
    public string CountText => _browser.ResultCountText;

    public string SearchText
    {
        get => _browser.SearchText;
        set => _browser.SearchText = value;
    }

    public ILibraryColumn SongColumn => _columns[0];
    public ILibraryColumn TitleSort => _columns[7];
    public ILibraryColumn ArchiveSort => _columns[8];
    public ILibraryColumn PathsColumn => _columns[1];
    public ILibraryColumn TuningColumn => _columns[2];
    public ILibraryColumn YearColumn => _columns[3];
    public ILibraryColumn DifficultyColumn => _columns[4];
    public ILibraryColumn LengthColumn => _columns[5];
    public ILibraryColumn StatusColumn => _columns[6];
    public IReadOnlyList<ILibraryFilterChip> ActiveFilters => _chips;
    public bool HasActiveFilters => _browser.HasActiveFilters;
    public ICommand ClearFiltersCommand => _browser.ClearFiltersCommand;
    public IReadOnlyList<ILibraryRow> Rows => _rowList;

    public ILibraryRow? SelectedRow
    {
        get => _browser.SelectedSong is { } song ? Row(song) : null;
        set
        {
            // While the rows follow a filter pass the list drops its selection (a Reset clears it, a
            // removed row takes it along). That null is the list's, not the user's: the browser
            // decides what is selected after the pass.
            if (_mirroring) return;
            _browser.SelectedSong = (value as LibraryRow)?.Item;
        }
    }

    public bool ShowFirstRun => string.IsNullOrWhiteSpace(_browser.PsarcLocation);
    public bool ShowNoMatch => !ShowFirstRun && _browser.IsEmptyStateVisible;
    public bool HasSelection => _browser.HasSelection;
    public string DetailTitle => _browser.SelectedSong?.Title ?? "";
    public string DetailArtist => _browser.SelectedSong?.Artist ?? "";
    public string DetailAlbum => _browser.SelectedSong?.Album ?? "";
    public object? DetailCover => _browser.SelectedSong?.CoverImage;
    public bool DetailHasCover => _browser.SelectedSong is { HasCoverSource: true };
    public string DetailProblem => _browser.SelectedSong is { CanLoad: false } ? _browser.LoadChartTip : "";

    public IReadOnlyList<string> DetailFacts => _browser.SelectedSong is { CanLoad: true } song
        ? [.. new[] { TuningFact(song), song.HasCapo ? song.CapoSummary : "", song.YearLabel }.Where(f => !string.IsNullOrWhiteSpace(f))]
        : [];

    public IReadOnlyList<ILibraryPath> Paths => _paths;

    /// <summary>A tuning that moves strings names their notes ("Drop D (D A D G B E)"); E Standard is just its name.</summary>
    private static string TuningFact(PsarcSongListItem song) =>
        SongRowFacts.IsNonStandardTuning(song.TuningSummary) ? song.TuningDisplay : song.TuningName;

    public ILibraryPath? SelectedPath
    {
        get => _paths.FirstOrDefault(p => ReferenceEquals(((LibraryPath)p).Choice, _browser.SelectedArrangement));
        set => _browser.SelectedArrangement = (value as LibraryPath)?.Choice;
    }

    public string ArchiveText => _browser.SelectedSong is { } song
        ? string.Join(" · ", new[] { song.FileName, _browser.ArchiveDetailText }.Where(s => !string.IsNullOrWhiteSpace(s)))
        : "";

    public string LoadStatusText => _browser.LoadStatusText;
    public bool ReplacesOpenProject => _browser.IsSelectionLoadable;
    public ICommand CheckLibraryCommand => _browser.RunLibraryDoctorCommand;
    public ICommand LoadCommand => _browser.LoadSelectedCommand;
    public string LoadTip => _browser.LoadChartTip;

    public void RowShown(ILibraryRow row)
    {
        if (row is LibraryRow r) _browser.EnsureCoverLoaded(r.Item);
    }

    public void RowHidden(ILibraryRow row)
    {
        // The selected song's cover stays: the rail shows it even when its row scrolls away.
        if (row is LibraryRow r && !ReferenceEquals(r.Item, _browser.SelectedSong)) _browser.ReleaseCover(r.Item);
    }

    public void FiltersOpening()
    {
        foreach (var facet in _browser.Facets) _browser.RefreshFacetCounts(facet);
    }

    private LibraryRow Row(PsarcSongListItem song) => _rows.GetValue(song, s => new LibraryRow(s, () => _browser.SortMode == 0));

    /// <summary>
    ///     Mirrors the song list edit for edit: a scan adds songs one batch at a time, and a
    ///     rebuilt list per add would reset the ListBox thousands of times. A filter pass
    ///     arrives as one reset and is synced as one.
    /// </summary>
    private void OnSongsChanged(object? sender, NotifyCollectionChangedEventArgs e)
    {
        _mirroring = true;
        try
        {
            if (e.Action == NotifyCollectionChangedAction.Add && e.NewItems is { } added && e.NewStartingIndex >= 0)
            {
                var at = e.NewStartingIndex;
                foreach (PsarcSongListItem song in added) _rowList.Insert(at++, Row(song));
            }
            else if (e.Action == NotifyCollectionChangedAction.Remove && e.OldItems is { } removed && e.OldStartingIndex >= 0)
            {
                for (var i = 0; i < removed.Count; i++) _rowList.RemoveAt(e.OldStartingIndex);
            }
            else
            {
                RebuildRows();
            }
        }
        finally
        {
            _mirroring = false;
        }

        // A Reset cleared the list's selection; hand back the song that is still listed (one the pass
        // dropped is replaced by the browser, which raises its own change).
        if (e.Action == NotifyCollectionChangedAction.Reset && _browser.SelectedSong is { } selected && _browser.Songs.Contains(selected))
            OnPropertyChanged(nameof(SelectedRow));
        OnPropertyChanged(nameof(ShowNoMatch));
    }

    private void RebuildRows() => _rowList.SyncWith(_browser.Songs.Select(Row).ToList());

    private void RebuildChips()
    {
        _chips = [.. _browser.ActiveFilterChips.Select(c => new LibraryChip(c.Label, new RemoveChip(_browser, c)))];
        OnPropertyChanged(nameof(ActiveFilters));
        OnPropertyChanged(nameof(HasActiveFilters));
    }

    private void RebuildPaths()
    {
        var songTuning = _browser.SelectedSong?.TuningCellLabel ?? "";
        _paths = [.. _browser.ArrangementChoices.Select(c => new LibraryPath(c, songTuning))];
        OnPropertyChanged(nameof(Paths));
        OnPropertyChanged(nameof(SelectedPath));
    }

    private void OnBrowserChanged(object? sender, PropertyChangedEventArgs e)
    {
        switch (e.PropertyName)
        {
            case nameof(SongBrowserViewModel.PsarcLocation):
                Raise(nameof(FolderPath), nameof(ShowFirstRun), nameof(ShowNoMatch));
                break;
            case nameof(SongBrowserViewModel.IsScanning):
                Raise(nameof(IsScanning), nameof(ScanText), nameof(StatusText), nameof(RefreshResultText), nameof(ShowNoMatch));
                break;
            case nameof(SongBrowserViewModel.ScanProgress): Raise(nameof(ScanProgress)); break;
            case nameof(SongBrowserViewModel.StatusText): Raise(nameof(ScanText), nameof(StatusText)); break;
            case nameof(SongBrowserViewModel.RefreshResultText): Raise(nameof(RefreshResultText)); break;
            case nameof(SongBrowserViewModel.ResultCountText): Raise(nameof(CountText)); break;
            case nameof(SongBrowserViewModel.SearchText): Raise(nameof(SearchText)); break;
            case nameof(SongBrowserViewModel.HasActiveFilters): Raise(nameof(HasActiveFilters)); break;
            case nameof(SongBrowserViewModel.IsEmptyStateVisible): Raise(nameof(ShowNoMatch)); break;
            case nameof(SongBrowserViewModel.SelectedSong):
                WatchSelectedCover();
                if (_browser.SelectedSong is { } song) _browser.EnsureCoverLoaded(song);
                Raise(nameof(SelectedRow), nameof(HasSelection), nameof(DetailTitle), nameof(DetailArtist), nameof(DetailAlbum),
                    nameof(DetailCover), nameof(DetailHasCover), nameof(DetailProblem), nameof(DetailFacts), nameof(ArchiveText), nameof(ReplacesOpenProject), nameof(LoadTip));
                break;
            case nameof(SongBrowserViewModel.SelectedArrangement): Raise(nameof(SelectedPath)); break;
            case nameof(SongBrowserViewModel.ArchiveDetailText): Raise(nameof(ArchiveText)); break;
            case nameof(SongBrowserViewModel.LoadStatusText): Raise(nameof(LoadStatusText)); break;
            case nameof(SongBrowserViewModel.IsSelectionLoadable) or nameof(SongBrowserViewModel.LoadChartTip):
                Raise(nameof(ReplacesOpenProject), nameof(LoadTip), nameof(DetailProblem));
                break;
            case nameof(SongBrowserViewModel.SongSortGlyph) or nameof(SongBrowserViewModel.TuningSortGlyph)
                or nameof(SongBrowserViewModel.YearSortGlyph) or nameof(SongBrowserViewModel.DifficultySortGlyph)
                or nameof(SongBrowserViewModel.TimeSortGlyph) or nameof(SongBrowserViewModel.TitleSortGlyph)
                or nameof(SongBrowserViewModel.PackSortGlyph):
                foreach (var column in _columns) column.RefreshSort();
                break;
            case nameof(SongBrowserViewModel.SortMode):
                foreach (var row in _rowList.OfType<LibraryRow>()) row.RefreshSubtitle();
                break;
        }
    }

    /// <summary>The selected song's cover usually decodes AFTER it is picked; the rail follows it as it arrives.</summary>
    private void WatchSelectedCover()
    {
        _watched?.PropertyChanged -= OnSelectedSongChanged;
        _watched = _browser.SelectedSong;
        _watched?.PropertyChanged += OnSelectedSongChanged;
    }

    private void OnSelectedSongChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(PsarcSongListItem.CoverImage)) OnPropertyChanged(nameof(DetailCover));
    }

    private void Raise(params string[] names)
    {
        foreach (var name in names) OnPropertyChanged(name);
    }

    /// <summary>A chip's ✕: the browser's own removal, which knows the search and lessons chips as well as the facets.</summary>
    private sealed class RemoveChip(SongBrowserViewModel browser, BrowserFilterChip chip) : ICommand
    {
        public event EventHandler? CanExecuteChanged { add { } remove { } }

        public bool CanExecute(object? parameter) => true;

        public void Execute(object? parameter) => browser.RemoveBrowserFilterCommand.Execute(chip);
    }
}
