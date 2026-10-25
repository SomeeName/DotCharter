using System.Collections.ObjectModel;
using System.Globalization;
using System.Windows.Input;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using DotCharter.Presentation.Contracts.Editor;
using DotCharter.Services.Editing.Tones;
using DotCharter.Tone.UI.ViewModels.Shell;

namespace DotCharter.UI.ViewModels.Editor;

/// <summary>Which names a <see cref="MarkerGroupsViewModel" /> lists.</summary>
public enum MarkerKind
{
    Section,
    Tone
}

/// <summary>One place a name is used; <see cref="TimeMs" /> is where Jump goes.</summary>
public sealed class MarkerOccurrenceRow(string label, double timeMs) : IMarkerOccurrenceRow
{
    public string Label { get; } = label;
    public double TimeMs { get; } = timeMs;
}

/// <summary>One name: its places, its inline rename and what is wrong with it.</summary>
public sealed partial class MarkerGroupRow(
    string name, string countLabel, IReadOnlyList<MarkerOccurrenceRow> occurrences, string deleteTip, string warning)
    : ObservableObject, IMarkerGroupRow
{
    public string Name { get; } = name;
    public string CountLabel { get; } = countLabel;
    public IReadOnlyList<MarkerOccurrenceRow> Occurrences { get; } = occurrences;
    IReadOnlyList<IMarkerOccurrenceRow> IMarkerGroupRow.Occurrences => Occurrences;
    public string DeleteTip { get; } = deleteTip;
    public string Warning { get; } = warning;
    public bool HasWarning => Warning.Length > 0;

    /// <summary>The rename box; Apply shows only once it differs from <see cref="Name" />.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsRenameDirty))]
    private string _editName = name;

    public bool IsRenameDirty =>
        EditName.Trim().Length > 0 && !string.Equals(EditName.Trim(), Name, StringComparison.Ordinal);

    /// <summary>The places show; the chevron folds them away.</summary>
    [ObservableProperty] private bool _isExpanded = true;

    [ObservableProperty][NotifyPropertyChangedFor(nameof(HasError))] private string _error = "";

    public bool HasError => Error.Length > 0;

    partial void OnEditNameChanged(string value) => Error = "";
}

/// <summary>
///     The Song tab's Sections or Tones, in the Phrases panel's shape (<see cref="IMarkerGroupsScreen" />).
///     Both are project-wide: a section or tone is renamed and deleted in every arrangement, so a
///     place is a moment of the SONG, listed once with the arrangements it is in. Names are in
///     the order they first sound; a tone that is defined but never placed comes last.
///     <para>
///         <see cref="Refresh" /> runs on every committed edit (the Song tab's
///         <c>HistoryChanged</c>), so it republishes NOTHING when the rows would read the same:
///         a note edit changes no name and no place, and a fresh row would also discard the
///         rename being typed into it.
///     </para>
/// </summary>
public sealed partial class MarkerGroupsViewModel : ViewModelBase, IMarkerGroupsScreen
{
    private readonly MarkerKind _kind;
    private readonly Func<ChartProject?> _project;
    private readonly Action<double> _seek;
    private readonly IHistoryService _history;
    private readonly ILocalizationService _localization;
    private readonly IAppLogger _logger;

    public MarkerGroupsViewModel(MarkerKind kind, Func<ChartProject?> project, Action<double> seek,
        IHistoryService history, ILocalizationService localization, IAppLogger logger)
    {
        _kind = kind;
        _project = project;
        _seek = seek;
        _history = history;
        _localization = localization;
        _logger = logger;
    }

    public ObservableCollection<MarkerGroupRow> Groups { get; } = [];
    IReadOnlyList<IMarkerGroupRow> IMarkerGroupsScreen.Groups => Groups;
    ICommand IMarkerGroupsScreen.RenameCommand => RenameCommand;
    ICommand IMarkerGroupsScreen.CancelRenameCommand => CancelRenameCommand;
    ICommand IMarkerGroupsScreen.DeleteCommand => DeleteCommand;
    ICommand IMarkerGroupsScreen.JumpToCommand => JumpToCommand;

    [ObservableProperty] private bool _hasGroups;
    [ObservableProperty] private string _statusLine = "";

    public string Title => _localization[_kind == MarkerKind.Section ? "Inspector_Sections" : "Inspector_Tones"];

    public string EmptyText => _localization[_kind == MarkerKind.Section ? "Inspector_Sections_None" : "Inspector_Tones_None"];

    private sealed record Place(double TimeMs, List<string> Arrangements, bool IsStart);

