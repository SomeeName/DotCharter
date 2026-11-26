using JuceSharp;
using JuceDriver = JuceSharp.AudioDriver;

namespace DotCharter.Tone.Audio;

/// <summary>
/// Builds the device watchdog's <see cref="AudioDeviceProbe"/>: whether the
/// engine is alive and on the configured device, whether that device is
/// present, and whether the device list changed. It owns the state those
/// answers need between ticks: the callback stall detector, the engine the
/// health monitor reported closed, the thinned healthy-tick enumeration and
/// the last device-list signature. <see cref="JuceAudioPlaybackService"/> owns
/// one and calls it from the watchdog tick only (one tick at a time).
/// </summary>
internal sealed class AudioDeviceProbeBuilder
{
    /// <summary>
    /// How often a HEALTHY watchdog tick still pays for a full device
    /// enumeration. Every 10th tick at the 3 s interval = twice a minute
    /// instead of twenty times.
    /// </summary>
    internal const int HealthyEnumerationEveryNTicks = 10;

    private readonly IAppLogger? _logger;
    private readonly double _healthyIntervalMs;
    private readonly AudioCallbackStallDetector _callbackStall = new();
    // The engine whose device the health monitor saw close and stay silent
    // (AudioDeviceClosedDetector). The next probe of that same engine counts it
    // as dead without waiting for the stall heartbeat; a rebuilt engine is a
    // different object, so the report never outlives the engine it is about.
    private volatile object? _closedDeviceEngine;
    private string? _outputTopologySignature;
    // Ticks since the last full device enumeration while healthy. 0 means "scan
    // on the next tick", which is what a return to health leaves behind.
    private int _healthyTickIndex;

    internal AudioDeviceProbeBuilder(IAppLogger? logger, double healthyIntervalMs)
    {
        _logger = logger;
        _healthyIntervalMs = healthyIntervalMs;
    }

    /// <summary>Records that <paramref name="engine"/> reported its device closed and stayed silent.</summary>
    internal void MarkDeviceClosed(object engine) => _closedDeviceEngine = engine;

    /// <summary>
    /// Forgets what the probe knew about the device world, after a manual apply:
    /// the next tick scans the devices and does not report the new list as a change
    /// from an old one.
    /// </summary>
    internal void Reset()
    {
        _outputTopologySignature = null;
        _healthyTickIndex = 0;
    }

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

    internal AudioDeviceProbe Build(
        JuceAudioEngine? engine, JuceEngineConfig? active, string desired, JuceDriver driver, AudioDeviceRecoveryState state)
    {
        var engineAlive = false;
        if (engine != null)
        {
            // 0 means JUCE dropped/closed the device out from under us. A
            // driver that just stops calling back (an unplugged ASIO interface)
            // keeps the cached rate, so a render clock that stopped advancing
            // counts as dead too — and so does an open device whose clock never
            // became readable (the ASIO reset after an unplug).
            var deviceOpen = engine.SampleRate > 0;
            var clockValid = engine.TryGetRenderClock(out var renderFrames, out _);
            var stalled = _callbackStall.Observe(engine, clockValid, renderFrames, deviceOpen);
            var reportedClosed = ReferenceEquals(_closedDeviceEngine, engine);
            engineAlive = deviceOpen && !stalled && !reportedClosed;
            if (stalled && !reportedClosed && state == AudioDeviceRecoveryState.Healthy)
            {
                _logger?.Warning(AppLogCategory.Audio,
                    "Audio device watchdog: the audio callback has not run for " +
                    $"{AudioCallbackStallDetector.StalledTicksBeforeDead * _healthyIntervalMs / 1000:0} s; treating the device as lost.");
            }
        }
        else
        {
            _callbackStall.Observe(null, clockValid: false, 0, deviceOpen: false);
        }

        var onDesired = engineAlive
            && active != null
            && active.PreferredDriver == driver
            && (string.IsNullOrEmpty(desired)
                || string.Equals(active.OutputDeviceId, desired, StringComparison.OrdinalIgnoreCase));

        if (!ShouldEnumerateDevices(state, engineAlive, onDesired, _healthyTickIndex))
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
}
