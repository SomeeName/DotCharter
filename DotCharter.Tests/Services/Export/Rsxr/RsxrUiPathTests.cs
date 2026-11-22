using System.Text.Json;
using System.Xml.Linq;
using CommunityToolkit.Mvvm.Input;
using DotCharter.Presentation.Contracts.Dialogs;
using DotCharter.Presentation.Screens.Dialogs;
using DotCharter.Services.Editing;
using DotCharter.Services.Editing.Collisions;
using DotCharter.Services.Editing.Highway;
using DotCharter.Services.Editing.Selection;
using DotCharter.Services.Editing.Targets;
using DotCharter.Services.Export;
using DotCharter.Services.Export.Rocksmith;
using DotCharter.Services.Infrastructure.Documents;
using DotCharter.Services.Infrastructure.Recovery;
using DotCharter.Services.Parsing;
using DotCharter.Services.Parsing.Rscp;
using DotCharter.Services.Session;
using DotCharter.Services.Audio.Dsp.Tones;
using DotCharter.Tone.Infrastructure.Storage;
using Microsoft.Extensions.DependencyInjection;
using ExportHarness = DotCharter.Tests.UI.ViewModels.Dialogs.RocksmithExportDialogViewModelTests.Harness;

namespace DotCharter.Tests.Services.Export.Rsxr;

public sealed class RsxrUiPathTests : IDisposable
{
    private readonly string _directory = Directory.CreateTempSubdirectory("dotcharter-ui-path-").FullName;

    public void Dispose() => BestEffortDelete.Directory(_directory);

    [Fact]
    public async Task Mac_only_typed_path_agrees_with_the_field_platform_path_and_written_package()
    {
        using var keys = TestRocksmithKeys.UseSynthetic();
        var project = DotCharter.Tests.UI.ViewModels.Dialogs.RocksmithExportDialogViewModelTests.CreateValidProject();
        var harness = new ExportHarness(project);
        harness.Config.AppConfig.ExportApplyDlcBuilderFixes = false;
        harness.Config.AppConfig.ExportGenerateDynamicDifficulty = false;
        using var vm = harness.Open();
        vm.IncludeVocals = false;
        vm.Target.SelectedPackageOption = vm.Target.PackageOptions.Single(p => !p.Plain);
        vm.Target.SelectedPlatformOption = vm.Target.PlatformOptions.Single(p => p.Value == ExportPlatform.Mac);
        var typed = Path.Combine(_directory, "typed_rsxr_m.psarc");
        vm.OutputPath = typed;
        vm.AudioPath = Path.Combine(_directory, "audio.wem");
        File.WriteAllBytes(vm.AudioPath, new byte[4000]);
        vm.PreviewAudioPath = vm.AudioPath;

        Assert.Equal(typed, vm.OutputPath);
        var options = vm.BuildOptions(project);
        Assert.Equal(typed, RocksmithPlatformTargets.OutputPathFor(options.OutputPath, options.Platform, true));
        Assert.Empty(vm.PlatformNote);
        Assert.False(vm.HasPlatformNote);
        Assert.True(vm.ExportCommand.CanExecute(null), vm.ExportBlockedReason);

        await vm.ExportCommand.ExecuteAsync(null);

        Assert.True(vm.HasResult, string.Join("\n", harness.Logger.Snapshot().Select(e => e.Message)));
        Assert.Equal(typed, vm.OutputPath);
        Assert.Equal(typed, vm.ResultPath);
        Assert.Equal(typed, Assert.Single(Directory.GetFiles(_directory, "*.psarc")));
        Assert.Equal(RsxrTestSupport.PackageMagic(false), File.ReadAllBytes(typed)[..4]);
        Assert.Empty(vm.PlatformNote);
    }

