using System.ComponentModel;
using System.Windows.Input;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using DotCharter.Core.Domain.Guitar;
using DotCharter.Localization;
using DotCharter.Presentation.Contracts.Editor;
using DotCharter.Services.Audio.Coordination;
using DotCharter.Services.Editing;
using DotCharter.Services.Editing.ChordShapes;
using DotCharter.Services.State;
using DotCharter.Tone.Diagnostics;

namespace DotCharter.Presentation.Screens.Editor;

/// <summary>
///     The Chord shapes list over the active arrangement. It stays current while hidden: every
///     history step, arrangement or level switch posts ONE coalesced refresh, and rows are
///     reused by template so an edit updates text in place and the list is only replaced when
///     the set of templates or the filter changes.
///     <para>
/// It is the chord-shape palette too: a row's Place verb and the recent strip go
///         through <see cref="IChordShapePlacementService" />, one undo step each.
///     </para>
/// </summary>
public sealed partial class ChordShapesViewModel : ObservableObject, IChordShapesScreen, IChordShapeRowOwner
{
    private enum Filter
    {
        All,
        Duplicates,
        MissingFingering
    }

    private readonly IChartEditorSurface _editor;
    private readonly IHistoryService _history;
    private readonly ProjectState _state;
    private readonly ILocalizationService _localization;
    private readonly IUiDispatcher _dispatcher;
    private readonly IChordShapePlacementService _placement;
    private readonly IEditorTransport _transport;

    private List<ChordShapeRowViewModel> _allRows = [];
    private Filter _filter = Filter.All;
    private bool _refreshPending;

    [ObservableProperty] private string _summary = "";
    [ObservableProperty] private IReadOnlyList<IChordShapeRow> _rows = [];
    [ObservableProperty] private bool _hasRows;
    [ObservableProperty] private string _emptyText = "";
    [ObservableProperty] private string _allFilterLabel = "";
    [ObservableProperty] private string _duplicatesFilterLabel = "";
    [ObservableProperty] private string _missingFingeringFilterLabel = "";
    [ObservableProperty] private bool _isShowingAll = true;
    [ObservableProperty] private bool _isShowingDuplicates;
    [ObservableProperty] private bool _isShowingMissingFingering;
    [ObservableProperty] private IReadOnlyList<IChordShapeRow> _recentRows = [];
    [ObservableProperty] private bool _hasRecentRows;

    public ChordShapesViewModel(
        IChartEditorSurface editor,
        IHistoryService history,
        ProjectState state,
        ILocalizationService localization,
        IUiDispatcher dispatcher,
        IChordShapePlacementService placement,
        IEditorTransport transport)
    {
        _editor = editor;
        _history = history;
        _state = state;
        _localization = localization;
        _dispatcher = dispatcher;
        _placement = placement;
        _transport = transport;
        RepeatPreviousChordCommand = new RelayCommand(RepeatPreviousChord);
        ShowAllCommand = new RelayCommand(() => SetFilter(Filter.All));
        ShowDuplicatesCommand = new RelayCommand(() => SetFilter(Filter.Duplicates));
        ShowMissingFingeringCommand = new RelayCommand(() => SetFilter(Filter.MissingFingering));

        _history.HistoryChanged += ScheduleRefresh;
        _state.PropertyChanged += OnStateChanged;
        _editor.Selection.SelectionChanged += OnSelectionChanged;
        _placement.RecentShapesChanged += (_, _) => UpdateRecentRows();
        Refresh();
    }

    public ICommand ShowAllCommand { get; }

    public ICommand ShowDuplicatesCommand { get; }

    public ICommand ShowMissingFingeringCommand { get; }

    public ICommand RepeatPreviousChordCommand { get; }

    /// <summary>Every row, unfiltered — the test hook and the filter's input.</summary>
    internal IReadOnlyList<ChordShapeRowViewModel> AllRows => _allRows;

