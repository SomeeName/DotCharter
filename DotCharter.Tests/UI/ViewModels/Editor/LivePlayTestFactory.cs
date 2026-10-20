using DotCharter.Tone.Audio.Abstractions;
using DotCharter.Services.Audio.LivePlay;
using DotCharter.UI.ViewModels.Editor;

namespace DotCharter.Tests.UI.ViewModels.Editor;

/// <summary>
///     Builds a <see cref="LivePlayViewModel" /> with the collaborators the
///     composition root would inject, each on a fake, so a test that only cares
///     about the view model does not restate its constructor.
/// </summary>
internal static class LivePlayTestFactory
{
    public static LivePlayViewModel Create(
        IAudioPlaybackService audio,
        ProjectState projectState,
        IAppLogger logger,
        IConfigurationService configService,
        IPracticeProfileService profiles)
    {
        return new LivePlayViewModel(audio, projectState, logger, configService, profiles,
            new LiveTuningDriftMonitor(FakeLocalization.English(), logger),
            new LivePlayMetersViewModel(logger),
            new LivePlayRunArchive(profiles, logger),
            FakeLocalization.English());
    }
}
