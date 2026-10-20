using DotCharter.Services.Audio.LivePlay;
using DotCharter.Services.Audio.LivePlay.Takes;
using DotCharter.Services.Audio.StemSeparation.Cache;
using DotCharter.Services.Gameplay.Profiles;
using DotCharter.Services.Gameplay.Stats;
using DotCharter.Tone.Infrastructure.Storage;

namespace DotCharter.Tests.Services.Gameplay;

/// <summary>
///     The practice calendar's goals and the adaptive practice plan were removed
///     on 2026-09-19 (restore from tag <c>pre-cut-2026-09</c>). Their stores lived
///     beside the kept data in each profile's folder — <c>practice-goals.json</c>
///     and <c>practice-plan.json</c> — and nothing migrates or deletes them, so an
///     upgraded install still carries both. This pins that such a folder loads:
///     the profile, its per-song bests and its newest run all come back, and
///     the leftover files are left alone rather than overwritten.
///     <para>
///         The History and Trends screens went the same day, and with them the
///         take library (star / rename / re-amp). Their data is NOT a separate
///         file — it lives on the session records in <c>practice-profiles.json</c>
///         and as audio under <c>takes/</c> — so the second case pins a store
///         written by that UI: it loads, the per-song stats read from it, the
///         take metadata survives a re-save, and the take sweep deletes none of
///         the player's recordings.
///     </para>
/// </summary>
public sealed class RemovedPracticeDataCompatibilityTests : IDisposable
{
    private const string LegacyGoals =
        """{"WeeklyMinutesTarget":210,"StreakDaysTarget":5}""";

    private const string LegacyPlan =
        """
        {"Preferences":{"SessionMinutes":45,"HorizonDays":7,"ActiveDaysMask":127,
        "IncludeWarmup":true,"IncludeTechnique":true,"IncludeRepertoire":true,
        "IncludeProgression":true,"PreferredGenres":["Metal"],"PreferredSongs":[],
        "LeanOnFavourites":true,"ExcludedSongs":[]},
        "ManualEntries":[{"Id":"a","Identity":"lib:Song","DisplayName":"Alt picking",
        "EstimatedMinutes":20}],"Seed":3}
        """;

    private readonly string _dir = Path.Combine(Path.GetTempPath(), "DotCharterTests", Guid.NewGuid().ToString("N"));

    [Fact]
    public void AProfileFolder_WithTheRemovedStoresInIt_StillLoadsItsKeptData()
    {
        var profilesFile = Path.Combine(_dir, "player-profiles.json");
        var legacyScores = Path.Combine(_dir, "legacy.json");

        // An install from before the cut: a named profile with one recorded run…
        var profiles = new PlayerProfileService(FakeLocalization.English(), logger: null, filePath: profilesFile, defaultProfileName: "Ann");
        profiles.Rename(profiles.ActiveProfile.Id, "Ann");
        var store = new PracticeProfileService(profiles, logger: null, legacyFilePath: legacyScores);
        store.Record("lib:Song", "Lead", new LivePlaySessionResult(1000, 0.9, 90, 10, 100, 20, 120, 100, 1.0),
            displayName: "Artist - Song");
        var profileId = profiles.ActiveProfile.Id;

        // …and the two stores the removed features wrote, in the same folder.
        var dataDir = profiles.GetDataDirectory(profileId);
        var goalsPath = Path.Combine(dataDir, "practice-goals.json");
        var planPath = Path.Combine(dataDir, "practice-plan.json");
        File.WriteAllText(goalsPath, LegacyGoals);
        File.WriteAllText(planPath, LegacyPlan);

        // A fresh start of the upgraded app over that folder.
        var reopenedProfiles = new PlayerProfileService(FakeLocalization.English(), logger: null, filePath: profilesFile, defaultProfileName: "X");
        var reopenedStore = new PracticeProfileService(reopenedProfiles, logger: null, legacyFilePath: legacyScores);
        var stats = new GameplayStatsService(reopenedStore);

        Assert.Equal(profileId, reopenedProfiles.ActiveProfile.Id);
        Assert.Equal("Ann", reopenedProfiles.ActiveProfile.Name);

        var best = stats.GetBestForSong("lib:Song");
        Assert.NotNull(best);
        Assert.Equal(1000, best!.BestScore);

        var newest = stats.GetMostRecentSession();
        Assert.NotNull(newest);
        Assert.Equal(1000, newest!.Score);

        // Nothing reads, rewrites or deletes the orphaned files.
        Assert.Equal(LegacyGoals, File.ReadAllText(goalsPath));
        Assert.Equal(LegacyPlan, File.ReadAllText(planPath));
    }

