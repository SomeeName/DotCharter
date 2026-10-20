using System.Collections.ObjectModel;
using DotCharter.Services.Editing;
using DotCharter.Services.Editing.Fingering;
using DotCharter.Services.Editing.Fixing;
using DotCharter.Services.Editing.FretAnchors;

namespace DotCharter.Tests.Services.Editing.Fingering;

/// <summary>
///     #511: the automatic fingering of a power chord or a two-fret octave follows
///     the 1·3 / 1·4 choice the ARRANGEMENT already made (the majority of its
///     templates of that pair), because the owner's charts make it per song. These
///     pin the majority (ties and no votes included), which templates vote, that a
///     template never votes for itself, and that the hint reaches every automatic
///     fingering: the editing resolver, the fingering service (Refret), the FHP
///     follower's free-grip test and the tab import.
/// </summary>
public sealed class ArrangementFingeringStyleTests
{
    private static Arrangement Guitar(params int[] offsets)
    {
        var arrangement = new Arrangement { StringCount = 6, Tuning = ["E", "A", "D", "G", "B", "E"] };
        if (offsets.Length > 0)
            arrangement.TuningPitches = new ObservableCollection<TuningOffset>(offsets.Select(o => new TuningOffset(o)));
        return arrangement;
    }

    private static ChordTemplate Shape(int id, (int String, int Fret)[] frets, params (int String, int Finger)[] fingers)
    {
        var template = new ChordTemplate { Id = id };
        foreach (var (s, f) in frets) template.Frets[s] = f;
        foreach (var (s, f) in fingers) template.Fingers[s] = f;
        return template;
    }

    // Root on the low E, the fifth on A two frets up.
    private static ChordTemplate PowerChord(int id, int fret, int upperFinger) =>
        Shape(id, [(0, fret), (1, fret + 2)], (0, 1), (1, upperFinger));

    // Root on the low E, the octave on D two frets up.
    private static ChordTemplate Octave(int id, int fret, int upperFinger) =>
        Shape(id, [(0, fret), (2, fret + 2)], (0, 1), (2, upperFinger));

    private static Arrangement With(params ChordTemplate[] templates)
    {
        var arrangement = Guitar();
        foreach (var t in templates) arrangement.ChordTemplates.Add(t);
        return arrangement;
    }

    [Theory]
    [InlineData(3, 1, PairFingering.IndexPinky)]
    [InlineData(1, 3, PairFingering.IndexRing)]
    [InlineData(2, 1, PairFingering.IndexPinky)]
    [InlineData(2, 2, null)]
    [InlineData(0, 0, null)]
    public void TheMajorityIsStrictAndATieKeepsTheDefault(int indexPinky, int indexRing, PairFingering? expected) =>
        Assert.Equal(expected, ArrangementFingeringStyle.Majority(indexPinky, indexRing));

    [Fact]
    public void EachPairHasItsOwnMajority()
    {
        var arrangement = With(PowerChord(0, 3, 4), PowerChord(1, 5, 4), PowerChord(2, 7, 3),
            Octave(3, 3, 3), Octave(4, 5, 3));

        var style = ArrangementFingeringStyle.Of(arrangement);

        Assert.Equal(PairFingering.IndexPinky, style.PowerChord);
        Assert.Equal(PairFingering.IndexRing, style.TwoFretOctave);
    }

    [Fact]
    public void OnlyTheTwoChoicesOnARealPairVote()
    {
        var arrangement = With(
            PowerChord(0, 5, 3),
            Shape(1, [(0, 5), (1, 7)], (0, 2), (1, 4)), // 2·4: a deliberate other fingering
            Shape(2, [(0, 3), (1, 5)], (0, 2), (1, 4)),
            Shape(3, [(0, 5), (1, 7)], (0, 0), (1, 4)), // a thumb
            Shape(4, [(3, 5), (4, 8)], (3, 1), (4, 4)), // G-B power chord: three frets, always 1·4
            Shape(5, [(3, 5), (4, 8)], (3, 1), (4, 4)),
            Shape(6, [(2, 7), (4, 10)], (2, 1), (4, 4)), // three-fret octave
            Shape(7, [(0, 5), (1, 7), (2, 7)], (0, 1), (1, 3), (2, 4))); // root·fifth·octave

        var style = ArrangementFingeringStyle.Of(arrangement);

        Assert.Equal(PairFingering.IndexRing, style.PowerChord); // the one 1·3 is the only vote
        Assert.Null(style.TwoFretOctave);
        Assert.Null(style.HintFor(arrangement, arrangement.ChordTemplates[4]));
        Assert.Null(style.HintFor(arrangement, arrangement.ChordTemplates[7]));
    }

