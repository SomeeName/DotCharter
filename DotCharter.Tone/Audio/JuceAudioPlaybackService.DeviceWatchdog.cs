// Partial: the device watchdog — a periodic presence/liveness probe of the output device and the auto-recovery after an unplug.
using JuceSharp;
using JuceDriver = JuceSharp.AudioDriver;
using Timer = System.Timers.Timer;

namespace DotCharter.Tone.Audio;

/// <summary>
/// Audio-interface disconnect/reconnect auto-recovery. A periodic managed
/// watchdog (no native callback, no ABI change) probes device liveness:
///
///  - Device presence: the configured output device name (matched BY NAME,
///    same fields RigConfig persists) is looked up in the live JUCE
///    enumeration. WASAPI re-scans actual endpoints, so hot-unplug/-replug
///    is reflected. ASIO names come from the registry and usually persist
///    while the hardware is unplugged — for those, recovery is driven by the
///    engine-liveness signal plus enumeration-topology changes (the
///    interface's WASAPI endpoints appearing is the hot-plug proxy).
///  - Engine liveness: native <c>dca_engine_get_sample_rate</c> returns 0
///    when JUCE has no open device (e.g. an ASIO reset that failed
///    after the hardware vanished).
///
/// When loss is detected we pause playback, raise <see cref="DeviceLost"/>
/// and keep polling; once the configured device is stably back (debounced by
/// <see cref="AudioDeviceRecoveryStateMachine"/>) the engine is rebuilt via
/// the normal ApplyAudioConfiguration path (forced full replace) — the
/// loaded song, stems, VSTs and mixer state are restored by the existing
/// ReplaceEngine rehydration logic (the stem half of that claim was FALSE
/// until the snapshot/restore in <c>RestoreStemsAfterEngineRebuild</c>: the
/// teardown cleared the stem dictionary and nothing re-issued it) — and
/// <see cref="DeviceReconnected"/>
/// fires so the UI can refresh. Manual settings-dialog restarts are never
/// fought: ticks are skipped while an apply is in flight, and every manual
/// apply resets the watchdog.
/// </summary>
public sealed partial class JuceAudioPlaybackService
{
    private const double DeviceWatchdogIntervalMs = 3000;
    // While the device is lost the watchdog checks once a second, so a replug
    // is noticed right away. The rebuild attempts themselves stay rare:
    // the counts below are in these one-second probes, so a failed attempt backs
    // off for 15 s and a dead engine with no hot-plug evidence retries every 30 s.
    private const double LostDeviceWatchdogIntervalMs = 1000;

    private readonly Timer _watchdogTimer;
    private readonly AudioDeviceRecoveryStateMachine _recovery = new(debounceTicks: 2, cooldownTicks: 15, deadRetryTicks: 30);
    private readonly AudioDeviceProbeBuilder _probe;

    // Last config handed to ApplyAudioConfiguration — the recovery target.
    private volatile RigConfig? _watchdogConfig;
    // > 0 while ApplyAudioConfiguration runs (manual apply OR watchdog
    // recovery). The probe skips its tick entirely in that window.
    private int _configApplyDepth;
    // Reentrancy guard: System.Timers.Timer fires on the thread pool and a
    // recovery attempt (full engine rebuild) can easily outlast one interval.
    private int _watchdogBusy;
    private volatile bool _deviceLost;

    /// <summary>
    /// Bounded wait for an in-flight <see cref="WatchdogTick"/> to leave the
    /// handler. <c>Timer.Stop()</c>/<c>Dispose()</c> only stops FUTURE ticks —
    /// a recovery already running walks the full engine-rebuild path, so
    /// <c>Dispose</c> has to see <c>_watchdogBusy</c> back at 0 before it
    /// destroys the engine. Returns false when the budget expires (a wedged
    /// native probe); the caller proceeds anyway, because the
    /// <c>IsDisposing</c> bails in <c>ReplaceEngineGated</c> keep a late tick
    /// from publishing an engine regardless.
    /// </summary>
    internal bool WaitForWatchdogIdle(TimeSpan budget)
    {
        var deadline = Environment.TickCount64 + (long)budget.TotalMilliseconds;
        while (Volatile.Read(ref _watchdogBusy) != 0)
        {
            if (Environment.TickCount64 >= deadline)
            {
                _logger?.Warning(AppLogCategory.Audio,
                    $"Audio device watchdog still busy after {budget.TotalSeconds:0.#} s at dispose; continuing.");
                return false;
            }
            Thread.Sleep(5);
        }
        return true;
    }

