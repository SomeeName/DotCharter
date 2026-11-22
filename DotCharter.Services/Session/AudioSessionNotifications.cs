using DotCharter.Services.State;
using DotCharter.Tone.Diagnostics;

namespace DotCharter.Services.Session;

/// <summary>Dispatches engine clears and waveform publication within their owning session.</summary>
internal static class AudioSessionNotifications
{
    public static void Clear(IAudioPlaybackService audio, ProjectState state, IUiDispatcher ui, Func<bool> isDisposed)
    {
        var project = state.ActiveProject;
        audio.FreeAudio();
        ui.Post(() =>
        {
            // A newer master in this project still needs its cleared stems reloaded.
            if (!isDisposed() && ReferenceEquals(project, state.ActiveProject)) state.NotifyAudioCleared();
        });
    }

    public static void Loaded(ProjectState state, IUiDispatcher ui, Func<bool> isCurrent) => ui.Post(() =>
    {
        if (isCurrent()) state.NotifyAudioLoaded();
    });
}
