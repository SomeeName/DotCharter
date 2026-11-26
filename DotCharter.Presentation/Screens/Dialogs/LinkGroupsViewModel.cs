using System.ComponentModel;
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
public sealed class LinkGroupsViewModel : DialogViewModel<bool>, ILinkGroupsScreen, IDialogSetup<LinkGroupsRequest>, IDisposable
{
    private readonly ChartEditDialogServices _edit;
    private readonly Func<ChartProject, MeasureContentOptions, CancellationToken, Action<string>, IReadOnlyList<LinkSuggestion>> _detect;
    private readonly AsyncRelayCommand _detectCommand;
    private readonly RelayCommand _linkSelected;
    private IReadOnlyList<ILinkGroupRow> _groups = [];
    private IReadOnlyList<ILinkSuggestionRow> _suggestions = [];
    private bool _isDetecting;
    private bool _showDetectionRetry;
    private string _detectionStatus = string.Empty;
    private bool _changed;

    public LinkGroupsViewModel(ChartEditDialogServices edit) : this(edit,
        (project, rule, token, reportLimit) => LinkSuggestionFinder.Find(project, project.Arrangements, null, rule, token, reportLimit))
    {
    }

    internal LinkGroupsViewModel(ChartEditDialogServices edit,
        Func<ChartProject, MeasureContentOptions, CancellationToken, Action<string>, IReadOnlyList<LinkSuggestion>> detect)
    {
        _edit = edit;
        _detect = detect;
        RenameCommand = new RelayCommand<ILinkGroupRow>(Rename);
        DeleteCommand = new RelayCommand<ILinkGroupRow>(Delete);
        GoToCommand = new RelayCommand<ILinkPlacementRow>(GoTo);
        _linkSelected = new RelayCommand(LinkSelected, () => _suggestions.Any(s => s.IsSelected));
        _detectCommand = new AsyncRelayCommand(DetectAsync);
        CancelDetectionCommand = new RelayCommand(_detectCommand.Cancel);
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

    public bool IsDetecting
    {
        get => _isDetecting;
        private set => SetProperty(ref _isDetecting, value);
    }

    public string DetectionStatus
    {
        get => _detectionStatus;
        private set => SetProperty(ref _detectionStatus, value);
    }

    public bool ShowDetectionRetry
    {
        get => _showDetectionRetry;
        private set => SetProperty(ref _showDetectionRetry, value);
    }

    public ICommand DetectCommand => _detectCommand;
    public ICommand CancelDetectionCommand { get; }

    public ICommand CloseCommand { get; }

    public void Dispose() => EndOpenWork();

    public void Configure(LinkGroupsRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);
        if (request.Suggestions.Count > 0) ShowSuggestions(request.Suggestions);
        else _detectCommand.Execute(null);
    }

    private void ShowSuggestions(IReadOnlyList<LinkSuggestion> suggestions)
    {
        Suggestions =
        [
            .. suggestions.Select(ILinkSuggestionRow (s) => new SuggestionRow(s, SuggestionTitle(s),
                SuggestionDetail(s), _linkSelected.NotifyCanExecuteChanged))
        ];
    }

    /// <summary>"Verse · 4 measures", or "Join link B · 1 measure" for runs that join an existing group.</summary>
    private string SuggestionTitle(LinkSuggestion suggestion)
    {
        var l = _edit.Localization;
        var length = l.Plural("LinkGroups_MeasuresOne", "LinkGroups_Measures", suggestion.MeasureCount);
        return suggestion.Join is { } join
            ? l.Format("LinkGroups_SuggestionJoinTitle", join.Letter, length)
            : l.Format("LinkGroups_SuggestionTitle", LinkGroupText.Name(l, suggestion.Name), length);
    }

