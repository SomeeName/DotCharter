using DotCharter.Services.Export.Audio;

namespace DotCharter.Tests.UI.ViewModels.Dialogs;

/// <summary>Retained dialog text can differ from the chart input measured by prefill after undo.</summary>
public sealed partial class RocksmithExportDialogViewModelTests
{
    [Fact]
    public async Task Retained_preview_draft_after_undo_exports_the_later_clips_gain()
    {
        var audio = Path.Combine(_tempDir, "sections.wav");
        var samples = Enumerable.Range(0, 8000 * 64)
            .Select(i => (short)((i < 8000 * 32 ? 2000 : 14000) * Math.Sin(i * 2 * Math.PI * 220 / 8000))).ToArray();
        PcmAudioLoader.WriteWav(audio, new PcmAudio(samples, 1, 8000));
        var project = CreateValidProject();
        project.Chart.PreviewStartMs = 0;
        ProjectVolumes.Measure(project.Chart, audio, null, null);
        var originalGain = project.Chart.PreviewVolume;
        project.Chart.PreviewVolumeSignature = "legacy-preview";
        var h = new Harness(project);
        h.State.TotalAudioLengthMs = 64_000;
        using (var first = h.Open())
        {
            await BoundedWait.ForAsync(Prefill(first), "the first dialog's empty-source prefill", TestContext.Current.CancellationToken);
            first.AudioPath = audio;
            first.PreviewStartText = "0:32.000";
            first.CloseCommand.Execute(null);
        }
        h.History.Undo();
        Assert.Equal(0, project.Chart.PreviewStartMs);
        Assert.Equal("0:32.000", h.Memory.ExportDraft!.PreviewStartText);
        using var release = new ManualResetEventSlim();
        var started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var calls = 0;
        h.VolumeDecoder = path =>
        {
            if (Interlocked.Increment(ref calls) == 1)
            {
                started.TrySetResult();
                Assert.True(release.Wait(BoundedWait.DeadlockCap * 2), "The reopened prefill decoder was not released.");
            }
            return PcmAudioLoader.Load(path, []);
        };
        using var vm = h.Open();
        vm.OutputPath = Path.Combine(_tempDir, "later_p.psarc");
        vm.Target.SelectedPackageOption = vm.Target.PackageOptions[1];
        await BoundedWait.ForAsync(started.Task, "the reopened prefill decoder", TestContext.Current.CancellationToken);
        Assert.Equal("0:32.000", vm.PreviewStartText);
        Assert.Equal(0, project.Chart.PreviewStartMs);
        var pending = Prefill(vm);
        var run = vm.ExportCommand.ExecuteAsync(null);
        try
        {
            Assert.True(vm.IsBuilding);
        }
        finally
        {
            release.Set();
            await BoundedWait.ForAsync(pending, "the old-start prefill", TestContext.Current.CancellationToken);
            await BoundedWait.ForAsync(run, "exporting the retained preview draft", TestContext.Current.CancellationToken);
        }
        var expected = new ChartData { PreviewStartMs = 32_000 };
        ProjectVolumes.Measure(expected, audio, null, null);
        Assert.NotEqual(originalGain, expected.PreviewVolume);
        var build = Assert.Single(h.Logger.Snapshot(), e => e.Message.StartsWith("Rocksmith export started.", StringComparison.Ordinal));
        Assert.Contains($"previewVolume={expected.PreviewVolume}.", build.Message, StringComparison.Ordinal);
        Assert.Equal(32_000, vm.BuildOptions(project).PreviewStartMs);
        Assert.Equal(expected.PreviewVolume, vm.BuildOptions(project).PreviewVolume);
        Assert.Equal(expected.PreviewVolumeSignature, project.Chart.PreviewVolumeSignature);

        static Task Prefill(DotCharter.Presentation.Screens.Dialogs.RocksmithExportViewModel screen) =>
            (Task)typeof(DotCharter.Presentation.Screens.Dialogs.RocksmithExportViewModel)
                .GetField("_volumeMeasurement", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)!.GetValue(screen)!;
    }
}
