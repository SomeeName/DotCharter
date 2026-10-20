using DotCharter.Localization;
using DotCharter.Presentation.Contracts.Dialogs;
using DotCharter.Presentation.Dialogs;
using DotCharter.Services.Import.Eof;
using DotCharter.Services.Infrastructure.Configuration;
using DotCharter.Tone.Diagnostics;
using DotCharter.Tone.Infrastructure.Configuration;
using DotCharter.Tone.Infrastructure.Storage;
using DotCharter.Tone.Logging;
using DotCharter.Services.Parsing.Eof;
using DotCharter.Services.State;

namespace DotCharter.Presentation.Screens.Dialogs;

/// <summary>
///     File ▸ Import ▸ Editor on Fire, end to end: the picker (or a dropped
///     file), the track-list read OFF the UI thread under a status-bar pill —
///     the dialog is not on screen yet, so the pill is its busy state — then the
///     dialog, then the project creation once it closes.
/// </summary>
public sealed class EofImportLauncher(
    IDialogs dialogs,
    IFileStorageService storage,
    IConfigurationService config,
    IStatusBarService statusBar,
    ProjectState state,
    DialogBasics basics,
    EofProjectCreator creator)
{
    private readonly ILocalizationService _localization = basics.Localization;
    private readonly IAppLogger _logger = basics.Logger;

    public const string ReadTaskId = "import.eof.read";

    public async Task PickAsync()
    {
        var filePath = await storage.OpenFilePickerAsync(_localization["Picker_SelectEofFile"], null,
            config.LoadAppConfig().LastEofDir);
        if (string.IsNullOrEmpty(filePath))
        {
            _logger.Info(AppLogCategory.Import, "Editor on Fire import file picker canceled.");
            return;
        }

        config.RememberDirectory(filePath, (c, d) => c.LastEofDir = d);
        await OpenAsync(filePath);
    }

    /// <summary>Reports every failure itself, so a caller has nothing to observe.</summary>
    public async Task OpenAsync(string filePath)
    {
        _logger.Info(AppLogCategory.Import, $"Reading Editor on Fire track list from '{filePath}'.");
        List<EofTrackInfo> tracks;
        statusBar.BeginTask(ReadTaskId, _localization.Format("Status_ImportingFile", Path.GetFileName(filePath)));
        try
        {
            tracks = await Task.Run(() => new EofImporter().GetAvailableTracks(filePath));
        }
        catch (Exception ex)
        {
            _logger.Error(AppLogCategory.Import, $"Failed to read Editor on Fire tracks from '{filePath}'.", ex);
            state.SetMessage(_localization.Format("Status_EofImportFailed", ex.Message), StatusSeverity.Error);
            return;
        }
        finally
        {
            statusBar.CompleteTask(ReadTaskId);
        }

        if (await dialogs.ShowAsync(new EofImportRequest(filePath, tracks)) is { } selection)
            await creator.CreateAsync(selection);
    }
}
