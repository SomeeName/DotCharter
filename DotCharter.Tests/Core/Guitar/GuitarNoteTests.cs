namespace DotCharter.Tests.Core.Guitar;

public class GuitarNoteTests
{
    private static GuitarNote MakeBendedNote()
    {
        var n = new GuitarNote
        {
            Position = new FractionalPosition(2),
            Sustain = new FractionalPosition(0, new Fraction(1, 2)),
            StringIndex = 3,
            Fret = 7,
            Mute = MuteType.Palm,
            Hopo = HopoType.HammerOn,
            Harmonic = HarmonicType.Pinch,
            Vibrato = true,
            VibratoStrength = 80,
            Tremolo = true,
            LinkNext = true,
            SlideTo = 9,
            UnpitchedSlide = true,
            Accent = true,
            Ignore = true,
            PassOtherNotes = true,
            BassPicking = BassPickingType.Pop
        };
        n.BendValues.Add(new BendValue(new FractionalPosition(0, new Fraction(1, 4)), 1.0));
        n.BendValues.Add(new BendValue(new FractionalPosition(0, new Fraction(1, 2)), 2.0));
        return n;
    }

    [Fact]
    public void Clone_CopiesAllScalarFields()
    {
        var a = MakeBendedNote();
        var b = a.Clone();

        Assert.Equal(a.Position, b.Position);
        Assert.Equal(a.Sustain, b.Sustain);
        Assert.Equal(a.StringIndex, b.StringIndex);
        Assert.Equal(a.Fret, b.Fret);
        Assert.Equal(a.Mute, b.Mute);
        Assert.Equal(a.Hopo, b.Hopo);
        Assert.Equal(a.Harmonic, b.Harmonic);
        Assert.Equal(a.Vibrato, b.Vibrato);
        Assert.Equal(a.VibratoStrength, b.VibratoStrength);
        Assert.Equal(a.Tremolo, b.Tremolo);
        Assert.Equal(a.LinkNext, b.LinkNext);
        Assert.Equal(a.SlideTo, b.SlideTo);
        Assert.Equal(a.UnpitchedSlide, b.UnpitchedSlide);
        Assert.Equal(a.Accent, b.Accent);
        Assert.Equal(a.Ignore, b.Ignore);
        Assert.Equal(a.PassOtherNotes, b.PassOtherNotes);
        Assert.Equal(a.BassPicking, b.BassPicking);
    }

    [Fact]
    public void Clone_DeepCopiesBendValues()
    {
        var a = MakeBendedNote();
        var b = a.Clone();

        Assert.Equal(a.BendValues.Count, b.BendValues.Count);
        // The cloned bend values must be separate instances.
        for (var i = 0; i < a.BendValues.Count; i++)
        {
            Assert.NotSame(a.BendValues[i], b.BendValues[i]);
            Assert.Equal(a.BendValues[i].Position, b.BendValues[i].Position);
            Assert.Equal(a.BendValues[i].Step, b.BendValues[i].Step);
        }

        // Mutating a bend on the clone must not affect the original.
        b.BendValues[0].Step = 99;
        Assert.Equal(1.0, a.BendValues[0].Step);
    }

    [Fact]
    public void EndPosition_IsPositionPlusSustain()
    {
        var n = new GuitarNote
        {
            Position = new FractionalPosition(2),
            Sustain = new FractionalPosition(1)
        };
        Assert.Equal(new FractionalPosition(3), n.EndPosition);
    }

    [Fact]
    public void BendValue_CompareTo_SortsByPosition()
    {
        var bends = new List<BendValue>
        {
            new(new FractionalPosition(0, new Fraction(3, 4)), 1),
            new(new FractionalPosition(0, new Fraction(1, 4)), 2),
            new(new FractionalPosition(0, new Fraction(1, 2)), 3)
        };
        bends.Sort();
        Assert.Equal(new Fraction(1, 4), bends[0].Position.Fraction);
        Assert.Equal(new Fraction(1, 2), bends[1].Position.Fraction);
        Assert.Equal(new Fraction(3, 4), bends[2].Position.Fraction);
    }
}
