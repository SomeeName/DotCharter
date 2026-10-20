using System.Diagnostics;
using System.Globalization;
using System.Windows.Input;
using CommunityToolkit.Mvvm.Input;
using DotCharter.Core.Domain.Project;
using DotCharter.Core.Utilities;
using DotCharter.Presentation.Contracts.Dialogs;
using DotCharter.Presentation.Dialogs;
using DotCharter.Presentation.Shared;
using DotCharter.Services.Audio.Coordination;
using DotCharter.Services.Editing.Linting;
using DotCharter.Services.Editing.Maintenance;
using DotCharter.Services.Editing.Quality;
using DotCharter.Services.Export.Validation;
using DotCharter.Services.Infrastructure.Documents;
using DotCharter.Tone.Infrastructure.Configuration;

namespace DotCharter.Presentation.Screens.Dialogs;

/// <summary>
/// Tools ▸ Chart quality report, one instance per open, over
///     <see cref="ChartQualityReporter" />. It shows a busy row at once; once that has painted
/// (<see cref="QualityReportChecks.AfterPaint" />) it assembles the project the way a save
///     does, copies it (<see cref="QualityReportSnapshot" />, into the spare copy when there is one)
/// and builds the report over the copy on <see cref="QualityReportChecks.OffThread" />.
///     The report lands rebound to the live chart, and one that lands after a newer check is
///     dropped. A Fix runs on the live chart through <see cref="RocksmithFixes" /> as one
///     undo step and the report runs again. The Editor on Fire round trip is not part of that run:
///     it is computed only while its section is open (<see cref="EofRoundTripSectionViewModel" />),
/// which it is on opening when the user exports this chart to Editor on Fire.
/// </summary>
public sealed class ChartQualityReportViewModel : DialogViewModel<bool>, IChartQualityReportScreen, IDisposable
{
    private readonly QualityReportChecks _checks;
    private readonly ChartEditDialogServices _edit;
    private readonly IProjectDocumentService _documents;
    private readonly IEditorTransport _transport;
    private readonly QualityAudioCheck _audio;
    private readonly RocksmithFixes _fixes;
    private readonly EofRoundTripSectionViewModel _eofRoundTrip;
    private readonly AsyncRelayCommand _verifyAudio;
    private CancellationTokenSource? _audioRun;
    private IReadOnlyDictionary<Arrangement, AudioChartLintOutcome>? _audioOutcomes;
    private readonly IDisposable _copiesOpen;
    private ChartQualityReport? _report;
    private bool _showEasierLevels;
    private bool _changed;
    private bool _focusEofRoundTrip;
    private int _generation;
    private string _error = "";

