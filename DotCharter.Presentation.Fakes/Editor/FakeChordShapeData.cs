using System.Globalization;
using System.Windows.Input;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using DotCharter.Core.Domain.Guitar;
using DotCharter.Presentation.Contracts.Editor;

namespace DotCharter.Presentation.Fakes.Editor;

/// <summary>One fake row: a real template drawn in the mini chord box, and verbs that only toggle the rename box.</summary>
public sealed class FakeChordShapeRow : ObservableObject, IChordShapeRow
{
    private bool _isChoosingMergeTarget;
    private bool _isRenaming;
    private string _nameDraft = "";

    internal FakeChordShapeRow(FakeShape shape, int chords, int handShapes, int duplicates, string fingerState)
    {
        FretsText = string.Join(' ', shape.Frets.Select(f => f < 0 ? "x" : f.ToString(CultureInfo.InvariantCulture)));
        IsUnnamed = shape.Name.Length == 0;
        Name = IsUnnamed ? FretsText : shape.Name;
        Diagram = shape.ToTemplate();
        DiagramChord = shape.ToChord();
        IsArpeggio = shape.Arpeggio;
        FingerStateText = fingerState;
        IsFingeringMissing = fingerState == FakeChordShapeData.Missing;
        IsFingeringSuggested = fingerState == FakeChordShapeData.Suggested;
        IsUnused = chords + handShapes == 0;
        LevelUsesText = IsUnused ? "Unused" : $"This level: {chords} chords · {handShapes} hand shapes";
        AllUsesText = chords % 2 == 0 ? "" : $"All levels: {chords * 3} chords · {handShapes * 3} hand shapes";
        IsDuplicate = duplicates > 0;
        DuplicateText = duplicates switch { 0 => "", 1 => "Same frets as 1 other", _ => $"Same frets as {duplicates} others" };
        MergeTargets = Enumerable.Range(0, duplicates)
            .Select(i => (IChordShapeMergeTarget)new FakeMergeTarget($"{Name} · fingered · {12 - i} uses"))
            .ToList();
        BeginRenameCommand = new RelayCommand(() =>
        {
            NameDraft = Name;
            IsRenaming = true;
        });
        CommitRenameCommand = new RelayCommand(() => IsRenaming = false);
        CancelRenameCommand = new RelayCommand(() => IsRenaming = false);
        ChooseMergeTargetCommand = new RelayCommand(() => IsChoosingMergeTarget = !IsChoosingMergeTarget);
        CanSplitSelection = chords > 4;
    }

    public string Name { get; }
    public bool IsUnnamed { get; }
    public string FretsText { get; }
    public ChordTemplate Diagram { get; }
    public Chord DiagramChord { get; }
    public int StringCount => 6;
    public bool IsArpeggio { get; }
    public string FingerStateText { get; }
    public bool IsFingeringMissing { get; }
    public bool IsFingeringSuggested { get; }
    public string LevelUsesText { get; }
    public string AllUsesText { get; }
    public bool IsUnused { get; }
    public string DuplicateText { get; }
    public bool IsDuplicate { get; }
    public ICommand PlaceCommand { get; } = new RelayCommand(() => { });
    public ICommand SelectUsesCommand { get; } = new RelayCommand(() => { });
    public ICommand NextUseCommand { get; } = new RelayCommand(() => { });

    public bool IsRenaming
    {
        get => _isRenaming;
        private set => SetProperty(ref _isRenaming, value);
    }

    public string NameDraft
    {
        get => _nameDraft;
        set => SetProperty(ref _nameDraft, value);
    }

    public ICommand BeginRenameCommand { get; }
    public ICommand CommitRenameCommand { get; }
    public ICommand CancelRenameCommand { get; }
    public IReadOnlyList<IChordShapeMergeTarget> MergeTargets { get; }

    public bool IsChoosingMergeTarget
    {
        get => _isChoosingMergeTarget;
        private set => SetProperty(ref _isChoosingMergeTarget, value);
    }

    public ICommand ChooseMergeTargetCommand { get; }
    public ICommand SplitSelectionCommand { get; } = new RelayCommand(() => { });
    public bool CanSplitSelection { get; }
}

public sealed class FakeMergeTarget(string label) : ObservableObject, IChordShapeMergeTarget
{
    public string Label { get; } = label;

    public ICommand MergeCommand { get; } = new RelayCommand(() => { });
}

/// <summary>A grip low string to high (<c>-1</c> unused) with its fingers (<c>-1</c> none).</summary>
internal sealed record FakeShape(string Name, int[] Frets, int[] Fingers, bool Arpeggio = false)
{
    public ChordTemplate ToTemplate()
    {
        var template = new ChordTemplate { Name = Name, Arpeggio = Arpeggio };
        for (var s = 0; s < Frets.Length; s++)
        {
            if (Frets[s] >= 0) template.Frets[s] = Frets[s];
            if (Fingers[s] >= 0) template.Fingers[s] = Fingers[s];
        }

        return template;
    }

