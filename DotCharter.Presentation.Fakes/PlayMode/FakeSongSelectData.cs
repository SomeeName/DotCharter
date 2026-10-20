using System.Globalization;
using System.Windows.Input;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using DotCharter.Presentation.Contracts.Library;
using DotCharter.Presentation.Contracts.PlayMode;

namespace DotCharter.Presentation.Fakes.PlayMode;

/// <summary>The rows and chips <see cref="FakeSongSelectScreen" />'s variants are built from.</summary>
internal static class FakeSongLibrary
{
    internal static IEnumerable<FakeSongRow> BuildLibrary()
    {
        // artist, title, album, tuning name, tuning notes, arrangements, length,
        // capo, difficulty, grade, accuracy, score, fc
        (string Artist, string Title, string Album, string Tuning, string Notes, string Arr, string Len,
            int Capo, double Diff, string Grade, double Acc, long Score, bool Fc)[] table =
        [
            ("Alice in Chains", "Them Bones", "Dirt", "Drop D", "D A D G B E", "Lead · Rhythm · Bass", "2:30", 0, 5.4, "A", 0.941, 812_400, false),
            ("Arctic Monkeys", "Do I Wanna Know?", "AM", "E Standard", "E A D G B E", "Lead · Bass", "4:32", 0, 3.8, "S", 0.982, 1_204_900, true),
            ("Baroness", "Take My Bones Away", "Yellow & Green", "C Standard", "C F A# D# G C", "Lead · Rhythm", "4:05", 0, 6.7, "", 0, 0, false),
            ("Black Sabbath", "Paranoid", "Paranoid", "E♭ Standard", "D# G# C# F# A# D#", "Lead · Rhythm · Bass", "2:48", 0, 4.1, "B", 0.883, 604_100, false),
            ("Black Sabbath", "Iron Man", "Paranoid", "E♭ Standard", "D# G# C# F# A# D#", "Lead · Rhythm · Bass", "5:56", 0, 3.6, "A", 0.921, 911_300, false),
            ("Chon", "Splash", "Grow", "E Standard", "E A D G B E", "Lead · Rhythm", "3:26", 0, 9.2, "", 0, 0, false),
            ("Deftones", "My Own Summer", "Around the Fur", "Drop C#", "C# G# C# F# A# D#", "Lead · Bass", "3:35", 0, 5.9, "C", 0.762, 401_250, false),
            ("Dream Theater", "Pull Me Under", "Images and Words", "E Standard", "E A D G B E", "Lead · Rhythm · Bass", "8:11", 0, 8.4, "B", 0.871, 1_884_000, false),
            ("Gojira", "Stranded", "Magma", "Drop C", "C G C F A D", "Lead · Rhythm · Bass", "4:00", 0, 7.8, "", 0, 0, false),
            ("Gorillaz", "Feel Good Inc.", "Demon Days", "E Standard", "E A D G B E", "Bass", "3:41", 0, 2.9, "S", 0.994, 702_800, true),
            ("Iron Maiden", "The Trooper", "Piece of Mind", "E Standard", "E A D G B E", "Lead · Rhythm · Bass", "4:12", 0, 6.2, "A", 0.928, 1_012_600, false),
            ("Jinjer", "Pisces", "King of Everything", "Drop A#", "A# F A# D# G C", "Lead · Rhythm · Bass", "4:44", 0, 7.1, "", 0, 0, false),
            ("Joe Satriani", "Always with Me, Always with You", "Surfing with the Alien", "E Standard", "E A D G B E", "Lead", "3:22", 0, 7.6, "B", 0.864, 553_900, false),
            ("John Mayer", "Neon", "Room for Squares", "E♭ Standard", "D# G# C# F# A# D#", "Lead", "4:20", 0, 8.9, "", 0, 0, false),
            ("Killswitch Engage", "My Curse", "The End of Heartache", "Drop C", "C G C F A D", "Lead · Rhythm · Bass", "3:45", 0, 6.0, "A", 0.913, 878_300, false),
            ("Led Zeppelin", "Kashmir", "Physical Graffiti", "DADGAD", "D A D G A D", "Lead · Rhythm · Bass", "8:31", 0, 5.2, "C", 0.781, 990_400, false),
            ("Mastodon", "Blood and Thunder", "Leviathan", "Drop D", "D A D G B E", "Lead · Rhythm · Bass", "3:49", 0, 6.8, "", 0, 0, false),
            ("Metallica", "Master of Puppets", "Master of Puppets", "E♭ Standard", "D# G# C# F# A# D#", "Lead · Rhythm · Bass", "8:35", 0, 8.1, "A", 0.906, 1_940_200, false),
            ("Metallica", "Enter Sandman", "Metallica", "E♭ Standard", "D# G# C# F# A# D#", "Lead · Rhythm · Bass", "5:31", 0, 5.0, "S", 0.975, 1_310_000, false),
            ("Motörhead", "Ace of Spades", "Ace of Spades", "E Standard", "E A D G B E", "Lead · Rhythm · Bass", "2:49", 0, 5.5, "S", 0.971, 688_100, true),
            ("Muse", "Hysteria", "Absolution", "E Standard", "E A D G B E", "Lead · Bass", "3:47", 0, 6.4, "B", 0.852, 741_500, false),
            ("Nirvana", "Come as You Are", "Nevermind", "E♭ Standard", "D# G# C# F# A# D#", "Lead · Rhythm · Bass", "3:39", 0, 2.4, "S", 0.988, 512_700, true),
            ("Opeth", "Ghost of Perdition", "Ghost Reveries", "D Standard", "D G C F A D", "Lead · Rhythm · Bass", "10:29", 0, 8.7, "", 0, 0, false),
            ("Pantera", "Cemetery Gates", "Cowboys from Hell", "E Standard", "E A D G B E", "Lead · Rhythm · Bass", "7:03", 0, 7.9, "C", 0.774, 1_120_300, false),
            ("Pink Floyd", "Comfortably Numb", "The Wall", "E Standard", "E A D G B E", "Lead · Rhythm · Bass", "6:23", 0, 5.1, "A", 0.934, 1_071_800, false),
            ("Polyphia", "Goat", "New Levels New Devils", "Drop C#", "C# G# C# F# A# D#", "Lead · Rhythm · Bass", "3:23", 0, 9.6, "", 0, 0, false),
            ("Queens of the Stone Age", "No One Knows", "Songs for the Deaf", "C Standard", "C F A# D# G C", "Lead · Rhythm · Bass", "4:38", 0, 4.6, "B", 0.869, 692_000, false),
            ("Radiohead", "Creep", "Pablo Honey", "E Standard", "E A D G B E", "Lead · Rhythm · Bass", "3:56", 0, 2.1, "S", 0.996, 455_900, true),
            ("Rage Against the Machine", "Killing in the Name", "Rage Against the Machine", "Drop D", "D A D G B E", "Lead · Rhythm · Bass", "5:14", 0, 4.9, "A", 0.917, 803_400, false),
            ("Red Hot Chili Peppers", "Can't Stop", "By the Way", "E Standard", "E A D G B E", "Lead · Rhythm · Bass", "4:29", 0, 6.1, "B", 0.848, 764_200, false),
            ("Rush", "YYZ", "Moving Pictures", "E Standard", "E A D G B E", "Lead · Rhythm · Bass", "4:26", 0, 8.3, "", 0, 0, false),
            ("Slipknot", "Duality", "Vol. 3: The Subliminal Verses", "Drop B", "B F# B E G# C#", "Lead · Rhythm · Bass", "4:12", 0, 5.8, "A", 0.925, 815_600, false),
            ("Soundgarden", "Black Hole Sun", "Superunknown", "E♭ Standard", "D# G# C# F# A# D#", "Lead · Rhythm · Bass", "5:18", 0, 4.3, "C", 0.759, 588_100, false),
            ("Steve Vai", "For the Love of God", "Passion and Warfare", "E Standard", "E A D G B E", "Lead", "6:03", 0, 0, "", 0, 0, false),
            ("System of a Down", "Chop Suey!", "Toxicity", "Drop C", "C G C F A D", "Lead · Rhythm · Bass", "3:30", 0, 5.6, "B", 0.877, 651_000, false),
            ("Tesseract", "King", "Sonder", "Drop F#", "F# C# F# B D# G#", "Lead · Rhythm · Bass", "6:16", 0, 8.8, "", 0, 0, false),
            ("The Beatles", "Blackbird", "The Beatles", "Open G", "D G D G B D", "Lead", "2:18", 3, 4.7, "A", 0.939, 288_400, false),
            ("The White Stripes", "Seven Nation Army", "Elephant", "E Standard", "E A D G B E", "Lead · Bass", "3:52", 0, 1.8, "S", 0.991, 402_300, true),
            ("Tool", "Schism", "Lateralus", "Drop D", "D A D G B E", "Lead · Rhythm · Bass", "6:47", 0, 7.4, "B", 0.858, 1_203_700, false),
            ("Van Halen", "Eruption", "Van Halen", "E♭ Standard", "D# G# C# F# A# D#", "Lead", "1:42", 0, 9.4, "", 0, 0, false),
            ("Wilco", "Impossible Germany", "Sky Blue Sky", "E Standard", "E A D G B E", "Lead · Rhythm · Bass", "5:57", 2, 6.5, "C", 0.792, 610_900, false)
        ];

        var day = 0;
        foreach (var t in table)
        {
            day++;
            yield return new FakeSongRow
            {
                Title = t.Title,
                Artist = t.Artist,
                Album = t.Album,
                TuningName = t.Tuning,
                TuningLabel = TuningCell(t.Tuning, t.Capo),
                TuningNotes = t.Notes,
                ArrangementNames = t.Arr.Split('·', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries),
                LengthText = t.Len,
                DifficultyLabel = t.Diff > 0 ? t.Diff.ToString("0.0", CultureInfo.CurrentCulture) : "—",
                HasBest = t.Grade.Length > 0,
                BestGradeLabel = t.Grade,
                BestAccuracyLabel = t.Acc > 0 ? t.Acc.ToString("P1", CultureInfo.CurrentCulture) : "",
                BestScore = t.Score,
                BestAccuracy = t.Acc,
                LastPlayedLabel = t.Grade.Length > 0 ? day + " days ago" : "",
                IsFullCombo = t.Fc
            };
        }
    }

