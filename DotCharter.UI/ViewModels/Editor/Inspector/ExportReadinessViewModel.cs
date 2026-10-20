using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using DotCharter.Presentation.Dialogs;
using DotCharter.Presentation.Shared;
using DotCharter.Services.Editing.Maintenance;
using DotCharter.Services.Export.Validation;
using DotCharter.Tone.UI.ViewModels.Shell;
using DotCharter.UI.ViewModels.Dialogs;

namespace DotCharter.UI.ViewModels.Editor.Inspector;

/// <summary>
///     Pre-export readiness on the FEHLER tab (<see cref="ChartExportValidator" />,
///     0.9.0): the explicit whole-project check, its findings, the verdict
///     sentence and the jump to a finding. Exposed as
///     <c>InspectorViewModel.ExportReadiness</c>.
///     <para>
///         Two ties back: the Issues tab (<see cref="InspectorIssuesViewModel" />)
///         calls <see cref="RefreshIfChecked" /> from its debounced recompute, so a
///         result that has been asked for follows edits without a timer of its
///         own, and the inspector calls <see cref="Reset" /> on a project switch, which invalidates the
///         old result instead of silently re-checking the new project.
///     </para>
/// </summary>
public sealed partial class ExportReadinessViewModel : ViewModelBase
{
    private readonly ProjectState _projectState;
    private readonly IProjectDocumentService _documentService;
    private readonly TransportViewModel _transport;
    private readonly ChartEditorViewModel _chartEditor;
    private readonly ILocalizationService _localization;
    private readonly ChartExportValidator _exportValidator = new();
    private readonly RocksmithFixes _fixes;

    [ObservableProperty] private bool _hasExportCheckRun;

    public ExportReadinessViewModel(
        ProjectState projectState,
        IProjectDocumentService documentService,
        TransportViewModel transport,
        ChartEditorViewModel chartEditor,
        ILocalizationService localization,
        IChartMaintenanceService maintenance,
        IDialogs dialogs,
        IAppLogger logger)
    {
        _projectState = projectState;
        _documentService = documentService;
        _transport = transport;
        _chartEditor = chartEditor;
        _localization = localization;
        _fixes = new RocksmithFixes(maintenance, chartEditor, projectState, dialogs, localization, logger);
    }


    /// <summary>Findings of the pre-export PSARC validation, shown in the
    /// FEHLER tab next to the per-arrangement editing issues.</summary>
    public ObservableCollection<ExportValidationItem> ExportFindings { get; } = [];

    public bool HasExportFindings => ExportFindings.Count > 0;

    public string ExportReadinessSummary
    {
        get
        {
            if (!HasExportCheckRun)
            {
                return _localization["Inspector_Export_NotChecked"];
            }

            var errors = ExportFindings.Count(f => f.IsError);
            var warnings = ExportFindings.Count - errors;
            return ExportFindings.Count > 0
                ? _localization.Format("Dialog_Export_ValidationSummary", errors, warnings)
                : _localization["Dialog_Export_ValidationOk"];
        }
    }

    /// <summary>Explicit "check export readiness": assembles the project the
    /// same way a save/export does (current lyrics/BPM/audio metadata) and
    /// runs the export validator over ALL arrangements. Once run, the result
    /// list stays live via the debounced issue refresh.</summary>
    [RelayCommand]
    public void CheckExportReadiness()
    {
        var project = _projectState.ActiveProject;
        if (project == null)
        {
            return;
        }

        _documentService.AssembleProject(project, _transport.Bpm,
            _projectState.CurrentAudioFileName, _chartEditor.LyricPhrases);
        HasExportCheckRun = true;
        RecomputeExportFindings();
    }

    /// <summary>
    ///     The single arrangement of <paramref name="project" /> carrying
    ///     <paramref name="name" />, or null when none does — or when MORE THAN
    ///     ONE does.
    ///     <para>
    ///         An export finding names its arrangement by NAME and nothing stops
    ///         a project holding two called "Lead" (a duplicated arrangement
    ///         keeps its name until it is renamed). Answering "the first match"
    ///         there would switch the charter to an arrangement the finding may
    ///         not be about, which is a NEW way to be wrong; declining leaves the
    ///         jump seeking without switching, which is what it always did.
    ///     </para>
    /// </summary>
    internal static Arrangement? ArrangementNamed(ChartProject? project, string? name) =>
        RocksmithFixes.ArrangementNamed(project, name);

    /// <summary>The Fix beside a finding: one undo step, then the result follows the edit.</summary>
    [RelayCommand]
    public async Task FixExportFinding(ExportValidationItem? item)
    {
        if (item?.Finding is not { } finding)
        {
            return;
        }

        await _fixes.FixAsync(finding);
        CheckExportReadiness();
    }

    [RelayCommand]
    public void JumpToExportFinding(ExportValidationItem? item)
    {
        if (item?.TimeMs is not { } timeMs)
        {
            return;
        }

        // SWITCH FIRST, THEN SEEK — the same order, for the same reason, as
        // JumpToIssue's level switch. CheckExportReadiness validates the whole
        // PROJECT, so this list spans every arrangement and each row prints the
        // one it belongs to. Seeking alone moved the playhead of whichever
        // arrangement happened to be open to another one's timestamp, where
        // there is generally nothing wrong: the charter was shown a clean bar
        // and told it was the problem.
        var owner = ArrangementNamed(_projectState.ActiveProject, item.ArrangementName);
        if (owner != null && !ReferenceEquals(owner, _chartEditor.ActiveArrangement))
        {
            _chartEditor.ActiveArrangement = owner;
        }

        _chartEditor.ExecuteSeek(timeMs);
    }

    private void RecomputeExportFindings()
    {
        ExportFindings.Clear();

        var project = _projectState.ActiveProject;
        if (project == null)
        {
            HasExportCheckRun = false;
        }
        else
        {
            var findings = _exportValidator.Validate(project);
            foreach (var finding in findings
                         .OrderByDescending(f => f.Severity)
                         .ThenBy(f => f.TimeMs ?? double.MaxValue))
            {
                ExportFindings.Add(new ExportValidationItem
                {
                    Severity = finding.Severity,
                    Location = ExportFindingFormatter.FormatLocation(finding),
                    Message = ExportFindingFormatter.FormatMessage(finding, _localization),
                    // Carried, not parsed back out of Location — that string is
                    // for display and is localized/formatted.
                    ArrangementName = finding.ArrangementName,
                    TimeMs = finding.TimeMs,
                    Finding = finding,
                    HasFix = _fixes.CanFix(finding),
                    FixDescription = _fixes.Describe(finding)
                });
            }
        }

        OnPropertyChanged(nameof(HasExportFindings));
        OnPropertyChanged(nameof(ExportReadinessSummary));
    }

    /// <summary>Re-validates a result the user has already asked for — called from
    /// the Issues tab's debounced recompute. (No project re-assembly here;
    /// the explicit check command does that.)</summary>
    internal void RefreshIfChecked()
    {
        if (HasExportCheckRun)
        {
            RecomputeExportFindings();
        }
    }

    /// <summary>Drops the result — called on a project switch, because readiness
    /// is per-project and only computed on request.</summary>
    internal void Reset()
    {
        HasExportCheckRun = false;
        ExportFindings.Clear();
        OnPropertyChanged(nameof(HasExportFindings));
        OnPropertyChanged(nameof(ExportReadinessSummary));
    }
}
