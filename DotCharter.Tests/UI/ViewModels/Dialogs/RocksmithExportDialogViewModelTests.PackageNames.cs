using CommunityToolkit.Mvvm.Input;
using DotCharter.Presentation.Screens.Dialogs;
using DotCharter.Services.Export;
using DotCharter.Services.Export.Rocksmith;

namespace DotCharter.Tests.UI.ViewModels.Dialogs;

public sealed partial class RocksmithExportDialogViewModelTests
{
    [Theory]
    [InlineData("en-US")]
    [InlineData("de-DE")]
    [InlineData("ja-JP")]
    public void Test_package_refusal_has_a_localized_message_in_every_builtin_locale(string culture)
    {
        var key = RocksmithTestPackageOwnershipException.MessageKey;
        var text = FakeLocalization.For(culture)[key];
        Assert.False(string.IsNullOrWhiteSpace(text));
        Assert.NotEqual(key, text);
        if (culture != "en-US") Assert.NotEqual(FakeLocalization.English()[key], text);
    }

    [Fact]
    public async Task Test_package_refusals_show_localized_text_and_keep_technical_details_separate()
    {
        using var keys = TestRocksmithKeys.UseNoKeys();
        var prompt = new RecordingPrompt();
        var harness = new Harness(CreateValidProject(), prompt);
        using var vm = harness.Open();
        vm.TestFolder = _tempDir;
        vm.AudioPath = CreateFakeWem();
        var existing = Path.Combine(_tempDir, "DotCharter_Dialog-Song_test_p.psarc");
        File.WriteAllText(existing, "foreign package");

        await ((IAsyncRelayCommand)vm.TestExportCommand).ExecuteAsync(null);

        Assert.Equal("foreign package", File.ReadAllText(existing));
        Assert.Equal(harness.Edit.Localization[RocksmithTestPackageOwnershipException.MessageKey], prompt.LastMessage);
        Assert.Contains(nameof(RocksmithTestPackageOwnershipException), prompt.LastDetails ?? "", StringComparison.Ordinal);
        Assert.False(prompt.IsBuildingWhileShown ?? true);
        Assert.False(vm.HasResult);
    }

    [Theory]
    [InlineData(false, false, "_p.psarc")]
    [InlineData(true, false, "_rsxr_p.psarc")]
    [InlineData(false, true, "_m.psarc")]
    [InlineData(true, true, "_m.psarc")]
    public void Release_names_keep_sanitization_and_follow_versions_for_each_platform(bool extended, bool mac, string suffix)
    {
        using var keys = TestRocksmithKeys.UseNoKeys();
        var project = CreateValidProject();
        project.Chart.Artist = "The Synthetic Artist";
        project.Chart.SongName = "A Synthetic: Tune";
        project.Chart.Version = "1.2";
        if (extended) project.Arrangements[0].StringCount = 7;
        var harness = new Harness(project);
        harness.State.CurrentProjectPath = Path.Combine(_tempDir, "project.dcproj");
        using var vm = harness.Open();
        if (mac) vm.Target.SelectedPlatformOption = vm.Target.PlatformOptions.Single(option => option.Value == ExportPlatform.Mac);
        Assert.Equal("The-Synthetic-Artist_A-Synthetic-Tune_v1_2" + suffix, Path.GetFileName(vm.OutputPath));
        vm.Version = " 2.1 ";
        Assert.Equal("The-Synthetic-Artist_A-Synthetic-Tune_v2_1" + suffix, Path.GetFileName(vm.OutputPath));
        vm.OutputPath = Path.Combine(_tempDir, "chosen" + suffix);
        var chosen = vm.OutputPath;
        vm.Version = "3";
        Assert.Equal(chosen, vm.OutputPath);
    }

    [Theory]
    [InlineData(false, false, false, "_test_p.psarc")]
    [InlineData(true, false, false, "_test_v1_p.psarc")]
    [InlineData(false, true, false, "_test_rsxr_p.psarc")]
    [InlineData(true, true, false, "_test_v1_rsxr_p.psarc")]
    [InlineData(false, false, true, "_test_m.psarc")]
    [InlineData(true, false, true, "_test_v1_m.psarc")]
    [InlineData(false, true, true, "_test_m.psarc")]
    [InlineData(true, true, true, "_test_v1_m.psarc")]
    public void Test_names_use_snapshot_identity_without_changing_key_title_or_internal_version_rules(
        bool running, bool extended, bool mac, string suffix)
    {
        var snapshot = CreateValidProject();
        snapshot.Chart.Version = "9.4";
        var plan = RocksmithTestExport.Plan(snapshot, "customkey", [], running);
        var path = RocksmithTestBuild.Prepare(snapshot, plan, _tempDir, mac, extended);
        Assert.Equal("DotCharter_Dialog-Song" + suffix, Path.GetFileName(path));
        Assert.Equal("customkey" + (running ? "v1" : ""), plan.DlcKey);
        Assert.Equal("Dialog Song" + (running ? " v1" : ""), snapshot.Chart.SongName);
        Assert.Equal("9.4", snapshot.Chart.Version);
        Assert.Equal("test", RocksmithTestExport.Version);
    }
}
