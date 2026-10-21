using System.Collections.ObjectModel;
using Avalonia.Media;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using DotCharter.Core.Linking;
using DotCharter.Services.Editing.Linking;
using DotCharter.UI.Editing.Linking;

namespace DotCharter.UI.ViewModels.Editor.Linking;

/// <summary>One placement in the link panel.</summary>
public sealed partial class LinkPanelRow(LinkPlacement placement, string range, string section, bool isHere, Action changed,
    Action<LinkPlacement> goTo) : ObservableObject
{
    public LinkPlacement Placement { get; } = placement;
    public string Range { get; } = range;
    public string Section { get; } = section;

    /// <summary>The placement the panel was opened from (the clicked chip, the selection).</summary>
    public bool IsHere { get; } = isHere;

    [ObservableProperty] private bool _isChecked = isHere;

    partial void OnIsCheckedChanged(bool value) => changed();

    [RelayCommand]
    private void GoTo() => goTo(Placement);
}

/// <summary>One arrangement's placements in the link panel.</summary>
public sealed class LinkPanelSection(string header, string count, IReadOnlyList<LinkPanelRow> rows)
{
    public string Header { get; } = header;
    public string Count { get; } = count;
    public IReadOnlyList<LinkPanelRow> Rows { get; } = rows;
}

/// <summary>A run that plays the same as the group again and can be linked back to it.</summary>
public sealed partial class LinkPanelSuggestion(LinkRunRequest run, string arrangement, string range, string section,
    Action<LinkRunRequest> link, Action<LinkPlacement> goTo)
{
    public LinkRunRequest Run { get; } = run;
    public string Arrangement { get; } = arrangement;
    public string Range { get; } = range;
    public string Section { get; } = section;

    [RelayCommand]
    private void Link() => link(Run);

    [RelayCommand]
    private void GoTo() => goTo(new LinkPlacement { Arrangement = Run.Arrangement, Position = Run.Start, EndPosition = Run.End });
}

/// <summary>
///     The link panel: one group's placements per arrangement, with Unlink selected, New group
///     from selected and Split by arrangement, and the runs that play the same again with a Link
///     button each — every edit one undo step through the history.
/// </summary>
public sealed partial class LinkPanelViewModel : ObservableObject
{
    private readonly ProjectState _state;
    private readonly IHistoryService _history;
    private readonly ILocalizationService _localization;
    private readonly Action<LinkPlacement> _goTo;
    private string? _groupId;
    private Arrangement? _focusArrangement;
    private FractionalPosition? _focusPosition;

    public LinkPanelViewModel(ProjectState state, IHistoryService history, ILocalizationService localization,
        Action<LinkPlacement> goTo)
    {
        _state = state;
        _history = history;
        _localization = localization;
        _goTo = goTo;
    }

    [ObservableProperty] private string _title = "";
    [ObservableProperty] private string _subtitle = "";
    [ObservableProperty] private IBrush _groupBrush = Brushes.Transparent;
    [ObservableProperty] private string _selectionSummary = "";
    [ObservableProperty] private bool _hasGroup;
    [ObservableProperty] private bool _hasSuggestions;

    public ObservableCollection<LinkPanelSection> Sections { get; } = [];

    /// <summary>Unlinked runs equal to the group again (<see cref="LinkRelinkSuggestions" />), read on
    /// every <see cref="Refresh" />, never per frame.</summary>
    public ObservableCollection<LinkPanelSuggestion> Suggestions { get; } = [];

    /// <summary>The group the panel shows (null: closed or dissolved).</summary>
    public string? GroupId => _groupId;

    /// <summary>Shows <paramref name="groupId" />, checking the placement that holds <paramref name="focusPosition" />.</summary>
    public void Open(string groupId, Arrangement? focusArrangement, FractionalPosition? focusPosition)
    {
        _groupId = groupId;
        _focusArrangement = focusArrangement;
        _focusPosition = focusPosition;
        Refresh();
    }

    /// <summary>The panel closed: forget the group, so edits made while it is closed re-read nothing.</summary>
    public void Close()
    {
        _groupId = null;
        Refresh();
    }

