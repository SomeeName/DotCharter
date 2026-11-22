using CommunityToolkit.Mvvm.Input;
using DotCharter.Services.Export.Rocksmith;

namespace DotCharter.Tests.UI.ViewModels.Dialogs;

public sealed partial class RocksmithExportDialogViewModelTests
{
    [Fact]
    public async Task A_typed_test_folder_is_retained_for_the_same_project_only()
    {
        using var keys = TestRocksmithKeys.UseNoKeys();
        var harness = new Harness(CreateValidProject());
        using var first = harness.Open();
        var folder = Path.Combine(_tempDir, "project-only");
        first.TestFolder = folder;
        first.AudioPath = CreateFakeWem();
        await ((IAsyncRelayCommand)first.TestExportCommand).ExecuteAsync(null);
        Assert.True(first.HasResult);
        first.CloseCommand.Execute(null);

        using (var same = harness.Open()) Assert.Equal(folder, same.TestFolder);

        harness.State.ActiveProject = CreateValidProject();
        using var other = harness.Open();
        Assert.Empty(other.TestFolder);
    }

    [Fact]
    public async Task A_blank_test_folder_exports_beside_the_release_output()
    {
        using var keys = TestRocksmithKeys.UseNoKeys();
        var harness = new Harness(CreateValidProject());
        using var vm = harness.Open();
        Assert.Empty(vm.TestFolder);
        vm.OutputPath = Path.Combine(_tempDir, "release_p.psarc");
        vm.AudioPath = CreateFakeWem();

        await ((IAsyncRelayCommand)vm.TestExportCommand).ExecuteAsync(null);

        Assert.Equal(_tempDir, Path.GetDirectoryName(FirstResult(vm)));
        Assert.Equal(_tempDir, vm.TestFolderWatermark);
    }

    [Fact]
    public async Task The_settings_default_is_used_by_multiple_projects()
    {
        using var keys = TestRocksmithKeys.UseNoKeys();
        var harness = new Harness(CreateValidProject());
        var folder = Path.Combine(_tempDir, "shared-tests");
        var settings = System.Text.Json.JsonSerializer.Serialize(new { ExportTestDefaultFolder = folder });
        harness.Config.AppConfig = System.Text.Json.JsonSerializer.Deserialize<AppConfig>(settings)!;
        using (var vm = harness.Open())
        {
            Assert.Equal(folder, vm.TestFolder);
            vm.OutputPath = Path.Combine(_tempDir, "release_p.psarc");
            vm.AudioPath = CreateFakeWem();
            await ((IAsyncRelayCommand)vm.TestExportCommand).ExecuteAsync(null);
            Assert.Equal(folder, Path.GetDirectoryName(FirstResult(vm)));
            vm.TestFolder = Path.Combine(_tempDir, "project-only");
            await ((IAsyncRelayCommand)vm.TestExportCommand).ExecuteAsync(null);
            Assert.Equal(vm.TestFolder, Path.GetDirectoryName(FirstResult(vm)));
        }

        harness.State.ActiveProject = CreateValidProject();
        using var other = harness.Open();
        Assert.Equal(folder, other.TestFolder);
    }

    [Fact]
    public async Task Test_cleanup_preserves_releases_and_other_projects_tests_in_the_shared_folder()
    {
        using var keys = TestRocksmithKeys.UseNoKeys();
        var harness = new Harness(CreateValidProject());
        harness.State.CurrentProjectPath = Path.Combine(_tempDir, "project.dcproj");
        File.WriteAllText(harness.State.CurrentProjectPath, "project");
        harness.TestOwnershipFactory = (path, id, folder) => new RocksmithTestPackageOwnership(path, id, folder, RecycleOwnedTestPackage);
        using var vm = harness.Open();
        vm.OutputPath = Path.Combine(_tempDir, "SharedRelease_v1_p.psarc");
        vm.TestFolder = _tempDir;
        vm.AudioPath = CreateFakeWem();
        vm.DlcKey = "SharedRelease";
        var files = new[] { "SharedRelease_v1_p.psarc", "SharedRelease_m.psarc", "Other_Song_test_p.psarc",
            "DotCharter_Dialog-Song_test_v2_m.psarc", "DotCharter_Dialog-Song_test_p.chords-ext.json" };
        foreach (var name in files) File.WriteAllText(Path.Combine(_tempDir, name), name);

        await ((IAsyncRelayCommand)vm.TestExportCommand).ExecuteAsync(null);

        Assert.True(vm.HasResult);
        Assert.All(files, name => Assert.Equal(name, File.ReadAllText(Path.Combine(_tempDir, name))));
    }
}
