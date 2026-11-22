using System.ComponentModel;
using System.Windows.Input;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using DotCharter.Core.Domain.Guitar;
using DotCharter.Core.Domain.Project;
using DotCharter.Core.Utilities;
using DotCharter.Localization;
using DotCharter.Presentation.Contracts.Editor;
using DotCharter.Presentation.Dialogs;
using DotCharter.Presentation.Navigation;
using DotCharter.Presentation.Shared;
using DotCharter.Services.Audio.Coordination;
using DotCharter.Services.Diagnostics;
using DotCharter.Services.Editing;
using DotCharter.Services.Editing.ChordShapes;
using DotCharter.Services.State;
using DotCharter.Tone.Diagnostics;

namespace DotCharter.Presentation.Screens.Editor;

/// <summary>
///     The Shapes panel over the active arrangement. Visible history, arrangement and level
///     changes share one queued refresh. Hidden changes wait for activation; cards are reused
///     by template so an edit updates text in place and the list is only replaced when the set
///     of templates or the search changes.
///     <para>
///         A merge, or a delete of a shape that is still used, waits in ONE confirmation bar that
///         says what moves where; nothing changes before its yes. The card verbs, the bar and
///         Clean up are in <c>ShapesPanelViewModel.Verbs.cs</c>.
///     </para>
///     <para>
///         Edit opens the shape editor on the card's shape, its scope chosen there. Cards are ticked for
///         the bulk verbs (Edit together, Merge into one, Delete), which are <see cref="ShapesBulkVerbs" />.
///         Clean up opens the Clean up shapes dialog.
///     </para>
/// </summary>
public sealed partial class ShapesPanelViewModel : ObservableObject, IShapePanelScreen, IShapeRowOwner, IShapesBulkHost, IShapesDragHost, IScreenLifecycle
{
    private readonly IChartEditorSurface _editor;
    private readonly IHistoryService _history;
    private readonly ProjectState _state;
    private readonly ILocalizationService _localization;
    private readonly ScreenRefreshScheduler _refresh;
    private readonly IChordShapePlacementService _placement;
    private readonly IEditorTransport _transport;
    private readonly IDialogs _dialogs;
    private readonly Func<Arrangement, ChordNamingOptions> _naming;
    private readonly RelayCommand _cleanUp;
    private readonly ShapesBulkVerbs _bulk;
    private readonly ShapesDragVerbs _drag;
    private readonly ShapeUsesList _usesList;
    private readonly RelayCommand _splitUses;

    private List<ShapePanelRowViewModel> _allRows = [];
    private ChordShapeIssues _issues = new([]);
    private ShapePanelRowViewModel? _mergeSource;
    private ShapePanelRowViewModel? _dragOver;
    private PendingVerb? _pending;
    private ShapePanelRowViewModel? _currentRow;
    private bool _shown = true;

    [ObservableProperty] private string _summary = "";
    [ObservableProperty] private string _searchText = "";
    [ObservableProperty] private IReadOnlyList<IShapePanelRow> _rows = [];
    [ObservableProperty] private bool _hasRows;
    [ObservableProperty] private string _emptyText = "";
    [ObservableProperty] private bool _hasCleanUp;
    [ObservableProperty] private string _cleanUpLabel = "";
    [ObservableProperty] private bool _hasAttentionFilter;
    [ObservableProperty] private string _attentionFilterLabel = "";
    [ObservableProperty] private bool _showAttentionOnly;
    [ObservableProperty] private bool _isChoosingMergeTarget;
    [ObservableProperty] private string _mergeHintText = "";
    [ObservableProperty] private bool _hasPending;
    [ObservableProperty] private string _pendingText = "";
    [ObservableProperty] private string _pendingConfirmLabel = "";
    [ObservableProperty] private bool _isPendingDestructive;
    [ObservableProperty] private bool _hasSelection;
    [ObservableProperty] private string _selectionText = "";
    [ObservableProperty] private bool _hasCurrentRow;
    [ObservableProperty] private IReadOnlyList<IShapeUseRow> _uses = [];
    [ObservableProperty] private bool _hasUses;
    [ObservableProperty] private string _usesSummary = "";
    [ObservableProperty] private bool _canSplitUses;

    /// <summary>True while the Uses page is changing the editor's selection: the host stays on this tab for it.</summary>
    public bool IsDrivingSelection => _usesList.IsDriving;

