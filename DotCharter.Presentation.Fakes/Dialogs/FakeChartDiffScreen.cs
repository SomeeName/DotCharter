using System.Windows.Input;
using DotCharter.Presentation.Contracts.Dialogs;

namespace DotCharter.Presentation.Fakes.Dialogs;

/// <summary>A services-free <see cref="IChartDiffScreen" />; Close answers true, Browse does nothing.</summary>
public sealed class FakeChartDiffScreen : FakeDialog<bool>, IChartDiffScreen
{
    private int _selectedIndex;

    private FakeChartDiffScreen(string fileName, IReadOnlyList<string> arrangements, IReadOnlyList<IChartDiffRow> rows,
        string summary, string status)
    {
        ComparedFileName = fileName;
        ComparableArrangements = arrangements;
        _selectedIndex = arrangements.Count > 0 ? 0 : -1;
        Rows = rows;
        Summary = summary;
        Status = status;
        BrowseFileCommand = Disabled();
        CloseCommand = Answer(true);
    }

    public static IReadOnlyList<FakeVariant<IChartDiffScreen>> Variants { get; } =
    [
        new(FakeVariantNames.Default, Default),
        new(FakeVariantNames.Empty, Empty),
        new(FakeVariantNames.LongText, LongText),
        new(FakeVariantNames.Full, Full),
        new(FakeVariantNames.Error, Error)
    ];

    public static FakeChartDiffScreen Default() => new("hysteria_v3.dcproj", ["Lead (Lead)", "Rhythm (Rhythm)", "Bass (Bass)"],
    [
        new Row("Added", "Level 12 · 0:41.250", "Note on string 3", "Fret 7", "added"),
        new Row("Removed", "Level 12 · 0:42.000", "Chord", "3 notes", "removed"),
        new Row("Changed", "Level 12 · 0:43.500", "Note on string 1", "Sustain, Bend", "changed"),
        new Row("Changed", "—", "Chord template", "Name, Fingers", "changed")
    ], "1 added · 1 removed · 2 changed", "");

    /// <summary>Nothing picked yet: the first thing anyone sees.</summary>
    public static FakeChartDiffScreen Empty() => new("", [], [], "", "Pick a project file to compare with.");

    public static FakeChartDiffScreen LongText() => new(
        "Hysterie – Liveaufnahme mit alternativer Stimmung (dritte überarbeitete Fassung).dcproj",
        ["Leadgitarre mit alternativer Stimmung (Lead)", "リズムギター（ドロップD・カポ2）(Rhythm)"],
        [
            new Row("Hinzugefügt", "Stufe 12 · 0:41.250", "Note auf Saite 3", "Bund 7, Sustain, Vibrato, Akzent", "added"),
            new Row("変更", "レベル 12 · 0:43.500", "コード", "運指、名前、ハンドシェイプの長さ", "changed")
        ], "1 hinzugefügt · 0 entfernt · 1 geändert", "");

    /// <summary>A re-charted song: the table scrolls.</summary>
    public static FakeChartDiffScreen Full() => new("hysteria_v1.dcproj", ["Lead (Lead)"],
    [
        .. Enumerable.Range(0, 60).Select(IChartDiffRow (i) => (i % 3) switch
        {
            0 => new Row("Added", $"Level {i % 20} · 0:{i:00}.250", $"Note on string {i % 6 + 1}", $"Fret {i % 22}", "added"),
            1 => new Row("Removed", $"Level {i % 20} · 0:{i:00}.500", "Chord", "4 notes", "removed"),
            _ => new Row("Changed", $"Level {i % 20} · 0:{i:00}.750", "Anchor", "Fret, Width", "changed")
        })
    ], "20 added · 20 removed · 20 changed", "");

    /// <summary>A file that would not load: the reason, and nothing to compare.</summary>
    public static FakeChartDiffScreen Error() => new("", [], [], "",
        "Could not load the file: the project was written by a newer version of DotCharter.");

    public string ComparedFileName { get; }

    public ICommand BrowseFileCommand { get; }

    public bool HasComparableArrangements => ComparableArrangements.Count > 0;

    public IReadOnlyList<string> ComparableArrangements { get; }

    public int SelectedArrangementIndex
    {
        get => _selectedIndex;
        set => SetProperty(ref _selectedIndex, value);
    }

    public bool HasResults => Rows.Count > 0;

    public IReadOnlyList<IChartDiffRow> Rows { get; }

    public string Summary { get; }

    public string Status { get; }

    public ICommand CloseCommand { get; }

    private sealed record Row(string Change, string Position, string Sound, string Detail, string Kind) : IChartDiffRow
    {
        public bool IsAdded => Kind == "added";

        public bool IsRemoved => Kind == "removed";

        public bool IsChanged => Kind == "changed";
    }
}
