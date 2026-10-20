// AudioHealthMonitor: the audio health monitor — a background poll of the render clock that logs the device config, dropouts and a heartbeat (a collaborator of JuceAudioPlaybackService).
using System.Diagnostics;
using JuceSharp;

namespace DotCharter.Tone.Audio.Diagnostics;

/// <summary>
///     Audio health logging (owner request: "log audio dropouts / stutter /
///     glitches — I get some and don't know the cause"). A lightweight background
///     poll (no native ABI change, no work on the audio thread) reads the render
///     clock and flags callback stalls via the pure
///     <see cref="AudioDropoutDetector" />, and emits:
///     <list type="bullet">
///       <item>an INFO line with the active device config (sample rate, block
///         size, block period, output latency) whenever it changes — the context
///         every dropout should be read against;</item>
///       <item>a WARNING per detected dropout burst, with how long the audio
///         thread stalled and the block period it blew through;</item>
///       <item>a periodic DEBUG heartbeat with the running dropout tally and the
///         measured render rate (which surfaces a SUSTAINED under-run the
///         per-poll staleness check can miss).</item>
///     </list>
///     Everything is gated on the <see cref="AppLogCategory.Audio" /> log category,
///     so the user's existing logging switch turns the whole thing on and off.
///     <para>
///         Owned by <c>JuceAudioPlaybackService</c>, which hands it the engine as a
///         getter: the monitor reads the current engine once per poll, takes none of
///         the service's gates and writes nothing but log lines, so it shares no
///         state with the service beyond that one volatile read.
///     </para>
/// </summary>
internal sealed class AudioHealthMonitor
{
    // 20 ms poll: fast enough to land inside the multi-block stalls a user hears
    // (a 48 kHz/256 block is 5.3 ms), cheap enough to be background noise (a
    // render-clock read is a lock-free native peek of two longs).
    private const int HealthPollIntervalMs = 20;
    private const double HealthHeartbeatMs = 30_000;

    private readonly AudioDropoutDetector _dropoutDetector = new();
    // The sustained-under-run half. Pure and separate because "the clock could
    // not be read" and "the clock read zero" have to stay different values —
    // folding them together is what made the heartbeat invent under-runs.
    private readonly AudioRenderRateTracker _renderRate = new();
    private readonly AutoResetEvent _healthStop = new(false);
    private Thread? _healthThread;

    // Config context, logged only when it changes.
    private readonly IAppLogger? _logger;
    private readonly Func<JuceAudioEngine?> _engine;
    private readonly Func<bool> _isDisposing;

    private int _healthLastSampleRate;
    private int _healthLastBlockFrames;
    private int _healthLastLatencySamples = -1;

    // Current dropout burst (max staleness across the polls it spans).
    private double _burstWorstStalenessMs;

    // Since-start + since-last-heartbeat tallies.
    private long _healthTotalDropouts;
    private double _healthWorstStalenessMs;
    private long _hbDropouts;
    private double _hbWorstStalenessMs;
    private long _hbLastTicks;

    // ABI v30: the engine's OWN counters, read once per heartbeat. The managed
    // detector above sees that the render clock went stale — the symptom. These
    // say whether the callback was late to START (something stalled us) or slow
    // to FINISH (we are too heavy), which is the question the owner actually
    // asked and the heuristic structurally cannot answer.
    private XrunStats _hbLastXrun;
    private bool _hbHaveXrun;

    public AudioHealthMonitor(IAppLogger? logger, Func<JuceAudioEngine?> engine, Func<bool> isDisposing)
    {
        _logger = logger;
        _engine = engine;
        _isDisposing = isDisposing;
    }

    public void Start()
    {
        if (_logger == null) return; // nothing to log to
        _healthThread = new Thread(HealthLoop)
        {
            IsBackground = true,
            Name = "AudioHealthMonitor",
            Priority = ThreadPriority.BelowNormal
        };
        _healthThread.Start();
    }