    public ShapesPanelViewModel(
        IChartEditorSurface editor,
        IHistoryService history,
        ProjectState state,
        ILocalizationService localization,
        IUiDispatcher dispatcher,
        ShapesPanelParts parts)
    {
        _editor = editor;
        _history = history;
        _state = state;
        _localization = localization;
        _refresh = new ScreenRefreshScheduler(dispatcher, Refresh);
        _placement = parts.Placement;
        _transport = parts.Transport;
        _dialogs = parts.Dialogs;
        _naming = parts.Naming;
        _bulk = new ShapesBulkVerbs(editor, localization, parts.Dialogs, this);
        _drag = new ShapesDragVerbs(this);
        _usesList = new ShapeUsesList(editor, state, localization);
        _splitUses = new RelayCommand(() => _currentRow?.SplitSelectionCommand.Execute(null), () => CanSplitUses);
        SplitUsesCommand = _splitUses;
        _cleanUp = new RelayCommand(CleanUp, () => HasCleanUp);
        CleanUpCommand = _cleanUp;
        RepeatPreviousChordCommand = new RelayCommand(RepeatPreviousChord);
        ConfirmPendingCommand = new RelayCommand(ConfirmPending);
        CancelPendingCommand = new RelayCommand(CancelPending);

        _history.HistoryChanged += ScheduleRefresh;
        _state.PropertyChanged += OnStateChanged;
        _editor.Selection.SelectionChanged += (_, _) => OnSelectionChanged();
        Refresh();
    }

    public ICommand CleanUpCommand { get; }

    public ICommand RepeatPreviousChordCommand { get; }

    public ICommand ConfirmPendingCommand { get; }

    public ICommand CancelPendingCommand { get; }

    public ICommand EditSelectedCommand => _bulk.EditCommand;

    public ICommand MergeSelectedCommand => _bulk.MergeCommand;

    public ICommand DeleteSelectedCommand => _bulk.DeleteCommand;

    public ICommand ClearSelectionCommand => _bulk.ClearCommand;

    public ICommand SplitUsesCommand { get; }

    public IShapePanelRow? CurrentRow
    {
        get => _currentRow;
        set => SetCurrent(value as ShapePanelRowViewModel);
    }

    /// <summary>Every card, unfiltered: the search's input and the test hook.</summary>
    internal IReadOnlyList<ShapePanelRowViewModel> AllRows => _allRows;

    public void OnShown()
    {
        _shown = true;
        if (_usesList.CatchUp()) PublishUses();
        _refresh.OnShown();
    }

    public void OnHidden()
    {
        _shown = false;
        _refresh.OnHidden();
    }

    /// <summary>Re-reads the arrangement now. Normally reached through the coalesced post.</summary>
    public void Refresh() => UiWorkTiming.Measure(nameof(ShapesPanelViewModel), RefreshCore);

    private void RefreshCore()
    {
        _refresh.Acknowledge();
        var arrangement = _editor.ActiveArrangement;
        if (arrangement is null)
        {
            _allRows = [];
            _issues = new ChordShapeIssues([]);
            Publish(0, 0, "ChordShapes_Empty_NoArrangement");
            return;
        }

        var naming = _naming(arrangement);
        var entries = ChordShapeCatalog.Build(arrangement, _state.ActiveLevel);
        _issues = new ChordShapeIssues(entries);
        var previous = new Dictionary<ChordTemplate, ShapePanelRowViewModel>(ReferenceEqualityComparer.Instance);
        foreach (var row in _allRows) previous[row.Template] = row;
        var rows = new List<ShapePanelRowViewModel>(entries.Count);
        foreach (var entry in entries)
        {
            var row = previous.TryGetValue(entry.Template, out var kept) ? kept : new ShapePanelRowViewModel(entry.Template, this, _localization);
            var issue = _issues.For(entry.Template);
            var keeper = issue.Keeper is { } k ? ChordShapeNames.DisplayName(k, arrangement.StringCount) : "";
            var suggested = entry.Template.Name.Length == 0 && !entry.Template.Unnamed
                ? NewChordNames.Suggest(arrangement, entry.Template.Frets, naming)
                : "";
            row.Update(entry, issue, keeper, arrangement.StringCount, suggested);
            rows.Add(row);
        }

        _allRows = rows;
        DropStalePending(arrangement);
        _bulk.DropIfStale(arrangement);
        Publish(entries.Count, rows.Count(r => r.HasIssue), entries.Count == 0 ? "ChordShapes_Empty_NoTemplates" : "Shapes_Empty_NoMatch");
        var cleanUp = ShapeCleanupFinder.Find(arrangement, entries).Count;
        HasCleanUp = cleanUp > 0;
        CleanUpLabel = cleanUp > 0 ? _localization.Format("Shapes_CleanUp", cleanUp) : "";
    }

