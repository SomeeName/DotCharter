using CommunityToolkit.Mvvm.Input;
using DotCharter.Presentation.Screens.Dialogs;
using DotCharter.Services.Diagnostics;
using DotCharter.Services.Export;
using DotCharter.Services.Export.Rocksmith;
using DotCharter.Tests.Presentation.Dialogs;
using DotCharter.Tone.Infrastructure.Storage;
using ExportHarness = DotCharter.Tests.UI.ViewModels.Dialogs.RocksmithExportDialogViewModelTests.Harness;

namespace DotCharter.Tests.Services.Export.Rsxr;

public sealed class RsxrPickedOutputTests : IDisposable
{
    private readonly string _directory = Directory.CreateTempSubdirectory("dotcharter-picked-output-").FullName;

    public void Dispose() => BestEffortDelete.Directory(_directory);

    [Fact]
    public async Task A_stock_only_draft_removes_the_tag_in_the_field_and_written_package()
    {
        using var keys = TestRocksmithKeys.UseNoKeys();
        var project = DotCharter.Tests.UI.ViewModels.Dialogs.RocksmithExportDialogViewModelTests.CreateValidProject();
        var harness = Harness(project);
        var requested = Path.Combine(_directory, "draft_rsxr_p.psarc");
        var expected = Path.Combine(_directory, "draft_p.psarc");
        harness.Memory.ExportDraftProject = project;
        harness.Memory.ExportDraft = new ExportDraft
        {
            DlcKey = "", AppId = "248750", AudioPath = "", PreviewAudioPath = "", PreviewStartText = "",
            AlbumArtPath = "", OutputPath = requested, AudioVolume = -8, PreviewVolume = -7, IncludeVocals = false,
            Ticks = new Dictionary<string, bool>()
        };
        using var vm = harness.Open();
        SetAudio(vm);

        Assert.Equal(expected, vm.OutputPath);
        await vm.ExportCommand.ExecuteAsync(null);

        Assert.True(vm.HasResult);
        Assert.Equal(expected, vm.ResultPath);
        Assert.Equal(RsxrTestSupport.PackageMagic(false), File.ReadAllBytes(expected)[..4]);
        Assert.False(File.Exists(requested));
    }