    internal static IEnumerable<FakeSongRow> BuildLongText()
    {
        string[] titles =
        [
            FakeFlowText.LongSongTitle,
            "Symphony No. 4 in D Minor, Op. 120 — IV. Langsam; Lebhaft (arranged for two guitars and bass)",
            FakeFlowText.JapaneseSongTitle,
            FakeFlowText.GermanSongTitle,
            "I Wrote This Song About A Girl Who Left Me For A Drummer And It Has A Very Long Name"
        ];
        string[] artists =
        [
            FakeFlowText.LongArtist,
            "Sufjan Stevens and the Illinoisemaker Choir",
            "…And You Will Know Us by the Trail of Dead",
            "Die Ärzte",
            "The Brian Jonestown Massacre feat. a Very Long Guest Credit"
        ];
        string[] albums =
        [
            "A Deluxe Remastered Anniversary Edition With Bonus Material And Extended Liner Notes",
            "Live at the Royal Albert Hall, 12 November 1974 (Disc Two)",
            "Untitled (the one everybody calls the grey album)",
            "Ein sehr langer deutscher Albumtitel mit einem Untertitel dazu",
            "非常に長いアルバム名のテストケース（デラックス・エディション）"
        ];

        for (var i = 0; i < 12; i++)
        {
            yield return new FakeSongRow
            {
                Title = titles[i % titles.Length],
                Artist = artists[i % artists.Length],
                Album = albums[i % albums.Length],
                TuningName = i % 2 == 0 ? "C# Standard" : "DADGAD",
                TuningLabel = i % 2 == 0 ? "C# Std · capo 7" : "DADGAD · capo 7",
                TuningNotes = "C# G# C# F# A# D#",
                ArrangementNames = ["Lead", "Rhythm", "Bass", "Combo", "Alternate Lead"],
                LengthText = "18:42",
                DifficultyLabel = 10.0.ToString("0.0", CultureInfo.CurrentCulture),
                HasBest = true,
                BestGradeLabel = "S",
                BestAccuracyLabel = 0.9994.ToString("P1", CultureInfo.CurrentCulture),
                BestScore = 12_345_678,
                BestAccuracy = 0.9994,
                LastPlayedLabel = "eleven and a half months ago",
                IsFullCombo = true
            };
        }
    }

