using DotCharter.Services.Editing;
using DotCharter.Services.Editing.ChordShapes;
using DotCharter.Tests.Presentation.Dialogs;

namespace DotCharter.Tests.Services.Editing.ChordShapes;

/// <summary>
///     Clean up shapes over a chart shaped like an old import: it finds the identical copies (merged into the
///     shape that keeps the name), the shapes nothing uses and the pairs that differ by one finger, and applies
///     the ticked proposals as ONE undo step with every chord, hand shape and notation-voice chord still on a
///     shape that exists.
/// </summary>
public sealed class ShapeCleanupTests
{
    private readonly MessyShapesFixture _f = new();
    private readonly HistoryService _history = new(new FakeAppLogger());
    private readonly IChartEditorSurface _editor = RecordingProxy.Create<IChartEditorSurface>().Proxy;

    /// <summary>By instance: two shapes with the same grip and name are equal by value.</summary>
    private bool Has(ChordTemplate template) => _f.Arrangement.ChordTemplates.Any(t => ReferenceEquals(t, template));

    private ShapeCleanupFindings Find() => ShapeCleanupFinder.Find(_f.Arrangement);

    private ShapeCleanupPlan PlanAll(ShapeCleanupFindings findings, ShapeCleanupChoice near = ShapeCleanupChoice.KeepBoth) =>
        ShapeCleanupPlanner.Plan(_f.Arrangement, findings.Merges, findings.Unused,
            findings.NearDuplicates.Select(p => (p, near)), _editor);

    [Fact]
    public void Finds_TheIdenticalCopies_KeepingTheNamedOrBusiestShape()
    {
        var found = Find();

        Assert.Equal(2, found.Merges.Count);
        var grip = found.Merges[0];
        Assert.Same(_f.Grip0, grip.Keeper);
        Assert.Equal([_f.Grip1, _f.Grip2], grip.Copies);
        Assert.Equal((2, 2), (grip.Chords, grip.HandShapes));
        var a5 = found.Merges[1];
        Assert.Same(_f.A5, a5.Keeper);
        Assert.Equal([_f.A5Copy], a5.Copies);
        Assert.Equal((1, 1), (a5.Chords, a5.HandShapes));
    }

    [Fact]
    public void Finds_TheShapesNothingUses() => Assert.Equal([_f.Orphan0, _f.Orphan1, _f.Orphan2], Find().Unused);

    [Fact]
    public void Finds_TheSameFretsWithAnotherFinger_AsAReviewPairNotAMerge()
    {
        var pair = Assert.Single(Find().NearDuplicates);

        Assert.Same(_f.Grip0, pair.First);
        Assert.Same(_f.GripAlt, pair.Second);
        Assert.Equal(ShapeCleanupDifference.Fingers, pair.Difference);
        Assert.Equal([4], pair.Strings);
        Assert.True(pair.FirstUses > pair.SecondUses);
    }

    [Fact]
    public void TwoNamesOnOneGrip_AreNeverMerged_OnlyReviewed()
    {
        _f.A5Copy.Name = "A5 power";

        var found = Find();

        Assert.DoesNotContain(found.Merges, m => ReferenceEquals(m.Keeper, _f.A5));
        var pair = Assert.Single(found.NearDuplicates, p => p.Difference == ShapeCleanupDifference.Name);
        Assert.Equal(["A5", "A5 power"], new[] { pair.First.Name, pair.Second.Name }.Order());
    }

    [Fact]
    public void AShapeWithGhostGems_TakesNoPart()
    {
        _f.Grip1.GhostMask = 1 << 3;

        var found = Find();

        Assert.DoesNotContain(found.Merges.SelectMany(m => m.Copies), t => ReferenceEquals(t, _f.Grip1));
    }

    [Fact]
    public void ATidyChart_FindsNothing_AndPlansNothing()
    {
        var tidy = new ChordShapesFixture();
        tidy.Arrangement.ChordTemplates.Remove(tidy.G);
        tidy.Arrangement.ChordTemplates.Remove(tidy.AmAlt);
        foreach (var level in tidy.Arrangement.Levels)
        {
            foreach (var chord in level.Chords.Where(c => c.TemplateId == tidy.AmAlt.Id).ToList()) level.Chords.Remove(chord);
            foreach (var shape in level.HandShapes.Where(h => h.TemplateId == tidy.AmAlt.Id).ToList()) level.HandShapes.Remove(shape);
        }

        var found = ShapeCleanupFinder.Find(tidy.Arrangement);

        Assert.True(found.IsTidy);
        Assert.Null(ShapeCleanupPlanner.Plan(tidy.Arrangement, found.Merges, found.Unused, [], _editor).Action);
    }

