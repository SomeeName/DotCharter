using DotCharter.Services.Editing.Collisions;
using DotCharter.Services.Editing.Validation.Rules;
using DotCharter.Tests.Services.Editing.HandShapes;

namespace DotCharter.Tests.Services.Editing.Improvement;

/// <summary>
///     Tools ▸ Improve Chart's hand-shape lengths and the D15 check (#758) share ONE gap
///     (<c>HandShapeGap</c>) under the same minimum note distance setting, so a shape the pass leaves
///     behind is never reported by the check.
///     The grid is 120 BPM in 4/4.
/// </summary>
public sealed class ChartImprovementHandShapeGapTests
{
    private static readonly (int, int)[] G5 = [(0, 3), (1, 5), (2, 5)];
    private static readonly (int, int)[] A5 = [(0, 5), (1, 7), (2, 7)];

    private static List<BeatMarker> Beats() =>
        [.. Enumerable.Range(0, 32).Select(i => new BeatMarker
        {
            Time = i * 500.0, Bpm = 120, IsMeasure = i % 4 == 0, BeatsInMeasure = 4
        })];

    private static FractionalPosition At(int sixteenths) => new(0, new Fraction(sixteenths, 16));

    private static HandShape Shape(int start, int end) => new() { Position = At(start), EndPosition = At(end) };

    private static int Improve(ChartLevel level, List<BeatMarker> beats, AppConfig? config = null) =>
        (int)typeof(ChartImprovementService)
            .GetMethod("ImproveHandShapeLengths",
                System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Static)!
            .Invoke(null, [level, beats, ChartSpacingEnforcer.SpacingGapOf(config ?? new AppConfig())])!;

    private static List<ArrangementIssue> Check(Arrangement arr, List<BeatMarker> beats, AppConfig? config = null)
    {
        var issues = new List<ArrangementIssue>();
        new HandShapeGapRule(new FakeConfigurationService { AppConfig = config ?? new AppConfig() })
            .Validate(ArrangementValidationContext.Build(arr, beats, null), issues);
        return issues;
    }

    /// <summary>The pass lengthens a short shape up to the next single note; it must stop the
    /// check's gap before it, not on it.</summary>
    [Fact]
    public void ExtendingAShapeTowardsASingleNote_LeavesTheChecksGap()
    {
        var arr = ChordHandShapePlannerTests.Arr();
        var level = arr.Levels[0];
        level.Chords.Add(ChordHandShapePlannerTests.Chord(0, 0, G5));
        level.Notes.Add(new GuitarNote { Position = At(24), StringIndex = 0, Fret = 3 });
        level.Chords.Add(ChordHandShapePlannerTests.Chord(3, 0, A5));
        var shape = Shape(0, 16);
        level.HandShapes.Add(shape);
        level.HandShapes.Add(Shape(48, 56));
        var beats = Beats();

        Improve(level, beats);

        Assert.True(shape.EndPosition > At(16), "the pass still lengthens the shape");
        Assert.Empty(Check(arr, beats));
    }

    /// <summary>#817: a shape over a held chord that touches the next note is trimmed to the same
    /// minimum gap as any other shape.</summary>
    [Fact]
    public void AHeldChordTouchingTheNextNote_IsTrimmedToTheMinimumGap()
    {
        var arr = ChordHandShapePlannerTests.Arr();
        var level = arr.Levels[0];
        level.Chords.Add(ChordHandShapePlannerTests.Chord(0, 2, G5));
        level.Notes.Add(new GuitarNote { Position = At(32), StringIndex = 3, Fret = 5 });
        level.Chords.Add(ChordHandShapePlannerTests.Chord(4, 0, A5));
        var shape = Shape(0, 32);
        level.HandShapes.Add(shape);
        level.HandShapes.Add(Shape(64, 72));
        var beats = Beats();

        Improve(level, beats);

        Assert.Equal(At(31), shape.EndPosition);
        Assert.Empty(Check(arr, beats));
    }

    /// <summary>#808: both read the minimum note distance setting, so under a larger one the pass
    /// trims a shape touching the next note to exactly the gap the check then accepts.</summary>
    [Fact]
    public void UnderALargerMinimumNoteDistance_ThePassTrimsToTheChecksGap()
    {
        var arr = ChordHandShapePlannerTests.Arr();
        var level = arr.Levels[0];
        level.Chords.Add(ChordHandShapePlannerTests.Chord(0, 0, G5));
        level.Notes.Add(new GuitarNote { Position = At(32), StringIndex = 3, Fret = 5 });
        level.Chords.Add(ChordHandShapePlannerTests.Chord(3, 0, A5));
        var shape = Shape(0, 32);
        level.HandShapes.Add(shape);
        level.HandShapes.Add(Shape(48, 56));
        var beats = Beats();
        var config = new AppConfig { MinNoteDistanceMode = MinNoteDistanceMode.BeatFraction, MinNoteDistanceDivisor = 8 };
        Assert.Single(Check(arr, beats, config));

        Improve(level, beats, config);

        Assert.Equal(At(30), shape.EndPosition);
        Assert.Empty(Check(arr, beats, config));
    }
}
