// The Library contract's rows, columns and chips over the song browser's own types.
using System.Globalization;
using System.Windows.Input;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using DotCharter.Presentation.Contracts.Library;

namespace DotCharter.UI.ViewModels.Panels;

/// <summary>
///     One song as the Library prints it: only what differs from the common case — the paths
///     the song has, a tuning that means work first, a problem — and "—" for what is unknown.
/// </summary>
public sealed class LibraryRow : ObservableObject, ILibraryRow
{
    private readonly Func<bool> _inArtistRun;

    public LibraryRow(PsarcSongListItem item, Func<bool> inArtistRun)
    {
        Item = item;
        _inArtistRun = inArtistRun;
        item.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName == nameof(PsarcSongListItem.CoverImage))
            {
                OnPropertyChanged(nameof(Cover));
            }
            else if (e.PropertyName == nameof(PsarcSongListItem.GroupHeader))
            {
                OnPropertyChanged(nameof(GroupHeader));
                OnPropertyChanged(nameof(HasGroupHeader));
            }
        };
    }

    public PsarcSongListItem Item { get; }
    public string GroupHeader => Item.GroupHeader;
    public bool HasGroupHeader => Item.HasGroupHeader;
    public string Title => Item.Title;

    public string Subtitle => _inArtistRun() || string.IsNullOrWhiteSpace(Item.Artist)
        ? (Item.Album.Length > 0 ? Item.Album : Item.Artist)
        : Item.Album.Length > 0 ? $"{Item.Artist} · {Item.Album}" : Item.Artist;

    public string Monogram => Initials(Item.Artist);

    /// <summary>What a screen reader announces for the row: Avalonia names a list item by its
    /// data's ToString, so the row overrides it (the default is the type name "…LibraryRow").</summary>
    public override string ToString() =>
        string.IsNullOrWhiteSpace(Item.Artist) ? Item.Title : $"{Item.Title}, {Item.Artist}";
    public object? Cover => Item.CoverImage;
    public IReadOnlyList<string> Paths => Item.ArrangementInitials;
    public string PathsSummary => Item.ArrangementSummary;
    public string TuningLabel => Item.TuningNeedsAttention ? Item.TuningCellLabel : "";
    public bool TuningNeedsAttention => Item.TuningNeedsAttention;

    /// <summary>The cell's tooltip: the tuning with its strings' notes, then the capo — what the name alone hides.</summary>
    public string TuningTip => Item.TuningNeedsAttention
        ? string.Join(" · ", new[] { Item.TuningDisplay, Item.HasCapo ? Item.CapoSummary : "" }.Where(s => s.Length > 0))
        : "";
    public string YearLabel => Unknown(Item.YearLabel);
    public string DifficultyLabel => Unknown(Item.DifficultyLabel);
    public string LengthLabel => Item.SongLengthFormatted;
    public string StatusLabel => Item.CanLoad ? "" : Item.StatusLabel;
    public bool HasProblem => !Item.CanLoad;
    public string SourceTag => Item.SourceTag;
    public bool HasSourceTag => SourceTag.Length > 0;

    /// <summary>The subtitle depends on the sort; the owner calls this when the sort changes.</summary>
    internal void RefreshSubtitle() => OnPropertyChanged(nameof(Subtitle));

    internal static string Unknown(string value) => string.IsNullOrWhiteSpace(value) ? "—" : value;

    internal static string Initials(string artist) =>
        string.Concat(artist.Split(' ', StringSplitOptions.RemoveEmptyEntries).Take(2).Select(w => char.ToUpper(w[0], CultureInfo.CurrentCulture)));
}

/// <summary>
///     One path in the rail. Its tuning prints only where it differs from the song's: the song's
///     tuning is already the rail's first fact, and "E Standard" on every path row would say it five
///     times over.
/// </summary>
public sealed record LibraryPath(PsarcArrangementChoice Choice, string SongTuning = "") : ILibraryPath
{
    public string Label => Choice.Label;

    public string Detail => string.Join(" · ", new[]
    {
        Choice.TuningLabel == SongTuning ? "" : Choice.TuningLabel,
        Choice.HasNoteCount ? Choice.NoteCountLabel : ""
    }.Where(s => s.Length > 0));
}

public sealed record LibraryChip(string Label, ICommand RemoveCommand) : ILibraryFilterChip;

