using DotCharter.Localization;
using DotCharter.Presentation.Contracts.Dialogs;
using DotCharter.Presentation.Dialogs;
using DotCharter.Services.Infrastructure.Configuration;
using DotCharter.Tone.Infrastructure.Configuration;
using DotCharter.Tone.Infrastructure.Storage;
using DotCharter.Tone.Logging;
using DotCharter.Services.State;

namespace DotCharter.Presentation.Screens.Dialogs;

/// <summary>
///     File ▸ Import ▸ Rocksmith XML: the no-project guard, the picker (or a
///     dropped file), then the progress card that reads and merges it.
/// </summary>
public sealed class RsXmlImportLauncher(
    IDialogs dialogs,
    IFileStorageService storage,
    IConfigurationService config,
    ProjectState state,
    IAppLogger logger,
    ILocalizationService localization)
{
    public async Task PickAsync()
    {
        if (RefuseWithoutProject("")) return;
        var appConfig = config.LoadAppConfig();
        var filePath = await storage.OpenFilePickerAsync(localization["Picker_SelectRsXmlFile"],
            [new FilePickerFilter("Rocksmith XML", ["*.xml"]), FilePickerFilters.AllFiles],
            string.IsNullOrEmpty(appConfig.LastRsXmlDir) ? appConfig.LastEofDir : appConfig.LastRsXmlDir);
        if (string.IsNullOrEmpty(filePath))
        {
            logger.Info(AppLogCategory.Import, "RS XML import file picker canceled.");
            return;
        }

        config.RememberDirectory(filePath, (c, d) => c.LastRsXmlDir = d);
        await OpenAsync(filePath);
    }

    /// <summary>Merges into the open project; the card reports every outcome itself.</summary>
    public async Task OpenAsync(string filePath)
    {
        if (RefuseWithoutProject(filePath)) return;
        await dialogs.ShowAsync(new RsXmlImportRequest(filePath));
    }

    private bool RefuseWithoutProject(string filePath)
    {
        if (state.ActiveProject != null) return false;
        logger.Warning(AppLogCategory.Import, $"RS XML import requested but no active project. File='{filePath}'.");
        state.SetMessage(localization["Status_RsXmlImportNoProject"]);
        return true;
    }
}
