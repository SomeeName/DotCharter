using System.Reflection;
using DotCharter.Presentation.Screens.Dialogs;
using DotCharter.Services.Export.Audio;

namespace DotCharter.Tests.UI.ViewModels.Dialogs;

/// <summary>A mismatched pending source must not publish over the fresh measurement's before fields.</summary>
public sealed partial class RocksmithExportDialogViewModelTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Unchanged_pending_inputs_join_one_real_decode_and_preserve_typed_values(bool typed)
    {
        var audio = AutomaticVolumeFixture.WriteMono(_tempDir);
        var project = CreateValidProject();
        AutomaticVolumeFixture.Legacy(project.Chart, false);
        using var release = new ManualResetEventSlim();
        var started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var calls = 0;
        var h = new Harness(project);
        h.State.CurrentAudioFileName = audio;
        h.VolumeDecoder = path =>
        {
            Interlocked.Increment(ref calls);
            started.TrySetResult();
            Assert.True(release.Wait(BoundedWait.DeadlockCap * 2), "The matching-input decoder was not released.");
            return PcmAudioLoader.Load(path, []);
        };
        using var vm = h.Open();
        vm.OutputPath = Path.Combine(_tempDir, "matching_p.psarc");
        vm.Target.SelectedPackageOption = vm.Target.PackageOptions[1];
        var pending = (Task)typeof(RocksmithExportViewModel).GetField("_volumeMeasurement", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(vm)!;
        var run = Task.CompletedTask;
        try
        {
            await BoundedWait.ForAsync(started.Task, "the matching-input prefill decode", TestContext.Current.CancellationToken);
            if (typed)
            {
                vm.AudioVolume = -4;
                vm.PreviewVolume = -5;
            }
            run = vm.ExportCommand.ExecuteAsync(null);
            Assert.True(vm.IsBuilding);
        }
        finally
        {
            release.Set();
            await BoundedWait.ForAsync(pending, "the matching-input prefill", TestContext.Current.CancellationToken);
            await BoundedWait.ForAsync(run, "exporting with matching pending inputs", TestContext.Current.CancellationToken);
        }
        var expected = AutomaticVolumeFixture.Expected(audio, false);
        Assert.Equal(1, calls);
        Assert.Equal(typed ? -4 : expected.Audio, vm.AudioVolume);
        Assert.Equal(typed ? -5 : expected.Preview, vm.PreviewVolume);
        Assert.Equal(expected.PreviewSignature, project.Chart.PreviewVolumeSignature);
        var build = Assert.Single(h.Logger.Snapshot(), e => e.Message.StartsWith("Rocksmith export started.", StringComparison.Ordinal));
        Assert.Contains($"previewVolume={(typed ? -5 : expected.Preview)}.", build.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Replaced_source_restarts_and_retires_the_old_pending_measurement()
    {
        var audio = AutomaticVolumeFixture.WriteMono(_tempDir);
        var pcm = PcmAudioLoader.Load(audio, []);
        var project = CreateValidProject();
        var before = AutomaticVolumeFixture.Legacy(project.Chart, false);
        using var releaseOld = new ManualResetEventSlim();
        using var releaseFresh = new ManualResetEventSlim();
        var oldStarted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var freshStarted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var calls = 0;
        var h = new Harness(project);
        h.State.CurrentAudioFileName = audio;
        h.VolumeDecoder = path =>
        {
            var decoded = PcmAudioLoader.Load(path, []);
            var call = Interlocked.Increment(ref calls);
            if (call == 1)
            {
                oldStarted.TrySetResult();
                Assert.True(releaseOld.Wait(BoundedWait.DeadlockCap * 2), "The old-source decoder was not released.");
            }
            else if (call == 2)
            {
                freshStarted.TrySetResult();
                Assert.True(releaseFresh.Wait(BoundedWait.DeadlockCap * 2), "The fresh-source decoder was not released.");
            }
            return decoded;
        };
        using var vm = h.Open();
        vm.OutputPath = Path.Combine(_tempDir, "replacement_p.psarc");
        vm.Target.SelectedPackageOption = vm.Target.PackageOptions[1];
        var pending = (Task)typeof(RocksmithExportViewModel).GetField("_volumeMeasurement", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(vm)!;
        var run = Task.CompletedTask;
        try
        {
            await BoundedWait.ForAsync(oldStarted.Task, "the original source's decode", TestContext.Current.CancellationToken);
            PcmAudioLoader.WriteWav(audio, pcm with { Samples = pcm.Samples.Select(s => (short)(s / 10)).ToArray() });
            run = vm.ExportCommand.ExecuteAsync(null);
            await BoundedWait.ForAsync(freshStarted.Task, "the replacement source's fresh decode", TestContext.Current.CancellationToken);
            releaseOld.Set();
            await BoundedWait.ForAsync(pending, "discarding the old-source prefill", TestContext.Current.CancellationToken);
            Assert.Equal(before, ProjectVolumeFields.From(project.Chart));
            Assert.DoesNotContain(h.Logger.Snapshot(), e => e.Message.StartsWith("Rocksmith export started.", StringComparison.Ordinal));
        }
        finally
        {
            releaseOld.Set();
            releaseFresh.Set();
            await BoundedWait.ForAsync(pending, "draining original source work", TestContext.Current.CancellationToken);
            await BoundedWait.ForAsync(run, "exporting the replacement source", TestContext.Current.CancellationToken);
        }
        var expected = AutomaticVolumeFixture.Expected(audio, false);
        Assert.Equal(expected, ProjectVolumeFields.From(project.Chart));
        var build = Assert.Single(h.Logger.Snapshot(), e => e.Message.StartsWith("Rocksmith export started.", StringComparison.Ordinal));
        Assert.Contains($"previewVolume={expected.Preview}.", build.Message, StringComparison.Ordinal);
        Assert.Equal(2, calls);
    }
}
