using DotCharter.Services.Editing.Fingering;
using DotCharter.Services.Editing.FretAnchors;

namespace DotCharter.Tests.Services.Editing.Fingering;

/// <summary>
///     #943: a grip that is part of the grip next to it takes that grip's
///     fingers. The owner's case: <c>14 16 x 16</c> on A–D–B beside
///     <c>14 16 16 16</c>, which the suggester fingers with a ring barre
///     (<c>1 3 3 3</c>); alone the three-string grip gets ring + pinky
///     (<c>1 3 x 4</c>), and the owner re-fingered it <c>1 3 x 3</c>.
/// </summary>
public sealed class NeighbourGripFingeringTests
{
    private static readonly Dictionary<int, int> ThreeStrings = new() { [1] = 14, [2] = 16, [4] = 16 };
    private static readonly Dictionary<int, int> FourStrings = new() { [1] = 14, [2] = 16, [3] = 16, [4] = 16 };

    private static Arrangement Guitar() =>
        new() { StringCount = 6, Tuning = ["E", "A", "D", "G", "B", "E"] };

    private static ChordTemplate Grip(int id, Dictionary<int, int> frets, Dictionary<int, int>? fingers = null)
    {
        var template = new ChordTemplate { Id = id, Frets = new Dictionary<int, int>(frets) };
        if (fingers != null)
        {
            foreach (var (s, f) in fingers) template.Fingers[s] = f;
        }
        else
        {
            ChordTemplateFingerSetter.SetSuggestedFingers(template, 6);
        }

        return template;
    }

    private static Chord ChordOf(ChordTemplate template, double beat)
    {
        var chord = new Chord { Position = new FractionalPosition((int)(beat * 4), 4), TemplateId = template.Id };
        foreach (var (s, f) in template.Frets) chord.ChordNotes.Add(new ChordNote { StringIndex = s, Fret = f });
        return chord;
    }

    [Fact]
    public void TheSuggesterAloneFingersTheSubsetWithThePinky()
    {
        // The premise of the rule: without its neighbour the grip is 1 3 x 4.
        Assert.Equal(new Dictionary<int, int> { [1] = 1, [2] = 3, [4] = 4 }, Grip(0, ThreeStrings).Fingers);
        Assert.Equal(new Dictionary<int, int> { [1] = 1, [2] = 3, [3] = 3, [4] = 3 }, Grip(1, FourStrings).Fingers);
    }

    [Fact]
    public void ASubsetTakesTheNextGripsFingers()
    {
        var small = Grip(0, ThreeStrings);
        var big = Grip(1, FourStrings);
        var byId = new Dictionary<int, ChordTemplate> { [0] = small, [1] = big };

        var plan = NeighbourGripFingering.Plan([[0, 1]], byId, _ => true, t => t.Fingers);

        Assert.Equal(new Dictionary<int, int> { [1] = 1, [2] = 3, [4] = 3 }, plan[0]);
        Assert.False(plan.ContainsKey(1));
    }

    [Fact]
    public void ADifferentFretOrALockedGripIsNotTouched()
    {
        var small = Grip(0, ThreeStrings);
        var moved = Grip(1, new Dictionary<int, int> { [1] = 12, [2] = 14, [3] = 14, [4] = 14 });
        var byId = new Dictionary<int, ChordTemplate> { [0] = small, [1] = moved, [2] = Grip(2, FourStrings) };

        Assert.Empty(NeighbourGripFingering.Plan([[1, 0, 1]], byId, _ => true, t => t.Fingers));
        Assert.Empty(NeighbourGripFingering.Plan([[0, 2]], byId, t => t.Id != 0, t => t.Fingers));
    }

    [Fact]
    public void WhereBothNeighboursQualifyTheOneWithMoreStringsWins()
    {
        var two = Grip(0, new Dictionary<int, int> { [1] = 14, [2] = 16 }, new Dictionary<int, int> { [1] = 2, [2] = 4 });
        var three = Grip(1, ThreeStrings, new Dictionary<int, int> { [1] = 1, [2] = 4, [4] = 4 });
        var four = Grip(2, FourStrings);
        var byId = new Dictionary<int, ChordTemplate> { [0] = two, [1] = three, [2] = four };

        // Before: the three-string grip; after: the four-string one.
        var plan = NeighbourGripFingering.Plan([[1, 0, 2]], byId, t => t.Id == 0, t => t.Fingers);

        Assert.Equal(new Dictionary<int, int> { [1] = 1, [2] = 3 }, plan[0]);
    }

    [Fact]
    public void GripSequenceCollapsesAChordAndTheHandShapeOverIt()
    {
        var level = new ChartLevel();
        level.HandShapes.Add(new HandShape { Position = new FractionalPosition(0), EndPosition = new FractionalPosition(2), TemplateId = 3 });
        level.Chords.Add(new Chord { Position = new FractionalPosition(0), TemplateId = 3 });
        level.Chords.Add(new Chord { Position = new FractionalPosition(1), TemplateId = 3 });
        level.Chords.Add(new Chord { Position = new FractionalPosition(2), TemplateId = 5 });

        Assert.Equal([3, 5], NeighbourGripFingering.GripSequence(level));
    }

    [Fact]
    public void ATabImportFingersTheSubsetLikeItsNeighbour()
    {
        var arrangement = Guitar();
        var small = Grip(0, ThreeStrings);
        var big = Grip(1, FourStrings);
        arrangement.ChordTemplates.Add(small);
        arrangement.ChordTemplates.Add(big);
        var level = new ChartLevel();
        arrangement.Levels.Add(level);
        level.Chords.Add(ChordOf(small, 0));
        level.HandShapes.Add(new HandShape { Position = new FractionalPosition(0), EndPosition = new FractionalPosition(4), TemplateId = 0 });
        level.Chords.Add(ChordOf(big, 4));

        ImportFhpPass.Run(arrangement, level, bassArrangement: false, barlines: []);

        var taken = arrangement.ChordTemplates.Single(t => t.Id == level.Chords[0].TemplateId);
        Assert.Equal(new Dictionary<int, int> { [1] = 1, [2] = 3, [4] = 3 }, taken.Fingers);
        Assert.Equal(taken.Id, level.HandShapes[0].TemplateId);
        // The template the import moved every user off is gone.
        Assert.Equal(2, arrangement.ChordTemplates.Count);
    }
}