    private void WatchdogTick()
    {
        if (IsDisposing) return;
        if (Interlocked.CompareExchange(ref _watchdogBusy, 1, 0) != 0) return;
        try
        {
            if (IsDisposing) return;
            if (Volatile.Read(ref _configApplyDepth) > 0) return;
            var cfg = _watchdogConfig;
            if (cfg == null) return; // nothing applied yet — nothing to watch

            AudioDeviceProbe probe;
            try
            {
                probe = BuildDeviceProbe(cfg);
            }
            catch (ObjectDisposedException)
            {
                // Engine swapped out from under us (manual restart racing the
                // depth check). Skip this tick; the next one sees fresh state.
                return;
            }

            switch (_recovery.Tick(probe))
            {
                case AudioDeviceRecoveryAction.ReportLost:
                    DeclareDeviceLost(cfg);
                    break;
                case AudioDeviceRecoveryAction.AttemptRecovery:
                    AttemptDeviceRecovery(cfg);
                    break;
            }
        }
        catch (Exception ex)
        {
            _logger?.Warning(AppLogCategory.Audio, "Audio device watchdog tick failed", ex);
        }
        finally
        {
            Interlocked.Exchange(ref _watchdogBusy, 0);
            FollowRecoveryStateWithTheWatchdogInterval();
        }
    }

    /// <summary>How often the watchdog checks the device in each recovery state:
    /// every 3 s while healthy, every second while the device is lost.</summary>
    internal static double WatchdogIntervalFor(AudioDeviceRecoveryState state) =>
        state == AudioDeviceRecoveryState.Healthy ? DeviceWatchdogIntervalMs : LostDeviceWatchdogIntervalMs;

    private void FollowRecoveryStateWithTheWatchdogInterval()
    {
        var wanted = WatchdogIntervalFor(_recovery.State);
        try
        {
            // Setting the interval restarts the timer, so only set it when it changes.
            if (_watchdogTimer.Interval != wanted) _watchdogTimer.Interval = wanted;
        }
        catch (ObjectDisposedException)
        {
            // Dispose stopped the timer while this tick ran; there is nothing left to pace.
        }
    }

    /// <summary>
    /// Called by the health monitor's thread when <paramref name="engine"/>
    /// reported its device closed and it stayed silent. Marks that engine
    /// dead and runs a watchdog check right away on the thread pool, so the loss
    /// is declared about a second after the unplug instead of after the stall
    /// heartbeat. Ignored for an engine that is not the current one, and while a
    /// settings apply or rebuild is running (those close the device on purpose).
    /// When a check is already running, the mark stays and the next one sees it.
    /// </summary>
    private void OnEngineReportedDeviceClosed(JuceAudioEngine engine)
    {
        if (IsDisposing || !ReferenceEquals(engine, _engine)) return;
        if (Volatile.Read(ref _configApplyDepth) > 0) return;
        _probe.MarkDeviceClosed(engine);
        _logger?.Warning(AppLogCategory.Audio,
            "Audio device watchdog: the engine reported its device closed and no audio callback followed; checking the device now.");
        ThreadPool.UnsafeQueueUserWorkItem(static service => service.WatchdogTick(), this, preferLocal: false);
    }

    private static (string BareName, JuceDriver Driver) DesiredOutputDevice(RigConfig cfg)
    {
        var driver = cfg.AudioDriver.Equals("ASIO", StringComparison.OrdinalIgnoreCase)
            ? JuceDriver.Asio
            : JuceDriver.Wasapi;
        var name = ResolveOutputDeviceId(cfg, driver);
        if (driver == JuceDriver.Wasapi)
            (name, _) = ParseWasapiLabel(name);
        return (name ?? string.Empty, driver);
    }

    private AudioDeviceProbe BuildDeviceProbe(RigConfig cfg)
    {
        var (desired, driver) = DesiredOutputDevice(cfg);
        return _probe.Build(_engine, _activeEngineConfig, desired, driver, _recovery.State);
    }

