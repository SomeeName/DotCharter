using DotCharter.Localization;
using DotCharter.Presentation.Contracts.Dialogs;
using DotCharter.Presentation.Dialogs;
using DotCharter.Services.Infrastructure;
using DotCharter.Services.Infrastructure.Configuration;
using DotCharter.Tone.Diagnostics;
using DotCharter.Tone.Infrastructure.Configuration;
using DotCharter.Tone.Infrastructure.Storage;
using DotCharter.Tone.Logging;
using DotCharter.Services.Parsing.Midi;
using DotCharter.Services.State;

namespace DotCharter.Presentation.Screens.Dialogs;

/// <summary>
///     File ▸ Import ▸ MIDI: the no-project guard, the picker (or a dropped
///     file), the parse OFF the UI thread under a status-bar pill, then the
///     dialog on what was parsed.
/// </summary>
public sealed class MidiImportLauncher(
    IDialogs dialogs,
    IFileStorageService storage,
    IConfigurationService config,
    IStatusBarService statusBar,
    ProjectState state,
    IExportTargetWriter files,
    DialogBasics basics)
{
    private readonly ILocalizationService _localization = basics.Localization;
    private readonly IAppLogger _logger = basics.Logger;

    public const string ReadTaskId = "import.midi.read";

    public async Task<bool> PickAsync()
    {
        if (RefuseWithoutProject("")) return false;
        var appConfig = config.LoadAppConfig();
        var filePath = await storage.OpenFilePickerAsync(_localization["Picker_SelectMidiFile"],
            [new FilePickerFilter("MIDI", ["*.mid", "*.midi"]), FilePickerFilters.AllFiles],
            string.IsNullOrEmpty(appConfig.LastMidiDir) ? appConfig.LastRscpDir : appConfig.LastMidiDir);
        if (string.IsNullOrEmpty(filePath))
        {
            _logger.Info(AppLogCategory.Import, "MIDI import file picker canceled.");
            return false;
        }

        config.RememberDirectory(filePath, (c, d) => c.LastMidiDir = d);
        return await OpenAsync(filePath);
    }

    /// <summary>Reports every failure itself, so a caller has nothing to observe.</summary>
    public async Task<bool> OpenAsync(string filePath)
    {
        if (RefuseWithoutProject(filePath)) return false;
        MidiFileData file;
        statusBar.BeginTask(ReadTaskId, _localization.Format("Status_ImportingFile", Path.GetFileName(filePath)));
        try
        {
            file = await Task.Run(() => ParseWithinLimit(filePath));
        }
        catch (ImportFileTooLargeException tooLarge)
        {
            _logger.Warning(AppLogCategory.Import, $"MIDI import refused: {tooLarge.Message} File='{filePath}'.");
            state.SetMessage(
                _localization.Format("Status_ImportFileTooLarge", Path.GetFileName(filePath),
                    ImportSizeLimits.ToMegabytes(tooLarge.Length), ImportSizeLimits.ToMegabytes(tooLarge.Cap)),
                StatusSeverity.Error);
            return false;
        }
        catch (Exception ex)
        {
            _logger.Error(AppLogCategory.Import, $"MIDI parse failed. File='{filePath}'.", ex);
            state.SetMessage(_localization.Format("Status_MidiImportFailed", ex.Message), StatusSeverity.Error);
            return false;
        }
        finally
        {
            statusBar.CompleteTask(ReadTaskId);
        }

        _logger.Info(AppLogCategory.Import,
            $"MIDI file loaded. File='{filePath}', format={file.Format}, ppq={file.TicksPerQuarterNote}, " +
            $"tracks={file.Tracks.Count}, tempoEvents={file.TempoEvents.Count}.");
        return await dialogs.ShowAsync(new MidiImportRequest(Path.GetFileName(filePath), file));
    }

    /// <summary>The length is checked first, through the same seam as the read, so an
    /// oversized file is refused without a byte of it being loaded.</summary>
    private MidiFileData ParseWithinLimit(string filePath)
    {
        ImportSizeLimits.RequireWithin(files.FileLength(filePath), ImportSizeLimits.MidiFileBytes, "The MIDI file");
        return MidiFileParser.Parse(files.ReadAllBytes(filePath));
    }

    /// <summary>A draft is added to the open project, so with none there is nothing to import into.</summary>
    private bool RefuseWithoutProject(string filePath)
    {
        if (state.ActiveProject != null) return false;
        _logger.Warning(AppLogCategory.Import, $"MIDI import requested but no active project. File='{filePath}'.");
        state.SetMessage(_localization["Status_MidiImportNoProject"]);
        return true;
    }
}
