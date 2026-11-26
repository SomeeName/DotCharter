using DotCharter.Services.Parsing.GuitarPro;

namespace DotCharter.Tests.Services.Parsing.GuitarPro;

/// <summary>
///     #932: a single note that slid in and still rings when a let-ring arpeggio starts, on a string
///     the arpeggio leaves free and inside its fret span, belongs to the arpeggio's hand shape. The
///     shape starts on that note (the slide's TARGET, never the materialised origin a sixteenth
///     earlier) and its grip includes the note. A note outside the span, or one that stopped
///     sounding before the run, keeps the shape on the run's first note.
/// </summary>
public sealed class GpLeadingGripToneHandShapeTests
{
    private static readonly FractionalPosition Target = new(0, new Fraction(1, 4));

    [Theory]
    [InlineData(6)]
    [InlineData(7)]
    public void RingingSlideTarget_InsideTheGrip_StartsTheArpeggioShape(int strings)
    {
        var o = strings - 6;
        var (level, arr) = Build(strings, o + 1, targetFret: 12, targetLength: new Fraction(1, 1));

        var shape = Assert.Single(level.HandShapes);
        Assert.Equal(Target, shape.Position);
        var template = arr.ChordTemplates.Single(t => t.Id == shape.TemplateId);
        Assert.Equal(12, template.Frets[o + 1]);
        Assert.Equal(11, template.Frets[o + 2]);
        Assert.Equal(12, template.Frets[o + 3]);
        Assert.Equal(10, template.Frets[o + 4]);
    }

    [Fact]
    public void NoteOutsideTheFretSpan_LeavesTheShapeOnTheRun()
    {
        var (level, _) = Build(6, 1, targetFret: 14, targetLength: new Fraction(1, 1));

        Assert.Equal(new FractionalPosition(1), Assert.Single(level.HandShapes).Position);
    }

    [Fact]
    public void NoteThatStoppedBeforeTheRun_LeavesTheShapeOnTheRun()
    {
        var (level, _) = Build(6, 1, targetFret: 12, targetLength: new Fraction(1, 4));

        Assert.Equal(new FractionalPosition(1), Assert.Single(level.HandShapes).Position);
    }

    [Fact]
    public void NoteOnAStringTheRunHolds_LeavesTheShapeOnTheRun()
    {
        var (level, _) = Build(6, 2, targetFret: 11, targetLength: new Fraction(1, 1));

        Assert.Equal(new FractionalPosition(1), Assert.Single(level.HandShapes).Position);
    }

    /// <summary>A slide-in from fret 10 into <paramref name="targetFret" /> on
    /// <paramref name="targetString" />, materialised as the import does, then the let-ring
    /// arpeggio 11/12/10 on strings +2/+3/+4 from beat 1.</summary>
    private static (ChartLevel Level, Arrangement Arrangement) Build(int strings, int targetString, int targetFret,
        Fraction targetLength)
    {
        var o = strings - 6;
        var arr = new Arrangement { Name = "Lead", StringCount = strings };
        arr.TuningPitches.Clear();
        for (var i = 0; i < strings; i++) arr.TuningPitches.Add(new TuningOffset(0));
        var level = new ChartLevel { DifficultyIndex = 0 };
        arr.Levels.Add(level);

        level.Notes.Add(new GuitarNote
        {
            Position = FractionalPosition.Zero,
            StringIndex = targetString,
            Fret = targetFret - 2,
            SlideTo = targetFret,
            LinkNext = true,
            Sustain = Target,
            NotationDuration = new Fraction(1, 4)
        });
        level.Notes.Add(new GuitarNote
        {
            Position = Target,
            StringIndex = targetString,
            Fret = targetFret,
            NotationDuration = targetLength
        });
        level.Notes.Add(Ring(new FractionalPosition(1), o + 2, 11, new Fraction(1, 2)));
        level.Notes.Add(Ring(new FractionalPosition(1, new Fraction(2, 3)), o + 4, 10, new Fraction(1, 1)));
        level.Notes.Add(Ring(new FractionalPosition(2, new Fraction(2, 3)), o + 3, 12, new Fraction(3, 2)));

        // The tab marks the authored beat (the origin's position) let ring, as the real file does.
        var letRing = level.Notes.Where(n => n.Position != Target).Select(n => n.Position).ToHashSet();
        GpHandShapeBuilder.BuildLetRingHandShapes(level, arr, letRing);
        return (level, arr);
    }

    private static GuitarNote Ring(FractionalPosition at, int s, int fret, Fraction length) => new()
    {
        Position = at,
        StringIndex = s,
        Fret = fret,
        NotationDuration = length
    };
}
