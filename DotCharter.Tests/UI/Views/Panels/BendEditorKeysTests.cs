using Avalonia.Input;
using DotCharter.Localization;
using DotCharter.UI.ViewModels.Panels;
using DotCharter.UI.Views.Panels;

namespace DotCharter.Tests.UI.Views.Panels;

/// <summary>
///     #593 / #595: the bend graph without a mouse. Enter adds a point, Home / End and Page Up /
///     Page Down pick points, the arrows pick a point when none is selected and move it when one
///     is, Delete removes it, and each edit is one write. The callout over the graph says "Slot"
///     in the UI language.
/// </summary>
public sealed class BendEditorKeysTests
{
    private int _writes;

    private BendEditorViewModel NewEditor(ILocalizationService? localization = null) =>
        new(() => [], _ => _writes++, () => new FractionalPosition(0), () => new FractionalPosition(4), () => { },
            localization ?? FakeLocalization.English());

    [Fact]
    public void Enter_AddsAPointAtSlotZero_ThenOneAfterTheSelectedPoint()
    {
        var vm = NewEditor();

        Assert.True(BendEditorKeys.Handle(vm, Key.Enter, KeyModifiers.None));
        Assert.Equal([0], vm.Points.Select(p => p.TimeSlot));
        Assert.Same(vm.Points[0], vm.SelectedPoint);

        Assert.True(BendEditorKeys.Handle(vm, Key.Up, KeyModifiers.None));
        Assert.True(BendEditorKeys.Handle(vm, Key.Insert, KeyModifiers.None));

        Assert.Equal([0, 1], vm.Points.Select(p => p.TimeSlot));
        Assert.Same(vm.Points[1], vm.SelectedPoint);
        Assert.Equal(BendEditorViewModel.StepQuantum, vm.Points[1].Step, 6);
    }

    [Fact]
    public void HomeEndAndPageKeys_WalkThePointsInTimeOrder()
    {
        var vm = NewEditor();
        vm.AddOrMovePoint(2, 1.0);
        vm.AddOrMovePoint(6, 2.0);
        vm.AddOrMovePoint(10, 0.0);
        vm.ClearSelection();

        Assert.True(BendEditorKeys.Handle(vm, Key.Home, KeyModifiers.None));
        Assert.Equal(2, vm.SelectedPoint!.TimeSlot);
        Assert.True(BendEditorKeys.Handle(vm, Key.PageDown, KeyModifiers.None));
        Assert.Equal(6, vm.SelectedPoint!.TimeSlot);
        Assert.True(BendEditorKeys.Handle(vm, Key.End, KeyModifiers.None));
        Assert.Equal(10, vm.SelectedPoint!.TimeSlot);
        Assert.False(BendEditorKeys.Handle(vm, Key.PageDown, KeyModifiers.None));
        Assert.True(BendEditorKeys.Handle(vm, Key.PageUp, KeyModifiers.None));
        Assert.Equal(6, vm.SelectedPoint!.TimeSlot);
    }

    [Fact]
    public void Arrows_PickAPointWhenNoneIsSelected_ThenMoveIt()
    {
        var vm = NewEditor();
        vm.AddOrMovePoint(2, 1.0);
        vm.AddOrMovePoint(8, 1.0);
        vm.ClearSelection();

        Assert.True(BendEditorKeys.Handle(vm, Key.Right, KeyModifiers.None));
        Assert.Equal(2, vm.SelectedPoint!.TimeSlot);
        vm.ClearSelection();
        Assert.True(BendEditorKeys.Handle(vm, Key.Left, KeyModifiers.None));
        Assert.Equal(8, vm.SelectedPoint!.TimeSlot);

        Assert.True(BendEditorKeys.Handle(vm, Key.Left, KeyModifiers.Shift));
        Assert.Equal(5, vm.SelectedPoint!.TimeSlot);
        Assert.True(BendEditorKeys.Handle(vm, Key.Up, KeyModifiers.None));
        Assert.Equal(1.0 + BendEditorViewModel.StepQuantum, vm.SelectedPoint!.Step, 6);
    }

    [Fact]
    public void Delete_RemovesTheSelectedPoint_AndEscClearsTheSelection()
    {
        var vm = NewEditor();
        vm.AddOrMovePoint(3, 1.0);
        vm.AddOrMovePoint(7, 1.0);

        Assert.True(BendEditorKeys.Handle(vm, Key.Delete, KeyModifiers.None));
        Assert.Equal([3], vm.Points.Select(p => p.TimeSlot));
        Assert.False(BendEditorKeys.Handle(vm, Key.Delete, KeyModifiers.None));

        BendEditorKeys.Handle(vm, Key.Home, KeyModifiers.None);
        Assert.True(BendEditorKeys.Handle(vm, Key.Escape, KeyModifiers.None));
        Assert.Null(vm.SelectedPoint);
    }

    [Fact]
    public void Escape_IsHandledOnlyWhenItClearsASelection()
    {
        var vm = NewEditor();
        vm.AddOrMovePoint(3, 1.0);

        Assert.True(BendEditorKeys.Handle(vm, Key.Escape, KeyModifiers.None));
        Assert.Null(vm.SelectedPoint);
        Assert.False(BendEditorKeys.Handle(vm, Key.Escape, KeyModifiers.None));
    }

    [Theory]
    [InlineData(Key.Home, KeyModifiers.Control)]
    [InlineData(Key.End, KeyModifiers.Control)]
    [InlineData(Key.PageUp, KeyModifiers.Control)]
    [InlineData(Key.PageDown, KeyModifiers.Alt)]
    [InlineData(Key.Enter, KeyModifiers.Alt)]
    [InlineData(Key.Insert, KeyModifiers.Control)]
    [InlineData(Key.Delete, KeyModifiers.Control)]
    [InlineData(Key.Back, KeyModifiers.Meta)]
    [InlineData(Key.Escape, KeyModifiers.Alt)]
    public void ModifierChords_AreNotHandled_AndChangeNothing(Key key, KeyModifiers modifiers)
    {
        var vm = NewEditor();
        vm.AddOrMovePoint(3, 1.0);
        vm.AddOrMovePoint(7, 1.0);
        var writes = _writes;

        Assert.False(BendEditorKeys.Handle(vm, key, modifiers));

        Assert.Equal([3, 7], vm.Points.Select(p => p.TimeSlot));
        Assert.Equal(7, vm.SelectedPoint!.TimeSlot);
        Assert.Equal(writes, _writes);
    }

    [Fact]
    public void EachKeyEdit_IsOneWrite()
    {
        var vm = NewEditor();
        vm.AddOrMovePoint(4, 1.0);
        var before = _writes;

        BendEditorKeys.Handle(vm, Key.Right, KeyModifiers.None);

        Assert.Equal(before + 1, _writes);
    }

    [Fact]
    public void AnUnrelatedKey_IsLeftToTheWindow()
    {
        var vm = NewEditor();
        vm.AddOrMovePoint(4, 1.0);

        Assert.False(BendEditorKeys.Handle(vm, Key.A, KeyModifiers.None));
        Assert.False(BendEditorKeys.Handle(vm, Key.Tab, KeyModifiers.None));
    }
}