    [Fact]
    public async Task Refused_tuning_allows_dialog_export_and_reports_the_skipped_arrangement()
    {
        using var keys = TestRocksmithKeys.UseNoKeys();
        var project = DotCharter.Tests.UI.ViewModels.Dialogs.RocksmithExportDialogViewModelTests.CreateValidProject();
        var refused = ChartProjectCloner.CloneArrangement(project.Arrangements[0]);
        refused.Name = "Refused";
        refused.StringCount = 7;
        refused.TuningPitches.Add(new TuningOffset(0));
        refused.TuningPitches[0] = new TuningOffset(25);
        project.Arrangements.Add(refused);
        var sentence = RsxrStringLayout.ExportRefusal(refused)!;
        var harness = new ExportHarness(project);
        harness.Config.AppConfig.ExportApplyDlcBuilderFixes = false;
        harness.Config.AppConfig.ExportGenerateDynamicDifficulty = false;
        using var vm = harness.Open();
        vm.IncludeVocals = false;
        vm.Target.SelectedPlatformOption = vm.Target.PlatformOptions.Single(p => p.Value == ExportPlatform.Pc);
        vm.OutputPath = Path.Combine(_directory, "accepted_p.psarc");
        vm.AudioPath = Path.Combine(_directory, "audio.wem");
        File.WriteAllBytes(vm.AudioPath, new byte[4000]);
        vm.PreviewAudioPath = vm.AudioPath;

        Assert.True(vm.ExportCommand.CanExecute(null), vm.ExportBlockedReason);
        await vm.ExportCommand.ExecuteAsync(null);

        Assert.True(vm.HasResult);
        Assert.Equal(vm.OutputPath, vm.ResultPath);
        Assert.Equal(RsxrTestSupport.PackageMagic(false), File.ReadAllBytes(vm.ResultPath)[..4]);
        Assert.Single(RsxrTestSupport.Entries(vm.ResultPath), e => e.Key.EndsWith(".sng", StringComparison.Ordinal));
        Assert.Contains($"Arrangement '{refused.Name}' was skipped: {sentence}", vm.ResultWarnings);
    }

    [Theory]
    [InlineData(ArrangementType.Bass, 5, false)]
    [InlineData(ArrangementType.Bass, 6, false)]
    [InlineData(ArrangementType.Lead, 7, false)]
    [InlineData(ArrangementType.Lead, 8, false)]
    [InlineData(ArrangementType.Bass, 4, false)]
    [InlineData(ArrangementType.Lead, 6, false)]
    [InlineData(ArrangementType.Bass, 5, true)]
    public async Task Arrangement_dialog_to_editing_to_export_and_saved_XML(ArrangementType type, int strings, bool mixed)
    {
        using var keys = TestRocksmithKeys.UseNoKeys();
        var config = new FakeConfigurationService
        {
            AppConfig = new AppConfig { ExportApplyDlcBuilderFixes = false, ExportGenerateDynamicDifficulty = false }
        };
        using var provider = new ServiceCollection().AddSingleton<IConfigurationService>(config)
            .AddKeyedTransient<IChartSerializer, RscpChartSerializer>("rscp").BuildServiceProvider();
        var documents = new ProjectDocumentService(provider, new FakeAppLogger());
        var project = documents.CreateNewProject("Path Song", "Synthetic", "audio.wem");
        new TempoService(new FakeAppLogger()).GenerateInitialBeats(project.Beats, 32_000, 120);
        var harness = new ExportHarness(project) { ExportDocuments = documents };
        harness.Config.AppConfig = config.AppConfig;
        harness.State.CurrentProjectPath = Path.Combine(_directory, "song.dcproj");
        harness.State.CurrentAudioFileName = "audio.wem";
        harness.Config.AppConfig.ExportApplyDlcBuilderFixes = false;
        harness.Config.AppConfig.ExportGenerateDynamicDifficulty = false;
        PlaceSounds(harness, 6, false);

        var settings = new ArrangementSettingsViewModel(harness.Edit, new DotCharter.Tests.Infrastructure.FakeReferencePitchService());
        settings.Configure(new ArrangementSettingsRequest(CreateNew: true));
        settings.Name = "Playable";
        settings.Type = type;
        settings.Strings = strings;
        settings.SaveCommand.Execute(null);
        Assert.True(await settings.Result);
        Assert.Equal(strings, harness.State.ActiveArrangement!.StringCount);
        Assert.Equal(type, harness.State.ActiveArrangement.ArrangementType);
        PlaceSounds(harness, strings, type == ArrangementType.Bass);

        using var vm = harness.Open();
        vm.Target.SelectedPlatformOption = vm.Target.PlatformOptions.Single(p => p.Value == ExportPlatform.Pc);
        vm.Arrangements[0].IsSelected = mixed;
        vm.OutputPath = Path.Combine(_directory, "song_p.psarc");
        vm.AudioPath = Path.Combine(_directory, "audio.wem");
        File.WriteAllBytes(vm.AudioPath, new byte[4000]);
        vm.PreviewAudioPath = vm.AudioPath;
        var extended = type == ArrangementType.Bass ? strings > 4 : strings > 6;
        var expected = Path.Combine(_directory, extended ? "song_rsxr_p.psarc" : "song_p.psarc");
        Assert.Equal(expected, vm.OutputPath);
        Assert.True(vm.ExportCommand.CanExecute(null), vm.ExportBlockedReason);

        await vm.ExportCommand.ExecuteAsync(null);

        Assert.True(vm.HasResult, string.Join("\n", harness.Logger.Snapshot().Select(e => e.Message)));
        Assert.Equal(expected, vm.ResultPath);
        Assert.True(File.Exists(expected));
        Assert.Equal(RsxrTestSupport.PackageMagic(extended), File.ReadAllBytes(expected)[..4]);
        if (extended) Assert.False(File.Exists(Path.Combine(_directory, "song_p.psarc")));
        AssertManifests(expected, extended, type);
        await SaveProject(harness, documents);
        Assert.True(File.Exists(harness.State.CurrentProjectPath));
        var xmls = Directory.GetFiles(Path.Combine(_directory, "export", "DTRS XML"), "*.xml")
            .Select(XDocument.Load).Where(d => d.Root?.Name == "song").ToArray();
        Assert.Contains(xmls, xml => xml.Root!.Element("tuning")?.Attribute("extendedRange")?.Value == (extended ? "1" : null));
        if (extended)
        {
            var xml = Assert.Single(xmls, d => d.Root!.Element("tuning")?.Attribute("extendedRange") != null);
            Assert.Equal(strings, (int)xml.Root!.Element("tuning")!.Attribute("strings")!);
            Assert.Equal(Enumerable.Range(0, strings), xml.Descendants("note").Select(n => (int)n.Attribute("string")!));
        }
    }