    /// <summary>Re-reads the group (after an edit, an undo, a new project).</summary>
    public void Refresh()
    {
        Sections.Clear();
        Suggestions.Clear();
        var project = _state.ActiveProject;
        var group = project?.LinkGroups.FirstOrDefault(g => g.Id == _groupId);
        HasGroup = group != null;
        HasSuggestions = false;
        if (project == null || group == null)
        {
            Title = Subtitle = SelectionSummary = "";
            NotifyCommands();
            return;
        }

        var measures = MeasureGrid.Build(project.Beats);
        var first = group.Placements.FirstOrDefault();
        var count = first == null ? 0 : LinkMeasureLabels.MeasureCount(measures, first);
        var name = string.IsNullOrEmpty(group.Name) ? _localization.Format("Link_GroupTitle", group.Letter) : group.Name;
        Title = $"{name} ×{group.Placements.Count}";
        var signature = first == null ? "" : LinkMeasureLabels.Signature(measures, first);
        var length = _localization.Plural("LinkPanel_MeasuresOne", "LinkPanel_MeasuresFmt", count);
        Subtitle = signature.Length == 0 ? length : $"{length} · {signature}";
        GroupBrush = new SolidColorBrush(LinkLaneModel.ColorOf(project, group));

        foreach (var arrangement in project.Arrangements)
        {
            var placements = group.Placements.Where(p => ReferenceEquals(p.Arrangement, arrangement)).OrderBy(p => p.Position).ToList();
            if (placements.Count == 0) continue;
            var rows = placements.Select((p, i) => new LinkPanelRow(p, LinkMeasureLabels.Range(measures, p),
                SectionLabel(arrangement, p, i), IsHere(p), UpdateSummary, _goTo)).ToList();
            Sections.Add(new LinkPanelSection(arrangement.Name, $"×{placements.Count}", rows));
        }

        foreach (var run in LinkRelinkSuggestions.Find(project, group))
        {
            var placement = new LinkPlacement { Arrangement = run.Arrangement, Position = run.Start, EndPosition = run.End };
            Suggestions.Add(new LinkPanelSuggestion(run, run.Arrangement.Name, LinkMeasureLabels.Range(measures, placement),
                SectionAt(run.Arrangement, run.Start), Link, _goTo));
        }

        HasSuggestions = Suggestions.Count > 0;
        UpdateSummary();
    }

    /// <summary>Links a suggested run back into the group: one undo step, refused unless it still plays the same.</summary>
    private void Link(LinkRunRequest run) => Apply(project => LinkGroupEdits.AddPlacement(project, _groupId!, run));

    private bool IsHere(LinkPlacement placement) =>
        _focusPosition is { } at && ReferenceEquals(placement.Arrangement, _focusArrangement) &&
        at >= placement.Position && at < placement.EndPosition;

    private static string SectionLabel(Arrangement arrangement, LinkPlacement placement, int index)
    {
        var section = SectionAt(arrangement, placement.Position);
        return string.IsNullOrEmpty(section) ? "" : $"{section} {index + 1}";
    }

    private static string SectionAt(Arrangement arrangement, FractionalPosition at) =>
        arrangement.EventPoints
            .Where(e => !string.IsNullOrEmpty(e.Section) && e.Position <= at)
            .MaxBy(e => e.Position)?.Section ?? "";

    private List<LinkPlacement> Checked() => [.. Sections.SelectMany(s => s.Rows).Where(r => r.IsChecked).Select(r => r.Placement)];

    private int Total => Sections.Sum(s => s.Rows.Count);

    private void UpdateSummary()
    {
        SelectionSummary = HasGroup ? _localization.Format("LinkPanel_SelectedFmt", Checked().Count, Total) : "";
        NotifyCommands();
    }

    private void NotifyCommands()
    {
        UnlinkSelectedCommand.NotifyCanExecuteChanged();
        NewGroupFromSelectedCommand.NotifyCanExecuteChanged();
        SplitByArrangementCommand.NotifyCanExecuteChanged();
    }

    private bool CanUnlink() => HasGroup && Checked().Count > 0;

    private bool CanNewGroup() => HasGroup && Checked().Count >= 2 && Checked().Count < Total;

    private bool CanSplit() => HasGroup && Sections.Count >= 2;

    [RelayCommand(CanExecute = nameof(CanUnlink))]
    private void UnlinkSelected() => Apply(project => LinkGroupEdits.Unlink(project, _groupId!, Checked()));

    [RelayCommand(CanExecute = nameof(CanNewGroup))]
    private void NewGroupFromSelected() => Apply(project => LinkGroupEdits.NewGroupFrom(project, _groupId!, Checked()));

    [RelayCommand(CanExecute = nameof(CanSplit))]
    private void SplitByArrangement() => Apply(project => LinkGroupEdits.SplitByArrangement(project, _groupId!));

    private void Apply(Func<ChartProject, IChartAction?> edit)
    {
        if (_state.ActiveProject is not { } project || edit(project) is not { } action) return;
        _history.EndGesture();
        _history.Do(action);
        Refresh();
    }
}
