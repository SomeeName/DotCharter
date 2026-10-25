using System.Windows.Input;
using CommunityToolkit.Mvvm.Input;
using DotCharter.Core.Domain.Project;
using DotCharter.Localization;
using DotCharter.Presentation.Contracts.Editor;
using DotCharter.Presentation.Dialogs;
using DotCharter.Presentation.Screens.Dialogs;
using DotCharter.Services.Editing.ChordShapes;
using DotCharter.Services.State;

namespace DotCharter.Presentation.Screens.Editor;

/// <summary>
///     Clean up shapes over the active arrangement. It reads what <see cref="ShapeCleanupFinder" /> finds when it
///     opens; every tick and every answer recounts Apply, and Apply hands the ticked proposals to
///     <see cref="ShapeCleanupPlanner" />, which builds the ONE undo step. Nothing is written before Apply.
/// </summary>
public sealed class ShapeCleanupViewModel : DialogViewModel<bool>, IShapeCleanupScreen, ICancelableDialog
{
    private readonly ChartEditDialogServices _edit;
    private readonly ILocalizationService _text;
    private readonly Arrangement? _arrangement;
    private readonly List<ShapeCleanupMergeItem> _merges;
    private readonly List<ShapeCleanupRemovalItem> _unused;
    private readonly List<ShapeCleanupNearItem> _near;
    private readonly RelayCommand _apply;

    public ShapeCleanupViewModel(ChartEditDialogServices edit)
    {
        _edit = edit;
        _text = edit.Localization;
        _arrangement = edit.Editor.ActiveArrangement;
        var found = _arrangement is null ? ShapeCleanupFindings.None : ShapeCleanupFinder.Find(_arrangement);
        var strings = Math.Max(1, _arrangement?.StringCount ?? 6);
        _merges = [.. found.Merges.Select(m => new ShapeCleanupMergeItem(m, MergeTitle(m, strings), MergeDetail(m), strings))];
        _unused = [.. found.Unused.Select(t => new ShapeCleanupRemovalItem(t, ChordShapeNames.DisplayName(t, strings), strings))];
        _near = [.. found.NearDuplicates.Select(p => NearItem(p, strings))];
        _apply = new RelayCommand(Apply, () => ChangeCount > 0);
        foreach (var item in _merges) item.PropertyChanged += (_, _) => Recount();
        foreach (var item in _unused) item.PropertyChanged += (_, _) => Recount();
        foreach (var item in _near) item.Decided += Recount;
        CancelCommand = new RelayCommand(() => TryCancel());
    }

    public string Summary => IsTidy ? _text["ShapeCleanup_Tidy"] : string.Join(" · ", SummaryParts());

    public bool IsTidy => _merges.Count + _unused.Count + _near.Count == 0;

    public string DuplicatesHeader => _text.Format("ShapeCleanup_DuplicatesHeader", _merges.Count);

    public IReadOnlyList<IShapeCleanupMerge> Duplicates => _merges;

    public bool HasDuplicates => _merges.Count > 0;

    public string UnusedHeader => _text.Format("ShapeCleanup_UnusedHeader", _unused.Count);

    public IReadOnlyList<IShapeCleanupRemoval> Unused => _unused;

    public bool HasUnused => _unused.Count > 0;

    public string NearDuplicatesHeader => _text.Format("ShapeCleanup_ReviewHeader", _near.Count);

    public IReadOnlyList<IShapeNearDuplicate> NearDuplicates => _near;

    public bool HasNearDuplicates => _near.Count > 0;

    public string ApplyLabel => _text.Plural("ShapeCleanup_ApplyOne", "ShapeCleanup_Apply", ChangeCount);

    public ICommand ApplyCommand => _apply;

    public ICommand CancelCommand { get; }

    /// <summary>Ticked groups, ticked unused shapes and pairs answered with a side.</summary>
    internal int ChangeCount => _merges.Count(m => m.IsIncluded) + _unused.Count(u => u.IsIncluded)
                                + _near.Count(n => n.Choice != ShapeCleanupChoice.KeepBoth);

