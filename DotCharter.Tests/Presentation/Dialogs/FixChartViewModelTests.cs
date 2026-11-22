using System.Reflection;
using DotCharter.Presentation.Contracts.Dialogs;
using DotCharter.Presentation.Screens.Dialogs;
using DotCharter.Services.Editing.Maintenance;

namespace DotCharter.Tests.Presentation.Dialogs;

/// <summary>
///     #497: the Fix chart dialog lists what each group would change before it changes anything, preselects the
///     group the palette id names, counts Polish after Errors only when Errors is ticked, and Apply runs the ticked
///     groups through the one maintenance call.
/// </summary>
public sealed class FixChartViewModelTests
{
    private static readonly FixChartPreview Both = new(
        [new FixChartChange("duplicatesRemoved", 12), new FixChartChange("slidesFixed", 1)],
        [new FixChartChange("end phrase", 3)]);

    private static readonly FixChartPreview PolishAlone = new([], [new FixChartChange("end phrase", 5)]);

    private static (FixChartViewModel Vm, FakeMaintenance Maintenance) Create(FixChartGroups preselect = FixChartGroups.Both, FixChartPreview? both = null)
    {
        var harness = new ChartEditHarness();
        var maintenance = DispatchProxy.Create<IChartMaintenanceService, FakeMaintenance>();
        var fake = (FakeMaintenance)(object)maintenance;
        fake.BothAnswer = both ?? Both;
        var vm = new FixChartViewModel(harness.Services, maintenance);
        vm.Configure(new FixChartRequest(preselect));
        return (vm, fake);
    }

    [Fact]
    public async Task OnOpening_ItListsEachGroupsChanges_BeforeAnythingChanges()
    {
        var (vm, maintenance) = Create();
        await vm.Pending;

        Assert.False(vm.IsChecking);
        Assert.Equal(["Duplicate notes and chords removed: 12", "Slides corrected: 1"], vm.ErrorsChanges.Select(c => c.Text));
        Assert.Equal(["End phrase added or fixed: 3"], vm.PolishChanges.Select(c => c.Text));
        Assert.Equal("Apply makes 16 changes as one undo step.", vm.TotalSummary);
        Assert.Empty(maintenance.Applied);
        Assert.True(vm.CanApply);
    }

    [Theory]
    [InlineData(FixChartGroups.Errors, true, false)]
    [InlineData(FixChartGroups.Polish, false, true)]
    [InlineData(FixChartGroups.Both, true, true)]
    public async Task ThePaletteIdsGroup_IsTheOneTicked(FixChartGroups preselect, bool errors, bool polish)
    {
        var (vm, _) = Create(preselect);
        await vm.Pending;

        Assert.Equal((errors, polish), (vm.FixErrors, vm.FixPolish));
    }

    [Fact]
    public async Task Apply_RunsExactlyTheTickedGroups_InOneCall_AndClosesTrue()
    {
        var (vm, maintenance) = Create(FixChartGroups.Polish);
        await vm.Pending;

        vm.ApplyCommand.Execute(null);

        Assert.Equal([FixChartGroups.Polish], maintenance.Applied);
        Assert.True(await vm.Result);
    }

    [Fact]
    public async Task UntickingErrors_CountsPolishOnItsOwn_AndTheTotalFollows()
    {
        var (vm, maintenance) = Create();
        await vm.Pending;

        vm.FixErrors = false;
        await vm.Pending;

        Assert.Equal(["End phrase added or fixed: 5"], vm.PolishChanges.Select(c => c.Text));
        Assert.Equal("Apply makes 5 changes as one undo step.", vm.TotalSummary);
        Assert.Equal([FixChartGroups.Both, FixChartGroups.Polish], maintenance.Previews);
    }

    [Fact]
    public async Task NothingTicked_OrNothingToChange_DisablesApply_AndSaysWhy()
    {
        var (vm, _) = Create();
        await vm.Pending;

        vm.FixErrors = false;
        await vm.Pending;
        vm.FixPolish = false;

        Assert.False(vm.CanApply);
        Assert.Equal("Tick a group to fix.", vm.TotalSummary);

        var (empty, _) = Create(both: FixChartPreview.Empty);
        await empty.Pending;
        Assert.False(empty.CanApply);
        Assert.Equal("Nothing to change.", empty.TotalSummary);
    }

    [Fact]
    public async Task Cancel_ChangesNothing_AndClosesFalse()
    {
        var (vm, maintenance) = Create();
        await vm.Pending;

        vm.CancelCommand.Execute(null);

        Assert.False(await vm.Result);
        Assert.Empty(maintenance.Applied);
    }

    /// <summary>Answers the two Fix chart members and records them; every other member is unreachable from this dialog.</summary>
    public class FakeMaintenance : DispatchProxy
    {
        public FixChartPreview BothAnswer { get; set; } = Both;

        public List<FixChartGroups> Applied { get; } = [];

        public List<FixChartGroups> Previews { get; } = [];

        protected override object? Invoke(MethodInfo? targetMethod, object?[]? args)
        {
            var groups = (FixChartGroups)args![0]!;
            switch (targetMethod!.Name)
            {
                case nameof(IChartMaintenanceService.FixChart):
                    Applied.Add(groups);
                    return null;
                case nameof(IChartMaintenanceService.PreviewFixChartAsync):
                    Previews.Add(groups);
                    return Task.FromResult(groups == FixChartGroups.Polish ? PolishAlone : BothAnswer);
                default:
                    throw new NotSupportedException(targetMethod.Name);
            }
        }
    }
}
