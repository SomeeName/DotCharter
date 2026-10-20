using DotCharter.Services.Editing.Fixing;
using DotCharter.Services.Editing.Validation.Rules;
using DotCharter.Tests.Services.Editing.HandShapes;

namespace DotCharter.Tests.Services.Editing.Validation;

/// <summary>
///     D11 (<see cref="ChordOutsideHandShapeRule" />, #479): a chord no hand shape covers.
///     No other rule could see it — they all start FROM a shape — and the export ships
///     such a chord with no grip. Plus its auto-fix, which is the "Generate hand shapes"
///     planner (#478) on the same coverage predicate.
/// </summary>
public sealed class ChordOutsideHandShapeRuleTests
{
    private static readonly (int, int)[] G5 = [(0, 3), (1, 5), (2, 5)];

    private static List<BeatMarker> Beats(int count = 32) =>
        [.. Enumerable.Range(0, count).Select(i => new BeatMarker { Time = i * 500.0, Bpm = 120 })];

    private static List<ArrangementIssue> Run(Arrangement arr)
    {
        var issues = new List<ArrangementIssue>();
        new ChordOutsideHandShapeRule().Validate(ArrangementValidationContext.Build(arr, Beats(), null), issues);
        return issues;
    }

    private static HandShape Bar(double start, double end) => new()
    {
        Position = ChordHandShapePlannerTests.Pos(start), EndPosition = ChordHandShapePlannerTests.Pos(end)
    };

    /// <summary>A Warning: every finding measured on a finished chart (917 over the
    /// RsCharter and official corpora, none in EOF's or the owner's) is a chord ON a
    /// shape's end — DLC Builder's cosmetic I19. See the enum member.</summary>
    [Fact]
    public void TheCodeIsD11_AndItIsAWarning()
    {
        Assert.Equal("D11", ArrangementIssueType.ChordOutsideHandShape.GetCode());
        Assert.Equal(ArrangementIssueSeverity.Warning, ArrangementIssueType.ChordOutsideHandShape.GetSeverity());
    }

    [Fact]
    public void AChordInsideABar_IsFine()
    {
        var arr = ChordHandShapePlannerTests.Arr();
        arr.Levels[0].Chords.Add(ChordHandShapePlannerTests.Chord(1, 0, G5));
        arr.Levels[0].HandShapes.Add(Bar(1, 2));

        Assert.Empty(Run(arr));
    }

    [Fact]
    public void AChordOnTheBarsEnd_IsOutsideIt()
    {
        // Half-open: [0, 2) ends where the chord at 2 begins.
        var arr = ChordHandShapePlannerTests.Arr();
        var chord = ChordHandShapePlannerTests.Chord(2, 0, G5);
        arr.Levels[0].Chords.Add(chord);
        arr.Levels[0].HandShapes.Add(Bar(0, 2));

        var issue = Assert.Single(Run(arr));

        Assert.Equal(ArrangementIssueType.ChordOutsideHandShape, issue.Type);
        Assert.Same(chord, issue.Chord);
        Assert.Equal(1000.0, issue.TimeMs);
        Assert.StartsWith("Chord sits exactly on the end of a hand shape", issue.Message);
    }

    [Fact]
    public void AChordFarFromAnyBar_SaysItHasNoGrip()
    {
        var arr = ChordHandShapePlannerTests.Arr();
        arr.Levels[0].Chords.Add(ChordHandShapePlannerTests.Chord(2, 0, G5));
        arr.Levels[0].HandShapes.Add(Bar(0, 1));

        var issue = Assert.Single(Run(arr));

        Assert.StartsWith("Chord is inside no hand shape", issue.Message);
    }

    [Fact]
    public void DeletingOneBar_MakesTheRuleFire()
    {
        // Non-vacuity on the "inside" fixture: the same chord, the bar taken away.
        var arr = ChordHandShapePlannerTests.Arr();
        arr.Levels[0].Chords.Add(ChordHandShapePlannerTests.Chord(1, 0, G5));
        var bar = Bar(1, 2);
        arr.Levels[0].HandShapes.Add(bar);
        Assert.Empty(Run(arr));

        arr.Levels[0].HandShapes.Remove(bar);

        Assert.Single(Run(arr));
    }