    /// <summary>Re-reads the arrangement now. Normally reached through the coalesced post.</summary>
    public void Refresh()
    {
        _refreshPending = false;
        var arrangement = _editor.ActiveArrangement;
        if (arrangement is null)
        {
            Publish([], []);
            EmptyText = _localization["ChordShapes_Empty_NoArrangement"];
            return;
        }

        var entries = ChordShapeCatalog.Build(arrangement, _state.ActiveLevel);
        var usesByTemplate = new Dictionary<ChordTemplate, int>(ReferenceEqualityComparer.Instance);
        foreach (var entry in entries) usesByTemplate[entry.Template] = entry.Uses.All;

        var previous = new Dictionary<ChordTemplate, ChordShapeRowViewModel>(ReferenceEqualityComparer.Instance);
        foreach (var row in _allRows) previous[row.Template] = row;
        var rows = new List<ChordShapeRowViewModel>(entries.Count);
        foreach (var entry in entries)
        {
            var current = previous.TryGetValue(entry.Template, out var kept)
                ? kept
                : new ChordShapeRowViewModel(entry.Template, this, _localization);
            current.Update(entry, arrangement.StringCount, usesByTemplate);
            rows.Add(current);
        }

        Publish(rows, entries);
        EmptyText = _localization[entries.Count == 0 ? "ChordShapes_Empty_NoTemplates" : "ChordShapes_Empty_NoMatch"];
        UpdateSplitAvailability();
        UpdateRecentRows();
    }

    /// <summary>The recent strip: the placement service's recent shapes, as the rows the list already has.</summary>
    private void UpdateRecentRows()
    {
        var recent = _editor.ActiveArrangement is { } arrangement ? _placement.RecentShapes(arrangement) : [];
        var byTemplate = new Dictionary<ChordTemplate, ChordShapeRowViewModel>(ReferenceEqualityComparer.Instance);
        foreach (var row in _allRows) byTemplate[row.Template] = row;
        var rows = recent.Select(t => byTemplate.GetValueOrDefault(t)).OfType<IChordShapeRow>().ToList();
        if (!rows.SequenceEqual(RecentRows)) RecentRows = rows;
        HasRecentRows = rows.Count > 0;
    }

    private void Publish(List<ChordShapeRowViewModel> rows, IReadOnlyList<ChordShapeEntry> entries)
    {
        if (!rows.SequenceEqual(_allRows)) _allRows = rows;

        var duplicates = entries.Count(e => e.IsDuplicate);
        var missing = entries.Count(e => e.FingerState == ChordFingerState.Missing);
        Summary = entries.Count == 0
            ? ""
            : _localization.Format("ChordShapes_Summary",
                _localization.Plural("ChordShapes_ShapeCountOne", "ChordShapes_ShapeCount", entries.Count),
                _localization.Plural("ChordShapes_DuplicateCountOne", "ChordShapes_DuplicateCount", duplicates),
                _localization.Plural("ChordShapes_MissingCountOne", "ChordShapes_MissingCount", missing));
        AllFilterLabel = _localization.Format("ChordShapes_Filter_All", entries.Count);
        DuplicatesFilterLabel = _localization.Format("ChordShapes_Filter_Duplicates", duplicates);
        MissingFingeringFilterLabel = _localization.Format("ChordShapes_Filter_Missing", missing);
        ApplyFilter();
    }

    private void SetFilter(Filter filter)
    {
        _filter = filter;
        IsShowingAll = filter == Filter.All;
        IsShowingDuplicates = filter == Filter.Duplicates;
        IsShowingMissingFingering = filter == Filter.MissingFingering;
        ApplyFilter();
    }

    private void ApplyFilter()
    {
        var visible = _allRows.Where(r => _filter switch
        {
            Filter.Duplicates => r.IsDuplicate,
            Filter.MissingFingering => r.IsFingeringMissing,
            _ => true
        }).ToList();
        if (!visible.SequenceEqual(Rows.OfType<ChordShapeRowViewModel>()) || visible.Count != Rows.Count)
            Rows = visible;
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

    private void OnSelectionChanged(object? sender, EventArgs e) => UpdateSplitAvailability();

    private void UpdateSplitAvailability()
    {
        var selection = _editor.Selection;
        var ids = new HashSet<int>();
        foreach (var chord in selection.SelectedChords) ids.Add(chord.TemplateId);
        foreach (var shape in selection.SelectedHandShapes) ids.Add(shape.TemplateId);
        foreach (var row in _allRows) row.CanSplitSelection = ids.Contains(row.Template.Id);
    }

    /// <summary>What a row action did, or why not — on the app's status rail, not in the pane.</summary>
    private void Say(string text, StatusSeverity severity = StatusSeverity.Info) => _state.SetMessage(text, severity);
}
