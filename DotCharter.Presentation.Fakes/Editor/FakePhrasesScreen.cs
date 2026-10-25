using System.Windows.Input;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using DotCharter.Presentation.Contracts.Editor;

namespace DotCharter.Presentation.Fakes.Editor;

/// <summary>
///     A services-free <see cref="IPhrasesScreen" />: a song's phrases as an editor-placed chart
///     keeps them (with the export note), the empty editor, long German and Japanese names, an
///     imported chart's thirty phrases, a rename in progress, an armed Propagate and folded groups.
/// </summary>
public sealed class FakePhrasesScreen : ObservableObject, IPhrasesScreen
{
    private FakePhrasesScreen(IReadOnlyList<FakePhraseGroupRow> phrases, string exportNote, string statusLine = "")
    {
        Phrases = phrases;
        ExportNote = exportNote;
        StatusLine = statusLine;
    }

    public static IReadOnlyList<FakeVariant<IPhrasesScreen>> Variants { get; } =
    [
        new(FakeVariantNames.Default, Default),
        new(FakeVariantNames.Empty, Empty),
        new(FakeVariantNames.LongText, LongText),
        new(FakeVariantNames.Full, Full),
        new("Renaming", Renaming),
        new("ConfirmingPropagate", ConfirmingPropagate),
        new("Folded", Folded)
    ];

    private const string MarkersExportNote = "On export: COUNT is added at the start and END after the last note.";

    public static FakePhrasesScreen Default() => new(Song(), MarkersExportNote);

    public static FakePhrasesScreen Empty() => new([], "");

    public static FakePhrasesScreen LongText() => new(
    [
        Group("Strophe mit ausgedehntem Gitarrensolo und Wiederholung", 3, 4.0),
        Group("ギターソロ付きの長いブリッジ部分（二回目）", 2, 40.0),
        Group("Refrain", 1, 70.0)
    ], "Beim Export: COUNT wird am Anfang und END nach der letzten Note ergänzt.");

    /// <summary>An imported chart: thirty phrases from the explicit store, which carries its own COUNT and END.</summary>
    public static FakePhrasesScreen Full() =>
        new([.. Enumerable.Range(0, 30).Select(i => Group($"phrase{i:00}", 1 + i % 4, 2.0 + i * 6))], "");

    public static FakePhrasesScreen Renaming()
    {
        var song = Song();
        song[1].EditName = "verse A";
        return new FakePhrasesScreen(song, MarkersExportNote);
    }

    public static FakePhrasesScreen ConfirmingPropagate()
    {
        var song = Song();
        ((FakePhraseIterationRow)song[2].Iterations[0]).IsConfirming = true;
        return new FakePhrasesScreen(song, MarkersExportNote,
            "Click again to copy this iteration into the 2 other iterations of the phrase.");
    }

    public static FakePhrasesScreen Folded()
    {
        var song = Song();
        foreach (var group in song.Skip(1)) group.IsExpanded = false;
        return new FakePhrasesScreen(song, MarkersExportNote);
    }

    private static List<FakePhraseGroupRow> Song() =>
    [
        Group("intro", 1, 0.0),
        Group("verse", 2, 4.0),
        Group("chorus", 3, 12.0),
        Group("bridge", 1, 36.0),
        Group("outro", 1, 52.0)
    ];

    private static FakePhraseGroupRow Group(string name, int iterations, double firstSeconds)
    {
        var rows = Enumerable.Range(0, iterations)
            .Select(i => new FakePhraseIterationRow(FakeMarkerGroupsScreen.PlaceLabel(i + 1, firstSeconds + i * 16), iterations > 1))
            .ToList<IPhraseIterationRow>();
        var deleteTip = iterations == 1
            ? $"Delete “{name}”: removes its 1 iteration. Ctrl+Z undoes it."
            : $"Delete “{name}”: removes all {iterations} of its iterations. Ctrl+Z undoes it.";
        return new FakePhraseGroupRow(name, iterations == 1 ? "1 iteration" : $"{iterations} iterations", rows, deleteTip);
    }

    public IReadOnlyList<IPhraseGroupRow> Phrases { get; }
    public bool HasPhrases => Phrases.Count > 0;
    public string StatusLine { get; }
    public string ExportNote { get; }
    public ICommand RefreshCommand { get; } = new RelayCommand(() => { });
    public ICommand RenamePhraseCommand { get; } = new RelayCommand<object?>(_ => { });
    public ICommand CancelRenameCommand { get; } = new RelayCommand<IPhraseGroupRow?>(row => row?.EditName = row.Name);
    public ICommand DeletePhraseCommand { get; } = new RelayCommand<object?>(_ => { });
    public ICommand JumpToCommand { get; } = new RelayCommand<object?>(_ => { });
    public ICommand PropagateCommand { get; } = new RelayCommand<object?>(_ => { });
}

/// <summary>A phrase row whose rename box and fold work, with nothing behind them.</summary>
public sealed partial class FakePhraseGroupRow(string name, string countLabel, IReadOnlyList<IPhraseIterationRow> iterations,
    string deleteTip) : ObservableObject, IPhraseGroupRow
{
    public string Name { get; } = name;
    public string CountLabel { get; } = countLabel;
    public IReadOnlyList<IPhraseIterationRow> Iterations { get; } = iterations;
    public string DeleteTip { get; } = deleteTip;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsRenameDirty))]
    private string _editName = name;

    public bool IsRenameDirty => EditName.Trim().Length > 0 && EditName.Trim() != Name;

    [ObservableProperty] private bool _isExpanded = true;
}

/// <summary>An iteration row.</summary>
public sealed partial class FakePhraseIterationRow(string label, bool canPropagate) : ObservableObject, IPhraseIterationRow
{
    public string Label { get; } = label;
    public bool CanPropagate { get; } = canPropagate;

    [ObservableProperty] private bool _isConfirming;
}
