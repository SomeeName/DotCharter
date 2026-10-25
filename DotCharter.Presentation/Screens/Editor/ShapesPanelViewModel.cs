using System.ComponentModel;
using System.Windows.Input;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using DotCharter.Core.Domain.Guitar;
using DotCharter.Localization;
using DotCharter.Presentation.Contracts.Editor;
using DotCharter.Presentation.Dialogs;
using DotCharter.Services.Audio.Coordination;
using DotCharter.Services.Editing;
using DotCharter.Services.Editing.ChordShapes;
using DotCharter.Services.State;
using DotCharter.Tone.Diagnostics;

namespace DotCharter.Presentation.Screens.Editor;

/// <summary>
///     The Shapes panel over the active arrangement. It stays current while hidden: every history
///     step, arrangement or level switch posts ONE coalesced refresh, and cards are reused by
///     template so an edit updates text in place and the list is only replaced when the set of
///     templates or the search changes.
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
public sealed partial class ShapesPanelViewModel : ObservableObject, IShapePanelScreen, IShapeRowOwner, IShapesBulkHost, IShapesDragHost
{
    private readonly IChartEditorSurface _editor;
    private readonly IHistoryService _history;
    private readonly ProjectState _state;
    private readonly ILocalizationService _localization;
    private readonly IUiDispatcher _dispatcher;
    private readonly IChordShapePlacementService _placement;
    private readonly IEditorTransport _transport;
    private readonly IDialogs _dialogs;
    private readonly RelayCommand _cleanUp;
    private readonly ShapesBulkVerbs _bulk;
    private readonly ShapesDragVerbs _drag;

    private List<ShapePanelRowViewModel> _allRows = [];
    private ChordShapeIssues _issues = new([]);
    private ShapePanelRowViewModel? _mergeSource;
    private ShapePanelRowViewModel? _dragOver;
    private PendingVerb? _pending;
    private bool _refreshPending;

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
        _dispatcher = dispatcher;
        _placement = parts.Placement;
        _transport = parts.Transport;
        _dialogs = parts.Dialogs;
        _bulk = new ShapesBulkVerbs(editor, localization, parts.Dialogs, this);
        _drag = new ShapesDragVerbs(this);
        _cleanUp = new RelayCommand(CleanUp, () => HasCleanUp);
        CleanUpCommand = _cleanUp;
        RepeatPreviousChordCommand = new RelayCommand(RepeatPreviousChord);
        ConfirmPendingCommand = new RelayCommand(ConfirmPending);
        CancelPendingCommand = new RelayCommand(CancelPending);

        _history.HistoryChanged += ScheduleRefresh;
        _state.PropertyChanged += OnStateChanged;
        _editor.Selection.SelectionChanged += (_, _) => UpdateSplitAvailability();
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

    /// <summary>Every card, unfiltered: the search's input and the test hook.</summary>
    internal IReadOnlyList<ShapePanelRowViewModel> AllRows => _allRows;

    /// <summary>Re-reads the arrangement now. Normally reached through the coalesced post.</summary>
    public void Refresh()
    {
        _refreshPending = false;
        var arrangement = _editor.ActiveArrangement;
        if (arrangement is null)
        {
            _allRows = [];
            _issues = new ChordShapeIssues([]);
            Publish(0, 0, "ChordShapes_Empty_NoArrangement");
            return;
        }

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
            row.Update(entry, issue, keeper, arrangement.StringCount);
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
    }

    private void ScheduleRefresh()
    {
        if (_refreshPending) return;
        _refreshPending = true;
        _dispatcher.Post(() =>
        {
            if (_refreshPending) Refresh();
        });
    }

    private void OnStateChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName is nameof(ProjectState.ActiveArrangement) or nameof(ProjectState.ActiveLevel)
            or nameof(ProjectState.ActiveProject))
        {
            ScheduleRefresh();
        }
    }

    /// <summary>What a card action did, or why not — on the app's status rail, not in the pane.</summary>
    private void Say(string text, StatusSeverity severity = StatusSeverity.Info) => _state.SetMessage(text, severity);
}
