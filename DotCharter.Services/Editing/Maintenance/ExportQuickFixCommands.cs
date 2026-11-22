using DotCharter.Localization;
using DotCharter.Services.Export.Rocksmith;
using DotCharter.Services.State;
using DotCharter.Services.Editing.Tones;

namespace DotCharter.Services.Editing.Maintenance;

/// <summary>
///     The Rocksmith export dialog's two one-click repairs: store the generated show lights, and give every
///     tone name an arrangement uses a default definition. Each is ONE undo node. A collaborator
///     <see cref="ChartMaintenanceService" /> owns and forwards to.
/// </summary>
internal sealed class ExportQuickFixCommands(
    ProjectState projectState,
    IChartEditorSurface chartEditor,
    IHistoryService historyService,
    ILocalizationService localization,
    IAppLogger logger)
{
    public int GenerateShowlights()
    {
        if (projectState.ActiveProject is not { } project || project.Showlights.Count > 0) return 0;
        var lights = RsDefaultShowlights.For(project, projectState.TotalAudioLengthMs);
        historyService.Do(new CompositeAction([new SetShowlightsAction(project, lights)]) { LabelKey = "History_GenerateShowlights" });
        chartEditor.RequestRedraw();
        projectState.SetMessage(localization["Status_ShowlightsGenerated"], StatusSeverity.Success);
        logger.Info(AppLogCategory.Editing, $"Export fix: stored the generated show lights ({lights.Count}) in the project.");
        return lights.Count;
    }

    public int CreateMissingToneDefinitions(Arrangement arrangement)
    {
        if (projectState.ActiveProject is not { } project) return 0;
        var made = DefaultToneDefinitions.For(project, arrangement);
        if (made.Count == 0)
        {
            projectState.SetMessage(localization.Format("Status_ToneDefinitionsNoChange", arrangement.Name), StatusSeverity.Warning);
            return 0;
        }

        historyService.Do(new CompositeAction(made.Select(t => new UpsertToneDefinitionAction(project, null, t)))
        {
            LabelKey = "History_CreateToneDefinitions"
        });
        chartEditor.RequestRedraw();
        projectState.SetMessage(localization.Plural("Status_ToneDefinitionsCreatedOne", "Status_ToneDefinitionsCreated", made.Count, arrangement.Name),
            StatusSeverity.Success);
        logger.Info(AppLogCategory.Editing, $"Export fix: created {made.Count} default tone definition(s) for '{arrangement.Name}'.");
        return made.Count;
    }
}
