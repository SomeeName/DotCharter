using System.Globalization;
using System.Windows.Input;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using DotCharter.Presentation.Contracts.Library;
using DotCharter.Presentation.Contracts.PlayMode;

namespace DotCharter.Presentation.Fakes.PlayMode;

/// <summary>
///     A services-free <see cref="ISongSelectScreen" /> for the gallery and for
///     the layout smoke tests.
///     <para>
///         <b>An empty fixture hides layout bugs.</b> Every layout fault the owner
///         reported on this screen was in rows that only exist when there IS data,
///         which is why <see cref="Library" /> is forty realistic rows and
///         <see cref="LongText" /> is the same screen with titles, artists and
///         albums at the length a real CDLC pack reaches. A variant that decays back
///         to an empty list is pinned by <c>UI/FakeSongSelectScreenTests</c>.
///     </para>
///     <para>
///         The commands MUTATE this object — typing filters, a header sorts, a
///         funnel's tick narrows, a chip's ✕ drops its own value — so the gallery's
///         copy of the screen is interactive without a single service behind it.
///         They are plain <see cref="RelayCommand" /> FIELDS rather than
///         <c>[RelayCommand]</c> methods on purpose: a generated <c>…Command</c> is
///         linted for reachability against the view models that declare it.
///     </para>
/// </summary>
public sealed partial class FakeSongSelectScreen : ObservableObject, ISongSelectScreen
{
    /// <summary>The tuning the fake's guitar was last heard in, for the tuning check.</summary>
    private const string GuitarTuning = "E Standard";

    private readonly List<FakeSongRow> _allRows;
    private readonly FakeSongColumn[] _columns;
    private readonly List<FakeFilterChip> _extraChips = [];
    private readonly bool _libraryConfigured;
    private FakeSongColumn _sortedBy;
    private bool _descending;

    private IReadOnlyList<IFilterChip> _activeFilters = [];
    private IReadOnlyList<ISongRow> _rows = [];
    private ISongRow? _selectedRow;
    private string _searchText = "";
    private IReadOnlyList<IArrangementTile> _arrangements = [];
    private IArrangementTile? _selectedArrangement;

    private FakeSongSelectScreen(IEnumerable<FakeSongRow> rows, bool libraryConfigured, bool loading = false)
    {
        _allRows = rows.ToList();
        _libraryConfigured = libraryConfigured;
        IsLoading = loading;

        _columns =
        [
            new("Song", r => r.Artist.ToLowerInvariant() + "\u0001" + r.Title.ToLowerInvariant()),
            new("Paths", null, [Has("Lead"), Has("Rhythm"), Has("Bass")]),
            new("Tuning", r => r.TuningLabel.Length == 0 ? null! : r.TuningLabel,
            [
                new("E Standard", r => r.TuningLabel.Length == 0),
                new("Eb Standard", r => r.TuningName == "E♭ Standard"),
                new("Drop tunings", r => r.TuningName.StartsWith("Drop", StringComparison.Ordinal)),
                new("Other", r => r.TuningLabel.Length > 0 && r.TuningName != "E♭ Standard" && !r.TuningName.StartsWith("Drop", StringComparison.Ordinal))
            ]),
            new("Time", r => r.LengthText.PadLeft(6)),
            new("Difficulty", r => r.DifficultyLabel == "—" ? null! : r.DifficultyLabel,
                [new("Easy", r => Diff(r) is > 0 and < 4), new("Medium", r => Diff(r) is >= 4 and < 7), new("Hard", r => Diff(r) >= 7)],
                descendingFirst: true),
            new("Your record", r => r.HasBest ? r.BestAccuracy : null!,
                [new("Played", r => r.HasBest), new("Not played yet", r => !r.HasBest), new("Full combo", r => r.IsFullCombo)],
                descendingFirst: true)
        ];
        foreach (var column in _columns)
        {
            column.Sorted += Sort;
            column.FilterChanged += ApplyFilter;
            foreach (var option in column.Options)
                option.CountLabel = _allRows.Count(option.Matches).ToString(CultureInfo.InvariantCulture);
        }

        _sortedBy = _columns[0];
        _sortedBy.SortGlyph = "↑";

        BackCommand = new RelayCommand(() => { });
        ClearSearchCommand = new RelayCommand(() => SearchText = "");
        ClearFiltersCommand = new RelayCommand(() =>
        {
            _extraChips.Clear();
            _searchText = "";
            OnPropertyChanged(nameof(SearchText));
            OnPropertyChanged(nameof(HasSearchText));
            foreach (var option in _columns.SelectMany(c => c.Options)) option.IsSelected = false;
            ApplyFilter();
        });
        PlayCommand = new RelayCommand(() => { }, () => CanPlay);
        ChooseLibraryFolderCommand = new RelayCommand(() => { });

        ApplyFilter();
    }

