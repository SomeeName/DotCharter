using DotCharter.Services.Editing.Validation.Rules;
using DotCharter.Tests.Services.Editing.HandShapes;

namespace DotCharter.Tests.Services.Editing.Validation;

/// <summary>
///     D15 (<see cref="HandShapeGapRule" />, #758, #808): a hand shape must end at least the
///     minimum note distance before the next note or chord, measured in beats; a held chord
///     gets no larger gap (#817). The grid is 120 BPM in 4/4, so one beat is 500 ms.
/// </summary>
public sealed class HandShapeGapRuleTests
{
    private const string Short = "Validation_HandShapeGap";
    private static readonly (int, int)[] G5 = [(0, 3), (1, 5), (2, 5)];
    private static readonly (int, int)[] A5 = [(0, 5), (1, 7), (2, 7)];

    private static List<BeatMarker> Beats(int count = 32) =>
        [.. Enumerable.Range(0, count).Select(i => new BeatMarker { Time = i * 500.0, Bpm = 120 })];

    private static List<ArrangementIssue> Run(Arrangement arr)
    {
        var issues = new List<ArrangementIssue>();
        new HandShapeGapRule().Validate(ArrangementValidationContext.Build(arr, Beats(), null), issues);
        return issues;
    }

    /// <summary>A position <paramref name="sixteenths" /> sixteenths of a beat from the start.</summary>
    private static FractionalPosition At(int sixteenths) => new(0, new Fraction(sixteenths, 16));

    private static HandShape Shape(int startSixteenths, int endSixteenths) => new()
    {
        Position = At(startSixteenths), EndPosition = At(endSixteenths)
    };

    /// <summary>A G5 at beat 0 under a shape ending <paramref name="endSixteenths" />, then the next chord at beat 2.</summary>
    private static (Arrangement Arr, HandShape Shape) ChordThenChordAtBeat2(int endSixteenths, double sustain = 0,
        (int, int)[]? next = null)
    {
        var arr = ChordHandShapePlannerTests.Arr();
        var shape = Shape(0, endSixteenths);
        arr.Levels[0].Chords.Add(ChordHandShapePlannerTests.Chord(0, sustain, G5));
        arr.Levels[0].Chords.Add(ChordHandShapePlannerTests.Chord(2, 0, next ?? A5));
        arr.Levels[0].HandShapes.Add(shape);
        arr.Levels[0].HandShapes.Add(Shape(32, 40));
        return (arr, shape);
    }

    [Fact]
    public void TheCodeIsD15_AndItIsAWarning()
    {
        Assert.Equal("D15", ArrangementIssueType.HandShapeGap.GetCode());
        Assert.Equal(ArrangementIssueSeverity.Warning, ArrangementIssueType.HandShapeGap.GetSeverity());
    }

    [Fact]
    public void AShapeTouchingTheNextChord_WarnsOnTheShapeAtItsEnd()
    {
        var (arr, shape) = ChordThenChordAtBeat2(32);

        var finding = Assert.Single(Run(arr));

        Assert.Equal(ArrangementIssueType.HandShapeGap, finding.Type);
        Assert.Equal(Short, finding.MessageKey);
        Assert.Same(shape, finding.HandShape);
        Assert.Equal(1000, finding.TimeMs);
    }

    [Fact]
    public void TheIdenticalNextChord_StillNeedsTheGap()
    {
        var (arr, _) = ChordThenChordAtBeat2(32, next: G5);

        Assert.Equal(Short, Assert.Single(Run(arr)).MessageKey);
    }

    [Fact]
    public void ASingleNoteRightAfterTheShape_CountsAsTheNextOnset()
    {
        var arr = ChordHandShapePlannerTests.Arr();
        arr.Levels[0].Chords.Add(ChordHandShapePlannerTests.Chord(0, 0, G5));
        arr.Levels[0].Notes.Add(new GuitarNote { Position = At(31), StringIndex = 0, Fret = 3 });
        arr.Levels[0].Chords.Add(ChordHandShapePlannerTests.Chord(3, 0, A5));
        arr.Levels[0].HandShapes.Add(Shape(0, 31));
        arr.Levels[0].HandShapes.Add(Shape(48, 56));

        Assert.Equal(Short, Assert.Single(Run(arr)).MessageKey);
    }

    /// <summary>The base gap is the app's minimum note distance, 1/64 of a 4/4 measure = 1/16
    /// beat (#808): a shape ending on the next chord warns, one leaving 1/16 beat passes.</summary>
    [Theory]
    [InlineData(32, true)]
    [InlineData(31, false)]
    [InlineData(30, false)]
    public void TheShortGap_IsTheMinimumNoteDistance(int endSixteenths, bool warns)
    {
        var (arr, _) = ChordThenChordAtBeat2(endSixteenths);

        AssertWarns(warns, Short, Run(arr));
    }

