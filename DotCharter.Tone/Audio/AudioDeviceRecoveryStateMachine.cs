namespace DotCharter.Tone.Audio;

/// <summary>
/// One watchdog-interval snapshot of the audio device world, fed into
/// <see cref="AudioDeviceRecoveryStateMachine.Tick"/>. Built by the playback
/// service from the live JUCE enumeration + engine liveness; kept as plain
/// booleans so the state machine stays hardware-free and unit-testable.
/// </summary>
/// <param name="DesiredDevicePresent">The configured output device name shows
/// up in the current enumeration for the configured driver. Note: ASIO names
/// come from the registry and typically persist while the hardware is
/// unplugged, so for ASIO this is usually <c>true</c>.</param>
/// <param name="EngineAlive">The engine exists and still reports an open
/// device (native <c>getCurrentAudioDevice() != null</c>).</param>
/// <param name="EngineOnDesiredDevice">The running engine is actually bound
/// to the configured driver + device (false while running on a fallback).</param>
/// <param name="TopologyChanged">The output-device enumeration differs from
/// the previous probe — a hot-plug event proxy. Arms retries so a
/// permanently-missing device can't cause periodic engine-restart glitches.</param>
public readonly record struct AudioDeviceProbe(
    bool DesiredDevicePresent,
    bool EngineAlive,
    bool EngineOnDesiredDevice,
    bool TopologyChanged);

public enum AudioDeviceRecoveryState
{
    /// <summary>Engine healthy (or parked on a fallback with no sign of the desired device).</summary>
    Healthy,
    /// <summary>Device loss detected; waiting for the configured device to come back.</summary>
    DeviceMissing,
    /// <summary>Device reappeared; requiring consecutive sightings before restarting (debounce).</summary>
    ReappearDebounce,
    /// <summary>A recovery attempt was handed to the caller and has not been resolved yet.</summary>
    RecoveryPending,
    /// <summary>Last recovery attempt failed; backing off before considering another one.</summary>
    Cooldown,
}

public enum AudioDeviceRecoveryAction
{
    None,
    /// <summary>Device loss just detected — log, stop playback, raise DeviceLost.</summary>
    ReportLost,
    /// <summary>Device is stably back — restart the engine with the saved configuration.
    /// The caller must resolve the attempt via NotifyRecoverySucceeded/Failed.</summary>
    AttemptRecovery,
}

/// <summary>
/// Pure state machine behind the audio-device disconnect/reconnect watchdog
/// (missing → waiting → reappeared, with debounce and failure backoff).
/// Owns no timers and touches no hardware: the playback service probes the
/// world every few seconds and feeds the result into <see cref="Tick"/>.
///
/// Key behaviors:
///  - A fresh loss arms one immediate recovery attempt (after debounce).
///  - Repeated unplug/replug cannot stack restarts: attempts require
///    <see cref="AudioDeviceRecoveryState.ReappearDebounce"/> consecutive
///    "present" probes and the machine parks in RecoveryPending until the
///    caller reports the outcome.
///  - After a FAILED attempt, further attempts are gated on a topology
///    change (some hardware — notably ASIO registry entries — never leaves
///    the enumeration, so presence alone must not retrigger restarts).
/// </summary>
public sealed class AudioDeviceRecoveryStateMachine
{
    private readonly int _debounceTicks;
    private readonly int _cooldownTicks;

    private int _debounce;
    private int _cooldown;
    // True when the machine is allowed to launch the next recovery attempt.
    // Set on a fresh loss and on every topology change; cleared when an
    // attempt starts so a failed attempt can't loop without new evidence.
    private bool _armed;

    public AudioDeviceRecoveryState State { get; private set; } = AudioDeviceRecoveryState.Healthy;

    /// <param name="debounceTicks">Consecutive "device present" probes required
    /// before a recovery attempt is issued (≥ 1).</param>
    /// <param name="cooldownTicks">Probes to skip after a failed recovery
    /// attempt before the machine returns to watching (≥ 1).</param>
    public AudioDeviceRecoveryStateMachine(int debounceTicks = 2, int cooldownTicks = 5)
    {
        _debounceTicks = Math.Max(1, debounceTicks);
        _cooldownTicks = Math.Max(1, cooldownTicks);
    }