    public Chord ToChord()
    {
        var chord = new Chord();
        for (var s = 0; s < Frets.Length; s++)
        {
            if (Frets[s] >= 0) chord.ChordNotes.Add(new ChordNote { StringIndex = s, Fret = Frets[s] });
        }

        return chord;
    }
}

internal static class FakeChordShapeData
{
    public const string Fingered = "Fingered";
    public const string Suggested = "Suggested fingering";
    public const string Missing = "Missing fingering";

    private static readonly FakeShape[] Grips =
    [
        new("C", [-1, 3, 2, 0, 1, 0], [-1, 3, 2, -1, 1, -1]),
        new("G", [3, 2, 0, 0, 0, 3], [2, 1, -1, -1, -1, 3]),
        new("Am", [-1, 0, 2, 2, 1, 0], [-1, -1, 2, 3, 1, -1]),
        new("F", [1, 3, 3, 2, 1, 1], [1, 3, 4, 2, 1, 1]),
        new("D", [-1, -1, 0, 2, 3, 2], [-1, -1, -1, 1, 3, 2]),
        new("Em", [0, 2, 2, 0, 0, 0], [-1, 2, 3, -1, -1, -1]),
        new("E5", [0, 2, 2, -1, -1, -1], [-1, 1, 2, -1, -1, -1]),
        new("A5", [-1, 0, 2, 2, -1, -1], [-1, -1, -1, -1, -1, -1]),
        new("Bm7b5", [-1, 2, 3, 2, 3, -1], [-1, 1, 3, 2, 4, -1]),
        new("Dsus4", [-1, -1, 0, 2, 3, 3], [-1, -1, -1, 1, 2, 3]),
        new("F#m", [2, 4, 4, 2, 2, 2], [1, 3, 4, 1, 1, 1], Arpeggio: true),
        new("", [-1, 7, 9, 9, -1, -1], [-1, -1, -1, -1, -1, -1]),
        new("", [-1, -1, 8, 6, -1, -1], [-1, -1, 3, 1, -1, -1]),
        new("", [-1, -1, 8, 5, -1, -1], [-1, -1, 4, 1, -1, -1])
    ];

    public static IReadOnlyList<FakeChordShapeRow> Song()
    {
        var rows = new List<FakeChordShapeRow>();
        for (var i = 0; i < Grips.Length; i++)
            rows.Add(Row(Grips[i], i));
        rows.Insert(3, new FakeChordShapeRow(Grips[2] with { Fingers = [-1, -1, 3, 2, 1, -1] }, 3, 1, 1, Fingered));
        rows.Insert(1, new FakeChordShapeRow(Grips[0] with { Name = "Cadd9" }, 0, 0, 1, Suggested));
        return rows;
    }

    public static IReadOnlyList<FakeChordShapeRow> LongNames() =>
    [
        new(new FakeShape("Cmaj7#11 (Vorschlag aus dem Import, bitte prüfen)", [-1, 3, 2, 0, 0, 2], [-1, 3, 2, -1, -1, 4]),
            42, 11, 2, Suggested),
        new(new FakeShape("ドロップDのパワーコード(ブリッジミュート)", [0, 0, 0, -1, -1, -1], [-1, -1, -1, -1, -1, -1]),
            317, 64, 0, Fingered),
        new(new FakeShape("Gesperrter Septakkord mit Barré im neunten Bund", [9, 11, 9, 10, 9, 9], [1, 3, 1, 2, 1, 1]),
            8, 2, 1, Missing),
        .. Song()
    ];

    public static IReadOnlyList<FakeChordShapeRow> Many(int count)
    {
        var rows = new List<FakeChordShapeRow>(count);
        for (var i = 0; i < count; i++)
        {
            var grip = Grips[i % Grips.Length];
            var shift = i / Grips.Length;
            var shifted = grip with
            {
                Name = grip.Name.Length == 0 ? "" : $"{grip.Name} (pos {shift + 1})",
                Frets = grip.Frets.Select(f => f < 0 ? f : f + shift).ToArray()
            };
            rows.Add(Row(shifted, i));
        }

        return rows;
    }

    private static FakeChordShapeRow Row(FakeShape shape, int i)
    {
        var fingered = shape.Frets.Select((f, s) => f <= 0 || shape.Fingers[s] >= 0).All(x => x);
        var state = !fingered ? Missing : i % 3 == 0 ? Suggested : Fingered;
        var duplicates = i % 5 == 2 ? 1 : i % 11 == 4 ? 2 : 0;
        var chords = i % 7 == 6 ? 0 : 3 + i * 7 % 40;
        var handShapes = chords == 0 ? 0 : i % 4;
        return new FakeChordShapeRow(shape, chords, handShapes, duplicates, state);
    }
}
