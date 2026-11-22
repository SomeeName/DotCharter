using System.Reflection;
using DotCharter.Presentation.Screens.Dialogs;
using DotCharter.Services.Export.Audio;

namespace DotCharter.Tests.UI.ViewModels.Dialogs;

/// <summary>Cancelled comparison/restart work cannot adopt shown values typed after the run ends.</summary>
public sealed partial class RocksmithExportDialogViewModelTests
{
    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task Cancelled_signature_comparison_preserves_manual_values_for_the_next_export(bool cancel)
    {
        var audio = AutomaticVolumeFixture.WriteMono(_tempDir);
        var pcm = PcmAudioLoader.Load(audio, []);
        var project = CreateValidProject();
        AutomaticVolumeFixture.Legacy(project.Chart, false);
        using var releaseDecode = new ManualResetEventSlim();
        using var releaseSignature = new ManualResetEventSlim();
        var decodeStarted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var comparisonStarted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var signatures = 0;
        var h = new Harness(project);
        h.State.CurrentAudioFileName = audio;
        h.VolumeDecoder = path =>
        {
            var decoded = PcmAudioLoader.Load(path, []);
            decodeStarted.TrySetResult();
            Assert.True(releaseDecode.Wait(BoundedWait.DeadlockCap * 2), "The cancelled-run decoder was not released.");
            return decoded;
        };
        h.VolumeSignature = path =>
        {
            if (Interlocked.Increment(ref signatures) == 2)
            {
                comparisonStarted.TrySetResult();
                Assert.True(releaseSignature.Wait(BoundedWait.DeadlockCap * 2), "The cancelled-run signature comparison was not released.");
            }
            return ProjectVolumes.Signature(path);
        };
        using var vm = h.Open();
        vm.OutputPath = Path.Combine(_tempDir, "after-cancel_p.psarc");
        vm.Target.SelectedPackageOption = vm.Target.PackageOptions[1];
        var prefill = VolumeTask(vm);
        var run = Task.CompletedTask;
        var join = Task.CompletedTask;
        try
        {
            await BoundedWait.ForAsync(decodeStarted.Task, "the pending prefill decode", TestContext.Current.CancellationToken);
            PcmAudioLoader.WriteWav(audio, pcm with { Samples = pcm.Samples.Select(s => (short)(s / 10)).ToArray() });
            run = vm.ExportCommand.ExecuteAsync(null);
            join = VolumeTask(vm);
            await BoundedWait.ForAsync(comparisonStarted.Task, "the pending signature comparison", TestContext.Current.CancellationToken);
            if (cancel)
            {
                vm.CancelBuildCommand.Execute(null);
                await BoundedWait.ForAsync(run, "cancelling export before signature comparison returns", TestContext.Current.CancellationToken);
                Assert.False(vm.IsBuilding);
                vm.AudioVolume = -4;
                vm.PreviewVolume = -5;
            }
            else
            {
                releaseDecode.Set();
                await BoundedWait.ForAsync(prefill, "the active run's original automatic prefill", TestContext.Current.CancellationToken);
                Assert.NotEqual(31, vm.AudioVolume);
                Assert.True(vm.IsBuilding);
            }
        }
        finally
        {
            releaseSignature.Set();
            releaseDecode.Set();
            await SettleVolumeTask(prefill, "draining prefill after cancellation");
            await SettleVolumeTask(join, "draining the cancelled join");
            await BoundedWait.ForAsync(run, "draining the cancelled command", TestContext.Current.CancellationToken);
        }
        if (!cancel)
        {
            var expected = AutomaticVolumeFixture.Expected(audio, false);
            Assert.Equal(expected.Audio, vm.AudioVolume);
            Assert.Equal(expected.Preview, vm.PreviewVolume);
            return;
        }
        Assert.Equal(-4, vm.AudioVolume);
        Assert.Equal(-5, vm.PreviewVolume);
        Assert.Equal(2, signatures);
        Assert.DoesNotContain(h.Logger.Snapshot(), e => e.Message.StartsWith("Rocksmith export started.", StringComparison.Ordinal));
        await BoundedWait.ForAsync(vm.ExportCommand.ExecuteAsync(null), "exporting the preserved manual values", TestContext.Current.CancellationToken);
        Assert.Equal(-4, vm.BuildOptions(project).AudioVolume);
        var build = Assert.Single(h.Logger.Snapshot(), e => e.Message.StartsWith("Rocksmith export started.", StringComparison.Ordinal));
        Assert.Contains("previewVolume=-5.", build.Message, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Prefill_finishing_after_close_or_dispose_does_not_fill_dialog_fields(bool dispose)
    {
        var audio = AutomaticVolumeFixture.WriteMono(_tempDir);
        var project = CreateValidProject();
        AutomaticVolumeFixture.Legacy(project.Chart, false);
        using var release = new ManualResetEventSlim();
        var started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var h = new Harness(project);
        h.State.CurrentAudioFileName = audio;
        h.VolumeDecoder = path =>
        {
            started.TrySetResult();
            Assert.True(release.Wait(BoundedWait.DeadlockCap * 2), "The closing-dialog prefill decoder was not released.");
            return PcmAudioLoader.Load(path, []);
        };
        using var vm = h.Open();
        var pending = VolumeTask(vm);
        try
        {
            await BoundedWait.ForAsync(started.Task, "the closing-dialog prefill", TestContext.Current.CancellationToken);
            if (dispose) vm.Dispose();
            else vm.CloseCommand.Execute(null);
        }
        finally
        {
            release.Set();
            await SettleVolumeTask(pending, "prefill ending after close/disposal");
        }
        Assert.Equal(31, vm.AudioVolume);
        Assert.Equal(32, vm.PreviewVolume);
    }

    private static Task VolumeTask(RocksmithExportViewModel screen) =>
        (Task)typeof(RocksmithExportViewModel).GetField("_volumeMeasurement", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(screen)!;

    private static async Task SettleVolumeTask(Task task, string operation)
    {
        try
        {
            await BoundedWait.ForAsync(task, operation, TestContext.Current.CancellationToken);
        }
        catch (OperationCanceledException) when (task.IsCanceled)
        {
            return;
        }
    }
}