    private sealed record Group(string Name, List<Place> Places, bool IsUndefined);

    /// <summary>Re-reads the project; publishes only when a row would read differently.</summary>
    public void Refresh()
    {
        var project = _project();
        var groups = project == null ? [] : _kind == MarkerKind.Section ? Sections(project) : Tones(project);
        var rows = groups.Select(g => ToRow(g, project!)).ToList();
        if (!Matches(rows))
        {
            Groups.Clear();
            foreach (var row in rows) Groups.Add(row);
        }

        HasGroups = Groups.Count > 0;
    }

    private bool Matches(List<MarkerGroupRow> rows) =>
        rows.Count == Groups.Count && rows.Select((row, i) =>
            row.Name == Groups[i].Name && row.Warning == Groups[i].Warning &&
            row.Occurrences.Select(o => o.Label).SequenceEqual(Groups[i].Occurrences.Select(o => o.Label))).All(same => same);

    private static List<Group> Sections(ChartProject project)
    {
        var groups = new Dictionary<string, Group>(StringComparer.Ordinal);
        foreach (var arrangement in project.Arrangements)
        {
            foreach (var ev in arrangement.EventPoints)
            {
                if (string.IsNullOrWhiteSpace(ev.Section)) continue;
                if (!groups.TryGetValue(ev.Section, out var group))
                    groups[ev.Section] = group = new Group(ev.Section, [], false);
                AddPlace(group, ev.Position.GetPositionMs(project.Beats), arrangement.Name, false);
            }
        }

        return Ordered(groups.Values);
    }

    private static List<Group> Tones(ChartProject project)
    {
        var groups = ToneKeyEdits.Keys(project)
            .ToDictionary(k => k.Key, k => new Group(k.Key, [], !k.IsDefined), StringComparer.OrdinalIgnoreCase);
        foreach (var arrangement in project.Arrangements)
        {
            if (!string.IsNullOrWhiteSpace(arrangement.StartingTone) &&
                groups.TryGetValue(arrangement.StartingTone.Trim(), out var start))
            {
                AddPlace(start, 0, arrangement.Name, true);
            }

            foreach (var change in arrangement.ToneChanges)
            {
                if (string.IsNullOrWhiteSpace(change.ToneName) ||
                    !groups.TryGetValue(change.ToneName.Trim(), out var group))
                {
                    continue;
                }

                AddPlace(group, change.Position.GetPositionMs(project.Beats), arrangement.Name, false);
            }
        }

        return Ordered(groups.Values);
    }

    /// <summary>One place per moment of the song: the same section in three arrangements is one place.</summary>
    private static void AddPlace(Group group, double timeMs, string arrangement, bool isStart)
    {
        var rounded = Math.Round(timeMs, 1);
        var place = group.Places.FirstOrDefault(p => p.IsStart == isStart && p.TimeMs == rounded);
        if (place == null) group.Places.Add(place = new Place(rounded, [], isStart));
        if (!place.Arrangements.Contains(arrangement)) place.Arrangements.Add(arrangement);
    }

    private static List<Group> Ordered(IEnumerable<Group> groups)
    {
        var list = groups.ToList();
        foreach (var group in list)
            group.Places.Sort((a, b) => a.IsStart != b.IsStart ? b.IsStart.CompareTo(a.IsStart) : a.TimeMs.CompareTo(b.TimeMs));
        return
        [
            .. list.Where(g => g.Places.Count > 0).OrderBy(g => g.Places[0].IsStart ? -1 : g.Places[0].TimeMs)
                .ThenBy(g => g.Name, StringComparer.OrdinalIgnoreCase),
            .. list.Where(g => g.Places.Count == 0).OrderBy(g => g.Name, StringComparer.OrdinalIgnoreCase)
        ];
    }

    private MarkerGroupRow ToRow(Group group, ChartProject project)
    {
        var loc = _localization;
        var several = project.Arrangements.Count > 1;
        var ordinal = 0;
        var occurrences = group.Places.Select(place =>
        {
            var label = place.IsStart
                ? loc["Inspector_Tones_StartOccurrence"]
                : MarkerOccurrenceLabel.Format(loc, ++ordinal, project.Beats, place.TimeMs);
            if (several) label += " · " + string.Join(", ", place.Arrangements);
            return new MarkerOccurrenceRow(label, place.TimeMs);
        }).ToList();

        var count = occurrences.Count;
        var (countLabel, deleteTip) = _kind == MarkerKind.Section
            ? (loc.Plural("Inspector_Sections_PlacesOne", "Inspector_Sections_PlacesFmt", count),
                loc.Plural("Inspector_Sections_DeleteTipOne", "Inspector_Sections_DeleteTipFmt", count, group.Name))
            : (loc.Plural("Inspector_Tones_UsesOne", "Inspector_Tones_UsesFmt", count),
                count == 0
                    ? loc.Format("Inspector_Tones_DeleteTipUnplaced", group.Name)
                    : loc.Plural("Inspector_Tones_DeleteTipOne", "Inspector_Tones_DeleteTipFmt", count, group.Name));
        return new MarkerGroupRow(group.Name, countLabel, occurrences, deleteTip,
            group.IsUndefined ? loc["Inspector_Tones_NotDefined"] : "");
    }

