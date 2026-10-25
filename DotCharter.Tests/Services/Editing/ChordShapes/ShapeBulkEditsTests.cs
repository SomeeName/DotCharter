using DotCharter.Services.Editing;
using DotCharter.Services.Editing.ChordShapes;
using DotCharter.Tests.Presentation.Dialogs;

namespace DotCharter.Tests.Services.Editing.ChordShapes;

/// <summary>
///     The Shapes panel's bulk verbs over the shared fixture (Am 4 chords + 1 hand shape, its finger-moved copy
///     3 chords + 1 hand shape): Merge into one keeps the most-used shape, Delete moves the uses of used shapes
///     onto another shape with the same frets, and Edit together gives every ticked shape one grip so they
///     become one shape. Each is ONE undo node, and a refusal changes nothing.
/// </summary>
public sealed class ShapeBulkEditsTests
{
    private static readonly Dictionary<int, int> EditedFrets = new() { [1] = 0, [2] = 2, [3] = 2, [4] = 1, [5] = 3 };

    private readonly ChordShapesFixture _f = new();
    private readonly HistoryService _history = new(new FakeAppLogger());
    private readonly IChartEditorSurface _editor = RecordingProxy.Create<IChartEditorSurface>().Proxy;

    private IEnumerable<Chord> AllChords => _f.Level0.Chords.Concat(_f.Level1.Chords);

    private IEnumerable<HandShape> AllHandShapes => _f.Level0.HandShapes.Concat(_f.Level1.HandShapes);

    private ChordTemplate Of(Chord chord) => ChordTemplateResolver.IndexById(_f.Arrangement.ChordTemplates)[chord.TemplateId];

    private ChordTemplate Of(HandShape shape) => ChordTemplateResolver.IndexById(_f.Arrangement.ChordTemplates)[shape.TemplateId];

    /// <summary>A third copy of the Am grip, fingered like <see cref="ChordShapesFixture.Am" />, with one chord on level 1.</summary>
    private ChordTemplate AddThirdCopy()
    {
        var copy = new ChordTemplate
        {
            Id = 6, Name = "Am", Frets = new Dictionary<int, int>(_f.Am.Frets), Fingers = new Dictionary<int, int>(_f.Am.Fingers)
        };
        _f.Arrangement.ChordTemplates.Add(copy);
        _f.Level1.Chords.Add(ChordShapesFixture.ChordOn(copy, 16));
        return copy;
    }

    private ShapeEditInput Together(ChordTemplate primary, params ChordTemplate[] ticked) =>
        new(_f.Arrangement, primary, null, [], [], ticked);

    private static ShapeEditDraft Unedited(ChordTemplate shape) =>
        new(shape.Frets, shape.Fingers, shape.Name, shape.Unnamed);

    private static ShapeEditDraft Edited(ChordTemplate shape) =>
        new(EditedFrets, new Dictionary<int, int>(), shape.Name, false);

    [Fact]
    public void KeeperOf_IsTheShapeWithTheMostUses_TheEarlierOnATie()
    {
        Assert.Same(_f.Am, ShapeBulkEdits.KeeperOf(_f.Arrangement, [_f.AmAlt, _f.Am]));
        Assert.Same(_f.AmAlt, ShapeBulkEdits.KeeperOf(_f.Arrangement, [_f.AmAlt, _f.G]));
        Assert.Null(ShapeBulkEdits.KeeperOf(_f.Arrangement, []));
    }

