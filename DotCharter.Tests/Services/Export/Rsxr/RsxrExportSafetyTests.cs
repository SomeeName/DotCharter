using CommunityToolkit.Mvvm.Input;
using DotCharter.Services.Export;
using DotCharter.Services.Export.DlcBuilder;
using DotCharter.Services.Export.Rocksmith;
using DotCharter.Services.Infrastructure.Documents;
using DotCharter.Services.Parsing;
using DotCharter.Services.Parsing.Xml;
using DotCharter.Services.Parsing.Xml.Song;
using DotCharter.Tone.Infrastructure.Storage;
using Microsoft.Extensions.DependencyInjection;
using ExportHarness = DotCharter.Tests.UI.ViewModels.Dialogs.RocksmithExportDialogViewModelTests.Harness;

namespace DotCharter.Tests.Services.Export.Rsxr;

public sealed class RsxrExportSafetyTests : IDisposable
{
    private readonly string _directory = Directory.CreateTempSubdirectory("dotcharter-export-safety-").FullName;

    public void Dispose() => BestEffortDelete.Directory(_directory);

    [Theory]
    [InlineData("chosen.psarc", true, "chosen_rsxr_p.psarc")]
    [InlineData("chosen.PSARC", true, "chosen_rsxr_p.psarc")]
    [InlineData("chosen.psarc", false, "chosen.psarc")]
    [InlineData("chosen_m.psarc", true, "chosen_rsxr_p.psarc")]
    [InlineData("chosen_m.psarc", false, "chosen_m.psarc")]
    [InlineData("chosen.psarc.building", true, "chosen.psarc.building")]
    [InlineData("chosen", true, "chosen")]
    public void Bare_archive_names_get_the_native_pc_suffix_only_when_tagged(string input, bool tagged, string expected) =>
        Assert.Equal(Path.Combine(_directory, expected), ExtendedRangePackageName.Apply(Path.Combine(_directory, input), tagged));

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Stock_choices_remove_the_tag_through_rows_picker_draft_and_exports(bool plain)
    {
        using var keys = TestRocksmithKeys.UseSynthetic();
        var harness = Harness();
        harness.State.ActiveProject!.Arrangements[0].StringCount = 6;
        using var vm = harness.Open();
        vm.Target.SelectedPackageOption = vm.Target.PackageOptions.Single(p => p.Plain == plain);
        vm.Target.SelectedPlatformOption = vm.Target.PlatformOptions.Single(p => p.Value == ExportPlatform.Pc);
        var authored = Path.Combine(_directory, "chosen_rsxr_p.psarc");
        var expected = Path.Combine(_directory, "chosen_p.psarc");
        vm.OutputPath = authored;
        vm.Arrangements[0].IsSelected = false;
        vm.Arrangements[0].IsSelected = true;
        Assert.Equal(expected, vm.OutputPath);
        harness.Storage.NextSaveFile = authored;
        await vm.BrowseOutputCommand.ExecuteAsync(null);
        Assert.Equal(expected, vm.OutputPath);
        SetAudio(vm);
        await vm.ExportCommand.ExecuteAsync(null);
        Assert.True(vm.HasResult);
        Assert.Equal(expected, vm.OutputPath);
        Assert.Equal(expected, vm.ResultPath);
        vm.TestFolder = Path.Combine(_directory, "test");
        await ((IAsyncRelayCommand)vm.TestExportCommand).ExecuteAsync(null);
        Assert.Equal(expected, vm.OutputPath);
        vm.TryCancel();
        using var reopened = harness.Open();
        Assert.Equal(expected, reopened.OutputPath);
    }

    [Fact]
    public void Stock_choices_remove_the_tag_after_clearing_extended_selection()
    {
        using var keys = TestRocksmithKeys.UseSynthetic();
        using var vm = Harness().Open();
        vm.Target.SelectedPlatformOption = vm.Target.PlatformOptions.Single(p => p.Value == ExportPlatform.Pc);
        vm.OutputPath = Path.Combine(_directory, "chosen_rsxr_p.psarc");
        vm.Arrangements[0].IsSelected = false;
        Assert.Equal(Path.Combine(_directory, "chosen_p.psarc"), vm.OutputPath);
        vm.OutputPath = Path.Combine(_directory, "authored_rsxr_p.psarc");
        vm.Target.SelectedPackageOption = vm.Target.PackageOptions.Single(p => p.Plain);
        vm.Target.SelectedPackageOption = vm.Target.PackageOptions.Single(p => !p.Plain);
        Assert.Equal(Path.Combine(_directory, "authored_p.psarc"), vm.OutputPath);
    }

