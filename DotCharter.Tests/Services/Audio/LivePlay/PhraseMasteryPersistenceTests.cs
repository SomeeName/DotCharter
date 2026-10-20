using DotCharter.Services.Audio.LivePlay;
using DotCharter.Tone.Infrastructure.Storage;

namespace DotCharter.Tests.Services.Audio.LivePlay;

/// <summary>
///     0.21 persistent per-phrase mastery: the DynamicDifficultyEngine's
///     ladder state survives sessions via the practice store (level resumes,
///     accuracy joins a decayed average), instead of dying in Reset().
/// </summary>
public sealed class PhraseMasteryPersistenceTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "DotCharterTests", Guid.NewGuid().ToString("N"));

    private string StorePath => Path.Combine(_dir, "practice-profiles.json");

    private PracticeProfileService NewService() => new(logger: null, filePath: StorePath);

    private static LivePhraseSpan Span(string name, int maxDifficulty = 5, int iteration = 0)
        => new(iteration, name, 0, 1000, maxDifficulty);

    private static LivePlaySessionResult SomeResult(long score = 1000)
        => new(score, 0.8, 8, 2, 20, 5, 60, 100, 0.5);

    // ---- engine seams -------------------------------------------------------

    [Fact]
    public void Seed_ResumesThePersistedLadder()
    {
        var engine = new DynamicDifficultyEngine();
        engine.Seed(new Dictionary<string, int> { ["verse"] = 3, ["chorus"] = 1 });

        Assert.Equal(3, engine.LevelFor(Span("verse")));
        Assert.Equal(1, engine.LevelFor(Span("chorus")));
        Assert.Equal(0, engine.LevelFor(Span("bridge"))); // unseeded: fresh
    }

    [Fact]
    public void Seed_IsStillClampedToThePhraseCeiling()
    {
        var engine = new DynamicDifficultyEngine();
        engine.Seed(new Dictionary<string, int> { ["verse"] = 9 });
        Assert.Equal(2, engine.LevelFor(Span("verse", maxDifficulty: 2)));
    }

    [Fact]
    public void SnapshotOutcomes_AggregatesTalliesAndFinalLevels_SortedByName()
    {
        var engine = new DynamicDifficultyEngine();
        engine.CompletePhrase(Span("verse"), hits: 10, misses: 0);              // levels up to 1
        engine.CompletePhrase(Span("verse", iteration: 1), hits: 6, misses: 4); // 0.6: holds
        engine.CompletePhrase(Span("chorus"), hits: 1, misses: 9);              // stays 0 (floor)
        engine.CompletePhrase(Span("rest"), hits: 0, misses: 0);                // never tallied

        var outcomes = engine.SnapshotOutcomes();

        Assert.Equal(2, outcomes.Count);
        Assert.Equal("chorus", outcomes[0].PhraseName); // ordinal sort
        Assert.Equal("verse", outcomes[1].PhraseName);
        Assert.Equal(16, outcomes[1].Hits);
        Assert.Equal(4, outcomes[1].Misses);
        Assert.Equal(1, outcomes[1].Level);
        Assert.Equal(0, outcomes[0].Level);
    }

    [Fact]
    public void SeededButUnplayedPhrase_ProducesNoOutcome()
    {
        var engine = new DynamicDifficultyEngine();
        engine.Seed(new Dictionary<string, int> { ["verse"] = 3 });
        Assert.Empty(engine.SnapshotOutcomes());
    }

    [Fact]
    public void Reset_ClearsLevelsAndTallies()
    {
        var engine = new DynamicDifficultyEngine();
        engine.Seed(new Dictionary<string, int> { ["verse"] = 3 });
        engine.CompletePhrase(Span("verse"), 10, 0);

        engine.Reset();

        Assert.Equal(0, engine.LevelFor(Span("verse")));
        Assert.Empty(engine.SnapshotOutcomes());
    }

    // ---- store folding + decay math ----------------------------------------

    [Fact]
    public void FirstSession_SetsDecayedAccuracyToTheRawAccuracy()
    {
        var service = NewService();
        var profile = service.Record("song", "lead", SomeResult(), phraseOutcomes:
            [new PhraseMasteryOutcome("verse", Level: 2, Hits: 9, Misses: 1)]);

        var record = profile.PhraseMastery["verse"];
        Assert.Equal(2, record.Level);
        Assert.Equal(0.9, record.DecayedAccuracy, 12);
        Assert.Equal(1, record.TimesPlayed);
    }

    [Fact]
    public void LaterSessions_FoldIntoTheDecayedAverage()
    {
        var service = NewService();
        service.Record("song", "lead", SomeResult(), phraseOutcomes:
            [new PhraseMasteryOutcome("verse", 1, Hits: 9, Misses: 1)]); // 0.9
        var profile = service.Record("song", "lead", SomeResult(), phraseOutcomes:
            [new PhraseMasteryOutcome("verse", 2, Hits: 5, Misses: 5)]); // 0.5

        var record = profile.PhraseMastery["verse"];
        const double alpha = PracticeProfileService.PhraseAccuracyDecayAlpha;
        Assert.Equal(alpha * 0.5 + (1 - alpha) * 0.9, record.DecayedAccuracy, 12);
        Assert.Equal(2, record.TimesPlayed);
        Assert.Equal(2, record.Level);
    }

    [Fact]
    public void LadderLevel_PersistsDownwardToo()
    {
        // Ladder STATE, not a best: a poor session lowering the level must
        // stick, or the ladder silently re-grinds from the peak next time.
        var service = NewService();
        service.Record("song", "lead", SomeResult(), phraseOutcomes:
            [new PhraseMasteryOutcome("verse", 3, 10, 0)]);
        var profile = service.Record("song", "lead", SomeResult(), phraseOutcomes:
            [new PhraseMasteryOutcome("verse", 1, 2, 8)]);

        Assert.Equal(1, profile.PhraseMastery["verse"].Level);
        // …while the bests stay governed by the score-alone invariant.
        Assert.Equal(1000, profile.BestScore);
    }

    [Fact]
    public void ZeroJudgedOutcome_NeverTouchesTheMap()
    {
        var service = NewService();
        var profile = service.Record("song", "lead", SomeResult(), phraseOutcomes:
            [new PhraseMasteryOutcome("rest", 4, 0, 0)]);
        Assert.Empty(profile.PhraseMastery);
    }

    [Fact]
    public void PhraseMastery_RoundTripsAcrossServiceInstances()
    {
        NewService().Record("song", "lead", SomeResult(), phraseOutcomes:
            [new PhraseMasteryOutcome("verse", 2, 9, 1)]);

        var reloaded = NewService().GetProfile("song", "lead");
        Assert.Equal(2, reloaded.PhraseMastery["verse"].Level);
        Assert.Equal(0.9, reloaded.PhraseMastery["verse"].DecayedAccuracy, 12);
    }

    [Fact]
    public void OldStoreWithoutPhraseMastery_LoadsEmptyMap()
    {
        Directory.CreateDirectory(_dir);
        File.WriteAllText(StorePath, """
            { "song|lead": { "BestScore": 1000, "BestAccuracy": 0.8, "BestStreak": 5,
              "MasteryPercent": 40.0, "Sessions": [
                { "EndedUtc": "2026-01-01T00:00:00Z", "Score": 1000, "Accuracy": 0.8,
                  "BestStreak": 5, "Hits": 8, "Misses": 2, "TotalSounds": 20,
                  "SpeedPercent": 100, "DurationSeconds": 60 } ] } }
            """);

        var profile = NewService().GetProfile("song", "lead");
        Assert.NotNull(profile.PhraseMastery);
        Assert.Empty(profile.PhraseMastery);
    }

    // ---- session resume (end to end through the pure session brain) ---------

    /// <summary>Two levels; phrase "riff" over beats 4..8; level 1 doubles
    /// the note count (same chart as the DD level-up session test).</summary>
    private static Arrangement TwoLevelArr()
    {
        var arr = new Arrangement { ArrangementType = ArrangementType.Lead };
        var level0 = new ChartLevel { DifficultyIndex = 0 };
        var level1 = new ChartLevel { DifficultyIndex = 1 };
        for (var beat = 4; beat < 8; beat++)
        {
            level0.Notes.Add(NoteAt(beat));
            level1.Notes.Add(NoteAt(beat));
            level1.Notes.Add(NoteAt(beat, halfBeatLater: true));
        }

        arr.Levels.Add(level0);
        arr.Levels.Add(level1);
        arr.Phrases.Add("riff");
        arr.PhraseIterations.Add(new PhraseIteration(new FractionalPosition(4), 0));
        return arr;
    }

    private static GuitarNote NoteAt(int beat, bool halfBeatLater = false) => new()
    {
        Position = new FractionalPosition(beat, halfBeatLater ? new Fraction(1, 2) : Fraction.Zero),
        Fret = 3,
        StringIndex = 0
    };

    private static List<BeatMarker> Beats(int count = 64)
        => [.. Enumerable.Range(0, count).Select(i => new BeatMarker { Time = i * 500.0, Bpm = 120 })];

    [Fact]
    public void Session_ResumesTheLadderFromInitialPhraseLevels()
    {
        var session = new LivePlaySessionService();
        session.Start(TwoLevelArr(), null, Beats(), new LivePlaySessionOptions
        {
            DynamicDifficulty = true,
            InitialPhraseLevels = new Dictionary<string, int> { ["riff"] = 1 }
        });

        // Fresh DD starts this chart with 4 sounds (level 0); the resumed
        // ladder loads level 1's 8 sounds immediately.
        Assert.Equal(8, session.Stats!.TotalSounds);
        Assert.Equal(1, session.Difficulty!.LevelFor(session.PhraseSpans[0]));
    }

    [Fact]
    public void SessionStop_ExposesPhraseOutcomesForPersistence()
    {
        var session = new LivePlaySessionService();
        session.Start(TwoLevelArr(), null, Beats(), new LivePlaySessionOptions { DynamicDifficulty = true });
        session.SyncTo(0);

        // Loop the riff (2000..4000 ms) and play the pass cleanly — the wrap
        // completes the phrase, which is what tallies it for persistence.
        session.SetLoop(2000, 4000);
        Span<LivePitch> one = [new LivePitch(43, 0f, 1f)];
        var passCompleted = false;
        for (var t = 0.0; t <= 4100 && !passCompleted; t += 50)
        {
            if (t is 2000 or 2500 or 3000 or 3500)
                session.Offer(one, [], t, LiveTechniqueFlags.None, 0f);
            passCompleted = session.HandleTick(t, isPlaying: true).PassCompleted;
        }

        Assert.True(passCompleted);
        session.Stop(1.0);

        var outcome = Assert.Single(session.LastPhraseOutcomes);
        Assert.Equal("riff", outcome.PhraseName);
        Assert.Equal(4, outcome.Hits);
        Assert.Equal(0, outcome.Misses);
        Assert.Equal(1, outcome.Level); // clean pass leveled the riff up
    }

    public void Dispose() => BestEffortDelete.Directory(_dir);
}