    public ChartQualityReportViewModel(QualityReportChecks checks, ChartEditDialogServices edit, IDialogs dialogs,
        IChartMaintenanceService maintenance, IProjectDocumentService documents, IEditorTransport transport,
        QualityAudioCheck audio)
    {
        _checks = checks;
        _edit = edit;
        _documents = documents;
        _transport = transport;
        _audio = audio;
        _copiesOpen = checks.Copies.Open();
        _fixes = new RocksmithFixes(maintenance, edit.Editor, edit.State, dialogs, edit.Localization, edit.Logger);
        _eofRoundTrip = new EofRoundTripSectionViewModel(edit.Localization, dialogs, edit.Logger, JumpTo,
            () => _edit.State.ActiveProject is { } project ? ChartProjectCloner.Clone(project) : null);
        _verifyAudio = new AsyncRelayCommand(VerifyAudioAsync, () => CanVerifyAudio);
        RecheckCommand = new RelayCommand(Recheck);
        CloseCommand = new RelayCommand(() => Close(_changed));
        AudioStatus = L(audio.HasAudio ? "Dialog_QualityReport_AudioNotChecked" : "Dialog_QualityReport_AudioNoFile");
        Recheck();
        var document = edit.State.Document;
        _eofRoundTrip.IsExpanded = document.EofRoundTripProject is { } wanted && ReferenceEquals(wanted, edit.State.ActiveProject);
        _eofRoundTrip.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName != nameof(IEofRoundTripSection.IsExpanded)) return;
            document.EofRoundTripProject = _eofRoundTrip.IsExpanded ? _edit.State.ActiveProject : null;
        };
    }

    public string Verdict { get; private set; } = "";
    public bool IsReady { get; private set; }
    public bool IsChecking { get; private set; }
    public bool HasVerdict { get; private set; }

    /// <summary>The check in flight, or the last one.</summary>
    internal Task Pending { get; private set; } = Task.CompletedTask;

    public string Counts { get; private set; } = "";
    public IReadOnlyList<IQualityArrangementSummary> Arrangements { get; private set; } = [];
    public IReadOnlyList<IQualityFindingGroup> Groups { get; private set; } = [];
    public bool HasFindings => Groups.Count > 0;

    public bool ShowEasierLevels
    {
        get => _showEasierLevels;
        set
        {
            if (_showEasierLevels == value) return;
            _showEasierLevels = value;
            Show(_report);
        }
    }

    public bool HasEasierLevelFindings { get; private set; }
    public string EasierLevelsNote { get; private set; } = "";
    public bool IsBusy { get; private set; }
    public string AudioStatus { get; private set; }
    public bool CanVerifyAudio => !IsBusy && _audio.HasAudio && _edit.State.ActiveProject != null;
    public ICommand VerifyAudioCommand => _verifyAudio;
    public ICommand RecheckCommand { get; }
    public IEofRoundTripSection EofRoundTrip => _eofRoundTrip;
    public bool FocusEofRoundTrip
    {
        get => _focusEofRoundTrip;
        set
        {
            _focusEofRoundTrip = value;
            if (value) _eofRoundTrip.IsExpanded = true;
        }
    }

    public ICommand CloseCommand { get; }

    public void Dispose()
    {
        _generation++;
        _copiesOpen.Dispose();
        _audioRun?.Cancel();
        _audioRun?.Dispose();
        _audioRun = null;
    }

    private string L(string key) => _edit.Localization[key];

    /// <summary>Shows the busy row now and copies the chart once it has painted; the checks run off the UI thread.</summary>
    private void Recheck()
    {
        var generation = ++_generation;
        _eofRoundTrip.Invalidate();
        if (_edit.State.ActiveProject == null)
        {
            Land(null);
            return;
        }

        IsChecking = true;
        OnPropertyChanged(nameof(IsChecking));
        var done = new TaskCompletionSource();
        Pending = done.Task;
        _checks.AfterPaint(() => _ = CopyAndCheckAsync(generation, done));
    }

    private async Task CopyAndCheckAsync(int generation, TaskCompletionSource done)
    {
        try
        {
            if (generation != _generation) return; // a newer check, or the dialog closed, before this one painted
            if (_edit.State.ActiveProject is not { } project)
            {
                Land(null);
                return;
            }

            var watch = Stopwatch.StartNew();
            _documents.AssembleProject(project, _transport.Bpm, _edit.State.CurrentAudioFileName, _edit.Editor.LyricPhrases);
            var snapshot = QualityReportSnapshot.Take(project, _audioOutcomes, _checks.Copies);
            await CheckAsync(snapshot, generation, _audio.AudioLengthMs, watch.Elapsed);
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            _edit.Logger.Error(AppLogCategory.Editing, "The chart quality report could not copy the chart.", ex);
            if (generation == _generation) Land(null, ex.Message);
        }
        finally
        {
            done.TrySetResult();
        }
    }

    private async Task CheckAsync(QualityReportSnapshot snapshot, int generation, double? audioLengthMs, TimeSpan copied)
    {
        var elapsed = TimeSpan.Zero;
        QualityReportCheck? check = null;
        var error = "";
        try
        {
            check = await _checks.OffThread(() =>
            {
                var watch = Stopwatch.StartNew();
                var result = snapshot.Check(_checks.Reporter, audioLengthMs);
                elapsed = watch.Elapsed;
                return result;
            });
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            error = ex.Message;
            _edit.Logger.Error(AppLogCategory.Editing, "The chart quality report's checks failed.", ex);
        }

        var stale = generation != _generation;
        _edit.Logger.Info(AppLogCategory.Performance, string.Create(CultureInfo.InvariantCulture,
            $"Quality report: {(snapshot.Reused ? "restored the spare copy" : "cloned the chart")} in {copied.TotalMilliseconds:0} ms on the UI thread after the busy row, checked it off the UI thread in {elapsed.TotalMilliseconds:0} ms{(stale ? "; dropped, the chart was rechecked" : "")}."));
        var report = stale || check == null ? null : snapshot.Rebind(check);
        snapshot.Release();
        if (stale) return;
        if (check != null && report == null)
        {
            _edit.Logger.Info(AppLogCategory.Editing, "Quality report: the chart's arrangements changed during the check; checking again.");
            Recheck();
            return;
        }

        Land(report, error);
    }

    private void Land(ChartQualityReport? report, string error = "")
    {
        IsChecking = false;
        HasVerdict = true;
        _error = error;
        Show(report);
    }

    private void Show(ChartQualityReport? report)
    {
        _report = report;
        var loc = _edit.Localization;
        IsReady = report?.IsReady ?? false;
        Verdict = _error.Length > 0 ? loc.Format("Dialog_QualityReport_CheckFailed", _error)
            : report == null ? L("Dialog_QualityReport_NoProject")
            : report.IsReady ? L("Dialog_QualityReport_Ready")
            : loc.Plural("Dialog_QualityReport_BlockedOne", "Dialog_QualityReport_Blocked", report.Errors);
        // The counts and every list below follow what is shown: a finding on an easier level that
        // does not stop the export is listed only on request.
        var easier = report?.Findings.Count(QualityLevelScope.IsOnEasierLevelAndNotBlocking) ?? 0;
        var shown = report?.Findings.Where(f => _showEasierLevels || !QualityLevelScope.IsOnEasierLevelAndNotBlocking(f)).ToList() ?? [];
        HasEasierLevelFindings = easier > 0;
        EasierLevelsNote = easier > 0 ? loc.Plural("Dialog_QualityReport_EasierLevelsOne", "Dialog_QualityReport_EasierLevels", easier) : "";
        Counts = report == null ? "" : CountsOf(shown);
        Arrangements = report?.Arrangements.Select(IQualityArrangementSummary (a) =>
        {
            var mine = shown.Where(f => ReferenceEquals(f.Arrangement, a.Arrangement)).ToList();
            return new QualityArrangementRow(a.Arrangement.Name, loc.Format("Dialog_QualityReport_Difficulty", a.Difficulty),
                CountsOf(mine), mine.Count == 0, mine.Any(f => f.Severity == QualitySeverity.Error));
        }).ToList() ?? [];
        Groups = report == null ? [] : BuildGroups(shown);
        OnPropertyChanged(string.Empty);
        _verifyAudio.NotifyCanExecuteChanged();
    }

    private string CountsText(int errors, int important, int warnings)
    {
        if (errors + important + warnings == 0) return L("Dialog_QualityReport_Clean");
        var loc = _edit.Localization;
        var parts = new List<string>(3);
        if (errors > 0) parts.Add(loc.Plural("Dialog_QualityReport_ErrorsOne", "Dialog_QualityReport_Errors", errors));
        if (important > 0) parts.Add(loc.Plural("Dialog_QualityReport_ImportantOne", "Dialog_QualityReport_Important", important));
        if (warnings > 0) parts.Add(loc.Plural("Dialog_QualityReport_WarningsOne", "Dialog_QualityReport_Warnings", warnings));
        return string.Join(" · ", parts);
    }

    private string CountsOf(IReadOnlyCollection<QualityFinding> findings) =>
        CountsText(findings.Count(f => f.Severity == QualitySeverity.Error),
            findings.Count(f => f.Severity == QualitySeverity.Important),
            findings.Count(f => f.Severity == QualitySeverity.Warning));

    private List<IQualityFindingGroup> BuildGroups(IReadOnlyList<QualityFinding> findings)
    {
        var groups = new List<IQualityFindingGroup>(3);
        Add(QualitySeverity.Error, "Dialog_QualityReport_GroupBlocking");
        Add(QualitySeverity.Important, "Dialog_QualityReport_GroupImportant");
        Add(QualitySeverity.Warning, "Dialog_QualityReport_GroupWarning");
        return groups;

        void Add(QualitySeverity severity, string titleKey)
        {
            var items = findings.Where(f => f.Severity == severity).Select(Row).ToList();
            if (items.Count == 0) return;
            groups.Add(new QualityFindingGroupRow(_edit.Localization.Format(titleKey, items.Count), severity == QualitySeverity.Error,
                severity == QualitySeverity.Important, items));
        }
    }

    private IQualityFinding Row(QualityFinding finding)
    {
        var export = finding.Export;
        var canFix = export != null && _fixes.CanFix(export);
        return new QualityFindingRow(Location(finding), QualityFindingText.Message(finding, _edit.Localization),
            Source(finding.Checks), new RelayCommand(() => Jump(finding), () => finding.CanJump), finding.CanJump,
            new QualityFixPart(canFix, canFix ? _fixes.Describe(export!) : "",
                new AsyncRelayCommand(() => FixAsync(export!), () => canFix)));
    }

    private string Location(QualityFinding finding)
    {
        var parts = new List<string>(3);
        if (finding.Arrangement != null) parts.Add(finding.Arrangement.Name);
        else if (finding.Export is { ArrangementName.Length: > 0 } export) parts.Add(export.ArrangementName);
        if (finding.Level != null && finding.Arrangement is { Levels.Count: > 1 })
            parts.Add(_edit.Localization.Format("Dialog_QualityReport_Level", finding.Level.DifficultyIndex));
        if (finding.TimeMs is { } ms) parts.Add(TimeSpan.FromMilliseconds(Math.Max(0, ms)).ToString(@"m\:ss\.fff"));
        return string.Join(" · ", parts);
    }

    private string Source(QualityChecks checks)
    {
        var parts = new List<string>(3);
        if (checks.HasFlag(QualityChecks.ChartRules)) parts.Add(L("Dialog_QualityReport_SourceRules"));
        if (checks.HasFlag(QualityChecks.ExportValidation)) parts.Add(L("Dialog_QualityReport_SourceExport"));
        if (checks.HasFlag(QualityChecks.AudioLint)) parts.Add(L("Dialog_QualityReport_SourceAudio"));
        return string.Join(" · ", parts);
    }

    /// <summary>Switch arrangement, then level, then seek and select — the Inspector's order, so the
    /// element on screen is the one the finding is about.</summary>
    private void Jump(QualityFinding finding)
    {
        if (IsChecking) return; // the row is the previous check's
        var editor = _edit.Editor;
        if (finding.Arrangement != null && !ReferenceEquals(finding.Arrangement, editor.ActiveArrangement))
            editor.SetActiveArrangement(finding.Arrangement);
        if (finding.Level != null && !ReferenceEquals(finding.Level, _edit.State.ActiveLevel))
            _edit.State.ActiveLevel = finding.Level;
        if (finding.TimeMs is { } ms) editor.ExecuteSeek(ms);

        var selection = editor.Selection;
        selection.ClearSelections();
        if (finding.Rule?.Note is { } note) selection.SelectedNotes.Add(note);
        else if (finding.Rule?.Chord is { } chord) selection.SelectedChords.Add(chord);
    }

    /// <summary>A round-trip place: its arrangement when one carries the name (the beat map and
    /// the vocals are no arrangement), then the time.</summary>
    private void JumpTo(string subject, double? timeMs)
    {
        var editor = _edit.Editor;
        if (RocksmithFixes.ArrangementNamed(_edit.State.ActiveProject, subject) is { } arrangement &&
            !ReferenceEquals(arrangement, editor.ActiveArrangement))
        {
            editor.SetActiveArrangement(arrangement);
        }

        if (timeMs is { } ms) editor.ExecuteSeek(ms);
    }

    private async Task FixAsync(ExportValidationFinding finding)
    {
        if (IsChecking) return; // the row is the previous check's
        await _fixes.FixAsync(finding);
        _changed = true;
        Recheck();
    }

    private async Task VerifyAudioAsync()
    {
        var project = _edit.State.ActiveProject;
        if (project == null || !_audio.HasAudio) return;
        _audioRun?.Cancel();
        _audioRun?.Dispose();
        var run = _audioRun = new CancellationTokenSource();
        SetBusy(true, L("Dialog_QualityReport_AudioRunning"));
        try
        {
            _audioOutcomes = await _audio.RunAsync(project, run.Token);
            var ran = _audioOutcomes.Values.Where(o => o.Ran).ToList();
            var failed = _audioOutcomes.Values.FirstOrDefault(o => !o.Ran);
            SetBusy(false, ran.Count == 0 && failed != null
                ? _edit.Localization.Format("Dialog_QualityReport_AudioFailed", failed.Error ?? "")
                : _edit.Localization.Format("Dialog_QualityReport_AudioDone", ran.Sum(o => o.SoundsVerified), ran.Sum(o => o.SoundsChecked)));
            Recheck();
        }
        catch (OperationCanceledException)
        {
            SetBusy(false, L("Dialog_QualityReport_AudioNotChecked"));
        }
    }

    private void SetBusy(bool busy, string status)
    {
        IsBusy = busy;
        AudioStatus = status;
        OnPropertyChanged(nameof(IsBusy));
        OnPropertyChanged(nameof(AudioStatus));
        OnPropertyChanged(nameof(CanVerifyAudio));
        _verifyAudio.NotifyCanExecuteChanged();
    }
}
