using DotCharter.Services.Parsing.GuitarPro;

namespace DotCharter.Tests.Services.Parsing.GuitarPro;

/// <summary>
///     #925 / #927: a slide-in into a chord is materialised as an origin chord a
///     sixteenth before the target. The origin is a lead-in, not a grip: the hand
///     shape starts at the TARGET (EOF rs.c gives the slide's target its own hand
///     shape, Charter's GP5SoundsTransformer does the same), and every origin note
///     sits on its target note's string (EOF gp_import.c), on any string count.
/// </summary>
public sealed class GpChordSlideInHandShapeTests
{
    [Theory]
    [InlineData(6)]
    [InlineData(7)]
    public void SlideInIntoAChord_HandShapeStartsAtTheTarget_AndTheOriginKeepsTheTargetStrings(int strings)
    {
        var arr = new Arrangement { Name = "Bass", StringCount = strings, ArrangementType = ArrangementType.Bass };
        arr.TuningPitches.Clear();
        for (var i = 0; i < strings; i++) arr.TuningPitches.Add(new TuningOffset(0));
        var level = new ChartLevel { DifficultyIndex = 0 };
        arr.Levels.Add(level);
        var target = new Chord { Position = new FractionalPosition(1), NotationDuration = new Fraction(1, 1) };
        target.ChordNotes.Add(Note(strings - 6 + 1, 5));
        target.ChordNotes.Add(Note(strings - 6 + 2, 7));
        level.Chords.Add(target);
        level.Chords.Add(Plain(2, strings - 6 + 1, 5, strings - 6 + 2, 7));

        GpPostProcessor.Run(level, arr);

        var origin = level.Chords.OrderBy(c => c.Position).First();
        var landed = level.Chords.OrderBy(c => c.Position).Skip(1).First();
        Assert.True(origin.ChordNotes.All(n => n.SlideTo != null));
        Assert.Equal(
            landed.ChordNotes.Select(n => n.StringIndex).OrderBy(s => s),
            origin.ChordNotes.Select(n => n.StringIndex).OrderBy(s => s));
        Assert.DoesNotContain(level.HandShapes, h => h.Position.CompareTo(landed.Position) < 0);
        Assert.Contains(level.HandShapes, h => h.Position.Equals(landed.Position));
    }

    private static ChordNote Note(int s, int fret) => new()
    {
        StringIndex = s,
        Fret = fret,
        Sustain = new FractionalPosition(1),
        SlideTo = GpNoteParser.SlideInFromBelow
    };

    private static Chord Plain(int beat, int s1, int f1, int s2, int f2)
    {
        var chord = new Chord { Position = new FractionalPosition(beat), NotationDuration = new Fraction(1, 1) };
        chord.ChordNotes.Add(new ChordNote { StringIndex = s1, Fret = f1, Sustain = new FractionalPosition(1) });
        chord.ChordNotes.Add(new ChordNote { StringIndex = s2, Fret = f2, Sustain = new FractionalPosition(1) });
        return chord;
    }
}
