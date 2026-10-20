using System.Windows.Input;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using DotCharter.Presentation.Contracts.Library;
using DotCharter.Presentation.Fakes.PlayMode;

namespace DotCharter.Presentation.Fakes.Library;

public sealed record FakeLibraryRow : ILibraryRow
{
    public event System.ComponentModel.PropertyChangedEventHandler? PropertyChanged { add { } remove { } }
    public object? Cover => null;
    public string GroupHeader { get; init; } = "";
    public bool HasGroupHeader => GroupHeader.Length > 0;
    public bool InArtistRun { get; init; }
    public required string Title { get; init; }
    public required string Artist { get; init; }
    public string Album { get; init; } = "";
    public string Subtitle => InArtistRun ? Album : $"{Artist} · {Album}";
    public string Monogram => string.Concat(Artist.Split(' ', StringSplitOptions.RemoveEmptyEntries).Take(2).Select(w => char.ToUpperInvariant(w[0])));
    public IReadOnlyList<string> Paths { get; init; } = ["L", "R", "B"];

    /// <summary>What a screen reader announces for the row, as the product's row does: Avalonia names a list
    /// item by its data's ToString, and a record's is every member.</summary>
    public override string ToString() => Artist.Length == 0 ? Title : $"{Title}, {Artist}";

    public string PathsSummary => string.Join(" · ", Paths.Select(p => p switch { "L" => "Lead", "R" => "Rhythm", "B" => "Bass", _ => "Vocals" }));
    public string TuningLabel { get; init; } = "";
    public bool TuningNeedsAttention { get; init; }
    public string TuningTip { get; init; } = "";
    /// <summary>The tuning facet option this row answers ("Drop D (D A D G B E)"); empty is the unknown bucket.</summary>
    public string TuningOption { get; init; } = "";
    public int? Capo { get; init; }
    public string YearLabel { get; init; } = "—";
    public string DifficultyLabel { get; init; } = "—";
    public string LengthLabel { get; init; } = "";
    public string StatusLabel { get; init; } = "";
    public bool HasProblem => StatusLabel.Length > 0;
    public string SourceTag { get; init; } = "";
    public bool HasSourceTag => SourceTag.Length > 0;
    public string Problem { get; init; } = "";
}

public sealed record FakeLibraryPath(string Label, string Detail) : ILibraryPath;

public sealed record FakeLibraryChip(string Label, ICommand RemoveCommand) : ILibraryFilterChip;

public sealed partial class FakeLibraryFilterOption(string label, Func<FakeLibraryRow, bool> matches) : ObservableObject, ILibraryFilterOption
{
    [ObservableProperty] private bool _isSelected;
    [ObservableProperty] private string _countLabel = "";

    public string Label => label;

    public Func<FakeLibraryRow, bool> Matches => matches;
}

public sealed partial class FakeLibraryColumn : ObservableObject, ILibraryColumn
{
    [ObservableProperty] private string _sortGlyph = "";

    public FakeLibraryColumn(string title, bool sortable, Func<FakeLibraryRow, string> key, IReadOnlyList<FakeLibraryFilterOption>? options = null)
    {
        Title = title;
        IsSortable = sortable;
        Key = key;
        Options = options ?? [];
        SortCommand = new RelayCommand(() => Sorted?.Invoke(this), () => sortable);
        ClearFilterCommand = new RelayCommand(() =>
        {
            foreach (var option in Options) option.IsSelected = false;
        });
        foreach (var option in Options)
        {
            option.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName != nameof(FakeLibraryFilterOption.IsSelected)) return;
            OnPropertyChanged(nameof(IsFiltered));
            FilterChanged?.Invoke();
        };
        }
    }

    public event Action<FakeLibraryColumn>? Sorted;
    public event Action? FilterChanged;

    public Func<FakeLibraryRow, string> Key { get; }
    public IReadOnlyList<FakeLibraryFilterOption> Options { get; }
    /// <summary>Settable so a layout test can caption the column with its real localized string.</summary>
    public string Title { get; set; }
    public bool IsSortable { get; }
    public ICommand SortCommand { get; }
    public bool HasFilter => Options.Count > 0;
    public bool IsFiltered => Options.Any(o => o.IsSelected);
    public IReadOnlyList<ILibraryFilterOption> FilterOptions => Options;
    public ICommand ClearFilterCommand { get; }

    /// <summary>No ticked option means the column does not narrow; otherwise any ticked one must match.</summary>
    public bool Admits(FakeLibraryRow row) => !IsFiltered || Options.Any(o => o.IsSelected && o.Matches(row));
}