    public bool TryCancel()
    {
        Close(false);
        return true;
    }

    private void Apply()
    {
        if (_arrangement is null) return;
        var plan = ShapeCleanupPlanner.Plan(
            _arrangement,
            _merges.Where(m => m.IsIncluded).Select(m => m.Source),
            _unused.Where(u => u.IsIncluded).Select(u => u.Source),
            _near.Select(n => (n.Source, n.Choice)),
            _edit.Editor);
        if (plan.Action is { } action && _edit.History.Do(action))
        {
            _edit.State.SetMessage(
                _text.Plural("ShapeCleanup_Status_AppliedOne", "ShapeCleanup_Status_Applied", plan.ShapesRemoved), StatusSeverity.Success);
            Close(true);
            return;
        }

        Close(false);
    }

    private void Recount()
    {
        OnPropertyChanged(nameof(ApplyLabel));
        _apply.NotifyCanExecuteChanged();
    }

    private IEnumerable<string> SummaryParts()
    {
        if (_merges.Count > 0)
            yield return _text.Plural("ShapeCleanup_SummaryDuplicatesOne", "ShapeCleanup_SummaryDuplicates", _merges.Count);
        if (_unused.Count > 0) yield return _text.Format("ShapeCleanup_SummaryUnused", _unused.Count);
        if (_near.Count > 0) yield return _text.Format("ShapeCleanup_SummaryReview", _near.Count);
    }

    private string MergeTitle(ShapeCleanupMerge merge, int strings) =>
        _text.Format("ShapeCleanup_MergeTitle", ChordShapeNames.DisplayName(merge.Keeper, strings), merge.Count);

    private string MergeDetail(ShapeCleanupMerge merge)
    {
        var chords = _text.Plural("ChordShapes_ChordsOne", "ChordShapes_Chords", merge.Chords);
        var hands = _text.Plural("ChordShapes_HandShapesOne", "ChordShapes_HandShapes", merge.HandShapes);
        var moved = merge.HandShapes == 0 ? chords
            : merge.Chords == 0 ? hands
            : _text.Format("ChordShapes_UsesPair", chords, hands);
        return ChordShapeNames.IsUnnamed(merge.Keeper)
            ? _text.Format("ShapeCleanup_MergeUnnamed", merge.Count, moved)
            : _text.Format("ShapeCleanup_MergeNamed", merge.Keeper.Name, moved);
    }

    private ShapeCleanupNearItem NearItem(ShapeCleanupNearDuplicate pair, int strings) => new(
        pair, DifferenceText(pair),
        _text.Format("ShapeCleanup_NearLabel", ChordShapeNames.DisplayName(pair.First, strings), UseCount(pair.FirstUses)),
        _text.Format("ShapeCleanup_NearLabel", ChordShapeNames.DisplayName(pair.Second, strings), UseCount(pair.SecondUses)),
        [_text["ShapeCleanup_Choice_Both"], _text["ShapeCleanup_Choice_Left"], _text["ShapeCleanup_Choice_Right"]],
        strings);

    private string UseCount(int uses) => _text.Plural("ChordShapes_UseCountOne", "ChordShapes_UseCount", uses);

    private string DifferenceText(ShapeCleanupNearDuplicate pair) => pair.Difference switch
    {
        ShapeCleanupDifference.Fingers => _text.PluralWith("ShapeCleanup_NearFingersOne", "ShapeCleanup_NearFingers", pair.Strings.Count,
            string.Join(", ", pair.Strings.Select(s => ShapeEditViewModel.StringName(_arrangement!, s)))),
        ShapeCleanupDifference.Name => _text.Format("ShapeCleanup_NearName", pair.First.Name, pair.Second.Name),
        _ => _text["ShapeCleanup_NearFlags"]
    };
}
