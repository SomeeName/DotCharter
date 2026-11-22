using System.Collections.ObjectModel;
using System.Windows.Input;
using CommunityToolkit.Mvvm.ComponentModel;

namespace DotCharter.UI.ViewModels.Editor.Inspector;

/// <summary>A choice of design D's Difficulty filter: every level (<see cref="Index" /> -1) or one level's index.</summary>
public sealed record IssueLevelChoice(int Index, string Label)
{
    public override string ToString() => Label;
}

/// <summary>
///     The Issues tab's finding rows (inspector design D) as ONE flat list: the severity groups in order
///     (Important above Warning), filtered by the Difficulty choice. A row carries what it shows itself, with no
///     <c>$parent</c> lookup in the markup (the jump, the Fix, the location against the beat map, the row on the Fixes card), and
///     its Fix asks the fix menu's own rule check (<see cref="IssueFixViewModel.CanOffer" />), once per row and pass.
/// </summary>
public sealed partial class InspectorIssueRows : ObservableObject
{
    private readonly ILocalizationService _localization;
    private readonly ICommand _jump;
    private readonly ICommand _fix;
    private IReadOnlyList<InspectorIssueGroup> _groups = [];
    private Arrangement? _arrangement;
    private IList<BeatMarker>? _beats;
    private ArrangementIssue? _shown;
    private Dictionary<object, int>? _levels;
    private int _levelCount = -1;
    private bool _refilling;

    [ObservableProperty] private IssueLevelChoice? _selectedLevel;

    public InspectorIssueRows(ILocalizationService localization, ICommand jump, ICommand fix)
    {
        _localization = localization;
        _jump = jump;
        _fix = fix;
    }

    /// <summary>The rows the list shows.</summary>
    public BulkObservableCollection<IssueListRow> Items { get; } = [];

    /// <summary>All levels, then each level of the arrangement (hardest last); empty for a one-level arrangement.</summary>
    public ObservableCollection<IssueLevelChoice> LevelChoices { get; } = [];

    /// <summary>The Difficulty choice shows only for an arrangement with more than one level.</summary>
    public bool HasLevelChoices => LevelChoices.Count > 1;

    /// <summary>Rebuilds the rows from <paramref name="groups" /> for <paramref name="arrangement" />.</summary>
    public void Rebuild(IReadOnlyList<InspectorIssueGroup> groups, Arrangement? arrangement, IList<BeatMarker>? beats)
    {
        _groups = groups;
        _arrangement = arrangement;
        _beats = beats;
        _levels = null;
        SyncLevelChoices(arrangement?.Levels.Count ?? 0);
        Fill();
    }

    /// <summary>The finding the Fixes card shows: its row lights its spine.</summary>
    public void MarkShown(ArrangementIssue? issue)
    {
        _shown = issue;
        foreach (var row in Items) row.IsOnFixesCard = ReferenceEquals(row.Issue, issue);
    }

    /// <summary>The 0-based index of the level holding the finding's element, or -1 for an arrangement-wide finding.</summary>
    internal int LevelOf(ArrangementIssue issue)
    {
        if (_arrangement == null) return -1;
        _levels ??= MapLevels(_arrangement);
        var element = (object?)issue.Note ?? (object?)issue.Chord ?? (object?)issue.HandShape ?? issue.Anchor;
        return element != null && _levels.TryGetValue(element, out var index) ? index : -1;
    }

    internal bool CanOffer(ArrangementIssue issue)
    {
        var index = LevelOf(issue);
        var level = index >= 0 ? _arrangement!.Levels[index] : null;
        return IssueFixViewModel.CanOffer(issue, _arrangement, level, _beats);
    }

    internal string MessageOf(ArrangementIssue issue) => ArrangementIssueText.Of(issue, _localization);

    internal string StampOf(ArrangementIssue issue) =>
        ChartPositionLabel.Format(_beats as IReadOnlyList<BeatMarker> ?? _beats?.ToList(), issue.TimeMs);

    partial void OnSelectedLevelChanged(IssueLevelChoice? value)
    {
        if (!_refilling) Fill();
    }

    private void Fill()
    {
        var wanted = SelectedLevel?.Index ?? -1;
        var many = _levelCount > 1;
        var rows = new List<IssueListRow>();
        foreach (var group in _groups)
        {
            foreach (var issue in group.Issues)
            {
                var level = many ? LevelOf(issue) : -1;
                if (wanted >= 0 && level >= 0 && level != wanted) continue;
                var label = level >= 0 ? _localization.Format("Inspector_Issues_RowLevel", level + 1) : "";
                rows.Add(new IssueListRow(this, issue, label, _jump, _fix) { IsOnFixesCard = ReferenceEquals(issue, _shown) });
            }
        }

        Items.ResetWith(rows);
    }

    private void SyncLevelChoices(int count)
    {
        if (count == _levelCount) return;
        _levelCount = count;
        LevelChoices.Clear();
        if (count > 1)
        {
            LevelChoices.Add(new IssueLevelChoice(-1, _localization["Inspector_Issues_AllLevels"]));
            for (var i = 0; i < count; i++) LevelChoices.Add(new IssueLevelChoice(i, _localization.Format("Inspector_Issues_RowLevel", i + 1)));
        }

        _refilling = true;
        try { SelectedLevel = LevelChoices.FirstOrDefault(); }
        finally { _refilling = false; }

        OnPropertyChanged(nameof(HasLevelChoices));
    }

    private static Dictionary<object, int> MapLevels(Arrangement arrangement)
    {
        var map = new Dictionary<object, int>(ReferenceEqualityComparer.Instance);
        for (var i = 0; i < arrangement.Levels.Count; i++)
        {
            var level = arrangement.Levels[i];
            foreach (var note in level.Notes) map.TryAdd(note, i);
            foreach (var chord in level.Chords) map.TryAdd(chord, i);
            foreach (var shape in level.HandShapes) map.TryAdd(shape, i);
            foreach (var anchor in level.Anchors) map.TryAdd(anchor, i);
        }

        return map;
    }
}

/// <summary>One finding row of the Issues tab: the jump, its Fix when the fix menu has one, the location and the level.</summary>
public sealed partial class IssueListRow : ObservableObject
{
    private readonly InspectorIssueRows _owner;
    private string? _stamp;
    private bool? _hasFix;
    private string? _message;

    /// <summary>This row's finding is the one on the Fixes card (its spine lights).</summary>
    [ObservableProperty] private bool _isOnFixesCard;

    internal IssueListRow(InspectorIssueRows owner, ArrangementIssue issue, string level, ICommand jump, ICommand fix)
    {
        _owner = owner;
        Issue = issue;
        Level = level;
        Jump = jump;
        Fix = fix;
    }

    public ArrangementIssue Issue { get; }

    /// <summary>"Level 3", or "" for a one-level arrangement or a finding no level holds.</summary>
    public string Level { get; }

    public bool HasLevel => Level.Length > 0;

    /// <summary>The location readout against the beat map (<c>M19.3 · 02:13.16</c>), computed when the row is realized.</summary>
    public string Stamp => _stamp ??= _owner.StampOf(Issue);

    /// <summary>The fix menu would offer a fix for this finding: the row draws its Fix.</summary>
    public bool HasFix => _hasFix ??= _owner.CanOffer(Issue);

    public ICommand Jump { get; }

    public ICommand Fix { get; }

    /// <summary>The finding's sentence in the UI language (a language switch rebuilds the rows).</summary>
    public string Message => _message ??= _owner.MessageOf(Issue);

    public override string ToString() => Message;
}
