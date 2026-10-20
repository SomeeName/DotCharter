using System.Globalization;
using System.Windows.Input;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using DotCharter.Presentation.Contracts.Library;

namespace DotCharter.Presentation.Fakes.Library;

/// <summary>A services-free <see cref="ILibraryScreen" />: search, sort, the column filters and selection work on the seeded rows.</summary>
public sealed partial class FakeLibraryScreen : ObservableObject, ILibraryScreen
{
    private const string LibraryFolder = @"C:\Users\player\Songs";
    private readonly IReadOnlyList<FakeLibraryRow> _all;
    private readonly FakeLibraryColumn[] _columns;
    private FakeLibraryColumn? _sortedBy;
    private bool _descending;
    private string _searchText = "";
    private ILibraryRow? _selectedRow;
    private ILibraryPath? _selectedPath;
    private object? _detailCover;

    [ObservableProperty] private IReadOnlyList<ILibraryRow> _rows = [];
    [ObservableProperty] private IReadOnlyList<ILibraryPath> _paths = [];
    [ObservableProperty] private IReadOnlyList<ILibraryFilterChip> _activeFilters = [];

    private FakeLibraryScreen(IReadOnlyList<FakeLibraryRow> rows, string folder, bool scanning = false,
        IReadOnlyList<FakeLibraryFilterOption>? tuningOptions = null)
    {
        _all = rows;
        FolderPath = folder;
        IsScanning = scanning;
        ScanProgress = scanning ? 0.37 : 0;
        ScanText = scanning ? "Scanning 812 of 2,160 archives…" : "";
        _columns =
        [
            new("Artist", true, r => r.Artist.ToLowerInvariant() + "\u0001" + r.Title),
            new("Paths", false, _ => "", [Has("Lead", "L"), Has("Rhythm", "R"), Has("Bass", "B"), Has("Vocals", "V")]),
            new("Tuning", true, r => r.TuningLabel, tuningOptions ??
            [
                new("E Standard", r => r.TuningLabel.Length == 0), new("Eb Standard", r => r.TuningLabel == "Eb Standard"),
                new("Drop tunings", r => r.TuningLabel.StartsWith("Drop", StringComparison.Ordinal)),
                new("Other", r => r.TuningLabel.Length > 0 && r.TuningLabel != "Eb Standard" && !r.TuningLabel.StartsWith("Drop", StringComparison.Ordinal))
            ]),
            new("Year", true, r => r.YearLabel,
            [
                new("1970s", r => r.YearLabel.StartsWith("197", StringComparison.Ordinal)), new("1980s", r => r.YearLabel.StartsWith("198", StringComparison.Ordinal)),
                new("1990s", r => r.YearLabel.StartsWith("199", StringComparison.Ordinal)), new("2000s and later", r => r.YearLabel.StartsWith('2'))
            ]),
            new("Diff", true, r => r.DifficultyLabel,
                [new("Easy", r => Diff(r) is >= 1 and <= 3), new("Medium", r => Diff(r) is >= 4 and <= 6), new("Hard", r => Diff(r) >= 7)]),
            new("Time", true, r => r.LengthLabel.PadLeft(6)),
            new("Status", false, _ => "", [new("Loads", r => !r.HasProblem), new("Problem", r => r.HasProblem)]),
            new("Title", true, r => r.Title.ToLowerInvariant() + "\u0001" + r.Artist),
            new("Pack / file", true, FakeLibraryData.ArchiveName)
        ];
        foreach (var column in _columns)
        {
            column.Sorted += Sort;
            column.FilterChanged += Refilter;
            foreach (var option in column.Options)
                option.CountLabel = rows.Count(option.Matches).ToString(CultureInfo.InvariantCulture);
        }

        ChooseLibraryFolderCommand = new RelayCommand(() => { });
        RefreshCommand = new RelayCommand(() => { }, () => folder.Length > 0 && !scanning);
        ClearFiltersCommand = new RelayCommand(ClearFilters);
        CheckLibraryCommand = new RelayCommand(() => { }, () => rows.Count > 0 && !scanning);
        LoadCommand = new RelayCommand(() => { }, () => SelectedRow is { HasProblem: false });
        Sort(_columns[0]);
    }

    public static IReadOnlyList<FakeVariant<ILibraryScreen>> Variants { get; } =
    [
        new(FakeVariantNames.Default, Default),
        new(FakeVariantNames.Empty, Empty),
        new(FakeVariantNames.LongText, LongText),
        new(FakeVariantNames.Full, Full),
        new(FakeVariantNames.Busy, Busy),
        new("NoMatch", NoMatch),
        new("Refreshed", Refreshed),
        new("ByTitle", ByTitle),
        new("ManyTunings", ManyTunings),
        new("Packages", Packages),
        new("PackageNeedsFfmpeg", PackageNeedsFfmpeg)
    ];

    /// <summary>Sixteen songs grouped by artist, one selected and ready to load.</summary>
    public static FakeLibraryScreen Default() => Select(new FakeLibraryScreen(FakeLibraryData.Library(), LibraryFolder), "Three Little Birds");

