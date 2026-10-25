using DotCharter.Services.Editing;
using DotCharter.Services.Editing.ChordShapes;
using DotCharter.Tests.Presentation.Dialogs;

namespace DotCharter.Tests.Services.Editing.ChordShapes;

/// <summary>
///     The one issue each shape of the Shapes panel carries, where a copy's uses go, and the two
///     edits behind the panel's name box and its Finger fix.
/// </summary>
public sealed class ChordShapeIssuesTests
{
    private readonly ChordShapesFixture _f = new();
    private readonly HistoryService _history = new(new FakeAppLogger());
    private readonly IChartEditorSurface _editor = RecordingProxy.Create<IChartEditorSurface>().Proxy;

    private ChordShapeIssues Issues() =>
        new(ChordShapeCatalog.Build(_f.Arrangement, _f.Level1));

    [Fact]
    public void TheCopyWithFewerUses_NamesTheKeeper_AndTheKeeperHasNoIssue()
    {
        var issues = Issues();

        var copy = issues.For(_f.AmAlt);
        Assert.Equal(ChordShapeIssueKind.SameFrets, copy.Kind);
        Assert.Same(_f.Am, copy.Keeper);
        Assert.Equal(ChordShapeIssueKind.None, issues.For(_f.Am).Kind);
    }

    [Fact]
    public void OnATieInUses_TheEarlierShapeKeeps()
    {
        foreach (var level in _f.Arrangement.Levels)
        {
            level.Chords.Clear();
            level.HandShapes.Clear();
        }

        _f.Level1.Chords.Add(ChordShapesFixture.ChordOn(_f.Am, 0));
        _f.Level1.Chords.Add(ChordShapesFixture.ChordOn(_f.AmAlt, 4));

        var issues = Issues();

        Assert.Equal(ChordShapeIssueKind.None, issues.For(_f.Am).Kind);
        Assert.Same(_f.Am, issues.For(_f.AmAlt).Keeper);
    }

    [Fact]
    public void AnUnfingeredShape_NamesNoFingering_AndAnUnusedOneNamesNothing()
    {
        var issues = Issues();

        Assert.Equal(ChordShapeIssueKind.NoFingering, issues.For(_f.C).Kind);
        Assert.Equal(ChordShapeIssueKind.None, issues.For(_f.G).Kind);
        Assert.Equal(ChordShapeIssueKind.None, issues.For(new ChordTemplate()).Kind);
    }

    [Fact]
    public void ACopyThatIsAlsoUnfingered_NamesTheCopy()
    {
        _f.AmAlt.Fingers.Clear();

        Assert.Equal(ChordShapeIssueKind.SameFrets, Issues().For(_f.AmAlt).Kind);
    }

    [Fact]
    public void TheMergeTarget_IsTheOtherCopyWithTheMostUses_OrNullWithNoCopy()
    {
        var issues = Issues();

        Assert.Same(_f.Am, issues.MergeTargetFor(_f.AmAlt));
        Assert.Same(_f.AmAlt, issues.MergeTargetFor(_f.Am));
        Assert.Null(issues.MergeTargetFor(_f.C));
    }

    [Fact]
    public void SetName_IsOneUndoStep_AndNoNameMarksTheShapeOnPurpose()
    {
        var plan = ChordShapeEdits.PlanSetName(_f.Arrangement, _f.C, "", unnamed: true, _editor);
        Assert.False(plan.IsRefused);

        Assert.True(_history.Do(plan.Action!));
        Assert.Equal("", _f.C.Name);
        Assert.True(_f.C.Unnamed);
        Assert.Contains(ChordShapeEdits.RenameLabelKey, Assert.Single(_history.GetTree(), n => n.ParentId is not null).Description,
            StringComparison.Ordinal);

        _history.Undo();
        Assert.Equal("C", _f.C.Name);
        Assert.False(_f.C.Unnamed);
    }

    [Fact]
    public void SetName_RefusesAnEditThatChangesNothing_AndAForeignShape()
    {
        Assert.Equal(ChordShapeEditRefusal.SameName,
            ChordShapeEdits.PlanSetName(_f.Arrangement, _f.C, " C ", unnamed: false, _editor).Refusal);
        Assert.Equal(ChordShapeEditRefusal.NotInArrangement,
            ChordShapeEdits.PlanSetName(_f.Arrangement, new ChordTemplate { Id = 99 }, "X", unnamed: false, _editor).Refusal);
    }

    [Fact]
    public void SuggestFingers_FingersAnUnfingeredShape_AsOneUndoStep()
    {
        var plan = ChordShapeEdits.PlanSuggestFingers(_f.Arrangement, _f.C, _editor);
        Assert.False(plan.IsRefused);

        Assert.True(_history.Do(plan.Action!));
        Assert.NotEmpty(_f.C.Fingers);
        Assert.Equal(ChordFingerState.Suggested, ChordShapeCatalog.FingerStateOf(_f.C, 6));

        _history.Undo();
        Assert.Empty(_f.C.Fingers);
    }

    [Fact]
    public void SuggestFingers_KeepsAPinnedShape_AndOffersNothingNew()
    {
        _f.C.Pinned = true;
        Assert.Equal(ChordShapeEditRefusal.Pinned, ChordShapeEdits.PlanSuggestFingers(_f.Arrangement, _f.C, _editor).Refusal);

        // Am already wears exactly the suggestion.
        Assert.Equal(ChordShapeEditRefusal.NoSuggestion, ChordShapeEdits.PlanSuggestFingers(_f.Arrangement, _f.Am, _editor).Refusal);
    }
}