    [Fact]
    public void TheMessage_NamesTheMinimumNoteDistance()
    {
        var (arr, _) = ChordThenChordAtBeat2(32);

        Assert.Equal(["1/16"], Assert.Single(Run(arr)).MessageArgs);
    }

    private static List<ArrangementIssue> RunWith(Arrangement arr, AppConfig config)
    {
        var issues = new List<ArrangementIssue>();
        new HandShapeGapRule(new FakeConfigurationService { AppConfig = config })
            .Validate(ArrangementValidationContext.Build(arr, Beats(), null), issues);
        return issues;
    }

    /// <summary>The check reads the setting the spacing clean-up reads: 1/8 beat set, a 1/16
    /// gap warns and the message names 1/8.</summary>
    [Fact]
    public void ALargerMinimumNoteDistanceSetting_IsTheGap()
    {
        var (arr, _) = ChordThenChordAtBeat2(31);
        var config = new AppConfig { MinNoteDistanceMode = MinNoteDistanceMode.BeatFraction, MinNoteDistanceDivisor = 8 };

        var finding = Assert.Single(RunWith(arr, config));

        Assert.Equal(["1/8"], finding.MessageArgs);
    }

    /// <summary>A zero minimum note distance leaves no gap to fall short of, but a shape ending ON
    /// the next chord still touches it.</summary>
    [Fact]
    public void WithNoMinimumNoteDistance_TouchingStillWarns()
    {
        var config = new AppConfig { MinNoteDistanceMode = MinNoteDistanceMode.Milliseconds, MinNoteDistanceMs = 0 };

        Assert.Single(RunWith(ChordThenChordAtBeat2(32).Arr, config));
        Assert.Empty(RunWith(ChordThenChordAtBeat2(31).Arr, config));
    }

    /// <summary>#817: a chord held a beat or longer gets no larger gap: leaving the minimum
    /// (1/16 beat) passes, touching the next chord still warns with the one message.</summary>
    [Theory]
    [InlineData(32, true)]
    [InlineData(31, false)]
    [InlineData(30, false)]
    public void AHeldChord_NeedsOnlyTheMinimumGap(int endSixteenths, bool warns)
    {
        var (arr, _) = ChordThenChordAtBeat2(endSixteenths, sustain: 1.5);

        AssertWarns(warns, Short, Run(arr));
    }

    private static void AssertWarns(bool warns, string key, List<ArrangementIssue> issues)
    {
        if (warns) Assert.Equal(key, Assert.Single(issues).MessageKey);
        else Assert.Empty(issues);
    }

    [Fact]
    public void AChordHeldUnderABeat_NeedsOnlyTheShortGap()
    {
        var (arr, _) = ChordThenChordAtBeat2(30, sustain: 0.75);

        Assert.Empty(Run(arr));
    }

    [Fact]
    public void TheLastShape_HasNothingToLeaveAGapBefore()
    {
        var arr = ChordHandShapePlannerTests.Arr();
        arr.Levels[0].Chords.Add(ChordHandShapePlannerTests.Chord(0, 0, G5));
        arr.Levels[0].HandShapes.Add(Shape(0, 32));

        Assert.Empty(Run(arr));
    }

    [Fact]
    public void TheLastShape_IsExempt_EvenWithANoteRightAfterIt()
    {
        var arr = ChordHandShapePlannerTests.Arr();
        arr.Levels[0].Chords.Add(ChordHandShapePlannerTests.Chord(0, 0, G5));
        arr.Levels[0].Notes.Add(new GuitarNote { Position = At(32), StringIndex = 0, Fret = 3 });
        arr.Levels[0].HandShapes.Add(Shape(0, 32));

        Assert.Empty(Run(arr));
    }

    /// <summary>A note that only continues a linked note (a vibrato or link-next tail) is held,
    /// not struck, so it is no next onset.</summary>
    [Fact]
    public void ALinkedContinuation_IsNotAnAttack()
    {
        var arr = ChordHandShapePlannerTests.Arr();
        var chord = ChordHandShapePlannerTests.Chord(0, 2, G5);
        chord.ChordNotes[0].LinkNext = true;
        arr.Levels[0].Chords.Add(chord);
        arr.Levels[0].Notes.Add(new GuitarNote { Position = At(32), StringIndex = 0, Fret = 3, Vibrato = true });
        arr.Levels[0].Chords.Add(ChordHandShapePlannerTests.Chord(4, 0, A5));
        arr.Levels[0].HandShapes.Add(Shape(0, 32));
        arr.Levels[0].HandShapes.Add(Shape(64, 72));

        Assert.Empty(Run(arr));
    }