    [Fact]
    public void ThePairIsJudgedByItsSoundInDropD()
    {
        var dropD = Guitar(-2, 0, 0, 0, 0, 0);
        // Low D + A two frets up is a sixth there, not a power chord; A + D still is.
        Assert.Null(TwoFretPairFingering.Classify(dropD, PowerChord(0, 5, 4), out _, out _));
        Assert.Equal(TwoFretPair.PowerChord,
            TwoFretPairFingering.Classify(dropD, Shape(1, [(1, 5), (2, 7)]), out _, out _));
    }

    [Fact]
    public void ANewPowerChordFromAnEditFollowsTheArrangement()
    {
        var arrangement = With(PowerChord(0, 3, 4), PowerChord(1, 5, 4), Octave(2, 3, 3));
        var created = new List<ChordTemplate>();

        var power = ChordTemplateResolver.Resolve(arrangement, new Dictionary<int, int> { [1] = 7, [2] = 9 },
            ArpeggioFlavour.Plain, null, created);
        var octave = ChordTemplateResolver.Resolve(arrangement, new Dictionary<int, int> { [1] = 7, [3] = 9 },
            ArpeggioFlavour.Plain, null, created);

        Assert.Equal(new Dictionary<int, int> { [1] = 1, [2] = 4 }, power.Fingers);
        Assert.Equal(new Dictionary<int, int> { [1] = 1, [3] = 3 }, octave.Fingers);
    }

    [Fact]
    public void WithNoClearMajorityTheSuggesterKeepsItsOwnChoice()
    {
        var tied = With(PowerChord(0, 3, 4), PowerChord(1, 5, 3));
        var created = new List<ChordTemplate>();

        var power = ChordTemplateResolver.Resolve(tied, new Dictionary<int, int> { [1] = 7, [2] = 9 },
            ArpeggioFlavour.Plain, null, created);

        Assert.Equal(3, power.Fingers[2]); // the suggester's 1·3
    }

    [Fact]
    public void ATemplateNeverVotesForItself()
    {
        // The only power chord is the one being suggested: no OTHER template says 1·4.
        var only = PowerChord(0, 5, 4);
        var arrangement = With(only);
        var probe = only.Clone();

        // A copy is ANOTHER template, and for it the 1·4 is a vote.
        ArrangementFingeringStyle.SetSuggestedFingers(arrangement, probe, 6);
        ArrangementFingeringStyle.SetSuggestedFingers(arrangement, only, 6);

        Assert.Equal(4, probe.Fingers[1]);
        Assert.Equal(3, only.Fingers[1]);
    }

    [Fact]
    public void TheFingeringServiceFollowsTheArrangementOnlyWhenGivenOne()
    {
        var arrangement = With(PowerChord(0, 3, 4), PowerChord(1, 5, 4));
        var unfingered = Shape(2, [(1, 7), (2, 9)]);
        var service = new FingeringGenerationService();

        Assert.Equal(4, service.SuggestFingeringFor(arrangement, unfingered)!.Fingers[2]);
        Assert.Equal(3, service.SuggestFingeringFor(unfingered, 6)!.Fingers[2]);
    }

    [Fact]
    public void AResizedHandShapeGripFollowsTheArrangement()
    {
        // The bar owned a one-string grip; resized over a power chord it becomes one.
        var current = Shape(5, [(1, 7)], (1, 1));
        var arrangement = With(PowerChord(0, 3, 4), PowerChord(1, 5, 4), current);
        var seed = Shape(-1, [(1, 7), (2, 9)]);

        var rebuilt = ArrangementFingeringStyle.RebuildHandShapeGrip(arrangement, current, seed, 6)!;

        Assert.Equal(new Dictionary<int, int> { [1] = 1, [2] = 4 }, rebuilt.Fingers);
        Assert.Equal(3, HandShapeAutoTemplate.Rebuild(current, seed, 6)!.Fingers[2]);
    }

