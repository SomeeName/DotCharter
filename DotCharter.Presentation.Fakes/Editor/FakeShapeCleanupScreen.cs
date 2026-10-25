using System.Windows.Input;
using DotCharter.Presentation.Contracts.Editor;
using DotCharter.Presentation.Fakes.Dialogs;

namespace DotCharter.Presentation.Fakes.Editor;

/// <summary>
///     A services-free <see cref="IShapeCleanupScreen" /> over an old imported chart. Ticking a
///     proposal or answering a near-duplicate really recounts Apply's caption. Apply answers
///     true, Cancel false.
/// </summary>
public sealed class FakeShapeCleanupScreen : FakeDialog<bool>, IShapeCleanupScreen
{
    private FakeShapeCleanupScreen(
        IReadOnlyList<FakeShapeCleanupMerge> duplicates,
        IReadOnlyList<FakeShapeCleanupRemoval> unused,
        IReadOnlyList<FakeShapeNearDuplicate> near,
        bool german = false)
    {
        Duplicates = duplicates;
        Unused = unused;
        NearDuplicates = near;
        German = german;
        foreach (var item in duplicates) item.PropertyChanged += (_, _) => OnPropertyChanged(nameof(ApplyLabel));
        foreach (var item in unused) item.PropertyChanged += (_, _) => OnPropertyChanged(nameof(ApplyLabel));
        foreach (var item in near) item.Decided += () => OnPropertyChanged(nameof(ApplyLabel));
        ApplyCommand = Answer(true);
        CancelCommand = Answer(false);
    }

    public static IReadOnlyList<FakeVariant<IShapeCleanupScreen>> Variants { get; } =
    [
        new(FakeVariantNames.Default, Default),
        new(FakeVariantNames.Empty, Empty),
        new(FakeVariantNames.LongText, LongText),
        new(FakeVariantNames.Full, Full),
        new("Decided", Decided)
    ];

    /// <summary>The old chart: three duplicate groups, four unused shapes, two pairs to review.</summary>
    public static FakeShapeCleanupScreen Default() =>
        new(FakeShapeCleanupData.Duplicates(), FakeShapeCleanupData.Unused(), FakeShapeCleanupData.NearDuplicates());

    /// <summary>Nothing to clean up.</summary>
    public static FakeShapeCleanupScreen Empty() => new([], [], []);

    public static FakeShapeCleanupScreen LongText() =>
        new(FakeShapeCleanupData.LongDuplicates(), FakeShapeCleanupData.Unused(), FakeShapeCleanupData.LongNearDuplicates(), german: true);

    /// <summary>A big import: twelve duplicate groups, thirty unused shapes, eight pairs to review.</summary>
    public static FakeShapeCleanupScreen Full() =>
        new(FakeShapeCleanupData.ManyDuplicates(12), FakeShapeCleanupData.ManyUnused(30), FakeShapeCleanupData.ManyNear(8));

    /// <summary>Both pairs answered and one unused shape unticked.</summary>
    public static FakeShapeCleanupScreen Decided()
    {
        var screen = Default();
        ((FakeShapeCleanupRemoval)screen.Unused[0]).IsIncluded = false;
        foreach (var pair in screen.NearDuplicates) pair.Choices[1].SelectCommand.Execute(null);
        return screen;
    }

    public string Summary => IsTidy
        ? German ? "Alles aufgeräumt: keine Duplikate, nichts ungenutzt." : "Nothing to clean up: no duplicates, nothing unused."
        : German
            ? $"{Duplicates.Count} Duplikatgruppen · {Unused.Count} ungenutzt · {NearDuplicates.Count} zu prüfen"
            : $"{Duplicates.Count} duplicate groups · {Unused.Count} unused · {NearDuplicates.Count} to review";

    public bool IsTidy => Duplicates.Count + Unused.Count + NearDuplicates.Count == 0;

    public string DuplicatesHeader => German ? $"Duplikate ({Duplicates.Count})" : $"Duplicates ({Duplicates.Count})";

    public IReadOnlyList<IShapeCleanupMerge> Duplicates { get; }

    public bool HasDuplicates => Duplicates.Count > 0;

    public string UnusedHeader => German ? $"Ungenutzt ({Unused.Count})" : $"Unused ({Unused.Count})";

    public IReadOnlyList<IShapeCleanupRemoval> Unused { get; }

    public bool HasUnused => Unused.Count > 0;

    public string NearDuplicatesHeader => German ? $"Prüfen ({NearDuplicates.Count})" : $"Review ({NearDuplicates.Count})";

    public IReadOnlyList<IShapeNearDuplicate> NearDuplicates { get; }

    public bool HasNearDuplicates => NearDuplicates.Count > 0;

    public string ApplyLabel
    {
        get
        {
            var changes = Duplicates.Count(d => d.IsIncluded) + Unused.Count(u => u.IsIncluded)
                          + NearDuplicates.Count(n => !n.Choices[0].IsSelected);
            return German ? $"{changes} Änderungen anwenden" : changes == 1 ? "Apply 1 change" : $"Apply {changes} changes";
        }
    }

    public ICommand ApplyCommand { get; }

    public ICommand CancelCommand { get; }

    private bool German { get; }
}