    // ── the named variants ────────────────────────────────────────────────

    public static IReadOnlyList<FakeVariant<ISongSelectScreen>> Variants { get; } =
    [
        new(FakeVariantNames.Default, Library),
        new(FakeVariantNames.Empty, Empty),
        new(FakeVariantNames.LongText, LongText),
        new(FakeVariantNames.Full, Full),
        new(FakeVariantNames.Busy, Loading),
        new("NoMatch", NoMatch),
        new("Played", Played),
        new("Setlist", Setlist),
        new("SetlistEmpty", SetlistEmpty),
        new("SetlistEnded", SetlistEnded)
    ];

    /// <summary>Forty songs in artist order, one selected — the ordinary state.</summary>
    public static FakeSongSelectScreen Library() => Select(new FakeSongSelectScreen(FakeSongLibrary.BuildLibrary(), true), "Take My Bones Away");

    /// <summary>First run: no library folder has been chosen yet.</summary>
    public static FakeSongSelectScreen Empty() => new([], libraryConfigured: false);

    /// <summary>The whole library at its realistic maximum, 2,160 songs, one selected mid-list.</summary>
    public static FakeSongSelectScreen Full() =>
        WithSetlists(Select(new FakeSongSelectScreen(FakeSongLibraryFull.Build(), true), "Master of Puppets"), FakeSetlistPanel.Many());

    /// <summary>Played songs only, best accuracy first, a full-combo song selected in its own tuning.</summary>
    public static FakeSongSelectScreen Played()
    {
        var screen = new FakeSongSelectScreen(FakeSongLibrary.BuildLibrary(), true);
        screen.Tick("Your record", "Played");
        screen.Sort(screen._columns[5]);
        return Select(screen, "Do I Wanna Know?");
    }

    /// <summary>The first songs of a library still being read.</summary>
    public static FakeSongSelectScreen Loading() => new(FakeSongLibrary.BuildLibrary().Take(7), true, loading: true);

    /// <summary>A stocked library with filters that match nothing.</summary>
    public static FakeSongSelectScreen NoMatch()
    {
        var screen = new FakeSongSelectScreen(FakeSongLibrary.BuildLibrary(), true);
        screen.Tick("Tuning", "Drop tunings");
        screen.Tick("Your record", "Full combo");
        screen.SearchText = "zzzzzzzz";
        return screen;
    }

    /// <summary>
    ///     The stress case: titles, artists and albums at the length real CDLC
    ///     reaches, capo on every row, and seven chips in the top bar.
    /// </summary>
    public static FakeSongSelectScreen LongText()
    {
        var screen = new FakeSongSelectScreen(FakeSongLibrary.BuildLongText(), true);
        foreach (var label in LongChipLabels) screen.AddChip(label);
        return WithSetlists(Select(screen, screen._allRows[0].Title), FakeSetlistPanel.LongText());
    }

    internal static readonly string[] LongChipLabels =
    [
        "Tuning · Drop C# / Db standard with a capo",
        "Ownership · Only songs I own a licence for",
        "Origin · Custom downloadable content (CDLC)",
        "Genre · Progressive metalcore and djent",
        "Paths · Lead, Rhythm, Bass and Combo",
        "Length · Longer than five minutes",
        "Record · Grade A or better, first try"
    ];

    // ── ISongSelectScreen ─────────────────────────────────────────────────

    public ISetlistPanel Setlists { get; private set; } = FakeSetlistPanel.None();

    public ICommand BackCommand { get; }

    public string SearchText
    {
        get => _searchText;
        set
        {
            if (!SetProperty(ref _searchText, value)) return;
            OnPropertyChanged(nameof(HasSearchText));
            ApplyFilter();
        }
    }