    public void Stop()
    {
        var joined = TryStopLoopThread(_healthStop, _healthThread, TimeSpan.FromSeconds(2));
        _healthThread = null;
        if (!joined)
        {
            _logger?.Warning(AppLogCategory.Audio,
                "Audio health monitor did not stop within 2 s; leaking its wait handle rather " +
                "than disposing it under the still-running loop.");
        }
    }

    /// <summary>
    ///     Signal the loop, join it, and dispose the wait handle ONLY if the join
    ///     succeeded. Returns whether it did.
    ///     <para>
    ///         The unconditional dispose this replaces was a crash path, not the
    ///         "handle leak either way" its comment claimed.
    ///         <see cref="Stop" /> is the first thing
    ///         <c>JuceAudioPlaybackService.Dispose</c> does, i.e. while the engine is still alive and
    ///         possibly wedged; if <c>HealthPoll</c>'s P/Invoke blocked past the
    ///         join the event was disposed anyway, and the loop's next
    ///         <c>WaitOne</c> — at most <see cref="HealthPollIntervalMs" /> away —
    ///         threw <see cref="ObjectDisposedException" /> on a background thread.
    ///         That terminates the process. A leaked <c>WaitHandle</c> does not.
    ///     </para>
    ///     <para>Pure and static so a test can drive the timed-out join without an
    ///     engine — see <c>AudioHealthMonitorLoopTests</c>.</para>
    /// </summary>
    internal static bool TryStopLoopThread(EventWaitHandle stop, Thread? thread, TimeSpan joinTimeout)
    {
        try { stop.Set(); }
        catch (ObjectDisposedException) { return false; }

        bool joined;
        try { joined = thread is null || thread.Join(joinTimeout); }
        catch (ThreadStateException) { joined = true; }

        if (joined)
        {
            try { stop.Dispose(); } catch (ObjectDisposedException) { /* already gone */ }
        }

        return joined;
    }

    private void HealthLoop()
    {
        _hbLastTicks = Stopwatch.GetTimestamp();
        RunPollLoop(_healthStop, HealthPollIntervalMs, HealthPoll,
            ex => _logger?.Warning(AppLogCategory.Audio, $"Audio health poll failed: {ex.Message}"));
    }

    /// <summary>
    ///     The monitor loop. <b>The WaitOne is INSIDE the try</b> — as the loop
    ///     CONDITION, outside it, it would break the one guarantee the inner catch's
    ///     comment makes ("a monitor must never take the app down"): an
    ///     <see cref="ObjectDisposedException" /> from
    ///     the wait itself would escape the whole method and kill the process.
    ///     <para>Pure: the wait handle, the interval and both callbacks are
    ///     injected, so the disposed-under-us case is testable without an engine.</para>
    /// </summary>
    internal static void RunPollLoop(WaitHandle stop, int intervalMs, Action poll,
        Action<Exception> onPollError)
    {
        try
        {
            // WaitOne returns true when stop is signalled, false on the poll timeout.
            while (!stop.WaitOne(intervalMs))
            {
                try { poll(); }
                catch (Exception ex)
                {
                    // A monitor must never take the app down. Log once and keep going.
                    onPollError(ex);
                }
            }
        }
        catch (ObjectDisposedException)
        {
            // The stop handle went away under us — a teardown that could not join
            // us in time. That is the exit signal, not an error.
        }
        catch (Exception ex)
        {
            // Last line of defence. An unhandled exception on a background thread
            // is a process kill, and this thread exists only to write log lines.
            try { onPollError(ex); } catch { /* nothing left to try */ }
        }
    }

