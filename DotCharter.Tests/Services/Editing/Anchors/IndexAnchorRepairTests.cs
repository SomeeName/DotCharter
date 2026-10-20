using DotCharter.Services.Editing.Anchors;

namespace DotCharter.Tests.Services.Editing.Anchors;

/// <summary>
///     #618: the fix for an E04 / I46 disagreement. On the verified corpus most findings whose grip
///     the arrangement also plays the other way were the issue's case — a double stop fingered 2-3
///     under FHP 2 while the passage plays the same double stop 1-3 under that very anchor. The
///     ANCHOR is right there, so re-fingering is the default; with no such evidence moving the anchor
///     is. Either fix is one undo step, and re-fingering never edits a template.
/// </summary>
public sealed class IndexAnchorRepairTests
{
    private const int Wrong = 0; // 2-3: puts the index on fret 1
    private const int Agreeing = 1; // 1-3: index on fret 2

    private static ChordTemplate Grip(int id, int indexFinger) => new()
    {
        Id = id,
        Frets = new Dictionary<int, int> { [3] = 2, [4] = 3 },
        Fingers = new Dictionary<int, int> { [3] = indexFinger, [4] = 3 }
    };

    private static Chord Strum(ChartLevel level, int templateId, int position)
    {
        var chord = new Chord { Position = new FractionalPosition(position), TemplateId = templateId };
        chord.ChordNotes.Add(new ChordNote { StringIndex = 3, Fret = 2 });
        chord.ChordNotes.Add(new ChordNote { StringIndex = 4, Fret = 3 });
        level.Chords.Add(chord);
        return chord;
    }

    /// <summary>
    ///     FHP 2 from the start; the wrong grip strummed twice under a hand shape at beats 2 and 3, and
    ///     (with <paramref name="evidence" />) the agreeing grip at beat 6 under the same anchor.
    /// </summary>
    private static (Arrangement Arr, ChartLevel Level, Chord Flagged, Chord Sibling, HandShape Shape, FretAnchor Anchor)
        Build(bool evidence = true)
    {
        var arr = new Arrangement();
        var level = new ChartLevel();
        arr.Levels.Add(level);
        arr.ChordTemplates.Add(Grip(Wrong, 2));
        arr.ChordTemplates.Add(Grip(Agreeing, 1));

        var anchor = new FretAnchor(new FractionalPosition(0), 2, 4);
        level.Anchors.Add(anchor);
        var flagged = Strum(level, Wrong, 2);
        var sibling = Strum(level, Wrong, 3);
        var shape = new HandShape
        {
            TemplateId = Wrong, Position = new FractionalPosition(2), EndPosition = new FractionalPosition(4)
        };
        level.HandShapes.Add(shape);
        if (evidence) Strum(level, Agreeing, 6);
        return (arr, level, flagged, sibling, shape, anchor);
    }

    [Fact]
    public void TheSameGripFingeredToAgreeUnderThisAnchor_IsTheDefault()
    {
        var (arr, level, flagged, sibling, shape, anchor) = Build();

        var repair = IndexAnchorRepair.For(arr, level, flagged, null, anchor);

        Assert.NotNull(repair);
        Assert.True(repair!.PrefersRefinger);
        Assert.Same(arr.ChordTemplates[Agreeing], repair.Refinger);
        Assert.Equal(1, repair.Evidence);
        // The anchor move stays on offer as the alternative.
        Assert.Equal(1, repair.MoveAnchorTo);
        // A chord and the hand shape over it are one decision: the shape and every strum
        // under it on the same grip are rebound together.
        Assert.Equal([flagged, sibling], repair.Chords);
        Assert.Equal([shape], repair.HandShapes);
    }

    [Fact]
    public void NoAgreeingGripElsewhere_MovingTheAnchorIsTheDefault()
    {
        var (arr, level, flagged, _, _, anchor) = Build(evidence: false);

        var repair = IndexAnchorRepair.For(arr, level, flagged, null, anchor);

        Assert.NotNull(repair);
        Assert.False(repair!.PrefersRefinger);
        Assert.Null(repair.Refinger);
        Assert.Equal(1, repair.MoveAnchorTo);
        Assert.Null(repair.Alternative(level));
    }