    /// <summary>The Library's rule: print a tuning only when it is not E Standard without a capo.</summary>
    internal static string TuningCell(string tuning, int capo) => (tuning, capo) switch
    {
        ("E Standard", 0) => "",
        (_, 0) => tuning,
        ("E Standard", _) => "E Std · capo " + capo.ToString(CultureInfo.InvariantCulture),
        _ => tuning + " · capo " + capo.ToString(CultureInfo.InvariantCulture)
    };

    /// <summary>Stamps each artist's name on the first row of its run.</summary>
    internal static List<FakeSongRow> Grouped(IEnumerable<FakeSongRow> rows)
    {
        var grouped = new List<FakeSongRow>();
        string? last = null;
        foreach (var row in rows)
        {
            grouped.Add(row with { GroupHeader = row.Artist == last ? "" : row.Artist, InArtistRun = true });
            last = row.Artist;
        }

        return grouped;
    }
}

/// <summary>A row of the fake library. A record, so a regrouped copy is one <c>with</c>.</summary>
public sealed record FakeSongRow : ISongRow
{
    public event System.ComponentModel.PropertyChangedEventHandler? PropertyChanged { add { } remove { } }

    public string GroupHeader { get; init; } = "";
    public bool HasGroupHeader => GroupHeader.Length > 0;
    public bool InArtistRun { get; init; }
    public string Title { get; init; } = "";
    public string Artist { get; init; } = "";
    public string Album { get; init; } = "";
    public string Subtitle => InArtistRun ? Album : $"{Artist} · {Album}";

