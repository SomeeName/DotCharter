using DotCharter.Services.Ai.Match;
using DotCharter.Services.Ai.Match.Model;
using DotCharter.Tone.Online.Tone3000;
using DotCharter.Tone.UI.ViewModels.PlayMode;
using DotCharter.UI.ViewModels.PlayMode;
using DotCharter.Tone.UI.ViewModels.Settings;
using Microsoft.Extensions.DependencyInjection;

namespace DotCharter.Tests.Perf;

/// <summary>
///     Play Mode's rig for a headless case that builds its own
///     <see cref="PlayModeViewModel" />: a fresh <see cref="PlayModeToneRigViewModel" />
///     whose factories resolve from the headless session's container, as Play Mode
///     did through <c>App.Services</c> before the rig was injected. The shared
///     chain singleton, the TONE3000 capture library and the per-screen parts are
///     the real registrations; the rig itself is fresh per case, so no case
///     inherits another's open overlay or rig screen.
/// </summary>
internal static class HeadlessToneRig
{
    /// <summary>A rig over the session container for a view model built on
    /// <paramref name="state" />.</summary>
    public static PlayModeToneRigViewModel FromSession(ProjectState state)
    {
        var services = DotCharter.UI.App.Services;
        return new PlayModeToneRigViewModel(
            state, null, null, null, services?.GetService<IHistoryService>(),
            () => services?.GetService<PedalboardViewModel>(),
            () => services?.GetService<INamCaptureLibrary>(),
            () => services?.GetService<LiveInputChannelViewModel>(),
            () => services?.GetService<RigReampViewModel>(),
            () => services?.GetService<IToneMatcher>(),
            () => services?.GetService<IStemIsolator>(),
            () => services?.GetService<IRigEstimator>(),
            () => services?.GetService<IRigEstimatorModelDownloadService>(),
            () => services?.GetService<IToneSectionProposer>(),
            () => services?.GetService<IToneSetMatcher>(),
            () => services?.GetService<IToneNarrator>());
    }
}
