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

    private readonly Timer _watchdogTimer;
    private readonly AudioDeviceRecoveryStateMachine _recovery = new(debounceTicks: 2, cooldownTicks: 5);
    private readonly AudioCallbackStallDetector _callbackStall = new();

    // Last config handed to ApplyAudioConfiguration — the recovery target.
    private volatile RigConfig? _watchdogConfig;
    // > 0 while ApplyAudioConfiguration runs (manual apply OR watchdog
    // recovery). The probe skips its tick entirely in that window.
    private int _configApplyDepth;
    // Reentrancy guard: System.Timers.Timer fires on the thread pool and a
    // recovery attempt (full engine rebuild) can easily outlast one interval.
    private int _watchdogBusy;
    private string? _outputTopologySignature;
    private volatile bool _deviceLost;
    // Ticks since the last full device enumeration while healthy. 0 means "scan
    // on the next tick", which is what a return to health leaves behind.
    private int _healthyTickIndex;

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
                    HandleDeviceLost(cfg);
                    break;
                case AudioDeviceRecoveryAction.AttemptRecovery:
                    AttemptDeviceRecovery(cfg);
                    break;
            }
        }
        catch (Exception ex)
        {
            _logger?.Warning(AppLogCategory.Audio, $"Audio device watchdog tick failed: {ex.Message}");
        }
        finally
        {
            Interlocked.Exchange(ref _watchdogBusy, 0);
        }
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

    /// <summary>
    /// How often a HEALTHY watchdog tick still pays for a full device
    /// enumeration. Every 10th tick at the 3 s interval = twice a minute
    /// instead of twenty times.
    /// </summary>
    internal const int HealthyEnumerationEveryNTicks = 10;

    /// <summary>
    /// Whether this tick needs the (expensive) live device enumeration.
    /// <para>
    /// `InitializeAsync` explains why this is not free: the startup device probe
    /// was REMOVED because "an ASIO scan costs several hundred ms" and because
    /// holding ASIO driver references from a scan made the real engine's ASIO
    /// open fail with "No such device". The watchdog then re-introduced exactly
    /// that scan, on a pool thread, twenty times a minute, for the life of the
    /// process.
    /// </para>
    /// <para>
    /// It is only genuinely NEEDED when something is already off: the engine is
    /// dead, or it is parked on a fallback device and we are watching for the
    /// configured one to reappear, or the recovery state machine is mid-flight.
    /// While the engine is alive AND bound to the configured device, the cheap
    /// liveness signal (`engine.SampleRate > 0`, one P/Invoke) is what actually
    /// catches an unplug — JUCE closes the device and the rate drops to 0 — so
    /// the enumeration is thinned rather than dropped: it still runs
    /// periodically, which keeps the narrow "endpoint vanished but JUCE has not
    /// noticed yet" case detectable, just a few ticks later.
    /// </para>
    /// Pure so it can be unit-tested without a device.
    /// </summary>
    internal static bool ShouldEnumerateDevices(
        AudioDeviceRecoveryState state, bool engineAlive, bool engineOnDesiredDevice, int healthyTickIndex)
    {
        if (state != AudioDeviceRecoveryState.Healthy) return true;
        if (!engineAlive || !engineOnDesiredDevice) return true;
        return healthyTickIndex % HealthyEnumerationEveryNTicks == 0;
    }

    private AudioDeviceProbe BuildDeviceProbe(RigConfig cfg)
    {
        var (desired, driver) = DesiredOutputDevice(cfg);

        var engine = _engine;
        var engineAlive = false;
        if (engine != null)
        {
            // 0 means JUCE dropped/closed the device out from under us. A
            // driver that just stops calling back (an unplugged ASIO interface)
            // keeps the cached rate, so a render clock that stopped advancing
            // counts as dead too.
            var clockValid = engine.TryGetRenderClock(out var renderFrames, out _);
            var stalled = _callbackStall.Observe(engine, clockValid, renderFrames);
            engineAlive = engine.SampleRate > 0 && !stalled;
            if (stalled && _recovery.State == AudioDeviceRecoveryState.Healthy)
            {
                _logger?.Warning(AppLogCategory.Audio,
                    "Audio device watchdog: the audio callback has not run for " +
                    $"{AudioCallbackStallDetector.StalledTicksBeforeDead * DeviceWatchdogIntervalMs / 1000:0} s; treating the device as lost.");
            }
        }
        else
        {
            _callbackStall.Observe(null, clockValid: false, 0);
        }

        var active = _activeEngineConfig;
        var onDesired = engineAlive
            && active != null
            && active.PreferredDriver == driver
            && (string.IsNullOrEmpty(desired)
                || string.Equals(active.OutputDeviceId, desired, StringComparison.OrdinalIgnoreCase));

        if (!ShouldEnumerateDevices(_recovery.State, engineAlive, onDesired, _healthyTickIndex))
        {
            _healthyTickIndex++;
            // Skipping the scan means asserting what the open device already
            // proves: JUCE is holding the configured device, so it is present.
            // No enumeration ⇒ no topology delta to report either, and
            // `_outputTopologySignature` is deliberately left untouched so the
            // next real scan still compares against the last one we saw.
            return new AudioDeviceProbe(
                DesiredDevicePresent: true, engineAlive, onDesired, TopologyChanged: false);
        }
        _healthyTickIndex = 1; // this tick paid for the scan; count from the next one

        IReadOnlyList<JuceAudioDevice> outs;
        try
        {
            outs = engine != null
                ? engine.EnumerateOutputDevicesLive()
                : JuceAudioEngine.EnumerateOutputDevices();
        }
        catch (ObjectDisposedException) { throw; }
        catch { outs = Array.Empty<JuceAudioDevice>(); }

        var present = string.IsNullOrEmpty(desired)
            // No name saved = "driver default": any device of that driver counts.
            ? outs.Any(d => d.Driver == driver)
            : outs.Any(d => d.Driver == driver
                            && string.Equals(d.Name, desired, StringComparison.OrdinalIgnoreCase));

        var signature = BuildTopologySignature(outs);
        var topologyChanged = !string.Equals(signature, _outputTopologySignature, StringComparison.Ordinal);
        _outputTopologySignature = signature;

        return new AudioDeviceProbe(present, engineAlive, onDesired, topologyChanged);
    }

    private static string BuildTopologySignature(IReadOnlyList<JuceAudioDevice> devices)
    {
        if (devices.Count == 0) return string.Empty;
        return string.Join("\n", devices
            .Select(d => $"{(int)d.Driver}|{d.Name}")
            .OrderBy(s => s, StringComparer.Ordinal));
    }

    private void HandleDeviceLost(RigConfig cfg)
    {
        _deviceLost = true;
        var (desired, driver) = DesiredOutputDevice(cfg);
        _logger?.Warning(AppLogCategory.Audio,
            $"Audio device lost (driver={driver}, output='{desired}'). " +
            "Playback paused; the engine will be restarted automatically when the device returns.");
        // Stop the transport + flush hanging MIDI notes so the app stays
        // fully usable while the device is gone. The loaded song remains
        // loaded — ReplaceEngine rehydrates it on recovery.
        try { Pause(); } catch { /* ignored */ }
        try { DeviceLost?.Invoke(this, EventArgs.Empty); }
        catch (Exception ex)
        {
            _logger?.Warning(AppLogCategory.Audio, $"DeviceLost subscriber threw: {ex.Message}");
        }
    }

    private void AttemptDeviceRecovery(RigConfig cfg)
    {
        var (desired, driver) = DesiredOutputDevice(cfg);
        _logger?.Info(AppLogCategory.Audio,
            $"Audio device watchdog: '{desired}' ({driver}) is available again — restarting the engine with the saved configuration.");

        var recovered = false;
        try
        {
            // Full replace on purpose: the in-place reconfigure path can
            // no-op against a dead device (JUCE sees no setup delta).
            ApplyAudioConfiguration(cfg, forceEngineReplace: true);
            var probe = BuildDeviceProbe(cfg);
            recovered = probe.EngineAlive && probe.EngineOnDesiredDevice;
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
                _logger?.Warning(AppLogCategory.Audio, $"DeviceReconnected subscriber threw: {ex.Message}");
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