    public AudioDeviceRecoveryAction Tick(AudioDeviceProbe probe)
    {
        switch (State)
        {
            case AudioDeviceRecoveryState.Healthy:
                if (!probe.EngineAlive
                    || (probe.EngineOnDesiredDevice && !probe.DesiredDevicePresent))
                {
                    // Engine died (device closed under it) or the configured
                    // device vanished from the enumeration.
                    State = AudioDeviceRecoveryState.DeviceMissing;
                    _armed = true;
                    _debounce = 0;
                    return AudioDeviceRecoveryAction.ReportLost;
                }
                if (!probe.EngineOnDesiredDevice
                    && probe.DesiredDevicePresent
                    && probe.TopologyChanged)
                {
                    // Running on a fallback device and the configured one just
                    // (re)appeared — head for recovery via the usual debounce.
                    State = AudioDeviceRecoveryState.ReappearDebounce;
                    _armed = true;
                    _debounce = 1;
                    if (_debounce >= _debounceTicks) return StartAttempt();
                }
                return AudioDeviceRecoveryAction.None;

            case AudioDeviceRecoveryState.DeviceMissing:
                if (probe.TopologyChanged) _armed = true;
                if (probe.DesiredDevicePresent && _armed)
                {
                    State = AudioDeviceRecoveryState.ReappearDebounce;
                    _debounce = 1;
                    if (_debounce >= _debounceTicks) return StartAttempt();
                }
                return AudioDeviceRecoveryAction.None;

            case AudioDeviceRecoveryState.ReappearDebounce:
                if (probe.TopologyChanged) _armed = true;
                if (!probe.DesiredDevicePresent)
                {
                    // Flapped away again — start over.
                    State = AudioDeviceRecoveryState.DeviceMissing;
                    _debounce = 0;
                    return AudioDeviceRecoveryAction.None;
                }
                _debounce++;
                if (_debounce >= _debounceTicks) return StartAttempt();
                return AudioDeviceRecoveryAction.None;

            case AudioDeviceRecoveryState.RecoveryPending:
                // The caller is (or should be) mid-restart; don't issue
                // anything until it reports back. Keep accumulating evidence.
                if (probe.TopologyChanged) _armed = true;
                return AudioDeviceRecoveryAction.None;

            case AudioDeviceRecoveryState.Cooldown:
                if (probe.TopologyChanged) _armed = true;
                if (--_cooldown <= 0)
                    State = AudioDeviceRecoveryState.DeviceMissing;
                return AudioDeviceRecoveryAction.None;

            default:
                return AudioDeviceRecoveryAction.None;
        }
    }

    /// <summary>The recovery attempt issued by <see cref="Tick"/> landed on the configured device.</summary>
    public void NotifyRecoverySucceeded()
    {
        if (State != AudioDeviceRecoveryState.RecoveryPending) return;
        State = AudioDeviceRecoveryState.Healthy;
        _debounce = 0;
        _cooldown = 0;
        _armed = false;
    }

    /// <summary>
    /// The recovery attempt failed (engine could not be restarted on the
    /// configured device). Back off; a new attempt requires the cooldown to
    /// elapse AND a subsequent topology change (fresh hot-plug evidence).
    /// </summary>
    public void NotifyRecoveryFailed()
    {
        if (State != AudioDeviceRecoveryState.RecoveryPending) return;
        State = AudioDeviceRecoveryState.Cooldown;
        _cooldown = _cooldownTicks;
        _debounce = 0;
    }

    /// <summary>
    /// The user applied a configuration manually (settings dialog / startup).
    /// Whatever the watchdog thought it knew is stale — start fresh.
    /// </summary>
    public void Reset()
    {
        State = AudioDeviceRecoveryState.Healthy;
        _debounce = 0;
        _cooldown = 0;
        _armed = false;
    }

    private AudioDeviceRecoveryAction StartAttempt()
    {
        State = AudioDeviceRecoveryState.RecoveryPending;
        _armed = false;
        _debounce = 0;
        return AudioDeviceRecoveryAction.AttemptRecovery;
    }
}
