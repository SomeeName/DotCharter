using DotCharter.Services.Audio.LivePlay;
using DotCharter.Services.Gameplay.Stats;
using DotCharter.Tone.Infrastructure.Storage;

namespace DotCharter.Tests.Services.Gameplay;

/// <summary>
///     0.20 difficulty wiring, svc half: the computed 1–10 chart rating
///     (ChartDifficultyEstimator) persists on the practice profile when a
///     session records and surfaces through GameplayStatsService — the
///     song-select sort/filter value. Schema-tolerant: pre-0.20 profiles
///     load with a null rating.
/// </summary>
public sealed class ChartDifficultyWiringTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "DotCharterTests", Guid.NewGuid().ToString("N"));

    private string StorePath => Path.Combine(_dir, "practice-profiles.json");

    private PracticeProfileService NewService() => new(logger: null, filePath: StorePath);

    private static LivePlaySessionResult SomeResult(long score = 1000)
        => new(score, 0.8, 8, 2, 20, 5, 60, 100, 0.5);

    [Fact]
    public void Record_StampsTheRating_RoundedAndClamped()
    {
        var service = NewService();

        var profile = service.Record("song", "lead", SomeResult(), chartDifficulty: 6.789123);
        Assert.Equal(6.79, profile.ChartDifficulty!.Value, 12);

        profile = service.Record("song", "lead", SomeResult(), chartDifficulty: 42.0);
        Assert.Equal(10.0, profile.ChartDifficulty!.Value, 12);
    }

    [Fact]
    public void NullRating_LeavesTheStoredOneUntouched()
    {
        var service = NewService();
        service.Record("song", "lead", SomeResult(), chartDifficulty: 4.5);

        var profile = service.Record("song", "lead", SomeResult());

        Assert.Equal(4.5, profile.ChartDifficulty!.Value, 12);
    }

    [Fact]
    public void NewRating_Overwrites_LatestParseWins()
    {
        // A re-charted file legitimately re-rates — even downward; the
        // rating is chart state, not a best.
        var service = NewService();
        service.Record("song", "lead", SomeResult(), chartDifficulty: 7.0);

        var profile = service.Record("song", "lead", SomeResult(), chartDifficulty: 5.0);

        Assert.Equal(5.0, profile.ChartDifficulty!.Value, 12);
    }

    [Fact]
    public void OldStoreWithoutRating_LoadsNull()
    {
        Directory.CreateDirectory(_dir);
        File.WriteAllText(StorePath, """
            { "song|lead": { "BestScore": 1000, "BestAccuracy": 0.8, "BestStreak": 5,
              "MasteryPercent": 40.0, "Sessions": [
                { "EndedUtc": "2026-01-01T00:00:00Z", "Score": 1000, "Accuracy": 0.8,
                  "BestStreak": 5, "Hits": 8, "Misses": 2, "TotalSounds": 20,
                  "SpeedPercent": 100, "DurationSeconds": 60 } ] } }
            """);

        Assert.Null(NewService().GetProfile("song", "lead").ChartDifficulty);
    }

    [Fact]
    public void Rating_PersistsAcrossServiceInstances()
    {
        NewService().Record("song", "lead", SomeResult(), chartDifficulty: 3.25);

        Assert.Equal(3.25, NewService().GetProfile("song", "lead").ChartDifficulty!.Value, 12);
    }

    [Fact]
    public void StatsService_SurfacesTheRating()
    {
        var store = NewService();
        store.Record("songA", "lead", SomeResult(score: 500), chartDifficulty: 2.5);
        store.Record("songA", "bass", SomeResult(score: 900), chartDifficulty: 6.0);
        store.Record("songB", "lead", SomeResult()); // unrated (pre-0.20 flow)
        var stats = new GameplayStatsService(store);

        Assert.Equal(2.5, stats.GetSongStats("songA", "lead")!.Difficulty!.Value, 12);
        // GetBestForSong follows the score-ranked best arrangement.
        Assert.Equal(6.0, stats.GetBestForSong("songA")!.Difficulty!.Value, 12);
        Assert.Null(stats.GetBestForSong("songB")!.Difficulty);

        Assert.All(stats.GetStatistics().RecentlyPlayed.Where(s => s.SongKey == "songA"),
            s => Assert.NotNull(s.Difficulty));
    }

    public void Dispose() => BestEffortDelete.Directory(_dir);
}