    /// <summary>A chord that continues one linked string but strikes the others is an attack.</summary>
    [Fact]
    public void AChordStrikingNewStrings_IsAnAttack_EvenWithALinkedString()
    {
        var arr = ChordHandShapePlannerTests.Arr();
        var first = ChordHandShapePlannerTests.Chord(0, 2, G5);
        first.ChordNotes[0].LinkNext = true;
        arr.Levels[0].Chords.Add(first);
        arr.Levels[0].Chords.Add(ChordHandShapePlannerTests.Chord(2, 0, (0, 3), (3, 5)));
        arr.Levels[0].HandShapes.Add(Shape(0, 32));
        arr.Levels[0].HandShapes.Add(Shape(32, 40));

        Assert.Equal(Short, Assert.Single(Run(arr)).MessageKey);
    }

    [Fact]
    public void NotesInsideTheShape_AreNotTheNextOnset()
    {
        var arr = ChordHandShapePlannerTests.Arr();
        arr.Levels[0].Chords.Add(ChordHandShapePlannerTests.Chord(0, 0, G5));
        arr.Levels[0].Notes.Add(new GuitarNote { Position = At(8), StringIndex = 0, Fret = 3 });
        arr.Levels[0].Notes.Add(new GuitarNote { Position = At(31), StringIndex = 1, Fret = 5 });
        arr.Levels[0].Chords.Add(ChordHandShapePlannerTests.Chord(3, 0, A5));
        arr.Levels[0].HandShapes.Add(Shape(0, 32));
        arr.Levels[0].HandShapes.Add(Shape(48, 56));

        Assert.Empty(Run(arr));
    }

    [Fact]
    public void TheSameShapeOnSeveralLevels_IsOneFinding_OnTheHighestLevel()
    {
        var arr = ChordHandShapePlannerTests.Arr(levels: 2);
        HandShape? last = null;
        foreach (var level in arr.Levels)
        {
            level.Chords.Add(ChordHandShapePlannerTests.Chord(0, 0, G5));
            level.Chords.Add(ChordHandShapePlannerTests.Chord(2, 0, A5));
            last = Shape(0, 32);
            level.HandShapes.Add(last);
            level.HandShapes.Add(Shape(32, 40));
        }

        Assert.Same(last, Assert.Single(Run(arr)).HandShape);
    }

    /// <summary>RS XML keeps the file's level order, so the hardest level can come first; the
    /// finding still names its shape, the one the Issues tab shows by default.</summary>
    [Fact]
    public void TheHardestLevelWins_WhateverTheListOrder()
    {
        var arr = ChordHandShapePlannerTests.Arr(levels: 2);
        arr.Levels[0].DifficultyIndex = 1;
        arr.Levels[1].DifficultyIndex = 0;
        var shapes = new List<HandShape>();
        foreach (var level in arr.Levels)
        {
            level.Chords.Add(ChordHandShapePlannerTests.Chord(0, 0, G5));
            level.Chords.Add(ChordHandShapePlannerTests.Chord(2, 0, A5));
            shapes.Add(Shape(0, 32));
            level.HandShapes.Add(shapes[^1]);
            level.HandShapes.Add(Shape(32, 40));
        }

        Assert.Same(shapes[0], Assert.Single(Run(arr)).HandShape);
    }

    /// <summary>
    ///     #808, the owner's chart at 160 BPM 4/4: a hand shape over a two-note chord ends 7/16 of
    ///     a beat in, and the next note comes on the half beat. The app's own minimum note distance
    ///     (1/64 of a 4/4 measure, 1/16 beat) is the gap its spacing clean-up leaves before every
    ///     next sound, so a shape that leaves it does not touch the next note.
    /// </summary>
    [Fact]
    public void AShapeLeavingTheMinimumNoteDistance_DoesNotTouchTheNextNote()
    {
        var arr = ChordHandShapePlannerTests.Arr();
        arr.Levels[0].Chords.Add(ChordHandShapePlannerTests.Chord(0, 0, (1, 5), (2, 7)));
        arr.Levels[0].Notes.Add(new GuitarNote { Position = At(8), StringIndex = 1, Fret = 5 });
        arr.Levels[0].Chords.Add(ChordHandShapePlannerTests.Chord(2, 0, (1, 5), (2, 7)));
        arr.Levels[0].HandShapes.Add(Shape(0, 7));
        arr.Levels[0].HandShapes.Add(Shape(32, 40));
        var at160 = Enumerable.Range(0, 32).Select(i => new BeatMarker { Time = i * 375.0, Bpm = 160 }).ToList();
        var issues = new List<ArrangementIssue>();

        new HandShapeGapRule().Validate(ArrangementValidationContext.Build(arr, at160, null), issues);

        Assert.Empty(issues);
    }

    [Fact]
    public void TheGapIsCountedInBeats_SoTempoDoesNotMatter()
    {
        var (arr, _) = ChordThenChordAtBeat2(30);
        var slow = Enumerable.Range(0, 32).Select(i => new BeatMarker { Time = i * 2000.0, Bpm = 30 }).ToList();
        var issues = new List<ArrangementIssue>();

        new HandShapeGapRule().Validate(ArrangementValidationContext.Build(arr, slow, null), issues);

        Assert.Empty(issues);
    }
}
