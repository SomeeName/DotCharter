using DotCharter.Services.Audio.LivePlay;
using DotCharter.Tone.Infrastructure.Storage;

namespace DotCharter.Tests.Services.Audio.LivePlay;

/// <summary>
///     #329: the detector that produced a run is recorded — on the session record and in the
///     per-detector bests the leaderboard ranks — and survives the store's round trip. A store
///     written before it existed loads with both absent, which reads as UNKNOWN, never as
///     either detector.
/// </summary>
public sealed class DetectorModePersistenceTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "DotCharterTests", Guid.NewGuid().ToString("N"));

    private string StorePath => Path.Combine(_dir, "practice-profiles.json");

    private PracticeProfileService NewService() => new(logger: null, filePath: StorePath);

    private static LivePlaySessionResult Run(long score, LiveDetectorMode detector, int hits = 9, int misses = 1,
        int total = 10, int streak = 9)
        => new(score, hits / (double)(hits + misses), hits, misses, total, streak, 60, 100,
            (hits + misses) / (double)total, Detector: detector);

    [Fact]
    public void TheDetector_SurvivesTheStoresRoundTrip()
    {
        NewService().Record("song", "lead", Run(1200, LiveDetectorMode.Cqt));
        NewService().Record("song", "lead", Run(800, LiveDetectorMode.Classic));

        var profile = NewService().GetProfile("song", "lead");               // a fresh read from disk

        Assert.Equal(new[] { "Classic", "Cqt" }, profile.Sessions.Select(s => s.Detector ?? "(null)").Order());
        Assert.NotNull(profile.DetectorBests);
        Assert.Equal(1200, profile.DetectorBests["Cqt"].BestScore);
        Assert.Equal(800, profile.DetectorBests["Classic"].BestScore);
        Assert.Equal(0.9, profile.DetectorBests["Classic"].BestAccuracy, 6);
        Assert.Equal(9, profile.DetectorBests["Classic"].BestStreak);
        Assert.NotEqual(default, profile.DetectorBests["Classic"].LastPlayedUtc);
        Assert.Contains("\"Detector\": \"Cqt\"", File.ReadAllText(StorePath), StringComparison.Ordinal);
    }

    /// <summary>A per-detector best follows the store's own rules: a worse run never lowers it,
    /// and a run under the coverage floor never sets its accuracy.</summary>
    [Fact]
    public void APerDetectorBest_KeepsTheBestScoreInvariantAndTheCoverageGate()
    {
        var service = NewService();
        service.Record("song", "lead", Run(1500, LiveDetectorMode.Cqt, hits: 8, misses: 2, streak: 8));
        service.Record("song", "lead", Run(400, LiveDetectorMode.Cqt, hits: 2, misses: 8, streak: 2));
        var profile = service.Record("song", "lead",
            Run(100, LiveDetectorMode.Cqt, hits: 2, misses: 0, total: 100, streak: 2)); // 100 %, 2 % coverage

        var best = profile.DetectorBests!["Cqt"];
        Assert.Equal(1500, best.BestScore);
        Assert.Equal(0.8, best.BestAccuracy, 6);
        Assert.Equal(8, best.BestStreak);
    }

    /// <summary>A store written before 0.64.0 has neither field. It loads, reads as unknown,
    /// and its overall bests are untouched; an unknown-detector run records no per-detector
    /// best.</summary>
    [Fact]
    public void AStoreFromBeforeTheDetectorWasRecorded_LoadsAsUnknown()
    {
        Directory.CreateDirectory(_dir);
        File.WriteAllText(StorePath, """
            {
              "song|lead": {
                "DisplayName": "Artist - Song",
                "BestScore": 4321,
                "BestAccuracy": 0.9,
                "BestStreak": 40,
                "Sessions": [ { "EndedUtc": "2026-09-01T12:00:00Z", "Score": 4321, "Accuracy": 0.9,
                                "BestStreak": 40, "Hits": 90, "Misses": 10, "TotalSounds": 100 } ]
              }
            }
            """);

        var service = NewService();
        var legacy = service.GetProfile("song", "lead");
        var after = service.Record("song", "lead", Run(100, LiveDetectorMode.Unknown));

        Assert.Equal(4321, legacy.BestScore);
        Assert.Null(legacy.Sessions[0].Detector);
        Assert.Null(after.DetectorBests);
        Assert.Null(after.Sessions[^1].Detector);
        Assert.DoesNotContain("Detector", File.ReadAllText(StorePath), StringComparison.Ordinal);
    }

    /// <summary>The session service stamps the detector from the mode the matcher ran in.</summary>
    [Theory]
    [InlineData(true, LiveDetectorMode.Cqt)]
    [InlineData(false, LiveDetectorMode.Classic)]
    public void TheSession_RecordsWhichDetectorJudgedTheRun(bool cqt, LiveDetectorMode expected)
    {
        var arr = new Arrangement();
        arr.Levels.Add(new ChartLevel());
        arr.Levels[0].Notes.Add(new GuitarNote { Position = new FractionalPosition(2), Fret = 3 });
        List<BeatMarker> beats = [.. Enumerable.Range(0, 4).Select(i => new BeatMarker { Time = i * 500, Bpm = 120 })];
        var options = new LivePlaySessionOptions { ChartVerify = cqt ? _ => new ChartVerifyOutcome(true, 1f) : null };
        var session = new LivePlaySessionService();
        session.Start(arr, arr.Levels[0], beats, options);
        session.HandleTick(2000, isPlaying: true);

        Assert.Equal(expected, session.Stop(transportSpeed: 1.0).Detector);
    }

    public void Dispose() => BestEffortDelete.Directory(_dir);
}
