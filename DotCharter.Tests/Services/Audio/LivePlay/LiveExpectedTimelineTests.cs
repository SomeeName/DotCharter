using DotCharter.Services.Audio.LivePlay;

namespace DotCharter.Tests.Services.Audio.LivePlay;

/// <summary>
///     <see cref="LiveExpectedTimeline" /> on its own, without a matcher: what one small
///     arrangement becomes as expected sounds, and how a leveled build picks its levels per
///     phrase. The matcher-level behaviour on top (stats, cursor, reload carry-over) stays
///     in the <c>LiveNoteMatcher*</c> tests.
/// </summary>
public sealed class LiveExpectedTimelineTests
{
    // 120 BPM => 500 ms / beat.
    private static List<BeatMarker> Beats(int count = 16, double bpm = 120)
    {
        var ms = 60000.0 / bpm;
        return [.. Enumerable.Range(0, count).Select(i => new BeatMarker { Time = i * ms, Bpm = bpm })];
    }

    /// <summary>One level, written OUT of time order: a bent A-string note at beat 3, a
    /// palm-muted low-E slide at beat 1, an ignored note at beat 2, a G5 power chord with
    /// its own notes at beat 4, and the same G5 named only by its template at beat 5.</summary>
    private static Arrangement SmallArrangement()
    {
        var arr = new Arrangement { ArrangementType = ArrangementType.Lead };
        arr.ChordTemplates.Add(new ChordTemplate
        {
            Id = 7,
            Name = "G5",
            Frets = new Dictionary<int, int> { [0] = 3, [1] = 5 }
        });

        var level = new ChartLevel();
        var bent = new GuitarNote { Position = new FractionalPosition(3), StringIndex = 1, Fret = 7 };
        bent.BendValues.Add(new BendValue(new FractionalPosition(3), 1.5));
        level.Notes.Add(bent);
        level.Notes.Add(new GuitarNote
        {
            Position = new FractionalPosition(1),
            StringIndex = 0,
            Fret = 2,
            SlideTo = 5,
            Mute = MuteType.Palm
        });
        level.Notes.Add(new GuitarNote { Position = new FractionalPosition(2), StringIndex = 0, Fret = 3, Ignore = true });
        var ownNotes = new Chord { Position = new FractionalPosition(4), TemplateId = 7 };
        ownNotes.ChordNotes.Add(new ChordNote { StringIndex = 0, Fret = 3 });
        ownNotes.ChordNotes.Add(new ChordNote { StringIndex = 1, Fret = 5 });
        level.Chords.Add(ownNotes);
        level.Chords.Add(new Chord { Position = new FractionalPosition(5), TemplateId = 7 });
        arr.Levels.Add(level);
        return arr;
    }

    [Fact]
    public void A_level_builds_into_time_ordered_expected_sounds()
    {
        var arr = SmallArrangement();
        var expected = new List<ExpectedSound>();

        new LiveExpectedTimeline().Build(expected, arr, arr.Levels[0], Beats(), transposeSemitones: 0);

        // The ignored note is not an expected sound; the rest come back sorted by time.
        Assert.Equal([500.0, 1500.0, 2000.0, 2500.0], expected.Select(s => s.TimeMs));

        var slide = expected[0];
        Assert.Equal([42], slide.MidiNotes); // E2 + 2
        Assert.Equal(45, slide.SlideToMidi); // E2 + 5
        Assert.True(slide.PalmMute);
        Assert.False(slide.IsChord);
        Assert.Equal(0, slide.StringIndex);
        Assert.Equal(1, slide.StringMask);

        var bent = expected[1];
        Assert.Equal([52], bent.MidiNotes); // A2 + 7
        Assert.Equal(2, bent.MaxBendSemis); // 1.5 rounds up
        Assert.Equal(1 << 1, bent.StringMask);

        // A chord's own notes and the same shape read from its template build the same sound.
        foreach (var chord in expected.Skip(2))
        {
            Assert.True(chord.IsChord);
            Assert.Equal([43, 50], chord.MidiNotes);
            Assert.Equal(0b11, chord.StringMask);
            Assert.Equal(0, chord.StringIndex);
            Assert.Equal(3, chord.Fret);
        }

        // A whole-song build belongs to no phrase iteration.
        Assert.All(expected, s => Assert.Equal(-1, s.PhraseIterationIndex));
        Assert.All(expected, s => Assert.Equal(0, s.LevelIndex));
    }

