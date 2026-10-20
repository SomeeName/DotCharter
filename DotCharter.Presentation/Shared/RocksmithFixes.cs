using DotCharter.Core.Domain.Project;
using DotCharter.Localization;
using DotCharter.Presentation.Contracts.Dialogs;
using DotCharter.Presentation.Dialogs;
using DotCharter.Services.Editing;
using DotCharter.Services.Editing.Maintenance;
using DotCharter.Services.Editing.Tones;
using DotCharter.Services.Export.Validation;
using DotCharter.Tone.Infrastructure.Configuration;
using DotCharter.Tone.Logging;
using DotCharter.Services.State;

namespace DotCharter.Presentation.Shared;

/// <summary>
/// The Fix beside an export finding's Go to, for the Rocksmith export dialog, the Chart
///     quality report and the Inspector's export-readiness view alike. A chart fix is ONE undo
///     step (<see cref="IChartMaintenanceService" />); a tone finding
///     opens the Tone designer on the finding's arrangement — except an undefined key that
/// <see cref="ToneKeyRepair" /> can repair without doubt, which is renamed in place.
/// </summary>
public sealed class RocksmithFixes(
    IChartMaintenanceService maintenance,
    IChartEditorSurface editor,
    ProjectState state,
    IDialogs dialogs,
    ILocalizationService localization,
    IAppLogger logger)
{
    /// <summary>A verb exists for the finding and its arrangement can be named unambiguously.</summary>
    public bool CanFix(ExportValidationFinding finding) =>
        RepairFor(finding) != null ||
        (finding.Fix != ExportFixKind.None && ArrangementNamed(state.ActiveProject, finding.ArrangementName) != null);

    /// <summary>What Fix will do, for its tooltip; empty when there is no fix.</summary>
    public string Describe(ExportValidationFinding finding)
    {
        if (RepairFor(finding) is { } repair) return localization.Format("Export_FixTip_RenameTone", repair.From, repair.To);
        return finding.Fix switch
        {
            ExportFixKind.AutoFix => localization.Format("Export_FixTip_AutoFix", finding.ArrangementName),
            ExportFixKind.FillFhp => localization.Format("Export_FixTip_FillFhp", finding.ArrangementName),
            ExportFixKind.GenerateStructure => localization.Format("Export_FixTip_Structure", finding.ArrangementName),
            ExportFixKind.OpenToneDesigner => localization.Format("Export_FixTip_Tones", finding.ArrangementName),
            _ => ""
        };
    }

    /// <summary>Runs the finding's fix; completes when a chart fix has landed or the Tone designer closed.</summary>
    public async Task FixAsync(ExportValidationFinding finding)
    {
        if (RepairFor(finding) != null)
        {
            maintenance.RepairUndefinedToneKey(finding.Detail);
            return;
        }

        var owner = ArrangementNamed(state.ActiveProject, finding.ArrangementName);
        if (owner == null || finding.Fix == ExportFixKind.None) return;
        if (finding.Fix != ExportFixKind.OpenToneDesigner)
        {
            maintenance.FixExportFinding(finding.Fix, owner);
            return;
        }

        if (!ReferenceEquals(editor.ActiveArrangement, owner)) editor.SetActiveArrangement(owner);
        try
        {
            await dialogs.ShowAsync<IToneDesignerScreen, bool>();
        }
        catch (InvalidOperationException ex)
        {
            logger.Error(AppLogCategory.Project, "The tone designer could not open.", ex);
        }
    }

    /// <summary>The rename that repairs an undefined-tone finding, when the project leaves no doubt.</summary>
    private ToneKeyRepairProposal? RepairFor(ExportValidationFinding finding) =>
        finding.MessageKey == ChartExportValidator.KeyToneNotDefined && state.ActiveProject is { } project
            ? ToneKeyRepair.ProposeFor(project, finding.Detail)
            : null;

    /// <summary>
    ///     The single arrangement carrying <paramref name="name" />, or null when none does or more
    ///     than one does: a finding names its arrangement by NAME, and acting on the first of two
    ///     "Lead"s could act on one the finding is not about.
    /// </summary>
    public static Arrangement? ArrangementNamed(ChartProject? project, string? name)
    {
        if (project == null || string.IsNullOrWhiteSpace(name)) return null;
        var wanted = name.Trim();
        Arrangement? found = null;
        foreach (var arrangement in project.Arrangements)
        {
            if (!string.Equals(arrangement.Name?.Trim(), wanted, StringComparison.OrdinalIgnoreCase)) continue;
            if (found != null) return null;
            found = arrangement;
        }

        return found;
    }
}
