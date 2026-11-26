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

    public int TrimMetadataSpaces()
    {
        if (projectState.ActiveProject is not { } project) return 0;
        var chart = project.Chart;
        var before = SongMetadataFields.From(chart);
        var after = before with
        {
            SongName = SongMetadataSpaces.Tidy(before.SongName),
            Artist = SongMetadataSpaces.Tidy(before.Artist),
            AlbumName = SongMetadataSpaces.Tidy(before.AlbumName)
        };
        var actions = new List<IChartAction> { new SetSongMetadataAction(chart, before, after) };
        actions.AddRange(project.Arrangements.Select(a => new SetArrangementMetadataTextAction(a,
            (SongMetadataSpaces.Tidy(a.ArtistName), SongMetadataSpaces.Tidy(a.ArtistNameSort), SongMetadataSpaces.Tidy(a.AlbumName)))));
        actions.RemoveAll(a => a is SetSongMetadataAction { HasEffect: false } or SetArrangementMetadataTextAction { HasEffect: false });
        if (actions.Count == 0) return 0;

        historyService.Do(new CompositeAction(actions) { LabelKey = "History_TrimMetadataSpaces" });
        chartEditor.RequestRedraw();
        projectState.SetMessage(localization["Status_MetadataSpacesTrimmed"], StatusSeverity.Success);
        logger.Info(AppLogCategory.Editing, "Export fix: trimmed the stray spaces in the song title, artist and album (chart and arrangement overrides).");
        return 1;
    }

    /// <summary>Sets the song's DLC key to <paramref name="key" /> (one undo node); 1 when it changed.</summary>
    public int AssignDlcKey(string key)
    {
        if (projectState.ActiveProject is not { } project || string.IsNullOrWhiteSpace(key)) return 0;
        var before = SongMetadataFields.From(project.Chart);
        var action = new SetSongMetadataAction(project.Chart, before, before with { DlcKey = key });
        if (!action.HasEffect) return 0;

        historyService.Do(new CompositeAction([action]) { LabelKey = "History_AssignDlcKey" });
        chartEditor.RequestRedraw();
        projectState.SetMessage(localization.Format("Status_DlcKeyAssigned", key), StatusSeverity.Success);
        logger.Info(AppLogCategory.Editing, $"Export fix: gave the project the DLC key '{key}' (a Library package already used the old one).");
        return 1;
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
