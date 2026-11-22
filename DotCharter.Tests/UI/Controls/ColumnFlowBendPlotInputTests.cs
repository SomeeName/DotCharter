using Avalonia.Input;
using DotCharter.UI.Controls;
using DotCharter.UI.ViewModels.Panels;

namespace DotCharter.Tests.UI.Controls;

/// <summary>
///     #751 P5 review H1: the Note tab's bend plot keeps every control the old bend graph had. Its keys go through the
///     graph's own keyboard (Enter adds, Home / End and Page Up / Page Down pick, the arrows move a quarter tone or a
///     slot, Ctrl finer, Shift three slots, Delete removes, Esc deselects, an arrow with nothing selected picks an end),
///     Tab stays the plot's, each key is one write; a right press deletes a handle and a Shift drag is unsnapped.
/// </summary>
public sealed class ColumnFlowBendPlotInputTests
{
    private int _writes;

    private BendEditorViewModel NewEditor() =>
        new(() => [], _ => _writes++, () => new FractionalPosition(0), () => new FractionalPosition(4), () => { },
            FakeLocalization.English());

    private static bool Hit(BendEditorViewModel vm, Key key, KeyModifiers modifiers = KeyModifiers.None) =>
        ColumnFlowBendPlot.HandleKey(vm, key, modifiers);

    [Fact]
    public void EnterAdds_HomeEndAndPageKeysPick_EscDeselects()
    {
        var vm = NewEditor();
        Assert.True(Hit(vm, Key.Enter));
        Assert.Equal([0], vm.Points.Select(p => p.TimeSlot));
        vm.AddOrMovePoint(6, 2.0);
        vm.AddOrMovePoint(10, 0.0);

        Assert.True(Hit(vm, Key.Home));
        Assert.Equal(0, vm.SelectedPoint!.TimeSlot);
        Assert.True(Hit(vm, Key.PageDown));
        Assert.Equal(6, vm.SelectedPoint!.TimeSlot);
        Assert.True(Hit(vm, Key.End));
        Assert.Equal(10, vm.SelectedPoint!.TimeSlot);
        Assert.True(Hit(vm, Key.PageUp));
        Assert.Equal(6, vm.SelectedPoint!.TimeSlot);
        Assert.True(Hit(vm, Key.Escape));
        Assert.Null(vm.SelectedPoint);
    }

    [Fact]
    public void Arrows_StepAQuarterTone_CtrlAnEighth_ShiftThreeSlots_AndPickAnEndWithNothingSelected()
    {
        var vm = NewEditor();
        vm.AddOrMovePoint(2, 1.0);
        vm.AddOrMovePoint(8, 1.0);
        vm.ClearSelection();

        Assert.True(Hit(vm, Key.Left));
        Assert.Equal(8, vm.SelectedPoint!.TimeSlot);
        Assert.True(Hit(vm, Key.Up));
        Assert.Equal(1.25, vm.SelectedPoint!.Step, 6);
        Assert.True(Hit(vm, Key.Up, KeyModifiers.Control));
        Assert.Equal(1.375, vm.SelectedPoint!.Step, 6);
        Assert.True(Hit(vm, Key.Left, KeyModifiers.Shift));
        Assert.Equal(5, vm.SelectedPoint!.TimeSlot);
    }

    [Fact]
    public void TabWalksAndLeavesPastTheEnd_DeleteRemoves_EachKeyIsOneWrite()
    {
        var vm = NewEditor();
        vm.AddOrMovePoint(2, 1.0);
        vm.AddOrMovePoint(8, 1.0);
        vm.ClearSelection();
        Assert.True(Hit(vm, Key.Tab));
        Assert.True(Hit(vm, Key.Tab));
        Assert.False(Hit(vm, Key.Tab));

        _writes = 0;
        Assert.True(Hit(vm, Key.Delete));
        Assert.Single(vm.Points);
        Assert.Equal(1, _writes);
    }

    [Fact]
    public void RightPressDeletesAHandle_LeftPressAddsAndGrabs_ShiftIsUnsnapped()
    {
        var vm = NewEditor();
        var point = vm.AddOrMovePoint(4, 1.0);

        Assert.Null(ColumnFlowBendPlot.Press(vm, point, 4, 1.0, PointerUpdateKind.RightButtonPressed, false));
        Assert.Empty(vm.Points);

        var grabbed = ColumnFlowBendPlot.Press(vm, null, 6, 1.1, PointerUpdateKind.LeftButtonPressed, true);
        vm.EndTransaction();
        Assert.NotNull(grabbed);
        Assert.Equal(6, grabbed.TimeSlot);
        Assert.NotEqual(Math.Round(1.1 / BendEditorViewModel.StepQuantum) * BendEditorViewModel.StepQuantum, grabbed.Step, 6);
    }
}
