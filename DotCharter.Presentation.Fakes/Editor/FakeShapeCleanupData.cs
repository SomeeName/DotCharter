using System.Windows.Input;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using DotCharter.Core.Domain.Guitar;
using DotCharter.Presentation.Contracts.Editor;

namespace DotCharter.Presentation.Fakes.Editor;

/// <summary>A fake duplicate group; its tick really toggles.</summary>
internal sealed class FakeShapeCleanupMerge(FakeShape shape, string title, string detail) : ObservableObject, IShapeCleanupMerge
{
    private bool _isIncluded = true;

    public string Title { get; } = title;

    public string Detail { get; } = detail;

    public ChordTemplate Diagram { get; } = shape.ToTemplate();

    public Chord DiagramChord { get; } = shape.ToChord();

    public int StringCount { get; } = shape.Frets.Length;

    public bool IsIncluded
    {
        get => _isIncluded;
        set => SetProperty(ref _isIncluded, value);
    }
}

/// <summary>A fake unused shape; its tick really toggles.</summary>
internal sealed class FakeShapeCleanupRemoval(FakeShape shape) : ObservableObject, IShapeCleanupRemoval
{
    private bool _isIncluded = true;

    public string Name { get; } = FakeShapeGrips.Title(shape);

    public ChordTemplate Diagram { get; } = shape.ToTemplate();

    public Chord DiagramChord { get; } = shape.ToChord();

    public int StringCount { get; } = shape.Frets.Length;

    public bool IsIncluded
    {
        get => _isIncluded;
        set => SetProperty(ref _isIncluded, value);
    }
}

/// <summary>A fake near-duplicate pair; its three answers really select one at a time.</summary>
public sealed class FakeShapeNearDuplicate : ObservableObject, IShapeNearDuplicate
{
    internal FakeShapeNearDuplicate(FakeShape first, int firstUses, FakeShape second, int secondUses, string difference, string[] choices)
    {
        DifferenceText = difference;
        FirstLabel = $"{FakeShapeGrips.Title(first)} · {firstUses} uses";
        FirstDiagram = first.ToTemplate();
        FirstChord = first.ToChord();
        SecondLabel = $"{FakeShapeGrips.Title(second)} · {secondUses} uses";
        SecondDiagram = second.ToTemplate();
        SecondChord = second.ToChord();
        StringCount = first.Frets.Length;
        var options = choices.Select(label => new FakeShapeCleanupChoice(label)).ToList();
        foreach (var option in options) option.Pick = () => Choose(options, option);
        options[0].IsSelected = true;
        Choices = options;
    }

    /// <summary>Raised when an answer changes, so the dialog recounts Apply.</summary>
    internal event Action? Decided;

    public string DifferenceText { get; }

    public string FirstLabel { get; }

    public ChordTemplate FirstDiagram { get; }

    public Chord FirstChord { get; }

    public string SecondLabel { get; }

    public ChordTemplate SecondDiagram { get; }

    public Chord SecondChord { get; }

    public int StringCount { get; }

    public IReadOnlyList<IShapeCleanupChoice> Choices { get; }

    private void Choose(List<FakeShapeCleanupChoice> options, FakeShapeCleanupChoice picked)
    {
        foreach (var option in options) option.IsSelected = ReferenceEquals(option, picked);
        Decided?.Invoke();
    }
}

/// <summary>One answer to a pair.</summary>
public sealed class FakeShapeCleanupChoice : ObservableObject, IShapeCleanupChoice
{
    private bool _isSelected;

    internal FakeShapeCleanupChoice(string label)
    {
        Label = label;
        SelectCommand = new RelayCommand(() => Pick());
    }

    internal Action Pick { get; set; } = () => { };

    public string Label { get; }

    public bool IsSelected
    {
        get => _isSelected;
        internal set => SetProperty(ref _isSelected, value);
    }

    public ICommand SelectCommand { get; }
}

/// <summary>The fixtures: what an old Rocksmith XML import leaves behind.</summary>
internal static class FakeShapeCleanupData
{
    private static readonly string[] Answers = ["Keep both", "Use the left one", "Use the right one"];

    public static List<FakeShapeCleanupMerge> Duplicates() =>
    [
        new(FakeShapeGrips.LeadGrip, "x x 9 8 11 x ×3", "Three unnamed copies become one; 35 chords and 13 hand shapes move"),
        new(FakeShapeGrips.A5, "A5 ×2", "Keeps “A5”; 6 chords and 2 hand shapes move onto it from “A5 (2)”"),
        new(FakeShapeGrips.E5, "E5 ×3", "Keeps “E5”; 11 chords and 3 hand shapes move onto it from two unnamed copies"),
        new(FakeShapeGrips.PowerNoName, "x 7 9 9 x x ×2", "Two unnamed copies become one; 9 chords move")
    ];