    [Fact]
    public void EachLevelIsJudgedOnItsOwnBars()
    {
        // The bar on level 0 does not cover level 1's chord at the same beat.
        var arr = ChordHandShapePlannerTests.Arr(levels: 2);
        arr.Levels[0].Chords.Add(ChordHandShapePlannerTests.Chord(1, 0, G5));
        arr.Levels[0].HandShapes.Add(Bar(1, 2));
        var lonely = ChordHandShapePlannerTests.Chord(1, 0, G5);
        arr.Levels[1].Chords.Add(lonely);

        var issue = Assert.Single(Run(arr));

        Assert.Same(lonely, issue.Chord);
    }

    [Fact]
    public void TheSameChordOnManyLevels_IsOneFinding()
    {
        var arr = ChordHandShapePlannerTests.Arr(levels: 4);
        foreach (var level in arr.Levels) level.Chords.Add(ChordHandShapePlannerTests.Chord(1, 0, G5));
        arr.Levels[3].Chords.Add(ChordHandShapePlannerTests.Chord(3, 0, G5));

        var issues = Run(arr);

        Assert.Equal(2, issues.Count);
        Assert.Contains("on 4 difficulty levels", issues[0].Message);
        Assert.Same(arr.Levels[3].Chords[0], issues[0].Chord); // the highest level's chord
        Assert.DoesNotContain("difficulty levels", issues[1].Message);
    }

    [Fact]
    public void AChordWithNoNotes_IsNotThisRulesBusiness()
    {
        var arr = ChordHandShapePlannerTests.Arr();
        arr.Levels[0].Chords.Add(ChordHandShapePlannerTests.Chord(1, 0));

        Assert.Empty(Run(arr));
    }

    [Fact]
    public void AnUnsortedLevel_IsJudgedTheSame()
    {
        // The sweep assumes position order; anything else takes the index path.
        var arr = ChordHandShapePlannerTests.Arr();
        var level = arr.Levels[0];
        level.Chords.Add(ChordHandShapePlannerTests.Chord(5, 0, G5));
        level.Chords.Add(ChordHandShapePlannerTests.Chord(1, 0, G5));
        level.HandShapes.Add(Bar(4, 6));
        level.HandShapes.Add(Bar(0, 1));

        var issue = Assert.Single(Run(arr));

        Assert.Equal(ChordHandShapePlannerTests.Pos(1), issue.Chord!.Position);
    }

    [Fact]
    public void TheRuleRunsInTheDefaultSet()
    {
        var arr = ChordHandShapePlannerTests.Arr();
        arr.Levels[0].Chords.Add(ChordHandShapePlannerTests.Chord(1, 0, G5));

        var issues = new ArrangementValidationService(new FakeAppLogger()).Validate(arr, Beats());

        Assert.Contains(issues, i => i.Type == ArrangementIssueType.ChordOutsideHandShape);
    }

    [Fact]
    public void AutoFix_ClearsEveryFinding_OnEveryLevel_AndIsAFixedPoint()
    {
        var arr = ChordHandShapePlannerTests.Arr(levels: 2);
        arr.Levels[0].Chords.Add(ChordHandShapePlannerTests.Chord(0, 0, G5));
        arr.Levels[0].Chords.Add(ChordHandShapePlannerTests.Chord(2, 0, G5));
        arr.Levels[0].HandShapes.Add(Bar(0, 2)); // the chord ON its end is outside
        arr.Levels[1].Chords.Add(ChordHandShapePlannerTests.Chord(0, 1, G5));
        arr.Levels[1].Chords.Add(ChordHandShapePlannerTests.Chord(1, 1, G5));
        Assert.Equal(3, Run(arr).Count); // beats 2 (level 0), 0 and 1 (level 1)

        var first = new ArrangementFixResult();
        new ArrangementAutoFixService().Fix(arr, first);

        Assert.Empty(Run(arr));
        Assert.Equal(2, first.Changes.GetValueOrDefault("handShapesAddedOverChords"));
        Assert.Equal(2, arr.Levels[0].HandShapes.Count); // the authored bar stayed
        Assert.Equal(ChordHandShapePlannerTests.Pos(2), arr.Levels[0].HandShapes[0].EndPosition);
        Assert.Single(arr.Levels[1].HandShapes); // two strums, one bar

        var second = new ArrangementFixResult();
        new ArrangementAutoFixService().Fix(arr, second);
        Assert.Equal(0, second.Changes.GetValueOrDefault("handShapesAddedOverChords"));
        Assert.Equal(0, second.TotalChanges);
    }
}
