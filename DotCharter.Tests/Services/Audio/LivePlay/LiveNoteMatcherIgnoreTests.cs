using DotCharter.Services.Audio.LivePlay;

namespace DotCharter.Tests.Services.Audio.LivePlay;

/// <summary>
///     A note or chord marked <c>Ignore</c> is not scored — Rocksmith's rule, and
///     the one the SNG exporter already follows. Live Play used to judge them, so
///     every fret 23+ note and sustained 7th-fret harmonic (which
///     <c>ChartSpacingEnforcer</c> flags itself) was a guaranteed miss that broke
///     the streak and lowered accuracy (#321).
/// </summary>
public sealed class LiveNoteMatcherIgnoreTests
{
    // 120 BPM => 500 ms / beat.
    private static List<BeatMarker> Beats()
        => [.. Enumerable.Range(0, 16).Select(i => new BeatMarker { Time = i * 500.0, Bpm = 120 })];

    private static Arrangement NewArr()
    {
        var arr = new Arrangement { ArrangementType = ArrangementType.Lead };
        arr.Levels.Add(new ChartLevel());
        return arr;
    }

    private static GuitarNote Note(int beat, int fret, bool ignore = false) => new()
    {
        Position = new FractionalPosition(beat),
        Fret = fret,
        StringIndex = 0,
        Ignore = ignore
    };

    private static Chord PowerChord(int beat, bool ignore = false)
    {
        var chord = new Chord { Position = new FractionalPosition(beat), Ignore = ignore };
        chord.ChordNotes.Add(new ChordNote { StringIndex = 0, Fret = 3 });
        chord.ChordNotes.Add(new ChordNote { StringIndex = 1, Fret = 5 });
        return chord;
    }

    private static LiveNoteMatcher Loaded(Arrangement arr)
    {
        var matcher = new LiveNoteMatcher();
        matcher.Load(arr, arr.Levels[0], Beats());
        return matcher;
    }

    [Fact]
    public void IgnoredNote_IsNotExpected_AndExpiresAsNoMiss()
    {
        var arr = NewArr();
        arr.Levels[0].Notes.Add(Note(beat: 2, fret: 3));                // 1000 ms, scored
        arr.Levels[0].Notes.Add(Note(beat: 4, fret: 24, ignore: true)); // 2000 ms, fret 24
        var matcher = Loaded(arr);

        Assert.Equal(1, matcher.Stats.TotalSounds);
        Assert.Single(matcher.Expected);

        var events = matcher.AdvanceTo(playheadMs: 4000);

        var miss = Assert.Single(events);
        Assert.Equal(1000, miss.Sound.TimeMs);
        Assert.Equal(1, matcher.Stats.Misses);
        Assert.Equal(1, matcher.Stats.Judged);
    }

    [Fact]
    public void IgnoredNote_PlayedAnyway_ScoresNothing()
    {
        var arr = NewArr();
        arr.Levels[0].Notes.Add(Note(beat: 2, fret: 3));
        arr.Levels[0].Notes.Add(Note(beat: 4, fret: 3, ignore: true)); // 2000 ms, G2 = 43
        var matcher = Loaded(arr);

        Assert.NotNull(matcher.OfferNote(midi: 43, timeMs: 1000));
        Assert.Null(matcher.OfferNote(midi: 43, timeMs: 2000));
        matcher.AdvanceTo(playheadMs: 4000);

        Assert.Equal(1, matcher.Stats.Hits);
        Assert.Equal(0, matcher.Stats.Misses);
        Assert.Equal(1.0, matcher.Stats.Accuracy);
    }

    [Fact]
    public void IgnoredChord_IsNotExpected_AndExpiresAsNoMiss()
    {
        var arr = NewArr();
        arr.Levels[0].Chords.Add(PowerChord(beat: 2));
        arr.Levels[0].Chords.Add(PowerChord(beat: 4, ignore: true));
        var matcher = Loaded(arr);

        Assert.Equal(1, matcher.Stats.TotalSounds);

        var events = matcher.AdvanceTo(playheadMs: 4000);

        var miss = Assert.Single(events);
        Assert.Equal(1000, miss.Sound.TimeMs);
        Assert.Equal(1, matcher.Stats.Misses);
    }

    [Fact]
    public void ChordNoteIgnore_AloneLeavesTheStrumScored()
    {
        // The SNG chord mask carries only the CHORD's Ignore; a chord note's
        // own flag does not take the strum out of scoring.
        var arr = NewArr();
        var chord = PowerChord(beat: 2);
        chord.ChordNotes[1].Ignore = true;
        arr.Levels[0].Chords.Add(chord);
        var matcher = Loaded(arr);

        Assert.Equal(1, matcher.Stats.TotalSounds);
    }

    [Fact]
    public void LeveledLoad_SkipsIgnoredSoundsToo()
    {
        var arr = NewArr();
        arr.Levels[0].Notes.Add(Note(beat: 2, fret: 3));
        arr.Levels[0].Notes.Add(Note(beat: 4, fret: 23, ignore: true));
        arr.Levels[0].Chords.Add(PowerChord(beat: 6, ignore: true));
        var matcher = new LiveNoteMatcher();

        matcher.Load(arr, Beats(), _ => 0);

        Assert.Equal(1, matcher.Stats.TotalSounds);
        Assert.Equal(1000, Assert.Single(matcher.Expected).TimeMs);
    }
}
