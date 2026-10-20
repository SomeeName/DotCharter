using DotCharter.Localization;
using DotCharter.Presentation.Contracts.Dialogs;
using DotCharter.Presentation.Dialogs;
using DotCharter.Services.Import.Youtube;
using DotCharter.Tone.Infrastructure.Configuration;
using DotCharter.Tone.Logging;
using DotCharter.Services.Session;
using DotCharter.Services.State;

namespace DotCharter.Presentation.Screens.Dialogs;

/// <summary>
/// File ▸ Audio from YouTube: the no-project guard, the dialog, then — once it has
///     closed — the same attach as File ▸ Attach audio on the downloaded mp3. A download the
///     attach does not keep (the user declines to replace the song audio) is deleted again.
/// </summary>
public sealed class YoutubeAudioLauncher(
    IDialogs dialogs,
    ProjectState state,
    IProjectSessionService session,
    ILocalizationService localization,
    IAppLogger logger)
{
    /// <summary>True when the download was attached to the open project.</summary>
    public async Task<bool> OpenAsync()
    {
        if (state.ActiveProject == null)
        {
            logger.Info(AppLogCategory.Import, "Audio from YouTube refused: no project is open.");
            state.SetMessage(localization["Status_YoutubeAudioNoProject"]);
            return false;
        }

        if (await dialogs.ShowAsync<IYoutubeAudioScreen, string?>() is not { } path)
        {
            state.SetMessage(localization["Dialog_YoutubeAudio_Canceled"], StatusSeverity.Warning);
            return false;
        }

        if (await session.AttachAudioAsync(path)) return true;
        logger.Info(AppLogCategory.Import, $"The YouTube download was not attached; deleting '{path}'.");
        YoutubeAudioDownload.Discard(path);
        return false;
    }
}
