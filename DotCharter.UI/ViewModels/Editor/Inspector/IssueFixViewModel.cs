using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using DotCharter.Services.Editing.Anchors;

namespace DotCharter.UI.ViewModels.Editor.Inspector;

/// <summary>
/// The fix menu for the finding the charter last jumped to on the Issues tab, exposed as
///     <c>InspectorIssuesViewModel.IssueFix</c>. Today it answers E04 and I46 — a grip whose index finger
///     disagrees with the hand position — with <see cref="IndexAnchorRepair" />: the DEFAULT first
///     (re-finger the grip the way the arrangement already plays it under this anchor, when it does;
///     otherwise move the anchor), and the other one beside it. Each is one undo step.
///     <para>
///         It holds a finding, not a selection: a recompute keeps the offer while the same finding
///         (same rule, same element) still stands and withdraws it once it is gone, so a button never
///         applies a repair to a disagreement that is gone.
///     </para>
/// </summary>
public sealed partial class IssueFixViewModel : ObservableObject
{
    private readonly IHistoryService _history;
    private readonly ILocalizationService _localization;
    private ArrangementIssue? _issue;
    private ChartLevel? _level;
    private IndexAnchorRepair? _repair;

    public IssueFixViewModel(IHistoryService history, ILocalizationService localization)
    {
        _history = history;
        _localization = localization;
    }

    /// <summary>The repair on offer, or null.</summary>
    public IndexAnchorRepair? Repair => _repair;

    public bool HasFix => _repair != null;

    /// <summary>True when both fixes exist; the alternative is always the anchor move.</summary>
    public bool HasAlternative => _repair is { PrefersRefinger: true, MoveAnchorTo: not null };

    /// <summary>"Fix E04:".</summary>
    public string Title => _issue == null ? "" : _localization.Format("Inspector_IssueFix_Title", _issue.Code);

    public string DefaultLabel => _repair == null ? "" : _repair.PrefersRefinger ? RefingerLabel(_repair) : MoveLabel(_repair);

    public string DefaultTip => _repair == null ? "" : _repair.PrefersRefinger ? RefingerTip(_repair) : MoveTip(_repair);

    public string AlternativeLabel => HasAlternative ? MoveLabel(_repair!) : "";

    public string AlternativeTip => HasAlternative ? MoveTip(_repair!) : "";

    /// <summary>
    ///     Offers the fixes for <paramref name="issue" /> on <paramref name="level" />, or withdraws the
    ///     offer when the finding has none (any other rule, or nothing to repair).
    /// </summary>
    public void Offer(ArrangementIssue? issue, Arrangement? arrangement, ChartLevel? level)
    {
        _issue = null;
        _level = null;
        _repair = null;
        if (issue is { Type: ArrangementIssueType.IndexFingerNotOnAnchorFret or ArrangementIssueType.FingeringAnchorMismatch, Anchor: { } anchor }
            && arrangement != null && level != null)
        {
            _repair = IndexAnchorRepair.For(arrangement, level, issue.Chord, issue.HandShape, anchor);
            if (_repair != null)
            {
                _issue = issue;
                _level = level;
            }
        }

        OnPropertyChanged(string.Empty);
    }

    /// <summary>After a recompute: keep the offer while the same finding stands, else withdraw it.</summary>
    public void Refresh(IReadOnlyList<ArrangementIssue> issues, Arrangement? arrangement)
    {
        if (_issue is not { } shown) return;
        var same = issues.FirstOrDefault(i => i.Type == shown.Type && ReferenceEquals(i.Chord, shown.Chord)
                                              && ReferenceEquals(i.HandShape, shown.HandShape));
        Offer(same, arrangement, _level);
    }

    [RelayCommand]
    private void ApplyDefault()
    {
        if (_repair != null && _level != null) Apply(_repair.Default(_level));
    }

    [RelayCommand]
    private void ApplyAlternative()
    {
        if (_repair != null && _level != null) Apply(_repair.Alternative(_level));
    }

    private void Apply(IChartAction? action)
    {
        if (action == null) return;
        _history.Do(action);
        Offer(null, null, null);
    }

    /// <summary>"1-3": the fingers of the fretted strings, lowest string first.</summary>
    internal static string Fingering(ChordTemplate template) =>
        string.Join('-', template.Frets.Keys.Order()
            .Select(s => template.Fingers.TryGetValue(s, out var f) && f >= 0 ? f.ToString(System.Globalization.CultureInfo.InvariantCulture) : "x"));

    private string RefingerLabel(IndexAnchorRepair repair) => _localization.Plural(
        "Inspector_IssueFix_RefingerOne", "Inspector_IssueFix_Refinger", repair.Evidence, Fingering(repair.Refinger!));

    private string RefingerTip(IndexAnchorRepair repair) =>
        _localization.Format("Inspector_IssueFix_RefingerTip", repair.Anchor.Fret, Fingering(repair.Refinger!));

    private string MoveLabel(IndexAnchorRepair repair) =>
        _localization.Format("Inspector_IssueFix_MoveAnchor", repair.MoveAnchorTo ?? repair.Anchor.Fret);

    private string MoveTip(IndexAnchorRepair repair) =>
        _localization.Format("Inspector_IssueFix_MoveAnchorTip", repair.Anchor.Fret, repair.MoveAnchorTo ?? repair.Anchor.Fret);
}
