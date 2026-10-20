using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using DotCharter.Services.Editing.Maintenance;
using DotCharter.Services.Editing.Quality;
using DotCharter.Tone.UI.ViewModels.Shell;

namespace DotCharter.UI.ViewModels.Editor.Inspector;

/// <summary>
/// The Issues (FEHLER) tab: the validation recompute and its severity
///     grouping, the refresh / auto-fix / jump commands, the easier-levels toggle and
///     the fix menu for the finding last jumped to. Exposed as
///     <c>InspectorViewModel.Issues</c>; the markup binds <c>Issues.*</c>.
///     <para>
///         The inspector DRIVES it: its 150 ms debounce after every history step or
///         project change calls <see cref="RecomputeIssues" />, and a switch to the
///         tab calls <see cref="EnsureIssueGroupsCurrent" />. It reaches back only
///         through <see cref="IInspectorIssuesHost" />. The CURRENT SELECTION's
///         field-level findings stay on the inspector: the Sound tab binds them.
///     </para>
/// </summary>
public sealed partial class InspectorIssuesViewModel : ViewModelBase
{
    private readonly ProjectState _projectState;
    private readonly ChartEditorViewModel _chartEditor;
    private readonly IArrangementValidationService _validationService;
    private readonly IChartMaintenanceService _chartMaintenance;
    private readonly ExportReadinessViewModel _exportReadiness;
    private readonly ILocalizationService _localization;
    private IInspectorIssuesHost? _host;
    private bool _issueGroupsStale;

    [ObservableProperty] private int _issueCount;

    /// <summary>
    ///     Severity of the WORST bucket, for the FEHLER header's dot.
    ///     <para>
    ///         It exists so the markup does not have to index
    ///         <see cref="IssueGroups" />. Binding the dot to
    ///         <c>IssueGroups[0].Severity</c> fails: that collection is
    ///         <c>Clear()</c>ed and refilled on every recompute — so in the
    ///         all-clear state (and in the window between the clear and the first
    ///         add) the indexer resolves against an EMPTY list and Avalonia logs
    ///         "Index was out of range" instead of pushing a value. The dot's
    ///         Background then stays unset, which is not the converter's grey
    ///         fallback: the converter is never reached at all.
    ///     </para>
    ///     <para>
    ///         Defaults to <see cref="ArrangementIssueSeverity.Warning" /> because
    ///         "no issues" must never render as the danger colour — and the dot is
    ///         hidden in that state anyway.
    ///     </para>
    /// </summary>
    [ObservableProperty] private ArrangementIssueSeverity _topIssueSeverity = ArrangementIssueSeverity.Warning;

    /// <summary>Also list the issues on easier difficulty levels than the hardest.</summary>
    [ObservableProperty] private bool _showEasierLevelIssues;

    /// <summary>Issues on easier levels other than the one on screen; the toggle hides or shows them.</summary>
    [ObservableProperty] private int _easierLevelIssueCount;

    public InspectorIssuesViewModel(
        ProjectState projectState,
        ChartEditorViewModel chartEditor,
        IArrangementValidationService validationService,
        IChartMaintenanceService chartMaintenance,
        ExportReadinessViewModel exportReadiness,
        IHistoryService historyService,
        ILocalizationService localization,
        Func<DotCharter.Presentation.Screens.Editor.IAiChatFindingsDoor?> aiChat)
    {
        _projectState = projectState;
        _chartEditor = chartEditor;
        _validationService = validationService;
        _chartMaintenance = chartMaintenance;
        _exportReadiness = exportReadiness;
        _localization = localization;
        // the fix menu for the finding last jumped to (E04 / I46).
        IssueFix = new IssueFixViewModel(historyService, localization);
        // Fix / Explain with AI for the same finding, through the AI chat.
        IssueAi = new IssueAiViewModel(projectState, localization, aiChat);
    }

    /// <summary>The issues listed — the hardest level's and the one on screen, plus the
    /// easier levels' when <see cref="ShowEasierLevelIssues" /> is on.</summary>
    public BulkObservableCollection<ArrangementIssue> Items { get; } = [];