    [Fact]
    public void MergeMany_MovesEveryUseOntoTheMostUsedShape_RemovesTheCopies_AndIsOneUndoStep()
    {
        var third = AddThirdCopy();
        var plan = ShapeBulkEdits.PlanMergeMany(_f.Arrangement, [third, _f.AmAlt, _f.Am], _editor);

        Assert.False(plan.IsRefused);
        Assert.Equal((4, 1, 2), (plan.ChordsMoved, plan.HandShapesMoved, plan.TemplatesRemoved));
        Assert.True(_history.Do(plan.Action!));

        Assert.Equal(5, _f.Arrangement.ChordTemplates.Count);
        Assert.DoesNotContain(_f.Arrangement.ChordTemplates, t => ReferenceEquals(t, _f.AmAlt) || ReferenceEquals(t, third));
        Assert.All(AllChords.Where(c => Of(c).HasFrets(ChordShapesFixture.AmFrets) && !Of(c).ForceArpeggioInRs), c => Assert.Same(_f.Am, Of(c)));
        Assert.All(AllHandShapes, h => Assert.Same(_f.Am, Of(h)));
        Assert.Equal(_f.Am.Fingers.OrderBy(f => f.Key), Of(_f.Level1.Chords[3]).Fingers.OrderBy(f => f.Key));

        _history.Undo();
        Assert.False(_history.CanUndo);
        Assert.Equal(7, _f.Arrangement.ChordTemplates.Count);
        Assert.Equal(3, AllChords.Count(c => Of(c) == _f.AmAlt));
        Assert.Equal(1, AllChords.Count(c => Of(c) == third));
    }

    [Fact]
    public void MergeMany_RemovesATickedShapeNothingUses()
    {
        var spare = new ChordTemplate { Id = 6, Name = "Spare", Frets = new Dictionary<int, int>(ChordShapesFixture.AmFrets) };
        _f.Arrangement.ChordTemplates.Add(spare);
        var plan = ShapeBulkEdits.PlanMergeMany(_f.Arrangement, [_f.Am, spare], _editor);

        Assert.True(_history.Do(plan.Action!));
        Assert.DoesNotContain(_f.Arrangement.ChordTemplates, t => ReferenceEquals(t, spare));
        Assert.Equal(6, _f.Arrangement.ChordTemplates.Count);
        Assert.Equal(4, AllChords.Count(c => Of(c) == _f.Am));
    }

    [Theory]
    [InlineData(ChordShapeEditRefusal.NeedsTwoShapes, 0)]
    [InlineData(ChordShapeEditRefusal.DifferentFrets, 3)]
    [InlineData(ChordShapeEditRefusal.DifferentArpeggio, 2)]
    public void MergeMany_RefusesWithoutChangingAnything(ChordShapeEditRefusal expected, int otherId)
    {
        var ticked = expected == ChordShapeEditRefusal.NeedsTwoShapes
            ? new[] { _f.Am }
            : [_f.Am, _f.Arrangement.ChordTemplates.Single(t => t.Id == otherId)];
        var plan = ShapeBulkEdits.PlanMergeMany(_f.Arrangement, ticked, _editor);

        Assert.True(plan.IsRefused);
        Assert.Equal(expected, plan.Refusal);
        Assert.False(_history.CanUndo);
    }

    [Fact]
    public void DeleteMany_MovesUsedShapesOntoTheCopyOutsideTheTicks_DeletesTheUnusedOnes_InOneUndoStep()
    {
        var plan = ShapeBulkEdits.PlanDeleteMany(_f.Arrangement, [_f.AmAlt, _f.G], _editor);

        Assert.False(plan.IsRefused);
        Assert.Equal((3, 1, 2), (plan.ChordsMoved, plan.HandShapesMoved, plan.TemplatesRemoved));
        Assert.True(_history.Do(plan.Action!));

        Assert.Equal(4, _f.Arrangement.ChordTemplates.Count);
        Assert.DoesNotContain(_f.Arrangement.ChordTemplates, t => t.Name == "G" || ReferenceEquals(t, _f.AmAlt));
        Assert.Equal(7, AllChords.Count(c => Of(c) == _f.Am));
        Assert.All(AllHandShapes, h => Assert.Same(_f.Am, Of(h)));

        _history.Undo();
        Assert.False(_history.CanUndo);
        Assert.Equal(6, _f.Arrangement.ChordTemplates.Count);
        Assert.Equal(3, AllChords.Count(c => Of(c) == _f.AmAlt));
    }