/// <summary>A facet option as a tickable filter row; the count is the facet's own, refreshed when a funnel opens.</summary>
public sealed class LibraryFilterOption : ObservableObject, ILibraryFilterOption
{
    private readonly BrowserFacetOption _option;

    public LibraryFilterOption(BrowserFacetOption option, string? prefix = null)
    {
        _option = option;
        Label = prefix is null ? option.Label : $"{prefix} · {option.Label}";
        option.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName == nameof(BrowserFacetOption.Count)) OnPropertyChanged(nameof(CountLabel));
            else if (e.PropertyName == nameof(BrowserFacetOption.IsChecked)) OnPropertyChanged(nameof(IsSelected));
        };
    }

    public string Label { get; }
    public string CountLabel => _option.Count.ToString(CultureInfo.CurrentCulture);

    public bool IsSelected
    {
        get => _option.IsChecked;
        set => _option.IsChecked = value;
    }
}

/// <summary>The lessons toggle as a Status filter row: ticked shows Guitarcade lessons, which are hidden by default.</summary>
public sealed class LibraryLessonsOption : ObservableObject, ILibraryFilterOption
{
    private readonly SongBrowserViewModel _browser;

    public LibraryLessonsOption(SongBrowserViewModel browser, string label)
    {
        _browser = browser;
        Label = label;
        browser.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName == nameof(SongBrowserViewModel.HideLessons)) OnPropertyChanged(nameof(IsSelected));
        };
    }

    public string Label { get; }
    public string CountLabel => "";

    public bool IsSelected
    {
        get => !_browser.HideLessons;
        set => _browser.HideLessons = !value;
    }
}

/// <summary>
///     A column header over one or more of the song browser's facets and its sort key. A column
///     that shares its popover between facets clears them through the browser's own one-pass
///     clear; a single facet clears itself (one pass either way).
/// </summary>
public sealed class LibraryColumn : ObservableObject, ILibraryColumn
{
    private readonly IReadOnlyList<(BrowserFacet Facet, string? Prefix)> _facets;
    private readonly ILibraryFilterOption? _extra;
    private IReadOnlyList<ILibraryFilterOption> _options = [];

    public LibraryColumn(SongBrowserViewModel browser, string title, string? sortKey, Func<string> glyph,
        IReadOnlyList<(BrowserFacet Facet, string? Prefix)> facets, ICommand? clear = null, ILibraryFilterOption? extra = null)
    {
        Title = title;
        IsSortable = sortKey != null;
        Glyph = glyph;
        _facets = facets;
        _extra = extra;
        SortCommand = new RelayCommand(() => browser.SortByColumnCommand.Execute(sortKey), () => sortKey != null);
        ClearFilterCommand = clear ?? (facets.Count == 1 ? facets[0].Facet.ClearFacetCommand : new RelayCommand(() => { }));
        foreach (var (facet, _) in facets)
        {
            facet.Options.CollectionChanged += (_, _) => RebuildOptions();
            facet.PropertyChanged += (_, e) =>
            {
                if (e.PropertyName == nameof(BrowserFacet.IsActive)) OnPropertyChanged(nameof(IsFiltered));
            };
        }

        extra?.PropertyChanged += (_, _) => OnPropertyChanged(nameof(IsFiltered));
        RebuildOptions();
    }

    internal Func<string> Glyph { get; }
    public string Title { get; }
    public bool IsSortable { get; }

    /// <summary>The browser draws ▲ / ▼; the Library's header draws arrows, so a sort never reads as a second funnel.</summary>
    public string SortGlyph => Glyph() switch { "▲" => "↑", "▼" => "↓", _ => "" };

    public ICommand SortCommand { get; }
    public bool HasFilter => _facets.Count > 0 || _extra != null;
    public bool IsFiltered => _facets.Any(f => f.Facet.IsActive) || _extra is { IsSelected: true };
    public IReadOnlyList<ILibraryFilterOption> FilterOptions => _options;
    public ICommand ClearFilterCommand { get; }

    internal void RefreshSort() => OnPropertyChanged(nameof(SortGlyph));

    private void RebuildOptions()
    {
        var options = _facets.SelectMany(f => f.Facet.Options.Select(o => (ILibraryFilterOption)new LibraryFilterOption(o, f.Prefix))).ToList();
        if (_extra != null) options.Add(_extra);
        _options = options;
        OnPropertyChanged(nameof(FilterOptions));
        OnPropertyChanged(nameof(HasFilter));
    }
}