    /// <summary>A practice store exactly as the pre-cut app wrote it: two runs
    /// of one arrangement, the newer one carrying a starred, renamed take with
    /// a re-amp render and a per-section breakdown with bar ranges.</summary>
    private const string LegacyStoreWithTakes =
        """
        {
          "lib:Song|Lead": {
            "DisplayName": "Artist - Song",
            "BestScore": 5200,
            "BestAccuracy": 0.93,
            "BestStreak": 41,
            "MasteryPercent": 72.5,
            "FullComboAchieved": false,
            "Sessions": [
              { "EndedUtc": "2026-08-01T18:00:00Z", "Score": 3100, "Accuracy": 0.81,
                "BestStreak": 20, "Hits": 81, "Misses": 19, "TotalSounds": 100,
                "SpeedPercent": 80, "DurationSeconds": 190,
                "TakeFile": "take-20260801-180000.opus" },
              { "EndedUtc": "2026-09-10T20:30:00Z", "Score": 5200, "Accuracy": 0.93,
                "BestStreak": 41, "Hits": 93, "Misses": 7, "TotalSounds": 100,
                "SpeedPercent": 100, "DurationSeconds": 200,
                "TakeFile": "take-20260910-203000.opus",
                "TakeStarred": true,
                "TakeLabel": "best solo yet",
                "ReampFiles": [ "take-20260910-203000-reamp-clean.wav" ],
                "Sections": [
                  { "Name": "Verse", "StartMs": 0, "EndMs": 30000, "Hits": 40, "Misses": 2,
                    "StartBar": 1, "EndBar": 16 },
                  { "Name": "Solo", "StartMs": 30000, "EndMs": 60000, "Hits": 53, "Misses": 5,
                    "StartBar": 17, "EndBar": 32 }
                ] }
            ]
          }
        }
        """;

    [Fact]
    public async Task AStoreWrittenByTheTakeLibrary_StillLoads_AndNoTakeIsDeleted()
    {
        var profilesFile = Path.Combine(_dir, "player-profiles.json");
        var legacyScores = Path.Combine(_dir, "legacy.json");

        var profiles = new PlayerProfileService(FakeLocalization.English(), logger: null, filePath: profilesFile, defaultProfileName: "Ann");
        var profileId = profiles.ActiveProfile.Id;
        var dataDir = profiles.GetDataDirectory(profileId);
        Directory.CreateDirectory(dataDir);
        File.WriteAllText(Path.Combine(dataDir, PracticeProfileService.StoreFileName), LegacyStoreWithTakes);

        // The recordings the take library managed, plus a render of each kind.
        var takesDir = Path.Combine(dataDir, LiveTakeStore.TakesDirectoryName);
        Directory.CreateDirectory(takesDir);
        string[] recordings =
        [
            "take-20260801-180000.opus", "take-20260910-203000.opus",
            "take-20260910-203000-reamp-clean.wav"
        ];
        foreach (var name in recordings)
            File.WriteAllBytes(Path.Combine(takesDir, name), [1, 2, 3, 4]);

        // A fresh start of the upgraded app over that folder.
        var reopenedProfiles = new PlayerProfileService(FakeLocalization.English(), logger: null, filePath: profilesFile, defaultProfileName: "X");
        var store = new PracticeProfileService(reopenedProfiles, logger: null, legacyFilePath: legacyScores);
        var stats = new GameplayStatsService(store);

        // The per-song stats the library and Results show still read from it.
        var song = stats.GetSongStats("lib:Song", "Lead");
        Assert.NotNull(song);
        Assert.Equal(2, song!.SessionCount);
        Assert.Equal(5200, song.BestScore);
        Assert.Equal(5200, stats.GetBestForSong("lib:Song")!.BestScore);
        Assert.Equal(5200, stats.GetMostRecentSession()!.Score);

        // Recording a new run re-saves the store; the old take metadata rides
        // along untouched rather than being dropped as an unknown field.
        store.Record("lib:Song", "Lead", new LivePlaySessionResult(900, 0.6, 12, 8, 20, 6, 30, 100, 0.5));
        var reread = new PracticeProfileService(reopenedProfiles, logger: null, legacyFilePath: legacyScores)
            .GetProfile("lib:Song", "Lead");
        Assert.Equal(3, reread.Sessions.Count);
        var kept = reread.Sessions.Single(s => s.TakeFile == "take-20260910-203000.opus");
        Assert.True(kept.TakeStarred);
        Assert.Equal("best solo yet", kept.TakeLabel);
        Assert.Equal(["take-20260910-203000-reamp-clean.wav"], kept.ReampFiles!);
        Assert.Equal(2, kept.Sections!.Count);
        Assert.Equal(5200, reread.BestScore);

        // The sweep that runs after every run deletes what the store does not
        // reference — and every one of these is still referenced.
        var takes = new LiveTakeStore(takesDir, new NoEncoder(), () => new AppConfig(), store);
        await takes.CleanupAsync();
        foreach (var name in recordings)
            Assert.True(File.Exists(Path.Combine(takesDir, name)), name);
    }

    private sealed class NoEncoder : IStemEncoder
    {
        public StemCodec PreferredCodec => StemCodec.Wav;
        public bool IsExternalEncoderAvailable => false;

        public Task<string> EncodeAsync(string wavPath, string destPathWithoutExtension, StemCodec codec,
            CancellationToken ct = default) => throw new NotSupportedException();

        public Task<string> DecodeToWavAsync(string encodedPath, StemCodec codec, string destWavPath,
            CancellationToken ct = default) => throw new NotSupportedException();
    }

    public void Dispose() => BestEffortDelete.Directory(_dir);
}
