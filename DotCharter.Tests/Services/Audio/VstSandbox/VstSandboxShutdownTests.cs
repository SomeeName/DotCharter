namespace DotCharter.Tests.Services.Audio.VstSandbox;

public sealed class VstSandboxShutdownTests
{
    private static readonly TimeSpan Detector = TimeSpan.FromSeconds(60);

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task EveryChildReceivesQuitBeforeTheSingleSharedBoundIsWaited(bool dispose)
    {
        using var f = new VstSandboxPerSeatFixture();
        await f.Chain(f.Entry(0), f.Entry(1), f.Entry(2), f.Entry(3));
        foreach (var child in f.Children)
            child.StopReply = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        f.StopWait.Timeout = true;
        f.StopWait.BeforeWait = (stops, bound) =>
        {
            Assert.All(f.Children, child => Assert.Equal(1, child.Stops));
            Assert.False(stops.IsCompleted);
            Assert.Equal(TimeSpan.FromSeconds(10), bound);
        };

        if (dispose)
        {
            f.Service.Dispose();
        }
        else
        {
            await f.Call("ShutdownSandboxLockedAsync", true).WaitAsync(Detector, TestContext.Current.CancellationToken);
        }

        Assert.Single(f.StopWait.Bounds);
        Assert.All(f.Children, child =>
        {
            Assert.Equal(1, child.Kills);
            Assert.Equal(1, child.DisposeCalls);
            Assert.False(child.IsRunning);
            Assert.Equal([true], child.ExitRequests);
        });
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task FakeTerminationReportsRequestedExitAndCrashReportsUnexpectedExit(bool dispose)
    {
        var child = new SandboxChild(0, []);
        await child.StartAsync(48000, 256);
        bool? requested = null;
        child.Exited += value => requested = value;
        if (dispose) child.Dispose();
        else child.Kill();
        Assert.True(requested);
        Assert.False(child.IsRunning);

        var crashed = new SandboxChild(1, []);
        await crashed.StartAsync(48000, 256);
        crashed.Exited += value => requested = value;
        crashed.Crash();
        Assert.False(requested);
    }
}
