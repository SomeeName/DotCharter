using DotCharter.Tone.Audio.Diagnostics;

namespace DotCharter.Tests.Services.Audio;

/// <summary>
/// The audio health monitor's stop path could kill the process.
/// <para>
/// <c>AudioHealthMonitor.Stop</c> is the FIRST thing <c>JuceAudioPlaybackService.Dispose</c>
/// does, i.e. while the engine is still alive and possibly wedged. It disposed
/// the loop's stop event <b>unconditionally</b>, whether or not the 2 s join
/// succeeded — and the loop's <c>WaitOne</c> sat OUTSIDE its try/catch as the
/// loop CONDITION. So a <c>HealthPoll</c> P/Invoke blocking on a dying ASIO
/// device meant: join times out, event is disposed, the loop's next wait (at
/// most 20 ms away) throws <see cref="ObjectDisposedException" /> on a
/// background thread, and an unhandled exception there terminates the process.
/// The comment beside the join called that case "a handle leak either way".
/// It was not.
/// </para>
/// <para>
/// Both halves are pure and injected, so this drives the exact interleaving
/// without an engine, a device or a clock: no wall-clock assertion anywhere.
/// </para>
/// </summary>
public sealed class AudioHealthMonitorLoopTests
{
    /// <summary>
    /// The crash path itself: the stop handle is disposed while the loop is
    /// running. The loop must EXIT, and must not let the exception escape onto
    /// its background thread.
    /// </summary>
    [Fact]
    public void ThePollLoopExitsCleanly_WhenItsStopHandleIsDisposedUnderIt()
    {
        var stop = new AutoResetEvent(false);
        var polling = new ManualResetEventSlim(false);
        var polls = 0;
        Exception? escaped = null;
        var finished = new ManualResetEventSlim(false);

        var loop = new Thread(() =>
        {
            try
            {
                AudioHealthMonitor.RunPollLoop(
                    stop, intervalMs: 1,
                    poll: () => { Interlocked.Increment(ref polls); polling.Set(); },
                    onPollError: _ => { });
            }
            catch (Exception ex)
            {
                // Standing in for "unhandled on a background thread", which in
                // the product is a process kill rather than a catchable error.
                escaped = ex;
            }
            finally { finished.Set(); }
        })
        { IsBackground = true, Name = "health-loop-under-test" };

        loop.Start();
        Assert.True(polling.Wait(TimeSpan.FromSeconds(10), TestContext.Current.CancellationToken), "the loop never polled");

        // Exactly what the timed-out-join path does.
        stop.Dispose();

        Assert.True(finished.Wait(TimeSpan.FromSeconds(10), TestContext.Current.CancellationToken), "the loop never exited");
        Assert.True(loop.Join(TimeSpan.FromSeconds(10)));
        Assert.Null(escaped);
        Assert.True(polls > 0);
    }

    /// <summary>A throwing poll is logged and the loop KEEPS GOING — the
    /// guarantee the inner catch's comment always claimed.</summary>
    [Fact]
    public void AThrowingPollIsReportedAndDoesNotStopTheLoop()
    {
        var stop = new AutoResetEvent(false);
        var errors = 0;
        var polls = 0;
        var enough = new ManualResetEventSlim(false);
        var finished = new ManualResetEventSlim(false);

        var loop = new Thread(() =>
        {
            AudioHealthMonitor.RunPollLoop(
                stop, intervalMs: 1,
                poll: () =>
                {
                    if (Interlocked.Increment(ref polls) >= 3) enough.Set();
                    throw new InvalidOperationException("device went away");
                },
                onPollError: _ => Interlocked.Increment(ref errors));
            finished.Set();
        })
        { IsBackground = true, Name = "throwing-poll-loop" };

        loop.Start();
        Assert.True(enough.Wait(TimeSpan.FromSeconds(10), TestContext.Current.CancellationToken), "the loop stopped at the first throw");

        stop.Set();
        Assert.True(finished.Wait(TimeSpan.FromSeconds(10), TestContext.Current.CancellationToken));
        Assert.True(loop.Join(TimeSpan.FromSeconds(10)));

        Assert.True(polls >= 3);
        Assert.Equal(polls, errors);
        stop.Dispose();
    }

    /// <summary>The normal stop: signal, the loop exits, the handle is
    /// disposed. This is the case the leak fix must not regress.</summary>
    [Fact]
    public void AJoinedLoopThreadHasItsWaitHandleDisposed()
    {
        var stop = new AutoResetEvent(false);
        var running = new ManualResetEventSlim(false);

        var loop = new Thread(() =>
        {
            running.Set();
            AudioHealthMonitor.RunPollLoop(stop, 1, () => { }, _ => { });
        })
        { IsBackground = true, Name = "joined-loop" };

        loop.Start();
        Assert.True(running.Wait(TimeSpan.FromSeconds(10), TestContext.Current.CancellationToken));

        var joined = AudioHealthMonitor.TryStopLoopThread(stop, loop, TimeSpan.FromSeconds(10));

        Assert.True(joined);
        // Disposed, because the thread is provably gone.
        Assert.Throws<ObjectDisposedException>(() => stop.WaitOne(0));
    }

    /// <summary>
    /// THE fix. A loop thread that cannot be joined must keep its wait handle:
    /// disposing it hands the still-running loop an ObjectDisposedException on
    /// a background thread. Leaking one kernel handle is the cheaper outcome by
    /// a wide margin.
    /// </summary>
    [Fact]
    public void AnUnjoinableLoopThreadKeepsItsWaitHandle()
    {
        var stop = new AutoResetEvent(false);
        var wedged = new ManualResetEventSlim(false);
        var release = new ManualResetEventSlim(false);

        // Stands in for a HealthPoll P/Invoke blocked on a dying device: the
        // thread is alive and will not observe the stop signal in time.
        var loop = new Thread(() =>
        {
            wedged.Set();
            release.Wait(TimeSpan.FromSeconds(30));
        })
        { IsBackground = true, Name = "wedged-loop" };

        loop.Start();
        Assert.True(wedged.Wait(TimeSpan.FromSeconds(10), TestContext.Current.CancellationToken));

        var joined = AudioHealthMonitor.TryStopLoopThread(
            stop, loop, TimeSpan.FromMilliseconds(50));

        Assert.False(joined);
        // NOT disposed — this is the whole point. Before the fix the dispose was
        // unconditional and the still-running loop's next wait threw. The handle
        // is still usable AND still carries the stop signal TryStopLoopThread
        // set on it, which is what lets a late-waking loop exit on its own.
        Assert.True(stop.WaitOne(0), "the handle must survive the failed join, still signalled");

        release.Set();
        Assert.True(loop.Join(TimeSpan.FromSeconds(10)));
        stop.Dispose();
    }
}