    public string Monogram =>
        string.Concat(Artist.Split(' ', StringSplitOptions.RemoveEmptyEntries).Where(w => char.IsLetterOrDigit(w[0])).Take(2)
            .Select(w => char.ToUpperInvariant(w[0])));

    /// <summary>What a screen reader announces for the row, as the product's row does: Avalonia names a list
    /// item by its data's ToString, and a record's is every member.</summary>
    public override string ToString() => Artist.Length == 0 ? Title : $"{Title}, {Artist}";

    public object? Cover => null;
    public IReadOnlyList<string> ArrangementNames { get; init; } = [];
    /// <summary>One initial per path TYPE: an alternate lead is still a lead ("Alternate Lead" → "L").</summary>
    public IReadOnlyList<string> Paths => [.. ArrangementNames.Select(n => n.Split(' ')[^1][..1]).Distinct()];
    public string PathsSummary => string.Join(" · ", ArrangementNames);
    public string TuningName { get; init; } = "";
    public string TuningLabel { get; init; } = "";
    public string TuningNotes { get; init; } = "";
    public bool TuningNeedsAttention => TuningLabel.Length > 0;
    public string LengthText { get; init; } = "";
    public string DifficultyLabel { get; init; } = "—";
    public bool HasBest { get; init; }
    public string BestGradeLabel { get; init; } = "";
    public string BestAccuracyLabel { get; init; } = "";
    public bool IsFullCombo { get; init; }

    /// <summary>The raw score behind the rail's figure.</summary>
    public long BestScore { get; init; }

    /// <summary>The accuracy the record cell shows, and so the record column's sort key.</summary>
    public double BestAccuracy { get; init; }

