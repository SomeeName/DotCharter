using DotCharter.Services.Audio.LivePlay;
using DotCharter.Services.Gameplay.Leaderboards;
using DotCharter.Services.Gameplay.Profiles;
using DotCharter.Services.Gameplay.Stats;
using DotCharter.Tone.Infrastructure.Storage;
using DotCharter.UI.ViewModels.PlayMode;

namespace DotCharter.Tests.UI.ViewModels.PlayMode;

/// <summary>
///     ONE stored-run read per front-door refresh — counted BEHAVIOURALLY, never
///     timed. (The file also pinned one plan generation per planner gesture
///     until the practice plan was removed on 2026-09-19.)
/// </summary>
public sealed class FrontDoorLastSessionReadTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "DotCharterTests", Guid.NewGuid().ToString("N"));

    public FrontDoorLastSessionReadTests() => Directory.CreateDirectory(_dir);

    public void Dispose() => BestEffortDelete.Directory(_dir);

    // ---- the front door reads ONE run, not the whole history -----------------

    /// <summary>
    ///     <see cref="IGameplayStatsService.GetMostRecentSession" /> finds the
    ///     newest run over the whole store,
    ///     including when the newest sits in a profile that is not the
    ///     newest-played group and when it is not the last APPENDED record.
    /// </summary>
    [Fact]
    public void GetMostRecentSession_IsTheNewestRunAcrossTheWholeStore()
    {
        var store = new CountingStore();
        Add(store, "song1|Lead", new DateTime(2026, 1, 5, 0, 0, 0, DateTimeKind.Utc), 10);
        Add(store, "song1|Lead", new DateTime(2026, 1, 3, 0, 0, 0, DateTimeKind.Utc), 20);
        Add(store, "song2|Rhythm", new DateTime(2026, 1, 9, 0, 0, 0, DateTimeKind.Utc), 30);
        Add(store, "song2|Rhythm", new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc), 40);
        Add(store, "song3|Bass", new DateTime(2026, 1, 7, 0, 0, 0, DateTimeKind.Utc), 50);

        var stats = new GameplayStatsService(store);
        var newest = stats.GetMostRecentSession();

        // Not vacuous: the answer is neither the last inserted profile nor the
        // last appended record of its own profile.
        Assert.NotNull(newest);
        Assert.Equal(new DateTime(2026, 1, 9, 0, 0, 0, DateTimeKind.Utc), newest.EndedUtc);
        Assert.Equal(30, newest.Score);

        // …and it is the newest record across the whole store, by a walk
        // written independently of the service's.
        var expected = store.GetAll().Values
            .SelectMany(p => p.Sessions)
            .OrderByDescending(s => s.EndedUtc)
            .First();
        Assert.Equal(expected.EndedUtc, newest.EndedUtc);
        Assert.Equal(expected.Score, newest.Score);
    }

    [Fact]
    public void GetMostRecentSession_IsNullOnAnEmptyStore()
        => Assert.Null(new GameplayStatsService(new CountingStore()).GetMostRecentSession());

    /// <summary>
    ///     The front door asks for the ONE run. It used to project the whole
    ///     session history (measured 1.48 MB on a 200-arrangement × 6-run ×
    ///     14-section store) for figures it reads off exactly one of them; that
    ///     projection was removed with the History screen on 2026-09-19, and
    ///     this pins that the Menu entry still reads through the one-record walk.
    /// </summary>
    [Fact]
    public void TheFrontDoor_ReadsOneRun_NotTheWholeProjectedHistory()
    {
        var store = new CountingStore();
        Add(store, "song1|Lead", new DateTime(2026, 1, 5, 0, 0, 0, DateTimeKind.Utc), 10);
        var stats = new CountingStats(new GameplayStatsService(store));
        var vm = NewVm(stats).Vm;

        vm.CurrentScreen = PlayModeScreen.SongSelect;

        Assert.True(stats.MostRecentCalls > 0,
            "entering Song Select must refresh the front door's facts");
    }

    // ---- fixture -------------------------------------------------------------

    private static void Add(CountingStore store, string key, DateTime endedUtc, long score)
    {
        if (!store.Profiles.TryGetValue(key, out var profile))
            store.Profiles[key] = profile = new PracticeArrangementProfile { DisplayName = key };
        profile.Sessions.Add(new PracticeSessionRecord
        {
            EndedUtc = endedUtc, Score = score, Accuracy = 0.8, Hits = 100, Misses = 10,
            TotalSounds = 110, DurationSeconds = 120
        });
    }

    private (PlayModeViewModel Vm, CountingStore Store) NewVm(IGameplayStatsService? stats = null)
    {
        var profiles = new PlayerProfileService(FakeLocalization.English(), logger: null,
            filePath: Path.Combine(_dir, "player-profiles.json"), defaultProfileName: "Tester");
        var store = new CountingStore();
        Add(store, "id:a|Lead", DateTime.UtcNow.AddDays(-9), 1000);

        var vm = TestRigBuilders.PlayMode(profiles, stats ?? new GameplayStatsService(store),
            new LeaderboardService(profiles), new ProjectState(),
            localization: FakeLocalization.English());

        vm.SongSelect.SetLibraryForTest([PlaySongItem.FromProject("id:a", false, FakeLocalization.English()), PlaySongItem.FromProject("id:b", false, FakeLocalization.English())]);
        return (vm, store);
    }

    private sealed class CountingStore : IPracticeProfileService
    {
        public readonly Dictionary<string, PracticeArrangementProfile> Profiles = new(StringComparer.Ordinal);
        public int GetAllCalls;

        public IReadOnlyDictionary<string, PracticeArrangementProfile> GetAll()
        {
            GetAllCalls++;
            return Profiles;
        }

        public PracticeArrangementProfile GetProfile(string songKey, string arrangementName)
            => Profiles.TryGetValue(songKey + "|" + arrangementName, out var p)
                ? p
                : new PracticeArrangementProfile();

        public PracticeArrangementProfile Record(string songKey, string arrangementName,
            LivePlaySessionResult result, string? displayName = null,
            IReadOnlyList<PhraseMasteryOutcome>? phraseOutcomes = null,
            IReadOnlyList<SectionResult>? sections = null,
            IReadOnlyList<TechniqueResult>? techniques = null,
            double? chartDifficulty = null, bool failed = false)
            => new();

        public void InvalidateCache() { }
        public bool IsStoreAuthoritative => true;

        public int RekeySong(string fromSongKey, string toSongKey) => 0;
        public bool AttachTakeFile(string songKey, string arrangementName, string takeFileName) => false;
        public bool AttachReplayFile(string songKey, string arrangementName, string replayFileName) => false;
    }

    private sealed class CountingStats(IGameplayStatsService inner) : IGameplayStatsService
    {
        public int MostRecentCalls;

        public ProfileStatistics GetStatistics() => inner.GetStatistics();

        public SongPlayStats? GetSongStats(string songKey, string arrangementName)
            => inner.GetSongStats(songKey, arrangementName);

        public SongPlayStats? GetBestForSong(string songKey) => inner.GetBestForSong(songKey);

        public LastSessionFacts? GetMostRecentSession()
        {
            MostRecentCalls++;
            return inner.GetMostRecentSession();
        }
    }
}
