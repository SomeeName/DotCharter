using DotCharter.Services.Export;
using DotCharter.Tests.UI.ViewModels.Dialogs;
using DotCharter.Tone.Infrastructure.Storage;

namespace DotCharter.Tests.Services.Export.Rsxr;

public sealed class RsxrDialogStateTests : IDisposable
{
    private readonly string _directory = Directory.CreateTempSubdirectory("dotcharter-platform-selection-").FullName;

    public void Dispose() => BestEffortDelete.Directory(_directory);

    [Theory]
    [InlineData(ExportPlatform.Mac, false)]
    [InlineData(ExportPlatform.PcAndMac, false)]
    [InlineData(ExportPlatform.Mac, true)]
    [InlineData(ExportPlatform.PcAndMac, true)]
    public async Task Mac_availability_and_package_names_follow_accepted_extended_content(ExportPlatform platform, bool accepted)
    {
        using var keys = TestRocksmithKeys.UseSynthetic();
        var project = RocksmithExportDialogViewModelTests.CreateValidProject();
        var extended = ChartProjectCloner.CloneArrangement(project.Arrangements[0]);
        extended.Name = "Extended";
        extended.StringCount = 7;
        extended.TuningPitches.Add(new TuningOffset(0));
        extended.TuningPitches[0] = new TuningOffset(accepted ? 0 : 25);
        project.Arrangements.Add(extended);
        var harness = new RocksmithExportDialogViewModelTests.Harness(project);
        harness.Config.AppConfig.ExportApplyDlcBuilderFixes = false;
        harness.Config.AppConfig.ExportGenerateDynamicDifficulty = false;
        using var vm = harness.Open();
        var row = vm.Arrangements.Single(a => a.Name == extended.Name);
        Assert.True(row.IsExtendedString);
        row.IsSelected = false;
        vm.IncludeVocals = false;
        vm.OutputPath = Path.Combine(_directory, "pair_p.psarc");
        vm.AudioPath = Path.Combine(_directory, "audio.wem");
        File.WriteAllBytes(vm.AudioPath, new byte[4000]);
        vm.PreviewAudioPath = vm.AudioPath;
        vm.Target.SelectedPlatformOption = vm.Target.PlatformOptions.Single(p => p.Value == platform);
        Assert.True(vm.ExportCommand.CanExecute(null), vm.ExportBlockedReason);
        var changed = new List<string?>();
        var gateChanges = 0;
        vm.PropertyChanged += (_, e) => changed.Add(e.PropertyName);
        vm.ExportCommand.CanExecuteChanged += (_, _) => gateChanges++;

        row.IsSelected = true;

        foreach (var property in new[] { nameof(vm.MacBlock), nameof(vm.PlatformNote), nameof(vm.HasPlatformNote),
                     nameof(vm.ExportBlockedReason), nameof(vm.IsExportBlocked) })
        {
            Assert.Contains(property, changed);
        }
        Assert.True(gateChanges > 0);
        Assert.Equal(!accepted, vm.Target.CanChoosePackage);
        // #898: the RS Extended Range package has a plain Mac form, so nothing blocks Mac.
        Assert.Null(vm.MacBlock);
        Assert.Equal(accepted, vm.OffersStandardPackage);
        Assert.Equal(platform, vm.BuildOptions(project).Platform);
        Assert.Equal(accepted, vm.BuildOptions(project).WriteStandardPackage);
        Assert.True(vm.ExportCommand.CanExecute(null), vm.ExportBlockedReason);
        Assert.Equal(accepted, vm.OutputPath.Contains("_rsxr", StringComparison.Ordinal));
        var tag = accepted ? "_rsxr" : "";
        var loc = FakeLocalization.English();
        var note = (platform, accepted) switch
        {
            (ExportPlatform.PcAndMac, true) => loc.Format("Dialog_Export_WritesFour", "pair_rsxr_p.psarc", "pair_rsxr_m.psarc", "pair_p.psarc", "pair_m.psarc"),
            (ExportPlatform.PcAndMac, false) => loc.Format("Dialog_Export_WritesBoth", "pair_p.psarc", "pair_m.psarc"),
            (_, true) => loc.Format("Dialog_Export_WritesBoth", "pair_rsxr_m.psarc", "pair_m.psarc"),
            _ => ""
        };
        Assert.Equal(note, vm.PlatformNote);
        Assert.Equal(note.Length > 0, vm.HasPlatformNote);
        await vm.ExportCommand.ExecuteAsync(null);
        Assert.True(vm.HasResult, string.Join('|', harness.Logger.Snapshot().Select(e => e.Message)));
        var names = Directory.GetFiles(_directory, "*.psarc").Select(Path.GetFileName).Order(StringComparer.Ordinal).ToArray();
        string[] expected = (platform, accepted) switch
        {
            (ExportPlatform.PcAndMac, true) => ["pair_m.psarc", "pair_p.psarc", "pair_rsxr_m.psarc", "pair_rsxr_p.psarc"],
            (ExportPlatform.PcAndMac, false) => ["pair_m.psarc", "pair_p.psarc"],
            (_, true) => ["pair_m.psarc", "pair_rsxr_m.psarc"],
            _ => ["pair_m.psarc"]
        };
        Assert.Equal(expected, names);
    }

    [Theory]
    [InlineData(false, 7, false)]
    [InlineData(false, 8, false)]
    [InlineData(true, 5, false)]
    [InlineData(true, 6, false)]
    [InlineData(false, 7, true)]
    [InlineData(false, 8, true)]
    [InlineData(true, 5, true)]
    [InlineData(true, 6, true)]
    public void Extended_selection_locks_plain_and_suppresses_the_stock_plain_warning(bool bass, int strings, bool withKeys)
    {
        using var keys = withKeys ? TestRocksmithKeys.UseSynthetic() : TestRocksmithKeys.UseNoKeys();
        var project = RsxrTestSupport.Chart(bass, strings).Project;
        using var vm = new RocksmithExportDialogViewModelTests.Harness(project).Open();
        Assert.False(vm.Target.CanChoosePackage);
        Assert.True(vm.Target.SelectedPackageOption.Plain);
        Assert.False(vm.Target.WritesPlainPackage);
        Assert.EndsWith("_rsxr_p.psarc", vm.OutputPath);
        vm.Arrangements[0].IsSelected = false;
        Assert.Equal(withKeys, vm.Target.CanChoosePackage);
        Assert.False(vm.OutputPath.EndsWith("_rsxr_p.psarc", StringComparison.Ordinal));
    }
}
