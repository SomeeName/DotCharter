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
    private static bool WaitFor(Func<bool> condition, int timeoutMs = 60_000)
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

    private static readonly TimeSpan Cadence = TimeSpan.FromMilliseconds(10);

    /// <summary>
    ///     Lets the worker tick exactly once on the fake clock: waits (a condition gate, not a
    ///     sleep) until the worker is parked on its next timer, then advances one cadence.
    /// </summary>
    private static void StepOneTick(StepTimeProvider clock, Func<int> ticks, int expectedTicks)
    {
        Assert.True(WaitFor(() => clock.PendingTimers >= 1), "worker never parked on its timer");
        clock.Advance(Cadence);
        Assert.True(WaitFor(() => ticks() >= expectedTicks), "worker did not tick");
    }

    [Fact]
    public void Start_is_idempotent()
    {
        var clock = new StepTimeProvider();
        var ticks = 0;
        var threads = new HashSet<int>();
        using var pump = new LivePlayDetectionPump(() =>
        {
            lock (threads) threads.Add(Environment.CurrentManagedThreadId);
            Interlocked.Increment(ref ticks);
        }, cadenceMs: 10, timeProvider: clock);
        pump.Start();
        pump.Start(); // second Start must not spawn a second worker
        pump.Start();

        // Each step wakes every parked worker: one worker => exactly one tick per step.
        for (var i = 1; i <= 3; i++) StepOneTick(clock, () => Volatile.Read(ref ticks), i);
        Assert.True(WaitFor(() => clock.PendingTimers == 1));

        Assert.Equal(3, Volatile.Read(ref ticks));
        lock (threads) Assert.Single(threads);
        pump.Stop();
        Assert.False(pump.IsRunning);
    }

    [Fact]
    public void No_tick_begins_after_stop_returns()
    {
        var clock = new StepTimeProvider();
        var ticks = 0;
        using var pump = new LivePlayDetectionPump(() => Interlocked.Increment(ref ticks), cadenceMs: 10, timeProvider: clock);
        pump.Start();
        for (var i = 1; i <= 3; i++) StepOneTick(clock, () => Volatile.Read(ref ticks), i);

        pump.Stop();
        Assert.False(pump.IsRunning);
        var atStop = Volatile.Read(ref ticks);

        // Time keeps moving after Stop returned, with plenty of cadences due: nothing may tick.
        clock.Advance(Cadence * 50);
        Assert.Equal(0, clock.PendingTimers);
        Assert.Equal(atStop, Volatile.Read(ref ticks));
    }

    /// <summary>#607: the tick used to SLEEP 80 ms, which a loaded box could finish
    /// before Stop was even called, so a Stop that does not join passed. Now the
    /// tick stays parked until the test releases it, and Stop must be SEEN parked
    /// (joining) before that release.</summary>
    [Fact]
    public async Task Stop_joins_an_in_flight_tick()
    {
        var ct = TestContext.Current.CancellationToken;
        using var entered = new ManualResetEventSlim(false);
        using var release = new ManualResetEventSlim(false);
        var completed = 0;
        var parked = 0;
        using var pump = new LivePlayDetectionPump(() =>
        {
            if (Interlocked.Increment(ref parked) == 1)
            {
                entered.Set();
                release.Wait(TimeSpan.FromMinutes(5));
            }

            Interlocked.Increment(ref completed);
        }, cadenceMs: 1);

        pump.Start();
        ParkedCall? stop = null;
        try
        {
            Assert.True(entered.Wait(60_000, ct), "tick never started");
            stop = ParkedCall.Start(pump.Stop);
            Assert.True(await stop.WaitUntilParkedOrReturnedAsync("Stop()", TimeSpan.FromSeconds(60), ct),
                "Stop returned while a tick was still in flight");
        }
        finally
        {
            release.Set();
        }

        await stop!.Returned.WaitAsync(TimeSpan.FromSeconds(60), ct);
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