    private void Publish(int shapes, int attention, string emptyKey)
    {
        Summary = shapes == 0
            ? ""
            : attention == 0
                ? _localization.Format("Shapes_SummaryTidy",
                    _localization.Plural("ChordShapes_ShapeCountOne", "ChordShapes_ShapeCount", shapes))
                : _localization.Format("Shapes_Summary",
                    _localization.Plural("ChordShapes_ShapeCountOne", "ChordShapes_ShapeCount", shapes), attention);
        EmptyText = _localization[emptyKey];
        if (shapes == 0)
        {
            HasCleanUp = false;
            CleanUpLabel = "";
        }

        AttentionFilterLabel = _localization.Format("Shapes_Filter_Attention", attention);
        HasAttentionFilter = attention > 0 || ShowAttentionOnly;
        ApplySearch();
        RefreshMergeState();
        UpdateSplitAvailability();
        _bulk.Refresh();
    }

    partial void OnHasCleanUpChanged(bool value) => _cleanUp.NotifyCanExecuteChanged();

    partial void OnSearchTextChanged(string value) => ApplySearch();

    partial void OnShowAttentionOnlyChanged(bool value)
    {
        HasAttentionFilter = value || _allRows.Any(r => r.HasIssue);
        ApplySearch();
    }

    private void ApplySearch()
    {
        var needle = SearchText.Trim();
        var visible = _allRows.Where(r => (!ShowAttentionOnly || r.HasIssue) && (needle.Length == 0
                                          || r.Title.Contains(needle, StringComparison.OrdinalIgnoreCase)
                                          || r.FretsText.Contains(needle, StringComparison.Ordinal))).ToList();
        if (visible.Count != Rows.Count || !visible.SequenceEqual(Rows.OfType<ShapePanelRowViewModel>())) Rows = visible;
        HasRows = visible.Count > 0;
        SetCurrent(_currentRow is { } current && visible.Contains(current) ? current : visible.FirstOrDefault(), force: true);
    }

    private void SetCurrent(ShapePanelRowViewModel? row, bool force = false)
    {
        row = row is not null && Rows.Contains(row) ? row : Rows.OfType<ShapePanelRowViewModel>().FirstOrDefault();
        if (ReferenceEquals(row, _currentRow) && !force) return;
        if (!ReferenceEquals(row, _currentRow))
        {
            _currentRow?.IsCurrent = false;
            (_currentRow = row)?.IsCurrent = true;
            OnPropertyChanged(nameof(CurrentRow));
            HasCurrentRow = row is not null;
        }

        if (_usesList.Rebuild(row?.Template)) Uses = _usesList.Rows;
        PublishUses();
    }

    private void OnSelectionChanged()
    {
        UpdateSplitAvailability();
        if (_usesList.FollowSelection(_shown)) PublishUses();
    }

    private void PublishUses() => (HasUses, UsesSummary, CanSplitUses) =
        _usesList.Publish(_currentRow?.CanSplitSelection == true, _currentRow?.Entry?.Uses.All ?? 0);

    partial void OnCanSplitUsesChanged(bool value) => _splitUses.NotifyCanExecuteChanged();

    private void ScheduleRefresh() => _refresh.Request();

    private void OnStateChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(ProjectState.ActiveProject))
        {
            _allRows = [];
            _issues = new ChordShapeIssues([]);
            _drag.End();
            CancelPending();
            _dragOver = null;
            Publish(0, 0, "ChordShapes_Empty_NoArrangement");
        }

        if (e.PropertyName is nameof(ProjectState.ActiveArrangement) or nameof(ProjectState.ActiveLevel)
            or nameof(ProjectState.ActiveProject))
        {
            ScheduleRefresh();
        }
    }

    /// <summary>What a card action did, or why not — on the app's status rail, not in the pane.</summary>
    private void Say(string text, StatusSeverity severity = StatusSeverity.Info) => _state.SetMessage(text, severity);
}
