using DotCharter.Services.Parsing.GuitarPro;

namespace DotCharter.Tests.Services.Parsing.GuitarPro;

/// <summary>
///     The ported Editor-on-Fire short-note / short-chord truncation
///     (<c>eof_gp_import_truncate_short_notes/chords</c>), implemented as the two
///     gates on <see cref="GpPostProcessor.AdjustSustains"/> — the shared
///     sub-quarter-note sustain drop every tab importer runs. Threshold: a sustain
///     is "short" when it is shorter than one quarter note (its BeatId is 0).
/// </summary>
public sealed class GpTruncateShortSustainsTests
{
    private static readonly FractionalPosition Eighth = new(0, new Fraction(1, 2));
    private static readonly FractionalPosition Quarter = new(0, new Fraction(1, 1)); // → BeatId 1

    private static ChartLevel LevelWithNote(FractionalPosition sustain, bool vibrato = false)
    {
        var note = new GuitarNote
        {
            Position = new FractionalPosition(4), StringIndex = 2, Fret = 5, Sustain = sustain, Vibrato = vibrato
        };
        var level = new ChartLevel();
        level.Notes.Add(note);
        return level;
    }

    private static ChartLevel LevelWithChord(FractionalPosition sustain, bool vibrato)
    {
        var chord = new Chord { Position = new FractionalPosition(4) };
        chord.ChordNotes.Add(new ChordNote { StringIndex = 5, Fret = 3, Sustain = sustain, Vibrato = vibrato });
        chord.ChordNotes.Add(new ChordNote { StringIndex = 4, Fret = 5, Sustain = sustain });
        var level = new ChartLevel();
        level.Chords.Add(chord);
        return level;
    }

    [Fact]
    public void ShortNote_IsTruncated_ByDefault()
    {
        var level = LevelWithNote(Eighth);
        GpPostProcessor.AdjustSustains(level, 6); // truncateShortNotes defaults on
        Assert.Equal(FractionalPosition.Zero, level.Notes[0].Sustain);
    }

    [Fact]
    public void ShortNote_KeepsSustain_WhenTruncateOff()
    {
        var level = LevelWithNote(Eighth);
        GpPostProcessor.AdjustSustains(level, 6, truncateShortNotes: false);
        Assert.Equal(Eighth, level.Notes[0].Sustain);
    }

    [Fact]
    public void QuarterNoteSustain_IsNotShort_AndSurvivesTruncation()
    {
        var level = LevelWithNote(Quarter);
        GpPostProcessor.AdjustSustains(level, 6); // truncate on
        Assert.Equal(Quarter, level.Notes[0].Sustain);
    }

    [Fact]
    public void ShortTechniqueNote_IsNeverTruncated()
    {
        // A vibrato note needs its sustain to render — truncation must not drop it
        // even with the toggle on (protects the swung-slide I08 invariant class).
        var level = LevelWithNote(Eighth, vibrato: true);
        GpPostProcessor.AdjustSustains(level, 6);
        Assert.Equal(Eighth, level.Notes[0].Sustain);
    }

    [Fact]
    public void ShortTechniqueChord_KeepsSustain_ByDefault()
    {
        // Chord truncation defaults OFF; a sustain-visible technique keeps the sustain.
        var level = LevelWithChord(Eighth, vibrato: true);
        GpPostProcessor.AdjustSustains(level, 6);
        Assert.All(level.Chords[0].ChordNotes, cn => Assert.Equal(Eighth, cn.Sustain));
    }

    [Fact]
    public void ShortChord_IsTruncated_WhenTruncateChordsOn()
    {
        var level = LevelWithChord(Eighth, vibrato: true);
        GpPostProcessor.AdjustSustains(level, 6, truncateShortChords: true);
        Assert.All(level.Chords[0].ChordNotes, cn => Assert.Equal(FractionalPosition.Zero, cn.Sustain));
    }

    [Fact]
    public void LongChord_SurvivesTruncateChordsOn()
    {
        // A chord authored a quarter note or longer is not "short" — kept even ON.
        var level = LevelWithChord(Quarter, vibrato: false);
        GpPostProcessor.AdjustSustains(level, 6, truncateShortChords: true);
        Assert.All(level.Chords[0].ChordNotes, cn => Assert.Equal(Quarter, cn.Sustain));
    }

    private static (ChartLevel Level, GuitarNote Descending) DescendingHammerRun()
    {
        // fret 5 then a hammer-flagged fret 3 on the SAME string — GP's HOPO flag
        // is direction-agnostic, so the descending note reads as a hammer-on until
        // AdjustSustains re-derives it.
        var high = new GuitarNote { Position = new FractionalPosition(4), StringIndex = 2, Fret = 5 };
        var low = new GuitarNote
        {
            Position = new FractionalPosition(5), StringIndex = 2, Fret = 3, Hopo = HopoType.HammerOn
        };
        var level = new ChartLevel();
        level.Notes.Add(high);
        level.Notes.Add(low);
        return (level, low);
    }

    [Fact]
    public void DescendingHammerOn_BecomesPullOff_ByDefault()
    {
        var (level, low) = DescendingHammerRun();
        GpPostProcessor.AdjustSustains(level, 6);
        Assert.Equal(HopoType.PullOff, low.Hopo);
    }

    [Fact]
    public void DescendingLeftHandTap_StaysHammerOn()
    {
        // A LEFT-hand tap is a fresh tap-on, not a legato pull-off — listed in
        // tapOriginated so the descending-hammer → pull-off derivation skips it
        // (owner report: an all-hammer left-hand tapping run got one pull-off).
        var (level, low) = DescendingHammerRun();
        GpPostProcessor.AdjustSustains(level, 6, tapOriginated: new HashSet<IGuitarSound> { low });
        Assert.Equal(HopoType.HammerOn, low.Hopo);
    }
}
