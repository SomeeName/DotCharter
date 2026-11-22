using System.Reflection;
using DotCharter.Presentation.Screens.Dialogs;
using DotCharter.Services.Export.Audio;

namespace DotCharter.Tests.UI.ViewModels.Dialogs;

/// <summary>The public export command must join applicable prefill before capturing build options.</summary>
public sealed partial class RocksmithExportDialogViewModelTests
{
    [Fact]
    public async Task ReopeningAfterATargetChange_RefreshesAutomaticGainAndKeepsTheTypedDraft()
    {
        var project = CreateValidProject();
        var audio = AutomaticVolumeFixture.WriteMono(_tempDir);
        var h = new Harness(project);
        h.State.CurrentAudioFileName = audio;
        h.Config.AppConfig.ExportAutoVolumeTargetLufs = -20;
        var pending = typeof(RocksmithExportViewModel).GetField("_volumeMeasurement", BindingFlags.Instance | BindingFlags.NonPublic)!;

        using (var first = h.Open())
        {
            await BoundedWait.ForAsync((Task)pending.GetValue(first)!, "the original target prefill", TestContext.Current.CancellationToken);
            Assert.Equal(RocksmithAutoVolume.Calculate(audio, -20), first.AudioVolume);
            first.PreviewVolume = -6.5;
        }

        h.Config.AppConfig.ExportAutoVolumeTargetLufs = -10;
        using var reopened = h.Open();
        await BoundedWait.ForAsync((Task)pending.GetValue(reopened)!, "the new target prefill", TestContext.Current.CancellationToken);

        Assert.NotEqual(RocksmithAutoVolume.Calculate(audio, -20), reopened.AudioVolume);
        Assert.Equal(RocksmithAutoVolume.Calculate(audio, -10), reopened.AudioVolume);
        Assert.Equal(-6.5, reopened.PreviewVolume);
    }

    [Theory]
    [InlineData(false, false)]
    [InlineData(true, false)]
    [InlineData(false, true)]
    public async Task Export_joins_pending_volume_prefill_before_starting_the_package(bool typed, bool changeInput)
    {
        var audio = AutomaticVolumeFixture.WriteMono(_tempDir);
        var project = CreateValidProject();
        var h = new Harness(project);
        h.State.CurrentAudioFileName = audio;
        using var vm = h.Open();
        var field = typeof(RocksmithExportViewModel).GetField("_volumeMeasurement", BindingFlags.Instance | BindingFlags.NonPublic)!;
        await BoundedWait.ForAsync((Task)field.GetValue(vm)!, "the initial export prefill", TestContext.Current.CancellationToken);
        AutomaticVolumeFixture.Legacy(project.Chart, false);
        vm.AudioVolume = typed ? -4 : 31;
        vm.PreviewVolume = typed ? -5 : 32;
        vm.AudioPath = audio;
        vm.OutputPath = Path.Combine(_tempDir, "joined_p.psarc");
        vm.Target.SelectedPackageOption = vm.Target.PackageOptions[1];
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var services = (ChartEditDialogServices)typeof(Harness).GetField("_services", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(h)!;
        var pending = Prefill();
        field.SetValue(vm, pending);
        Assert.True(vm.ExportCommand.CanExecute(null));
        var run = vm.ExportCommand.ExecuteAsync(null);
        try
        {
            Assert.True(vm.IsBuilding);
            Assert.DoesNotContain(h.Logger.Snapshot(), e => e.Message.StartsWith("Rocksmith export started.", StringComparison.Ordinal));
            if (changeInput) vm.AudioPath = Path.Combine(_tempDir, "other.wav");
        }
        finally
        {
            release.TrySetResult();
            await BoundedWait.ForAsync(pending, "the released export prefill", TestContext.Current.CancellationToken);
            await BoundedWait.ForAsync(run, "the joined export", TestContext.Current.CancellationToken);
        }
        if (changeInput)
        {
            Assert.DoesNotContain(h.Logger.Snapshot(), e => e.Message.StartsWith("Rocksmith export started.", StringComparison.Ordinal));
            Assert.False(vm.IsBuilding);
            return;
        }
        var expected = AutomaticVolumeFixture.Expected(audio, false);
        Assert.Equal(typed ? -4 : expected.Audio, vm.AudioVolume);
        Assert.Equal(typed ? -5 : expected.Preview, vm.PreviewVolume);

        async Task Prefill()
        {
            await BoundedWait.ForAsync(release.Task, "the export-prefill release", TestContext.Current.CancellationToken);
            await BoundedWait.ForAsync(RocksmithExportSupport.EnsureProjectVolumesAsync(vm, project, services, typed, typed),
                "measuring the released export inputs", TestContext.Current.CancellationToken);
        }
    }
}
