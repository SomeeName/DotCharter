using DotCharter.Services.Editing;
using DotCharter.Services.Editing.ChordShapes;
using DotCharter.Tests.Presentation.Dialogs;

namespace DotCharter.Tests.Services.Editing.ChordShapes;

/// <summary>
///     The scope-aware shape edit: This chord, Selection and Every use each state their outcome before
///     Apply (the shape changes, a new shape is made, or the uses join an existing one), and Apply is
///     ONE undo node.
/// </summary>
public sealed class ShapeEditPlannerTests
{
    private static readonly Dictionary<int, int> EditedFrets = new() { [1] = 0, [2] = 2, [3] = 2, [4] = 1, [5] = 3 };

    private readonly ChordShapesFixture _f = new();
    private readonly HistoryService _history = new(new FakeAppLogger());
    private readonly IChartEditorSurface _editor = RecordingProxy.Create<IChartEditorSurface>().Proxy;

    private IEnumerable<Chord> AmChords => _f.Level0.Chords.Concat(_f.Level1.Chords).Where(c => c.TemplateId == _f.Am.Id);

    private ShapeEditInput Input(ChordTemplate shape, Chord? origin = null, IReadOnlyList<Chord>? selected = null) =>
        new(_f.Arrangement, shape, origin, selected ?? [], []);

    private static ShapeEditDraft Edited(ChordTemplate shape, string? name = null) =>
        new(EditedFrets, new Dictionary<int, int>(), name ?? shape.Name, false);

    private static ShapeEditDraft Unedited(ChordTemplate shape) =>
        new(shape.Frets, shape.Fingers, shape.Name, shape.Unnamed);

    private ChordTemplate Of(Chord chord) => ChordTemplateResolver.IndexById(_f.Arrangement.ChordTemplates)[chord.TemplateId];

    [Fact]
    public void ThisChord_MakesANewShapeForThatChordOnly_AndTheOthersKeepTheOldGrip()
    {
        var origin = _f.Level1.Chords[0];
        var plan = ShapeEditPlanner.Plan(Input(_f.Am, origin), ShapeEditScope.ThisChord, Edited(_f.Am), true, _editor);

        Assert.Equal(ShapeEditOutcome.MakesNewShape, plan.Outcome);
        Assert.Single(plan.Reach.Chords);
        Assert.Equal(3, plan.ChordsKept);
        Assert.True(_history.Do(plan.Action!));

        Assert.Equal(7, _f.Arrangement.ChordTemplates.Count);
        Assert.Equal(EditedFrets, Of(origin).Frets);
        Assert.Equal(3, AmChords.Count(c => !ReferenceEquals(c, origin)));
        Assert.Equal(_f.Am.Frets, ChordShapesFixture.AmFrets);
        Assert.Contains(origin.ChordNotes, n => n.StringIndex == 5 && n.Fret == 3);
    }

    [Fact]
    public void Selection_ThatLeavesUsesBehind_MakesANewShapeForTheSelectedOnes()
    {
        var picked = new[] { _f.Level1.Chords[0], _f.Level1.Chords[1] };
        var plan = ShapeEditPlanner.Plan(Input(_f.Am, null, picked), ShapeEditScope.Selection, Edited(_f.Am), true, _editor);

        Assert.Equal(ShapeEditOutcome.MakesNewShape, plan.Outcome);
        Assert.Equal((2, 2), (plan.Reach.Chords.Count, plan.ChordsKept));
        _history.Do(plan.Action!);

        Assert.Equal(EditedFrets, Of(picked[0]).Frets);
        Assert.Same(Of(picked[0]), Of(picked[1]));
        Assert.Equal(2, AmChords.Count());
        Assert.Equal(ChordShapesFixture.AmFrets, _f.Am.Frets);
    }

    [Fact]
    public void Selection_ThatCoversEveryUse_ChangesTheShapeItself_AndStatesIt()
    {
        var all = AmChords.ToList();
        var plan = ShapeEditPlanner.Plan(Input(_f.Am, null, all), ShapeEditScope.Selection, Edited(_f.Am), true, _editor);

        Assert.Equal(ShapeEditOutcome.ChangesShape, plan.Outcome);
        Assert.Equal(0, plan.ChordsKept);
        _history.Do(plan.Action!);

        Assert.Equal(6, _f.Arrangement.ChordTemplates.Count);
        Assert.Equal(EditedFrets, _f.Am.Frets);
        Assert.All(all, c => Assert.Contains(c.ChordNotes, n => n.StringIndex == 5 && n.Fret == 3));
    }

    [Fact]
    public void EveryUse_ChangesTheShapeItself_OnEveryLevel_AndTheHandShapesFollow()
    {
        var plan = ShapeEditPlanner.Plan(Input(_f.Am), ShapeEditScope.EveryUse, Edited(_f.Am), true, _editor);

        Assert.Equal(ShapeEditOutcome.ChangesShape, plan.Outcome);
        Assert.Equal((4, 1), (plan.Reach.Chords.Count, plan.Reach.HandShapes.Count));
        Assert.Equal(2, plan.Reach.Levels);
        _history.Do(plan.Action!);

        Assert.Equal(EditedFrets, _f.Am.Frets);
        Assert.Equal(_f.Am.Id, _f.Level0.HandShapes[0].TemplateId);
        Assert.True(_f.Am.Pinned);
    }

