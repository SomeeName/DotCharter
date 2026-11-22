using DotCharter.UI.ViewModels.Panels;

namespace DotCharter.Tests.UI;

/// <summary>
///     The Bends graph draws the held rest of the sustain, as Guitar Pro does. <see cref="BendEditorViewModel.HoldFrom" />
///     is the point the flat hold line starts at (the last point, when it stops before the sustain's end); it is
///     display only, so no point is stored for it.
/// </summary>
public sealed class BendEditorHoldTests
{
    [Fact]
    public void ABendThatPeaksEarly_HoldsFromItsLastPointToTheEnd()
    {
        var writes = new List<IReadOnlyList<BendValue>>();
        var vm = Editor(writes, [new BendValue(new FractionalPosition(0), 0), new BendValue(new FractionalPosition(1), 2)]);

        Assert.Same(vm.Points[^1], vm.HoldFrom);
        Assert.Equal(3, vm.HoldFrom!.TimeSlot);
        Assert.Equal(2, vm.Points.Count);
        Assert.Empty(writes);
    }

    [Fact]
    public void ACurveEndingOnTheSustainEnd_HasNoHold() =>
        Assert.Null(Editor([], [new BendValue(new FractionalPosition(0), 0), new BendValue(new FractionalPosition(4), 2)]).HoldFrom);

    [Fact]
    public void NoBend_HasNoHold() => Assert.Null(Editor([], []).HoldFrom);

    [Fact]
    public void TheHoldFollowsAnEdit()
    {
        var vm = Editor([], [new BendValue(new FractionalPosition(0), 0), new BendValue(new FractionalPosition(1), 2)]);
        var added = vm.AddOrMovePoint(9, 1);

        Assert.Same(added, vm.HoldFrom);
    }

    private static BendEditorViewModel Editor(List<IReadOnlyList<BendValue>> writes, IReadOnlyList<BendValue> values) =>
        new(() => values,
            writes.Add,
            () => new FractionalPosition(0),
            () => new FractionalPosition(4),
            () => { }, FakeLocalization.English());
}
