using DotCharter.Services.Editing.Validation.Rules;
using DotCharter.Tests.Services.Editing.HandShapes;
using DotCharter.UI.ViewModels.Editor.Inspector;

namespace DotCharter.Tests.UI.ViewModels.Editor;

/// <summary>
///     The Issues tab's fix for D17/D18 (#970): a sustain across a phrase start is offered "End it on the
///     boundary", applied as one undo step, and a Link-Next chain gets no fix.
/// </summary>
public sealed class IssueFixBoundaryTrimTests
{
    private static List<BeatMarker> Beats() =>
        [.. Enumerable.Range(0, 16).Select(i => new BeatMarker { Time = i * 500.0, Bpm = 120 })];

    private static (Arrangement Arr, GuitarNote Note) Build(bool linkNext = false)
    {
        var arr = ChordHandShapePlannerTests.Arr();
        arr.EventPoints.Add(new EventPoint { Position = ChordHandShapePlannerTests.Pos(4), Section = "verse" });
        var note = new GuitarNote
        {
            Position = ChordHandShapePlannerTests.Pos(3), StringIndex = 1, Fret = 5,
            Sustain = ChordHandShapePlannerTests.Pos(2), LinkNext = linkNext
        };
        arr.Levels[0].Notes.Add(note);
        return (arr, note);
    }

    private static List<ArrangementIssue> D17s(Arrangement arr)
    {
        var issues = new List<ArrangementIssue>();
        new BoundaryCrossingRule().Validate(ArrangementValidationContext.Build(arr, Beats(), null), issues);
        return issues;
    }

    [Fact]
    public void TheFix_TrimsTheSustain_InOneUndoStep()
    {
        var (arr, note) = Build();
        var history = new HistoryService(new FakeAppLogger());
        var vm = new IssueFixViewModel(history, FakeLocalization.English());

        vm.Offer(Assert.Single(D17s(arr)), arr, arr.Levels[0], Beats());

        Assert.True(vm.HasFix);
        Assert.Equal(FakeLocalization.English()["Inspector_IssueFix_TrimToBoundary"], vm.DefaultLabel);
        vm.ApplyDefaultCommand.Execute(null);

        Assert.Equal(ChordHandShapePlannerTests.Pos(1), note.Sustain);
        Assert.Empty(D17s(arr));

        history.Undo();
        Assert.False(history.CanUndo);
        Assert.Equal(ChordHandShapePlannerTests.Pos(2), note.Sustain);
    }

    [Fact]
    public void FixAll_TrimsToo_ButALinkNextChainHasNoFix()
    {
        var (arr, note) = Build();
        var fix = IssueFixViewModel.DefaultFix(Assert.Single(D17s(arr)), arr, arr.Levels[0], Beats());
        Assert.NotNull(fix);
        fix.Execute();
        Assert.Equal(ChordHandShapePlannerTests.Pos(1), note.Sustain);

        var (linked, _) = Build(linkNext: true);
        Assert.False(IssueFixViewModel.CanOffer(Assert.Single(D17s(linked)), linked, linked.Levels[0], Beats()));
    }
}