    /// <summary>Issues bucketed by severity (Important first) for the FEHLER
    /// triage surface — each group renders a header + count badge + click-to-jump
    /// rows. Rebuilt from <see cref="Items" /> on every recompute.</summary>
    public BulkObservableCollection<InspectorIssueGroup> IssueGroups { get; } = [];

    /// <summary>The Issues tab's fixes for the finding last jumped to.</summary>
    public IssueFixViewModel IssueFix { get; }

    /// <summary>The Issues tab's Fix / Explain with AI for the finding last jumped to.</summary>
    public IssueAiViewModel IssueAi { get; }

    /// <summary>
    ///     Localized headline for the FEHLER tab ("14 Probleme gefunden" /
    ///     "Keine Probleme gefunden"). Replaces the old hard-coded English
    ///     "{0} issue(s)".
    ///     <para>
    ///         It is a COUNTED string, so it goes through
    ///         <c>LocalizationService.Plural</c> and not <c>Format</c>: a single
    ///         finding would read "1 Probleme gefunden", which is the same
    ///         defect the launch screen's "1 Tage Serie" was. Both forms are
    ///         pinned in both languages by <c>UI/PluralStringTests</c>.
    ///     </para>
    /// </summary>
    // With none, the header names the tab and the empty state below says "No issues detected":
    // both said it, one line apart.
    public string IssueSummary => IssueCount == 0
        ? _localization["Inspector_Tab_Issues"]
        : _localization.Plural(
            "Inspector_Issues_CountOne", "Inspector_Issues_Count", IssueCount);

    /// <summary>The Issues tab badge's tooltip: its number counts lint findings
    /// over every level.</summary>
    public string IssueBadgeTip => ProblemCountText.IssueBadgeTip(_localization, IssueCount);

    public bool HasEasierLevelIssues => EasierLevelIssueCount > 0;

    public string EasierLevelIssuesNote => _localization.Plural(
        "Dialog_QualityReport_EasierLevelsOne", "Dialog_QualityReport_EasierLevels", EasierLevelIssueCount);

    /// <summary>True while the groups are behind the issue list — read by the
    /// tests that pin the deferral.</summary>
    internal bool IssueGroupsAreStale => _issueGroupsStale;

    /// <summary>The inspector ties itself back here once, from its constructor.</summary>
    internal void AttachHost(IInspectorIssuesHost host) => _host = host;

    /// <summary>The inspector's Dispose: a disposed host is never called again.</summary>
    internal void DetachHost(IInspectorIssuesHost host)
    {
        if (ReferenceEquals(_host, host)) _host = null;
    }

    [RelayCommand]
    public void RefreshIssues() => RecomputeIssues();

    /// <summary>
    ///     One-click "fix the mechanical ones" from the Issues tab: runs the
    ///     safe structural auto-fix pipeline (one undoable snapshot, see
    ///     ChartMaintenanceService.AutoFixChart) and re-validates, so the list
    ///     that remains is the genuine chart-review work.
    /// </summary>
    [RelayCommand]
    public void AutoFixIssues()
    {
        _chartMaintenance.AutoFixChart();
        RecomputeIssues();
    }

    [RelayCommand]
    public void JumpToIssue(ArrangementIssue? issue)
    {
        if (issue == null) return;

        // EVERY rule sweeps EVERY level of the arrangement (they all open
        // `foreach (var level in arrangement.Levels)`), so on a chart with
        // dynamic difficulty most findings belong to a level that is not the one
        // on screen. Jumping seeked to the right instant and then selected an
        // element the active level does not contain: the highway showed nothing,
        // the Sound card described a sound the charter could not see, and any
        // edit made from it landed on a DIFFERENT level's element. Switch first,
        // then select — setting ActiveLevel does not touch the selection.
        var owner = LevelOwning(_projectState.ActiveArrangement, issue);
        if (owner != null && !ReferenceEquals(owner, _projectState.ActiveLevel))
            _projectState.ActiveLevel = owner;
        IssueFix.Offer(issue, _projectState.ActiveArrangement, owner);
        IssueAi.Offer(issue, _projectState.ActiveArrangement, owner, Items);

        if (issue.HasTime)
            _chartEditor.ExecuteSeek(issue.TimeMs!.Value);

        // This selection is the inspector talking to itself — it must not
        // switch the tab away from Issues mid-triage (the host's
        // SelectProgrammatically marks it so).
        if (_host != null) _host.SelectProgrammatically(SelectIssueElement);
        else SelectIssueElement();

        void SelectIssueElement()
        {
            _chartEditor.Selection.ClearSelections();
            if (issue.Note != null) _chartEditor.Selection.SelectedNotes.Add(issue.Note);
            else if (issue.Chord != null) _chartEditor.Selection.SelectedChords.Add(issue.Chord);
        }
    }

