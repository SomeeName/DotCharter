using DotCharter.Services.Audio.LivePlay;

namespace DotCharter.Tests.Services.Audio.LivePlay;

/// <summary>
///     A dynamic-difficulty level change must not re-level the phrase
///     iterations the player has ALREADY played.
///     <para>
///         <c>DynamicDifficultyEngine</c> keys levels by phrase NAME, and
///         <c>LiveNoteMatcher.Reload</c> rebuilt EVERY span at the phrase's
///         current level — so the iteration whose own accuracy moved the ladder
///         was rebuilt by that move too. Levelling UP put notes BEHIND the
///         cursor, which can never be judged (the total grows, coverage falls, a
///         full combo becomes unreachable). Levelling DOWN deleted notes the
///         player had already HIT, so the hit count and the score went DOWN
///         mid-run.
///     </para>
/// </summary>
public sealed class DynamicDifficultyReloadTests
{
    // 120 BPM ⇒ 500 ms a beat.
    private static List<BeatMarker> Beats(int count = 24, double bpm = 120)
    {
        var ms = 60000.0 / bpm;
        return [.. Enumerable.Range(0, count).Select(i => new BeatMarker { Time = i * ms, Bpm = bpm })];
    }

    private static GuitarNote Note(int beat, int fret)
        => new() { Position = new FractionalPosition(beat), Fret = fret, StringIndex = 0 };

    /// <summary>
    ///     One phrase name ("riff") played twice — iteration 0 at beat 0, iteration
    ///     1 at beat 8. Level 0 has ONE note per iteration; level 1 has TWO.
    /// </summary>
    private static Arrangement RepeatedRiff()
    {
        var arr = new Arrangement { ArrangementType = ArrangementType.Lead };

        var level0 = new ChartLevel { DifficultyIndex = 0 };
        level0.Notes.Add(Note(beat: 1, fret: 1));
        level0.Notes.Add(Note(beat: 9, fret: 1));

        var level1 = new ChartLevel { DifficultyIndex = 1 };
        level1.Notes.Add(Note(beat: 1, fret: 1));
        level1.Notes.Add(Note(beat: 2, fret: 3)); // the note level 1 adds
        level1.Notes.Add(Note(beat: 9, fret: 1));
        level1.Notes.Add(Note(beat: 10, fret: 3));

        arr.Levels.Add(level0);
        arr.Levels.Add(level1);
        arr.Phrases.Add("riff");
        arr.PhraseIterations.Add(new PhraseIteration(new FractionalPosition(0), 0));
        arr.PhraseIterations.Add(new PhraseIteration(new FractionalPosition(8), 0));
        return arr;
    }

    /// <summary>Plays iteration 0 clean at level 0 and stops with the playhead
    /// well past it (beat 8 = 4000 ms).</summary>
    private static LiveNoteMatcher PlayedFirstIterationAtLevel0(Arrangement arr)
    {
        var matcher = new LiveNoteMatcher();
        matcher.Load(arr, Beats(), _ => 0);
        matcher.OfferNote(midi: 41, timeMs: 500); // beat 1, fret 1
        matcher.AdvanceTo(4000);
        Assert.Equal(1, matcher.Stats.Hits);
        Assert.Equal(0, matcher.Stats.Misses);
        return matcher;
    }

    [Fact]
    public void LevellingUp_DoesNotAddNotesBehindTheCursor()
    {
        var arr = RepeatedRiff();
        var matcher = PlayedFirstIterationAtLevel0(arr);
        var scoreBefore = matcher.Stats.Score;

        matcher.Reload(arr, Beats(), _ => 1, playheadMs: 4000);

        // Iteration 0 keeps the one note it was played with; iteration 1 gets two.
        Assert.Equal(3, matcher.Stats.TotalSounds);
        Assert.Equal(1, matcher.Stats.Hits);
        Assert.Equal(0, matcher.Stats.Misses);
        Assert.Equal(scoreBefore, matcher.Stats.Score);
        Assert.All(matcher.Expected.Where(s => s.PhraseIterationIndex == 0),
            s => Assert.Equal(0, s.LevelIndex));
        Assert.All(matcher.Expected.Where(s => s.PhraseIterationIndex == 1),
            s => Assert.Equal(1, s.LevelIndex));
    }

    [Fact]
    public void LevellingDown_NeverTakesBackAHitOrItsScore()
    {
        var arr = RepeatedRiff();
        var matcher = new LiveNoteMatcher();
        matcher.Load(arr, Beats(), _ => 1);
        matcher.OfferNote(midi: 41, timeMs: 500);  // beat 1
        matcher.OfferNote(midi: 43, timeMs: 1000); // beat 2 — the level-1 note
        matcher.AdvanceTo(4000);
        Assert.Equal(2, matcher.Stats.Hits);
        var scoreBefore = matcher.Stats.Score;

        matcher.Reload(arr, Beats(), _ => 0, playheadMs: 4000);

        Assert.Equal(2, matcher.Stats.Hits);
        Assert.Equal(scoreBefore, matcher.Stats.Score);
        // …and the run's own denominator did not shrink under the played half.
        Assert.Equal(3, matcher.Stats.TotalSounds); // 2 played + 1 for the reloaded iteration
    }

    /// <summary>
    ///     The half the reload exists FOR: an iteration the playhead has not
    ///     reached still moves.
    /// </summary>
    [Fact]
    public void AnUnplayedIterationStillPicksUpTheNewLevel()
    {
        var arr = RepeatedRiff();
        var matcher = PlayedFirstIterationAtLevel0(arr);

        matcher.Reload(arr, Beats(), _ => 1, playheadMs: 4000);

        var second = matcher.Expected.Where(s => s.PhraseIterationIndex == 1).ToList();
        Assert.Equal(2, second.Count);
        Assert.All(second, s => Assert.Equal(1, s.LevelIndex));
    }

    /// <summary>
    ///     Nothing played yet ⇒ nothing frozen: a reload before the first note
    ///     re-levels the whole chart, which is what a seed/resume needs.
    /// </summary>
    [Fact]
    public void WithNothingBehindTheHorizon_EveryIterationIsStillFree()
    {
        var arr = RepeatedRiff();
        var matcher = new LiveNoteMatcher();
        matcher.Load(arr, Beats(), _ => 0);

        matcher.Reload(arr, Beats(), _ => 1, playheadMs: 0);

        Assert.Equal(4, matcher.Stats.TotalSounds);
        Assert.All(matcher.Expected, s => Assert.Equal(1, s.LevelIndex));
    }
}
