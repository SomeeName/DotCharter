using DotCharter.Presentation.Contracts.Dialogs;
using DotCharter.Presentation.Screens.Dialogs;
using DotCharter.Services.Editing;

namespace DotCharter.Tests.Presentation.Dialogs;

/// <summary>
///     Edit ▸ Paste special: the three modes are one radio group, Apply pastes
///     at the playhead or the mouse on the editor's grid, and the choices carry
///     over to the next open (the view model lives only while it is open).
/// </summary>
public sealed class PasteSpecialViewModelTests
{
    private static (PasteSpecialViewModel Vm, RecordingProxy Editing, ChartEditHarness H) Open(
        DialogMemory? memory = null, ChartEditHarness? harness = null)
    {
        var h = harness ?? new ChartEditHarness();
        var (editing, recorder) = RecordingProxy.Create<IGlobalEditingCoordinator>();
        return (new PasteSpecialViewModel(editing, h.Transport, h.State, memory ?? new DialogMemory(), new FakeAppLogger()),
            recorder, h);
    }

    [Fact]
    public void The_three_modes_are_one_radio_group()
    {
        var (vm, _, _) = Open();
        Assert.True(vm.IsPreserve);

        vm.IsOverwriteAll = true;
        Assert.Equal((false, false, true), (vm.IsPreserve, vm.IsOverwriteSameType, vm.IsOverwriteAll));

        vm.IsOverwriteAll = false;
        Assert.True(vm.IsOverwriteAll, "unchecking the checked radio does not leave the group empty");
    }

    [Fact]
    public void Apply_pastes_at_the_playhead_on_the_editors_grid_and_answers_true()
    {
        var (vm, editing, h) = Open();
        h.State.CurrentTimeMs = 1500;
        h.Transport.GridDivisor = 8;
        vm.IsOverwriteSameType = true;

        vm.ApplyCommand.Execute(null);

        Assert.Equal("PasteSpecial(1500, 8, OverwriteSameType)", Assert.Single(editing.Calls));
        Assert.True(ChartEditHarness.Answer(vm.Result));
    }

    [Fact]
    public void At_the_mouse_it_pastes_where_the_pointer_last_was()
    {
        var (vm, editing, h) = Open();
        h.State.CurrentTimeMs = 1500;
        h.State.LastPointerTimeMs = 250;
        vm.UseMousePosition = true;

        vm.ApplyCommand.Execute(null);

        Assert.StartsWith("PasteSpecial(250, ", Assert.Single(editing.Calls));
    }

    [Fact]
    public void The_choices_carry_over_to_the_next_open()
    {
        var memory = new DialogMemory();
        var (first, _, h) = Open(memory);
        first.IsOverwriteAll = true;
        first.UseMousePosition = true;
        first.CancelCommand.Execute(null);

        var (second, _, _) = Open(memory, h);

        Assert.True(second.IsOverwriteAll);
        Assert.True(second.UseMousePosition);
    }

    [Fact]
    public void Cancel_answers_false_and_pastes_nothing()
    {
        var (vm, editing, _) = Open();

        Assert.True(vm.TryCancel());

        Assert.False(ChartEditHarness.Answer(vm.Result));
        Assert.Empty(editing.Calls);
    }

    [Fact]
    public void Conforms_to_its_contract()
    {
        var (vm, _, _) = Open();
        Assert.Empty(ContractConformanceTests.Violations(typeof(IPasteSpecialScreen), vm));
    }
}
