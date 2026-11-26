using DotCharter.Services.Editing.FretAnchors;

namespace DotCharter.Tests.Services.Editing.FretAnchors;

/// <summary>
///     #1007: a hand shape is half-open, so a sound that starts exactly AT its end belongs to the
///     NEXT shape. Before a GP import trims its shapes, every per-chord shape ends on the next
///     chord; an inclusive end let that chord's top fret leak into the shape's reach, widening the
///     anchor to 5 or marking the shape too wide to lock.
/// </summary>
public class HandShapeEndExclusiveReachTests
{
    private sealed record Grip(int Beat, int EndBeat, int[] Frets, int[] Fingers, int[]? SlideTo = null, bool OwnShape = true);

    private static IReadOnlyList<FretAnchor> Generate(params Grip[] grips)
    {
        var level = new ChartLevel();
        var templates = new List<ChordTemplate>();
        for (var id = 0; id < grips.Length; id++)
        {
            var grip = grips[id];
            var chord = new Chord { Position = new FractionalPosition(grip.Beat), TemplateId = id };
            var frets = new Dictionary<int, int>();
            var fingers = new Dictionary<int, int>();
            for (var s = 0; s < grip.Frets.Length; s++)
            {
                chord.ChordNotes.Add(new ChordNote { StringIndex = s, Fret = grip.Frets[s], SlideTo = grip.SlideTo?[s] });
                frets[s] = grip.Frets[s];
                fingers[s] = grip.Fingers[s];
            }

            level.Chords.Add(chord);
            if (grip.OwnShape)
            {
                level.HandShapes.Add(new HandShape
                {
                    Position = new FractionalPosition(grip.Beat),
                    EndPosition = new FractionalPosition(grip.EndBeat),
                    TemplateId = id
                });
            }
            templates.Add(new ChordTemplate { Id = id, Frets = frets, Fingers = fingers });
        }

        return new FretAnchorGenerationService().GenerateAnchors(level, chordTemplates: templates);
    }

    private static string Describe(IEnumerable<FretAnchor> anchors) =>
        string.Join(" ", anchors.Select(a => $"{a.Position}:{a.Fret}w{a.Width}"));

    [Fact]
    public void ChordAtShapeEnd_DoesNotWidenThePreviousShapesAnchor()
    {
        // 8+11 (index + pinky, four frets), then a chord reaching fret 12 exactly at the shape's end.
        var anchors = Generate(
            new Grip(0, 1, [8, 11], [1, 4]),
            new Grip(1, 2, [10, 12], [1, 3]));

        var first = anchors.Single(a => a.Position == FractionalPosition.Zero);
        Assert.True(first.Fret == 8 && first.Width == 4, Describe(anchors));
        for (var i = 1; i < anchors.Count; i++)
            Assert.True(anchors[i].Fret != anchors[i - 1].Fret || anchors[i].Width != anchors[i - 1].Width, Describe(anchors));
    }

    [Fact]
    public void SlideChord_NextChordAtShapeEnd_KeepsWidthFour()
    {
        // 12/14 slid up to 14/16, the next chord on the slide's target.
        var anchors = Generate(
            new Grip(0, 1, [12, 14], [1, 3], [14, 16]),
            new Grip(1, 2, [14, 16], [1, 3]));

        var first = anchors.Single(a => a.Position == FractionalPosition.Zero);
        Assert.True(first.Fret == 12 && first.Width == 4, Describe(anchors));
    }