    [Fact]
    public async Task Native_export_of_a_bare_archive_name_agrees_in_field_result_and_file()
    {
        using var keys = TestRocksmithKeys.UseNoKeys();
        using var vm = Harness().Open();
        vm.Target.SelectedPlatformOption = vm.Target.PlatformOptions.Single(p => p.Value == ExportPlatform.Pc);
        vm.OutputPath = Path.Combine(_directory, "chosen.psarc");
        SetAudio(vm);

        await vm.ExportCommand.ExecuteAsync(null);

        var expected = Path.Combine(_directory, "chosen_rsxr_p.psarc");
        Assert.True(vm.HasResult);
        Assert.Equal(expected, vm.OutputPath);
        Assert.Equal(expected, vm.ResultPath);
        Assert.Equal(RsxrTestSupport.PackageMagic(true), File.ReadAllBytes(expected)[..4]);
        Assert.False(File.Exists(Path.Combine(_directory, "chosen.psarc")));
    }

    [Theory]
    [InlineData(false, 7)]
    [InlineData(false, 8)]
    [InlineData(true, 5)]
    [InlineData(true, 6)]
    public void Refused_XML_is_left_out_and_an_accepted_arrangement_keeps_its_marker(bool bass, int strings)
    {
        var built = RsxrTestSupport.Chart(bass, strings);
        var arrangement = built.Project.Arrangements[0];
        arrangement.TuningPitches[0] = new TuningOffset(25);
        var warnings = new List<string>();
        var writer = new RsXmlSongWriter { Warning = warnings.Add };
        Assert.Null(writer.TryWrite(built.Project));
        Assert.Equal(RsxrStringLayout.ExportRefusal(arrangement), Assert.Single(warnings));
        arrangement.TuningPitches[0] = new TuningOffset(0);
        var accepted = writer.Write(built.Project);
        Assert.Equal("1", accepted.Root!.Element("tuning")!.Attribute("extendedRange")?.Value);
        Assert.Single(warnings);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void Saving_XML_reports_the_native_refusal_even_without_a_serializer_logger(bool bundle)
    {
        var config = new FakeConfigurationService
        {
            AppConfig = new AppConfig
            {
                ExportApplyDlcBuilderFixes = false, ExportGenerateDynamicDifficulty = false
            }
        };
        using var provider = new ServiceCollection().AddSingleton<IConfigurationService>(config)
            .AddKeyedTransient<IChartSerializer, XmlChartSerializer>("xml").BuildServiceProvider();
        var logger = new FakeAppLogger();
        var documents = new ProjectDocumentService(provider, logger);
        var built = RsxrTestSupport.Chart(false, 7);
        built.Project.Arrangements[0].TuningPitches[0] = new TuningOffset(25);
        var sentence = RsxrStringLayout.ExportRefusal(built.Project.Arrangements[0])!;
        var projectPath = Path.Combine(_directory, "song.dcproj");
        var xmlPath = Path.Combine(_directory, "song.xml");
        if (bundle) documents.SaveDlcBuilderXmlBundle(projectPath, built.Project);
        else Assert.Equal(sentence, Assert.Throws<InvalidDataException>(() => documents.SaveChart(xmlPath, built.Project)).Message);
        Assert.True(logger.HasEntryContaining("WARN", sentence));
        if (bundle) Assert.Empty(Directory.GetFiles(DlcBuilderBundle.DirectoryFor(projectPath), "*.xml"));
        else Assert.False(File.Exists(xmlPath));
    }

    private static ExportHarness Harness()
    {
        var project = DotCharter.Tests.UI.ViewModels.Dialogs.RocksmithExportDialogViewModelTests.CreateValidProject();
        project.Arrangements[0].StringCount = 7;
        project.Arrangements[0].TuningPitches.Add(new TuningOffset(0));
        var harness = new ExportHarness(project);
        harness.Config.AppConfig.ExportApplyDlcBuilderFixes = false;
        harness.Config.AppConfig.ExportGenerateDynamicDifficulty = false;
        return harness;
    }

    private void SetAudio(DotCharter.Presentation.Screens.Dialogs.RocksmithExportViewModel vm)
    {
        vm.AudioPath = Path.Combine(_directory, "audio.wem");
        File.WriteAllBytes(vm.AudioPath, new byte[4000]);
        vm.PreviewAudioPath = vm.AudioPath;
    }
}
