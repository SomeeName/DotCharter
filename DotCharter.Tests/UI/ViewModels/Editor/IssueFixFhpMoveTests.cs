using DotCharter.Services.Editing.Validation.Rules;
using DotCharter.Tests.Services.Editing.HandShapes;
using DotCharter.UI.ViewModels.Editor.Inspector;

namespace DotCharter.Tests.UI.ViewModels.Editor;

/// <summary>
///     The Issues tab's fix for D50-D52: an FHP between notes is offered "Move it onto the next note",
///     applied as one undo step (and by Fix all); D54 has no fix.
/// </summary>
public sealed class IssueFixFhpMoveTests
{
    private static List<BeatMarker> Beats() =>
        [.. Enumerable.Range(0, 16).Select(i => new BeatMarker { Time = i * 500.0, Bpm = 120 })];

    private static (Arrangement Arr, FretAnchor Between) Build()
    {
        var arr = ChordHandShapePlannerTests.Arr();
        var level = arr.Levels[0];
        level.Notes.Add(new GuitarNote { Position = ChordHandShapePlannerTests.Pos(2), StringIndex = 1, Fret = 5 });
        level.Notes.Add(new GuitarNote { Position = ChordHandShapePlannerTests.Pos(4), StringIndex = 1, Fret = 7 });
        level.Anchors.Add(new FretAnchor(ChordHandShapePlannerTests.Pos(2), 5, 4));
        var between = new FretAnchor(ChordHandShapePlannerTests.Pos(3), 22, 3);
        level.Anchors.Add(between);
        return (arr, between);
    }

    private static List<ArrangementIssue> Findings(Arrangement arr)
    {
        var issues = new List<ArrangementIssue>();
        new FretHandPositionRule().Validate(ArrangementValidationContext.Build(arr, Beats(), null), issues);
        return issues;
    }

    [Fact]
    public void TheFix_MovesTheAnchorOntoTheNextNote_InOneUndoStep()
    {
        var (arr, between) = Build();
        var history = new HistoryService(new FakeAppLogger());
        var vm = new IssueFixViewModel(history, FakeLocalization.English());

        vm.Offer(Assert.Single(Findings(arr), i => i.Type == ArrangementIssueType.AnchorBetweenNotes), arr, arr.Levels[0], Beats());

        Assert.True(vm.HasFix);
        Assert.Equal(FakeLocalization.English()["Inspector_IssueFix_MoveAnchorToNextNote"], vm.DefaultLabel);
        Assert.Equal(FakeLocalization.English()["Inspector_IssueFix_MoveAnchorToNextNoteTip"], vm.DefaultTip);
        vm.ApplyDefaultCommand.Execute(null);

        Assert.Equal(ChordHandShapePlannerTests.Pos(4), between.Position);
        Assert.DoesNotContain(Findings(arr), i => i.Type == ArrangementIssueType.AnchorBetweenNotes);

        history.Undo();
        Assert.False(history.CanUndo);
        Assert.Equal(ChordHandShapePlannerTests.Pos(3), between.Position);
    }

    [Fact]
    public void FixAll_MovesToo_ButAHighAnchorHasNoFix()
    {
        var (arr, between) = Build();
        var findings = Findings(arr);
        var fix = IssueFixViewModel.DefaultFix(Assert.Single(findings, i => i.Type == ArrangementIssueType.AnchorBetweenNotes),
            arr, arr.Levels[0], Beats());
        Assert.NotNull(fix);

        Assert.False(IssueFixViewModel.CanOffer(Assert.Single(findings, i => i.Type == ArrangementIssueType.AnchorFretAbove21),
            arr, arr.Levels[0], Beats()));

        fix.Execute();
        Assert.Equal(ChordHandShapePlannerTests.Pos(4), between.Position);
    }
}