    [Fact]
    public void ArpeggioFollowedByHighChordAtItsEnd_AnchorsAtTheShapeStart_NotInsideIt()
    {
        // A let-ring 9-11-11-11 arpeggio over beats 1-3 after a 6-8 grip (whose anchor still covers
        // fret 9), then a fret-14 chord exactly at the shape's end. That chord is the next hand
        // position: it must not make the shape too wide to lock, or the walk keeps fret 9 under the
        // old anchor and plants fret 11 inside the shape.
        var level = new ChartLevel();
        var templates = new List<ChordTemplate>
        {
            new() { Id = 0, Frets = new() { [0] = 6, [1] = 8 }, Fingers = new() { [0] = 1, [1] = 3 } },
            new() { Id = 1, Frets = new() { [0] = 9, [1] = 11, [2] = 11, [3] = 11 }, Fingers = new() { [0] = 1, [1] = 3, [2] = 3, [3] = 3 } },
            new() { Id = 2, Frets = new() { [0] = 14, [1] = 16 }, Fingers = new() { [0] = 1, [1] = 3 } }
        };
        level.Chords.Add(TwoStringChord(0, 0, 6, 8));
        level.HandShapes.Add(Shape(0, 1, 0));
        level.HandShapes.Add(Shape(1, 3, 1));
        var arpeggio = new (int Beat, int Half, int String, int Fret)[] { (1, 0, 0, 9), (1, 1, 1, 11), (2, 0, 2, 11), (2, 1, 3, 11) };
        foreach (var (beat, half, str, fret) in arpeggio)
            level.Notes.Add(new GuitarNote { Position = new FractionalPosition(beat, new Fraction(half, 2)), StringIndex = str, Fret = fret });
        level.Chords.Add(TwoStringChord(3, 2, 14, 16));
        level.HandShapes.Add(Shape(3, 4, 2));

        var anchors = new FretAnchorGenerationService().GenerateAnchors(level, chordTemplates: templates);

        Assert.Contains(anchors, a => a.Position == new FractionalPosition(1) && a.Fret == 9);
        var inside = anchors.Where(a => a.Position > new FractionalPosition(1) && a.Position < new FractionalPosition(3)).ToList();
        Assert.True(inside.Count == 0, Describe(anchors));
    }

    [Fact]
    public void ArpeggioRoot_IgnoresTheNextShapesFirstNoteAtItsEnd()
    {
        // #1017: a let-ring 12-14-14-14 arpeggio over beats 0-4, then the next shape's 10-12-12
        // arpeggio starting exactly at the first shape's end. Before an import trims its shapes they
        // abut, so the next shape's single fret-10 note sits on the first shape's End: it belongs to
        // the NEXT shape and must not drag the first arpeggio's anchor down to 10 (width 5).
        var level = ArpeggioLevel();
        level.HandShapes.Add(Shape(4, 8, 1));
        level.Notes.Add(new GuitarNote { Position = new FractionalPosition(5), StringIndex = 1, Fret = 12 });
        level.Notes.Add(new GuitarNote { Position = new FractionalPosition(6), StringIndex = 3, Fret = 12 });

        var anchors = new FretAnchorGenerationService().GenerateAnchors(level);

        var first = anchors.Single(a => a.Position == FractionalPosition.Zero);
        Assert.True(first.Fret == 12 && first.Width == 4, Describe(anchors));
        Assert.Contains(anchors, a => a.Position == new FractionalPosition(4) && a.Fret == 10);
    }

    [Fact]
    public void ArpeggioRoot_KeepsALoneNoteAtItsEnd_WhenNoShapeStartsThere()
    {
        // The same arpeggio with a lone fret-10 note at its End and NO shape starting there: the
        // running arpeggio sweeps on, so the note stays in the root (the hand-corrected corpus anchors
        // those continuations together).
        var level = ArpeggioLevel();

        var anchors = new FretAnchorGenerationService().GenerateAnchors(level);

        var first = anchors.Single(a => a.Position == FractionalPosition.Zero);
        Assert.True(first.Fret == 10, Describe(anchors));
    }

    private static ChartLevel ArpeggioLevel()
    {
        var level = new ChartLevel();
        var notes = new (int Beat, int String, int Fret)[] { (0, 0, 12), (1, 1, 14), (2, 3, 14), (3, 2, 14), (4, 0, 10) };
        foreach (var (beat, str, fret) in notes)
            level.Notes.Add(new GuitarNote { Position = new FractionalPosition(beat), StringIndex = str, Fret = fret });
        level.HandShapes.Add(Shape(0, 4, 0));
        return level;
    }

    private static Chord TwoStringChord(int beat, int templateId, int low, int high)
    {
        var chord = new Chord { Position = new FractionalPosition(beat), TemplateId = templateId };
        chord.ChordNotes.Add(new ChordNote { StringIndex = 0, Fret = low });
        chord.ChordNotes.Add(new ChordNote { StringIndex = 1, Fret = high });
        return chord;
    }

    private static HandShape Shape(int beat, int endBeat, int templateId) => new()
    {
        Position = new FractionalPosition(beat),
        EndPosition = new FractionalPosition(endBeat),
        TemplateId = templateId
    };
}
