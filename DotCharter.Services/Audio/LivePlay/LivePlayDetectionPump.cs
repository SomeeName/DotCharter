using System.Diagnostics;

namespace DotCharter.Services.Audio.LivePlay;

/// <summary>
///     The dedicated Live Play detection worker: a single background thread that
///     invokes <c>tick</c> at a fixed cadence (~33 ms), independent of the UI
///     thread's health. This is the architectural half of the
///     judging-inertness fix — a dispatcher timer (Background OR Input priority)
///     shares the UI thread, so sustained render/compositor load stretches its
///     tick gaps past the session's seek-jump threshold; a dedicated thread
///     cannot be starved by rendering at all.
///     <para>
///     Pacing is drift-aware: ticks aim at absolute multiples of the cadence on a
///     monotonic clock, so the schedule does not accumulate the per-tick timer
///     slop that <c>Thread.Sleep(interval)</c> loops drift by. When the thread
///     falls far behind (debugger pause, CPU starvation) it REALIGNS to the
///     present instead of firing a burst of catch-up ticks — the session resolves
///     the gap itself (stall-vs-seek evidence), and a tick burst would only pile
///     more work onto an already-starved machine.
///     </para>
///     <para>
///     Lifecycle: <see cref="Start"/>/<see cref="Stop"/> are idempotent and
///     thread-safe. <see cref="Stop"/> blocks until the worker has fully exited —
///     a tick in flight completes, and NO tick begins after Stop returns. Never
///     call Stop while holding a lock the tick callback also takes (classic
///     join-under-lock deadlock); as a belt-and-braces guard a Stop issued from
///     inside the tick callback itself skips the self-join and lets the loop
///     exit on return.
///     </para>
/// </summary>
public sealed class LivePlayDetectionPump : IDisposable
{
    private readonly Action _tick;
    private readonly int _cadenceMs;
    private readonly string _threadName;
    private readonly object _lifecycle = new();
    private Run? _run;

    /// <summary>A worker that was told to stop from INSIDE its own tick and so
    /// could not be joined by that <see cref="Stop" />. The next
    /// <see cref="Start" /> hands it to the new worker to join first, and a
    /// later <see cref="Stop" /> joins it directly.</summary>
    private Run? _retiring;

    /// <summary>
    ///     One worker's lifetime. The stop flag is PER RUN, never shared
    ///     (a shared one races): with one field, a <see cref="Start" /> racing a
    ///     <see cref="Stop" /> that had released the lock but not yet joined reset
    ///     the flag under the OLD worker — which was then mid-tick, so two workers
    ///     ticked at once into a channel whose producer side is single-threaded by
    ///     contract. Each run also owns its wake event and knows who to dispose it.
    /// </summary>
    private sealed class Run
    {
        public Thread Thread { get; set; } = null!;
        public required ManualResetEventSlim Wake { get; init; }
        public volatile bool StopRequested;

        /// <summary>The worker this one must JOIN before its first tick, so that
        /// a Start issued from inside the previous worker's tick cannot make two
        /// ticks overlap.</summary>
        public Run? Predecessor;

        /// <summary>Set by a self-stop: nobody joins this run, so it disposes its
        /// own wake event on exit.</summary>
        public volatile bool DisposeWakeOnExit;
    }

    public LivePlayDetectionPump(Action tick, int cadenceMs = 33, string threadName = "LivePlay Detection Pump")
    {
        ArgumentNullException.ThrowIfNull(tick);
        ArgumentOutOfRangeException.ThrowIfLessThan(cadenceMs, 1);
        _tick = tick;
        _cadenceMs = cadenceMs;
        _threadName = threadName;
    }

    /// <summary>True while the worker thread is running (between Start and Stop).</summary>
    public bool IsRunning
    {
        get { lock (_lifecycle) return _run != null; }
    }

    /// <summary>Starts the worker. No-op when already running.</summary>
    public void Start()
    {
        lock (_lifecycle)
        {
            if (_run != null) return;
            // A self-stopped predecessor is still finishing its tick right now
            // (it may even be the thread calling Start): the new worker waits
            // for it before it ticks, so the two never overlap.
            var run = new Run { Wake = new ManualResetEventSlim(false), Predecessor = _retiring };
            _retiring = null;
            // AboveNormal mirrors the intent of the old Input-priority timer:
            // detection must outrank rendering under load. Background so it can
            // never hold the process open.
            run.Thread = new Thread(() => Worker(run))
            {
                IsBackground = true,
                Name = _threadName,
                Priority = ThreadPriority.AboveNormal
            };
            _run = run;
            run.Thread.Start();
        }
    }

    /// <summary>
    ///     Stops the worker and waits for it to exit. No-op when not running.
    ///     After this returns, no further tick will run (and none is in flight) —
    ///     including a worker retired by an earlier self-stop.
    /// </summary>
    public void Stop()
    {
        Run? run;
        Run? retiring;
        lock (_lifecycle)
        {
            run = _run;
            retiring = _retiring;
            _run = null;
            if (run != null)
            {
                run.StopRequested = true;
                run.Wake.Set();
                if (run.Thread == Thread.CurrentThread)
                {
                    // Stop from inside the tick: the loop exits when the tick
                    // returns, and this thread cannot join itself. It is parked
                    // as "retiring" so the next Start, or the next Stop, joins it.
                    run.DisposeWakeOnExit = true;
                    _retiring = run;
                    return;
                }
            }

            _retiring = null;
        }

        if (retiring != null && retiring.Thread != Thread.CurrentThread) retiring.Thread.Join();
        if (run == null) return;
        run.Thread.Join();
        run.Wake.Dispose();
    }

    public void Dispose() => Stop();

    private void Worker(Run run)
    {
        try
        {
            // Never overlap the worker this one replaced, even when that worker
            // is the thread that started us (a Start from inside its tick).
            var predecessor = run.Predecessor;
            if (predecessor != null && predecessor.Thread != Thread.CurrentThread) predecessor.Thread.Join();

            var clock = Stopwatch.StartNew();
            long tickIndex = 0;
            while (!run.StopRequested)
            {
                tickIndex++;
                var targetMs = tickIndex * _cadenceMs;
                var waitMs = targetMs - clock.ElapsedMilliseconds;
                if (waitMs > 0)
                {
                    if (run.Wake.Wait((int)Math.Min(waitMs, int.MaxValue))) break; // woken = stop
                }
                else if (waitMs < -4L * _cadenceMs)
                {
                    // Far behind schedule: realign rather than burst catch-up ticks.
                    tickIndex = clock.ElapsedMilliseconds / _cadenceMs;
                }

                if (run.StopRequested) break;
                _tick();
            }
        }
        finally
        {
            if (run.DisposeWakeOnExit) run.Wake.Dispose();
        }
    }
}
