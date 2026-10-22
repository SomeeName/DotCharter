using DotCharter.Services.Audio.LivePlay;
using DotCharter.Services.Audio.LivePlay.Replays;
using DotCharter.Services.Audio.LivePlay.Takes;
using DotCharter.UI.ViewModels.Editor;

namespace DotCharter.Tests.UI.ViewModels.Editor;

/// <summary>
///     <see cref="LivePlayRunArchive" /> on fakes: a run's take and replay reach
///     their stores and are attached to the session record under the run's own
///     keys, a store the construction did not get is a no-op, and nothing it
///     does blocks the caller (the session gate is held while it is called).
///     The waits below are completion signals with a generous ceiling, never a
///     timing assertion.
/// </summary>
public sealed class LivePlayRunArchiveTests
{
    private static readonly TimeSpan Ceiling = TimeSpan.FromSeconds(30);

    [Fact]
    public void Without_stores_nothing_is_recorded_or_kept()
    {
        var profiles = new RecordingPracticeStore();
        var archive = new LivePlayRunArchive(profiles, new FakeAppLogger());

        Assert.False(archive.KeepsReplays);
        Assert.Null(archive.TryStartTake(48_000));

        archive.SaveReplayAsync(Replay());

        Assert.Equal(0, profiles.ReplayAttaches);
    }

    [Fact]
    public void A_take_is_started_through_the_take_store()
    {
        var takes = new FakeTakeStore();
        var archive = new LivePlayRunArchive(new RecordingPracticeStore(), new FakeAppLogger(), takeStore: takes);

        archive.TryStartTake(44_100);

        Assert.Equal([44_100], takes.StartedAt);
    }

    [Fact]
    public void A_finalized_take_is_attached_under_the_runs_keys_and_announced()
    {
        var profiles = new RecordingPracticeStore();
        var takes = new FakeTakeStore { FinalizedName = "take-1.opus" };
        var archive = new LivePlayRunArchive(profiles, new FakeAppLogger(), takeStore: takes);

        archive.FinalizeTakeAsync(Recorder(), attach: true, "song.rscp", "Lead:Lead");

        Assert.True(takes.CleanedUp.Wait(Ceiling, TestContext.Current.CancellationToken), "the take was never finalized and swept");
        Assert.Equal(("song.rscp", "Lead:Lead", "take-1.opus"), profiles.LastTakeAttach);
    }

    [Fact]
    public void An_unrecorded_runs_take_is_closed_but_never_attached()
    {
        var profiles = new RecordingPracticeStore();
        var takes = new FakeTakeStore { FinalizedName = "take-2.opus" };
        var archive = new LivePlayRunArchive(profiles, new FakeAppLogger(), takeStore: takes);

        archive.FinalizeTakeAsync(Recorder(), attach: false, string.Empty, string.Empty);

        Assert.True(takes.CleanedUp.Wait(Ceiling, TestContext.Current.CancellationToken), "the take was never finalized and swept");
        Assert.Null(profiles.LastTakeAttach);
    }

    [Fact]
    public void A_saved_replay_is_attached_under_its_own_song_and_arrangement()
    {
        var profiles = new RecordingPracticeStore();
        var replays = new FakeReplayStore { SavedName = "replay-1.json" };
        var archive = new LivePlayRunArchive(profiles, new FakeAppLogger(), replayStore: replays);

        Assert.True(archive.KeepsReplays);
        archive.SaveReplayAsync(Replay());

        Assert.True(profiles.ReplayAttached.Wait(Ceiling, TestContext.Current.CancellationToken), "the replay was never attached");
        Assert.Equal(("song.rscp", "Lead:Lead", "replay-1.json"), profiles.LastReplayAttach);
    }

    private static RunReplay Replay() => new() { SongKey = "song.rscp", ArrangementName = "Lead:Lead" };

    private static LiveTakeRecorder Recorder() => new(new MemoryStream(), "take.wav", 48_000, TimeSpan.FromSeconds(1));

    private sealed class FakeTakeStore : ILiveTakeStore
    {
        public List<int> StartedAt { get; } = [];

        public string? FinalizedName { get; init; }

        public ManualResetEventSlim CleanedUp { get; } = new();

        public bool IsEnabled => true;

        public string TakesDirectory => "takes";

        public LiveTakeRecorder? TryStartTake(int sampleRate)
        {
            StartedAt.Add(sampleRate);
            return null;
        }

        public Task<string?> FinalizeTakeAsync(LiveTakeRecorder recorder)
        {
            recorder.Dispose();
            return Task.FromResult(FinalizedName);
        }

        public Task CleanupAsync()
        {
            CleanedUp.Set();
            return Task.CompletedTask;
        }
    }

    private sealed class FakeReplayStore : IReplayStore
    {
        public string? SavedName { get; init; }

        public string ReplaysDirectory => "replays";

        public string? Save(RunReplay replay) => SavedName;

        public RunReplay? TryLoad(string fileName) => null;
    }

    /// <summary>Records the two attach calls; everything else is an empty store.</summary>
    private sealed class RecordingPracticeStore : IPracticeProfileService
    {
        private int _replayAttaches;

        public (string, string, string)? LastTakeAttach { get; private set; }

        public (string, string, string)? LastReplayAttach { get; private set; }

        public ManualResetEventSlim ReplayAttached { get; } = new();

        public int ReplayAttaches => Volatile.Read(ref _replayAttaches);

        public bool IsStoreAuthoritative => true;

        public int RekeySong(string fromSongKey, string toSongKey) => 0;

        public PracticeArrangementProfile GetProfile(string songKey, string arrangementName) => new();

        public PracticeArrangementProfile Record(string songKey, string arrangementName,
            LivePlaySessionResult result, string? displayName = null,
            IReadOnlyList<PhraseMasteryOutcome>? phraseOutcomes = null,
            IReadOnlyList<SectionResult>? sections = null,
            IReadOnlyList<TechniqueResult>? techniques = null,
            double? chartDifficulty = null, bool failed = false) => new();

        public IReadOnlyDictionary<string, PracticeArrangementProfile> GetAll() => new Dictionary<string, PracticeArrangementProfile>();

        public bool AttachTakeFile(string songKey, string arrangementName, string takeFileName)
        {
            LastTakeAttach = (songKey, arrangementName, takeFileName);
            return true;
        }

        public bool AttachReplayFile(string songKey, string arrangementName, string replayFileName)
        {
            LastReplayAttach = (songKey, arrangementName, replayFileName);
            Interlocked.Increment(ref _replayAttaches);
            ReplayAttached.Set();
            return true;
        }

        public void InvalidateCache()
        {
        }
    }
}
