using DotCharter.Presentation.Screens.Settings;
using DotCharter.Tone.Ai;
using DotCharter.Services.Ai.Match;
using DotCharter.Services.Ai.Match.Model;
using DotCharter.Tone.Infrastructure.Shell;

namespace DotCharter.Tests.Presentation.Settings;

/// <summary>
///     Builds Settings ▸ AI the way the composition root does — every AI
///     service optional — so a case names only the one it is about.
/// </summary>
internal static class AiTabRig
{
    internal static AiSettingsViewModel Create(
        IConfigurationService config,
        IAiSecretStore? secrets = null,
        IToneDesignModelFactory? factory = null,
        IToneDesignService? design = null,
        IAppLogger? logger = null,
        IRigEstimatorModelDownloadService? downloads = null,
        Func<IRigEstimator?>? estimator = null,
        IShellLauncher? shell = null) =>
        new(config, FakeLocalization.English(),
            new MatchModelViewModel(FakeLocalization.English(), downloads, estimator, shell, logger),
            secrets, factory, design, logger);
}