    /// <summary>The agreeing grip is attested only by a chord played under an anchor on the SAME
    ///     fret; one played under another hand position says nothing about this one.</summary>
    [Fact]
    public void AGripPlayedOnlyUnderAnotherHandPosition_IsNoEvidence()
    {
        var (arr, level, flagged, _, _, anchor) = Build(evidence: false);
        level.Anchors.Add(new FretAnchor(new FractionalPosition(5), 7, 4));
        Strum(level, Agreeing, 6);

        var repair = IndexAnchorRepair.For(arr, level, flagged, null, anchor);

        Assert.False(repair!.PrefersRefinger);
    }

    [Fact]
    public void TheMostPlayedAgreeingGripWins()
    {
        var (arr, level, flagged, _, _, anchor) = Build();
        var barre = Grip(7, 1);
        barre.Fingers[4] = 4;
        arr.ChordTemplates.Add(barre);
        Strum(level, 7, 7);
        Strum(level, 7, 8);

        var repair = IndexAnchorRepair.For(arr, level, flagged, null, anchor);

        Assert.Same(barre, repair!.Refinger);
        Assert.Equal(2, repair.Evidence);
    }

    [Fact]
    public void AGripThatAgrees_NeedsNoRepair()
    {
        var (arr, level, _, _, _, anchor) = Build();

        Assert.Null(IndexAnchorRepair.For(arr, level, level.Chords[^1], null, anchor));
    }

    /// <summary>An I46 finding starts from the hand shape: it and the strums under it move together.</summary>
    [Fact]
    public void FromAHandShape_TheShapeAndItsStrumsAreRebound()
    {
        var (arr, level, flagged, sibling, shape, anchor) = Build();

        var repair = IndexAnchorRepair.For(arr, level, null, shape, anchor);

        Assert.Equal([flagged, sibling], repair!.Chords);
        Assert.Equal([shape], repair.HandShapes);
    }

    [Fact]
    public void TheRefingeringIsOneUndoStep_AndEditsNoTemplate()
    {
        var (arr, level, flagged, sibling, shape, anchor) = Build();
        var history = new HistoryService(new FakeAppLogger());
        var wrongFingers = new Dictionary<int, int>(arr.ChordTemplates[Wrong].Fingers);

        var repair = IndexAnchorRepair.For(arr, level, flagged, null, anchor)!;
        Assert.True(history.Do(repair.Default(level)!));

        Assert.All([flagged, sibling], c => Assert.Equal(Agreeing, c.TemplateId));
        Assert.Equal(Agreeing, shape.TemplateId);
        Assert.Equal(2, anchor.Fret);
        Assert.Equal(2, arr.ChordTemplates.Count);
        Assert.Equal(wrongFingers, arr.ChordTemplates[Wrong].Fingers);
        Assert.Equal([2, 3], flagged.ChordNotes.Select(n => n.Fret));

        history.Undo();

        Assert.False(history.CanUndo);
        Assert.All([flagged, sibling], c => Assert.Equal(Wrong, c.TemplateId));
        Assert.Equal(Wrong, shape.TemplateId);
    }

    [Fact]
    public void TheAnchorMoveIsOneUndoStep()
    {
        var (arr, level, flagged, _, _, anchor) = Build(evidence: false);
        var history = new HistoryService(new FakeAppLogger());

        var repair = IndexAnchorRepair.For(arr, level, flagged, null, anchor)!;
        Assert.True(history.Do(repair.Default(level)!));

        Assert.Equal(1, anchor.Fret);
        Assert.Equal(Wrong, flagged.TemplateId);

        history.Undo();

        Assert.False(history.CanUndo);
        Assert.Equal(2, anchor.Fret);
    }

    [Fact]
    public void WithEvidence_TheAlternativeMovesTheAnchor()
    {
        var (arr, level, flagged, _, _, anchor) = Build();
        var history = new HistoryService(new FakeAppLogger());

        var repair = IndexAnchorRepair.For(arr, level, flagged, null, anchor)!;
        Assert.True(history.Do(repair.Alternative(level)!));

        Assert.Equal(1, anchor.Fret);
        Assert.Equal(Wrong, flagged.TemplateId);
    }
}