    [Fact]
    public void TheGripAResizeReplacesDoesNotVote()
    {
        // The bar's own old grip is the only 1·4 power chord: nothing ELSE says 1·4.
        var current = Shape(5, [(0, 5), (1, 7)], (0, 1), (1, 4));
        var arrangement = With(current);
        var seed = Shape(-1, [(1, 7), (2, 9)]);

        var rebuilt = ArrangementFingeringStyle.RebuildHandShapeGrip(arrangement, current, seed, 6)!;

        Assert.Equal(3, rebuilt.Fingers[2]);
    }

    [Fact]
    public void TheFollowerJudgesAFreeGripAgainstTheArrangementsStyle()
    {
        var pinky = PowerChord(0, 5, 4);
        var ring = PowerChord(1, 7, 3);
        var arrangement = With(pinky, ring, PowerChord(2, 3, 4), PowerChord(3, 9, 4));

        var resolver = ChordFingeringFollower.FreeGripResolver(arrangement);
        Assert.NotNull(resolver(pinky)); // the arrangement's own suggestion: free
        Assert.Null(resolver(ring)); // against it: authored, pins its anchor

        // An explicit hint replaces the arrangement's: none = the plain suggester.
        var plain = ChordFingeringFollower.FreeGripResolver(arrangement, null, _ => null);
        Assert.Null(plain(pinky));
        Assert.NotNull(plain(ring));
    }

    [Fact]
    public void ALoneAuthoredPairIsNotItsOwnSuggestion()
    {
        // The song's one power chord, typed 1·4: with no OTHER template saying
        // 1·4 it is still authored and keeps pinning its anchor.
        var lone = PowerChord(0, 5, 4);
        var arrangement = With(lone, Octave(1, 3, 4));

        Assert.Null(ChordFingeringFollower.FreeGripResolver(arrangement)(lone));
        Assert.Null(ArrangementFingeringStyle.HintsFromOthers(arrangement)(lone));
        Assert.Equal(PairFingering.IndexPinky, ArrangementFingeringStyle.HintsFromOthers(arrangement)(lone.Clone()));
    }

    [Fact]
    public void ATabImportFingersItsUnfingeredPairsTheWayTheFileDoes()
    {
        var authored = PowerChord(0, 3, 4);
        var suggested = PowerChord(1, 5, 3);
        var deliberate = Shape(2, [(1, 5), (2, 7)], (1, 2), (2, 4));
        var arrangement = With(authored, suggested, deliberate);
        var level = new ChartLevel();
        arrangement.Levels.Add(level);
        for (var i = 0; i < 3; i++)
        {
            var template = arrangement.ChordTemplates[i];
            var chord = new Chord { Position = new FractionalPosition(2 + 4 * i), TemplateId = template.Id };
            foreach (var (s, f) in template.Frets) chord.ChordNotes.Add(new ChordNote { StringIndex = s, Fret = f });
            level.Chords.Add(chord);
        }

        ImportFhpPass.Run(arrangement, level, bassArrangement: false, t => ReferenceEquals(t, authored));

        Assert.Equal(4, suggested.Fingers[1]);
        Assert.Equal(new Dictionary<int, int> { [1] = 2, [2] = 4 }, deliberate.Fingers);
        Assert.Equal(4, authored.Fingers[1]);
    }

    [Fact]
    public void WithNothingAuthoredTheImportChangesNoFingering()
    {
        var arrangement = With(PowerChord(0, 3, 4), PowerChord(1, 5, 3), PowerChord(2, 7, 4));

        Assert.Equal(0, ArrangementFingeringStyle.FollowAuthored(arrangement, _ => false));
        Assert.Equal(3, arrangement.ChordTemplates[1].Fingers[1]);
    }
}
