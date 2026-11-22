namespace DotCharter.Tests.Infrastructure;

/// <summary>The three answers <see cref="ParkedCall" /> gives, each on a call whose
/// state the test controls.</summary>
public sealed class ParkedCallTests
{
    private static readonly TimeSpan DeadlockCap = TimeSpan.FromSeconds(60);

    [Fact]
    public async Task ACallBlockedOnALock_IsReportedParked_AndReturnsOnceTheLockIsFree()
    {
        var ct = TestContext.Current.CancellationToken;
        var gate = new object();
        using var held = new ManualResetEventSlim();
        using var release = new ManualResetEventSlim();
        var holder = ParkedCall.Start(() =>
        {
            lock (gate)
            {
                held.Set();
                Assert.True(release.Wait(DeadlockCap, ct), "The test never released the lock.");
            }
        });
        Assert.True(held.Wait(DeadlockCap, ct), "The holder never took the lock.");
        var entered = 0;
        var call = ParkedCall.Start(() =>
        {
            lock (gate) Volatile.Write(ref entered, 1);
        });

        Assert.True(await call.WaitUntilParkedOrReturnedAsync("the locked call", DeadlockCap, ct));
        Assert.Equal(0, Volatile.Read(ref entered));

        release.Set();
        await Task.WhenAll(call.Returned, holder.Returned).WaitAsync(DeadlockCap, ct);
        Assert.Equal(1, Volatile.Read(ref entered));
    }

    [Fact]
    public async Task ACallThatReturns_IsReportedReturned_NeverParked()
    {
        var ct = TestContext.Current.CancellationToken;
        var call = ParkedCall.Start(() => { });

        Assert.False(await call.WaitUntilParkedOrReturnedAsync("the empty call", DeadlockCap, ct));
        await call.Returned.WaitAsync(DeadlockCap, ct);
    }

    [Fact]
    public async Task ACallThatDoesNeither_FailsAtTheCap_NamingTheCall()
    {
        var ct = TestContext.Current.CancellationToken;
        var stop = 0;
        var call = ParkedCall.Start(() =>
        {
            // Busy, never in a managed wait: neither parked nor returned.
            while (Volatile.Read(ref stop) == 0) Thread.SpinWait(64);
        });

        try
        {
            var failure = await Assert.ThrowsAnyAsync<Xunit.Sdk.XunitException>(
                () => call.WaitUntilParkedOrReturnedAsync("the spinning call", TimeSpan.Zero, ct));
            Assert.Contains("the spinning call neither parked in a wait nor returned", failure.Message, StringComparison.Ordinal);
        }
        finally
        {
            Volatile.Write(ref stop, 1);
        }

        await call.Returned.WaitAsync(DeadlockCap, ct);
    }

    [Fact]
    public async Task WhatTheCallThrew_FaultsReturned()
    {
        var ct = TestContext.Current.CancellationToken;
        var call = ParkedCall.Start(() => throw new InvalidOperationException("boom"));

        Assert.False(await call.WaitUntilParkedOrReturnedAsync("the throwing call", DeadlockCap, ct));
        var thrown = await Assert.ThrowsAsync<InvalidOperationException>(() => call.Returned.WaitAsync(DeadlockCap, ct));
        Assert.Equal("boom", thrown.Message);
    }
}
