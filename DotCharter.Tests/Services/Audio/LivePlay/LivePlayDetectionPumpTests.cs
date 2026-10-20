using DotCharter.Services.Audio.LivePlay;

namespace DotCharter.Tests.Services.Audio.LivePlay;

/// <summary>
///     Lifecycle contract of the dedicated Live Play detection worker (the
///     2026-07-17 fix's architectural half): idempotent Start/Stop, a joining
///     Stop after which NO tick runs, restartability, and no deadlock when a
///     tick itself requests the stop. Timing asserts are deliberately loose
///     (polling with generous timeouts) — the cadence precision itself is not
///     under test, the lifecycle edges are.
/// </summary>
public class LivePlayDetectionPumpTests
{
    private static bool WaitFor(Func<bool> condition, int timeoutMs = 5000)
        => SpinWait.SpinUntil(condition, timeoutMs);

    [Fact]
    public void Ticks_after_start_and_reports_running()
    {
        var ticks = 0;
        using var pump = new LivePlayDetectionPump(() => Interlocked.Increment(ref ticks), cadenceMs: 5);

        Assert.False(pump.IsRunning);
        pump.Start();
        Assert.True(pump.IsRunning);
        Assert.True(WaitFor(() => Volatile.Read(ref ticks) >= 3), "worker never ticked");
    }

    [Fact]
    public void Start_is_idempotent()
    {
        var ticks = 0;
        using var pump = new LivePlayDetectionPump(() => Interlocked.Increment(ref ticks), cadenceMs: 5);
        pump.Start();
        pump.Start(); // second Start must not spawn a second worker
        pump.Start();
        Assert.True(WaitFor(() => Volatile.Read(ref ticks) >= 2));
        pump.Stop();

        // With a single worker every tick observed the stop flag before the
        // join returned — a duplicate thread would keep incrementing.
        var after = Volatile.Read(ref ticks);
        Thread.Sleep(100);
        Assert.Equal(after, Volatile.Read(ref ticks));
    }

    [Fact]
    public void No_tick_begins_after_stop_returns()
    {
        var ticks = 0;
        using var pump = new LivePlayDetectionPump(() => Interlocked.Increment(ref ticks), cadenceMs: 1);
        pump.Start();
        Assert.True(WaitFor(() => Volatile.Read(ref ticks) >= 5));

        pump.Stop();
        Assert.False(pump.IsRunning);
        var atStop = Volatile.Read(ref ticks);
        Thread.Sleep(150); // many cadences worth of opportunity
        Assert.Equal(atStop, Volatile.Read(ref ticks));
    }

    [Fact]
    public void Stop_joins_an_in_flight_tick()
    {
        var entered = new ManualResetEventSlim(false);
        var completed = 0;
        using var pump = new LivePlayDetectionPump(() =>
        {
            entered.Set();
            Thread.Sleep(80);
            Interlocked.Increment(ref completed);
        }, cadenceMs: 1);

        pump.Start();
        Assert.True(entered.Wait(5000, TestContext.Current.CancellationToken), "tick never started");
        pump.Stop();

        // Stop returned ⇒ the in-flight tick ran to completion first.
        Assert.True(Volatile.Read(ref completed) >= 1, "Stop returned while a tick was still in flight");
    }

    [Fact]
    public void Stop_is_idempotent_and_restart_resumes_ticking()
    {
        var ticks = 0;
        using var pump = new LivePlayDetectionPump(() => Interlocked.Increment(ref ticks), cadenceMs: 5);
        pump.Start();
        Assert.True(WaitFor(() => Volatile.Read(ref ticks) >= 1));
        pump.Stop();
        pump.Stop(); // second Stop is a no-op
        Assert.False(pump.IsRunning);

        var beforeRestart = Volatile.Read(ref ticks);
        pump.Start();
        Assert.True(WaitFor(() => Volatile.Read(ref ticks) > beforeRestart), "restart never ticked");
        pump.Stop();
    }

    [Fact]
    public void Stop_requested_from_inside_a_tick_does_not_deadlock()
    {
        LivePlayDetectionPump? pump = null;
        var stopped = 0;
        pump = new LivePlayDetectionPump(() =>
        {
            // A tick tearing its own pump down (belt-and-braces path): must
            // not self-join.
            pump!.Stop();
            Interlocked.Increment(ref stopped);
        }, cadenceMs: 1);

        pump.Start();
        Assert.True(WaitFor(() => Volatile.Read(ref stopped) >= 1), "self-stopping tick never ran");
        Assert.True(WaitFor(() => !pump.IsRunning));
        pump.Stop(); // outer stop after self-stop is still a safe no-op
    }

    /// <summary>
    ///     Concurrency audit #12: the stop flag was ONE field shared by every
    ///     worker, so a <c>Start</c> issued while the previous worker was still
    ///     inside its tick (here: from that very tick, after a self-stop) reset
    ///     the flag under the old worker and the two ticked concurrently — into a
    ///     channel whose producer side is single-threaded by contract. The
    ///     replacement worker must not tick until its predecessor has exited.
    ///     The 50 ms hold inside the first tick is a DELAY that gives an
    ///     unguarded pump every chance to overlap; the overlap counter is the
    ///     assertion, and with the join in place it cannot depend on timing.
    /// </summary>
    [Fact]
    public void A_restart_from_inside_a_tick_never_overlaps_the_old_worker()
    {
        LivePlayDetectionPump? pump = null;
        var inTick = 0;
        var overlaps = 0;
        var firstTickDone = 0;
        var laterTicks = 0;
        var threads = new HashSet<int>();
        pump = new LivePlayDetectionPump(() =>
        {
            lock (threads) threads.Add(Environment.CurrentManagedThreadId);
            if (Interlocked.Exchange(ref inTick, 1) == 1) Interlocked.Increment(ref overlaps);
            try
            {
                if (Volatile.Read(ref firstTickDone) == 0)
                {
                    // Retire this worker and start its replacement from INSIDE
                    // the tick, then stay in the tick for a while.
                    pump!.Stop();
                    pump.Start();
                    Thread.Sleep(50);
                    Volatile.Write(ref firstTickDone, 1);
                }
                else
                {
                    Interlocked.Increment(ref laterTicks);
                }
            }
            finally
            {
                Volatile.Write(ref inTick, 0);
            }
        }, cadenceMs: 1);

        pump.Start();
        Assert.True(WaitFor(() => Volatile.Read(ref laterTicks) >= 5), "the replacement worker never ticked");
        pump.Stop();

        Assert.Equal(0, Volatile.Read(ref overlaps));
        lock (threads) Assert.Equal(2, threads.Count); // the old worker and its replacement
        Assert.False(pump.IsRunning);
    }
}