/// <summary>The rows the Library fake shows: a real-sized library, and the stress case.</summary>
internal static class FakeLibraryData
{
    internal static IReadOnlyList<FakeLibraryRow> Library() =>
    [
        Row("Blind Melon", "No Rain", "Blind Melon", "1992", "3:47"),
        Row("blink-182", "All the Small Things", "Enema of the State", "1999", "3:00"),
        Row("blink-182", "Dammit", "Dude Ranch", "1997", "2:56", diff: "4"),
        Row("blink-182", "What's My Age Again?", "Enema of the State", "1999", "2:39"),
        Row("Blondie", "Call Me", "Call Me", "1980", "3:42", paths: ["L", "R", "B", "V"]),
        Row("Blue Öyster Cult", "(Don't Fear) The Reaper", "Agents of Fortune", "1976", "5:17", diff: "6"),
        Row("Blue Öyster Cult", "Burnin' for You", "Fire of Unknown Origin", "1981", "4:40"),
        Row("Blue Öyster Cult", "Godzilla", "Spectres", "1977", "3:53", tuning: "Eb Standard"),
        Row("Bob Marley & The Wailers", "Three Little Birds", "Exodus", "1977", "3:06", paths: ["L", "R", "B", "V"]),
        Row("Deftones", "My Own Summer", "Around the Fur", "1997", "3:35", tuning: "Drop C#", diff: "6"),
        Row("Gojira", "Stranded", "Magma", "2016", "4:00", tuning: "Drop C", diff: "8"),
        Row("Led Zeppelin", "Kashmir", "Physical Graffiti", "1975", "8:31", tuning: "DADGAD"),
        Row("Nirvana", "Come as You Are", "Nevermind", "1991", "3:39", tuning: "Eb Standard", diff: "2"),
        Row("Radiohead", "Creep", "Pablo Honey", "1992", "3:56", tuning: "E Std · capo 2"),
        Row("Radiohead", "Paranoid Android", "OK Computer", "—", "6:23", status: "Can't read",
            problem: "The archive has no song manifest — it was cut short or damaged. Check library lists what else is affected."),
        Row("Tame Impala", "The Less I Know the Better", "Currents", "2015", "3:36", paths: ["B"])
    ];

    internal static IReadOnlyList<FakeLibraryRow> LongText() =>
    [
        Row(FakeFlowText.LongArtist, FakeFlowText.LongSongTitle, "Complete Recordings, Remastered Deluxe Anniversary Edition", "2004", "12:47",
            paths: ["L", "R", "B", "V"], tuning: "C# Std · capo 3", diff: "9"),
        Row("Die Ärzte", FakeFlowText.GermanSongTitle, "Geräusch (Live in der Wuhlheide, erweiterte Fassung)", "2003", "4:12", tuning: "Eb Standard"),
        Row("東京事変", FakeFlowText.JapaneseSongTitle, "大発見", "2011", "3:58", paths: ["L", "B"]),
        Row("東京事変", "閃光少女", "娯楽", "2007", "3:08", status: "Unlesbar",
            problem: "Dem Archiv fehlt das Song-Manifest – es wurde abgeschnitten oder ist beschädigt. „Bibliothek prüfen“ listet, was sonst betroffen ist."),
        Row("Polkadot Stingray", "Sakasama", "Wanderlust", "2020", "3:03", paths: ["L", "R", "B", "V"])
    ];

