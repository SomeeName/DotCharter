using DotCharter.Services.Editing.Validation.Rules;
using DotCharter.Tests.Services.Editing.HandShapes;
using DotCharter.UI.ViewModels.Editor.Inspector;

namespace DotCharter.Tests.UI.ViewModels.Editor;

/// <summary>
///     The Issues tab's fix for D40: a note 2 ms off the grid is offered "Snap onto the grid", applied as one
///     undo step, and Fix all snaps it too; a D41 (more than 3 ms off) gets no fix.
/// </summary>
public sealed class IssueFixGridSnapTests
{
    private static List<BeatMarker> Beats() =>
        [.. Enumerable.Range(0, 16).Select(i => new BeatMarker { Time = i * 500.0, Bpm = 120 })];

    private static (Arrangement Arr, GuitarNote Note) Build(long num, long den)
    {
        var arr = ChordHandShapePlannerTests.Arr();
        var note = new GuitarNote { Position = new FractionalPosition(4, new Fraction(num, den)), StringIndex = 1, Fret = 5 };
        arr.Levels[0].Notes.Add(note);
        return (arr, note);
    }

    private static ArrangementIssue Finding(Arrangement arr)
    {
        var issues = new List<ArrangementIssue>();
        new OffGridRule().Validate(ArrangementValidationContext.Build(arr, Beats(), null), issues);
        return Assert.Single(issues);
    }

    [Fact]
    public void TheFix_SnapsTheNote_InOneUndoStep()
    {
        var (arr, note) = Build(2, 500);
        var history = new HistoryService(new FakeAppLogger());
        var vm = new IssueFixViewModel(history, FakeLocalization.English());

        vm.Offer(Finding(arr), arr, arr.Levels[0], Beats());

        Assert.True(vm.HasFix);
        Assert.Equal(FakeLocalization.English()["Inspector_IssueFix_SnapToGrid"], vm.DefaultLabel);
        vm.ApplyDefaultCommand.Execute(null);
        Assert.Equal(new FractionalPosition(4), note.Position);

        history.Undo();
        Assert.False(history.CanUndo);
        Assert.Equal(new FractionalPosition(4, new Fraction(2, 500)), note.Position);
    }

    [Fact]
    public void FixAll_SnapsToo_ButAD41HasNoFix()
    {
        var (arr, note) = Build(1, 500);
        IssueFixViewModel.DefaultFix(Finding(arr), arr, arr.Levels[0], Beats())!.Execute();
        Assert.Equal(new FractionalPosition(4), note.Position);

        var (far, _) = Build(7, 1000);
        Assert.False(IssueFixViewModel.CanOffer(Finding(far), far, far.Levels[0], Beats()));
    }
}