    /// <summary>
    ///     The level that actually holds an issue's back-referenced element, or
    ///     null when the issue is not bound to one (a whole-arrangement finding,
    ///     or an element that has since been deleted).
    ///     <para>
    ///         Reference identity, deliberately — not a collection
    ///         <c>Contains</c>. Two field-identical sounds at one position on two
    ///         difficulty levels are the ORDINARY case here, so if any of these
    ///         element types ever gains value equality the default comparer would
    ///         answer "the first level" for every issue and defeat the lookup.
    ///         (None of them overrides <c>Equals</c> today, so this costs
    ///         nothing and is not currently observable.)
    ///     </para>
    /// </summary>
    internal static ChartLevel? LevelOwning(Arrangement? arrangement, ArrangementIssue? issue)
    {
        if (arrangement == null || issue == null) return null;

        foreach (var level in arrangement.Levels)
        {
            if (issue.Note != null && HoldsInstance(level.Notes, issue.Note)) return level;
            if (issue.Chord != null && HoldsInstance(level.Chords, issue.Chord)) return level;
            if (issue.HandShape != null && HoldsInstance(level.HandShapes, issue.HandShape)) return level;
            if (issue.Anchor != null && HoldsInstance(level.Anchors, issue.Anchor)) return level;
        }

        return null;

        static bool HoldsInstance<T>(IList<T> items, T target) where T : class
        {
            for (var i = 0; i < items.Count; i++)
            {
                if (ReferenceEquals(items[i], target))
                    return true;
            }

            return false;
        }
    }

    /// <summary>One validation pass over the active arrangement — the inspector's
    /// debounced refresh, the toggle and the two commands all land here.</summary>
    internal void RecomputeIssues()
    {
        var arr = _projectState.ActiveArrangement;
        if (arr == null || _projectState.Beats.Count == 0)
        {
            Items.ResetWith([]);
            IssueCount = 0;
            IssueFix.Offer(null, null, null);
            IssueAi.Offer(null, null, null, []);
            _chartEditor.PublishHandShapeFindings(HandShapeFindings.None);
            RebuildIssueGroups();
            return;
        }

        // The project too: the tone BODIES a tone change needs live there, not on
        // the arrangement, and without it a chart whose tone changes all play
        // silently validates completely clean.
        var results = _validationService.Validate(
            arr, _projectState.Beats, _projectState.TotalAudioLengthMs, _projectState.ActiveProject);
        // on a chart with a difficulty ladder most issues repeat the hardest level's on its
        // thinned copies. Those are listed on request; the hardest level and the one on screen always.
        // The 2D timeline marks the bars these findings name — from the FULL
        // list, before the easier-level filter: the timeline draws whichever level
        // is on screen, and the set is matched by reference against its shapes.
        _chartEditor.PublishHandShapeFindings(HandShapeFindings.Collect(results));
        var easier = OnEasierLevels(arr, results, _projectState.ActiveLevel);
        EasierLevelIssueCount = easier.Count;
        if (!ShowEasierLevelIssues && easier.Count > 0) results = [.. results.Where(i => !easier.Contains(i))];
        // One Reset, not Clear + N Adds:
        // this runs on the 150 ms debounce after every edit and the list is
        // bound in nested non-virtualizing ItemsControls.
        Items.ResetWith(results);
        IssueCount = Items.Count;
        IssueFix.Refresh(results, arr);
        IssueAi.Refresh(results, arr);

        RebuildIssueGroups();

        // Keep the export section current once the user asked for it — this
        // shares the debounced history/project-change refresh, so results
        // follow edits without an extra timer. (No project re-assembly here;
        // the explicit check command does that.)
        _exportReadiness.RefreshIfChecked();
    }