    private async Task DetectAsync(CancellationToken cancellation)
    {
        if (IsDetecting || OpenToken.IsCancellationRequested || _edit.State.ActiveProject is not { } project) return;
        using var run = CancellationTokenSource.CreateLinkedTokenSource(OpenToken, cancellation);
        var token = run.Token;
        var completed = false;
        void CancelOnProjectSwitch(object? sender, PropertyChangedEventArgs args)
        {
            if (args.PropertyName == nameof(ProjectState.ActiveProject)) run.Cancel();
        }

        _edit.State.ProjectChanged += run.Cancel;
        _edit.State.PropertyChanged += CancelOnProjectSwitch;
        _edit.History.HistoryChanged += run.Cancel;
        IsDetecting = true;
        ShowDetectionRetry = false;
        DetectionStatus = string.Empty;
        Suggestions = [];
        try
        {
            await Task.Yield();
            token.ThrowIfCancellationRequested();
            var snapshot = ChartProjectCloner.Clone(project, retainIdentities: true);
            var live = snapshot.Arrangements.Zip(project.Arrangements).ToDictionary(a => a.First, a => a.Second);
            var rule = LinkSuggestionFinder.RuleFor(_edit.Config.LoadAppConfig());
            var (proposals, limited) = await Task.Run(() => Search(snapshot, rule, token), token);
            token.ThrowIfCancellationRequested();
            ShowSuggestions([.. proposals.Select(s => s with
            {
                Runs = [.. s.Runs.Select(r => r with { Arrangement = live[r.Arrangement] })]
            })]);
            if (limited) DetectionStatus = _edit.Localization["LinkGroups_DetectionLimit"];
            completed = !limited;
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested)
        {
        }
        catch (Exception ex)
        {
            _edit.Logger.Warning(AppLogCategory.Editing, "Linked measures detection failed.", ex);
            if (!token.IsCancellationRequested)
            {
                var reason = ex.Message.Split(['\r', '\n'], 2)[0].Trim();
                DetectionStatus = _edit.Localization.Format("LinkGroups_DetectionFailed", reason.Length > 160 ? reason[..157] + "..." : reason);
            }
        }
        finally
        {
            _edit.History.HistoryChanged -= run.Cancel;
            _edit.State.PropertyChanged -= CancelOnProjectSwitch;
            _edit.State.ProjectChanged -= run.Cancel;
            ShowDetectionRetry = !completed && !OpenToken.IsCancellationRequested;
            IsDetecting = false;
        }
    }

    private (IReadOnlyList<LinkSuggestion> Suggestions, bool Limited) Search(
        ChartProject snapshot, MeasureContentOptions rule, CancellationToken token)
    {
        var limited = false;
        var suggestions = _detect(snapshot, rule, token, message =>
        {
            limited = true;
            _edit.Logger.Warning(AppLogCategory.Editing, message);
        });
        return (suggestions, limited);
    }

    private string SuggestionDetail(LinkSuggestion suggestion)
    {
        var l = _edit.Localization;
        var ranges = string.Join(", ", suggestion.FirstMeasures.Distinct().Order()
            .Select(m => LinkGroupText.Bars(m, suggestion.MeasureCount)));
        var detail = $"{LinkGroupText.PerArrangement(l, suggestion.Runs.Select(r => r.Arrangement))} · {ranges}";
        if (suggestion.RequiresReview)
        {
            return suggestion.Mismatch is { } mismatch
                ? $"{detail} · {l["LinkGroups_NearMatch"]}: {LinkGroupText.Mismatch(l, mismatch)}"
                : $"{detail} · {l["LinkGroups_DifferentContent"]}";
        }

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
        private bool _isSelected = !suggestion.RequiresReview;

        public LinkSuggestion Suggestion { get; } = suggestion;

        public bool IsSelected
        {
            get => _isSelected;
            set
            {
                if (Suggestion.RequiresReview) return;
                if (SetProperty(ref _isSelected, value)) changed();
            }
        }

        public string Title { get; } = title;

        public string Detail { get; } = detail;
    }
}
