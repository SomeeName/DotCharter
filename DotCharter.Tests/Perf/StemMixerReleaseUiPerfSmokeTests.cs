using DotCharter.Tone.Infrastructure.Storage;
using DotCharter.Tone.UI.Platform;
using DotCharter.UI.ViewModels.Editor;

namespace DotCharter.Tests.Perf;

/// <summary>
///     #263 staged on the real dispatcher: a stem mixer disposed with its app, whose
///     rebuild is still parked in the engine load, must not take the NEXT app's UI
///     thread when that load finishes.
///     <para>
///         The flake this pins: <c>headless-uithread-claims.log</c> named a pool
///         thread whose dispatcher queue head was
///         <c>StemMixerViewModel.TakePendingRebuildOrRelease</c> — a rebuild
///         continuation of an app an earlier class had released, calling
///         <c>Dispatcher.UIThread</c> between Avalonia's reset and the session's
///         claim, which makes the caller the UI thread. The claim (#192) took it
///         back, but <c>HeadlessUiThreadClaimUiPerfSmokeTests</c>, which needs ITS
///         staged thread to win that window, lost.
///     </para>
///     <para>
///         Deterministic: the engine load waits on a gate, and the gate is opened
///         from a plain thread inside <c>RunBeforeNextClaimForTesting</c> — exactly
///         the window the leftover continuation used to land in. Before the fix the
///         continuation dispatched <c>FinishRebuild</c> there and the claim counted
///         a reclaim; now the disposed mixer dispatches nothing.
///     </para>
/// </summary>
[Collection("HeadlessUI")]
public sealed class StemMixerReleaseUiPerfSmokeTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(),
        "dotcharter-stemmixer-release-ui-" + Guid.NewGuid().ToString("N"));

    public void Dispose() => BestEffortDelete.Directory(_root);

    [Fact]
    public async Task AReleasedMixersParkedRebuild_DoesNotTakeTheNextAppsUiThread()
    {
        var dir = Path.Combine(_root, "song");
        Directory.CreateDirectory(Path.Combine(dir, "stems"));
        File.WriteAllBytes(Path.Combine(dir, "stems", "bass.wav"), [0]);
        var project = new ChartProject();
        project.Stems.Add(new Stem { Name = "bass", Path = Path.Combine("stems", "bass.wav"), Local = true, Volume = 1.0 });
        var state = new ProjectState { CurrentProjectPath = Path.Combine(dir, "song.rscp"), ActiveProject = project };
        var audio = new FakeAudioPlaybackService();
        // Default options: the gate's continuation runs on the thread that opens it.
        var gate = new TaskCompletionSource();
        audio.StemLoadGate = gate;

        var parked = await HeadlessSessionFixture.Dispatch(async () =>
        {
            var vm = new StemMixerViewModel(state, audio, FakeLocalization.KeysOnly(), ui: new AvaloniaUiDispatcher());
            // A condition with a deadlock-detector cap: the probe runs on a worker
            // and BuildStrips needs this dispatcher, so the wait yields to it.
            var deadline = DateTime.UtcNow + TimeSpan.FromSeconds(60);
            while (audio.StemLoadCalls.Count == 0 && DateTime.UtcNow < deadline)
                await Task.Delay(5, TestContext.Current.CancellationToken);
            // What App.ReleaseForIsolatedTeardown does to the container's singleton.
            vm.Dispose();
            return audio.StemLoadCalls.Count == 1;
        }, TestContext.Current.CancellationToken);
        Assert.True(parked, "the rebuild never reached the engine load, so nothing was parked to race");

        Thread? opener = null;
        var reclaimsBefore = HeadlessUiThreadClaim.Reclaims;
        HeadlessUiThreadClaim.RunBeforeNextClaimForTesting(() =>
        {
            // Between Avalonia's reset and the claim: the load finishes and the
            // rebuild's continuation runs here, off the session thread.
            opener = new Thread(() => gate.SetResult()) { Name = "stem-load-finished", IsBackground = true };
            opener.Start();
            opener.Join();
        });

        await HeadlessSessionFixture.Dispatch(() => { }, TestContext.Current.CancellationToken);

        Assert.NotNull(opener);
        Assert.True(HeadlessUiThreadClaim.Reclaims == reclaimsBefore,
            "the released stem mixer's rebuild continuation reached Dispatcher.UIThread between two apps and took "
            + $"the UI thread ({HeadlessUiThreadClaim.Reclaims - reclaimsBefore} reclaim(s); see "
            + "headless-uithread-claims.log). A disposed StemMixerViewModel must dispatch nothing (#263).");
    }
}
