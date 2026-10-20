using DotCharter.Services.Audio.LivePlay;
using DotCharter.Services.Gameplay.Leaderboards;
using DotCharter.Services.Gameplay.Profiles;
using DotCharter.Tone.Infrastructure.Storage;

namespace DotCharter.Tests.Services.Gameplay;

/// <summary>
///     #329: the local leaderboard ranks ONE detector's runs. The two judging paths hear
///     differently (the CQT verifier credits ~92 % of a real DI performance, the classic path
///     ~60 %), so a board that ranked their scores together let the detector decide the order.
///     Each profile's row on a detector's board is its best run WITH that detector
///     (<see cref="PracticeArrangementProfile.DetectorBests" />).
/// </summary>
public sealed class LeaderboardServiceTests : IDisposable
{
    private const string Song = "lib:SongA";
    private const string Lead = "Lead:Lead";

    private readonly string _dir = Path.Combine(Path.GetTempPath(), "DotCharterTests", Guid.NewGuid().ToString("N"));

    private PlayerProfileService NewPlayers()
        => new(FakeLocalization.English(), logger: null, filePath: Path.Combine(_dir, "player-profiles.json"));

    private PracticeProfileService NewScoped(IPlayerProfileService players)
        => new(players, logger: null, legacyFilePath: Path.Combine(_dir, "legacy.json"));

    private static LivePlaySessionResult Run(long score, LiveDetectorMode detector, double accuracy = 0.9)
        => new(score, accuracy, (int)(accuracy * 100), 100 - (int)(accuracy * 100), 100, 10, 60, 100, 1.0,
            Detector: detector);

    [Fact]
    public void RunsOfTwoDetectors_AreNeverRankedAgainstEachOther()
    {
        var players = NewPlayers();
        var store = NewScoped(players);
        store.Record(Song, Lead, Run(1500, LiveDetectorMode.Cqt));          // default profile
        var second = players.Create("Zwei");
        players.SwitchTo(second.Id);
        store.Record(Song, Lead, Run(900, LiveDetectorMode.Classic));

        var leaderboards = new LeaderboardService(players);
        var cqt = Assert.Single(leaderboards.GetSongLeaderboard(Song, Lead, LiveDetectorMode.Cqt));
        var classic = Assert.Single(leaderboards.GetSongLeaderboard(Song, Lead, LiveDetectorMode.Classic));

        Assert.Equal(1500, cqt.BestScore);
        Assert.Equal(LiveDetectorMode.Cqt, cqt.Detector);
        Assert.False(cqt.IsActiveProfile);
        Assert.Equal(900, classic.BestScore);
        Assert.Equal("Zwei", classic.ProfileName);
        Assert.Equal(LiveDetectorMode.Classic, classic.Detector);
        Assert.Equal(1, classic.Rank);
    }

    /// <summary>One profile that played with both: each board shows that detector's best, not
    /// the profile's overall one.</summary>
    [Fact]
    public void AProfileThatPlayedWithBoth_ShowsEachDetectorsOwnBest()
    {
        var players = NewPlayers();
        var store = NewScoped(players);
        store.Record(Song, Lead, Run(900, LiveDetectorMode.Classic, accuracy: 0.95));
        store.Record(Song, Lead, Run(1500, LiveDetectorMode.Cqt, accuracy: 0.8));
        store.Record(Song, Lead, Run(700, LiveDetectorMode.Classic, accuracy: 0.7)); // worse: lowers nothing

        var leaderboards = new LeaderboardService(players);
        var classic = Assert.Single(leaderboards.GetSongLeaderboard(Song, Lead, LiveDetectorMode.Classic));
        var cqt = Assert.Single(leaderboards.GetSongLeaderboard(Song, Lead, LiveDetectorMode.Cqt));

        Assert.Equal(900, classic.BestScore);
        Assert.Equal(0.95, classic.BestAccuracy, 6);
        Assert.Equal(1500, cqt.BestScore);
        Assert.Equal(0.8, cqt.BestAccuracy, 6);
        Assert.Equal(1500, store.GetProfile(Song, Lead).BestScore);          // the overall best is unchanged
    }

    /// <summary>A best recorded before the detector was (every store written before 0.64.0)
    /// is on NO detector's board — a pre-0.64 CQT run paid every hit the untimed 100, so it
    /// ranks fairly against neither — and still on the overall board.</summary>
    [Fact]
    public void ABestOfUnknownDetector_IsOnNoDetectorsBoard()
    {
        var players = NewPlayers();
        var store = NewScoped(players);
        store.Record(Song, Lead, Run(2000, LiveDetectorMode.Unknown));
        var second = players.Create("Zwei");
        players.SwitchTo(second.Id);
        store.Record(Song, Lead, Run(800, LiveDetectorMode.Cqt));

        var leaderboards = new LeaderboardService(players);
        var cqt = Assert.Single(leaderboards.GetSongLeaderboard(Song, Lead, LiveDetectorMode.Cqt));
        var overall = leaderboards.GetSongLeaderboard(Song, Lead, LiveDetectorMode.Unknown);

        Assert.Equal(800, cqt.BestScore);
        Assert.Empty(leaderboards.GetSongLeaderboard(Song, Lead, LiveDetectorMode.Classic));
        Assert.Equal(new long[] { 2000, 800 }, overall.Select(r => r.BestScore));
    }

    public void Dispose() => BestEffortDelete.Directory(_dir);
}
