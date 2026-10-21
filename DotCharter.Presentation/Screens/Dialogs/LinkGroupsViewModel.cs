using System.Windows.Input;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using DotCharter.Core.Domain.Primitives;
using DotCharter.Core.Domain.Project;
using DotCharter.Core.Linking;
using DotCharter.Core.Utilities;
using DotCharter.Presentation.Contracts.Dialogs;
using DotCharter.Presentation.Dialogs;
using DotCharter.Services.Editing.Linking;
using DotCharter.Services.State;
using DotCharter.Tone.Infrastructure.Configuration;

namespace DotCharter.Presentation.Screens.Dialogs;

/// <summary>
///     Tools ▸ Linked measures. The rows are rebuilt from the project after every edit, and every
///     edit goes through <see cref="LinkGroupEdits" /> and the history, so each is one undo step.
/// </summary>
public sealed class LinkGroupsViewModel : DialogViewModel<bool>, ILinkGroupsScreen, IDialogSetup<LinkGroupsRequest>
{
    private readonly ChartEditDialogServices _edit;
    private readonly RelayCommand _linkSelected;
    private IReadOnlyList<ILinkGroupRow> _groups = [];
    private IReadOnlyList<ILinkSuggestionRow> _suggestions = [];
    private bool _changed;

    public LinkGroupsViewModel(ChartEditDialogServices edit)
    {
        _edit = edit;
        RenameCommand = new RelayCommand<ILinkGroupRow>(Rename);
        DeleteCommand = new RelayCommand<ILinkGroupRow>(Delete);
        GoToCommand = new RelayCommand<ILinkPlacementRow>(GoTo);
        _linkSelected = new RelayCommand(LinkSelected, () => _suggestions.Any(s => s.IsSelected));
        CloseCommand = new RelayCommand(() => Close(_changed));
        Refresh();
    }

    public bool HasGroups => _groups.Count > 0;

    public IReadOnlyList<ILinkGroupRow> Groups
    {
        get => _groups;
        private set
        {
            if (!SetProperty(ref _groups, value)) return;
            OnPropertyChanged(nameof(HasGroups));
        }
    }

    public ICommand RenameCommand { get; }

    public ICommand DeleteCommand { get; }

    public ICommand GoToCommand { get; }

    public bool HasSuggestions => _suggestions.Count > 0;

    public IReadOnlyList<ILinkSuggestionRow> Suggestions
    {
        get => _suggestions;
        private set
        {
            if (!SetProperty(ref _suggestions, value)) return;
            OnPropertyChanged(nameof(HasSuggestions));
            _linkSelected.NotifyCanExecuteChanged();
        }
    }

    public ICommand LinkSelectedCommand => _linkSelected;

    public ICommand CloseCommand { get; }

    public void Configure(LinkGroupsRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);
        var l = _edit.Localization;
        Suggestions =
        [
            .. request.Suggestions.Select(ILinkSuggestionRow (s) => new SuggestionRow(s,
                l.Format("LinkGroups_SuggestionTitle", LinkGroupText.Name(l, s.Name),
                    l.Plural("LinkGroups_MeasuresOne", "LinkGroups_Measures", s.MeasureCount)),
                SuggestionDetail(s), _linkSelected.NotifyCanExecuteChanged))
        ];
    }

    private string SuggestionDetail(LinkSuggestion suggestion)
    {
        var l = _edit.Localization;
        var ranges = string.Join(", ", suggestion.FirstMeasures.Distinct().Order()
            .Select(m => LinkGroupText.Bars(m, suggestion.MeasureCount)));
        var detail = $"{LinkGroupText.PerArrangement(l, suggestion.Runs.Select(r => r.Arrangement))} · {ranges}";
        return suggestion.FromRepeats ? $"{detail} · {l["LinkGroups_FromRepeats"]}" : detail;
    }

    private void Refresh()
    {
        var project = _edit.State.ActiveProject;
        if (project == null)
        {
            Groups = [];
            return;
        }

        var measures = MeasureGrid.Build(project.Beats);
        var l = _edit.Localization;
        Groups =
        [
            .. project.LinkGroups.Select(ILinkGroupRow (g) => new GroupRow(g.Id, g.Letter, g.Name,
                LinkGroupText.PerArrangement(l, g.Placements.Select(p => p.Arrangement)),
                [.. g.Placements.Where(p => p.Arrangement != null).Select(p => Placement(measures, p))]))
        ];
    }

    private PlacementRow Placement(IReadOnlyList<MeasureSpan> measures, LinkPlacement placement)
    {
        var arrangement = placement.Arrangement!;
        var label = LinkRuns.TryResolve(measures, placement, out var first, out var count)
            ? $"{arrangement.Name} · {LinkGroupText.Bars(first, count)}"
            : arrangement.Name;
        return new PlacementRow(label, arrangement, placement.Position);
    }

    private void Rename(ILinkGroupRow? row)
    {
        if (row is not GroupRow group || _edit.State.ActiveProject is not { } project) return;
        Run(LinkGroupEdits.Rename(project, group.Id, group.Name), $"rename link group {group.Letter}");
    }

    private void Delete(ILinkGroupRow? row)
    {
        if (row is not GroupRow group || _edit.State.ActiveProject is not { } project) return;
        Run(LinkGroupEdits.Delete(project, group.Id), $"unlink link group {group.Letter}");
    }

    private void Run(IChartAction? action, string what)
    {
        if (action != null && _edit.History.Do(action))
        {
            _changed = true;
            _edit.Logger.Info(AppLogCategory.Editing, $"Linked measures manager: {what}.");
        }

        Refresh();
    }

    private void GoTo(ILinkPlacementRow? row)
    {
        if (row is not PlacementRow placement || _edit.State.ActiveProject is not { } project) return;
        var editor = _edit.Editor;
        if (!ReferenceEquals(editor.ActiveArrangement, placement.Arrangement)) editor.SetActiveArrangement(placement.Arrangement);
        editor.ExecuteSeek(TimelineMath.CalculateTimeMs(placement.Position, project.Beats));
        Close(true);
    }

    private void LinkSelected()
    {
        if (_edit.State.ActiveProject is not { } project) return;
        var chosen = _suggestions.OfType<SuggestionRow>().Where(s => s.IsSelected).ToList();
        if (GpLinkSuggestionOffer.Link(_edit, project, chosen.Select(s => s.Suggestion)) > 0) _changed = true;
        Suggestions = [.. _suggestions.Except(chosen)];
        Refresh();
    }

    private sealed class GroupRow(string id, string letter, string name, string summary, IReadOnlyList<ILinkPlacementRow> placements)
        : ObservableObject, ILinkGroupRow
    {
        private string _name = name;

        public string Id { get; } = id;

        public string Letter { get; } = letter;

        public string Name
        {
            get => _name;
            set => SetProperty(ref _name, value);
        }

        public string Summary { get; } = summary;

        public IReadOnlyList<ILinkPlacementRow> Placements { get; } = placements;
    }

    private sealed record PlacementRow(string Label, Arrangement Arrangement, FractionalPosition Position) : ILinkPlacementRow;

    private sealed class SuggestionRow(LinkSuggestion suggestion, string title, string detail, Action changed)
        : ObservableObject, ILinkSuggestionRow
    {
        private bool _isSelected = true;

        public LinkSuggestion Suggestion { get; } = suggestion;

        public bool IsSelected
        {
            get => _isSelected;
            set
            {
                if (SetProperty(ref _isSelected, value)) changed();
            }
        }

        public string Title { get; } = title;

        public string Detail { get; } = detail;
    }
}
