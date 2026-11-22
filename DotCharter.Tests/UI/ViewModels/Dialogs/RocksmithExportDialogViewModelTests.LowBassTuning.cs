namespace DotCharter.Tests.UI.ViewModels.Dialogs;

/// <summary>#906: the export dialog's "Fix low bass tuning" box, its reason line, its warning and its build option.</summary>
public sealed partial class RocksmithExportDialogViewModelTests
{
    [Fact]
    public void A_ticked_b_standard_bass_offers_the_fix_on_by_default_and_names_it()
    {
        var loc = FakeLocalization.English();
        var project = CreateValidProject();
        project.Arrangements.Add(LowBass("Low Bass"));
        var vm = OpenHarness(project).Vm;

        Assert.True(vm.OffersLowBassTuningFix);
        Assert.True(vm.FixLowBassTuning);
        Assert.Equal(loc.Format("Dialog_Export_LowBassTuningReason", "Low Bass"), vm.LowBassTuningReason);
        Assert.False(vm.HasLowBassTuningWarning);
        Assert.True(vm.BuildOptions(project).FixLowBassTuning);
    }

    [Fact]
    public void Clearing_the_box_warns_and_builds_without_the_fix()
    {
        var loc = FakeLocalization.English();
        var project = CreateValidProject();
        project.Arrangements.Add(LowBass("Low Bass"));
        var vm = OpenHarness(project).Vm;

        vm.FixLowBassTuning = false;

        Assert.True(vm.HasLowBassTuningWarning);
        Assert.Equal(loc.Format("Dialog_Export_LowBassTuningWarning", "Low Bass"), vm.LowBassTuningWarning);
        Assert.False(vm.BuildOptions(project).FixLowBassTuning);
    }

    [Fact]
    public void Unticking_the_low_bass_hides_the_box_and_an_e_standard_bass_never_shows_it()
    {
        var project = CreateValidProject();
        project.Arrangements.Add(LowBass("Low Bass"));
        project.Arrangements.Add(LowBass("E Bass", 0));
        var vm = OpenHarness(project).Vm;
        Assert.True(vm.OffersLowBassTuningFix);

        var changed = new List<string?>();
        vm.PropertyChanged += (_, e) => changed.Add(e.PropertyName);
        vm.Arrangements.Single(a => a.Name == "Low Bass").IsSelected = false;

        Assert.False(vm.OffersLowBassTuningFix);
        Assert.Equal("", vm.LowBassTuningReason);
        Assert.Contains(nameof(vm.OffersLowBassTuningFix), changed);
    }

    [Fact]
    public void The_choice_is_remembered_for_the_next_open_of_the_project()
    {
        var project = CreateValidProject();
        project.Arrangements.Add(LowBass("Low Bass"));
        var harness = OpenHarness(project);
        harness.Vm.FixLowBassTuning = false;
        harness.Vm.TryCancel();

        harness.Open();

        Assert.False(harness.Vm.FixLowBassTuning);
    }

    private static Arrangement LowBass(string name, int offset = -5)
    {
        var bass = new Arrangement { ArrangementType = ArrangementType.Bass, Name = name, StringCount = 4 };
        for (var i = 0; i < 6; i++) bass.TuningPitches.Add(new TuningOffset(offset));
        return bass;
    }
}