    public bool HasSearchText => _searchText.Length > 0;

    public ICommand ClearSearchCommand { get; }

    public string CountText => $"{_rows.Count.ToString("N0", CultureInfo.CurrentCulture)} of {_allRows.Count.ToString("N0", CultureInfo.CurrentCulture)} songs";

    public bool IsLoading { get; }

    public string LoadingText => IsLoading ? "Reading your library…" : "";

    public ICommand ChooseLibraryFolderCommand { get; }

    public ILibraryColumn SongColumn => _columns[0];
    public ILibraryColumn PathsColumn => _columns[1];
    public ILibraryColumn TuningColumn => _columns[2];
    public ILibraryColumn LengthColumn => _columns[3];
    public ILibraryColumn DifficultyColumn => _columns[4];
    public ILibraryColumn RecordColumn => _columns[5];

    public IReadOnlyList<IFilterChip> ActiveFilters => _activeFilters;

    public bool HasActiveFilters => _activeFilters.Count > 0;

    public ICommand ClearFiltersCommand { get; }

    public IReadOnlyList<ISongRow> Rows => _rows;

    public bool HasRows => _rows.Count > 0;

    public ISongRow? SelectedRow
    {
        get => _selectedRow;
        set
        {
            if (!SetProperty(ref _selectedRow, value)) return;
            foreach (var name in (string[])
                     [
                         nameof(HasSelection), nameof(HasBestRun), nameof(BestGradeLabel), nameof(BestAccuracyLabel), nameof(BestScoreLabel),
                         nameof(LastPlayedLabel), nameof(IsBestFullCombo), nameof(CanPlay)
                     ])
            {
                OnPropertyChanged(name);
            }

            RebuildArrangements();
        }
    }

    public bool HasSelection => _selectedRow != null;

    public object? DetailCover => null;

    public IReadOnlyList<IArrangementTile> Arrangements => _arrangements;

    public IArrangementTile? SelectedArrangement
    {
        get => _selectedArrangement;
        set
        {
            _selectedArrangement = value;
            foreach (var tile in _arrangements.OfType<FakeArrangementTile>())
                tile.SetSelected(ReferenceEquals(tile, value));

            // Republish unconditionally: the tile's own visual has already
            // flipped by the time this runs, so an unchanged value still needs a
            // notification to put it back.
            OnPropertyChanged(nameof(SelectedArrangement));
            foreach (var name in (string[])
                     [nameof(ExpectedTuningLabel), nameof(ExpectedTuningNotes), nameof(TuningCheckLabel), nameof(TuningNeedsChange), nameof(TuningIsVerified)])
            {
                OnPropertyChanged(name);
            }
        }
    }

    public bool HasBestRun => _selectedRow?.HasBest ?? false;

    public string BestGradeLabel => _selectedRow?.BestGradeLabel ?? "";

    public string BestAccuracyLabel => _selectedRow?.BestAccuracyLabel ?? "";

    public string BestScoreLabel => Selected is { HasBest: true } row ? row.BestScore.ToString("N0", CultureInfo.CurrentCulture) : "";

    public string LastPlayedLabel => Selected is { HasBest: true } row ? row.LastPlayedLabel : "";

    public bool IsBestFullCombo => _selectedRow?.IsFullCombo ?? false;

    public string ExpectedTuningLabel => Selected is not { } row ? ""
        : row.TuningLabel.Length > 0 ? row.TuningLabel : row.TuningName;

    public string ExpectedTuningNotes =>
        _selectedArrangement?.TuningLabel is { Length: > 0 } notes ? notes : _selectedRow?.TuningNotes ?? "";

    public bool TuningIsVerified => Selected is { } row && row.TuningName == GuitarTuning && row.TuningLabel.Length == 0;

    public bool TuningNeedsChange => Selected is not null && !TuningIsVerified;

    public string TuningCheckLabel => Selected is null ? ""
        : TuningIsVerified ? "Your guitar is in tune for this"
        : $"Retune from {GuitarTuning}";

    public ICommand PlayCommand { get; }

    public bool CanPlay => _selectedRow is not null;

    public bool ShowFirstRun => !_libraryConfigured && _allRows.Count == 0;