    [Theory]
    [InlineData("picked", true, false, false)]
    [InlineData("picked", true, true, false)]
    [InlineData("picked", false, false, false)]
    [InlineData("picked", true, false, true)]
    [InlineData("picked", true, true, true)]
    [InlineData("default", true, false, false)]
    [InlineData("typed", true, false, false)]
    [InlineData("typedAfterPicker", true, false, false)]
    public async Task Export_confirms_only_an_existing_renamed_picker_path(
        string source, bool exists, bool accept, bool initiallyExtended)
    {
        using var keys = TestRocksmithKeys.UseNoKeys();
        var project = DotCharter.Tests.UI.ViewModels.Dialogs.RocksmithExportDialogViewModelTests.CreateValidProject();
        var extended = ChartProjectCloner.CloneArrangement(project.Arrangements[0]);
        extended.Name = "Extended";
        extended.StringCount = 7;
        extended.TuningPitches.Add(new TuningOffset(0));
        project.Arrangements.Add(extended);
        var harness = Harness(project);
        var prompt = new Prompt { Answer = accept };
        using var vm = Open(harness, prompt);
        var row = vm.Arrangements.Single(a => a.Name == extended.Name);
        row.IsSelected = initiallyExtended;
        if (source != "default")
            vm.OutputPath = Path.Combine(_directory, initiallyExtended ? "picked_rsxr_p.psarc" : "picked_p.psarc");
        if (source is "picked" or "typedAfterPicker")
        {
            harness.Storage.NextSaveFile = vm.OutputPath;
            await vm.BrowseOutputCommand.ExecuteAsync(null);
        }
        if (source == "typedAfterPicker") vm.OutputPath = Path.Combine(_directory, "typed_p.psarc");
        var confirmed = vm.OutputPath;
        File.WriteAllText(confirmed, "confirmed package");
        row.IsSelected = !initiallyExtended;
        var final = vm.OutputPath;
        Assert.NotEqual(confirmed, final);
        if (exists) File.WriteAllText(final, "destination package");
        SetAudio(vm);
        Assert.True(vm.ExportCommand.CanExecute(null), vm.ExportBlockedReason);

        await vm.ExportCommand.ExecuteAsync(null);

        var asks = source == "picked" && exists;
        Assert.Equal(asks ? 1 : 0, prompt.Calls.Count);
        Assert.Equal("confirmed package", File.ReadAllText(confirmed));
        if (asks)
        {
            var call = Assert.Single(prompt.Calls);
            Assert.Equal(MessageDialogKind.Warning, call.Kind);
            Assert.True(call.Confirmation);
            Assert.Equal(FakeLocalization.English().Format("Dialog_Export_OverwritePackage", final), call.Message);
        }
        if (asks && !accept)
        {
            Assert.False(vm.HasResult);
            Assert.False(vm.IsBuilding);
            Assert.False(vm.Result.IsCompleted);
            Assert.Equal("destination package", File.ReadAllText(final));
            Assert.Equal(2, Directory.GetFiles(_directory, "*.psarc").Length);
            return;
        }
        Assert.True(vm.HasResult, string.Join("\n", harness.Logger.Snapshot().Select(e => e.Message)));
        Assert.Equal(final, vm.ResultPath);
        Assert.Equal(RsxrTestSupport.PackageMagic(!initiallyExtended), File.ReadAllBytes(final)[..4]);
        await vm.ExportCommand.ExecuteAsync(null);
        Assert.Equal(asks ? 1 : 0, prompt.Calls.Count);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task A_confirmed_picker_path_exports_without_an_additional_question(bool test)
    {
        using var keys = TestRocksmithKeys.UseNoKeys();
        var project = DotCharter.Tests.UI.ViewModels.Dialogs.RocksmithExportDialogViewModelTests.CreateValidProject();
        var harness = Harness(project);
        var prompt = new Prompt();
        var extended = ChartProjectCloner.CloneArrangement(project.Arrangements[0]);
        extended.Name = "Extended";
        extended.StringCount = 7;
        extended.TuningPitches.Add(new TuningOffset(0));
        project.Arrangements.Add(extended);
        using var vm = Open(harness, prompt);
        var row = vm.Arrangements.Single(a => a.Name == extended.Name);
        row.IsSelected = false;
        vm.OutputPath = Path.Combine(_directory, "picked_p.psarc");
        harness.Storage.NextSaveFile = vm.OutputPath;
        await vm.BrowseOutputCommand.ExecuteAsync(null);
        File.WriteAllText(vm.OutputPath, "existing package");
        if (test)
        {
            row.IsSelected = true;
            File.WriteAllText(vm.OutputPath, "release package");
            vm.TestFolder = Path.Combine(_directory, "test");
            vm.DlcKey = "PickedTest";
            Directory.CreateDirectory(vm.TestFolder);
            File.WriteAllText(Path.Combine(vm.TestFolder, RocksmithTestExport.PackageName(vm.DlcKey) + "_rsxr_p.psarc"), "test package");
        }
        SetAudio(vm);

        await ((IAsyncRelayCommand)(test ? vm.TestExportCommand : vm.ExportCommand)).ExecuteAsync(null);

        Assert.Empty(prompt.Calls);
        Assert.True(vm.HasResult);
        Assert.Equal(RsxrTestSupport.PackageMagic(test), File.ReadAllBytes(vm.ResultPath)[..4]);
        if (test) Assert.Equal("release package", File.ReadAllText(vm.OutputPath));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task A_Mac_pick_confirms_the_PC_name_after_platform_and_row_changes(bool accept)
    {
        using var keys = TestRocksmithKeys.UseSynthetic();
        var project = DotCharter.Tests.UI.ViewModels.Dialogs.RocksmithExportDialogViewModelTests.CreateValidProject();
        var extended = ChartProjectCloner.CloneArrangement(project.Arrangements[0]);
        extended.Name = "Extended";
        extended.StringCount = 7;
        extended.TuningPitches.Add(new TuningOffset(0));
        project.Arrangements.Add(extended);
        var harness = Harness(project);
        var prompt = new Prompt { Answer = accept };
        using var vm = Open(harness, prompt);
        var row = vm.Arrangements.Single(a => a.Name == extended.Name);
        row.IsSelected = false;
        vm.Target.SelectedPlatformOption = vm.Target.PlatformOptions.Single(p => p.Value == ExportPlatform.Mac);
        var picked = Path.Combine(_directory, "song_m.psarc");
        harness.Storage.NextSaveFile = picked;
        await vm.BrowseOutputCommand.ExecuteAsync(null);
        Assert.Equal(picked, vm.OutputPath);
        File.WriteAllText(picked, "picked package");
        vm.Target.SelectedPlatformOption = vm.Target.PlatformOptions.Single(p => p.Value == ExportPlatform.Pc);
        Assert.Equal(Path.Combine(_directory, "song_p.psarc"), vm.OutputPath);
        row.IsSelected = true;
        var final = Path.Combine(_directory, "song_rsxr_p.psarc");
        Assert.Equal(final, vm.OutputPath);
        byte[] existing = [0, 255, 12, 34, 56, 78];
        File.WriteAllBytes(final, existing);
        SetAudio(vm);
        Assert.True(vm.ExportCommand.CanExecute(null), vm.ExportBlockedReason);

        await vm.ExportCommand.ExecuteAsync(null);

        if (!accept) Assert.Equal(existing, File.ReadAllBytes(final));
        Assert.Equal("picked package", File.ReadAllText(picked));
        var call = Assert.Single(prompt.Calls);
        Assert.True(call.Confirmation);
        Assert.Equal(MessageDialogKind.Warning, call.Kind);
        Assert.Equal(FakeLocalization.English().Format("Dialog_Export_OverwritePackage", final), call.Message);
        Assert.False(vm.IsBuilding);
        Assert.False(vm.Result.IsCompleted);
        Assert.Equal(accept, vm.HasResult);
        if (!accept)
        {
            Assert.Empty(vm.ResultPath);
            Assert.Equal(2, Directory.GetFiles(_directory, "*.psarc").Length);
            return;
        }
        Assert.Equal(final, vm.ResultPath);
        Assert.Equal(RsxrTestSupport.PackageMagic(true), File.ReadAllBytes(final)[..4]);
        await vm.ExportCommand.ExecuteAsync(null);
        Assert.Single(prompt.Calls);
    }

    [Fact]
    public async Task A_Mac_pick_exports_for_Mac_without_an_additional_question()
    {
        using var keys = TestRocksmithKeys.UseSynthetic();
        var project = DotCharter.Tests.UI.ViewModels.Dialogs.RocksmithExportDialogViewModelTests.CreateValidProject();
        var harness = Harness(project);
        var prompt = new Prompt();
        using var vm = Open(harness, prompt);
        vm.Target.SelectedPlatformOption = vm.Target.PlatformOptions.Single(p => p.Value == ExportPlatform.Mac);
        var picked = Path.Combine(_directory, "song_m.psarc");
        harness.Storage.NextSaveFile = picked;
        await vm.BrowseOutputCommand.ExecuteAsync(null);
        File.WriteAllText(picked, "existing package");
        SetAudio(vm);
        Assert.True(vm.ExportCommand.CanExecute(null), vm.ExportBlockedReason);

        await vm.ExportCommand.ExecuteAsync(null);

        Assert.Empty(prompt.Calls);
        Assert.True(vm.HasResult);
        Assert.Equal(picked, vm.ResultPath);
        Assert.Equal(RsxrTestSupport.PackageMagic(false), File.ReadAllBytes(picked)[..4]);
    }

    [Theory]
    [InlineData(false, false)]
    [InlineData(false, true)]
    [InlineData(true, false)]
    [InlineData(true, true)]
    public async Task A_Mac_pick_confirms_only_a_different_existing_PC_name_for_both_platforms(bool pickPcName, bool exists)
    {
        using var keys = TestRocksmithKeys.UseSynthetic();
        var project = DotCharter.Tests.UI.ViewModels.Dialogs.RocksmithExportDialogViewModelTests.CreateValidProject();
        var harness = Harness(project);
        var prompt = new Prompt { Answer = true };
        using var vm = Open(harness, prompt);
        vm.Target.SelectedPlatformOption = vm.Target.PlatformOptions.Single(p => p.Value == ExportPlatform.Mac);
        var final = Path.Combine(_directory, "song_p.psarc");
        harness.Storage.NextSaveFile = Path.Combine(_directory, pickPcName ? "song_p.psarc" : "song_m.psarc");
        await vm.BrowseOutputCommand.ExecuteAsync(null);
        vm.Target.SelectedPlatformOption = vm.Target.PlatformOptions.Single(p => p.Value == ExportPlatform.PcAndMac);
        Assert.Equal(final, vm.OutputPath);
        if (exists) File.WriteAllText(final, "existing package");
        SetAudio(vm);
        Assert.True(vm.ExportCommand.CanExecute(null), vm.ExportBlockedReason);

        await vm.ExportCommand.ExecuteAsync(null);

        Assert.True(vm.HasResult);
        Assert.Equal(2, vm.ResultPath.Split('\n').Length);
        Assert.Equal(RsxrTestSupport.PackageMagic(false), File.ReadAllBytes(final)[..4]);
        Assert.True(File.Exists(Path.Combine(_directory, "song_m.psarc")));
        Assert.Equal(!pickPcName && exists ? 1 : 0, prompt.Calls.Count);
        if (prompt.Calls.Count > 0)
            Assert.Equal(FakeLocalization.English().Format("Dialog_Export_OverwritePackage", final), prompt.Calls[0].Message);
    }

    private ExportHarness Harness(ChartProject project)
    {
        var harness = new ExportHarness(project);
        harness.Config.AppConfig.LastExportDir = _directory;
        harness.Config.AppConfig.ExportApplyDlcBuilderFixes = false;
        harness.Config.AppConfig.ExportGenerateDynamicDifficulty = false;
        return harness;
    }

    private static RocksmithExportViewModel Open(ExportHarness harness, Prompt prompt) =>
        new(harness.Edit, harness.Io with { Prompt = prompt }, harness.Dialogs, harness.Memory,
            RecordingProxy.Create<DotCharter.Services.Editing.Maintenance.IChartMaintenanceService>().Proxy,
            harness.DlcBuilderWriter, harness.IssuesDoor);

    private void SetAudio(RocksmithExportViewModel vm)
    {
        vm.IncludeVocals = false;
        vm.AudioPath = Path.Combine(_directory, "audio.wem");
        File.WriteAllBytes(vm.AudioPath, new byte[4000]);
        vm.PreviewAudioPath = vm.AudioPath;
    }

    private sealed class Prompt : IMessagePrompt
    {
        public bool Answer { get; init; }
        public List<(string Message, MessageDialogKind Kind, bool Confirmation)> Calls { get; } = [];

        public Task<bool> ShowAsync(string title, string message, MessageDialogKind kind = MessageDialogKind.Info,
            string? details = null, bool confirmation = false)
        {
            Calls.Add((message, kind, confirmation));
            return Task.FromResult(Answer);
        }
    }
}
