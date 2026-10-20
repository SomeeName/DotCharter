using DotCharter.Services.Audio.LivePlay;
using DotCharter.Services.Gameplay.Leaderboards;
using DotCharter.Services.Gameplay.Profiles;
using DotCharter.Services.Gameplay.Scoring;
using DotCharter.Services.Gameplay.Stats;
using DotCharter.Tone.Infrastructure.Storage;

namespace DotCharter.Tests.Services.Gameplay;

public sealed class GradeCalculatorTests
{
    [Theory]
    [InlineData(1.00, LetterGrade.S)]
    [InlineData(0.99, LetterGrade.S)]
    [InlineData(0.97, LetterGrade.A)]
    [InlineData(0.92, LetterGrade.B)]
    [InlineData(0.85, LetterGrade.C)]
    [InlineData(0.70, LetterGrade.D)]
    [InlineData(0.10, LetterGrade.F)]
    public void AccuracyMapsToGrade(double accuracy, LetterGrade expected)
        => Assert.Equal(expected, GradeCalculator.FromAccuracy(accuracy));
}

public sealed class GameplayStatsAndLeaderboardTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "DotCharterTests", Guid.NewGuid().ToString("N"));

    private PlayerProfileService NewPlayers()
        => new(FakeLocalization.English(), logger: null, filePath: Path.Combine(_dir, "player-profiles.json"));

    private PracticeProfileService NewScoped(IPlayerProfileService players)
        => new(players, logger: null, legacyFilePath: Path.Combine(_dir, "legacy.json"));

    private static LivePlaySessionResult Result(long score, double accuracy, int hits, int misses,
        int streak = 5, double duration = 60)
        => new(score, accuracy, hits, misses, hits + misses, streak, duration, 100,
            1.0);

    [Fact]
    public void Statistics_AggregateAcrossSongsAndSessions()
    {
        var players = NewPlayers();
        var store = NewScoped(players);
        store.Record("lib:SongA", "Lead:Lead", Result(1000, 0.96, 96, 4, streak: 30, duration: 200));
        store.Record("lib:SongA", "Lead:Lead", Result(1200, 0.99, 99, 1, streak: 50, duration: 210));
        store.Record("lib:SongB", "Bass:Bass", Result(500, 0.85, 85, 15, streak: 12, duration: 180));

        var stats = new GameplayStatsService(store).GetStatistics();

        Assert.Equal(2, stats.SongsPlayed);
        Assert.Equal(3, stats.TotalSessions);
        Assert.Equal(590, stats.TotalPlayTimeSeconds, 1);
        Assert.Equal(96 + 99 + 85, stats.TotalNotesHit);
        Assert.Equal(50, stats.HighestStreak);
        Assert.Equal(0.99, stats.BestAccuracy, 3);
        Assert.Equal(3, stats.AccuracyHistory.Count);
        Assert.Equal(1, stats.GradeDistribution[LetterGrade.S]); // SongA best 0.99
        Assert.Equal(1, stats.GradeDistribution[LetterGrade.C]); // SongB best 0.85
        Assert.Equal("lib:SongA", stats.MostPlayed[0].SongKey);
        Assert.Equal(2, stats.MostPlayed[0].SessionCount);
    }

    /// <summary>
    ///     "Songs played" is SONGS. The practice store keys on
    ///     <c>songKey|arrangementName</c>, so one song practised on Lead,
    ///     Rhythm and Bass is three store rows — and counting the rows printed
    ///     "3" under a label that says Songs played, on a page whose whole
    ///     point is a true summary of a library. <c>GradeDistribution</c> is
    ///     per-ARRANGEMENT deliberately and documents it; this figure never did.
    /// </summary>
    [Fact]
    public void Statistics_SongsPlayed_CountsSongs_NotArrangementsOfOneSong()
    {
        var players = NewPlayers();
        var store = NewScoped(players);
        store.Record("lib:SongA", "Lead:Lead", Result(1000, 0.96, 96, 4));
        store.Record("lib:SongA", "Rhythm:Rhythm", Result(900, 0.90, 90, 10));
        store.Record("lib:SongA", "Bass:Bass", Result(800, 0.88, 88, 12));
        store.Record("lib:SongB", "Lead:Lead", Result(700, 0.80, 80, 20));

        var stats = new GameplayStatsService(store).GetStatistics();

        Assert.Equal(2, stats.SongsPlayed);
        // The per-arrangement figures beside it are unchanged — the rows are
        // still one per arrangement, which is what those readouts mean.
        Assert.Equal(4, stats.TotalSessions);
        Assert.Equal(4, stats.MostPlayed.Count);
    }

    [Fact]
    public void SongStats_ReturnsNullForUnplayed()
    {
        var players = NewPlayers();
        var stats = new GameplayStatsService(NewScoped(players));
        Assert.Null(stats.GetSongStats("lib:Nope", "Lead:Lead"));
    }

    [Fact]
    public void Leaderboard_RanksProfilesByBestScore()
    {
        var players = NewPlayers();
        var store = NewScoped(players);

        // Default profile: 800 points.
        store.Record("lib:SongA", "Lead:Lead", Result(800, 0.9, 90, 10));

        // Second profile: 1500 points on the same song.
        var second = players.Create("Zwei");
        players.SwitchTo(second.Id);
        store.Record("lib:SongA", "Lead:Lead", Result(1500, 0.97, 97, 3));

        var board = new LeaderboardService(players).GetSongLeaderboard("lib:SongA", "Lead:Lead", LiveDetectorMode.Unknown);

        Assert.Equal(2, board.Count);
        Assert.Equal(1, board[0].Rank);
        Assert.Equal("Zwei", board[0].ProfileName);
        Assert.Equal(1500, board[0].BestScore);
        Assert.True(board[0].IsActiveProfile);
        Assert.Equal(2, board[1].Rank);
        Assert.Equal(800, board[1].BestScore);
        Assert.Equal(LetterGrade.B, board[1].Grade);
    }

    /// <summary>
    ///     One profile, two arrangements of one song: the board is per-ARRANGEMENT,
    ///     so asking for one never mixes in the other's score. (The any-arrangement
    ///     overload this test used to exercise was removed in 0.58 — it was driven
    ///     by nothing but this test; see <see cref="ILeaderboardService" />.)
    /// </summary>
    [Fact]
    public void Leaderboard_IsScopedToTheArrangementAsked()
    {
        var players = NewPlayers();
        var store = NewScoped(players);
        store.Record("lib:SongA", "Lead:Lead", Result(700, 0.9, 90, 10));
        store.Record("lib:SongA", "Rhythm:Rhythm", Result(900, 0.8, 80, 20));

        var service = new LeaderboardService(players);
        Assert.Equal(700, Assert.Single(service.GetSongLeaderboard("lib:SongA", "Lead:Lead", LiveDetectorMode.Unknown)).BestScore);
        Assert.Equal(900, Assert.Single(service.GetSongLeaderboard("lib:SongA", "Rhythm:Rhythm", LiveDetectorMode.Unknown)).BestScore);
    }

    [Fact]
    public void Leaderboard_OmitsProfilesWithoutPlays()
    {
        var players = NewPlayers();
        var store = NewScoped(players);
        store.Record("lib:SongA", "Lead:Lead", Result(800, 0.9, 90, 10));
        players.Create("Leer");

        var board = new LeaderboardService(players).GetSongLeaderboard("lib:SongA", "Lead:Lead", LiveDetectorMode.Unknown);
        Assert.Single(board);
    }

    public void Dispose() => BestEffortDelete.Directory(_dir);
}
