using CommunityToolkit.Mvvm.Input;
using DotCharter.Presentation.Screens.Dialogs;
using DotCharter.Services.Export;
using DotCharter.Services.Export.DlcBuilder;
using DotCharter.Services.Infrastructure.Documents;
using DotCharter.Services.Parsing;
using DotCharter.Services.Parsing.Rscp;
using DotCharter.Services.Parsing.Xml;
using DotCharter.Tone.Infrastructure.Storage;
using Microsoft.Extensions.DependencyInjection;

namespace DotCharter.Tests.UI.ViewModels.Dialogs;

/// <summary>
///     The export dialog warns whenever the PC package will be written unencrypted (no encrypted
///     package possible, or Plain picked) and never when an encrypted one is written; its
///     "Export DLC Builder project" button writes the chart as a project into a picked folder.
/// </summary>
public sealed class RocksmithExportPlainWarningTests : IDisposable
{
    private readonly string _dir = Directory.CreateTempSubdirectory("dotcharter-plainwarn-").FullName;

    public void Dispose() => BestEffortDelete.Directory(_dir);

    private static ExportTargetViewModel Target() => new(FakeLocalization.English());

    private static void Pick(ExportTargetViewModel target, bool plain) =>
        target.SelectedPackageOption = target.PackageOptions.Single(o => o.Plain == plain);

    private static void Pick(ExportTargetViewModel target, ExportPlatform platform) =>
        target.SelectedPlatformOption = target.PlatformOptions.Single(o => o.Value == platform);

    [Fact]
    public void WithKeys_ThePlainWarningFollowsThePackageForm()
    {
        using var keys = TestRocksmithKeys.UseSynthetic();
        var target = Target();

        Assert.False(target.WritesPlainPackage);
        Pick(target, plain: true);
        Assert.True(target.WritesPlainPackage);
        Pick(target, plain: false);
        Assert.False(target.WritesPlainPackage);
    }

    [Fact]
    public void WithoutKeys_ThePlainWarningShowsWhateverThePickerSays()
    {
        using var keys = TestRocksmithKeys.UseNoKeys();
        var target = Target();

        Assert.True(target.WritesPlainPackage);
        Pick(target, plain: false);
        Assert.True(target.WritesPlainPackage);
    }

    [Fact]
    public void AMacOnlyTarget_WritesNoPlainPackage_SoItNeverWarns()
    {
        using var keys = TestRocksmithKeys.UseSynthetic();
        var target = Target();
        Pick(target, plain: true);
        Assert.True(target.WritesPlainPackage);

        Pick(target, ExportPlatform.Mac);
        Assert.False(target.WritesPlainPackage);
        Pick(target, ExportPlatform.PcAndMac);
        Assert.True(target.WritesPlainPackage);
    }

    [Fact]
    public void ThePlainWarning_RaisesChangesWhenEitherChoiceMovesIt()
    {
        using var keys = TestRocksmithKeys.UseSynthetic();
        var target = Target();
        var raised = new List<string?>();
        target.PropertyChanged += (_, e) => raised.Add(e.PropertyName);

        Pick(target, plain: true);
        Assert.Contains(nameof(ExportTargetViewModel.WritesPlainPackage), raised);

        raised.Clear();
        Pick(target, ExportPlatform.Mac);
        Assert.Contains(nameof(ExportTargetViewModel.WritesPlainPackage), raised);
    }

    [Fact]
    public void TheDialogExposesTheWarning_ThroughItsTarget()
    {
        using var keys = TestRocksmithKeys.UseNoKeys();
        var vm = new RocksmithExportDialogViewModelTests.Harness(RocksmithExportDialogViewModelTests.CreateValidProject()).Open();

        Assert.True(((DotCharter.Presentation.Contracts.Dialogs.IRocksmithExportScreen)vm).Target.WritesPlainPackage);
    }

