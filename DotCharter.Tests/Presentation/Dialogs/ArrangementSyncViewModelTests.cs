using DotCharter.Presentation.Contracts.Dialogs;
using DotCharter.Presentation.Screens.Dialogs;
using DotCharter.Services.Editing.Maintenance;

namespace DotCharter.Tests.Presentation.Dialogs;

/// <summary>
///     Arrangement ▸ Sync sounds: the operation OVERWRITES, so the dialog says
///     what a pair would change before anything is pressed, and Apply stays off
///     while the pair would change nothing.
/// </summary>
public sealed class ArrangementSyncViewModelTests
{
    private static ChartEditHarness TwoArrangements(bool shared)
    {
        var h = new ChartEditHarness();
        var rhythm = new Arrangement { Name = "Rhythm" };
        rhythm.Levels.Add(new ChartLevel { DifficultyIndex = 0 });
        h.Project.Arrangements.Add(rhythm);
        if (shared)
        {
            var position = h.Editor.SnapToGrid(500);
            // The same sound in both, charted differently: the sync would carry the accent across.
            h.Project.Arrangements[0].Levels[0].Notes.Add(
                new GuitarNote { Position = position, StringIndex = 2, Fret = 5, Accent = true });
            rhythm.Levels[0].Notes.Add(new GuitarNote { Position = position, StringIndex = 2, Fret = 5 });
        }

        return h;
    }

    private static (ArrangementSyncViewModel Vm, RecordingProxy Maintenance) Open(ChartEditHarness h)
    {
        var (maintenance, recorder) = RecordingProxy.Create<IChartMaintenanceService>();
        return (new ArrangementSyncViewModel(h.State, maintenance, FakeLocalization.English(), new FakeAppLogger()),
            recorder);
    }

    [Fact]
    public void It_opens_on_the_active_arrangement_as_source_and_the_next_one_as_target()
    {
        var h = TwoArrangements(shared: true);
        h.State.ActiveArrangement = h.Project.Arrangements[1];

        var (vm, _) = Open(h);

        Assert.Equal(["Lead", "Rhythm"], vm.Arrangements);
        Assert.Equal((1, 0), (vm.SourceIndex, vm.TargetIndex));
    }

    [Fact]
    public void A_pair_that_shares_sounds_previews_the_change_and_Apply_syncs_it()
    {
        var h = TwoArrangements(shared: true);
        var (vm, maintenance) = Open(h);

        Assert.True(vm.CanApply);
        Assert.NotEqual(FakeLocalization.English()["ArrangementSync_NothingShared"], vm.Preview);

        vm.ApplyCommand.Execute(null);

        Assert.Equal("SyncArrangement(DotCharter.Core.Domain.Project.Arrangement, DotCharter.Core.Domain.Project.Arrangement)",
            Assert.Single(maintenance.Calls));
        var (_, args) = Assert.Single(maintenance.Invocations);
        Assert.Same(h.Project.Arrangements[0], args[0]);
        Assert.Same(h.Project.Arrangements[1], args[1]);
        Assert.True(ChartEditHarness.Answer(vm.Result));
    }

    [Fact]
    public void Swap_swaps_the_pair_and_refreshes_the_preview()
    {
        var h = TwoArrangements(shared: true);
        var (vm, maintenance) = Open(h);
        var changed = new List<string?>();
        vm.PropertyChanged += (_, e) => changed.Add(e.PropertyName);

        vm.SwapCommand.Execute(null);
        vm.ApplyCommand.Execute(null);

        Assert.Equal((1, 0), (vm.SourceIndex, vm.TargetIndex));
        Assert.Contains(nameof(IArrangementSyncScreen.SourceIndex), changed);
        Assert.Contains(nameof(IArrangementSyncScreen.TargetIndex), changed);
        Assert.Same(h.Project.Arrangements[1], maintenance.Invocations.Single().Args[0]);
    }

    [Fact]
    public void The_same_arrangement_twice_or_nothing_shared_leaves_Apply_off()
    {
        var h = TwoArrangements(shared: false);
        var (vm, maintenance) = Open(h);
        Assert.False(vm.CanApply);
        Assert.Equal(FakeLocalization.English()["ArrangementSync_NothingShared"], vm.Preview);

        vm.TargetIndex = vm.SourceIndex;
        Assert.False(vm.CanApply);
        Assert.Equal(FakeLocalization.English()["ArrangementSync_PickTwo"], vm.Preview);

        vm.ApplyCommand.Execute(null);
        Assert.Empty(maintenance.Calls);
        Assert.False(vm.Result.IsCompleted);
    }

    [Fact]
    public void Cancel_answers_false()
    {
        var (vm, maintenance) = Open(TwoArrangements(shared: true));

        vm.CancelCommand.Execute(null);

        Assert.False(ChartEditHarness.Answer(vm.Result));
        Assert.Empty(maintenance.Calls);
    }

    [Fact]
    public void Conforms_to_its_contract()
    {
        var (vm, _) = Open(TwoArrangements(shared: true));
        Assert.Empty(ContractConformanceTests.Violations(typeof(IArrangementSyncScreen), vm));
    }
}