    /// <summary>First run: no folder chosen, so the screen is the one place to choose it.</summary>
    public static FakeLibraryScreen Empty() => new([], "");

    /// <summary>German and Japanese titles, a very long artist and album, a problem in German.</summary>
    public static FakeLibraryScreen LongText()
    {
        var screen = new FakeLibraryScreen(FakeLibraryData.LongText(), @"D:\Musik\Rocksmith\Eigene Songs (CDLC)\Sammlung 2024 – aktualisiert");
        screen.Tick("Paths", "Lead");
        return Select(screen, FakeLibraryData.LongText()[0].Title);
    }

    /// <summary>Filtered to songs with a lead or a vocal path, newest first (the unknown year last), the unreadable song selected.</summary>
    public static FakeLibraryScreen Full()
    {
        var screen = new FakeLibraryScreen(FakeLibraryData.Library(), LibraryFolder);
        screen.Tick("Paths", "Lead");
        screen.Tick("Paths", "Vocals");
        screen.Sort(screen._columns[3]);
        screen.Sort(screen._columns[3]);
        return Select(screen, "Paranoid Android");
    }

    /// <summary>A scan in flight over the part of the list already read.</summary>
    public static FakeLibraryScreen Busy() => new(FakeLibraryData.Library().Take(6).ToList(), @"E:\Rocksmith\dlc", scanning: true);

    /// <summary>A Refresh of the same folder just finished: the list and the selection stayed, and the header says what changed.</summary>
    public static FakeLibraryScreen Refreshed() =>
        Select(new FakeLibraryScreen(FakeLibraryData.Library(), LibraryFolder) { RefreshResultText = "Refreshed · 12 new, 1 removed" }, "Three Little Birds");

    /// <summary>The song column's second order: by title, Z to A, so the header's title caption is the lit one.</summary>
    public static FakeLibraryScreen ByTitle()
    {
        var screen = new FakeLibraryScreen(FakeLibraryData.Library(), LibraryFolder);
        screen.Sort(screen._columns[7]);
        screen.Sort(screen._columns[7]);
        return Select(screen, "Three Little Birds");
    }

    /// <summary>DotCharter song packages beside the Rocksmith archives, a package selected and ready to load.</summary>
    public static FakeLibraryScreen Packages() =>
        Select(new FakeLibraryScreen(FakeLibraryData.WithPackages(), LibraryFolder), "My Own Summer (Shove It) — Live at the Warfield, San Francisco, Remastered");

    /// <summary>The Opus package this computer cannot play (no ffmpeg) selected: disabled, with its reason.</summary>
    public static FakeLibraryScreen PackageNeedsFfmpeg() =>
        Select(new FakeLibraryScreen(FakeLibraryData.WithPackages(), LibraryFolder), "Silvera");

    /// <summary>Songs exist; the search and a filter leave none.</summary>
    public static FakeLibraryScreen NoMatch()
    {
        var screen = new FakeLibraryScreen(FakeLibraryData.Library(), LibraryFolder);
        screen.Tick("Tuning", "Drop tunings");
        screen.SearchText = "zzzz";
        return screen;
    }

    /// <summary>A large library's Tuning popover: over thirty tunings labelled "Name (letters)" (one wider than the popover), the unknown bucket, and the capo options.</summary>
    public static FakeLibraryScreen ManyTunings() => ManyTunings(FakeLibraryTunings.English);

    /// <summary><see cref="ManyTunings()" /> with the popover's localized strings looked up through <paramref name="text" />.</summary>
    public static FakeLibraryScreen ManyTunings(Func<string, string> text)
    {
        var rows = FakeLibraryTunings.Rows();
        return Select(new FakeLibraryScreen(rows, LibraryFolder, tuningOptions: FakeLibraryTunings.Options(rows, text)), rows[0].Title);
    }

    public string FolderPath { get; }
    public ICommand ChooseLibraryFolderCommand { get; }
    public ICommand RefreshCommand { get; }
    public bool IsScanning { get; }
    public double ScanProgress { get; }
    public string ScanText { get; }
    public ICommand CancelScanCommand { get; } = new RelayCommand(() => { });
    public string StatusText { get; init; } = "";
    public string RefreshResultText { get; init; } = "";
    public string CountText => $"{Rows.Count} of {_all.Count} songs";