    private void HealthPoll()
    {
        if (_isDisposing()) return;
        // Feature switch = the Audio log category. Off ⇒ do nothing but sleep, and
        // forget any in-progress gap so re-enabling starts clean.
        if (_logger == null || !_logger.IsEnabled(AppLogCategory.Audio))
        {
            _dropoutDetector.Reset();
            // …and the rate window with it. Spanning a gap in which the callback
            // was not running at all measures a rate that never existed, and the
            // one it measures is always LOW — i.e. it reads as an under-run.
            _renderRate.Forget();
            return;
        }

        var engine = _engine();
        if (engine == null)
        {
            _dropoutDetector.Reset();
            // …and the rate window with it. Spanning a gap in which the callback
            // was not running at all measures a rate that never existed, and the
            // one it measures is always LOW — i.e. it reads as an under-run.
            _renderRate.Forget();
            return;
        }

        int sampleRate, blockFrames, latencySamples;
        bool clockValid;
        long hostTicks;
        try
        {
            clockValid = engine.TryGetRenderClock(out _, out hostTicks);
            sampleRate = engine.SampleRate;
            blockFrames = engine.BufferFrames;
            latencySamples = engine.OutputLatencySamples;
        }
        catch (ObjectDisposedException)
        {
            _dropoutDetector.Reset();
            // …and the rate window with it. Spanning a gap in which the callback
            // was not running at all measures a rate that never existed, and the
            // one it measures is always LOW — i.e. it reads as an under-run.
            _renderRate.Forget();
            return;
        }

        var nowTicks = Stopwatch.GetTimestamp();
        var poll = _dropoutDetector.Observe(clockValid, hostTicks, nowTicks,
            Stopwatch.Frequency, sampleRate, blockFrames);

        if (clockValid) LogConfigIfChanged(sampleRate, blockFrames, latencySamples);

        if (poll.GapStarted)
        {
            _burstWorstStalenessMs = poll.StalenessMs;
        }
        else if (poll.InGap)
        {
            if (poll.StalenessMs > _burstWorstStalenessMs) _burstWorstStalenessMs = poll.StalenessMs;
        }
        else if (poll.GapEnded)
        {
            _healthTotalDropouts++;
            _hbDropouts++;
            if (_burstWorstStalenessMs > _healthWorstStalenessMs) _healthWorstStalenessMs = _burstWorstStalenessMs;
            if (_burstWorstStalenessMs > _hbWorstStalenessMs) _hbWorstStalenessMs = _burstWorstStalenessMs;
            _logger.Warning(AppLogCategory.Audio,
                $"Audio dropout: the audio callback stalled ~{_burstWorstStalenessMs:F1} ms " +
                $"(block period {poll.BlockPeriodMs:F2} ms at {sampleRate} Hz / {blockFrames} frames). " +
                "Likely a buffer under-run — raise the buffer size or reduce CPU/DSP load if this repeats.");
        }

        MaybeHeartbeat(nowTicks, clockValid, sampleRate);
    }

    private void LogConfigIfChanged(int sampleRate, int blockFrames, int latencySamples)
    {
        if (sampleRate == _healthLastSampleRate &&
            blockFrames == _healthLastBlockFrames &&
            latencySamples == _healthLastLatencySamples)
        {
            return;
        }

        _healthLastSampleRate = sampleRate;
        _healthLastBlockFrames = blockFrames;
        _healthLastLatencySamples = latencySamples;

        var blockPeriodMs = sampleRate > 0 ? blockFrames * 1000.0 / sampleRate : 0;
        var latencyMs = sampleRate > 0 ? latencySamples * 1000.0 / sampleRate : 0;
        _logger?.Info(AppLogCategory.Audio,
            $"Audio device config: sampleRate={sampleRate} Hz, block={blockFrames} frames " +
            $"({blockPeriodMs:F2} ms), outputLatency={latencySamples} frames ({latencyMs:F1} ms).");
    }