    [Fact]
    public void DeleteMany_OfUnusedShapesOnly_NeedsNoTarget()
    {
        var spare = new ChordTemplate { Id = 6, Name = "Spare", Frets = new Dictionary<int, int> { [0] = 1 } };
        _f.Arrangement.ChordTemplates.Add(spare);
        var plan = ShapeBulkEdits.PlanDeleteMany(_f.Arrangement, [_f.G, spare], _editor);

        Assert.Equal((0, 0, 2), (plan.ChordsMoved, plan.HandShapesMoved, plan.TemplatesRemoved));
        Assert.True(_history.Do(plan.Action!));
        Assert.Equal(5, _f.Arrangement.ChordTemplates.Count);
    }

    [Fact]
    public void DeleteMany_RefusesWhileAUsedShapeHasNowhereToSendItsUses()
    {
        // Both copies ticked: neither may take the other's uses. C has no copy at all.
        foreach (var ticked in new[] { new[] { _f.Am, _f.AmAlt }, [_f.C, _f.G] })
        {
            var plan = ShapeBulkEdits.PlanDeleteMany(_f.Arrangement, ticked, _editor);
            Assert.Equal(ChordShapeEditRefusal.NoMergeTarget, plan.Refusal);
        }

        Assert.False(_history.CanUndo);
        Assert.Equal(6, _f.Arrangement.ChordTemplates.Count);
    }

    [Fact]
    public void Together_WithTheGripAsItIs_CollapsesCopiesFingeredAlikeIntoTheEditedShape()
    {
        var input = Together(_f.Am, _f.Am, _f.AmAlt);
        var plan = ShapeEditPlanner.Plan(input, ShapeEditScope.Together, Unedited(_f.Am), true, _editor);

        Assert.Equal(ShapeEditOutcome.MergesShapes, plan.Outcome);
        Assert.Equal((7, 2, 1), (plan.Reach.Chords.Count, plan.Reach.HandShapes.Count, plan.ShapesEmptied));
        Assert.Equal(0, plan.ChordsKept);
        Assert.Null(plan.Match);
        Assert.True(_history.Do(plan.Action!));

        Assert.DoesNotContain(_f.Arrangement.ChordTemplates, t => ReferenceEquals(t, _f.AmAlt));
        Assert.Equal(5, _f.Arrangement.ChordTemplates.Count);
        Assert.Equal(7, AllChords.Count(c => Of(c) == _f.Am));
        Assert.All(AllHandShapes, h => Assert.Same(_f.Am, Of(h)));

        _history.Undo();
        Assert.False(_history.CanUndo);
        Assert.Equal(6, _f.Arrangement.ChordTemplates.Count);
        Assert.Equal(3, AllChords.Count(c => Of(c) == _f.AmAlt));
    }

    [Fact]
    public void Together_WithAnotherFingering_MovesEverythingOntoTheTickedShapeThatHasIt()
    {
        var plan = ShapeEditPlanner.Plan(Together(_f.Am, _f.Am, _f.AmAlt), ShapeEditScope.Together, Unedited(_f.AmAlt), true, _editor);

        Assert.Equal(ShapeEditOutcome.MergesShapes, plan.Outcome);
        Assert.Null(plan.Match);
        Assert.True(_history.Do(plan.Action!));

        var left = Assert.Single(_f.Arrangement.ChordTemplates, t => t.HasFrets(ChordShapesFixture.AmFrets) && !t.ForceArpeggioInRs);
        Assert.Same(_f.AmAlt, left);
        Assert.Equal(7, AllChords.Count(c => Of(c) == left));
        Assert.Equal(_f.AmAlt.Fingers.OrderBy(f => f.Key), left.Fingers.OrderBy(f => f.Key));
    }