    /// <summary>Budget for a rebuilt engine's first callbacks before a recovery counts as failed.</summary>
    private const int RecoveryCallbackBudgetMs = 2000;

    private bool NewEngineCallsBack()
    {
        var engine = _engine;
        if (engine == null) return false;
        return AudioCallbackStallDetector.WaitForCallbackProgress(
            () => (engine.TryGetRenderClock(out var frames, out _), frames),
            () => Environment.TickCount64,
            Thread.Sleep,
            RecoveryCallbackBudgetMs,
            pollMs: 20);
    }

    /// <summary>
    /// The device is gone: stop the transport clock, tell the UI, then pause the
    /// native sources. The managed transport stops FIRST because pausing a
    /// source whose device has stopped calling back waits for a callback that never
    /// comes (JUCE's transport stop polls for up to a second per source), and a
    /// running transport clock extrapolates the playhead with no audio clock
    /// behind it, which the user sees as lag and jitter. The loaded song stays loaded;
    /// ReplaceEngine brings it back on recovery.
    /// </summary>
    internal void DeclareDeviceLost(RigConfig cfg)
    {
        _deviceLost = true;
        var (desired, driver) = DesiredOutputDevice(cfg);
        _logger?.Warning(AppLogCategory.Audio,
            $"Audio device lost (driver={driver}, output='{desired}'). " +
            "Playback paused; the engine will be restarted automatically when the device returns.");
        StopTransportClock();
        try { DeviceLost?.Invoke(this, EventArgs.Empty); }
        catch (Exception ex)
        {
            _logger?.Warning(AppLogCategory.Audio, "DeviceLost subscriber threw", ex);
        }
        // Then the native sources and the hanging MIDI notes.
        try { Pause(); } catch { /* ignored */ }
    }

    private void AttemptDeviceRecovery(RigConfig cfg)
    {
        var (desired, driver) = DesiredOutputDevice(cfg);
        _logger?.Info(AppLogCategory.Audio,
            $"Audio device watchdog: '{desired}' ({driver}) is available again — restarting the engine with the saved configuration.");

        var recovered = false;
        var started = Environment.TickCount64;
        try
        {
            // Full replace on purpose: the in-place reconfigure path can
            // no-op against a dead device (JUCE sees no setup delta). The
            // channel counts come from the last healthy apply: rescanning
            // every driver through a dead engine is slow and tells us nothing new.
            ApplyAudioConfiguration(cfg, forceEngineReplace: true, reuseRememberedChannelCounts: true);
            _logger?.Info(AppLogCategory.Audio,
                $"Audio device watchdog: the engine rebuild took {Environment.TickCount64 - started} ms.");
            var probe = BuildDeviceProbe(cfg);
            // A fresh engine is never "stalled" on its first probe, so the
            // probe alone would call a driver that opened but never calls back
            // (hardware still unplugged) a recovery. Require the callback to run.
            recovered = probe.EngineAlive && probe.EngineOnDesiredDevice && NewEngineCallsBack();
        }
        catch (Exception ex)
        {
            _logger?.Error(AppLogCategory.Audio, "Audio device watchdog: engine restart threw.", ex);
        }

        if (recovered)
        {
            _recovery.NotifyRecoverySucceeded();
            _deviceLost = false;
            var engine = _engine;
            int sr = 0, buf = 0;
            try { sr = engine?.SampleRate ?? 0; buf = engine?.BufferFrames ?? 0; } catch { /* ignored */ }
            var name = _activeEngineConfig?.OutputDeviceId;
            if (string.IsNullOrEmpty(name)) name = desired;
            _logger?.Info(AppLogCategory.Audio,
                $"Audio device recovered. output='{name}', sr={sr}, buf={buf}.");
            try { DeviceReconnected?.Invoke(this, new AudioDeviceReconnectedEventArgs(name ?? string.Empty, sr, buf)); }
            catch (Exception ex)
            {
                _logger?.Warning(AppLogCategory.Audio, "DeviceReconnected subscriber threw", ex);
            }
        }
        else
        {
            _recovery.NotifyRecoveryFailed();
            _logger?.Warning(AppLogCategory.Audio,
                $"Audio device watchdog: engine restart did not land on '{desired}' ({driver}); backing off before the next attempt.");
        }
    }
}
