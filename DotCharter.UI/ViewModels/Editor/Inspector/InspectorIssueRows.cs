using System.Collections.ObjectModel;
using System.Windows.Input;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace DotCharter.UI.ViewModels.Editor.Inspector;

/// <summary>A choice of design D's Difficulty filter: every level (<see cref="Index" /> -1) or one level's index.</summary>
public sealed record IssueLevelChoice(int Index, string Label)
{
    public override string ToString() => Label;
}

/// <summary>The Issues tab's Ignore verbs, handed to every row.</summary>
public sealed record IssueIgnoreCommands(ICommand Ignore, ICommand IgnoreAllLike, ICommand StopIgnoring, ICommand StopIgnoringAllLike);

/// <summary>
///     The Issues tab's finding rows (inspector design D) as ONE flat list: the severity groups in order
///     (Important above Warning), filtered by the Difficulty choice. A row carries what it shows itself, with no
///     <c>$parent</c> lookup in the markup (the jump, the Fix, the location against the beat map, the row on the Fixes card), and
///     its Fix asks the fix menu's own rule check (<see cref="IssueFixViewModel.CanOffer" />), once per row and pass.
///     <para>
///         Identical findings (same rule, same sentence, and both shown or both ignored) fold into ONE group row
///         with their count; it expands to the occurrences, each with its own jump and Ignore.
///     </para>
/// </summary>
public sealed partial class InspectorIssueRows : ObservableObject
{
    private readonly ILocalizationService _localization;
    private readonly ICommand _jump;
    private readonly ICommand _fix;
    private readonly Func<ChartProject?> _project;
    private readonly IssueIgnoreCommands _ignore;
    private readonly HashSet<string> _expanded = new(StringComparer.Ordinal);
    private IReadOnlySet<ArrangementIssue> _ignored = new HashSet<ArrangementIssue>();
    private IReadOnlyList<InspectorIssueGroup> _groups = [];
    private Arrangement? _arrangement;
    private IList<BeatMarker>? _beats;
    private ArrangementIssue? _shown;
    private Dictionary<object, int>? _levels;
    private int _levelCount = -1;
    private bool _refilling;

    [ObservableProperty] private IssueLevelChoice? _selectedLevel;

    public InspectorIssueRows(ILocalizationService localization, ICommand jump, ICommand fix, Func<ChartProject?> project,
        IssueIgnoreCommands ignore)
    {
        _project = project;
        _localization = localization;
        _jump = jump;
        _fix = fix;
        _ignore = ignore;
        ToggleGroup = new RelayCommand<IssueListRow?>(Toggle);
    }

    /// <summary>The rows the list shows.</summary>
    public BulkObservableCollection<IssueListRow> Items { get; } = [];

    /// <summary>A group row's expand / fold.</summary>
    public IRelayCommand<IssueListRow?> ToggleGroup { get; }

    /// <summary>All levels, then each level of the arrangement (hardest last); empty for a one-level arrangement.</summary>
    public ObservableCollection<IssueLevelChoice> LevelChoices { get; } = [];

    /// <summary>The Difficulty choice shows only for an arrangement with more than one level.</summary>
    public bool HasLevelChoices => LevelChoices.Count > 1;

