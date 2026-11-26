using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using System.Diagnostics;
using System.Globalization;
using DotCharter.Services.Editing.Maintenance;
using DotCharter.Services.Editing.Quality;
using DotCharter.Tone.Diagnostics;
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
public sealed partial class InspectorIssuesViewModel : ViewModelBase, IDisposable
{
    private readonly ProjectState _projectState;
    private readonly ChartEditorViewModel _chartEditor;
    private readonly IArrangementValidationService _validationService;
    private readonly IChartMaintenanceService _chartMaintenance;
    private readonly ExportReadinessViewModel _exportReadiness;
    private readonly ILocalizationService _localization;
    private IInspectorIssuesHost? _host;
    private bool _issueGroupsStale;
    private readonly IUiDispatcher _ui;
    private readonly IAppLogger _logger;
    private readonly QualityReportCopies _copies = new();
    private readonly IHistoryService _history;
    // The last pass's findings after the easier-level filter, in order, and which of them are ignored.
    private IReadOnlyList<ArrangementIssue> _listed = [];
    private HashSet<ArrangementIssue> _ignored = [];
    private int _generation;
    private bool _running;
    private bool _disposed;

    [ObservableProperty][NotifyPropertyChangedFor(nameof(HasIssues))] private int _issueCount;

    /// <summary>The Issues tab's count badge shows only when there is something to count.</summary>
    public bool HasIssues => IssueCount > 0;

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

    /// <summary>Also list the warnings the charter ignored, each with Stop ignoring.</summary>
    [ObservableProperty] private bool _showIgnoredIssues;

    /// <summary>Findings of the last pass the charter ignored: out of the count and, unless shown, the list.</summary>
    [ObservableProperty] private int _ignoredIssueCount;

    public InspectorIssuesViewModel(
        ProjectState projectState,
        ChartEditorViewModel chartEditor,
        IArrangementValidationService validationService,
        IChartMaintenanceService chartMaintenance,
        ExportReadinessViewModel exportReadiness,
        IHistoryService historyService,
        ILocalizationService localization,
        Func<DotCharter.Presentation.Screens.Editor.IAiChatFindingsDoor?> aiChat,
        IUiDispatcher ui,
        IAppLogger logger)
    {
        _ui = ui;
        _logger = logger;
        _projectState = projectState;
        _chartEditor = chartEditor;
        _validationService = validationService;
        _chartMaintenance = chartMaintenance;
        _exportReadiness = exportReadiness;
        _localization = localization;
        _history = historyService;
        WeakLanguageSubscription.Subscribe(localization, this, static vm => vm.OnLanguageChanged());
        // the fix menu for the finding last jumped to (E04 / I46).
        IssueFix = new IssueFixViewModel(historyService, localization);
        // Fix / Explain with AI for the same finding, through the AI chat.
        IssueAi = new IssueAiViewModel(projectState, localization, aiChat);
        AutoFixConfirm = new InspectorPendingAction().ClearOn(projectState, historyService);
        // design D's flat, virtualized finding rows follow the groups and the finding on the Fixes card
        FixIssueCommand = new RelayCommand<ArrangementIssue?>(FixIssue);
        Ignore = new IssueIgnoreCommands(
            new RelayCommand<ArrangementIssue?>(IgnoreIssue),
            new RelayCommand<ArrangementIssue?>(IgnoreAllLike),
            new RelayCommand<ArrangementIssue?>(StopIgnoring),
            new RelayCommand<ArrangementIssue?>(StopIgnoringAllLike));
        Rows = new InspectorIssueRows(localization, JumpToIssueCommand, FixIssueCommand, () => _projectState.ActiveProject, Ignore);
        IssueGroups.CollectionChanged += (_, _) => Rows.Rebuild(IssueGroups, _projectState.ActiveArrangement, _projectState.Beats);
        IssueAi.PropertyChanged += (_, _) =>
        {
            Rows.MarkShown(IssueAi.Issue);
            NotifyIssueExplanation();
        };
        IssueFix.PropertyChanged += (_, _) => NotifyIssueExplanation();
        exportReadiness.ProjectFindings.Items.CollectionChanged += (_, _) => NotifyIssueExplanation();
        exportReadiness.ProjectFindings.JumpToIssue = JumpToIssue;
        exportReadiness.ProjectFindings.FixIssue = FixIssue;
        exportReadiness.ProjectFindings.Seek = chartEditor.ExecuteSeek;
    }

