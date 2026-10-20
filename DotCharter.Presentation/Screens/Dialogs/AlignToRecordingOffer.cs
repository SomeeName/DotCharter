using DotCharter.Localization;
using DotCharter.Presentation.Contracts.Dialogs;
using DotCharter.Presentation.Dialogs;
using DotCharter.Tone.Audio.Abstractions;
using DotCharter.Tone.Infrastructure.Configuration;
using DotCharter.Tone.Logging;
using DotCharter.Services.State;

namespace DotCharter.Presentation.Screens.Dialogs;

/// <summary>
/// After a Guitar Pro or MIDI import: the chart sits on the tab's WRITTEN tempo,
///     so the status rail offers Align to recording as the next step, with the verb beside it.
///     Offered only when it can run: a project with a grid of two beats, an unlocked map and audio.
/// </summary>
public sealed class AlignToRecordingOffer(IDialogs dialogs, ProjectState state, IPlaybackTransport playback,
    ILocalizationService localization, IAppLogger logger)
{
    public bool Offer(string source)
    {
        if (state.ActiveProject is not { Beats.Count: >= 2, IsBeatMapLocked: false } || string.IsNullOrEmpty(playback.CurrentAudioPath))
            return false;

        state.SetMessage(localization["Status_AlignToRecordingOffer"], StatusSeverity.Info, new StatusMessageAction(
            localization["Action_AlignToRecording"],
            () => dialogs.ShowAsync<IAlignToRecordingScreen, bool>().Observe(logger, "align to recording")));
        logger.Info(AppLogCategory.Import, $"Align to recording offered after the {source}.");
        return true;
    }
}
