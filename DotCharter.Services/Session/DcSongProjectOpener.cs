using DotCharter.Localization;
using DotCharter.Services.Package.DcSong;
using DotCharter.Services.State;
using DotCharter.Tone.Diagnostics;

namespace DotCharter.Services.Session;

/// <summary>
/// File ▸ Open (and a drop) of a <c>.dcsong</c>: asks before replacing the open
///     project, asks where the new project folder goes, unpacks the package into a NEW
///     folder there (<see cref="DcSongProjectUnpacker" />) and opens the written
///     <c>project.rscp</c> through the session's ordinary load. The status line says when
///     the package's audio is lossy (Opus): chart-from-audio and stem separation work best
///     from a lossless (FLAC) package. Built by <see cref="ProjectSessionService" />, which
///     routes the extension here.
/// </summary>
internal sealed class DcSongProjectOpener(
    DcSongProjectUnpacker unpacker,
    IFileStorageService storage,
    IConfigurationService config,
    ProjectState state,
    ILocalizationService localization,
    IAppLogger logger,
    IStatusBarService? statusBar,
    Func<Task<bool>> mayReplaceOpenProject,
    Func<string, Task> loadConfirmedProject)
{
    private const string TaskId = "dcsong-open";

    public async Task OpenAsync(string packagePath)
    {
        logger.Info(AppLogCategory.Project, $"Opening song package '{packagePath}' as a project.");
        // Opening REPLACES the session, so ask first — before the folder picker, and
        // once: the load below is told the user already agreed.
        if (!await mayReplaceOpenProject())
        {
            logger.Info(AppLogCategory.Project, $"Open '{packagePath}' cancelled — the user kept the unsaved project.");
            return;
        }

        var baseDir = await storage.OpenFolderPickerAsync(localization["Picker_SelectProjectFolder"],
            config.LoadAppConfig().LastProjectFolderDir);
        if (string.IsNullOrEmpty(baseDir))
        {
            logger.Info(AppLogCategory.Project, $"Song package open cancelled at the folder picker. Package='{packagePath}'.");
            return;
        }

        config.RememberPickedFolder(baseDir, (c, d) => c.LastProjectFolderDir = d);

        DcSongUnpackResult result;
        using var cancel = new CancellationTokenSource();
        statusBar?.BeginTask(TaskId, localization["Status_DcSong_Unpacking"], cancel: cancel.Cancel);
        try
        {
            result = await Task.Run(() => unpacker.UnpackAsync(packagePath, baseDir, cancel.Token), cancel.Token);
        }
        catch (OperationCanceledException)
        {
            logger.Info(AppLogCategory.Project, $"Song package open cancelled while unpacking. Package='{packagePath}'.");
            state.SetMessage(localization["Status_DcSong_OpenCanceled"], StatusSeverity.Warning);
            return;
        }
        catch (DcSongReadException ex)
        {
            logger.Warning(AppLogCategory.Project, $"Song package '{packagePath}' refused ({ex.Reason}): {ex.Message}");
            state.SetMessage(localization.Format("Status_DcSong_OpenFailed", localization[ex.ResourceKey]), StatusSeverity.Error);
            return;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            logger.Error(AppLogCategory.Project, $"Song package '{packagePath}' could not be unpacked into '{baseDir}'.", ex);
            state.SetMessage(localization.Format("Status_DcSong_OpenFailed", ex.Message), StatusSeverity.Error);
            return;
        }
        finally
        {
            statusBar?.CompleteTask(TaskId);
        }

        await loadConfirmedProject(result.ProjectPath);
        // The load reports its own failure; only a project that really opened gets ours.
        if (!string.Equals(state.CurrentProjectPath, result.ProjectPath, StringComparison.OrdinalIgnoreCase)) return;

        var folder = Path.GetFileName(result.ProjectDirectory);
        var message = localization.Format(result.AudioLossy ? "Status_DcSong_OpenedLossy" : "Status_DcSong_Opened", result.Title, folder);
        if (result.StemsSkipped.Count > 0) message += " " + localization["Status_DcSong_StemsSkipped"];
        state.SetMessage(message, result.AudioLossy || result.StemsSkipped.Count > 0 ? StatusSeverity.Warning : StatusSeverity.Success);
    }
}
