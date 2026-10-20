using DotCharter.Localization;
using DotCharter.Tone.Audio.Abstractions;
using DotCharter.Services.Audio.Coordination;
using DotCharter.Tone.Audio.Dsp;
using DotCharter.Tone.Audio.Dsp.Presets;
using DotCharter.Services.Audio.LivePlay.Takes;
using DotCharter.Services.Gameplay.Leaderboards;
using DotCharter.Services.Gameplay.Profiles;
using DotCharter.Services.Gameplay.Stats;
using DotCharter.Services.Library;
using DotCharter.Tone.Diagnostics;
using DotCharter.Tone.Infrastructure.Shell;
using DotCharter.Tone.Infrastructure.Storage;
using DotCharter.Tone.Online.Tone3000;
using DotCharter.Services.Parsing.Rscp;
using DotCharter.Services.Session;
using DotCharter.Tone.UI.ViewModels.PlayMode;
using DotCharter.UI.ViewModels.Editor;
using DotCharter.UI.ViewModels.Panels;
using DotCharter.UI.ViewModels.PlayMode;

namespace DotCharter.Tests.Infrastructure;

/// <summary>
///     Builds the two view models whose service dependencies became REQUIRED in
///     #661 — the composition root always passes them — with every service a test
///     does not name filled by a fake that behaves like the old null did: an
///     in-memory config, an inert session/take store/TONE3000/capture library/shell,
///     a fake transport with no live sample rate. The parameter names and order
///     are the constructors' old ones, so a fixture names only what it cares about.
/// </summary>
internal static class TestRigBuilders
{
    public static PedalboardViewModel Pedalboard(
        IDspChainService dsp,
        IDspTonePresetService presets,
        ILocalizationService localization,
        IRigConfigService? config = null,
        ITone3000Service? tone3000 = null,
        ITone3000AuthService? tone3000Auth = null,
        INamCaptureLibrary? namLibrary = null,
        IAudioPlaybackService? audio = null,
        IFileStorageService? fileStorage = null,
        IUiDispatcher? dispatcher = null,
        IPlayerDspChains? chains = null,
        Action<Action>? subscribeProfileSwitch = null,
        Func<ToneDesignHostSeams, object?>? toneDesignPanel = null,
        IShellLauncher? shell = null) =>
        new(dsp, presets, localization,
            config ?? new FakeConfigurationService(),
            tone3000 ?? new InertTone3000Service(),
            tone3000Auth ?? new InertTone3000Auth(),
            namLibrary ?? new InertNamCaptureLibrary(),
            // No live sample rate: a node's rate-dependent labels read as they did
            // with no transport at all.
            audio ?? new FakeAudioPlaybackService { ActiveSampleRate = 0 },
            fileStorage ?? new FakeFileStorageService(),
            // The single seat the board was handed, as the null chains would give.
            chains ?? new PlayerDspChains([dsp]),
            shell ?? new InertShellLauncher(),
            dispatcher,
            subscribeProfileSwitch,
            toneDesignPanel);

    public static PlayModeViewModel PlayMode(
        IPlayerProfileService profiles,
        IGameplayStatsService stats,
        ILeaderboardService leaderboards,
        ProjectState projectState,
        ILocalizationService localization,
        IConfigurationService? configService = null,
        SongBrowserViewModel? songBrowser = null,
        LivePlayViewModel? livePlay = null,
        TransportViewModel? transport = null,
        IProjectSessionService? projectSession = null,
        ILibraryService? library = null,
        StemMixerViewModel? stemMixer = null,
        IAppLogger? logger = null,
        RsTonePlaybackCoordinator? rsToneCoordinator = null,
        IDspTonePresetService? tonePresets = null,
        IAudioPlaybackService? audio = null,
        ILiveTakeStore? takeStore = null,
        IChartGenreIndex? chartGenres = null,
        PlayModeToneRigViewModel? toneRig = null,
        PlayModeSettingsViewModel? settings = null,
        PlaySetlistViewModel? setlists = null,
        Func<ILatencyCalibrationPrompt?>? latencyPrompt = null,
        IExportTargetWriter? files = null)
    {
        logger ??= new FakeAppLogger();
        return new PlayModeViewModel(profiles, stats, leaderboards, projectState, localization,
            configService ?? new FakeConfigurationService(),
            projectSession ?? new InertProjectSession(),
            library ?? new InertPsarcLibrary(),
            logger,
            tonePresets ?? new FakeDspTonePresetService(),
            audio ?? new FakeAudioPlaybackService(),
            takeStore ?? new InertLiveTakeStore(),
            chartGenres ?? new ChartGenreIndex(logger: logger),
            files ?? new ExportTargetWriter(),
            songBrowser, livePlay, transport, stemMixer, rsToneCoordinator, toneRig, settings, setlists, latencyPrompt);
    }
}
