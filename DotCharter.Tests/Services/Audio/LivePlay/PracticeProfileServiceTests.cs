using DotCharter.Services.Audio.LivePlay;
using DotCharter.Tone.Infrastructure.Storage;

namespace DotCharter.Tests.Services.Audio.LivePlay;

public sealed class PracticeProfileServiceTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "DotCharterTests", Guid.NewGuid().ToString("N"));

    private string StorePath => Path.Combine(_dir, "practice-profiles.json");

    private PracticeProfileService NewService() => new(logger: null, filePath: StorePath);

    private static LivePlaySessionResult Result(long score = 1000, double accuracy = 0.8,
        int hits = 8, int misses = 2, int total = 20, int bestStreak = 5,
        double speedPercent = 100, double duration = 60)
        => new(score, accuracy, hits, misses, total, bestStreak, duration, speedPercent, (hits + misses) / (double)total);

    [Fact]
    public void UnplayedSong_ReturnsEmptyProfile()
    {
        var profile = NewService().GetProfile("song", "Lead:Guitar 1");
        Assert.Equal(0, profile.BestScore);
        Assert.Empty(profile.Sessions);
    }

    [Fact]
    public void Record_TracksBestsAndHistory()
    {
        var service = NewService();
        service.Record("song", "lead", Result(score: 1000, accuracy: 0.8, bestStreak: 5));
        var profile = service.Record("song", "lead", Result(score: 800, accuracy: 0.9, bestStreak: 12));

        Assert.Equal(1000, profile.BestScore);      // first run scored higher
        Assert.Equal(0.9, profile.BestAccuracy, 3); // second was more accurate
        Assert.Equal(12, profile.BestStreak);
        Assert.Equal(2, profile.Sessions.Count);
    }

    [Fact]
    public void WorseRun_DoesNotLowerStoredBests_ButStillRecordsHistory()
    {
        var service = NewService();
        service.Record("song", "lead", Result(score: 5000, accuracy: 0.95, bestStreak: 30));

        // A clearly worse run in every dimension must NOT replace the stored best…
        var profile = service.Record("song", "lead", Result(score: 2000, accuracy: 0.60, bestStreak: 5));
        Assert.Equal(5000, profile.BestScore);
        Assert.Equal(0.95, profile.BestAccuracy, 3);
        Assert.Equal(30, profile.BestStreak);

        // …but the attempt is still recorded as history (losing it would be a regression).
        Assert.Equal(2, profile.Sessions.Count);
        Assert.Equal(2000, profile.Sessions[^1].Score);
    }

    [Fact]
    public void AShortRun_CannotSetTheBestAccuracy()
    {
        // Two notes played cleanly and quit used to store BestAccuracy = 1.0
        // forever — a permanent S grade in three places, the top local
        // leaderboard row, "cleared" in the skill estimator and a published
        // online score. Accuracy is Hits/(Hits+Misses) over JUDGED sounds only,
        // so it says nothing about how much of the song was played.
        var service = NewService();

        var profile = service.Record("song", "lead",
            Result(score: 200, accuracy: 1.0, hits: 2, misses: 0, total: 60));

        Assert.Equal(0.0, profile.BestAccuracy, 3);
        Assert.Single(profile.Sessions); // still history, and still folds into mastery
        Assert.True(profile.MasteryPercent > 0);
    }

    [Fact]
    public void AShortRun_NeverLowersAnEstablishedBestAccuracy()
    {
        var service = NewService();
        service.Record("song", "lead", Result(score: 5000, accuracy: 0.95, hits: 57, misses: 3, total: 60));

        var profile = service.Record("song", "lead",
            Result(score: 100, accuracy: 1.0, hits: 2, misses: 0, total: 60));

        Assert.Equal(0.95, profile.BestAccuracy, 3);
    }

    [Fact]
    public void ARunCoveringHalfTheChart_MaySetTheBestAccuracy()
    {
        // The floor is MinCoverageForBestAccuracy, inclusive.
        var service = NewService();

        var profile = service.Record("song", "lead",
            Result(score: 3000, accuracy: 0.9, hits: 27, misses: 3, total: 60));

        Assert.Equal(0.9, profile.BestAccuracy, 3);
    }

    [Fact]
    public void Record_PersistsAcrossServiceInstances()
    {
        NewService().Record("song", "lead", Result());

        var reloaded = NewService().GetProfile("song", "lead");
        Assert.Single(reloaded.Sessions);
        Assert.Equal(1000, reloaded.BestScore);
        Assert.True(File.Exists(StorePath));
    }

    [Fact]
    public void EmptySession_IsIgnored()
    {
        var service = NewService();
        var profile = service.Record("song", "lead", Result(hits: 0, misses: 0, score: 0));
        Assert.Empty(profile.Sessions);
        Assert.False(File.Exists(StorePath));
    }

    [Fact]
    public void Mastery_ScalesWithAccuracyCoverageAndSpeed()
    {
        var service = NewService();

        // Half the song judged, 100% accurate, at 60% speed => 30 mastery.
        var profile = service.Record("song", "lead",
            Result(accuracy: 1.0, hits: 10, misses: 0, total: 20, speedPercent: 60));
        Assert.Equal(30.0, profile.MasteryPercent, 1);

        // A full clean run at 100% speed lifts it to 100; mastery never drops.
        profile = service.Record("song", "lead",
            Result(accuracy: 1.0, hits: 20, misses: 0, total: 20, speedPercent: 100));
        Assert.Equal(100.0, profile.MasteryPercent, 1);

        profile = service.Record("song", "lead",
            Result(accuracy: 0.5, hits: 5, misses: 5, total: 20, speedPercent: 50));
        Assert.Equal(100.0, profile.MasteryPercent, 1);
    }

    [Fact]
    public void ProfilesAreKeyedPerArrangement()
    {
        var service = NewService();
        service.Record("song", "lead", Result(score: 500));
        service.Record("song", "bass", Result(score: 900));

        Assert.Equal(500, service.GetProfile("song", "lead").BestScore);
        Assert.Equal(900, service.GetProfile("song", "bass").BestScore);
    }

    [Fact]
    public void AttachTakeFile_AttachesToMostRecentSession_AndPersists()
    {
        var service = NewService();
        service.Record("song", "lead", Result(score: 100));
        service.Record("song", "lead", Result(score: 200));

        Assert.True(service.AttachTakeFile("song", "lead", "take-1.opus"));

        var reloaded = NewService().GetProfile("song", "lead");
        Assert.Null(reloaded.Sessions[0].TakeFile);              // older run untouched
        Assert.Equal("take-1.opus", reloaded.Sessions[^1].TakeFile);
    }

    [Fact]
    public void AttachTakeFile_WithoutSessions_ReturnsFalse() => Assert.False(NewService().AttachTakeFile("song", "lead", "take-1.wav"));

    [Fact]
    public void AttachTakeFile_NeverOverwritesAnExistingTake()
    {
        var service = NewService();
        service.Record("song", "lead", Result());
        Assert.True(service.AttachTakeFile("song", "lead", "first.wav"));

        Assert.False(service.AttachTakeFile("song", "lead", "second.wav"));
        Assert.Equal("first.wav", service.GetProfile("song", "lead").Sessions[^1].TakeFile);
    }

    [Fact]
    public void CorruptStore_StartsFresh()
    {
        Directory.CreateDirectory(_dir);
        File.WriteAllText(StorePath, "{ not json !!!");

        var profile = NewService().Record("song", "lead", Result());
        Assert.Single(profile.Sessions);
    }

    [Fact]
    public void CorruptStore_IsQuarantinedRatherThanOverwritten()
    {
        // "Unparseable" is unrecoverable, but the file is still the only copy of
        // whatever the player had — it gets moved aside, not silently replaced.
        Directory.CreateDirectory(_dir);
        File.WriteAllText(StorePath, "{ not json !!!");

        NewService().Record("song", "lead", Result(score: 500));

        Assert.Equal(500, PracticeProfileService.ReadStoreFile(StorePath)["song|lead"].BestScore);
        var quarantined = Assert.Single(Directory.GetFiles(_dir, "practice-profiles.corrupt-*.json"));
        Assert.Equal("{ not json !!!", File.ReadAllText(quarantined));
    }

    [Fact]
    public void AStoreThatCouldNotBeRead_IsNeverWrittenOver()
    {
        // A momentary lock (antivirus scan, cloud sync, backup tool) is NOT a
        // corrupt store. Treating the two alike made the empty read
        // authoritative and the next run atomically replaced the player's
        // entire history with a single entry.
        Directory.CreateDirectory(_dir);
        NewService().Record("song", "lead", Result(score: 9000, bestStreak: 30));

        var service = NewService();
        using (new FileStream(StorePath, FileMode.Open, FileAccess.Read, FileShare.None))
        {
            // The read fails while the file is held.
            Assert.Equal(0, service.GetProfile("song", "lead").BestScore);
        }

        // Lock gone. The next run must re-read rather than flatten the history.
        service.Record("song", "lead", Result(score: 10, bestStreak: 1));

        var onDisk = PracticeProfileService.ReadStoreFile(StorePath)["song|lead"];
        Assert.Equal(9000, onDisk.BestScore);
        Assert.Equal(30, onDisk.BestStreak);
        Assert.Equal(2, onDisk.Sessions.Count);
    }

    [Fact]
    public void WhileTheStoreCannotBeRead_NothingIsPersisted()
    {
        Directory.CreateDirectory(_dir);
        NewService().Record("song", "lead", Result(score: 9000));
        var before = File.ReadAllText(StorePath);

        var service = NewService();
        using (new FileStream(StorePath, FileMode.Open, FileAccess.Read, FileShare.None))
        {
            service.Record("song", "lead", Result(score: 10));
        }

        Assert.Equal(before, File.ReadAllText(StorePath));
    }

    /// <summary>
    ///     The stored session list comes back CHRONOLOGICAL, whatever order the
    ///     file is in.
    ///     <para>
    ///         Readers treat that order as a fact without enforcing it:
    ///         <c>Sessions[^1]</c> is "last played" in <c>GameplayStatsService</c>
    ///         (three methods), and <see cref="PracticeProfileService.Record" />'s
    ///         200-session cap trims with <c>RemoveAt(0)</c> — it deletes whatever
    ///         is FIRST. Appending <c>DateTime.UtcNow</c> keeps that true only
    ///         while the clock moves forward; a backwards NTP correction, a manual
    ///         clock fix or a store restored from a machine whose clock ran ahead
    ///         leaves an older stamp after a newer one, and then "last played" is a
    ///         date from months ago and the cap deletes the NEWEST run.
    ///     </para>
    /// </summary>
    [Fact]
    public void AnOutOfOrderStore_LoadsChronologically()
    {
        WriteStore(
            "{ \"EndedUtc\": \"2026-03-04T00:00:00Z\", \"Hits\": 9, \"Misses\": 1, \"TotalSounds\": 10 }",
            "{ \"EndedUtc\": \"2026-01-01T00:00:00Z\", \"Hits\": 5, \"Misses\": 5, \"TotalSounds\": 10 }",
            "{ \"EndedUtc\": \"2026-02-02T00:00:00Z\", \"Hits\": 7, \"Misses\": 3, \"TotalSounds\": 10 }");

        var sessions = NewService().GetProfile("song", "lead").Sessions;

        Assert.Equal(
            [Utc(1), Utc(2), Utc(3)],
            sessions.Select(x => x.EndedUtc.ToUniversalTime()).ToArray());
    }

    /// <summary>…and the whole-store snapshot every read-model is handed is that
    /// same list, so <c>Sessions[^1]</c> — "last played" on the stats page, the
    /// song rows and the Trends picker — is the newest run rather than the last
    /// line of the file.</summary>
    [Fact]
    public void TheStoreSnapshot_PutsTheNewestRunLast()
    {
        WriteStore(
            "{ \"EndedUtc\": \"2026-03-04T00:00:00Z\", \"Hits\": 9, \"Misses\": 1, \"TotalSounds\": 10 }",
            "{ \"EndedUtc\": \"2026-01-01T00:00:00Z\", \"Hits\": 5, \"Misses\": 5, \"TotalSounds\": 10 }");

        var all = NewService().GetAll();

        Assert.Equal(Utc(3), all["song|lead"].Sessions[^1].EndedUtc.ToUniversalTime());
    }

    /// <summary>The cross-profile read (the local leaderboard's path — it never
    /// disturbs the active store's cache) orders the same way.</summary>
    [Fact]
    public void ReadStoreFile_OrdersSessionsToo()
    {
        WriteStore(
            "{ \"EndedUtc\": \"2026-03-04T00:00:00Z\", \"Hits\": 9, \"Misses\": 1, \"TotalSounds\": 10 }",
            "{ \"EndedUtc\": \"2026-01-01T00:00:00Z\", \"Hits\": 5, \"Misses\": 5, \"TotalSounds\": 10 }");

        var read = PracticeProfileService.ReadStoreFile(StorePath);

        Assert.Equal(Utc(1), read["song|lead"].Sessions[0].EndedUtc.ToUniversalTime());
    }

    /// <summary>The three stamps the ordering cases use: 1 = January,
    /// 2 = February, 3 = March, all 2026.</summary>
    private static DateTime Utc(int which) => which switch
    {
        1 => new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc),
        2 => new DateTime(2026, 2, 2, 0, 0, 0, DateTimeKind.Utc),
        _ => new DateTime(2026, 3, 4, 0, 0, 0, DateTimeKind.Utc)
    };

    /// <summary>Writes a store file whose one arrangement holds
    /// <paramref name="sessionsJson" /> in exactly the given order.</summary>
    private void WriteStore(params string[] sessionsJson)
    {
        Directory.CreateDirectory(_dir);
        File.WriteAllText(StorePath,
            "{ \"song|lead\": { \"DisplayName\": \"Song\", \"Sessions\": [" +
            string.Join(",", sessionsJson) + "] } }");
    }

    public void Dispose() => BestEffortDelete.Directory(_dir);
}