    [Fact]
    public void A_build_replaces_what_the_list_held_and_carries_the_transpose()
    {
        var arr = SmallArrangement();
        var timeline = new LiveExpectedTimeline();
        var expected = new List<ExpectedSound>();

        timeline.Build(expected, arr, arr.Levels[0], Beats(), transposeSemitones: 0);
        timeline.Build(expected, arr, arr.Levels[0], Beats(), transposeSemitones: -2);

        Assert.Equal(4, expected.Count);
        Assert.Equal([40], expected[0].MidiNotes);
        Assert.Equal(43, expected[0].SlideToMidi);
        Assert.Equal([41, 48], expected[2].MidiNotes);
        Assert.Equal([41, 48], expected[3].MidiNotes);
    }

    /// <summary>Two phrases; the second allows only level 0. The selector asks for level 1
    /// everywhere, so the intro plays level 1 and the verse is clamped to level 0.</summary>
    [Fact]
    public void A_leveled_build_picks_each_phrase_level_clamped_to_its_max_difficulty()
    {
        var arr = new Arrangement { ArrangementType = ArrangementType.Lead };
        for (var lvl = 0; lvl < 2; lvl++)
        {
            var level = new ChartLevel { DifficultyIndex = lvl };
            level.Notes.Add(new GuitarNote { Position = new FractionalPosition(1), Fret = 1 + 2 * lvl });
            level.Notes.Add(new GuitarNote { Position = new FractionalPosition(5), Fret = 1 + 2 * lvl });
            arr.Levels.Add(level);
        }

        arr.Phrases.Add("intro");
        arr.Phrases.Add("verse");
        arr.PhraseInfos["verse"] = new PhraseInfo(maxDifficulty: 0, solo: false);
        arr.PhraseIterations.Add(new PhraseIteration(new FractionalPosition(4), 1));
        arr.PhraseIterations.Add(new PhraseIteration(new FractionalPosition(0), 0));

        var spans = LiveExpectedTimeline.BuildPhraseSpans(arr, Beats());
        Assert.Equal(["intro", "verse"], spans.Select(s => s.PhraseName));
        Assert.Equal(2000, spans[0].EndMs);
        Assert.Equal(double.PositiveInfinity, spans[1].EndMs);
        Assert.Equal(1, spans[0].MaxDifficulty); // no metadata = fully leveled
        Assert.Equal(0, spans[1].MaxDifficulty);

        var expected = new List<ExpectedSound>();
        new LiveExpectedTimeline().Build(expected, arr, Beats(), _ => 1, transposeSemitones: 0);

        Assert.Equal(2, expected.Count);
        Assert.Equal((500.0, 1, 0, 43), (expected[0].TimeMs, expected[0].LevelIndex,
            expected[0].PhraseIterationIndex, expected[0].MidiNotes[0]));
        Assert.Equal((2500.0, 0, 1, 41), (expected[1].TimeMs, expected[1].LevelIndex,
            expected[1].PhraseIterationIndex, expected[1].MidiNotes[0]));
    }

    [Fact]
    public void An_arrangement_without_levels_builds_nothing()
    {
        var expected = new List<ExpectedSound> { new() { TimeMs = 1 } };

        new LiveExpectedTimeline().Build(expected, new Arrangement(), Beats(), _ => 0, transposeSemitones: 0);

        Assert.Empty(expected);
    }
}