    private void RebuildIssueGroups()
    {
        // Recomputed here — where the list is rebuilt — from the ISSUES rather
        // than bound through the groups' indexer (see TopIssueSeverity), which
        // is also what lets the groups themselves wait for the tab.
        TopIssueSeverity = IssueGrouping.TopSeverity(Items);
        OnPropertyChanged(nameof(IssueSummary));
        // The selection's findings and the Sound tab's work queue are derived
        // from the issue list on the host.
        _host?.OnIssuesRecomputed();

        // The severity groups are bound ONLY by the FEHLER tab's nested
        // ItemsControls. While that tab is not showing, rebuilding them is N
        // container teardowns nobody sees, per edit — so they go stale and are
        // rebuilt when the tab is opened (EnsureIssueGroupsCurrent). The badge,
        // the header dot, the queue and the selection findings are all
        // derived from the issue list directly and stay current. Without a host
        // (a direct test) there is no hidden tab to wait for.
        if (_host?.IsIssuesTabSelected ?? true) SyncIssueGroups();
        else _issueGroupsStale = true;
    }

    partial void OnShowEasierLevelIssuesChanged(bool value) => RecomputeIssues();

    partial void OnEasierLevelIssueCountChanged(int value)
    {
        OnPropertyChanged(nameof(HasEasierLevelIssues));
        OnPropertyChanged(nameof(EasierLevelIssuesNote));
    }

    partial void OnIssueCountChanged(int value)
    {
        _ = value;
        OnPropertyChanged(nameof(IssueSummary));
        OnPropertyChanged(nameof(IssueBadgeTip));
    }

    /// <summary>
    ///     The issues whose element sits on an easier level than the hardest and is not on
    ///     <paramref name="shown" />. One element → level map per pass, by reference (see
    ///     <see cref="LevelOwning" /> for why identity), so this is O(elements + issues).
    /// </summary>
    internal static HashSet<ArrangementIssue> OnEasierLevels(Arrangement arrangement,
        IReadOnlyList<ArrangementIssue> issues, ChartLevel? shown)
    {
        var easier = new HashSet<ArrangementIssue>(ReferenceEqualityComparer.Instance);
        if (arrangement.Levels.Count < 2 || issues.Count == 0) return easier;

        var owner = new Dictionary<object, ChartLevel>(ReferenceEqualityComparer.Instance);
        foreach (var level in arrangement.Levels)
        {
            foreach (var note in level.Notes) owner.TryAdd(note, level);
            foreach (var chord in level.Chords) owner.TryAdd(chord, level);
            foreach (var shape in level.HandShapes) owner.TryAdd(shape, level);
            foreach (var anchor in level.Anchors) owner.TryAdd(anchor, level);
        }

        foreach (var issue in issues)
        {
            var element = (object?)issue.Note ?? (object?)issue.Chord ?? (object?)issue.HandShape ?? issue.Anchor;
            if (element == null || !owner.TryGetValue(element, out var level)) continue;
            if (ReferenceEquals(level, shown) || !QualityLevelScope.IsEasierLevel(arrangement, level)) continue;
            easier.Add(issue);
        }

        return easier;
    }

    private void SyncIssueGroups()
    {
        IssueGroups.ResetWith(IssueGrouping.Build(Items));
        _issueGroupsStale = false;
    }

    /// <summary>Called when the FEHLER tab comes into view — the one moment a
    /// deferred group rebuild has to have happened.</summary>
    internal void EnsureIssueGroupsCurrent()
    {
        if (_issueGroupsStale) SyncIssueGroups();
    }
}
