using DotCharter.Services.Parsing.GuitarPro;

namespace DotCharter.Tests.Services.Parsing.GuitarPro;

/// <summary>
///     An arpeggio tabbed as three let-ring notes and a plain fourth note (s1f1 LR, s2f3 LR,
///     s4f3 LR, s3f3) is one grip: the fourth note is played inside the hand shape the first
///     three ring under. The importer's bar ended on the third note, and the
///     owner lengthened eight such bars by hand and re-saved each grip.
/// </summary>
public sealed class GpLetRingTrailingGripToneTests
{
    private static GuitarNote Note(int beat, int stringIndex, int fret) => new()
    {
        Position = new FractionalPosition(beat), StringIndex = stringIndex, Fret = fret,
        Sustain = new FractionalPosition(1)
    };

    private static (ChartLevel Level, Arrangement Arr) Import(GuitarNote tail,
        HashSet<FractionalPosition>? otherRunPositions = null)
    {
        var arr = new Arrangement();
        var level = new ChartLevel();
        var ringing = new[] { Note(0, 5, 1), Note(1, 4, 3), Note(2, 2, 3) };
        var letRing = new HashSet<FractionalPosition>();
        foreach (var n in ringing)
        {
            level.Notes.Add(n);
            letRing.Add(n.Position);
        }

        level.Notes.Add(tail);
        GpHandShapeBuilder.BuildLetRingHandShapes(level, arr, letRing, otherRunPositions: otherRunPositions);
        return (level, arr);
    }

    [Fact]
    public void APlainNoteThatFitsTheGrip_EndsTheLetRingShapeAfterIt()
    {
        var (level, arr) = Import(Note(3, 3, 3));

        var shape = Assert.Single(level.HandShapes);
        Assert.Equal(new FractionalPosition(0), shape.Position);
        Assert.Equal(new FractionalPosition(4), shape.EndPosition);
        var template = arr.ChordTemplates.Single(t => t.Id == shape.TemplateId);
        Assert.Equal(3, template.Frets[3]);
        Assert.Equal(4, template.Frets.Count);
    }

    [Theory]
    [InlineData(2, 5)] // the same string the run holds, re-fretted
    [InlineData(3, 9)] // a stretch past one hand
    public void APlainNoteThatDoesNotFit_StillEndsTheShape(int stringIndex, int fret)
    {
        var (level, arr) = Import(Note(3, stringIndex, fret));

        var shape = Assert.Single(level.HandShapes);
        Assert.Equal(new FractionalPosition(3), shape.EndPosition);
        Assert.Equal(3, arr.ChordTemplates.Single(t => t.Id == shape.TemplateId).Frets.Count);
    }

    [Fact]
    public void ANoteOfTheOtherRunSet_IsNeverTakenAsTheTail()
    {
        // The roll set and the authored let-ring set build apart (import-tabs-audio.md):
        // a beat the other set rings from must not be swallowed as this run's tail.
        var (level, _) = Import(Note(3, 3, 3), [new FractionalPosition(3)]);

        Assert.Equal(new FractionalPosition(3), Assert.Single(level.HandShapes).EndPosition);
    }
}
