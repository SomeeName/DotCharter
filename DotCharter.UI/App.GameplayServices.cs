// Composition root, gameplay slice: player + practice profiles, take recording and
// offline re-amp, run replays and their video export, gameplay stats, the chart
// genre index and the local leaderboard.

using DotCharter.Services.Audio.StemSeparation.Cache;
using Microsoft.Extensions.DependencyInjection;

namespace DotCharter.UI;

public partial class App
{
    private static void AddGameplayServices(IServiceCollection services)
    {
        services.AddSingleton<DotCharter.Services.Gameplay.Profiles.IPlayerProfileService>(sp =>
            new DotCharter.Services.Gameplay.Profiles.PlayerProfileService(
                sp.GetRequiredService<ILocalizationService>(),
                sp.GetRequiredService<IAppLogger>()));
        services.AddSingleton<DotCharter.Services.Audio.LivePlay.IPracticeProfileService>(sp =>
            new DotCharter.Services.Audio.LivePlay.PracticeProfileService(
                sp.GetRequiredService<DotCharter.Services.Gameplay.Profiles.IPlayerProfileService>(),
                sp.GetRequiredService<IAppLogger>()));
        // Take recorder (0.17 foundations): persists the dry pre-VST input
        // capture per Live Play run into the active profile's takes/ dir.
        // Opt-in (AppConfig.LivePlayTakeRecorderEnabled, default OFF).
        services.AddSingleton<DotCharter.Services.Audio.LivePlay.Takes.ILiveTakeStore>(sp =>
            new DotCharter.Services.Audio.LivePlay.Takes.LiveTakeStore(
                sp.GetRequiredService<DotCharter.Services.Gameplay.Profiles.IPlayerProfileService>(),
                sp.GetRequiredService<DotCharter.Services.Audio.LivePlay.IPracticeProfileService>(),
                new FfmpegStemEncoder(sp.GetService<IAppLogger>(),
                    configuredFfmpegPath: () => sp.GetService<IConfigurationService>()?.LoadAppConfig().FfmpegPath),
                () => sp.GetRequiredService<IConfigurationService>().LoadAppConfig(),
                sp.GetService<IAppLogger>()));
        // v0.32 run replays: judged event stream per run, profile-scoped
        // `replays/` dir (inside the profiles tree — payload-filter refusal +
        // backup inherited). Tiny files; no sweep.
        services.AddSingleton<DotCharter.Services.Audio.LivePlay.Replays.IReplayStore>(sp =>
            new DotCharter.Services.Audio.LivePlay.Replays.ReplayStore(
                sp.GetRequiredService<DotCharter.Services.Gameplay.Profiles.IPlayerProfileService>(),
                sp.GetService<IAppLogger>()));
        services.AddSingleton<DotCharter.Services.Gameplay.Stats.IGameplayStatsService>(sp =>
            new DotCharter.Services.Gameplay.Stats.GameplayStatsService(
                sp.GetRequiredService<DotCharter.Services.Audio.LivePlay.IPracticeProfileService>()));
        // Own-chart genres (the song browser's genre facet, the metadata
        // dialog): the `dcGenre` header of each of the player's own `.rscp`
        // projects, read ONCE per library scan on a pool thread. A singleton so
        // the cache survives the scans; the read never happens on the UI thread.
        services.AddSingleton<DotCharter.Services.Parsing.Rscp.IChartGenreIndex>(sp =>
            new DotCharter.Services.Parsing.Rscp.ChartGenreIndex(logger: sp.GetService<IAppLogger>()));
        services.AddSingleton<DotCharter.Services.Gameplay.Leaderboards.ILeaderboardService,
            DotCharter.Services.Gameplay.Leaderboards.LeaderboardService>();
        // Play Mode setlists: one setlists.json per player profile, read and
        // written by PlaySetlistViewModel off the UI thread.
        services.AddSingleton<DotCharter.Services.Gameplay.Setlists.IPlaySetlistStore>(sp =>
            new DotCharter.Services.Gameplay.Setlists.PlaySetlistStore(
                sp.GetRequiredService<DotCharter.Services.Gameplay.Profiles.IPlayerProfileService>(),
                sp.GetService<IAppLogger>()));
    }
}
