using System.Globalization;
using System.Reflection;
using System.Resources;
using DotCharter.Localization;
using DotCharter.Services.Audio.LivePlay;
using DotCharter.Services.Gameplay.Leaderboards;
using DotCharter.Services.Gameplay.Profiles;
using DotCharter.Services.Gameplay.Stats;
using DotCharter.Tone.Infrastructure.Storage;
using DotCharter.UI.ViewModels.PlayMode;

namespace DotCharter.Tests.UI.ViewModels.PlayMode;

/// <summary>
///     The results screen's local leaderboard ranks ONE note detector's runs
///     (#329), and the screen says which (#642). A player who flipped the CQT
///     verifier at Get Ready saw a board that had lost their old best with no
///     reason given.
///     <para>
///         The library's per-song best (Song Select's "Your best") stays
///         cross-detector on purpose — it is the player's best on the song — and
///         its tooltip says so. The last two facts pin that decision.
///     </para>
/// </summary>
public sealed class ResultsBoardDetectorCaptionTests : IDisposable
{
    private const string Song = "lib:SongA";
    private const string Lead = "Lead:Lead";

    private static readonly ResourceManager Resources = new(
        "DotCharter.Localization.Resources", typeof(LocalizationService).Assembly);

    private readonly string _dir = Path.Combine(Path.GetTempPath(), "DotCharterTests", Guid.NewGuid().ToString("N"));

    [Fact]
    public void TheCaptionNamesTheDetectorThatJudgedTheRun()
    {
        var loc = FakeLocalization.English();

        Assert.Contains("CQT", ResultsBoardCaption.For(LiveDetectorMode.Cqt, loc), StringComparison.Ordinal);
        Assert.Contains("classic", ResultsBoardCaption.For(LiveDetectorMode.Classic, loc), StringComparison.Ordinal);
        // A run that recorded no detector gets the pre-0.64.0 board, every
        // profile's best whatever produced it, and must not claim one detector.
        var any = ResultsBoardCaption.For(LiveDetectorMode.Unknown, loc);
        Assert.Contains("either", any, StringComparison.Ordinal);
        Assert.DoesNotContain("CQT", any, StringComparison.Ordinal);
    }

    /// <summary>Every detector has its own caption, in all three languages — a key
    /// missing from a translation falls back to English without a sound.</summary>
    [Fact]
    public void EveryDetectorHasItsOwnCaption_InEveryLanguage()
    {
        var modes = Enum.GetValues<LiveDetectorMode>();
        var keys = modes.Select(ResultsBoardCaption.KeyFor).ToArray();
        Assert.Equal(keys.Length, keys.Distinct(StringComparer.Ordinal).Count());

        foreach (var culture in new[] { CultureInfo.InvariantCulture, CultureInfo.GetCultureInfo("de-DE"), CultureInfo.GetCultureInfo("ja-JP") })
        {
            var set = Resources.GetResourceSet(culture, createIfNotExists: true, tryParents: false);
            Assert.NotNull(set);
            var captions = new List<string>();
            foreach (var key in keys.Append("PlayMode_Results_Leaderboard_Detector_Tip").Append("PlayMode_SongSelect_YourBest_Tip"))
            {
                var value = set!.GetString(key);
                Assert.False(string.IsNullOrWhiteSpace(value), $"{key} is missing from the {culture.Name} resx");
                captions.Add(value!);
            }

            Assert.Equal(captions.Count, captions.Distinct(StringComparer.Ordinal).Count());
        }
    }

    /// <summary>
    ///     The wiring: the caption on the results screen follows the run's
    ///     detector, and the board beside it is that detector's board.
    /// </summary>
    [Fact]
    public void TheResultsScreenCaptionsTheBoardWithTheRunsDetector()
    {
        var vm = NewVm(out var store);
        store.Record(Song, Lead, Run(1500, LiveDetectorMode.Cqt));
        store.Record(Song, Lead, Run(900, LiveDetectorMode.Classic));
        var loc = FakeLocalization.English();

        CompleteRun(vm, Run(1500, LiveDetectorMode.Cqt));
        Assert.Equal(loc["PlayMode_Results_Leaderboard_Cqt"], vm.RunResults.ResultLeaderboardCaption);
        Assert.Equal(1500, Assert.Single(vm.RunResults.ResultLeaderboard).BestScore);

        CompleteRun(vm, Run(900, LiveDetectorMode.Classic));
        Assert.Equal(loc["PlayMode_Results_Leaderboard_Classic"], vm.RunResults.ResultLeaderboardCaption);
        Assert.Equal(900, Assert.Single(vm.RunResults.ResultLeaderboard).BestScore);
    }

    /// <summary>
    ///     The library's best is the song's best whichever detector judged it:
    ///     a classic best above a CQT one is still "your best". This is the
    ///     decision the tooltip describes; split it per detector and this fails.
    /// </summary>
    [Fact]
    public void TheLibrarysBest_IsTheSongsBestAcrossBothDetectors()
    {
        _ = NewVm(out var store);
        store.Record(Song, Lead, Run(1500, LiveDetectorMode.Cqt));
        store.Record(Song, Lead, Run(2100, LiveDetectorMode.Classic));

        Assert.Equal(2100, new GameplayStatsService(store).GetBestForSong(Song)!.BestScore);

        var tip = FakeLocalization.English()["PlayMode_SongSelect_YourBest_Tip"];
        Assert.Contains("either note detector", tip, StringComparison.Ordinal);
        Assert.Contains("any arrangement", tip, StringComparison.Ordinal);
    }

    // ---- fixture -------------------------------------------------------------

    private static LivePlaySessionResult Run(long score, LiveDetectorMode detector) =>
        new(score, 0.9, 90, 10, 100, 20, 60, 100, 1.0, Detector: detector);

    private PlayModeViewModel NewVm(out PracticeProfileService store)
    {
        var profiles = new PlayerProfileService(FakeLocalization.English(), logger: null,
            filePath: Path.Combine(_dir, "player-profiles.json"), defaultProfileName: "Tester");
        store = new PracticeProfileService(profiles, logger: null,
            legacyFilePath: Path.Combine(_dir, "legacy.json"));
        return TestRigBuilders.PlayMode(profiles, new GameplayStatsService(store),
            new LeaderboardService(profiles), new ProjectState(), FakeLocalization.English(),
            new FakeConfigurationService());
    }

    /// <summary>Drives the private session-end handler the live session raises.
    /// It only answers a run THIS flow started, so the screen is Playing first.</summary>
    private static void CompleteRun(PlayModeViewModel vm, LivePlaySessionResult result)
    {
        vm.CurrentScreen = PlayModeScreen.Playing;
        var bests = new PracticeBests(0, 0, 0, 0);
        var report = new LivePlaySessionReport(result, bests, bests, WasRecorded: true, Song, Lead, []);
        var method = typeof(PlayModeViewModel).GetMethod("OnSessionCompleted",
            BindingFlags.Instance | BindingFlags.NonPublic);
        Assert.True(method != null, "PlayModeViewModel.OnSessionCompleted is gone — this test was renamed out of its job");
        method!.Invoke(vm, [report]);
        Assert.Equal(PlayModeScreen.Results, vm.CurrentScreen);
    }

    public void Dispose() => BestEffortDelete.Directory(_dir);
}
