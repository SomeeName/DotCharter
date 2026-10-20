using DotCharter.Localization;
using DotCharter.Services.Export.Validation;
using DotCharter.Tests.Presentation.Dialogs;
using DotCharter.UI.ViewModels.Dialogs;
using DotCharter.UI.ViewModels.Editor.Inspector;

namespace DotCharter.Tests.UI.ViewModels.Editor;

/// <summary>
///     The FEHLER tab's export-readiness block, driven on the extracted
///     <see cref="ExportReadinessViewModel" /> over a real chart editor: the
///     verdict stays "not checked" until the explicit check runs, the debounced
///     refresh only re-validates a result that was asked for, a project switch
///     drops the result, and a finding's jump switches arrangement before it
///     seeks.
/// </summary>
public sealed class ExportReadinessViewModelTests
{
    private static ExportReadinessViewModel Create(ChartEditHarness harness, ILocalizationService localization) =>
        new(harness.State, new CheckpointsViewModelTests.FakeDocumentService(), harness.Transport, harness.Editor,
            localization, RecordingProxy.Create<DotCharter.Services.Editing.Maintenance.IChartMaintenanceService>().Proxy,
            new DotCharter.Presentation.Dialogs.DialogsSlot(), new FakeAppLogger());

    [Fact]
    public void BeforeTheCheck_TheVerdictSaysNotChecked_AndTheRefreshDoesNothing()
    {
        var localization = FakeLocalization.English();
        var vm = Create(new ChartEditHarness(), localization);

        vm.RefreshIfChecked();

        Assert.False(vm.HasExportCheckRun);
        Assert.Empty(vm.ExportFindings);
        Assert.Equal(localization["Inspector_Export_NotChecked"], vm.ExportReadinessSummary);
    }

    [Fact]
    public void TheCheck_ListsTheValidatorsFindings_AndTheVerdictCountsThem()
    {
        var harness = new ChartEditHarness();
        var localization = FakeLocalization.KeysOnly();
        var vm = Create(harness, localization);

        vm.CheckExportReadinessCommand.Execute(null);

        var expected = new ChartExportValidator().Validate(harness.Project);
        Assert.True(vm.HasExportCheckRun);
        Assert.Equal(expected.Count, vm.ExportFindings.Count);
        Assert.Equal(expected.Count > 0, vm.HasExportFindings);
        var errors = expected.Count(f => f.Severity == ExportFindingSeverity.Error);
        Assert.Equal(
            expected.Count > 0
                ? localization.Format("Dialog_Export_ValidationSummary", errors, expected.Count - errors)
                : localization["Dialog_Export_ValidationOk"],
            vm.ExportReadinessSummary);
    }

    [Fact]
    public void AReset_DropsTheResult()
    {
        var harness = new ChartEditHarness();
        var localization = FakeLocalization.KeysOnly();
        var vm = Create(harness, localization);
        vm.CheckExportReadinessCommand.Execute(null);
        vm.ExportFindings.Add(new ExportValidationItem
        {
            Severity = ExportFindingSeverity.Error,
            Location = "Lead",
            Message = "m"
        });

        vm.Reset();

        Assert.False(vm.HasExportCheckRun);
        Assert.Empty(vm.ExportFindings);
        Assert.False(vm.HasExportFindings);
        Assert.Equal(localization["Inspector_Export_NotChecked"], vm.ExportReadinessSummary);
    }

    [Fact]
    public void TheJump_SwitchesToTheFindingsArrangement_ThenSeeks()
    {
        var harness = new ChartEditHarness();
        var rhythm = new Arrangement { Name = "Rhythm" };
        rhythm.Levels.Add(new ChartLevel { DifficultyIndex = 0 });
        harness.Project.Arrangements.Add(rhythm);
        var vm = Create(harness, FakeLocalization.KeysOnly());

        vm.JumpToExportFindingCommand.Execute(new ExportValidationItem
        {
            Severity = ExportFindingSeverity.Warning,
            Location = "Rhythm · 0:01.250",
            Message = "m",
            ArrangementName = "rhythm",
            TimeMs = 1250
        });

        Assert.Same(rhythm, harness.Editor.ActiveArrangement);
        Assert.Equal(1250, harness.State.CurrentTimeMs);
    }

    [Fact]
    public void AFindingRow_CarriesItsFix()
    {
        var harness = new ChartEditHarness();
        var vm = Create(harness, FakeLocalization.KeysOnly());

        vm.CheckExportReadinessCommand.Execute(null);

        var expected = new ChartExportValidator().Validate(harness.Project);
        Assert.Equal(expected.Count(f => f.Fix != ExportFixKind.None), vm.ExportFindings.Count(i => i.HasFix));
        Assert.All(vm.ExportFindings.Where(i => i.HasFix), i => Assert.NotEqual("", i.FixDescription));
    }
}