    [Fact]
    public void ApplyingEverything_IsOneUndoStep_AndLeavesEveryUseOnAShapeThatExists()
    {
        var found = Find();
        var before = _f.UseIds();
        var chordCount = _f.AllChords.Count();
        var shapeCount = _f.AllHandShapes.Count();

        var plan = PlanAll(found);
        Assert.True(_history.Do(plan.Action!));

        Assert.Equal(4, _f.Arrangement.ChordTemplates.Count);
        Assert.All(new[] { _f.Grip0, _f.E5, _f.A5, _f.GripAlt }, t => Assert.True(Has(t)));
        Assert.Equal([0, 1, 2, 3], _f.Arrangement.ChordTemplates.Select(t => t.Id));
        Assert.Equal(chordCount, _f.AllChords.Count());
        Assert.Equal(shapeCount, _f.AllHandShapes.Count());
        Assert.All(_f.AllChords, c => Assert.InRange(c.TemplateId, 0, 3));
        Assert.All(_f.AllHandShapes, h => Assert.InRange(h.TemplateId, 0, 3));
        Assert.Equal([5, 1, 3, 2], _f.Arrangement.ChordTemplates.Select(t => _f.AllChords.Count(c => c.TemplateId == t.Id)));
        Assert.Equal(6, plan.ShapesRemoved);
        Assert.Equal((3, 3), (plan.ChordsMoved, plan.HandShapesMoved));
        Assert.Equal(1, _history.GetTree().Count(n => n.ParentId is not null));

        _history.Undo();

        Assert.Equal(10, _f.Arrangement.ChordTemplates.Count);
        Assert.Equal(Enumerable.Range(0, 10), _f.Arrangement.ChordTemplates.Select(t => t.Id));
        Assert.Equal(before, _f.UseIds());
        Assert.False(_history.CanUndo);
    }

    [Fact]
    public void AChordAndTheHandShapeOverIt_StayOnOneShape_AndTheVoiceChordFollows()
    {
        var plan = PlanAll(Find());
        _history.Do(plan.Action!);

        foreach (var level in _f.Arrangement.Levels)
        {
            foreach (var shape in level.HandShapes)
            {
                var chord = level.Chords.Single(c => c.Position >= shape.Position && c.Position < shape.EndPosition);
                Assert.Equal(chord.TemplateId, shape.TemplateId);
            }
        }

        var voiceChord = Assert.Single(_f.Arrangement.NotationVoices[0].Chords);
        Assert.Same(_f.Grip0, _f.Arrangement.ChordTemplates[voiceChord.TemplateId]);
    }

    [Fact]
    public void ADuplicateGroupLeftUnticked_StaysAsItWas()
    {
        var found = Find();
        var plan = ShapeCleanupPlanner.Plan(_f.Arrangement, found.Merges.Skip(1), [], [], _editor);

        _history.Do(plan.Action!);

        Assert.True(Has(_f.Grip1));
        Assert.True(Has(_f.Orphan0));
        Assert.False(Has(_f.A5Copy));
        Assert.Equal(1, plan.ShapesRemoved);
    }

    [Fact]
    public void AnUnusedShapeLeftUnticked_Stays()
    {
        var found = Find();
        var plan = ShapeCleanupPlanner.Plan(_f.Arrangement, [], found.Unused.Skip(1), [], _editor);

        _history.Do(plan.Action!);

        Assert.True(Has(_f.Orphan0));
        Assert.False(Has(_f.Orphan1));
        Assert.False(Has(_f.Orphan2));
        Assert.Equal(8, _f.Arrangement.ChordTemplates.Count);
    }

    [Fact]
    public void ReviewAnswers_AreDoNothingUntilChosen_AndEachChoiceKeepsTheShapeItNames()
    {
        var found = Find();
        var pair = found.NearDuplicates[0];

        var keepBoth = ShapeCleanupPlanner.Plan(_f.Arrangement, [], [], [(pair, ShapeCleanupChoice.KeepBoth)], _editor);
        Assert.Null(keepBoth.Action);

        var left = ShapeCleanupPlanner.Plan(_f.Arrangement, [], [], [(pair, ShapeCleanupChoice.UseFirst)], _editor);
        _history.Do(left.Action!);
        Assert.True(Has(_f.Grip0));
        Assert.False(Has(_f.GripAlt));
        _history.Undo();

        var right = ShapeCleanupPlanner.Plan(_f.Arrangement, [], [], [(pair, ShapeCleanupChoice.UseSecond)], _editor);
        _history.Do(right.Action!);
        Assert.True(Has(_f.GripAlt));
        Assert.False(Has(_f.Grip0));
    }

    [Fact]
    public void KeepingTheOtherFingering_WhileTheCopiesMerge_SendsTheWholeGroupThere()
    {
        var found = Find();
        var plan = ShapeCleanupPlanner.Plan(_f.Arrangement, found.Merges, [],
            [(found.NearDuplicates[0], ShapeCleanupChoice.UseSecond)], _editor);

        _history.Do(plan.Action!);

        // The group's keeper is merged away itself, so its copies follow it: all of their uses land on GripAlt.
        Assert.False(Has(_f.Grip0));
        Assert.False(Has(_f.Grip1));
        Assert.False(Has(_f.Grip2));
        var kept = _f.Arrangement.ChordTemplates.Single(t => ReferenceEquals(t, _f.GripAlt));
        Assert.Equal(7, _f.AllChords.Count(c => c.TemplateId == kept.Id));
        Assert.All(_f.AllChords, c => Assert.Contains(_f.Arrangement.ChordTemplates, t => t.Id == c.TemplateId));
    }
}