    /// <summary>The pass now in flight: completes once its result has landed (or been dropped).</summary>
    internal Task Pending { get; private set; } = Task.CompletedTask;

    /// <summary>The validation step, off the UI thread; a test replaces it with one it controls.</summary>
    internal Func<Func<IssuePass>, Task<IssuePass>> OffThread { get; set; } = Task.Run;

    /// <summary>The issues listed — the hardest level's and the one on screen, plus the
    /// easier levels' when <see cref="ShowEasierLevelIssues" /> is on.</summary>
    public BulkObservableCollection<ArrangementIssue> Items { get; } = [];

    /// <summary>Issues bucketed by severity (Important first) for the FEHLER
    /// triage surface — each group renders a header + count badge + click-to-jump
    /// rows. Rebuilt from <see cref="Items" /> on every recompute.</summary>
    public BulkObservableCollection<InspectorIssueGroup> IssueGroups { get; } = [];

    /// <summary>The finding on the Fixes card stops the export (the last whole-project check says so), design D's status line.</summary>
    public bool IssueBlocksExport => IssueAi.Issue is { } issue && _exportReadiness.ProjectFindings.Blocks(issue);

    /// <summary>"Export blocker" or "Needs review".</summary>
    public string IssueStatus => _localization[IssueBlocksExport ? "Inspector_Issues_ExportBlocker" : "Inspector_Issues_NeedsReview"];

    /// <summary>The offline Explain's remedy line: the fix menu's default fix, or D's "listen first" when it has none.</summary>
    public string IssueSuggestion => IssueFix.HasFix
        ? _localization.Format("Inspector_Issues_Suggested", IssueFix.DefaultLabel)
        : _localization["Inspector_Issues_ListenFirst"];

    private void NotifyIssueExplanation()
    {
        OnPropertyChanged(nameof(IssueBlocksExport));
        OnPropertyChanged(nameof(IssueStatus));
        OnPropertyChanged(nameof(IssueSuggestion));
    }

    /// <summary>The finding rows the list shows, with the Difficulty filter.</summary>
    public InspectorIssueRows Rows { get; }

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
    public void RefreshIssues()
    {
        RecomputeIssues();
        _exportReadiness.RefreshChartReport();
    }

    /// <summary>
    ///     One-click "fix the mechanical ones" from the Issues tab: runs the
    ///     safe structural auto-fix pipeline (one undoable snapshot, see
    ///     ChartMaintenanceService.AutoFixChart) and re-validates, so the list
    ///     that remains is the genuine chart-review work.
    /// </summary>
    /// <remarks>Design D asks first ("Auto-fix 14 visible arrangement findings?", <see cref="AutoFixConfirm" />); Confirm runs it.</remarks>
    [RelayCommand]
    public void AutoFixIssues() => AutoFixConfirm.Ask(
        _localization.Plural("Inspector_Issues_AutoFixAskOne", "Inspector_Issues_AutoFixAsk", IssueCount), RunAutoFix);

    /// <summary>Each row's Fix (<see cref="FixIssue" />), handed to <see cref="Rows" />.</summary>
    public IRelayCommand<ArrangementIssue?> FixIssueCommand { get; }

    /// <summary>The Auto-fix question under the findings card's buttons.</summary>
    public InspectorPendingAction AutoFixConfirm { get; }

