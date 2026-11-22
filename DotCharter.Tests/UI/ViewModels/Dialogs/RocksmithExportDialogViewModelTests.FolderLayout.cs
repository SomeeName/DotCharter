using DotCharter.Services.Export.Audio;

namespace DotCharter.Tests.UI.ViewModels.Dialogs;

public sealed partial class RocksmithExportDialogViewModelTests
{
    [Fact]
    public async Task FolderLayout_Saved_project_defaults_to_own_export_but_browse_and_draft_remember_choices()
    {
        var previous = Directory.CreateDirectory(Path.Combine(_tempDir, "previous")).FullName;
        var current = Directory.CreateDirectory(Path.Combine(_tempDir, "current")).FullName;
        var harness = new Harness(CreateValidProject());
        harness.Config.AppConfig.LastExportDir = previous;
        harness.State.CurrentProjectPath = Path.Combine(current, "project.dcproj");
        var vm = harness.Open();
        Assert.Equal(Path.Combine(current, "export"), Path.GetDirectoryName(vm.OutputPath));

        var suggested = vm.OutputPath;
        await vm.BrowseOutputCommand.ExecuteAsync(null);
        Assert.Equal(previous, Assert.Single(harness.Storage.RequestedStartDirectories));
        Assert.Equal(suggested, vm.OutputPath);

        var picked = Path.Combine(previous, "chosen_p.psarc");
        harness.Storage.NextSaveFile = picked;
        await vm.BrowseOutputCommand.ExecuteAsync(null);
        vm.CloseCommand.Execute(null);
        Assert.Equal(picked, harness.Open().OutputPath);
        harness.Vm.CloseCommand.Execute(null);

        var next = CreateValidProject();
        harness.State.ActiveProject = next;
        harness.State.ActiveArrangement = next.Arrangements[0];
        harness.State.CurrentProjectPath = Path.Combine(_tempDir, "next.dcproj");
        using var nextVm = harness.Open();
        Assert.Equal(Path.Combine(_tempDir, "export"), Path.GetDirectoryName(nextVm.OutputPath));
    }

    [Fact]
    public void FolderLayout_Unsaved_project_uses_last_export_folder()
    {
        var previous = Directory.CreateDirectory(Path.Combine(_tempDir, "previous")).FullName;
        var harness = new Harness(CreateValidProject());
        harness.Config.AppConfig.LastExportDir = previous;
        using var vm = harness.Open();
        Assert.Equal(previous, Path.GetDirectoryName(vm.OutputPath));
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void FolderLayout_Main_audio_uses_working_file_and_never_named_siblings(bool workingExists)
    {
        var working = Path.Combine(_tempDir, "song.flac");
        if (workingExists) File.WriteAllText(working, "working audio");
        File.WriteAllText(Path.ChangeExtension(working, ".wem"), "stale encoded audio");
        File.WriteAllText(Path.ChangeExtension(working, ".wav"), "stale wave audio");
        var harness = new Harness(CreateValidProject())
        {
            VolumeDecoder = _ => new PcmAudio(new short[4800], 1, 48000),
            VolumeSignature = _ => "synthetic"
        };
        harness.State.CurrentProjectPath = Path.Combine(_tempDir, "project.dcproj");
        harness.State.CurrentAudioFileName = working;
        using var vm = harness.Open();
        Assert.Equal(workingExists ? working : "", vm.AudioPath);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void FolderLayout_Default_package_uses_release_name_and_follows_versions(bool extended)
    {
        using var keys = TestRocksmithKeys.UseNoKeys();
        var project = CreateValidProject();
        if (extended)
            project.Arrangements.Add(new Arrangement { Name = "Seven", ArrangementType = ArrangementType.Lead, StringCount = 7 });
        var harness = new Harness(project);
        harness.State.CurrentProjectPath = Path.Combine(_tempDir, "project.dcproj");
        using var vm = harness.Open();
        var expected = "DotCharter_Dialog-Song_v1"
            + (extended ? "_rsxr_p.psarc" : "_p.psarc");
        Assert.Equal(expected, Path.GetFileName(vm.OutputPath));
        vm.Version = "2.1";
        Assert.Equal("DotCharter_Dialog-Song_v2_1" + (extended ? "_rsxr_p.psarc" : "_p.psarc"), Path.GetFileName(vm.OutputPath));
        vm.OutputPath = Path.Combine(_tempDir, "chosen_p.psarc");
        var chosen = vm.OutputPath;
        vm.Version = "3";
        Assert.Equal(chosen, vm.OutputPath);
    }
}

