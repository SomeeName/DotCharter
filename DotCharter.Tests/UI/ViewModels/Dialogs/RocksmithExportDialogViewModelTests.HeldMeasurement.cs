using System.Runtime.CompilerServices;

namespace DotCharter.Tests.UI.ViewModels.Dialogs;

/// <summary>Closing the dialog ends its open-time volume measurement, so a slow one cannot pin the project.</summary>
public sealed partial class RocksmithExportDialogViewModelTests
{
    /// <summary>
    ///     Opening the dialog measures the song's volumes on the pool, and that measurement holds the
    ///     project until it ends: on a long song or a loaded box, seconds after the dialog closed. The
    ///     audio signature is held open for the whole test, so the closed project must be collectable
    ///     while the measurement is still running.
    /// </summary>
    [Fact]
    public async Task Closing_the_dialog_releases_the_project_while_its_volume_measurement_still_runs()
    {
        using var release = new ManualResetEventSlim();
        var started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        try
        {
            var (harness, probe) = OpenWithAHeldMeasurement(AutomaticVolumeFixture.WriteMono(_tempDir), started, release);
            await BoundedWait.ForAsync(started.Task, "the open-time audio signature", TestContext.Current.CancellationToken);

            CloseAndForget(harness);
            harness.State.ActiveArrangement = null;
            harness.State.ActiveProject = new ChartProject();

            GC.Collect();
            GC.WaitForPendingFinalizers();
            GC.Collect();

            Assert.False(release.IsSet);
            Assert.False(probe.IsAlive,
                "the closed export dialog's volume measurement is still holding the project; it stays alive until the measurement ends.");
        }
        finally
        {
            release.Set();
        }
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static (Harness Harness, WeakReference Probe) OpenWithAHeldMeasurement(string audio, TaskCompletionSource started,
        ManualResetEventSlim release)
    {
        var project = CreateValidProject();
        var harness = new Harness(project);
        harness.State.CurrentAudioFileName = audio;
        harness.VolumeSignature = _ =>
        {
            started.TrySetResult();
            BoundedWait.For(release, "the test to release the held audio signature");
            return "held";
        };
        harness.Open();
        return (harness, new WeakReference(project));
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static void CloseAndForget(Harness harness)
    {
        harness.Vm.CloseCommand.Execute(null);
        harness.Forget();
    }
}