    [RelayCommand]
    private void Rename(MarkerGroupRow? row)
    {
        var project = _project();
        if (row is not { IsRenameDirty: true } || project == null) return;
        var oldName = row.Name;
        var newName = row.EditName.Trim();
        IChartAction action;
        if (_kind == MarkerKind.Tone)
        {
            // ONE verb renames the key in all five places it is stored, or refuses with the
            // reason printed under the row: never a merge.
            var plan = ToneKeyEdits.PlanRename(project, oldName, newName);
            if (!plan.IsReady)
            {
                row.Error = RefusalText(plan);
                if (row.HasError)
                    _logger.Info(AppLogCategory.Project, $"Tone rename '{oldName}' -> '{newName}' refused: {plan.Outcome}.");
                return;
            }

            action = plan.Action!;
        }
        else
        {
            var events = project.Arrangements.SelectMany(a => a.EventPoints).Where(e => e.Section == oldName).ToList();
            action = new RenameStringFieldAction<EventPoint>(events, (e, v) => e.Section = v, oldName, newName);
        }

        if (!action.HasEffect || !_history.Do(action)) return;
        _logger.Info(AppLogCategory.Project, $"Renamed {_kind} '{oldName}' -> '{newName}'.");
        Refresh();
        StatusLine = _localization.Format("Inspector_Markers_Renamed", oldName, newName);
    }

    /// <summary>Esc in the rename box: back to the name.</summary>
    [RelayCommand]
    private void CancelRename(MarkerGroupRow? row) => row?.EditName = row.Name;

    [RelayCommand]
    private void Delete(MarkerGroupRow? row)
    {
        var project = _project();
        if (row == null || project == null) return;
        var name = row.Name;
        var parts = new List<IChartAction>();
        if (_kind == MarkerKind.Tone)
        {
            // The definition, the slots and the declared names go with the changes.
            if (ToneKeyEdits.BuildDelete(project, name) is { } delete) parts.Add(delete);
        }
        else
        {
            foreach (var arrangement in project.Arrangements) AddSectionDelete(parts, arrangement, name);
        }

        var action = new CompositeAction(parts) { LabelKey = "History_Delete" };
        if (!action.HasEffect || !_history.Do(action)) return;
        _logger.Info(AppLogCategory.Project, $"Bulk-deleted {_kind} '{name}'.");
        Refresh();
        StatusLine = _localization.Format("Inspector_Markers_Deleted", name);
    }

    /// <summary>A marker whose only payload is the section goes; one that also carries a phrase keeps it.</summary>
    private static void AddSectionDelete(List<IChartAction> parts, Arrangement arrangement, string name)
    {
        var remove = arrangement.EventPoints.Where(ev => ev.Section == name && string.IsNullOrEmpty(ev.Phrase)).ToList();
        var clear = arrangement.EventPoints.Where(ev => ev.Section == name && !string.IsNullOrEmpty(ev.Phrase)).ToList();
        if (remove.Count > 0) parts.Add(new RemoveEventPointsAction(arrangement.EventPoints, remove));
        if (clear.Count > 0)
            parts.Add(new RenameStringFieldAction<EventPoint>(clear, (e, v) => e.Section = v, name, string.Empty));
    }

    [RelayCommand]
    private void JumpTo(MarkerOccurrenceRow? row)
    {
        if (row != null) _seek(row.TimeMs);
    }

    /// <summary>The printed reason a tone rename was refused; empty for "nothing to do".</summary>
    private string RefusalText(ToneKeyRenamePlan plan) => plan.Outcome switch
    {
        ToneKeyRenameOutcome.InvalidKey => _localization["Dialog_Tones_KeyInvalid"],
        ToneKeyRenameOutcome.Taken => string.Format(CultureInfo.CurrentCulture,
            _localization["Inspector_Tones_RenameTaken"], plan.NewKey),
        _ => ""
    };
}
