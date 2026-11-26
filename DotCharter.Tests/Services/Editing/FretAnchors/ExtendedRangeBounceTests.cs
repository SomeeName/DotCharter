using DotCharter.Services.Editing.FretAnchors;

namespace DotCharter.Tests.Services.Editing.FretAnchors;

/// <summary>
///     #930: on a seven-string guitar a riff that rocks between two one-fret power chords
///     (B5 at fret 2, C5 at fret 3 on the two lowest strings) keeps ONE anchor: the hand
///     stays at fret 2 and plays the C5 with the next finger. The six-string walk still
///     re-plants on every grip (the charters' own habit on a six-string).
/// </summary>
public class ExtendedRangeBounceTests
{
    private static Chord Chord(int beat, int templateId, int fret)
    {
        var chord = new Chord { Position = new FractionalPosition(beat), TemplateId = templateId };
        chord.ChordNotes.Add(new ChordNote { StringIndex = 0, Fret = fret });
        chord.ChordNotes.Add(new ChordNote { StringIndex = 1, Fret = fret });
        return chord;
    }

    private static ChordTemplate Template(int id, int fret) => new()
    {
        Id = id,
        Frets = new Dictionary<int, int> { [0] = fret, [1] = fret },
        Fingers = new Dictionary<int, int> { [0] = 1, [1] = 1 }
    };

    // B5 B5 | C5 C5 | B5 B5 | C5 C5 | B5 B5, each grip its own hand shape.
    private static IReadOnlyList<FretAnchor> Generate(int stringCount)
    {
        var level = new ChartLevel();
        for (var bar = 0; bar < 5; bar++)
        {
            var fret = bar % 2 == 0 ? 2 : 3;
            level.Chords.Add(Chord(bar * 2, fret - 2, fret));
            level.Chords.Add(Chord(bar * 2 + 1, fret - 2, fret));
            level.HandShapes.Add(new HandShape
            {
                Position = new FractionalPosition(bar * 2),
                EndPosition = new FractionalPosition(bar * 2 + 2),
                TemplateId = fret - 2
            });
        }

        var arrangement = new Arrangement { StringCount = stringCount };
        return new FretAnchorGenerationService()
            .GenerateAnchors(level, chordTemplates: [Template(0, 2), Template(1, 3)], tuning: arrangement);
    }

    [Fact]
    public void SevenString_OneFretGripBounce_KeepsTheLowAnchor()
    {
        var anchors = Generate(7);

        var anchor = Assert.Single(anchors);
        Assert.Equal(2, anchor.Fret);
    }

    [Fact]
    public void SixString_OneFretGripBounce_StillReplantsEveryGrip()
    {
        var anchors = Generate(6);

        Assert.Equal([2, 3, 2, 3, 2], anchors.Select(a => a.Fret));
    }
}
