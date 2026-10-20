using System.Windows.Input;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using DotCharter.Presentation.Contracts.Editor;

namespace DotCharter.Presentation.Fakes.Editor;

/// <summary>
///     A services-free <see cref="IChordShapesScreen" />: a real-sized chart's shapes (with
///     duplicates, missing and suggested fingerings, arpeggios and unused templates), the empty
///     editor, long German and Japanese names, a 90-template arrangement, and each filter. Every
///     variant with rows shows the recent-shapes strip over its first few rows.
/// </summary>
public sealed class FakeChordShapesScreen : ObservableObject, IChordShapesScreen
{
    private readonly IReadOnlyList<FakeChordShapeRow> _all;
    private IReadOnlyList<IChordShapeRow> _rows;
    private bool _isShowingAll = true;
    private bool _isShowingDuplicates;
    private bool _isShowingMissingFingering;

    private FakeChordShapesScreen(IReadOnlyList<FakeChordShapeRow> rows, string emptyText)
    {
        _all = rows;
        _rows = rows;
        RecentRows = rows.Take(5).Reverse().ToList<IChordShapeRow>();
        EmptyText = emptyText;
        var duplicates = rows.Count(r => r.IsDuplicate);
        var missing = rows.Count(r => r.IsFingeringMissing);
        Summary = rows.Count == 0 ? "" : $"{rows.Count} shapes · {duplicates} duplicates · {missing} without fingering";
        AllFilterLabel = $"All {rows.Count}";
        DuplicatesFilterLabel = $"Duplicates {duplicates}";
        MissingFingeringFilterLabel = $"Missing fingering {missing}";
        ShowAllCommand = new RelayCommand(() => Show(all: true));
        ShowDuplicatesCommand = new RelayCommand(() => Show(duplicates: true));
        ShowMissingFingeringCommand = new RelayCommand(() => Show(missing: true));
    }

    public static IReadOnlyList<FakeVariant<IChordShapesScreen>> Variants { get; } =
    [
        new(FakeVariantNames.Default, Default),
        new(FakeVariantNames.Empty, Empty),
        new(FakeVariantNames.LongText, LongText),
        new(FakeVariantNames.Full, Full),
        new("Duplicates", DuplicatesOnly),
        new("MissingFingering", MissingOnly),
        new("Renaming", Renaming),
        new("ChoosingMergeTarget", ChoosingMergeTarget)
    ];

    /// <summary>A song's worth of shapes: a dozen grips, two of them duplicated, one arpeggio, one unused.</summary>
    public static FakeChordShapesScreen Default() =>
        new(FakeChordShapeData.Song(), "No chord shape matches this filter.");

    /// <summary>No chart open.</summary>
    public static FakeChordShapesScreen Empty() => new([], "Open a chart to see its chord shapes.");

    public static FakeChordShapesScreen LongText() =>
        new(FakeChordShapeData.LongNames(), "Keine Akkordform passt zu diesem Filter.");

    /// <summary>A long imported arrangement: ninety templates, many of them reduced duplicates.</summary>
    public static FakeChordShapesScreen Full() => new(FakeChordShapeData.Many(90), "No chord shape matches this filter.");

    public static FakeChordShapesScreen DuplicatesOnly()
    {
        var screen = Default();
        screen.Show(duplicates: true);
        return screen;
    }

    public static FakeChordShapesScreen MissingOnly()
    {
        var screen = Default();
        screen.Show(missing: true);
        return screen;
    }

    public static FakeChordShapesScreen Renaming()
    {
        var screen = Default();
        screen._all[0].BeginRenameCommand.Execute(null);
        return screen;
    }

    /// <summary>A duplicate's "Merge into…" open on its two targets.</summary>
    public static FakeChordShapesScreen ChoosingMergeTarget()
    {
        var screen = Default();
        screen._all.First(r => r.MergeTargets.Count > 0).ChooseMergeTargetCommand.Execute(null);
        return screen;
    }

    public string Summary { get; }

    public IReadOnlyList<IChordShapeRow> Rows
    {
        get => _rows;
        private set
        {
            if (!SetProperty(ref _rows, value)) return;
            OnPropertyChanged(nameof(HasRows));
        }
    }

    public bool HasRows => _rows.Count > 0;

    public string EmptyText { get; }

    public string AllFilterLabel { get; }

    public string DuplicatesFilterLabel { get; }

    public string MissingFingeringFilterLabel { get; }

    public bool IsShowingAll
    {
        get => _isShowingAll;
        private set => SetProperty(ref _isShowingAll, value);
    }

    public bool IsShowingDuplicates
    {
        get => _isShowingDuplicates;
        private set => SetProperty(ref _isShowingDuplicates, value);
    }

    public bool IsShowingMissingFingering
    {
        get => _isShowingMissingFingering;
        private set => SetProperty(ref _isShowingMissingFingering, value);
    }

    public ICommand ShowAllCommand { get; }

    public ICommand ShowDuplicatesCommand { get; }

    public ICommand ShowMissingFingeringCommand { get; }

    public IReadOnlyList<IChordShapeRow> RecentRows { get; }

    public bool HasRecentRows => RecentRows.Count > 0;

    public ICommand RepeatPreviousChordCommand { get; } = new RelayCommand(() => { });

    private void Show(bool all = false, bool duplicates = false, bool missing = false)
    {
        IsShowingAll = all;
        IsShowingDuplicates = duplicates;
        IsShowingMissingFingering = missing;
        Rows = _all.Where(r => all || (duplicates && r.IsDuplicate) || (missing && r.IsFingeringMissing)).ToList();
    }
}
