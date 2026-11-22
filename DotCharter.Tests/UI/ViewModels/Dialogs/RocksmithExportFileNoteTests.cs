using DotCharter.Presentation.Screens.Dialogs;
using DotCharter.Services.Export;
using DotCharter.Tone.Infrastructure.Storage;

namespace DotCharter.Tests.UI.ViewModels.Dialogs;

/// <summary>#903: the line under Platform lists every file the export writes: extended first, then the standard package.</summary>
public sealed class RocksmithExportFileNoteTests : IDisposable
{
    private readonly string _directory = Directory.CreateTempSubdirectory("dotcharter-file-note-").FullName;

    public void Dispose() => BestEffortDelete.Directory(_directory);

    private static string Note(string key, params string[] names) =>
        FakeLocalization.English().Format(key, [.. names]);

    private static ChartProject Mixed()
    {
        var project = RocksmithExportDialogViewModelTests.CreateValidProject();
        project.Arrangements.Add(new Arrangement { ArrangementType = ArrangementType.Lead, Name = "Seven", StringCount = 7 });
        return project;
    }

    private RocksmithExportViewModel Open(ChartProject project, ExportPlatform platform)
    {
        var vm = new RocksmithExportDialogViewModelTests.Harness(project).Open();
        vm.Target.SelectedPlatformOption = vm.Target.PlatformOptions.First(o => o.Value == platform);
        vm.OutputPath = Path.Combine(_directory, platform == ExportPlatform.Mac ? "song_m.psarc" : "song_p.psarc");
        return vm;
    }

    [Theory]
    [InlineData(ExportPlatform.Pc, true, "Dialog_Export_WritesBoth", "song_rsxr_p.psarc", "song_p.psarc")]
    [InlineData(ExportPlatform.Mac, true, "Dialog_Export_WritesBoth", "song_rsxr_m.psarc", "song_m.psarc")]
    [InlineData(ExportPlatform.PcAndMac, true, "Dialog_Export_WritesFour", "song_rsxr_p.psarc", "song_rsxr_m.psarc", "song_p.psarc", "song_m.psarc")]
    [InlineData(ExportPlatform.Pc, false, "", "")]
    [InlineData(ExportPlatform.Mac, false, "", "")]
    [InlineData(ExportPlatform.PcAndMac, false, "Dialog_Export_WritesBoth", "song_rsxr_p.psarc", "song_rsxr_m.psarc")]
    public void The_note_lists_every_file_and_follows_the_checkbox(ExportPlatform platform, bool standard, string key, params string[] names)
    {
        using var keys = TestRocksmithKeys.UseSynthetic();
        using var vm = Open(Mixed(), platform);
        Assert.True(vm.OffersStandardPackage);
        var changed = new List<string?>();
        vm.PropertyChanged += (_, e) => changed.Add(e.PropertyName);

        vm.WriteStandardPackage = !standard;
        vm.WriteStandardPackage = standard;

        Assert.Contains(nameof(vm.PlatformNote), changed);
        Assert.Contains(nameof(vm.HasPlatformNote), changed);
        Assert.Equal(key.Length == 0 ? "" : Note(key, names), vm.PlatformNote);
        Assert.Equal(key.Length > 0, vm.HasPlatformNote);
    }

    [Fact]
    public void Toggling_the_checkbox_changes_the_note()
    {
        using var keys = TestRocksmithKeys.UseSynthetic();
        using var vm = Open(Mixed(), ExportPlatform.PcAndMac);
        vm.WriteStandardPackage = true;
        var withStandard = vm.PlatformNote;
        vm.WriteStandardPackage = false;
        Assert.NotEqual(withStandard, vm.PlatformNote);
    }

    [Fact]
    public void A_project_without_an_extended_arrangement_names_only_the_standard_packages()
    {
        using var keys = TestRocksmithKeys.UseSynthetic();
        using var vm = Open(RocksmithExportDialogViewModelTests.CreateValidProject(), ExportPlatform.PcAndMac);
        Assert.False(vm.OffersStandardPackage);
        Assert.Equal(Note("Dialog_Export_WritesBoth", "song_p.psarc", "song_m.psarc"), vm.PlatformNote);
    }
}
