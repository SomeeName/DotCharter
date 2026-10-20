using DotCharter.Services.Audio.LivePlay;
using DotCharter.Tone.Infrastructure.Storage;

namespace DotCharter.Tests.Services.Audio.LivePlay;

/// <summary>
///     #295 step 3: a never-played song's phrases start where the player usually plays, read from
///     their history as a depth up the ladder (a level only compares across songs with its
///     ladder's size), with a way back to level 0.
/// </summary>
public sealed class PlayerLadderDepthTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "DotCharterTests", "depth-" + Guid.NewGuid().ToString("N"));

    public void Dispose() => BestEffortDelete.Directory(_dir);

    private static LivePhraseSpan Span(string name, int maxDifficulty, int iteration = 0)
        => new(iteration, name, 0, 1000, maxDifficulty);

    private static PracticeArrangementProfile Profile(params PhraseMasteryRecord[] records)
    {
        var profile = new PracticeArrangementProfile();
        for (var i = 0; i < records.Length; i++) profile.PhraseMastery["p" + i] = records[i];
        return profile;
    }

    private static PhraseMasteryRecord Record(int level, int size, int timesPlayed = 2) =>
        new() { Level = level, LadderSize = size, TimesPlayed = timesPlayed };

    [Fact]
    public void Depth_is_the_mean_position_up_each_phrases_own_ladder()
    {
        // Level 7 of 8 is the top (1.0); level 6 of 25 is a quarter (0.25).
        var depth = PlayerLadderDepth.From([Profile(Record(7, 8)), Profile(Record(6, 25))]);

        Assert.Equal(0.625, depth!.Value, 6);
    }

    [Fact]
    public void Once_played_phrases_and_records_without_a_ladder_size_count_for_nothing()
    {
        var depth = PlayerLadderDepth.From([Profile(Record(4, 8), Record(7, 8, timesPlayed: 1), Record(9, 0))]);

        Assert.Equal(4 / 7.0, depth!.Value, 6);
        Assert.Null(PlayerLadderDepth.From([Profile(Record(7, 8, timesPlayed: 1), Record(9, 0))]));
        Assert.Null(PlayerLadderDepth.From([Profile(Record(0, 8))]));
    }

    [Fact]
    public void An_unseen_phrase_starts_at_the_depth_of_its_own_ladder_and_a_stored_level_wins()
    {
        var engine = new DynamicDifficultyEngine { StartingDepth = 0.5 };
        engine.Seed(new Dictionary<string, int> { ["chorus"] = 1 });

        Assert.Equal(4, engine.LevelFor(Span("verse", maxDifficulty: 7)));
        Assert.Equal(12, engine.LevelFor(Span("bridge", maxDifficulty: 24)));
        Assert.Equal(1, engine.LevelFor(Span("chorus", maxDifficulty: 7)));
    }

    [Fact]
    public void Start_at_zero_drops_the_depth_and_every_level_the_run_moved()
    {
        var engine = new DynamicDifficultyEngine { StartingDepth = 0.5 };
        engine.CompletePhrase(Span("verse", maxDifficulty: 7), hits: 10, misses: 0);
        Assert.Equal(5, engine.LevelFor(Span("verse", maxDifficulty: 7)));

        engine.StartOverAtInitialLevel();

        Assert.Equal(0, engine.LevelFor(Span("verse", maxDifficulty: 7)));
        Assert.Equal(0, engine.LevelFor(Span("chorus", maxDifficulty: 7)));
        Assert.Single(engine.SnapshotOutcomes()); // the judged tallies stay
    }

    [Fact]
    public void The_ladder_size_is_recorded_with_the_level_and_survives_the_store()
    {
        var engine = new DynamicDifficultyEngine { StartingDepth = 0.5 };
        engine.CompletePhrase(Span("verse", maxDifficulty: 7), hits: 6, misses: 4); // holds at 4
        var outcome = Assert.Single(engine.SnapshotOutcomes());
        Assert.Equal((4, 8), (outcome.Level, outcome.LadderSize));

        var store = new PracticeProfileService(logger: null, filePath: Path.Combine(_dir, "practice-profiles.json"));
        store.Record("song", "lead", new LivePlaySessionResult(1000, 0.6, 6, 4, 10, 2, 60, 100, 0.6),
            phraseOutcomes: [outcome]);

        var reread = new PracticeProfileService(logger: null, filePath: Path.Combine(_dir, "practice-profiles.json"));
        var record = reread.GetProfile("song", "lead").PhraseMastery["verse"];
        Assert.Equal((4, 8), (record.Level, record.LadderSize));
    }

    [Fact]
    public void The_session_starts_unseen_phrases_at_the_depth_and_starts_over_at_the_bottom_on_request()
    {
        var arrangement = Leveled(levels: 5);
        var beats = Enumerable.Range(0, 16).Select(i => new BeatMarker { Time = i * 500, Bpm = 120 }).ToList();
        var session = new LivePlaySessionService();
        session.Start(arrangement, null, beats, new LivePlaySessionOptions { DynamicDifficulty = true, StartingDepth = 0.5 });
        var span = LiveExpectedTimeline.BuildPhraseSpans(arrangement, beats)[0];
        Assert.Equal(2, session.Difficulty!.LevelFor(span));
        var atDepth = session.Matcher!.Stats.TotalSounds;

        Assert.True(session.StartLadderOverAtBottom(playheadMs: 0));

        Assert.Equal(0, session.Difficulty.LevelFor(span));
        Assert.True(session.Matcher.Stats.TotalSounds < atDepth, "the sounds ahead were not reloaded at level 0");
    }

    /// <summary>One phrase over 8 beats; level N has N + 1 notes.</summary>
    private static Arrangement Leveled(int levels)
    {
        var arrangement = new Arrangement { ArrangementType = ArrangementType.Lead };
        for (var l = 0; l < levels; l++)
        {
            var level = new ChartLevel { DifficultyIndex = l };
            for (var n = 0; n <= l; n++)
                level.Notes.Add(new GuitarNote { Position = new FractionalPosition(1 + n), Fret = 3 + n, StringIndex = 0 });
            arrangement.Levels.Add(level);
        }

        arrangement.Phrases.Add("riff");
        arrangement.PhraseIterations.Add(new PhraseIteration(new FractionalPosition(0), 0));
        return arrangement;
    }
}