    public string SearchText
    {
        get => _searchText;
        set
        {
            if (SetProperty(ref _searchText, value)) Refilter();
        }
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
    public bool HasActiveFilters => ActiveFilters.Count > 0;
    public ICommand ClearFiltersCommand { get; }

    public ILibraryRow? SelectedRow
    {
        get => _selectedRow;
        set
        {
            if (!SetProperty(ref _selectedRow, value)) return;
            Paths = value is FakeLibraryRow row ? FakeLibraryData.PathsOf(row) : [];
            SelectedPath = Paths.FirstOrDefault();
            foreach (var name in (string[])[nameof(HasSelection), nameof(DetailTitle), nameof(DetailArtist), nameof(DetailAlbum),
                         nameof(DetailProblem), nameof(DetailFacts), nameof(ArchiveText), nameof(LoadStatusText), nameof(ReplacesOpenProject)])
            {
                OnPropertyChanged(name);
            }

            ((RelayCommand)LoadCommand).NotifyCanExecuteChanged();
        }
    }

    public bool ShowFirstRun => FolderPath.Length == 0;
    public bool ShowNoMatch => _all.Count > 0 && Rows.Count == 0;
    public bool HasSelection => SelectedRow != null;
    private FakeLibraryRow? Selected => SelectedRow as FakeLibraryRow;
    public string DetailTitle => Selected?.Title ?? "";
    public string DetailArtist => Selected?.Artist ?? "";
    public string DetailAlbum => Selected?.Album ?? "";
    public string DetailProblem => Selected?.Problem ?? "";
    /// <summary>The fake has no art of its own; a layout test hands it a bitmap to see the cover box.</summary>
    public object? DetailCover
    {
        get => _detailCover;
        set
        {
            if (!SetProperty(ref _detailCover, value)) return;
            OnPropertyChanged(nameof(DetailHasCover));
        }
    }

    public bool DetailHasCover => _detailCover != null;

    public IReadOnlyList<string> DetailFacts => Selected is { HasProblem: false } row
        ? [.. new[] { row.TuningLabel.Length > 0 ? row.TuningTip : "E Standard", row.TuningLabel.Contains("capo") ? "" : "No capo", row.YearLabel }
            .Where(f => f.Length > 0 && f != "—")]
        : [];

    public ILibraryPath? SelectedPath
    {
        get => _selectedPath;
        set
        {
            if (SetProperty(ref _selectedPath, value)) OnPropertyChanged(nameof(LoadStatusText));
        }
    }

    public string ArchiveText => Selected is { } row ? $"{FakeLibraryData.ArchiveName(row)} · 3.6 MB" : "";

    public string LoadStatusText => Selected switch
    {
        null => "Pick a song to load",
        { HasProblem: true } row => $"{row.Title} can't be loaded",
        { } row => $"Ready to load — {row.Title} · {SelectedPath?.Label}"
    };

    public bool ReplacesOpenProject => Selected is { HasProblem: false };
    public ICommand CheckLibraryCommand { get; }
    public ICommand LoadCommand { get; }
    public string LoadTip => Selected is { HasProblem: false } ? "Open this song in the chart editor" : "Pick a song that can be read";

    private static FakeLibraryFilterOption Has(string label, string initial) => new(label, r => r.Paths.Contains(initial));

    private static int Diff(FakeLibraryRow row) => int.TryParse(row.DifficultyLabel, out var d) ? d : 0;

    private static bool Unknown(string value) => value.Length == 0 || value == "—";

    private void Tick(string column, string option) =>
        _columns.Single(c => c.Title == column).Options.Single(o => o.Label == option).IsSelected = true;

    private void ClearFilters()
    {
        _searchText = "";
        OnPropertyChanged(nameof(SearchText));
        foreach (var option in _columns.SelectMany(c => c.Options)) option.IsSelected = false;
        Refilter();
    }

    private void Sort(FakeLibraryColumn column)
    {
        _descending = ReferenceEquals(_sortedBy, column) && !_descending;
        if (_sortedBy is { } previous) previous.SortGlyph = "";
        _sortedBy = column;
        column.SortGlyph = _descending ? "↓" : "↑";
        Refilter();
    }

    private void Refilter()
    {
        var key = _sortedBy?.Key ?? _columns[0].Key;
        var matching = _all.Where(r => _columns.All(c => c.Admits(r))
                                       && (_searchText.Length == 0
                                           || r.Title.Contains(_searchText, StringComparison.OrdinalIgnoreCase)
                                           || r.Artist.Contains(_searchText, StringComparison.OrdinalIgnoreCase)));
        // Unknown values sort last in either direction.
        var ordered = matching.OrderBy(r => Unknown(key(r)));
        var sorted = _descending ? ordered.ThenByDescending(key, StringComparer.Ordinal) : ordered.ThenBy(key, StringComparer.Ordinal);
        // Artist headings only make sense while the list is in artist order.
        Rows = ReferenceEquals(_sortedBy, _columns[0]) ? FakeLibraryData.Grouped(sorted) : sorted.ToList();
        ActiveFilters =
        [
            .. _columns.SelectMany(c => c.Options.Where(o => o.IsSelected)
                .Select(o => new FakeLibraryChip($"{c.Title} · {o.Label}", new RelayCommand(() => o.IsSelected = false))))
        ];
        OnPropertyChanged(nameof(HasActiveFilters));
        OnPropertyChanged(nameof(CountText));
        OnPropertyChanged(nameof(ShowNoMatch));
    }

    private static FakeLibraryScreen Select(FakeLibraryScreen screen, string title)
    {
        screen.SelectedRow = screen.Rows.FirstOrDefault(r => r.Title == title) ?? screen.Rows.FirstOrDefault();
        return screen;
    }
}