    /// <summary>Which of the next rebuild's findings the charter ignored (they are listed only with Show ignored).</summary>
    public void SetIgnored(IReadOnlySet<ArrangementIssue> ignored) => _ignored = ignored;

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
        foreach (var row in Items) row.IsOnFixesCard = !row.IsGroup && ReferenceEquals(row.Issue, issue);
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
        return IssueFixViewModel.CanOffer(issue, _arrangement, level, _beats, _project());
    }

    internal string MessageOf(ArrangementIssue issue) => ArrangementIssueText.Of(issue, _localization);

    internal string StampOf(ArrangementIssue issue) =>
        ChartPositionLabel.Format(_beats as IReadOnlyList<BeatMarker> ?? _beats?.ToList(), issue.TimeMs);

    internal string CountOf(int count) => _localization.Format("Issues_Row_GroupCount", count);

    internal string Text(string key) => _localization[key];

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
            // Clusters of identical findings, in the order of their first occurrence.
            var clusters = new Dictionary<string, List<(ArrangementIssue Issue, string Level)>>(StringComparer.Ordinal);
            var order = new List<string>();
            foreach (var issue in group.Issues)
            {
                var level = many ? LevelOf(issue) : -1;
                if (wanted >= 0 && level >= 0 && level != wanted) continue;
                var label = level >= 0 ? _localization.Format("Inspector_Issues_RowLevel", level + 1) : "";
                var key = GroupKey(issue, _ignored.Contains(issue));
                if (!clusters.TryGetValue(key, out var members))
                {
                    clusters[key] = members = [];
                    order.Add(key);
                }

                members.Add((issue, label));
            }

            foreach (var key in order) AddCluster(rows, key, clusters[key]);
        }

        Items.ResetWith(rows);
    }

    private void AddCluster(List<IssueListRow> rows, string key, List<(ArrangementIssue Issue, string Level)> members)
    {
        var ignored = _ignored.Contains(members[0].Issue);
        if (members.Count == 1)
        {
            rows.Add(Row(members[0].Issue, members[0].Level, ignored, IssueRowKind.Single, key, 1));
            return;
        }

        rows.Add(Row(members[0].Issue, "", ignored, IssueRowKind.Group, key, members.Count));
        if (!_expanded.Contains(key)) return;
        foreach (var (issue, level) in members) rows.Add(Row(issue, level, ignored, IssueRowKind.Member, key, 1));
    }

    private IssueListRow Row(ArrangementIssue issue, string level, bool ignored, IssueRowKind kind, string key, int count) =>
        new(this, issue, level, _jump, _fix, _ignore)
        {
            IsOnFixesCard = kind != IssueRowKind.Group && ReferenceEquals(issue, _shown),
            IsIgnored = ignored,
            Kind = kind,
            GroupKey = key,
            Count = count,
            IsExpanded = _expanded.Contains(key)
        };

    private static string GroupKey(ArrangementIssue issue, bool ignored) =>
        string.Concat(issue.Code, "\u0001", issue.Message, ignored ? "\u0001i" : "");

    private void Toggle(IssueListRow? row)
    {
        if (row is not { IsGroup: true }) return;
        if (!_expanded.Remove(row.GroupKey)) _expanded.Add(row.GroupKey);
        Fill();
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

/// <summary>What an <see cref="IssueListRow" /> stands for.</summary>
public enum IssueRowKind
{
    /// <summary>A finding with no identical sibling.</summary>
    Single,

    /// <summary>The folded row for identical findings: their count, the expand, and Ignore all.</summary>
    Group,

    /// <summary>One occurrence under an expanded group.</summary>
    Member
}

/// <summary>
///     One row of the Issues tab: a finding (the jump, its Fix when the fix menu has one, Ignore, the location and
///     the level), or a group of identical findings (the count, the expand, Ignore all).
/// </summary>
public sealed partial class IssueListRow : ObservableObject
{
    private readonly InspectorIssueRows _owner;
    private readonly IssueIgnoreCommands _ignoreCommands;
    private string? _stamp;
    private bool? _hasFix;
    private string? _message;

    /// <summary>This row's finding is the one on the Fixes card (its spine lights).</summary>
    [ObservableProperty] private bool _isOnFixesCard;

    internal IssueListRow(InspectorIssueRows owner, ArrangementIssue issue, string level, ICommand jump, ICommand fix,
        IssueIgnoreCommands ignore)
    {
        _owner = owner;
        _ignoreCommands = ignore;
        Issue = issue;
        Level = level;
        Jump = jump;
        Fix = fix;
    }

    /// <summary>The finding, or a group's first occurrence.</summary>
    public ArrangementIssue Issue { get; }

    /// <summary>A single finding, a group of identical ones, or one occurrence in a group.</summary>
    public IssueRowKind Kind { get; init; }

    /// <summary>The identity of the group this row folds or belongs to.</summary>
    internal string GroupKey { get; init; } = "";

    /// <summary>How many identical findings a group row stands for (1 otherwise).</summary>
    public int Count { get; init; } = 1;

    public bool IsGroup => Kind == IssueRowKind.Group;

    public bool IsMember => Kind == IssueRowKind.Member;

    /// <summary>A group row whose occurrences are listed under it.</summary>
    public bool IsExpanded { get; init; }

    /// <summary>The charter ignored this finding (it shows only with Show ignored).</summary>
    public bool IsIgnored { get; init; }

    /// <summary>A folded group draws a right chevron, an expanded one a down chevron.</summary>
    public bool IsFolded => IsGroup && !IsExpanded;

    public bool IsUnfolded => IsGroup && IsExpanded;

    /// <summary>What a screen reader announces: the sentence, and a group's count.</summary>
    public string AccessibleName => ToString();

    /// <summary>"×75" on a group row.</summary>
    public string CountText => IsGroup ? _owner.CountOf(Count) : "";

    /// <summary>A group row's expand / fold, or the finding's jump.</summary>
    public ICommand Activate => IsGroup ? _owner.ToggleGroup : Jump;

    /// <summary>What <see cref="Activate" /> receives.</summary>
    public object Target => IsGroup ? this : Issue;

    /// <summary>"Level 3", or "" for a one-level arrangement, a finding no level holds, or a group.</summary>
    public string Level { get; }

    public bool HasLevel => Level.Length > 0;

    /// <summary>A group row shows no location: its occurrences carry theirs.</summary>
    public bool HasStamp => !IsGroup;

    /// <summary>The location readout against the beat map (<c>M19.3 · 02:13.16</c>), computed when the row is realized.</summary>
    public string Stamp => IsGroup ? "" : _stamp ??= _owner.StampOf(Issue);

    /// <summary>The fix menu would offer a fix for this finding: the row draws its Fix (never on a group row).</summary>
    public bool HasFix => !IsGroup && (_hasFix ??= _owner.CanOffer(Issue));

    public ICommand Jump { get; }

    public ICommand Fix { get; }

    /// <summary>A warning can be ignored or brought back; an error never, so its row draws no button.</summary>
    public bool HasIgnore => FindingIgnores.IsIgnorable(Issue);

    /// <summary>Ignore / Ignore all on a shown finding; Stop ignoring on an ignored one (a group's acts on all).</summary>
    public ICommand IgnoreCommand => (IsIgnored, IsGroup) switch
    {
        (false, false) => _ignoreCommands.Ignore,
        (false, true) => _ignoreCommands.IgnoreAllLike,
        (true, false) => _ignoreCommands.StopIgnoring,
        _ => _ignoreCommands.StopIgnoringAllLike
    };

    public string IgnoreLabel => _owner.Text(IsIgnored ? "Issues_Row_StopIgnoring" : IsGroup ? "Issues_Row_IgnoreAll" : "Issues_Row_Ignore");

    public string IgnoreTip => _owner.Text(IsIgnored ? "Issues_Row_StopIgnoringTip" : IsGroup ? "Issues_Row_IgnoreAllTip" : "Issues_Row_IgnoreTip");

    /// <summary>The finding's sentence in the UI language (a language switch rebuilds the rows).</summary>
    public string Message => _message ??= _owner.MessageOf(Issue);

    /// <summary>The row's tooltip: a group explains its fold, a finding reads its sentence.</summary>
    public string Tip => IsGroup ? _owner.Text("Issues_Row_GroupTip") : Message;

    public override string ToString() => IsGroup ? $"{Message} {CountText}" : Message;
}