    [Fact]
    public void Together_WithNewFrets_RewritesTheEditedShapeAndEveryTickedShapeFollowsIt()
    {
        var plan = ShapeEditPlanner.Plan(Together(_f.Am, _f.Am, _f.AmAlt), ShapeEditScope.Together, Edited(_f.Am), true, _editor);

        Assert.Equal(ShapeEditOutcome.MergesShapes, plan.Outcome);
        Assert.True(_history.Do(plan.Action!));

        Assert.Equal(EditedFrets, _f.Am.Frets);
        Assert.Equal(5, _f.Arrangement.ChordTemplates.Count);
        Assert.Equal(7, AllChords.Count(c => Of(c) == _f.Am));
        Assert.All(AllChords.Where(c => Of(c) == _f.Am), c => Assert.Contains(c.ChordNotes, n => n.StringIndex == 5 && n.Fret == 3));
        Assert.All(AllHandShapes, h => Assert.Same(_f.Am, Of(h)));

        _history.Undo();
        Assert.False(_history.CanUndo);
        Assert.Equal(ChordShapesFixture.AmFrets, _f.Am.Frets);
        Assert.Equal(6, _f.Arrangement.ChordTemplates.Count);
        Assert.All(AllChords.Where(c => Of(c) == _f.AmAlt), c => Assert.Contains(c.ChordNotes, n => n.StringIndex == 5 && n.Fret == 0));
    }

    [Fact]
    public void Together_WithHandShapeSyncOff_LeavesTheHandShapesOnTheOldGripAndKeepsTheShapesTheyStillUse()
    {
        var plan = ShapeEditPlanner.Plan(Together(_f.Am, _f.Am, _f.AmAlt), ShapeEditScope.Together, Edited(_f.Am), false, _editor);

        Assert.Equal(0, plan.ShapesEmptied);
        Assert.Equal(2, plan.HandShapesAffected);
        Assert.True(_history.Do(plan.Action!));

        Assert.Equal(7, AllChords.Count(c => Of(c) == _f.Am));
        // The Am hand shape sits on a copy of the old grip; the AmAlt hand shape stays on AmAlt, which is not removed.
        Assert.Equal(7, _f.Arrangement.ChordTemplates.Count);
        Assert.Same(_f.AmAlt, Of(_f.Level1.HandShapes[0]));
        Assert.Equal(ChordShapesFixture.AmFrets, Of(_f.Level0.HandShapes[0]).Frets);
        Assert.NotSame(_f.Am, Of(_f.Level0.HandShapes[0]));
    }

    [Fact]
    public void Together_OntoAnExistingShapeOutsideTheTicks_JoinsIt_AndNamesIt()
    {
        var grip = new ShapeEditDraft(_f.C.Frets, _f.C.Fingers, "x", false);
        var plan = ShapeEditPlanner.Plan(Together(_f.Am, _f.Am, _f.AmAlt), ShapeEditScope.Together, grip, true, _editor);

        Assert.Same(_f.C, plan.Match);
        Assert.Equal(1, plan.MatchUses);
        Assert.Equal(2, plan.ShapesEmptied);
        Assert.True(_history.Do(plan.Action!));

        Assert.Equal(4, _f.Arrangement.ChordTemplates.Count);
        Assert.Equal(8, AllChords.Count(c => Of(c) == _f.C));
        Assert.Equal("C", _f.C.Name);
    }

    [Fact]
    public void Together_WithOneShape_ChangesNothing()
    {
        var plan = ShapeEditPlanner.Plan(Together(_f.Am, _f.Am), ShapeEditScope.Together, Edited(_f.Am), true, _editor);

        Assert.Equal(ShapeEditOutcome.Unchanged, plan.Outcome);
        Assert.Null(plan.Action);
    }

    [Fact]
    public void Together_ReachIsEveryUseOfEveryTickedShape()
    {
        var reach = ShapeEditPlanner.ReachOf(Together(_f.AmAlt, _f.Am, _f.AmAlt), ShapeEditScope.Together, true);

        Assert.Equal((7, 2, 2), (reach.Chords.Count, reach.HandShapes.Count, reach.Levels));
    }
}