    private static async Task SaveProject(ExportHarness harness, IProjectDocumentService documents)
    {
        var recovery = DotCharter.Tests.Presentation.Dialogs.RecordingProxy.Create<ICrashRecoveryService>().Proxy;
        var companions = DotCharter.Tests.Presentation.Dialogs.RecordingProxy.Create<ISaveCompanionService>().Proxy;
        using var session = new ProjectSessionService(new FakeAudioPlaybackService(), harness.Config, harness.Logger,
            harness.Storage, documents, harness.State, harness.Io.Transport, harness.Edit.Editor,
            new TempoService(harness.Logger), recovery, harness.History, companions, new TestUiDispatcher(),
            FakeLocalization.English(), new FakeProjectPathRekey(),
            songToneEdits: new SongToneEditStore(Path.Combine(Path.GetDirectoryName(harness.State.CurrentProjectPath)!, "tone-edits")));
        Assert.True(await session.Saver.SaveProjectCoreAsync(harness.State.CurrentProjectPath));
        await session.Saver.FlushCompanionsAsync();
    }

    private static void PlaceSounds(ExportHarness harness, int strings, bool bass)
    {
        var state = harness.State;
        var scope = new EditingScope(state);
        var selection = new SelectionService();
        using var editing = new HighwayEditingService(state, selection, harness.History, new StrictCollisionService(),
            harness.Config, scope, new EditTargetService(selection, scope, harness.Config), harness.Logger);
        state.PreviewNoteFret = 3;
        for (var i = 0; i < strings; i++) editing.ApplyPlacement(new FractionalPosition(4 + i * 2), i);
        int[] chordStrings = bass && strings == 6 ? [0, 5] : [0, 1];
        foreach (var i in chordStrings) editing.ApplyPlacement(new FractionalPosition(24), i);
        Assert.Equal(strings, state.ActiveArrangement!.Levels[0].Notes.Count);
        Assert.Equal(chordStrings, Assert.Single(state.ActiveArrangement.Levels[0].Chords).ChordNotes.Select(n => n.StringIndex));
    }

