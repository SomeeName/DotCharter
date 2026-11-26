using DotCharter.Services.Editing.FretAnchors;

namespace DotCharter.Tests.Services.Editing.FretAnchors;

/// <summary>
///     #942: a seven-string riff plays a one-fret grip at fret 9 on beat 4 and drops to
///     fret 7 on the next downbeat. The fret-9 grip sits inside the fret-7 box (7-10), so
///     the hand moves to 7 once, where the 9 starts, instead of reaching up to 9 and
///     coming back down a beat later. A real position (three or more strokes) or a
///     drop past the ring finger keeps its anchor.
/// </summary>
public class HigherAnchorFoldTests
{
    private static Chord Grip(int beat, int fret)
    {
        var chord = new Chord { Position = new FractionalPosition(beat), TemplateId = fret };
        chord.ChordNotes.Add(new ChordNote { StringIndex = 0, Fret = fret });
        chord.ChordNotes.Add(new ChordNote { StringIndex = 1, Fret = fret });
        return chord;
    }

    private static ChordTemplate Template(int fret) => new()
    {
        Id = fret,
        Frets = new Dictionary<int, int> { [0] = fret, [1] = fret },
        Fingers = new Dictionary<int, int> { [0] = 1, [1] = 1 }
    };

    // One grip a beat, each its own hand shape (index barre).
    private static IReadOnlyList<FretAnchor> Generate(int stringCount, params int[] frets)
    {
        var level = new ChartLevel();
        for (var beat = 0; beat < frets.Length; beat++)
        {
            level.Chords.Add(Grip(beat, frets[beat]));
            level.HandShapes.Add(new HandShape
            {
                Position = new FractionalPosition(beat),
                EndPosition = new FractionalPosition(beat + 1),
                TemplateId = frets[beat]
            });
        }

        var arrangement = new Arrangement { StringCount = stringCount };
        var templates = frets.Distinct().Select(Template).ToList();
        return new FretAnchorGenerationService().GenerateAnchors(level, chordTemplates: templates, tuning: arrangement);
    }

    [Fact]
    public void SevenString_PassingGripSlidDownTwoFrets_FoldsIntoTheLowerAnchor()
    {
        var anchors = Generate(7, 2, 2, 5, 9, 7, 7, 5, 5);

        Assert.Equal([2, 5, 7, 5], anchors.Select(a => a.Fret));
        Assert.Equal(new FractionalPosition(3), anchors[2].Position);
    }

    [Fact]
    public void SixString_PassingGripSlidDownTwoFrets_StillReplants()
    {
        var anchors = Generate(6, 2, 2, 5, 9, 7, 7, 5, 5);

        Assert.Equal([2, 5, 9, 7, 5], anchors.Select(a => a.Fret));
    }

    [Fact]
    public void SevenString_ThreeFretDrop_KeepsBothAnchors()
    {
        var anchors = Generate(7, 2, 2, 5, 9, 6, 6, 6, 6);

        Assert.Equal([2, 5, 9, 6], anchors.Select(a => a.Fret));
    }

    [Fact]
    public void SevenString_ChromaticWalkDown_KeepsEveryPosition()
    {
        var anchors = Generate(7, 4, 4, 4, 3, 3, 3, 2, 2);

        Assert.Equal([4, 3, 2], anchors.Select(a => a.Fret));
    }
}
