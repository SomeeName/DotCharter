using DotCharter.Localization;
using DotCharter.Tone.Diagnostics;
using Timer = System.Timers.Timer;

namespace DotCharter.Tone.Audio.Vst.Sandbox;

/// <summary>
/// Connects one seat's recovery to its service-owned lifecycle and slot events.
/// State snapshots take the shared sandbox gate; lifecycle changes hold the VST gate.
/// </summary>
internal sealed class VstSandboxRecoveryLink
{
    public string TaskId { get; init; } = "vst-sandbox-restart";

    /// <summary>Not disposing and the sandbox mode is on.</summary>
    public required Func<bool> IsActive { get; init; }

    /// <summary>True when the host is still THE sandbox child (not a stale instance); takes the sandbox gate.</summary>
    public required Func<IVstSandboxHost, bool> IsCurrentHost { get; init; }

    /// <summary>Latches "gave up" under the sandbox gate.</summary>
    public required Action MarkGaveUp { get; init; }

    /// <summary>Clears the slot table under the sandbox gate and raises the unload events.</summary>
    public required Action NotifySlotsUnloaded { get; init; }

    /// <summary>Slot ids (plus the instrument) the running child hosts now; takes the sandbox gate.</summary>
    public required Func<List<string>> LiveSlotIds { get; init; }

    /// <summary>The current child, or null; takes the sandbox gate.</summary>
    public required Func<IVstSandboxHost?> CurrentHost { get; init; }

    /// <summary>The native bridge's heartbeat counter; 0 while the bridge is disarmed or not yet beating. May throw.</summary>
    public required Func<uint> ReadHeartbeat { get; init; }

    /// <summary>How often the health monitor reads the heartbeat; two equal readings in a row kill the child as wedged.
    /// Read when the monitor starts, so a service can set it after its seats are built.</summary>
    public Func<TimeSpan> HealthInterval { get; init; } = () => DefaultHealthInterval;

    public static readonly TimeSpan DefaultHealthInterval = TimeSpan.FromSeconds(3);

    /// <summary>Tears the child and the bridge down. The caller holds the VST gate.</summary>
    public required Func<Task> ShutdownLocked { get; init; }

    /// <summary>Shuts down, respawns and reloads the chain and the instrument; null when the engine is down.
    /// The caller holds the VST gate.</summary>
    public required Func<Task<(IVstSandboxHost Host, IReadOnlyDictionary<string, string> States)?>> RebuildLocked { get; init; }
}

/// <summary>
/// Crash handling for the out-of-process VST sandbox: the restart attempts and their
/// give-up, plus the plugin state captured while healthy and replayed after a restart.
/// The in-flight flags and the crash budget live here; the gates stay with the service
/// (<c>_vstGate</c> is taken before anything the link does under it, the sandbox gate only
/// ever inside a short <c>lock</c>).
/// </summary>
internal sealed class VstSandboxRecovery
{
    private readonly object _sandboxGate;
    private readonly VstSandboxChainState _desired;
    private readonly SemaphoreSlim _vstGate;
    private readonly IAppLogger? _logger;
    private readonly ILocalizationService _localization;
    private readonly IStatusBarService? _statusBar;
    private readonly VstSandboxRecoveryLink _link;
    private readonly Func<TimeSpan, Task> _delay;
    private sealed class RestartRun(IVstSandboxHost host)
    {
        public IVstSandboxHost? ExpectedHost { get; set; } = host;
    }
    private RestartRun? _restartRun;

    private readonly VstSandboxRestartPolicy _policy = new();
    private readonly System.Diagnostics.Stopwatch _clock = System.Diagnostics.Stopwatch.StartNew();
    private int _restartInFlight;
    public bool IsRestartPending => Volatile.Read(ref _restartInFlight) != 0;
    internal Task PendingRestart { get; private set; } = Task.CompletedTask;
    // The host that exited while a restart was already in flight: the attempt reads it to
    // learn that its own new child died during the replay.
    private volatile IVstSandboxHost? _crashSeenWhileRestarting;
    private int _captureInFlight;
    internal Task PendingCapture { get; private set; } = Task.CompletedTask;
    private int _healthTicks;
    private Timer? _healthTimer;
    private uint _lastHeartbeat;
    private bool _heartbeatPrimed;

    public VstSandboxRecovery(object sandboxGate, VstSandboxChainState desired, SemaphoreSlim vstGate,
        IAppLogger? logger, ILocalizationService localization, IStatusBarService? statusBar, VstSandboxRecoveryLink link,
        Func<TimeSpan, Task>? delay = null)
    {
        _sandboxGate = sandboxGate;
        _desired = desired;
        _vstGate = vstGate;
        _logger = logger;
        _localization = localization;
        _statusBar = statusBar;
        _link = link;
        _delay = delay ?? (duration => Task.Delay(duration));
    }