    private static void AssertManifests(string path, bool extended, ArrangementType type)
    {
        foreach (var entry in RsxrTestSupport.Entries(path).Where(e => e.Key.EndsWith(".json", StringComparison.Ordinal)
                     || e.Key.EndsWith(".hsan", StringComparison.Ordinal)))
        {
            using var manifest = JsonDocument.Parse(entry.Value);
            var attributes = manifest.RootElement.GetProperty("Entries").EnumerateObject()
                .Select(e => e.Value.GetProperty("Attributes")).ToArray();
            foreach (var attribute in attributes)
            {
                var extendedEntry = extended && (type != ArrangementType.Bass || attribute.GetProperty("ArrangementName").GetString() == "Bass");
                Assert.Equal(extendedEntry, attribute.TryGetProperty("ExtendedRange", out _));
            }
        }
    }

    [Fact]
    public async Task Choice_selection_draft_version_picker_and_test_export_share_the_name()
    {
        using var keys = TestRocksmithKeys.UseSynthetic();
        var chart = DotCharter.Tests.UI.ViewModels.Dialogs.RocksmithExportDialogViewModelTests.CreateValidProject();
        chart.Arrangements[0].StringCount = 7;
        chart.Arrangements[0].TuningPitches.Add(new TuningOffset(0));
        var harness = new ExportHarness(chart);
        harness.Config.AppConfig.ExportApplyDlcBuilderFixes = false;
        harness.Config.AppConfig.ExportGenerateDynamicDifficulty = false;
        using var vm = harness.Open();
        vm.Target.SelectedPlatformOption = vm.Target.PlatformOptions.Single(p => p.Value == ExportPlatform.Pc);
        var plainPath = ExtendedRangePackageName.Apply(vm.OutputPath, false);
        foreach (var choice in vm.Target.PackageOptions)
        {
            vm.Target.SelectedPackageOption = choice;
            var tagged = true;
            Assert.Equal(ExtendedRangePackageName.Apply(plainPath, tagged), vm.OutputPath);
            vm.Arrangements[0].IsSelected = false;
            Assert.Equal(plainPath, vm.OutputPath);
            vm.Arrangements[0].IsSelected = true;
            Assert.Equal(ExtendedRangePackageName.Apply(plainPath, tagged), vm.OutputPath);
        }

        vm.Target.SelectedPlatformOption = vm.Target.PlatformOptions.Single(p => p.Value == ExportPlatform.Mac);
        Assert.DoesNotContain("_rsxr", vm.OutputPath, StringComparison.Ordinal);
        Assert.EndsWith("_m.psarc", vm.OutputPath);
        vm.Target.SelectedPlatformOption = vm.Target.PlatformOptions.Single(p => p.Value == ExportPlatform.Pc);
        Assert.EndsWith("_rsxr_p.psarc", vm.OutputPath);
        vm.Version = "2.1";
        Assert.Equal(RocksmithPackageName.For(chart, vm.Version, extended: true), Path.GetFileName(vm.OutputPath));
        await vm.BrowseOutputCommand.ExecuteAsync(null);
        Assert.Equal(Path.GetFileName(vm.OutputPath), Assert.Single(harness.Storage.SuggestedFileNames));
        var derived = vm.OutputPath;
        vm.OutputPath = "";
        await vm.BrowseOutputCommand.ExecuteAsync(null);
        Assert.Equal("package_rsxr_p.psarc", harness.Storage.SuggestedFileNames[^1]);
        vm.OutputPath = derived;
        vm.TryCancel();
        using var reopened = harness.Open();
        Assert.Equal(vm.OutputPath, reopened.OutputPath);
        reopened.TestFolder = _directory;
        reopened.AudioPath = Path.Combine(_directory, "audio.wem");
        File.WriteAllBytes(reopened.AudioPath, new byte[4000]);
        reopened.PreviewAudioPath = reopened.AudioPath;
        var untagged = Path.Combine(_directory, RocksmithTestExport.PackageName(RocksmithDlcKey.Create(chart.Chart.Artist, chart.Chart.SongName)) + "_p.psarc");
        File.WriteAllText(untagged, "superseded");
        await ((IAsyncRelayCommand)reopened.TestExportCommand).ExecuteAsync(null);
        Assert.True(reopened.HasResult);
        Assert.EndsWith("_rsxr_p.psarc", reopened.ResultPath);
        Assert.Equal("superseded", File.ReadAllText(untagged));
        reopened.Arrangements[0].IsSelected = false;
        reopened.TryCancel();
        using var stock = harness.Open();
        Assert.Equal(RocksmithPackageName.For(chart, stock.Version), Path.GetFileName(stock.OutputPath));
    }
}