    [Fact]
    public void EveryUse_WithTheSyncOff_KeepsTheOldGripOnTheHandShapes()
    {
        var plan = ShapeEditPlanner.Plan(Input(_f.Am), ShapeEditScope.EveryUse, Edited(_f.Am), false, _editor);
        _history.Do(plan.Action!);

        var kept = ChordTemplateResolver.IndexById(_f.Arrangement.ChordTemplates)[_f.Level0.HandShapes[0].TemplateId];
        Assert.NotSame(_f.Am, kept);
        Assert.Equal(ChordShapesFixture.AmFrets, kept.Frets);
        Assert.Equal(EditedFrets, _f.Am.Frets);
    }

    [Fact]
    public void ThisChord_CarriesTheHandShapeOverIt_WhileTheSyncIsOn_AndLeavesItWhenOff()
    {
        var origin = _f.Level0.Chords[0];
        var on = ShapeEditPlanner.Plan(Input(_f.Am, origin), ShapeEditScope.ThisChord, Edited(_f.Am), true, _editor);
        Assert.Equal((1, 1), (on.Reach.Chords.Count, on.Reach.HandShapes.Count));
        Assert.Equal(1, on.HandShapesAffected);

        var off = ShapeEditPlanner.Plan(Input(_f.Am, origin), ShapeEditScope.ThisChord, Edited(_f.Am), false, _editor);
        Assert.Equal((1, 0), (off.Reach.Chords.Count, off.Reach.HandShapes.Count));
        Assert.Equal(1, off.HandShapesAffected);
        _history.Do(on.Action!);
        Assert.NotEqual(_f.Am.Id, _f.Level0.HandShapes[0].TemplateId);
    }

    [Fact]
    public void AnEditThatMakesTheShapeEqualAnother_EveryUse_JoinsIt_AndRemovesTheEmptiedShape()
    {
        // AmAlt wears the Am frets with one finger moved: typing Am's fingers makes it equal to Am.
        var plan = ShapeEditPlanner.Plan(Input(_f.AmAlt), ShapeEditScope.EveryUse,
            new ShapeEditDraft(_f.AmAlt.Frets, _f.Am.Fingers, "Am7", false), true, _editor);

        Assert.Equal(ShapeEditOutcome.JoinsExisting, plan.Outcome);
        Assert.Same(_f.Am, plan.Match);
        Assert.Equal(5, plan.MatchUses);
        _history.Do(plan.Action!);

        Assert.DoesNotContain(_f.AmAlt, _f.Arrangement.ChordTemplates);
        Assert.Equal("Am", _f.Am.Name);
        Assert.Equal(7, AmChords.Count());
        Assert.Equal(2, _f.Level0.HandShapes.Concat(_f.Level1.HandShapes).Count(h => h.TemplateId == _f.Am.Id));
        var node = Assert.Single(_history.GetTree(), n => n.ParentId is not null);
        Assert.Contains(ShapeEditPlanner.EditLabelKey, node.Description, StringComparison.Ordinal);
        _history.Undo();
        Assert.Contains(_f.AmAlt, _f.Arrangement.ChordTemplates);
        Assert.Equal(4, AmChords.Count());
    }

    [Fact]
    public void AnEditThatMakesTheGripEqualAnother_ThisChord_JoinsItAndKeepsTheShapeTheOthersUse()
    {
        var origin = _f.Level1.Chords[3];
        var plan = ShapeEditPlanner.Plan(Input(_f.AmAlt, origin), ShapeEditScope.ThisChord,
            new ShapeEditDraft(_f.AmAlt.Frets, _f.Am.Fingers, "Am", false), true, _editor);

        Assert.Equal(ShapeEditOutcome.JoinsExisting, plan.Outcome);
        _history.Do(plan.Action!);

        Assert.Equal(_f.Am.Id, origin.TemplateId);
        Assert.Contains(_f.AmAlt, _f.Arrangement.ChordTemplates);
        Assert.Equal(2, _f.Level0.Chords.Concat(_f.Level1.Chords).Count(c => c.TemplateId == _f.AmAlt.Id));
    }

    [Fact]
    public void ANameOnlyEdit_OfOneChord_KeepsTheTypedNameOnTheNewShape()
    {
        var origin = _f.Level1.Chords[0];
        var plan = ShapeEditPlanner.Plan(Input(_f.Am, origin), ShapeEditScope.ThisChord,
            new ShapeEditDraft(_f.Am.Frets, _f.Am.Fingers, "Dmin", false), true, _editor);

        Assert.Equal(ShapeEditOutcome.MakesNewShape, plan.Outcome);
        _history.Do(plan.Action!);
        Assert.Equal("Dmin", Of(origin).Name);
    }

    [Fact]
    public void NothingTyped_PlansNothing()
    {
        var plan = ShapeEditPlanner.Plan(Input(_f.Am), ShapeEditScope.EveryUse, Unedited(_f.Am), true, _editor);

        Assert.Equal(ShapeEditOutcome.Unchanged, plan.Outcome);
        Assert.Null(plan.Action);
    }

    [Fact]
    public void AScopeThatReachesNothing_PlansNothing()
    {
        var plan = ShapeEditPlanner.Plan(Input(_f.Am), ShapeEditScope.Selection, Edited(_f.Am), true, _editor);

        Assert.Equal(ShapeEditOutcome.Unchanged, plan.Outcome);
        Assert.Null(plan.Action);
    }

    [Fact]
    public void EveryScope_IsOneUndoNode()
    {
        var origin = _f.Level1.Chords[0];
        _history.Do(ShapeEditPlanner.Plan(Input(_f.Am, origin), ShapeEditScope.ThisChord, Edited(_f.Am), true, _editor).Action!);

        Assert.Single(_history.GetTree(), n => n.ParentId is not null);
        _history.Undo();
        Assert.Equal(6, _f.Arrangement.ChordTemplates.Count);
        Assert.Equal(_f.Am.Id, origin.TemplateId);
        Assert.False(_history.CanUndo);
    }
}