    [Fact]
    public async Task TheButton_WritesTheChartAsADlcBuilderProject_IntoThePickedFolder()
    {
        var project = RocksmithExportDialogViewModelTests.CreateValidProject();
        var harness = new RocksmithExportDialogViewModelTests.Harness(project);
        var folder = Path.Combine(_dir, "dlc builder");
        harness.Storage.NextFolder = folder;
        harness.DlcBuilderWriter = RealWriter(harness);
        var vm = harness.Open();
        vm.AudioPath = Path.Combine(folder, "audio.wem");

        await ((IAsyncRelayCommand)vm.ExportDlcBuilderProjectCommand).ExecuteAsync(null);

        var projectFile = Path.Combine(folder, Rs2DlcProjectBuilder.FileNameFor(project));
        Assert.True(File.Exists(projectFile), $"no {projectFile}");
        Assert.NotEmpty(Directory.GetFiles(Path.Combine(folder, DlcBuilderBundle.DirectoryName), "*.xml"));
        var text = File.ReadAllText(projectFile);
        Assert.Contains("Lead", text);
        Assert.Contains("audio.wem", text);
        Assert.DoesNotContain(folder.Replace("\\", "\\\\", StringComparison.Ordinal), text);
        Assert.True(vm.HasDlcBuilderProjectMessage);
        Assert.Contains(projectFile, vm.DlcBuilderProjectMessage);
        Assert.Equal(FakeLocalization.English()["Picker_SelectDlcBuilderFolder"], harness.Storage.RequestedTitles.Last());
        Assert.Equal(Path.GetDirectoryName(vm.OutputPath), harness.Storage.RequestedStartDirectories.Last());
    }

    [Fact]
    public async Task ACancelledFolderPick_WritesNothing_AndSaysNothing()
    {
        var harness = new RocksmithExportDialogViewModelTests.Harness(RocksmithExportDialogViewModelTests.CreateValidProject());
        harness.Storage.NextFolder = null;
        var (writer, calls) = DotCharter.Tests.Presentation.Dialogs.RecordingProxy.Create<IDlcBuilderProjectWriter>();
        harness.DlcBuilderWriter = writer;
        var vm = harness.Open();

        await ((IAsyncRelayCommand)vm.ExportDlcBuilderProjectCommand).ExecuteAsync(null);

        Assert.Empty(calls.Calls);
        Assert.False(vm.HasDlcBuilderProjectMessage);
    }

    [Fact]
    public async Task AWriterFailure_IsShownInAPrompt_AndLeavesNoMessage()
    {
        var prompt = new RocksmithExportDialogViewModelTests.RecordingPrompt();
        var harness = new RocksmithExportDialogViewModelTests.Harness(RocksmithExportDialogViewModelTests.CreateValidProject(), prompt);
        harness.Storage.NextFolder = _dir;
        harness.DlcBuilderWriter = new ThrowingWriter();
        var vm = harness.Open();

        await ((IAsyncRelayCommand)vm.ExportDlcBuilderProjectCommand).ExecuteAsync(null);

        Assert.Equal(1, prompt.ShowCalls);
        Assert.False(vm.HasDlcBuilderProjectMessage);
    }

    private static DlcBuilderProjectWriter RealWriter(RocksmithExportDialogViewModelTests.Harness harness)
    {
        var services = new ServiceCollection();
        services.AddKeyedSingleton<IChartSerializer, RscpChartSerializer>("rscp");
        services.AddKeyedSingleton<IChartSerializer, XmlChartSerializer>("xml");
        services.AddSingleton<IConfigurationService>(harness.Config);
        var logger = new FakeAppLogger();
        return new DlcBuilderProjectWriter(new ProjectDocumentService(services.BuildServiceProvider(), logger), harness.Config, logger);
    }

    private sealed class ThrowingWriter : IDlcBuilderProjectWriter
    {
        public string Write(string folder, ChartProject snapshot, DlcBuilderProjectSources sources) =>
            throw new IOException("disk full");
    }
}
