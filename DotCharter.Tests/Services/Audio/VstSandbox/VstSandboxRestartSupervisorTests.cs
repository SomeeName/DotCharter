using DotCharter.Tone.Audio.Vst.Sandbox;

namespace DotCharter.Tests.Services.Audio.VstSandbox;

/// <summary>
/// #388: a child that died during the restart replay was swallowed (the crash handler
/// returns while a restart is in flight) and the chain stayed dead. The supervisor retries
/// with backoff, charges each failure to the policy, and surfaces the give-up.
/// </summary>
public sealed class VstSandboxRestartSupervisorTests
{
    private static readonly TimeSpan[] Delays = [TimeSpan.Zero, TimeSpan.FromSeconds(1), TimeSpan.FromSeconds(2)];

    private static VstSandboxRestartPolicy Policy(int budget)
        => new(maxCrashesPerWindow: budget, delays: Delays);

    [Fact]
    public async Task AnAttemptThatThrowsIsRetriedWithBackoffUntilItSticks()
    {
        var attempts = 0;
        var waits = new List<TimeSpan>();
        var failures = new List<Exception?>();

        var ok = await VstSandboxRestartSupervisor.RunAsync(
            () => ++attempts < 3 ? throw new IOException("child died") : Task.FromResult(true),
            Policy(5), () => TimeSpan.Zero, d => { waits.Add(d); return Task.CompletedTask; },
            (ex, _) => failures.Add(ex), () => Task.CompletedTask);

        Assert.True(ok);
        Assert.Equal(3, attempts);
        Assert.Equal([TimeSpan.FromSeconds(1)], waits); // the first retry has a zero backoff: no wait
        Assert.All(failures, f => Assert.IsType<IOException>(f));
        Assert.Equal(2, failures.Count);
    }

    [Fact]
    public async Task AChildThatDiesDuringReplayIsRetriedAndFinallyGivenUpOnce()
    {
        var attempts = 0;
        var gaveUp = 0;
        var retries = 0;

        var ok = await VstSandboxRestartSupervisor.RunAsync(
            () => { attempts++; return Task.FromResult(false); },
            Policy(2), () => TimeSpan.Zero, _ => Task.CompletedTask,
            (_, _) => retries++, () => { gaveUp++; return Task.CompletedTask; });

        Assert.False(ok);
        Assert.Equal(3, attempts); // budget 2 crashes + the one that exceeds it
        Assert.Equal(2, retries);
        Assert.Equal(1, gaveUp);
    }

    [Fact]
    public async Task AnOwnershipCheckCanEndRetriesWithoutChargingThePolicy()
    {
        var policy = Policy(2);
        var checks = 0;
        var ended = 0;
        var ok = await VstSandboxRestartSupervisor.RunAsync(
            () => Task.FromResult(false), policy, () => TimeSpan.Zero, _ => Task.CompletedTask,
            (_, _) => Assert.Fail("no retry expected"), () => { ended++; return Task.CompletedTask; },
            () => { checks++; return null; });

        Assert.False(ok);
        Assert.Equal(1, checks);
        Assert.Equal(1, ended);
        Assert.Equal(0, policy.RecentCrashCount(TimeSpan.Zero));
    }

    [Fact]
    public async Task ASuccessfulFirstAttemptNeverTouchesThePolicy()
    {
        var policy = Policy(2);
        var ok = await VstSandboxRestartSupervisor.RunAsync(
            () => Task.FromResult(true), policy, () => TimeSpan.Zero, _ => Task.CompletedTask,
            (_, _) => Assert.Fail("no retry expected"), () => Task.FromException(new InvalidOperationException()));

        Assert.True(ok);
        Assert.Equal(0, policy.RecentCrashCount(TimeSpan.Zero));
    }
}
