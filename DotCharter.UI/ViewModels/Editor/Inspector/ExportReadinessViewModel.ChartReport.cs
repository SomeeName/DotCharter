using System.Diagnostics;
using System.Globalization;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using DotCharter.Presentation.Contracts.Dialogs;
using DotCharter.Presentation.Dialogs;
using DotCharter.Presentation.Screens.Dialogs;
using DotCharter.Services.Editing.Quality;
using DotCharter.Tone.Diagnostics;

namespace DotCharter.UI.ViewModels.Editor.Inspector;

/// <summary>One arrangement's line in the Issues tab's "Whole project" block: its place in the chart, name, estimated difficulty ("Difficulty 6.4") and what it holds ("2 errors · 1 warning", or "No problems").
/// Index -1 is the song-wide row: the findings no arrangement owns (the lyric checks, vocal export findings), listed only when there are some, so the rows add up to the totals.</summary>
public sealed record ProjectArrangementRow(int Index, string Name, string Difficulty, string Counts, bool IsClean, bool HasError,
    int FindingCount = 0, string Tip = "")
{
    public const int SongWideIndex = -1;

    public bool IsSongWide => Index == SongWideIndex;
}

/// <summary>
///     The parts of the former Chart quality report dialog that the Issues tab did not already
///     show: the WHOLE-PROJECT block (a verdict, the total counts, and one row per arrangement with its
///     difficulty and finding counts; a click makes that arrangement active), and the Editor on Fire
///     round trip (what an EOF export of this chart would approximate or drop). The dialog is gone;
///     Tools ▸ Chart quality report and the EOF export's "Show" open the Issues tab, which carries both.
///     <para>The block runs the rules over EVERY arrangement off the UI thread
///     (<see cref="ChartQualityReporter" /> over a <see cref="QualityReportSnapshot" />): the refresh only
///     posts the copy, the checks run on a worker, and a result for a chart that has since been rechecked is
///     dropped. The round trip is LAZY as it was: nothing is computed while its expander is closed.</para>
/// </summary>
public sealed partial class ExportReadinessViewModel
{
    private readonly QualityReportCopies _reportCopies = new();
    private ChartQualityReporter _reporter = null!;
    private IUiDispatcher _ui = null!;
    private IAppLogger _logger = null!;
    private int _reportGeneration;

    [ObservableProperty] private IReadOnlyList<ProjectArrangementRow> _projectArrangements = [];

    /// <summary>"Ready to ship", or what stops it ("2 things stop the export"); empty until the first check lands.</summary>
    [ObservableProperty] private string _projectVerdict = "";

    /// <summary>The whole project's totals ("1 error · 3 warnings", or "No problems").</summary>
    [ObservableProperty] private string _projectCounts = "";

    /// <summary>"Plus N on easier difficulty levels", empty when none are left out of the counts.</summary>
    [ObservableProperty] private string _projectEasierLevelsNote = "";

    [ObservableProperty] private bool _isProjectChecking;

    [ObservableProperty] private bool _projectIsReady;

    public bool HasProjectReport => ProjectVerdict.Length > 0;

    /// <summary>The project has something to say: a failed or blocked check, a finding in any arrangement, or an export finding.</summary>
    public bool ProjectNeedsAttention =>
        (HasProjectReport && (!ProjectIsReady || ProjectArrangements.Any(r => !r.IsClean))) || HasExportFindings;

    public bool HasProjectArrangements => ProjectArrangements.Count > 0;

    /// <summary>How many findings <see cref="ProjectCounts" /> counts; the rows' <see cref="ProjectArrangementRow.FindingCount" /> add up to it.</summary>
    public int ProjectFindingCount { get; private set; }

    public bool HasProjectEasierLevelsNote => ProjectEasierLevelsNote.Length > 0;

    /// <summary>The check in flight, or the last one; for tests.</summary>
    internal Task ProjectReportPending { get; private set; } = Task.CompletedTask;

    /// <summary>The checks, off the UI thread; a test replaces it with a worker it controls.</summary>
    internal Func<Func<QualityReportCheck>, Task<QualityReportCheck>> ProjectReportOffThread { get; set; } = Task.Run;

    /// <summary>Design D's project-wide findings with the Arrangement choice, Jump and Fix.</summary>
    public ProjectFindingsList ProjectFindings { get; private set; } = null!;

    /// <summary>The Editor on Fire round trip section; open, it runs off the UI thread.</summary>
    public IEofRoundTripSection EofRoundTrip { get; private set; } = null!;

    private EofRoundTripSectionViewModel _eofRoundTripModel = null!;

    /// <summary>The concrete section, for a test to hand its off-thread runner to.</summary>
    internal EofRoundTripSectionViewModel EofRoundTripModel => _eofRoundTripModel;