    public static List<FakeShapeCleanupRemoval> Unused() =>
    [
        new(FakeShapeGrips.DyadNoName), new(FakeShapeGrips.Bm7b5), new(FakeShapeGrips.G with { Name = "G (Copy)" }),
        new(FakeShapeGrips.Cadd9 with { Name = "" }), new(FakeShapeGrips.LeadGrip with { Frets = [-1, -1, 7, 6, 9, -1] }),
        new(FakeShapeGrips.E5 with { Name = "" }), new(FakeShapeGrips.PowerNoName with { Frets = [-1, 5, 7, 7, -1, -1] }),
        new(FakeShapeGrips.LeadGrip with { Frets = [-1, -1, 12, 11, 14, -1] })
    ];

    public static List<FakeShapeNearDuplicate> NearDuplicates() =>
    [
        new(FakeShapeGrips.LeadGrip, 35, FakeShapeGrips.LeadGripOtherFingers, 2, "Same frets, another finger on the D string", Answers),
        new(FakeShapeGrips.Am, 22, FakeShapeGrips.AmOtherFingers, 3, "Same frets, other fingers on the D and G strings", Answers),
        new(FakeShapeGrips.C, 12, FakeShapeGrips.C with { Name = "Cmaj" }, 4, "Same grip, another name: “C” and “Cmaj”", Answers)
    ];

    public static List<FakeShapeCleanupMerge> LongDuplicates() =>
    [
        new(FakeShapeGrips.F with { Name = "Gesperrter Septakkord mit Barré im ersten Bund" }, "Gesperrter Septakkord mit Barré im ersten Bund ×4",
            "Behält „Gesperrter Septakkord mit Barré im ersten Bund“; 142 Akkorde und 38 Handformen werden umgehängt"),
        new(FakeShapeGrips.A5 with { Name = "ドロップDのパワーコード" }, "ドロップDのパワーコード ×2", "「ドロップDのパワーコード」を残し、317個のコードと64個のハンドシェイプを移動します"),
        .. Duplicates()
    ];

    public static List<FakeShapeNearDuplicate> LongNearDuplicates() =>
    [
        new(FakeShapeGrips.F with { Name = "Gesperrter Septakkord (Import)" }, 142, FakeShapeGrips.F with { Name = "F-Barré, Daumen über den Hals" }, 17,
            "Gleiche Bünde, anderer Name und andere Finger auf der A- und D-Saite",
            ["Beide behalten", "Die linke verwenden", "Die rechte verwenden"]),
        .. NearDuplicates()
    ];

    public static List<FakeShapeCleanupMerge> ManyDuplicates(int count) =>
        Enumerable.Range(0, count).Select(i =>
        {
            var grip = Pick(i) with { Frets = Pick(i).Frets.Select(f => f < 0 ? f : f + i / 6 * 2).ToArray() };
            var title = i < 6 ? FakeShapeGrips.Title(grip) : $"{FakeShapeGrips.Title(grip)} (pos {i / 6 + 1})";
            return new FakeShapeCleanupMerge(grip, $"{title} ×{2 + i % 3}",
                $"Keeps “{title}”; {4 + i * 3} chords and {1 + i % 5} hand shapes move onto it");
        }).ToList();

    public static List<FakeShapeCleanupRemoval> ManyUnused(int count) =>
        Enumerable.Range(0, count).Select(i => new FakeShapeCleanupRemoval(Pick(i) with
        {
            Name = i % 3 == 0 ? "" : $"{FakeShapeGrips.Title(Pick(i))} ({i + 2})",
            Frets = Pick(i).Frets.Select(f => f < 0 ? f : f + i % 5).ToArray()
        })).ToList();

    public static List<FakeShapeNearDuplicate> ManyNear(int count) =>
        Enumerable.Range(0, count).Select(i => new FakeShapeNearDuplicate(Pick(i), 20 - i, Pick(i) with { Name = $"{FakeShapeGrips.Title(Pick(i))} (alt)" },
            1 + i, i % 2 == 0 ? "Same grip, another name" : "Same frets, other fingers on one string", Answers)).ToList();

    private static FakeShape Pick(int i) =>
        (i % 6) switch
        {
            0 => FakeShapeGrips.Am,
            1 => FakeShapeGrips.C,
            2 => FakeShapeGrips.G,
            3 => FakeShapeGrips.D,
            4 => FakeShapeGrips.E5,
            _ => FakeShapeGrips.F
        };
}