    /// <summary>A fresh crash budget (sandbox mode switched on, or a user-driven plugin request after giving up).</summary>
    public void ResetBudget()
    {
        lock (_sandboxGate) _policy.Reset();
    }

    /// <summary>Invalidates pending work when an explicit edit takes ownership of this seat.</summary>
    public void CancelPendingRestart()
    {
        lock (_sandboxGate)
        {
            if (_restartRun != null) _statusBar?.CompleteTask(_link.TaskId);
            _restartRun = null;
            Volatile.Write(ref _restartInFlight, 0);
        }
    }

    /// <summary>The child's heartbeat moved outside a recovery attempt.</summary>
    public void NoteHealthy()
    {
        lock (_sandboxGate)
            if (_restartRun == null) _policy.OnHealthy(_clock.Elapsed);
    }

    public void OnCrashed(IVstSandboxHost crashed)
    {
        RestartRun run;
        TimeSpan? delay;
        lock (_sandboxGate)
        {
            if (!_link.IsActive() || !_link.IsCurrentHost(crashed)) return;
            if (_restartRun != null)
            {
                _crashSeenWhileRestarting = crashed;
                return;
            }
            run = new RestartRun(crashed);
            _restartRun = run;
            Volatile.Write(ref _restartInFlight, 1);
            delay = _policy.OnCrash(_clock.Elapsed);
        }
        _link.NotifySlotsUnloaded();
        if (delay != null) _logger?.Warning(AppLogCategory.Audio, _localization["Vst_SandboxCrashed"]);
        PendingRestart = Task.Run(() => RestartAsync(run, delay));
    }

    private bool Owns(RestartRun run)
    {
        lock (_sandboxGate)
        {
            return ReferenceEquals(_restartRun, run) && _link.IsActive()
                && ReferenceEquals(_link.CurrentHost(), run.ExpectedHost);
        }
    }

    private async Task RestartAsync(RestartRun run, TimeSpan? delay)
    {
        var taskId = _link.TaskId;
        try
        {
            if (delay == null)
            {
                await GiveUpAfterFailedRestartAsync(run).ConfigureAwait(false);
                return;
            }
            if (delay.Value > TimeSpan.Zero) await _delay(delay.Value).ConfigureAwait(false);
            if (!Owns(run)) return;
            _statusBar?.BeginTask(taskId, _localization["Vst_SandboxRestarting"], indeterminate: true);
            await VstSandboxRestartSupervisor.RunAsync(
                () => RestartAttemptAsync(run), _policy, () => _clock.Elapsed, _delay,
                (failure, backoff) => OnRestartAttemptFailed(run, failure, backoff),
                () => GiveUpAfterFailedRestartAsync(run), () => NextBackoff(run)).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            _logger?.Error(AppLogCategory.Audio, "VST sandbox restart failed", ex);
        }
        finally
        {
            lock (_sandboxGate)
            {
                if (ReferenceEquals(_restartRun, run))
                {
                    _statusBar?.CompleteTask(taskId);
                    _restartRun = null;
                    Volatile.Write(ref _restartInFlight, 0);
                }
            }
        }
    }

    private TimeSpan? NextBackoff(RestartRun run)
    {
        lock (_sandboxGate) return Owns(run) ? _policy.OnCrash(_clock.Elapsed) : null;
    }

    private async Task<bool> RestartAttemptAsync(RestartRun run)
    {
        if (!Owns(run)) return true;
        if (!await _vstGate.TryEnterAsync().ConfigureAwait(false)) return true;
        try
        {
            if (!Owns(run)) return true;
            _link.NotifySlotsUnloaded();
            (IVstSandboxHost Host, IReadOnlyDictionary<string, string> States)? replay;
            try { replay = await _link.RebuildLocked().ConfigureAwait(false); }
            finally { lock (_sandboxGate) run.ExpectedHost = _link.CurrentHost(); }
            if (replay is not var (host, states) || !Owns(run)) return true;
            await ReplayStateAsync(host, states).ConfigureAwait(false);
            if (!host.IsRunning || ReferenceEquals(_crashSeenWhileRestarting, host)) return false;
            _logger?.Warning(AppLogCategory.Audio, _localization["Vst_SandboxRestored"]);
            return true;
        }
        finally { _vstGate.Release(); }
    }

    private void OnRestartAttemptFailed(RestartRun run, Exception? failure, TimeSpan backoff)
    {
        if (!Owns(run)) return;
        _logger?.Warning(AppLogCategory.Audio,
            $"VST sandbox restart attempt failed ({failure?.Message ?? "the new child exited during the replay"}); retrying in {backoff.TotalSeconds:0.#} s.");
    }

    private async Task GiveUpAfterFailedRestartAsync(RestartRun run)
    {
        if (!await _vstGate.TryEnterAsync().ConfigureAwait(false)) return;
        try
        {
            if (!Owns(run)) return;
            _link.MarkGaveUp();
            _logger?.Error(AppLogCategory.Audio, _localization["Vst_SandboxGaveUp"]);
            _link.NotifySlotsUnloaded();
            await _link.ShutdownLocked().ConfigureAwait(false);
        }
        catch (Exception ex) { _logger?.Warning(AppLogCategory.Audio, "Sandbox shutdown after a failed restart failed", ex); }
        finally { _vstGate.Release(); }
    }