    private void InitializeChartReport(IDialogs dialogs, IAppLogger logger, ChartQualityReporter reporter, IUiDispatcher ui)
    {
        _reporter = reporter;
        _ui = ui;
        _logger = logger;
        ProjectFindings = new ProjectFindingsList(_localization, _projectState, () => _chartEditor.ActiveArrangement,
            arrangement => _chartEditor.ActiveArrangement = arrangement);
        _eofRoundTripModel = new EofRoundTripSectionViewModel(_localization, dialogs, logger, JumpToSubject,
            () => _projectState.ActiveProject is { } project ? ChartProjectCloner.Clone(project) : null);
        EofRoundTrip = _eofRoundTripModel;
        _eofRoundTripModel.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName != nameof(IEofRoundTripSection.IsExpanded)) return;
            _projectState.Document.EofRoundTripProject = _eofRoundTripModel.IsExpanded ? _projectState.ActiveProject : null;
        };
    }

    /// <summary>
    ///     Brings the report parts up to date for the chart now open: called when the Issues tab is
    ///     selected, on its Refresh, and after a project switch. The whole-project check is posted, not
    ///     run here; the section opens by itself when an Editor on Fire export of this chart asked to
    ///     show its losses.
    /// </summary>
    internal void RefreshChartReport()
    {
        StartProjectCheck();

        // An open section may describe the chart as it was before the edits since: it runs again.
        if (_eofRoundTripModel.IsExpanded) _eofRoundTripModel.Invalidate();
        else if (_projectState.Document.EofRoundTripProject is { } wanted && ReferenceEquals(wanted, _projectState.ActiveProject)) _eofRoundTripModel.IsExpanded = true;
    }

    /// <summary>Set by the Issues tab's rail: a Whole project row was chosen, so the list of its findings is what to show.</summary>
    internal Action? ProjectArrangementSelected { get; set; }

    private void ResetChartReport()
    {
        _reportGeneration++;
        ShowProjectReport(null, "");
        _eofRoundTripModel.IsExpanded = false;
        // The Review card may still be on screen (re-expanded at once): what it showed was the old project's round trip.
        _eofRoundTripModel.Invalidate();
    }

    /// <summary>Makes the arrangement of a "Whole project" row the active one, so the tab lists its findings. The
    /// song-wide row keeps the arrangement: the findings list carries the project's lyric findings beside any.</summary>
    [RelayCommand]
    private void SelectProjectArrangement(ProjectArrangementRow? row)
    {
        if (row == null || _projectState.ActiveProject is not { } project) return;
        if (row.IsSongWide)
        {
            ProjectArrangementSelected?.Invoke();
            return;
        }

        if (row.Index < 0 || row.Index >= project.Arrangements.Count) return;
        var target = project.Arrangements[row.Index];
        if (!ReferenceEquals(target, _chartEditor.ActiveArrangement)) _chartEditor.ActiveArrangement = target;
        ProjectArrangementSelected?.Invoke();
    }

    private void StartProjectCheck()
    {
        var generation = ++_reportGeneration;
        if (_projectState.ActiveProject == null)
        {
            ShowProjectReport(null, "");
            return;
        }

        IsProjectChecking = true;
        var done = new TaskCompletionSource();
        ProjectReportPending = done.Task;
        // The copy is taken after the click has painted, so the Refresh button answers at once.
        _ui.Post(() => _ = CopyAndCheckProjectAsync(generation, done));
    }

    private async Task CopyAndCheckProjectAsync(int generation, TaskCompletionSource done)
    {
        QualityReportSnapshot? snapshot = null;
        try
        {
            if (generation != _reportGeneration)
            {
                done.TrySetResult();
                return; // a newer check, or a project switch, before this one ran
            }

            if (_projectState.ActiveProject is not { } project)
            {
                ShowProjectReport(null, "");
                done.TrySetResult();
                return;
            }

            var watch = Stopwatch.StartNew();
            _documentService.AssembleProject(project, _transport.Bpm, _projectState.CurrentAudioFileName, _chartEditor.LyricPhrases);
            snapshot = QualityReportSnapshot.Take(project, null, _reportCopies);
            var copied = watch.Elapsed;
            var audioLengthMs = _projectState.TotalAudioLengthMs;
            var taken = snapshot;
            QualityReportCheck? check = null;
            var error = "";
            var elapsed = TimeSpan.Zero;
            try
            {
                check = await ProjectReportOffThread(() =>
                {
                    var worker = Stopwatch.StartNew();
                    var result = taken.Check(_reporter, audioLengthMs > 0 ? audioLengthMs : null);
                    elapsed = worker.Elapsed;
                    return result;
                }).ConfigureAwait(false);
            }
            catch (Exception ex) when (ex is not OutOfMemoryException)
            {
                error = ex.Message;
                _logger.Error(AppLogCategory.Editing, "The whole-project check failed.", ex);
            }

            _ui.Post(() => LandProjectReport(taken, check, error, generation, copied, elapsed, done));
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            snapshot?.Release();
            _logger.Error(AppLogCategory.Editing, "The whole-project check could not copy the chart.", ex);
            _ui.Post(() =>
            {
                if (generation == _reportGeneration) ShowProjectReport(null, ex.Message);
                done.TrySetResult();
            });
        }
    }

    /// <summary>The UI half: rebinds the report to the live chart and shows it, unless a newer check is due.</summary>
    private void LandProjectReport(QualityReportSnapshot snapshot, QualityReportCheck? check, string error, int generation,
        TimeSpan copied, TimeSpan elapsed, TaskCompletionSource done)
    {
        try
        {
            var report = check == null ? null : snapshot.Rebind(check);
            snapshot.Release();
            var stale = generation != _reportGeneration;
            _logger.Info(AppLogCategory.Performance, string.Create(CultureInfo.InvariantCulture,
                $"Whole project: copied the chart in {copied.TotalMilliseconds:0} ms on the UI thread, checked it off the UI thread in {elapsed.TotalMilliseconds:0} ms{(stale ? "; dropped, the chart was rechecked" : "")}."));
            if (stale) return;
            if (check != null && report == null)
            {
                // The chart's arrangements changed during the check: this result must not land.
                StartProjectCheck();
                return;
            }

            ShowProjectReport(report, error);
        }
        finally
        {
            done.TrySetResult();
        }
    }

    private void ShowProjectReport(ChartQualityReport? report, string error)
    {
        // The counts follow what the tab lists: a finding on an easier level that does not stop the
        // export is left out of them, and said once.
        var easier = report?.Findings.Count(QualityLevelScope.IsOnEasierLevelAndNotBlocking) ?? 0;
        var shown = report?.Findings.Where(f => !QualityLevelScope.IsOnEasierLevelAndNotBlocking(f)).ToList() ?? [];
        IsProjectChecking = false;
        ProjectIsReady = report?.IsReady ?? false;
        ProjectVerdict = error.Length > 0 ? _localization.Format("Dialog_QualityReport_CheckFailed", error)
            : report == null ? ""
            : report.IsReady ? _localization["Dialog_QualityReport_Ready"]
            : _localization.Plural("Dialog_QualityReport_BlockedOne", "Dialog_QualityReport_Blocked", report.Errors);
        ProjectCounts = report == null ? "" : CountsOf(shown);
        ProjectFindingCount = shown.Count;
        ProjectEasierLevelsNote = easier > 0
            ? _localization.Plural("Dialog_QualityReport_EasierLevelsOne", "Dialog_QualityReport_EasierLevels", easier)
            : "";
        ProjectArrangements = report == null ? [] : [.. report.Arrangements.Select((a, i) =>
        {
            var mine = shown.Where(f => ReferenceEquals(f.Arrangement, a.Arrangement)).ToList();
            return new ProjectArrangementRow(i, a.Arrangement.Name,
                _localization.Format("Dialog_QualityReport_Difficulty", a.Difficulty), CountsOf(mine), mine.Count == 0,
                mine.Any(f => f.Severity == QualitySeverity.Error), mine.Count,
                _localization["Inspector_Issues_WholeProjectRowTip"]);
        }), .. SongWideRow(shown)];
        ProjectFindings.Show(shown, report == null ? [] : [.. report.Arrangements.Select(a => a.Arrangement)]);
        OnPropertyChanged(nameof(HasProjectReport));
        OnPropertyChanged(nameof(HasProjectArrangements));
        OnPropertyChanged(nameof(HasProjectEasierLevelsNote));
    }

    /// <summary>The row for the findings no arrangement owns, or none when there are none.</summary>
    private IEnumerable<ProjectArrangementRow> SongWideRow(List<QualityFinding> shown)
    {
        var songWide = shown.Where(f => f.Arrangement == null).ToList();
        if (songWide.Count == 0) yield break;
        yield return new ProjectArrangementRow(ProjectArrangementRow.SongWideIndex,
            _localization["Inspector_Issues_SongWideRow"], "", CountsOf(songWide), false,
            songWide.Any(f => f.Severity == QualitySeverity.Error), songWide.Count,
            _localization["Inspector_Issues_SongWideRowTip"]);
    }

    private string CountsOf(IReadOnlyCollection<QualityFinding> findings)
    {
        var errors = findings.Count(f => f.Severity == QualitySeverity.Error);
        var important = findings.Count(f => f.Severity == QualitySeverity.Important);
        var warnings = findings.Count(f => f.Severity == QualitySeverity.Warning);
        if (errors + important + warnings == 0) return _localization["Dialog_QualityReport_Clean"];
        var parts = new List<string>(3);
        if (errors > 0) parts.Add(_localization.Plural("Dialog_QualityReport_ErrorsOne", "Dialog_QualityReport_Errors", errors));
        if (important > 0) parts.Add(_localization.Plural("Dialog_QualityReport_ImportantOne", "Dialog_QualityReport_Important", important));
        if (warnings > 0) parts.Add(_localization.Plural("Dialog_QualityReport_WarningsOne", "Dialog_QualityReport_Warnings", warnings));
        return string.Join(" · ", parts);
    }

    private void JumpToSubject(string subject, double? timeMs)
    {
        if (ArrangementNamed(_projectState.ActiveProject, subject) is { } owner && !ReferenceEquals(owner, _chartEditor.ActiveArrangement))
        {
            _chartEditor.ActiveArrangement = owner;
        }

        if (timeMs is { } ms) _chartEditor.ExecuteSeek(ms);
    }
}
