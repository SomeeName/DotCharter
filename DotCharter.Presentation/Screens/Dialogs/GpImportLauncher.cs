using DotCharter.Localization;
using DotCharter.Presentation.Contracts.Dialogs;
using DotCharter.Presentation.Dialogs;
using DotCharter.Services.Infrastructure.Configuration;
using DotCharter.Tone.Diagnostics;
using DotCharter.Tone.Infrastructure.Configuration;
using DotCharter.Tone.Infrastructure.Storage;
using DotCharter.Tone.Logging;
using DotCharter.Services.Parsing.GuitarPro;
using DotCharter.Services.State;

namespace DotCharter.Presentation.Screens.Dialogs;

/// <summary>
///     File ▸ Import ▸ Guitar Pro: the picker (or a dropped file), the track
///     list read OFF the UI thread under a status-bar pill — for .gp/.gpx that
///     is the whole score inflated and loaded — then the dialog on those tracks.
/// </summary>
public sealed class GpImportLauncher(
    IDialogs dialogs,
    IFileStorageService storage,
    IConfigurationService config,
    IStatusBarService statusBar,
    ProjectState state,
    IAppLogger logger,
    ILocalizationService localization)
{
    public const string ReadTaskId = "import.gp.read";

    private bool _reading;

    /// <summary>True once a score was imported.</summary>
    public async Task<bool> PickAsync()
    {
        var filePath = await storage.OpenFilePickerAsync(localization["Picker_SelectGpFile"],
            [FilePickerFilters.GuitarPro, FilePickerFilters.AllFiles], config.LoadAppConfig().LastGpDir);
        if (string.IsNullOrEmpty(filePath))
        {
            logger.Info(AppLogCategory.Import, "Guitar Pro import file picker canceled.");
            return false;
        }

        config.RememberDirectory(filePath, (c, d) => c.LastGpDir = d);
        return await OpenAsync(filePath);
    }

    /// <summary>Reports every failure itself — a read that fails leaves no dialog, so the status line says why.</summary>
    public async Task<bool> OpenAsync(string filePath)
    {
        if (_reading)
        {
            logger.Info(AppLogCategory.Import, $"Guitar Pro import ignored: a read is in flight. File='{filePath}'.");
            return false;
        }

        logger.Info(AppLogCategory.Import, $"Reading Guitar Pro track list from '{filePath}'.");
        List<GpTrackInfo> tracks;
        _reading = true;
        statusBar.BeginTask(ReadTaskId, localization.Format("Status_ImportingFile", Path.GetFileName(filePath)));
        try
        {
            tracks = await Task.Run(() => new GpImporter().GetAvailableTracks(filePath));
        }
        catch (Exception ex)
        {
            logger.Error(AppLogCategory.Import, $"Failed to read Guitar Pro tracks from '{filePath}'.", ex);
            state.SetMessage(localization.Format("Status_GpImportFailed", ex.Message), StatusSeverity.Error);
            return false;
        }
        finally
        {
            statusBar.CompleteTask(ReadTaskId);
            _reading = false;
        }

        return await dialogs.ShowAsync(new GpImportRequest(filePath, tracks));
    }
}