    public bool ShowNoMatch => _libraryConfigured && _allRows.Count > 0 && _rows.Count == 0;

    // ── the fake's own machinery ──────────────────────────────────────────

    private FakeSongRow? Selected => _selectedRow as FakeSongRow;

    private static FakeSongFilterOption Has(string path) => new(path, r => r.ArrangementNames.Contains(path));

    private static double Diff(FakeSongRow row) =>
        double.TryParse(row.DifficultyLabel, NumberStyles.Float, CultureInfo.CurrentCulture, out var d) ? d : 0;

    private void Tick(string column, string option) =>
        _columns.Single(c => c.Title == column).Options.Single(o => o.Label == option).IsSelected = true;

    private static FakeSongSelectScreen Select(FakeSongSelectScreen screen, string title)
    {
        screen.SelectedRow = screen._rows.FirstOrDefault(r => r.Title == title) ?? screen._rows.FirstOrDefault();
        return screen;
    }

    private void AddChip(string label)
    {
        FakeFilterChip chip = null!;
        chip = new FakeFilterChip(label, new RelayCommand(() =>
        {
            _extraChips.Remove(chip);
            ApplyFilter();
        }));
        _extraChips.Add(chip);
        ApplyFilter();
    }

    private void Sort(FakeSongColumn column)
    {
        _descending = ReferenceEquals(_sortedBy, column) ? !_descending : column.DescendingFirst;
        _sortedBy.SortGlyph = "";
        _sortedBy = column;
        column.SortGlyph = _descending ? "↓" : "↑";
        ApplyFilter();
    }

    private void ApplyFilter()
    {
        var query = _searchText.Trim();
        var kept = _allRows.Where(r => _columns.All(c => c.Admits(r))
                                       && (query.Length == 0
                                           || r.Title.Contains(query, StringComparison.CurrentCultureIgnoreCase)
                                           || r.Artist.Contains(query, StringComparison.CurrentCultureIgnoreCase)
                                           || r.Album.Contains(query, StringComparison.CurrentCultureIgnoreCase)));

        // Unknown values sort last in either direction.
        var key = _sortedBy.Key!;
        var ordered = kept.OrderBy(r => key(r) is null);
        var sorted = (_descending ? ordered.ThenByDescending(key) : ordered.ThenBy(key)).ThenBy(r => r.Title, StringComparer.CurrentCultureIgnoreCase);

        // Artist headings only make sense while the list is in artist order.
        _rows = ReferenceEquals(_sortedBy, _columns[0]) ? FakeSongLibrary.Grouped(sorted) : sorted.ToList();
        if (_selectedRow is FakeSongRow picked)
            _selectedRow = _rows.OfType<FakeSongRow>().FirstOrDefault(r => r.Title == picked.Title && r.Artist == picked.Artist);

        _activeFilters =
        [
            .. _columns.SelectMany(c => c.Options.Where(o => o.IsSelected)
                .Select(o => new FakeFilterChip($"{c.Title} · {o.Label}", new RelayCommand(() => o.IsSelected = false)))),
            .. _extraChips
        ];

        foreach (var name in (string[])
                 [
                     nameof(Rows), nameof(HasRows), nameof(CountText), nameof(ShowFirstRun), nameof(ShowNoMatch), nameof(ActiveFilters),
                     nameof(HasActiveFilters), nameof(SelectedRow), nameof(HasSelection), nameof(CanPlay)
                 ])
        {
            OnPropertyChanged(name);
        }

        if (_selectedRow is null && _arrangements.Count > 0) RebuildArrangements();
    }

    private void RebuildArrangements()
    {
        if (Selected is not { } row)
        {
            _arrangements = [];
            _selectedArrangement = null;
            OnPropertyChanged(nameof(Arrangements));
            OnPropertyChanged(nameof(SelectedArrangement));
            return;
        }

        var tiles = row.ArrangementNames
            .Select(name => new FakeArrangementTile(this, name, row.TuningName, row.TuningNotes, 40 + name.Length * 37))
            .ToList();
        _arrangements = tiles;
        OnPropertyChanged(nameof(Arrangements));
        SelectedArrangement = tiles.FirstOrDefault();
    }

    internal void Pick(FakeArrangementTile tile) => SelectedArrangement = tile;
}