    /// <summary>"4 days ago" — the rail's stamp.</summary>
    public string LastPlayedLabel { get; init; } = "";
}

/// <summary>One arrangement tile. Selection is the only thing that moves.</summary>
public sealed class FakeArrangementTile : ObservableObject, IArrangementTile
{
    private bool _isSelected;

    public FakeArrangementTile(FakeSongSelectScreen owner, string name, string tuningName,
        string tuningNotes, int noteCount)
    {
        Name = name;
        TuningName = tuningName;
        TuningLabel = tuningNotes;
        NoteCountLabel = noteCount.ToString("N0", CultureInfo.CurrentCulture) + " notes";
        SelectCommand = new RelayCommand(() => owner.Pick(this));
    }

    public string Name { get; }
    public string TuningLabel { get; }
    public string NoteCountLabel { get; }

    /// <summary>The resolved common-tuning NAME this tile expects.</summary>
    public string TuningName { get; }

    public bool IsSelected => _isSelected;

    public ICommand SelectCommand { get; }

    internal void SetSelected(bool value) => SetProperty(ref _isSelected, value, nameof(IsSelected));
}

/// <summary>An active filter and the ✕ that drops exactly it.</summary>
public sealed class FakeFilterChip(string label, ICommand removeCommand) : ObservableObject, IFilterChip
{
    public string Label { get; } = label;

    public ICommand RemoveCommand { get; } = removeCommand;
}

/// <summary>A funnel option over the fake's rows.</summary>
public sealed partial class FakeSongFilterOption(string label, Func<FakeSongRow, bool> matches) : ObservableObject, ILibraryFilterOption
{
    [ObservableProperty] private bool _isSelected;
    [ObservableProperty] private string _countLabel = "";

    public string Label => label;

    public Func<FakeSongRow, bool> Matches => matches;
}

/// <summary>A column header over the fake's rows: its sort key and its funnel.</summary>
public sealed partial class FakeSongColumn : ObservableObject, ILibraryColumn
{
    [ObservableProperty] private string _sortGlyph = "";

    public FakeSongColumn(string title, Func<FakeSongRow, IComparable>? key, IReadOnlyList<FakeSongFilterOption>? options = null,
        bool descendingFirst = false)
    {
        Title = title;
        Key = key;
        DescendingFirst = descendingFirst;
        Options = options ?? [];
        SortCommand = new RelayCommand(() => Sorted?.Invoke(this), () => key != null);
        ClearFilterCommand = new RelayCommand(() =>
        {
            foreach (var option in Options) option.IsSelected = false;
        });
        foreach (var option in Options)
        {
            option.PropertyChanged += (_, e) =>
            {
                if (e.PropertyName != nameof(FakeSongFilterOption.IsSelected)) return;
                OnPropertyChanged(nameof(IsFiltered));
                FilterChanged?.Invoke();
            };
        }
    }

    public event Action<FakeSongColumn>? Sorted;
    public event Action? FilterChanged;

    /// <summary>The sort key; null for a column that does not sort. A null key value sorts last either way.</summary>
    public Func<FakeSongRow, IComparable>? Key { get; }

    /// <summary>Best score and difficulty read best from the top: their first press sorts high to low.</summary>
    public bool DescendingFirst { get; }

    public IReadOnlyList<FakeSongFilterOption> Options { get; }
    /// <summary>Settable so a layout test can caption the column with its real localized string.</summary>
    public string Title { get; set; }
    public bool IsSortable => Key != null;
    public ICommand SortCommand { get; }
    public bool HasFilter => Options.Count > 0;
    public bool IsFiltered => Options.Any(o => o.IsSelected);
    public IReadOnlyList<ILibraryFilterOption> FilterOptions => Options;
    public ICommand ClearFilterCommand { get; }

    /// <summary>No ticked option means the column does not narrow; otherwise any ticked one must match.</summary>
    public bool Admits(FakeSongRow row) => !IsFiltered || Options.Any(o => o.IsSelected && o.Matches(row));
}
