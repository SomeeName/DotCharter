using DotCharter.Services.Audio.LivePlay;

namespace DotCharter.Tests.Services.Audio.LivePlay;

/// <summary>
///     Coverage for the difficulty-selector Load overload: per-phrase level
///     choice, MaxDifficulty clamping and the no-phrase-data fallback.
/// </summary>
public sealed class LiveNoteMatcherLeveledLoadTests
{
    // 120 BPM => 500 ms / beat.
    private static List<BeatMarker> Beats(int count = 16, double bpm = 120)
    {
        var ms = 60000.0 / bpm;
        return [.. Enumerable.Range(0, count).Select(i => new BeatMarker { Time = i * ms, Bpm = bpm })];
    }

    private static GuitarNote Note(int beat, int fret) => new()
    {
        Position = new FractionalPosition(beat),
        Fret = fret,
        StringIndex = 0
    };

    /// <summary>Two phrases (beats 0.. and 4..), two levels. Level 0 plays
    /// fret 1 at beats 1 and 5; level 1 plays fret 3 at the same beats.</summary>
    private static Arrangement TwoPhraseTwoLevelArr()
    {
        var arr = new Arrangement { ArrangementType = ArrangementType.Lead };
        var level0 = new ChartLevel { DifficultyIndex = 0 };
        level0.Notes.Add(Note(beat: 1, fret: 1));
        level0.Notes.Add(Note(beat: 5, fret: 1));
        var level1 = new ChartLevel { DifficultyIndex = 1 };
        level1.Notes.Add(Note(beat: 1, fret: 3));
        level1.Notes.Add(Note(beat: 5, fret: 3));
        arr.Levels.Add(level0);
        arr.Levels.Add(level1);
        arr.Phrases.Add("intro");
        arr.Phrases.Add("verse");
        arr.PhraseIterations.Add(new PhraseIteration(new FractionalPosition(0), 0));
        arr.PhraseIterations.Add(new PhraseIteration(new FractionalPosition(4), 1));
        return arr;
    }

    [Fact]
    public void SelectorPicksLevelPerPhrase()
    {
        var arr = TwoPhraseTwoLevelArr();
        var matcher = new LiveNoteMatcher();

        matcher.Load(arr, Beats(), phrase => phrase.PhraseName == "intro" ? 0 : 1);

        Assert.Equal(2, matcher.Expected.Count);
        // Intro at level 0: fret 1 (E2+1 = MIDI 41) at 500 ms.
        Assert.Equal(500, matcher.Expected[0].TimeMs);
        Assert.Equal(new[] { 41 }, matcher.Expected[0].MidiNotes);
        Assert.Equal(0, matcher.Expected[0].LevelIndex);
        Assert.Equal(0, matcher.Expected[0].PhraseIterationIndex);
        // Verse at level 1: fret 3 (MIDI 43) at 2500 ms.
        Assert.Equal(2500, matcher.Expected[1].TimeMs);
        Assert.Equal(new[] { 43 }, matcher.Expected[1].MidiNotes);
        Assert.Equal(1, matcher.Expected[1].LevelIndex);
        Assert.Equal(1, matcher.Expected[1].PhraseIterationIndex);
    }

    [Fact]
    public void SelectorIsClampedToPhraseMaxDifficulty()
    {
        var arr = TwoPhraseTwoLevelArr();
        arr.PhraseInfos["intro"] = new PhraseInfo(maxDifficulty: 0, solo: false);
        var matcher = new LiveNoteMatcher();

        matcher.Load(arr, Beats(), _ => 99);

        // Intro clamps to level 0 despite the selector asking for 99;
        // verse has no PhraseInfo so it is treated as fully leveled.
        Assert.Equal(0, matcher.Expected[0].LevelIndex);
        Assert.Equal(1, matcher.Expected[1].LevelIndex);
    }

    [Fact]
    public void SelectorIsClampedToAvailableLevels()
    {
        var arr = TwoPhraseTwoLevelArr();
        var matcher = new LiveNoteMatcher();

        matcher.Load(arr, Beats(), _ => 42);

        Assert.All(matcher.Expected, s => Assert.Equal(1, s.LevelIndex));
    }

    [Fact]
    public void NegativeSelectorClampsToLevelZero()
    {
        var arr = TwoPhraseTwoLevelArr();
        var matcher = new LiveNoteMatcher();

        matcher.Load(arr, Beats(), _ => -3);

        Assert.All(matcher.Expected, s => Assert.Equal(0, s.LevelIndex));
    }

    [Fact]
    public void NoPhraseData_FallsBackToHighestLevel()
    {
        var arr = TwoPhraseTwoLevelArr();
        arr.PhraseIterations.Clear();
        var matcher = new LiveNoteMatcher();

        matcher.Load(arr, Beats(), _ => 0);

        Assert.Equal(2, matcher.Expected.Count);
        Assert.All(matcher.Expected, s =>
        {
            Assert.Equal(1, s.LevelIndex);
            Assert.Equal(-1, s.PhraseIterationIndex);
        });
    }

    [Fact]
    public void NoLevels_LoadsEmpty()
    {
        var arr = new Arrangement();
        var matcher = new LiveNoteMatcher();

        matcher.Load(arr, Beats(), _ => 0);

        Assert.Empty(matcher.Expected);
        Assert.Equal(0, matcher.Stats.TotalSounds);
    }

    [Fact]
    public void SingleLevelOverload_TagsLevelIndex()
    {
        var arr = TwoPhraseTwoLevelArr();
        var matcher = new LiveNoteMatcher();

        matcher.Load(arr, arr.Levels[1], Beats());

        Assert.Equal(2, matcher.Expected.Count);
        Assert.All(matcher.Expected, s => Assert.Equal(1, s.LevelIndex));
        Assert.Equal(2, matcher.Stats.TotalSounds);
    }

    [Fact]
    public void BuildPhraseSpans_ProducesConsecutiveSpans()
    {
        var arr = TwoPhraseTwoLevelArr();

        var spans = LiveExpectedTimeline.BuildPhraseSpans(arr, Beats());

        Assert.Equal(2, spans.Count);
        Assert.Equal("intro", spans[0].PhraseName);
        Assert.Equal(0, spans[0].StartMs);
        Assert.Equal(2000, spans[0].EndMs);
        Assert.Equal("verse", spans[1].PhraseName);
        Assert.Equal(2000, spans[1].StartMs);
        Assert.Equal(double.PositiveInfinity, spans[1].EndMs);
        Assert.Equal(1, spans[0].MaxDifficulty);
    }
}
