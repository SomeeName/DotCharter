using System.Text.Json;
using DotCharter.Services.Audio.LivePlay;
using DotCharter.Services.Gameplay.Stats;
using DotCharter.Tone.Infrastructure.Storage;

namespace DotCharter.Tests.Services.Audio.LivePlay;

/// <summary>
///     0.20 full-combo recognition: FC per run (whole chart judged, zero
///     misses) and per section, persisted with the practice store so a stored
///     best carries FC/tier metadata — schema-tolerant (old records load with
///     nulls) and never violating the best-score invariant.
/// </summary>
public sealed class FullComboDetectionTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "DotCharterTests", Guid.NewGuid().ToString("N"));

    private string StorePath => Path.Combine(_dir, "practice-profiles.json");

    private PracticeProfileService NewService() => new(logger: null, filePath: StorePath);

    private static LivePlaySessionResult Result(long score = 1000, int hits = 8, int misses = 2, int total = 20,
        int perfects = 0, int greats = 0, int goods = 0)
        => new(score, hits + misses == 0 ? 1.0 : hits / (double)(hits + misses), hits, misses, total,
            BestStreak: hits, DurationSeconds: 60, SpeedPercent: 100,
            SongProgress: total == 0 ? 0 : (hits + misses) / (double)total,
            PerfectCount: perfects, GreatCount: greats, GoodCount: goods);

    // ---- run-level FC -------------------------------------------------------

    [Fact]
    public void FullRun_NoMisses_IsFullCombo() => Assert.True(Result(hits: 20, misses: 0, total: 20).IsFullCombo);

    [Fact]
    public void AnyMiss_IsNotFullCombo() => Assert.False(Result(hits: 19, misses: 1, total: 20).IsFullCombo);

    [Fact]
    public void CleanPartialRun_IsNotFullCombo() =>
        // No misses but only half the chart judged — coverage matters.
        Assert.False(Result(hits: 10, misses: 0, total: 20).IsFullCombo);

    [Fact]
    public void EmptyChart_IsNotFullCombo() => Assert.False(Result(hits: 0, misses: 0, total: 0).IsFullCombo);

    // ---- section-level FC ---------------------------------------------------

    [Fact]
    public void Section_AllSoundsHit_IsFullCombo() => Assert.True(new SectionResult("verse", 0, 1000, 8, Hits: 8, Misses: 0).IsFullCombo);

    [Theory]
    [InlineData(7, 1)] // one miss
    [InlineData(4, 0)] // clean but not fully judged
    public void Section_MissOrPartial_IsNotFullCombo(int hits, int misses) => Assert.False(new SectionResult("verse", 0, 1000, 8, hits, misses).IsFullCombo);

    [Fact]
    public void EmptySection_IsNotFullCombo() => Assert.False(new SectionResult("noguitar", 0, 1000, 0, 0, 0).IsFullCombo);

    [Fact]
    public void Report_CountsFullComboSections()
    {
        var sections = new[]
        {
            new SectionResult("intro", 0, 1000, 4, 4, 0),   // FC
            new SectionResult("verse", 1000, 2000, 6, 5, 1), // miss
            new SectionResult("chorus", 2000, 3000, 5, 5, 0) // FC
        };
        var result = Result(hits: 14, misses: 1, total: 15);
        var report = new LivePlaySessionReport(result, new PracticeBests(0, 0, 0, 0),
            new PracticeBests(0, 0, 0, 0), true, "song", "lead", sections);

        Assert.Equal(2, report.FullComboSectionCount);
        Assert.False(report.IsFullCombo);
    }

    // ---- persistence --------------------------------------------------------

    [Fact]
    public void Record_StampsFcAndTierMetadataOnSessionAndBest()
    {
        var service = NewService();
        var profile = service.Record("song", "lead",
            Result(score: 2000, hits: 20, misses: 0, total: 20, perfects: 15, greats: 4, goods: 1));

        Assert.True(profile.FullComboAchieved);
        Assert.True(profile.BestRunFullCombo);
        Assert.Equal(15, profile.BestRunPerfects);
        Assert.Equal(4, profile.BestRunGreats);
        Assert.Equal(1, profile.BestRunGoods);

        var session = profile.Sessions[^1];
        Assert.True(session.FullCombo);
        Assert.Equal(15, session.PerfectCount);
        Assert.Equal(4, session.GreatCount);
        Assert.Equal(1, session.GoodCount);
    }

    [Fact]
    public void WorseRun_NeverRestampsBestRunMetadata_ButStickyFcSurvives()
    {
        var service = NewService();
        service.Record("song", "lead",
            Result(score: 5000, hits: 18, misses: 2, total: 20, perfects: 18));

        // A lower-scoring FC run: sticky flag set, best-run metadata intact
        // (the stored best still describes the 5000-point run).
        var profile = service.Record("song", "lead",
            Result(score: 2000, hits: 20, misses: 0, total: 20, perfects: 5, greats: 10, goods: 5));

        Assert.Equal(5000, profile.BestScore);
        Assert.True(profile.FullComboAchieved);
        Assert.False(profile.BestRunFullCombo);
        Assert.Equal(18, profile.BestRunPerfects);
    }

    [Fact]
    public void BetterRun_RestampsBestRunMetadata()
    {
        var service = NewService();
        service.Record("song", "lead",
            Result(score: 2000, hits: 20, misses: 0, total: 20, perfects: 20));
        var profile = service.Record("song", "lead",
            Result(score: 6000, hits: 18, misses: 2, total: 20, perfects: 10, greats: 8));

        Assert.Equal(6000, profile.BestScore);
        Assert.False(profile.BestRunFullCombo); // the best run was not an FC
        Assert.Equal(10, profile.BestRunPerfects);
        Assert.True(profile.FullComboAchieved); // the earlier FC stays sticky
    }

    [Fact]
    public void EqualScoreFcRun_UpgradesTheFlag_NeverDowngrades()
    {
        var service = NewService();
        service.Record("song", "lead",
            Result(score: 2000, hits: 18, misses: 2, total: 20));
        var profile = service.Record("song", "lead",
            Result(score: 2000, hits: 20, misses: 0, total: 20));
        Assert.True(profile.BestRunFullCombo);

        // …and an equal-score non-FC run afterwards never downgrades it.
        profile = service.Record("song", "lead",
            Result(score: 2000, hits: 19, misses: 1, total: 20));
        Assert.True(profile.BestRunFullCombo);
    }

    [Fact]
    public void OldStoreWithoutFcFields_LoadsWithNulls()
    {
        Directory.CreateDirectory(_dir);
        File.WriteAllText(StorePath, """
            {
              "song|lead": {
                "DisplayName": "Artist - Title",
                "BestScore": 4200,
                "BestAccuracy": 0.9,
                "BestStreak": 30,
                "MasteryPercent": 80.0,
                "Sessions": [
                  { "EndedUtc": "2026-01-01T00:00:00Z", "Score": 4200, "Accuracy": 0.9,
                    "BestStreak": 30, "Hits": 90, "Misses": 10, "TotalSounds": 100,
                    "SpeedPercent": 100, "DurationSeconds": 200 }
                ]
              }
            }
            """);

        var profile = NewService().GetProfile("song", "lead");

        Assert.Equal(4200, profile.BestScore);
        Assert.False(profile.FullComboAchieved);
        Assert.Null(profile.BestRunFullCombo); // unknown, not "false claim"
        Assert.Null(profile.BestRunPerfects);
        Assert.Null(profile.Sessions[0].FullCombo);
        Assert.Null(profile.Sessions[0].PerfectCount);
    }

    [Fact]
    public void LegacyBest_StaysUnknown_UntilARunRaisesIt()
    {
        Directory.CreateDirectory(_dir);
        File.WriteAllText(StorePath, """
            { "song|lead": { "BestScore": 5000, "BestAccuracy": 0.9, "BestStreak": 30,
              "MasteryPercent": 80.0, "Sessions": [
                { "EndedUtc": "2026-01-01T00:00:00Z", "Score": 5000, "Accuracy": 0.9,
                  "BestStreak": 30, "Hits": 90, "Misses": 10, "TotalSounds": 100,
                  "SpeedPercent": 100, "DurationSeconds": 200 } ] } }
            """);
        var service = NewService();

        // A run below the legacy best must not claim to describe it.
        var profile = service.Record("song", "lead", Result(score: 100, hits: 5, misses: 5, total: 20));
        Assert.Null(profile.BestRunFullCombo);

        // Beating the legacy best finally stamps known metadata.
        profile = service.Record("song", "lead",
            Result(score: 9000, hits: 20, misses: 0, total: 20, perfects: 20));
        Assert.True(profile.BestRunFullCombo);
        Assert.Equal(20, profile.BestRunPerfects);
    }

    [Fact]
    public void RoundTrip_PersistsFcMetadata()
    {
        NewService().Record("song", "lead",
            Result(score: 2000, hits: 20, misses: 0, total: 20, perfects: 20));

        var reloaded = NewService().GetProfile("song", "lead");
        Assert.True(reloaded.FullComboAchieved);
        Assert.True(reloaded.BestRunFullCombo);
        Assert.Equal(20, reloaded.BestRunPerfects);
        Assert.True(reloaded.Sessions[^1].FullCombo);

        // And the JSON is valid, parseable store content.
        using var doc = JsonDocument.Parse(File.ReadAllText(StorePath));
        Assert.True(doc.RootElement.TryGetProperty("song|lead", out _));
    }

    [Fact]
    public void GameplayStats_SurfaceTheFcBadge()
    {
        var service = NewService();
        service.Record("song", "lead",
            Result(score: 2000, hits: 20, misses: 0, total: 20), displayName: "Artist - Title");
        service.Record("other", "lead",
            Result(score: 900, hits: 18, misses: 2, total: 20), displayName: "Other");
        var stats = new GameplayStatsService(service);

        Assert.True(stats.GetBestForSong("song")!.FullCombo);
        Assert.False(stats.GetBestForSong("other")!.FullCombo);
        Assert.True(stats.GetSongStats("song", "lead")!.FullCombo);
    }

    public void Dispose() => BestEffortDelete.Directory(_dir);
}
