namespace DotCharter.Tests.Core.Guitar;

/// <summary>
///     The one shared invariant every sustain-changing pass leans on: a bend point
///     can never lie outside [onset, onset + sustain]. Pinned here at the Core
///     level so import (<c>GpPostProcessor</c>) and the save/edit spacing pass
///     (<c>ChartSpacingEnforcer</c>) — which both delegate to it — can never
///     regress the "bends outside the note sustain" bug independently.
/// </summary>
public class BendGeometryTests
{
    private static GuitarNote NoteWith(FractionalPosition position, FractionalPosition sustain,
        params FractionalPosition[] bendPositions)
    {
        var note = new GuitarNote { Position = position, StringIndex = 0, Sustain = sustain };
        foreach (var p in bendPositions) note.BendValues.Add(new BendValue(p, 1));
        return note;
    }

    [Fact]
    public void ClampBendsToSustain_PullsAPointPastTheEndBackToTheEnd()
    {
        var note = NoteWith(new FractionalPosition(2), new FractionalPosition(1),
            new FractionalPosition(2), new FractionalPosition(5)); // second point far past end (beat 3)

        BendGeometry.ClampBendsToSustain(note, note.Position);

        Assert.Equal(new FractionalPosition(3), note.BendValues[1].Position); // 2 + 1 = 3
        Assert.False(BendGeometry.HasBendOutsideSustain(note, note.Position));
    }

    [Fact]
    public void ClampBendsToSustain_RaisesAPointBeforeTheStartUpToTheStart()
    {
        var note = NoteWith(new FractionalPosition(2), new FractionalPosition(1),
            new FractionalPosition(1)); // before the onset

        BendGeometry.ClampBendsToSustain(note, note.Position);

        Assert.Equal(new FractionalPosition(2), note.BendValues[0].Position);
        Assert.False(BendGeometry.HasBendOutsideSustain(note, note.Position));
    }

    [Fact]
    public void ClampBendsToSustain_IsIdempotent_AndLeavesInRangePointsUntouched()
    {
        var mid = new FractionalPosition(2, new Fraction(1, 2));
        var note = NoteWith(new FractionalPosition(2), new FractionalPosition(1),
            new FractionalPosition(2), mid, new FractionalPosition(3));

        BendGeometry.ClampBendsToSustain(note, note.Position);
        BendGeometry.ClampBendsToSustain(note, note.Position); // twice — must not drift

        Assert.Equal(new FractionalPosition(2), note.BendValues[0].Position);
        Assert.Equal(mid, note.BendValues[1].Position);
        Assert.Equal(new FractionalPosition(3), note.BendValues[2].Position);
    }

    [Fact]
    public void ClampBendsToSustain_ZeroSustain_CollapsesEveryPointOntoTheHead()
    {
        var note = NoteWith(new FractionalPosition(2), FractionalPosition.Zero,
            new FractionalPosition(2, new Fraction(1, 4)));

        BendGeometry.ClampBendsToSustain(note, note.Position);

        Assert.Equal(new FractionalPosition(2), note.BendValues[0].Position);
        Assert.False(BendGeometry.HasBendOutsideSustain(note, note.Position));
    }

    [Fact]
    public void HasBendOutsideSustain_NoBends_IsFalse()
    {
        var note = NoteWith(new FractionalPosition(2), new FractionalPosition(1));
        Assert.False(BendGeometry.HasBendOutsideSustain(note, note.Position));
    }
}
