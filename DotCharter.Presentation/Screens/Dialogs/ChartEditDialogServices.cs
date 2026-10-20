using DotCharter.Localization;
using DotCharter.Services.Editing;
using DotCharter.Services.Infrastructure.Configuration;
using DotCharter.Tone.Infrastructure.Configuration;
using DotCharter.Tone.Logging;
using DotCharter.Services.State;

namespace DotCharter.Presentation.Screens.Dialogs;

/// <summary>
///     What every chart-editing dialog (beat, set BPM, fret-hand position)
///     reads and writes through: the editor surface, the undo history, the open
///     project, the config the spacing enforcer reads, the strings and the log.
///     One part so each of those view models stays inside seven dependencies.
/// </summary>
public sealed class ChartEditDialogServices(
    IChartEditorSurface editor,
    IHistoryService history,
    ProjectState state,
    IConfigurationService config,
    ILocalizationService localization,
    IAppLogger logger)
{
    public IChartEditorSurface Editor { get; } = editor;

    public IHistoryService History { get; } = history;

    public ProjectState State { get; } = state;

    public IConfigurationService Config { get; } = config;

    public ILocalizationService Localization { get; } = localization;

    public IAppLogger Logger { get; } = logger;

    /// <summary>Says so on the status line and returns true when the beat map is locked.</summary>
    public bool RefuseIfBeatMapLocked(string what)
    {
        if (State.ActiveProject?.IsBeatMapLocked != true) return false;
        Logger.Warning(AppLogCategory.Events, $"{what} blocked; tempo/beat map is locked.");
        State.SetMessage(Localization["Status_BeatMapLocked"]);
        return true;
    }
}
