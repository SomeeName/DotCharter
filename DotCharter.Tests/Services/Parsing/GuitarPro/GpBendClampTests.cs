using DotCharter.Services.Parsing.GuitarPro;

namespace DotCharter.Tests.Services.Parsing.GuitarPro;

/// <summary>
///     Bend points are placed against the AUTHORED sustain at parse time, but
///     swing placement, tie-merge and the short-sustain trim can all shorten the
///     final sustain — leaving a bend's tail beyond the note's end, where it
///     renders on the NEXT sound. <see cref="GpPostProcessor.ClampBendsToSustain"/>
///     pulls any such point back to the note end (owner report 2026-07-19).
/// </summary>
public sealed class GpBendClampTests
{
    [Fact]
    public void NoteBendPastSustain_IsClampedToTheNoteEnd()
    {
        var note = new GuitarNote
        {
            Position = new FractionalPosition(4),
            Sustain = new FractionalPosition(0, new Fraction(1, 2)), // eighth note
            StringIndex = 2,
            Fret = 7
        };
        // Curve: rises to full within the sustain, then a stray point a whole
        // beat past the note end (as if placed against a longer authored rhythm).
        note.BendValues.Add(new BendValue(new FractionalPosition(4), 0));
        note.BendValues.Add(new BendValue(new FractionalPosition(4, new Fraction(1, 2)), 1.0));
        note.BendValues.Add(new BendValue(new FractionalPosition(5, new Fraction(1, 2)), 1.0));
        var level = new ChartLevel();
        level.Notes.Add(note);

        GpPostProcessor.ClampBendsToSustain(level);

        // No bend point may lie past the note's end.
        var end = note.EndPosition;
        Assert.All(note.BendValues, b => Assert.True(b.Position.CompareTo(end) <= 0));
        Assert.Equal(end, note.BendValues[^1].Position);
    }

    [Fact]
    public void ChordNoteBendPastSustain_IsClampedToTheChordNoteEnd()
    {
        var chord = new Chord { Position = new FractionalPosition(4) };
        var cn = new ChordNote
        {
            StringIndex = 1, Fret = 12, Sustain = new FractionalPosition(0, new Fraction(1, 2))
        };
        cn.BendValues.Add(new BendValue(new FractionalPosition(4), 0));
        cn.BendValues.Add(new BendValue(new FractionalPosition(6), 1.0)); // 2 beats past a half-beat note
        chord.ChordNotes.Add(cn);
        var level = new ChartLevel();
        level.Chords.Add(chord);

        GpPostProcessor.ClampBendsToSustain(level);

        var end = chord.Position + cn.Sustain;
        Assert.All(cn.BendValues, b => Assert.True(b.Position.CompareTo(end) <= 0));
    }

    [Fact]
    public void BendWithinSustain_IsUntouched()
    {
        var note = new GuitarNote
        {
            Position = new FractionalPosition(4),
            Sustain = new FractionalPosition(2),
            StringIndex = 0,
            Fret = 5
        };
        var mid = new FractionalPosition(5);
        note.BendValues.Add(new BendValue(new FractionalPosition(4), 0));
        note.BendValues.Add(new BendValue(mid, 1.0));
        var level = new ChartLevel();
        level.Notes.Add(note);

        GpPostProcessor.ClampBendsToSustain(level);

        Assert.Equal(new FractionalPosition(4), note.BendValues[0].Position);
        Assert.Equal(mid, note.BendValues[1].Position); // unchanged
    }
}
