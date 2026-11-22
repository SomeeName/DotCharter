using System.Reflection;
using DotCharter.Presentation.Screens.Dialogs;
using DotCharter.Services.Diagnostics;
using DotCharter.Services.Export;
using DotCharter.Services.Export.Rocksmith;
using DotCharter.Tests.Presentation.Dialogs;
using DotCharter.Tone.Infrastructure.Storage;
using ExportHarness = DotCharter.Tests.UI.ViewModels.Dialogs.RocksmithExportDialogViewModelTests.Harness;

namespace DotCharter.Tests.Services.Export.Rsxr;

public sealed class RsxrFileHandlingTests : IDisposable
{
    private readonly string _directory = Directory.CreateTempSubdirectory("dotcharter-package-files-").FullName;

    public void Dispose() => BestEffortDelete.Directory(_directory);

    [Theory]
    [InlineData("chosen_m.psarc", true, "chosen_rsxr_p.psarc")]
    [InlineData("chosen_rsxr_m.psarc", true, "chosen_rsxr_p.psarc")]
    [InlineData("chosen_M.PSARC", true, "chosen_rsxr_p.psarc")]
    [InlineData("chosen_m.psarc", false, "chosen_m.psarc")]
    [InlineData("chosen.psarc", false, "chosen.psarc")]
    [InlineData("chosen_m.psarc.building", true, "chosen_m.psarc.building")]
    [InlineData("chosen", true, "chosen")]
    public async Task Tagging_a_mac_name_selects_the_native_pc_name(string input, bool tagged, string expected)
    {
        Assert.Equal(expected, ExtendedRangePackageName.Apply(input, tagged));
        if (!tagged || !input.EndsWith("_m.psarc", StringComparison.OrdinalIgnoreCase)) return;
        var built = RsxrTestSupport.Chart(false, 7);
        var audio = Path.Combine(_directory, "audio.wem");
        File.WriteAllBytes(audio, new byte[4000]);
        var path = Path.Combine(_directory, input);
        var result = await RsxrTestSupport.Build(built.Project, built.SongLengthMs, audio, path);
        Assert.Equal(Path.Combine(_directory, expected), result.PsarcPath);
        Assert.Equal(RsxrTestSupport.PackageMagic(true), File.ReadAllBytes(result.PsarcPath)[..4]);
        Assert.False(File.Exists(path));
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task Picker_confirms_the_changed_final_path_and_declining_keeps_the_field(bool accepted)
    {
        var harness = Harness();
        var prompt = new Prompt { Answer = accepted };
        var files = DispatchProxy.Create<IExportTargetWriter, FileProbe>();
        var probe = (FileProbe)(object)files;
        var picked = Path.Combine(_directory, "fresh_p.psarc");
        var final = Path.Combine(_directory, "fresh_rsxr_p.psarc");
        probe.Existing.Add(final);
        harness.Storage.NextSaveFile = picked;
        var maintenance = RecordingProxy.Create<DotCharter.Services.Editing.Maintenance.IChartMaintenanceService>().Proxy;
        using var vm = new RocksmithExportViewModel(harness.Edit, harness.Io with { Prompt = prompt, Files = files },
            harness.Dialogs, harness.Memory, maintenance, harness.DlcBuilderWriter, harness.IssuesDoor);
        var before = Path.Combine(_directory, "prior_rsxr_p.psarc");
        vm.OutputPath = before;

        await vm.BrowseOutputCommand.ExecuteAsync(null);

        var confirmation = Assert.Single(prompt.Calls);
        Assert.True(confirmation.Confirmation);
        Assert.Equal(MessageDialogKind.Warning, confirmation.Kind);
        Assert.Contains(final, confirmation.Message, StringComparison.Ordinal);
        Assert.Contains(final, probe.Probed);
        Assert.Equal(accepted ? final : before, vm.OutputPath);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Picker_completes_an_extensionless_name_before_confirming_replacement(bool accepted)
    {
        var harness = Harness();
        var prompt = new Prompt { Answer = accepted };
        var files = DispatchProxy.Create<IExportTargetWriter, FileProbe>();
        var probe = (FileProbe)(object)files;
        var final = Path.Combine(_directory, "song_rsxr_p.psarc");
        probe.Existing.Add(final);
        harness.Storage.NextSaveFile = Path.Combine(_directory, "song");
        var maintenance = RecordingProxy.Create<DotCharter.Services.Editing.Maintenance.IChartMaintenanceService>().Proxy;
        using var vm = new RocksmithExportViewModel(harness.Edit, harness.Io with { Prompt = prompt, Files = files },
            harness.Dialogs, harness.Memory, maintenance, harness.DlcBuilderWriter, harness.IssuesDoor);
        var before = Path.Combine(_directory, "prior_rsxr_p.psarc");
        vm.OutputPath = before;

        await vm.BrowseOutputCommand.ExecuteAsync(null);

        var confirmation = Assert.Single(prompt.Calls);
        Assert.True(confirmation.Confirmation);
        Assert.Equal(MessageDialogKind.Warning, confirmation.Kind);
        Assert.Contains(final, confirmation.Message, StringComparison.Ordinal);
        Assert.Contains(final, probe.Probed);
        var expected = accepted ? final : before;
        Assert.Equal(expected, vm.OutputPath);
        RocksmithExportSupport.PrepareOutputName(vm, true);
        Assert.Equal(expected, vm.OutputPath);
    }

    [Theory]
    [InlineData(true, true)]
    [InlineData(true, false)]
    [InlineData(false, true)]
    [InlineData(false, false)]
    public async Task Picker_only_asks_when_renaming_to_an_existing_file(bool tagged, bool finalExists)
    {
        var harness = Harness();
        var prompt = new Prompt { Answer = false };
        var files = DispatchProxy.Create<IExportTargetWriter, FileProbe>();
        var probe = (FileProbe)(object)files;
        var picked = Path.Combine(_directory, tagged ? "ready_rsxr_p.psarc" : "fresh_rsxr_p.psarc");
        var final = Path.Combine(_directory, tagged ? "ready_rsxr_p.psarc" : "fresh_p.psarc");
        if (finalExists) probe.Existing.Add(final);
        harness.Storage.NextSaveFile = picked;
        var pickers = new RocksmithExportPickers(harness.Edit, harness.Io with { Prompt = prompt, Files = files });

        var result = await pickers.OutputAsync("", tagged);

        if (!tagged && finalExists)
        {
            Assert.Single(prompt.Calls);
            Assert.Null(result);
        }
        else
        {
            Assert.Empty(prompt.Calls);
            Assert.Equal(final, result);
        }
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void A_derived_stock_name_follows_versions_without_the_range_tag(bool plain)
    {
        using var keys = TestRocksmithKeys.UseSynthetic();
        var harness = Harness(false);
        using var vm = harness.Open();
        vm.Target.SelectedPackageOption = vm.Target.PackageOptions.Single(p => p.Plain == plain);
        var authored = Path.Combine(_directory, RocksmithReleaseName.For(harness.State.ActiveProject!, "1") + "_rsxr_p.psarc");
        vm.OutputPath = authored;

        vm.Version = "2";

        Assert.Equal(Path.Combine(_directory, RocksmithReleaseName.For(harness.State.ActiveProject!, "2") + "_p.psarc"), vm.OutputPath);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void Native_derived_names_follow_the_version_and_selected_content(bool extended)
    {
        var harness = Harness(extended);
        using var vm = harness.Open();
        vm.Version = "2";
        Assert.Equal(RocksmithPackageName.For(harness.State.ActiveProject!, vm.Version, extended: extended), Path.GetFileName(vm.OutputPath));
    }

    [Fact]
    public async Task Native_export_completes_an_extensionless_name_only_when_exporting()
    {
        using var keys = TestRocksmithKeys.UseNoKeys();
        var harness = Harness();
        harness.Config.AppConfig.ExportApplyDlcBuilderFixes = false;
        harness.Config.AppConfig.ExportGenerateDynamicDifficulty = false;
        using var vm = harness.Open();
        var typed = Path.Combine(_directory, "song");
        vm.OutputPath = typed;
        vm.AudioPath = Path.Combine(_directory, "audio.wem");
        File.WriteAllBytes(vm.AudioPath, new byte[4000]);
        vm.PreviewAudioPath = vm.AudioPath;
        Assert.Equal(typed, vm.OutputPath);
        Assert.True(vm.ExportCommand.CanExecute(null), vm.ExportBlockedReason);

        await vm.ExportCommand.ExecuteAsync(null);

        var expected = Path.Combine(_directory, "song_rsxr_p.psarc");
        Assert.True(vm.HasResult, string.Join("\n", harness.Logger.Snapshot().Select(e => e.Message)));
        Assert.Equal(expected, vm.OutputPath);
        Assert.Equal(expected, vm.ResultPath);
        Assert.Equal(RsxrTestSupport.PackageMagic(true), File.ReadAllBytes(expected)[..4]);
        Assert.False(File.Exists(typed));
    }

    [Fact]
    public void Mac_version_omits_the_reserved_package_tag()
    {
        using var keys = TestRocksmithKeys.UseSynthetic();
        var harness = Harness(false);
        using var vm = harness.Open();
        vm.Target.SelectedPackageOption = vm.Target.PackageOptions.Single(p => !p.Plain);
        vm.Target.SelectedPlatformOption = vm.Target.PlatformOptions.Single(p => p.Value == ExportPlatform.Mac);
        var project = harness.State.ActiveProject!;
        vm.OutputPath = Path.Combine(_directory, RocksmithReleaseName.For(project, "1") + "_m.psarc");

        vm.Version = "1.rsxr";

        Assert.Equal(ExportPathDefaults.WithPlatformSuffix(Path.Combine(_directory, RocksmithReleaseName.For(project, "1") + "_m.psarc"), true), vm.OutputPath);
        Assert.EndsWith("_v1_m.psarc", vm.OutputPath);
    }

    [Fact]
    public void Platform_paths_keep_the_tag_only_on_pc()
    {
        var pc = Path.Combine(_directory, "song_rsxr_p.psarc");
        var mac = Path.Combine(_directory, "song_m.psarc");

        Assert.Equal(new[] { pc, mac }, new[]
        {
            RocksmithPlatformTargets.OutputPathFor(pc, ExportPlatform.PcAndMac, false),
            RocksmithPlatformTargets.OutputPathFor(pc, ExportPlatform.PcAndMac, true)
        });
        Assert.Equal(new[] { pc, mac }, new[]
        {
            ExportPathDefaults.WithPlatformSuffix(pc, false),
            ExportPathDefaults.WithPlatformSuffix(pc, true)
        });
    }

    private static ExportHarness Harness(bool extended = true)
    {
        var project = DotCharter.Tests.UI.ViewModels.Dialogs.RocksmithExportDialogViewModelTests.CreateValidProject();
        if (extended)
        {
            project.Arrangements[0].StringCount = 7;
            project.Arrangements[0].TuningPitches.Add(new TuningOffset(0));
        }
        return new ExportHarness(project);
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

    public class FileProbe : DispatchProxy
    {
        public HashSet<string> Existing { get; } = new(StringComparer.OrdinalIgnoreCase);
        public List<string> Probed { get; } = [];

        protected override object? Invoke(MethodInfo? targetMethod, object?[]? args)
        {
            if (targetMethod?.Name == nameof(IExportTargetWriter.FileExists))
            {
                var path = (string)args![0]!;
                Probed.Add(path);
                return Existing.Contains(path);
            }
            if (targetMethod?.Name == nameof(IExportTargetWriter.DirectoryExists)) return false;
            if (targetMethod?.Name == "get_DocumentsFolder") return "";
            throw new NotSupportedException(targetMethod?.Name);
        }
    }
}
