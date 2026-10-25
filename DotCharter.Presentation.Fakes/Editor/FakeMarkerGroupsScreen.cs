using System.Globalization;
using System.Windows.Input;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using DotCharter.Presentation.Contracts.Editor;

namespace DotCharter.Presentation.Fakes.Editor;

/// <summary>
///     A services-free <see cref="IMarkerGroupsScreen" />: a two-arrangement song's sections, its
///     tones (one placed with no definition, one rename refused), the empty list, long German and
///     Japanese names, a dense chart's sixty sections, a rename in progress and folded rows.
/// </summary>
public sealed class FakeMarkerGroupsScreen : ObservableObject, IMarkerGroupsScreen
{
    private FakeMarkerGroupsScreen(IReadOnlyList<FakeMarkerGroupRow> groups, string emptyText, string statusLine = "",
        string title = "Sections")
    {
        Title = title;
        Groups = groups;
        EmptyText = emptyText;
        StatusLine = statusLine;
    }

    public static IReadOnlyList<FakeVariant<IMarkerGroupsScreen>> Variants { get; } =
    [
        new(FakeVariantNames.Default, Default),
        new("Tones", Tones),
        new(FakeVariantNames.Empty, Empty),
        new(FakeVariantNames.LongText, LongText),
        new(FakeVariantNames.Full, Full),
        new("Renaming", Renaming),
        new("Folded", Folded)
    ];

    private const string NoSections = "No sections yet. Place one on the Sections lane.";

    /// <summary>"#2 · bar 9 · 0:16.0" at 120 BPM in 4/4, as the app labels a place.</summary>
    public static string PlaceLabel(int ordinal, double seconds) =>
        string.Create(CultureInfo.InvariantCulture,
            $"#{ordinal} · bar {(int)(seconds / 2) + 1} · {TimeSpan.FromSeconds(seconds):m\\:ss\\.f}");

    public static FakeMarkerGroupsScreen Default() => new(Sections(), NoSections);

    public static FakeMarkerGroupsScreen Tones()
    {
        var refused = Tone("lead_dist", [8.0, 40.0]);
        refused.EditName = "lead_clean";
        refused.Error = "“lead_clean” is already a tone. Pick another name.";
        return new FakeMarkerGroupsScreen(
        [
            new FakeMarkerGroupRow("lead_clean", "2 uses", [new FakeMarkerOccurrenceRow("Starting tone · Lead"),
                    new FakeMarkerOccurrenceRow(PlaceLabel(1, 24.0) + " · Lead")],
                "Delete “lead_clean”: removes all 2 of its uses and its definition. Ctrl+Z undoes it.", ""),
            refused,
            Tone("solo", [56.0], "Placed, but no tone has this name: the game plays its default tone here."),
            new FakeMarkerGroupRow("spare_clean", "0 uses", [], "Delete “spare_clean”: removes its definition. Ctrl+Z undoes it.", "")
        ], "No tones yet.", title: "Tones");
    }

    /// <summary>The list as it reads after its last section was deleted.</summary>
    public static FakeMarkerGroupsScreen Empty() => new([], NoSections, "Deleted “outro”. Ctrl+Z brings it back.");

    public static FakeMarkerGroupsScreen LongText() => new(
    [
        Section("Strophe mit ausgedehntem Gitarrensolo und Wiederholung", [4.0, 68.0]),
        Section("ギターソロ付きの長いブリッジ部分（二回目）", [40.0]),
        Section("Refrain", [20.0, 84.0])
    ], NoSections);

    /// <summary>A dense chart: sixty section names, long enough that the list virtualizes.</summary>
    public static FakeMarkerGroupsScreen Full() =>
        new([.. Enumerable.Range(0, 60).Select(i => Section($"section {i:00}", [2.0 + i * 4]))], NoSections);

    public static FakeMarkerGroupsScreen Renaming()
    {
        var song = Sections();
        song[1].EditName = "verse A";
        return new FakeMarkerGroupsScreen(song, NoSections, "Renamed “chorus 1” to “chorus”.");
    }

    public static FakeMarkerGroupsScreen Folded()
    {
        var song = Sections();
        foreach (var group in song.Skip(1)) group.IsExpanded = false;
        return new FakeMarkerGroupsScreen(song, NoSections);
    }

    private static List<FakeMarkerGroupRow> Sections() =>
    [
        Section("intro", [0.0]),
        Section("verse", [8.0, 40.0]),
        Section("chorus", [24.0, 56.0, 88.0]),
        Section("bridge", [72.0]),
        Section("outro", [104.0])
    ];

    private static FakeMarkerGroupRow Section(string name, double[] seconds)
    {
        var places = seconds.Select((t, i) => new FakeMarkerOccurrenceRow(PlaceLabel(i + 1, t) + " · Lead, Rhythm")).ToList();
        var tip = places.Count == 1
            ? $"Delete “{name}”: removes its 1 place. Ctrl+Z undoes it."
            : $"Delete “{name}”: removes all {places.Count} of its places. Ctrl+Z undoes it.";
        return new FakeMarkerGroupRow(name, places.Count == 1 ? "1 place" : $"{places.Count} places", places, tip, "");
    }

    private static FakeMarkerGroupRow Tone(string name, double[] seconds, string warning = "")
    {
        var places = seconds.Select((t, i) => new FakeMarkerOccurrenceRow(PlaceLabel(i + 1, t) + " · Lead")).ToList();
        var tip = places.Count == 1
            ? $"Delete “{name}”: removes its 1 use and its definition. Ctrl+Z undoes it."
            : $"Delete “{name}”: removes all {places.Count} of its uses and its definition. Ctrl+Z undoes it.";
        return new FakeMarkerGroupRow(name, places.Count == 1 ? "1 use" : $"{places.Count} uses", places, tip, warning);
    }

    public string Title { get; }
    public IReadOnlyList<IMarkerGroupRow> Groups { get; }
    public bool HasGroups => Groups.Count > 0;
    public string EmptyText { get; }
    public string StatusLine { get; }
    public ICommand RenameCommand { get; } = new RelayCommand<object?>(_ => { });
    public ICommand CancelRenameCommand { get; } = new RelayCommand<IMarkerGroupRow?>(row => row?.EditName = row.Name);
    public ICommand DeleteCommand { get; } = new RelayCommand<object?>(_ => { });
    public ICommand JumpToCommand { get; } = new RelayCommand<object?>(_ => { });
}

/// <summary>A name row whose rename box and fold work, with nothing behind them.</summary>
public sealed partial class FakeMarkerGroupRow(string name, string countLabel, IReadOnlyList<IMarkerOccurrenceRow> occurrences,
    string deleteTip, string warning) : ObservableObject, IMarkerGroupRow
{
    public string Name { get; } = name;
    public string CountLabel { get; } = countLabel;
    public IReadOnlyList<IMarkerOccurrenceRow> Occurrences { get; } = occurrences;
    public string DeleteTip { get; } = deleteTip;
    public string Warning { get; } = warning;
    public bool HasWarning => Warning.Length > 0;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsRenameDirty))]
    private string _editName = name;

    public bool IsRenameDirty => EditName.Trim().Length > 0 && EditName.Trim() != Name;

    [ObservableProperty] private bool _isExpanded = true;

    [ObservableProperty][NotifyPropertyChangedFor(nameof(HasError))] private string _error = "";

    public bool HasError => Error.Length > 0;
}

/// <summary>A place row.</summary>
public sealed class FakeMarkerOccurrenceRow(string label) : IMarkerOccurrenceRow
{
    public string Label { get; } = label;
}