    private void MaybeHeartbeat(long nowTicks, bool clockValid, int sampleRate)
    {
        var elapsedMs = (nowTicks - _hbLastTicks) * 1000.0 / Stopwatch.Frequency;
        if (elapsedMs < HealthHeartbeatMs) return;

        // Effective render rate over the window catches a SUSTAINED under-run the
        // per-poll staleness check can miss: render frames advancing slower than
        // wall-clock means the device kept falling behind real time.
        // null — not zero — whenever the clock could not be read: the engine went
        // away between the poll and here, or the read threw between devices. The
        // tracker's whole job is that the two stay distinguishable.
        long? renderFrames = null;
        var current = _engine();
        if (clockValid && current != null)
        {
            try
            {
                if (current.TryGetRenderClock(out var rf, out _)) renderFrames = rf;
            }
            catch (ObjectDisposedException) { /* between devices — skip the rate */ }
        }

        var reading = _renderRate.Observe(renderFrames, elapsedMs);

        ReportEngineXruns(sampleRate);

        if (_hbDropouts > 0 || reading.HaveRate)
        {
            var rate = reading.HaveRate
                ? $", renderRate≈{reading.EffectiveSampleRate:F0} Hz vs {sampleRate} Hz"
                : "";
            _logger?.Debug(AppLogCategory.Audio,
                $"Audio health: {_hbDropouts} dropout(s) in the last {elapsedMs / 1000:F0} s " +
                $"(worst ~{_hbWorstStalenessMs:F1} ms), {_healthTotalDropouts} since start " +
                $"(worst ~{_healthWorstStalenessMs:F1} ms){rate}.");

            if (AudioRenderRateTracker.IsSustainedUnderRun(reading, sampleRate))
            {
                _logger?.Warning(AppLogCategory.Audio,
                    $"Audio render rate {reading.EffectiveSampleRate:F0} Hz is off the device rate {sampleRate} Hz by " +
                    $">2% over {elapsedMs / 1000:F0} s — the audio thread is not keeping up (sustained under-run).");
            }
        }

        _hbDropouts = 0;
        _hbWorstStalenessMs = 0;
        _hbLastTicks = nowTicks;
    }

    /// <summary>
    ///     The attributed half of the heartbeat: what the audio CALLBACK counted,
    ///     and whose fault it was.
    ///
    ///     <para>
    ///         Read once per heartbeat rather than per poll — these are relaxed
    ///         atomics, so the read is cheap, but the counters are cumulative and
    ///         a per-poll delta would just be noise at 20 ms. Deliberately does NOT
    ///         call <c>ResetXrunStats</c>: the engine rebases them itself on every
    ///         device restart, and resetting here would destroy the counts for any
    ///         other reader.
    ///     </para>
    ///     <para>
    ///         Silent when nothing audible happened. A health log that prints a
    ///         line every 30 seconds saying everything is fine is a log nobody
    ///         reads when it stops being fine.
    ///     </para>
    /// </summary>
    private void ReportEngineXruns(int sampleRate)
    {
        var engine = _engine();
        if (engine == null)
        {
            // No engine ⇒ no baseline worth keeping. The next one starts its own
            // counters at zero, and diffing across that gap invents a window.
            _hbHaveXrun = false;
            return;
        }

        XrunStats current;
        try
        {
            if (!engine.TryGetXrunStats(out current)) return;
        }
        catch (ObjectDisposedException)
        {
            _hbHaveXrun = false;
            return;
        }

        // The first heartbeat only establishes the baseline: reporting the
        // process-lifetime totals as if they were this window's would blame the
        // current song for every dropout since launch.
        if (!_hbHaveXrun)
        {
            _hbLastXrun = current;
            _hbHaveXrun = true;
            return;
        }

        var window = AudioXrunAttribution.Delta(_hbLastXrun, current);
        _hbLastXrun = current;

        if (!window.HasDropouts) return;

        _logger?.Warning(AppLogCategory.Audio,
            AudioXrunAttribution.Describe(window, current, sampleRate, _healthLastBlockFrames));
    }
}