    /// <summary>
    ///     A library with DotCharter song packages (<c>.dcsong</c>) beside the Rocksmith archives:
    ///     one exported from an archive the list also holds, one with a long title, and one with
    ///     Opus audio on a computer without ffmpeg, which shows disabled with its reason.
    /// </summary>
    internal static IReadOnlyList<FakeLibraryRow> WithPackages() =>
    [
        .. Library().Take(9),
        Row("Bob Marley & The Wailers", "Three Little Birds", "Exodus", "1977", "3:06", paths: ["L", "R", "B", "V"], diff: "3") with { SourceTag = "DotCharter song" },
        Row("Deftones", "My Own Summer (Shove It) — Live at the Warfield, San Francisco, Remastered", "Around the Fur (Deluxe)", "1997", "3:35",
            tuning: "Drop C#", diff: "6") with { SourceTag = "DotCharter song" },
        Row("Gojira", "Silvera", "Magma", "2016", "3:33", tuning: "Drop C", status: "Needs ffmpeg",
            problem: "This song package holds Opus audio, which DotCharter plays through ffmpeg. Put ffmpeg on your PATH, or ffmpeg.exe in the app's tools folder, then refresh the Library.") with { SourceTag = "DotCharter song" },
        .. Library().Skip(9)
    ];

    /// <summary>What the archive says per path: an unreadable one says nothing at all.</summary>
    internal static IReadOnlyList<FakeLibraryPath> PathsOf(FakeLibraryRow row) => row.HasProblem
        ? []
        :
        [
            .. row.Paths.Select(p => p switch
            {
                "L" => new FakeLibraryPath("Lead", "325 notes"),
                "R" => new FakeLibraryPath("Rhythm", "206 notes"),
                "B" => new FakeLibraryPath("Bass", "390 notes"),
                _ => new FakeLibraryPath("Vocals", "Lyrics")
            })
        ];

    /// <summary>Stamps each artist's name on the first row of its run.</summary>
    internal static List<FakeLibraryRow> Grouped(IEnumerable<FakeLibraryRow> rows)
    {
        var grouped = new List<FakeLibraryRow>();
        string? last = null;
        foreach (var row in rows)
        {
            grouped.Add(row with { GroupHeader = row.Artist == last ? "" : row.Artist, InArtistRun = true });
            last = row.Artist;
        }

        return grouped;
    }

    /// <summary>The archive's file name the fake prints in the rail and sorts the pack order by.</summary>
    internal static string ArchiveName(FakeLibraryRow row) => row.HasSourceTag
        ? $"{row.Artist} - {row.Title}.dcsong"
        : $"{new string(row.Title.Where(char.IsLetterOrDigit).Take(10).ToArray()).ToLowerInvariant()}_p.psarc";

    /// <summary>The strings' notes beside each tuning the rows print, as the app's tooltip shows them.</summary>
    private static readonly Dictionary<string, string> TuningTips = new(StringComparer.Ordinal)
    {
        ["Eb Standard"] = "Eb Standard (D# G# C# F# A# D#)",
        ["Drop C#"] = "Drop C# (C# G# C# F# A# D#)",
        ["Drop C"] = "Drop C (C G C F A D)",
        ["DADGAD"] = "DADGAD (D A D G A D)",
        ["E Std · capo 2"] = "E Standard (E A D G B E) · capo 2",
        ["C# Std · capo 3"] = "C# Standard (C# F# B E G# C#) · capo 3"
    };

    private static FakeLibraryRow Row(string artist, string title, string album, string year, string length,
        IReadOnlyList<string>? paths = null, string tuning = "", string diff = "—", string status = "", string problem = "") =>
        new()
        {
            Artist = artist, Title = title, Album = album, YearLabel = year, LengthLabel = length, Paths = paths ?? ["L", "R", "B"],
            TuningLabel = tuning, TuningNeedsAttention = tuning.Length > 0, TuningTip = TuningTips.GetValueOrDefault(tuning, tuning),
            DifficultyLabel = diff, StatusLabel = status, Problem = problem
        };
}
