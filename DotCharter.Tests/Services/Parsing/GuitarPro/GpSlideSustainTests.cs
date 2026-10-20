using DotCharter.Services.Parsing.GuitarPro;

namespace DotCharter.Tests.Services.Parsing.GuitarPro;

/// <summary>
///     A pitched slide travels ACROSS its sustain and must land on — never
///     overshoot — the note it slides into. <see cref="GpPostProcessor.ClampSlideSustains"/>
///     shortens an over-long slide sustain to end exactly at the target onset
///     (owner report 2026-07-19: the slide tail ran past the target chord).
/// </summary>
public sealed class GpSlideSustainTests
{
    [Fact]
    public void OverLongSlideSustain_IsClampedToTheTargetOnset()
    {
        var level = new ChartLevel();
        // A slide at beat 0 authored a whole note long, but its target lands at
        // beat 1 — the slide must end at beat 1, not run to beat 4.
        level.Notes.Add(new GuitarNote
        {
            Position = new FractionalPosition(0),
            Sustain = new FractionalPosition(4),
            StringIndex = 0,
            Fret = 3,
            SlideTo = 5
        });
        level.Notes.Add(new GuitarNote { Position = new FractionalPosition(1), StringIndex = 0, Fret = 5 });

        GpPostProcessor.ClampSlideSustains(level, 6);

        Assert.Equal(new FractionalPosition(1), level.Notes[0].Position + level.Notes[0].Sustain);
    }

    [Fact]
    public void SlideSustainThatAlreadyLandsOnTarget_IsUntouched()
    {
        var level = new ChartLevel();
        level.Notes.Add(new GuitarNote
        {
            Position = new FractionalPosition(0),
            Sustain = new FractionalPosition(1),
            StringIndex = 0,
            Fret = 3,
            SlideTo = 5
        });
        level.Notes.Add(new GuitarNote { Position = new FractionalPosition(1), StringIndex = 0, Fret = 5 });

        GpPostProcessor.ClampSlideSustains(level, 6);

        Assert.Equal(new FractionalPosition(1), level.Notes[0].Sustain); // unchanged
    }

    [Fact]
    public void ChordSlide_EachSlidingNoteLandsOnItsTarget()
    {
        // {3,2,2} sliding up to {4,3,3}: after the full pipeline the start chord
        // keeps a slide sustain that ends exactly at the target onset.
        var arr = new Arrangement { Name = "Lead", StringCount = 6 };
        arr.TuningPitches.Clear();
        for (var i = 0; i < 6; i++) arr.TuningPitches.Add(new TuningOffset(0));

        var start = new Chord { Position = new FractionalPosition(0), NotationDuration = new Fraction(4, 1) };
        start.ChordNotes.Add(new ChordNote { StringIndex = 0, Fret = 3, SlideTo = 4, Sustain = new FractionalPosition(4) });
        start.ChordNotes.Add(new ChordNote { StringIndex = 1, Fret = 2, SlideTo = 3, Sustain = new FractionalPosition(4) });
        start.ChordNotes.Add(new ChordNote { StringIndex = 2, Fret = 2, SlideTo = 3, Sustain = new FractionalPosition(4) });
        var target = new Chord { Position = new FractionalPosition(1), NotationDuration = new Fraction(1, 1) };
        target.ChordNotes.Add(new ChordNote { StringIndex = 0, Fret = 4, Sustain = new FractionalPosition(1) });
        target.ChordNotes.Add(new ChordNote { StringIndex = 1, Fret = 3, Sustain = new FractionalPosition(1) });
        target.ChordNotes.Add(new ChordNote { StringIndex = 2, Fret = 3, Sustain = new FractionalPosition(1) });

        var level = new ChartLevel();
        level.Chords.Add(start);
        level.Chords.Add(target);

        GpPostProcessor.Run(level, arr);

        var startChord = level.Chords.Single(c => c.Position == new FractionalPosition(0));
        Assert.All(startChord.ChordNotes,
            cn => Assert.Equal(new FractionalPosition(1), startChord.Position + cn.Sustain));
    }
}
