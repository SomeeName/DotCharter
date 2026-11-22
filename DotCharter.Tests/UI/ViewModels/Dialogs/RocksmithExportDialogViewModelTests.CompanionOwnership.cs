using CommunityToolkit.Mvvm.Input;
using DotCharter.Services.Export;
using DotCharter.Services.Export.Rocksmith;

namespace DotCharter.Tests.UI.ViewModels.Dialogs;

public sealed partial class RocksmithExportDialogViewModelTests
{
    [Fact]
    public async Task Release_export_to_the_companion_path_can_be_refreshed_on_save()
    {
        using var keys = TestRocksmithKeys.UseNoKeys();
        var project = CreateValidProject();
        var harness = new Harness(project);
        var projectPath = Path.Combine(_tempDir, "project.dcproj");
        File.WriteAllText(projectPath, "project");
        harness.State.CurrentProjectPath = projectPath;
        harness.Config.AppConfig.SaveCompanionPsarc = true;
        using var vm = harness.Open();
        vm.AudioPath = CreateFakeWem();
        project.AudioFileName = Path.GetFileName(vm.AudioPath);

        await vm.ExportCommand.ExecuteAsync(null);

        Assert.True(vm.HasResult);
        var path = FirstResult(vm);
        var original = File.ReadAllBytes(path);
        project.Arrangements[0].Levels[0].Notes[0].Fret = 4;
        var companion = new SaveCompanionService(harness.Config, harness.Edit.Logger);
        Assert.Equal(path, await companion.BuildPsarcAsync(projectPath, project, harness.State.TotalAudioLengthMs,
            TestContext.Current.CancellationToken));
        Assert.False(original.SequenceEqual(File.ReadAllBytes(path)));
    }

    [Fact]
    public async Task Test_export_preserves_unowned_legacy_packages_in_its_test_folder()
    {
        using var keys = TestRocksmithKeys.UseNoKeys();
        var harness = new Harness(CreateValidProject());
        var projectPath = Path.Combine(_tempDir, "project.dcproj");
        File.WriteAllText(projectPath, "project");
        harness.State.CurrentProjectPath = projectPath;
        harness.TestOwnershipFactory = (path, id, folder) => new RocksmithTestPackageOwnership(path, id, folder, RecycleOwnedTestPackage);
        using var vm = harness.Open();
        vm.TestFolder = Directory.CreateDirectory(Path.Combine(_tempDir, "test")).FullName;
        vm.AudioPath = CreateFakeWem();
        vm.DlcKey = "LegacyKey";
        var legacy = Path.Combine(vm.TestFolder, "legacykey_p.psarc");
        var unrelated = Path.Combine(vm.TestFolder, "legacykey2_p.psarc");
        var current = Path.Combine(vm.TestFolder, "DotCharter_Dialog-Song_test_v2_p.psarc");
        var outside = Path.Combine(_tempDir, "legacykey_p.psarc");
        foreach (var path in new[] { legacy, unrelated, current, outside }) File.WriteAllText(path, "previous package");

        await ((IAsyncRelayCommand)vm.TestExportCommand).ExecuteAsync(null);

        Assert.True(vm.HasResult);
        Assert.All(new[] { legacy, unrelated, current, outside }, path => Assert.Equal("previous package", File.ReadAllText(path)));
        Assert.False(Directory.Exists(Path.Combine(_tempDir, ".recycled-tests")));
    }
}