    private void RunAutoFix()
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
        IssueFix.Offer(issue, _projectState.ActiveArrangement, owner, _projectState.Beats, _projectState.ActiveProject);
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
            // An anchor finding names its shape only as context: selecting it would let Delete remove it.
            else if (issue is { HandShape: not null, Anchor: null }) _chartEditor.Selection.SelectedHandShapes.Add(issue.HandShape);
            else SelectLyric(issue.Lyric);
        }
    }

    /// <summary>
    ///     A finding row's Fix (inspector design D): jumps to the finding — so the Fixes card shows it — and applies
    ///     its default fix, one undo step, as the fix menu's button does. A finding whose fix a guard blocks stays
    ///     offered with its reason.
    /// </summary>
    public void FixIssue(ArrangementIssue? issue)
    {
        if (issue == null) return;
        JumpToIssue(issue);
        if (IssueFix.ApplyDefaultCommand.CanExecute(null)) IssueFix.ApplyDefaultCommand.Execute(null);
    }

    /// <summary>A lyric finding (D13): its vocal track becomes the active one, the lyrics row shows
    /// and the syllable is selected.</summary>
    private void SelectLyric(LyricPlace? place)
    {
        if (place == null || _projectState.ActiveProject is not { } project || place.Find(project) is not var (track, lyric))
            return;
        _chartEditor.ActiveVocalTrack = track;
        if (_chartEditor.ActiveRibbon is { } ribbon) ribbon.ShowLyrics = true;
        _chartEditor.Selection.SelectedLyric = lyric;
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

    /// <summary>
    ///     One validation pass over the active arrangement — the inspector's debounced refresh, the
    ///     toggle and the two commands all land here. The rules run OFF the UI thread over a copy of the
    ///     chart (<see cref="QualityReportSnapshot" />, the pattern the chart quality report uses); only
    ///     the copy, the empty-grid reset and the landing of the result happen on the UI thread.
    ///     <para>
    ///         One pass is in flight at a time: a request that arrives meanwhile only moves
    ///         <c>_generation</c>, and the pass that finishes sees it, drops its result and starts the
    ///         next one, so a burst of edits costs one trailing pass. A result is also dropped when the
    ///         arrangement's revision moved since the copy was taken, so a stale list never replaces
    ///         a newer one.
    ///     </para>
    /// </summary>
    internal void RecomputeIssues()
    {
        _generation++;
        var arr = _projectState.ActiveArrangement;
        var project = _projectState.ActiveProject;
        var arrangementIndex = project == null || arr == null ? -1 : project.Arrangements.IndexOf(arr);
        // Without an instrument arrangement, the project-wide lyric checks still run.
        if (project == null || _projectState.Beats.Count == 0
            || (arr == null ? project.VocalTracks.Count == 0 : arrangementIndex < 0))
        {
            ShowNoIssues();
            return;
        }

        if (_running) return;
        StartPass(project, arr, arrangementIndex);
    }

    private void ShowNoIssues()
    {
        _listed = [];
        _ignored = [];
        IgnoredIssueCount = 0;
        Items.ResetWith([]);
        IssueCount = 0;
        IssueFix.Offer(null, null, null);
        IssueAi.Offer(null, null, null, []);
        _chartEditor.PublishHandShapeFindings(HandShapeFindings.None);
        RebuildIssueGroups();
    }

    private void StartPass(ChartProject project, Arrangement? arr, int arrangementIndex)
    {
        _running = true;
        var generation = _generation;
        var revision = arr?.GetRevision() ?? 0;
        var levelIndex = _projectState.ActiveLevel == null || arr == null ? -1 : arr.Levels.IndexOf(_projectState.ActiveLevel);
        var beats = _projectState.Beats.ToList();
        var totalAudioLengthMs = _projectState.TotalAudioLengthMs;
        var done = new TaskCompletionSource();
        Pending = done.Task;
        QualityReportSnapshot? snapshot = null;
        try
        {
            var watch = Stopwatch.StartNew();
            snapshot = QualityReportSnapshot.Take(project, null, _copies);
            var copied = watch.Elapsed;
            _ = RunPassAsync(snapshot, copied, generation, revision, arr, arrangementIndex, levelIndex, beats,
                totalAudioLengthMs, done);
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            snapshot?.Release();
            _running = false;
            _logger.Error(AppLogCategory.Editing, "The Issues tab could not copy the chart to validate it.", ex);
            done.TrySetResult();
        }
    }

    private async Task RunPassAsync(QualityReportSnapshot snapshot, TimeSpan copied, int generation, long revision,
        Arrangement? arr, int arrangementIndex, int levelIndex, List<BeatMarker> beats, double totalAudioLengthMs,
        TaskCompletionSource done)
    {
        IssuePass? pass = null;
        try
        {
            pass = await OffThread(() => Validate(snapshot, arrangementIndex, levelIndex, beats, totalAudioLengthMs))
                .ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            _logger.Error(AppLogCategory.Editing, "The Issues tab's validation failed.", ex);
        }

        _ui.Post(() => Land(snapshot, pass, copied, generation, revision, arr, done));
    }

    /// <summary>The worker's half: the rules over the copy, and which of their issues sit on easier levels.</summary>
    private IssuePass Validate(QualityReportSnapshot snapshot, int arrangementIndex, int levelIndex,
        List<BeatMarker> beats, double totalAudioLengthMs)
    {
        var watch = Stopwatch.StartNew();
        HashSet<ArrangementIssue> easier = [];
        IReadOnlyList<ArrangementIssue> issues = [];
        var check = snapshot.CheckIssues(copy =>
        {
            var lyrics = LyricStyleRule.CheckAll(copy, beats);
            if (arrangementIndex < 0) return issues = lyrics;
            var arrangement = copy.Arrangements[arrangementIndex];
            // The project too: the tone BODIES a tone change needs live there, not on
            // the arrangement, and without it a chart whose tone changes all play
            // silently validates completely clean.
            issues = _validationService.Validate(arrangement, beats, totalAudioLengthMs, copy);
            // on a chart with a difficulty ladder most issues repeat the hardest level's on its
            // thinned copies. Those are listed on request; the hardest level and the one on screen always.
            easier = OnEasierLevels(arrangement, issues,
                levelIndex < 0 || levelIndex >= arrangement.Levels.Count ? null : arrangement.Levels[levelIndex]);
            // The project's lyric findings join in time order (OrderBy is stable).
            if (lyrics.Count > 0) issues = [.. issues.Concat(lyrics).OrderBy(i => i.TimeMs.HasValue).ThenBy(i => i.TimeMs)];
            return issues;
        });
        var onEasier = new bool[issues.Count];
        for (var i = 0; i < onEasier.Length; i++) onEasier[i] = easier.Contains(issues[i]);
        return new IssuePass(check, onEasier, watch.Elapsed);
    }

    /// <summary>The UI half: rebinds the issues to the live chart and shows them, unless a newer pass is due.</summary>
    private void Land(QualityReportSnapshot snapshot, IssuePass? pass, TimeSpan copied, int generation, long revision,
        Arrangement? arr, TaskCompletionSource done)
    {
        _running = false;
        try
        {
            var rebound = pass == null ? null : snapshot.RebindIssues(pass.Check);
            snapshot.Release();
            var moved = generation != _generation
                || !ReferenceEquals(_projectState.ActiveArrangement, arr)
                || (arr?.GetRevision() ?? 0) != revision;
            _logger.Info(AppLogCategory.Performance, string.Create(CultureInfo.InvariantCulture,
                $"Issues: copied the chart in {copied.TotalMilliseconds:0} ms on the UI thread, validated it off the UI thread in {pass?.Elapsed.TotalMilliseconds ?? 0:0} ms{(moved ? "; dropped, the chart moved on" : "")}."));
            if (moved || (pass != null && rebound == null))
            {
                // A newer request, an edit or a reshaped chart: this result must not land; validate again.
                RecomputeIssues();
                return;
            }

            if (pass != null && rebound != null) ShowIssues(arr, rebound, pass.OnEasierLevels);
        }
        finally
        {
            done.TrySetResult();
        }
    }

    private void ShowIssues(Arrangement? arr, IReadOnlyList<ArrangementIssue> results, bool[] onEasier)
    {
        // The 2D timeline marks the bars these findings name — from the FULL
        // list, before the easier-level filter: the timeline draws whichever level is on
        // screen, and the set is matched by reference against its shapes.
        _chartEditor.PublishHandShapeFindings(HandShapeFindings.Collect(results));
        var easierCount = onEasier.Count(e => e);
        EasierLevelIssueCount = easierCount;
        if (!ShowEasierLevelIssues && easierCount > 0) results = [.. results.Where((_, i) => !onEasier[i])];
        // The warnings the charter ignored leave the count and the list; errors never match.
        var (shown, ignored) = FindingIgnores.Partition(results, _projectState.ActiveProject);
        _listed = results;
        _ignored = new HashSet<ArrangementIssue>(ignored, ReferenceEqualityComparer.Instance);
        IgnoredIssueCount = ignored.Count;
        results = shown;
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

    /// <summary>Completes when the pass now in flight (and any it triggers) has landed; for tests.</summary>
    internal async Task SettleAsync()
    {
        while (true)
        {
            var pending = Pending;
            await pending.ConfigureAwait(false);
            if (ReferenceEquals(pending, Pending) && !_running) return;
        }
    }

    /// <summary>A validation over the copy: its issues, which of them sit on easier levels, and the time it took.</summary>
    internal sealed record IssuePass(ArrangementIssueCheck Check, bool[] OnEasierLevels, TimeSpan Elapsed);

    /// <summary>
    ///     Ends the language subscription's work: the localization service outlives the
    ///     container, and its weak entry still reaches this view model until it is
    ///     collected, so a released app's tab would otherwise rebuild its groups (and
    ///     move the lists of a view nobody shows) on the next app's language load.
    /// </summary>
    public void Dispose()
    {
        _disposed = true;
        AutoFixConfirm.Dispose();
    }

    private void OnLanguageChanged()
    {
        if (_disposed) return;
        OnPropertyChanged(nameof(ShowIgnoredLabel));
        RebuildIssueGroups();
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

    partial void OnShowIgnoredIssuesChanged(bool value) => RebuildIssueGroups();

    partial void OnIgnoredIssueCountChanged(int value)
    {
        OnPropertyChanged(nameof(HasIgnoredIssues));
        OnPropertyChanged(nameof(ShowIgnoredLabel));
    }

    /// <summary>The Show ignored toggle shows only when something is ignored.</summary>
    public bool HasIgnoredIssues => IgnoredIssueCount > 0;

    /// <summary>"Show ignored (75)".</summary>
    public string ShowIgnoredLabel => _localization.Format("Inspector_Issues_ShowIgnored", IgnoredIssueCount);

    /// <summary>Every row's Ignore / Ignore all / Stop ignoring.</summary>
    public IssueIgnoreCommands Ignore { get; }

    /// <summary>Ignores one warning: it leaves the count and the list until its subject changes. One undo step.</summary>
    public void IgnoreIssue(ArrangementIssue? issue)
    {
        if (issue != null) ChangeIgnores([issue], []);
    }

    /// <summary>Ignores every listed warning like <paramref name="issue" /> (same rule, same sentence). One undo step.</summary>
    public void IgnoreAllLike(ArrangementIssue? issue)
    {
        if (issue != null) ChangeIgnores([.. Items.Where(i => FindingIgnores.LikeKey(i) == FindingIgnores.LikeKey(issue))], []);
    }

    /// <summary>Lists an ignored warning again. One undo step.</summary>
    public void StopIgnoring(ArrangementIssue? issue)
    {
        if (issue != null) ChangeIgnores([], [issue]);
    }

    /// <summary>Lists every ignored warning like <paramref name="issue" /> again. One undo step.</summary>
    public void StopIgnoringAllLike(ArrangementIssue? issue)
    {
        if (issue != null) ChangeIgnores([], [.. _ignored.Where(i => FindingIgnores.LikeKey(i) == FindingIgnores.LikeKey(issue))]);
    }

    private void ChangeIgnores(IReadOnlyList<ArrangementIssue> ignore, IReadOnlyList<ArrangementIssue> restore)
    {
        if (_projectState.ActiveProject is not { } project) return;
        // An error is never ignored, whichever door asks.
        var add = ignore.Where(FindingIgnores.IsIgnorable).Select(i => FindingIgnores.EntryFor(i, project)).ToList();
        var remove = restore.Select(i => FindingIgnores.EntryFor(i, project)).ToList();
        if (_history.Do(new SetIgnoredFindingsAction(project.IgnoredFindings, add, remove))) RecomputeIssues();
    }

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
        Rows.SetIgnored(_ignored);
        IssueGroups.ResetWith(IssueGrouping.Build(ShowIgnoredIssues && _ignored.Count > 0 ? _listed : Items));
        _issueGroupsStale = false;
    }

    /// <summary>Called when the FEHLER tab comes into view — the one moment a
    /// deferred group rebuild has to have happened.</summary>
    internal void EnsureIssueGroupsCurrent()
    {
        if (_issueGroupsStale) SyncIssueGroups();
    }
}