    // ------------------------------------------------------------------
    //  Plugin state: captured while healthy, replayed after a restart
    // ------------------------------------------------------------------

    /// <summary>Asks the running child for every live slot's state and stores the answers in
    /// the desired state. Skipped while a restart replays, when the slots hold defaults.</summary>
    public async Task CaptureStateAsync(IVstSandboxChannel host)
    {
        if (Volatile.Read(ref _restartInFlight) != 0) return;
        if (Interlocked.Exchange(ref _captureInFlight, 1) != 0) return;
        try
        {
            var live = _link.LiveSlotIds();
            var captured = await VstSandboxStateSync.CaptureAsync(host, live,
                (slot, ex) => _logger?.Warning(AppLogCategory.Audio, $"Sandbox getState({slot}) failed", ex))
                .ConfigureAwait(false);
            if (Volatile.Read(ref _restartInFlight) != 0) return;
            lock (_sandboxGate)
            {
                var wanted = _desired.Chain.Select(entry => entry.Id.ToString()).ToHashSet(StringComparer.Ordinal);
                if (!string.IsNullOrEmpty(_desired.InstrumentPath)) wanted.Add(VstSandboxProtocol.InstrumentSlotId);
                foreach (var stale in _desired.PluginStates.Keys.Where(key => !wanted.Contains(key)).ToList())
                    _desired.PluginStates.Remove(stale);
                foreach (var (slot, state) in captured)
                    if (wanted.Contains(slot)) _desired.PluginStates[slot] = state;
            }
        }
        finally { Interlocked.Exchange(ref _captureInFlight, 0); }
    }

    /// <summary>Sends the captured state of every slot that is loaded again after a restart.</summary>
    public async Task ReplayStateAsync(IVstSandboxChannel host, IReadOnlyDictionary<string, string> states)
    {
        if (states.Count == 0) return;
        var restored = await VstSandboxStateSync.ReplayAsync(host, states, _link.LiveSlotIds(),
            (slot, ex) => _logger?.Warning(AppLogCategory.Audio, $"Sandbox setState({slot}) failed", ex))
            .ConfigureAwait(false);
        _logger?.Info(AppLogCategory.Audio, $"VST sandbox: restored plugin state for {restored} slot(s).");
    }

    public void CaptureStateInBackground(IVstSandboxHost host)
        => PendingCapture = Task.Run(() => CaptureStateAsync(host));

    // ------------------------------------------------------------------
    //  Health monitor (hung-but-alive children)
    // ------------------------------------------------------------------

    /// <summary>The next heartbeat reading primes the monitor again (a fresh child was spawned).</summary>
    public void ResetHeartbeat() => _heartbeatPrimed = false;

    // Both of these run under the service's _vstGate (spawn / shutdown / dispose are all
    // gated callers), which is what keeps the null-check-then-assign from racing itself
    // and leaking a live timer.
    public void EnsureHealthTimer()
    {
        if (_healthTimer != null) return;
        var timer = new Timer(_link.HealthInterval().TotalMilliseconds) { AutoReset = true };
        timer.Elapsed += (_, _) => HealthTick();
        timer.Start();
        _healthTimer = timer;
    }

    public void StopHealthTimer()
    {
        var timer = _healthTimer;
        _healthTimer = null;
        if (timer == null) return;
        try { timer.Stop(); timer.Dispose(); } catch { /* ignored */ }
    }

    private void HealthTick()
    {
        if (!_link.IsActive()) return;
        var host = _link.CurrentHost();
        if (host is not { IsRunning: true }) return;

        try
        {
            var heartbeat = _link.ReadHeartbeat();
            if (heartbeat == 0) return; // bridge disarmed / not yet beating

            if (!_heartbeatPrimed)
            {
                _heartbeatPrimed = true;
                _lastHeartbeat = heartbeat;
                return;
            }
            if (heartbeat != _lastHeartbeat)
            {
                _lastHeartbeat = heartbeat;
                NoteHealthy();
                // Every ~30 s while healthy, so a crash loses at most that much tweaking.
                if (++_healthTicks % 10 == 0) CaptureStateInBackground(host);
                return;
            }
            // The worker beats ~10x/s even when idle; two identical readings
            // 3 s apart mean the child is alive but wedged. Kill it — the
            // Exited handler takes the normal crash/restart path.
            _logger?.Warning(AppLogCategory.Audio,
                "VST sandbox heartbeat stalled — terminating the wedged child for restart.");
            _heartbeatPrimed = false;
            try { host.Kill(); } catch { /* ignored */ }
            OnCrashed(host);
        }
        catch { /* stats are best-effort */ }
    }
}
