using DotCharter.Tone.Audio;
using DotCharter.Tone.Audio.Vst.Sandbox;

namespace DotCharter.Tests.Services.Audio.VstSandbox;

/// <summary>
/// The playback service used to <c>.GetAwaiter().GetResult()</c> every pipe
/// round trip to the VST sandbox child — a pool thread parked for up to 180 s
/// per plugin load. These pin the replacement (<see cref="VstSandboxCalls"/>):
/// a call whose reply never arrives returns to its caller at once, still gives
/// up at its bound, and a failure still falls back to the same logged warning.
/// <para>
/// The fake below IGNORES the timeout it is handed — the shape of a child that
/// stopped reading its pipe, where <see cref="VstSandboxHost.SendAsync"/>'s own
/// reply timer never starts — so the bound asserted here is the outer one.
/// Every wait in this file is a DEADLOCK DETECTOR, not a budget.
/// </para>
/// </summary>
public sealed class SandboxNonBlockingCallTests
{
    private static readonly TimeSpan Detector = TimeSpan.FromSeconds(60);
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public async Task AnEditorToggleReturnsToItsCallerWhileTheChildHasNotAnswered()
    {
        var logger = new FakeAppLogger();
        using var svc = new JuceAudioPlaybackService(FakeLocalization.English(), logger);
        var child = new FakeChannel();

        // Call on a thread of our own: a blocking implementation parks THAT
        // thread, and the detector below fails instead of hanging the run.
        var caller = Task.Factory.StartNew(
            () => svc.SendSandboxEditorToggleAsync(child, "slot-1", open: false),
            Ct, TaskCreationOptions.LongRunning, TaskScheduler.Default);
        var returned = await Task.WhenAny(caller, Task.Delay(Detector, Ct));
        Assert.True(returned == caller,
            "SendSandboxEditorToggleAsync blocked its caller on a reply that has not arrived.");

        var pending = await caller;
        Assert.False(pending.IsCompleted, "The toggle cannot have finished: the child never answered.");
        Assert.Equal("hideEditor", Assert.Single(child.Requests).Op);

        child.Reply.SetResult(new VstSandboxMessage { Ok = true });
        await pending.WaitAsync(Detector, Ct);
        Assert.DoesNotContain(logger.Entries, e => e.Message.Contains("Sandbox editor", StringComparison.Ordinal));
    }

    [Fact]
    public async Task AReplyThatNeverArrivesStillGivesUpAtItsBound()
    {
        var child = new FakeChannel();
        Exception? failure = null;

        var reply = await VstSandboxCalls.TrySendAsync(child, new VstSandboxRequest { Op = "load" },
                TimeSpan.FromMilliseconds(50), ex => failure = ex)
            .WaitAsync(Detector, Ct);

        Assert.Null(reply);
        Assert.IsType<TimeoutException>(failure);
    }

    [Fact]
    public async Task AFailedCallFallsBackToTheLoggedWarning()
    {
        var logger = new FakeAppLogger();
        using var svc = new JuceAudioPlaybackService(FakeLocalization.English(), logger);
        var child = new FakeChannel();
        child.Reply.SetException(new IOException("VST sandbox process exited."));

        await svc.SendSandboxEditorToggleAsync(child, "slot-2", open: false).WaitAsync(Detector, Ct);

        Assert.Contains(logger.Entries, e =>
            e.Message.Contains("Sandbox editor hide(slot-2) failed: VST sandbox process exited.", StringComparison.Ordinal));
    }

    [Fact]
    public void TheDisposeStopIsBoundedAndKillsAWedgedChild()
    {
        var child = new FakeChannel(); // StopAsync never completes

        var finished = VstSandboxCalls.StopForDispose(child, TimeSpan.FromMilliseconds(10), TimeSpan.FromMilliseconds(100));

        Assert.False(finished);
        Assert.Equal(1, child.Kills);
    }

    [Fact]
    public void AChildThatStopsCleanlyIsNotKilled()
    {
        var child = new FakeChannel();
        child.Stop.SetResult();

        Assert.True(VstSandboxCalls.StopForDispose(child, TimeSpan.FromMilliseconds(10), Detector));
        Assert.Equal(0, child.Kills);
    }

    private sealed class FakeChannel : IVstSandboxChannel
    {
        public TaskCompletionSource<VstSandboxMessage> Reply { get; } =
            new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource Stop { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public List<VstSandboxRequest> Requests { get; } = [];
        public int Kills;

        public bool IsRunning => true;
        public int? ChildProcessId => null;

        public Task<VstSandboxMessage> SendAsync(VstSandboxRequest request, TimeSpan? timeout = null)
        {
            lock (Requests) Requests.Add(request);
            return Reply.Task; // the timeout is ignored on purpose — see the class summary
        }

        public Task StopAsync(TimeSpan? gracePeriod = null) => Stop.Task;

        public void Kill() => Interlocked.Increment(ref Kills);
    }
}
