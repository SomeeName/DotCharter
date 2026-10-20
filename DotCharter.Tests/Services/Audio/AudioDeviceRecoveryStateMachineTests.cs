using DotCharter.Tone.Audio;

namespace DotCharter.Tests.Services.Audio;

public sealed class AudioDeviceRecoveryStateMachineTests
{
    private static AudioDeviceProbe Healthy(bool topologyChanged = false) => new(
        DesiredDevicePresent: true,
        EngineAlive: true,
        EngineOnDesiredDevice: true,
        TopologyChanged: topologyChanged);

    private static AudioDeviceProbe Unplugged(bool topologyChanged = false) => new(
        DesiredDevicePresent: false,
        EngineAlive: true,
        EngineOnDesiredDevice: true,
        TopologyChanged: topologyChanged);

    private static AudioDeviceProbe Replugged(bool topologyChanged = false) => new(
        DesiredDevicePresent: true,
        EngineAlive: true,
        EngineOnDesiredDevice: true,
        TopologyChanged: topologyChanged);

    private static AudioDeviceProbe EngineDead(bool present = true, bool topologyChanged = false) => new(
        DesiredDevicePresent: present,
        EngineAlive: false,
        EngineOnDesiredDevice: false,
        TopologyChanged: topologyChanged);

    private static AudioDeviceProbe OnFallback(bool present, bool topologyChanged = false) => new(
        DesiredDevicePresent: present,
        EngineAlive: true,
        EngineOnDesiredDevice: false,
        TopologyChanged: topologyChanged);

    [Fact]
    public void Healthy_device_stays_healthy()
    {
        var sm = new AudioDeviceRecoveryStateMachine();
        for (var i = 0; i < 10; i++)
            Assert.Equal(AudioDeviceRecoveryAction.None, sm.Tick(Healthy(topologyChanged: i == 0)));
        Assert.Equal(AudioDeviceRecoveryState.Healthy, sm.State);
    }

    [Fact]
    public void Unplug_reports_lost_once()
    {
        var sm = new AudioDeviceRecoveryStateMachine();
        Assert.Equal(AudioDeviceRecoveryAction.ReportLost, sm.Tick(Unplugged(topologyChanged: true)));
        Assert.Equal(AudioDeviceRecoveryState.DeviceMissing, sm.State);
        // Still gone: no further ReportLost spam.
        Assert.Equal(AudioDeviceRecoveryAction.None, sm.Tick(Unplugged()));
        Assert.Equal(AudioDeviceRecoveryAction.None, sm.Tick(Unplugged()));
    }

    [Fact]
    public void Engine_death_with_device_still_listed_reports_lost()
    {
        // ASIO case: the registry keeps the name in the enumeration but JUCE
        // closed the device after a failed reset.
        var sm = new AudioDeviceRecoveryStateMachine();
        Assert.Equal(AudioDeviceRecoveryAction.ReportLost, sm.Tick(EngineDead(present: true)));
        Assert.Equal(AudioDeviceRecoveryState.DeviceMissing, sm.State);
    }

    [Fact]
    public void Replug_attempts_recovery_after_debounce()
    {
        var sm = new AudioDeviceRecoveryStateMachine(debounceTicks: 2);
        sm.Tick(Unplugged(topologyChanged: true));
        // First sighting: debounce, no attempt yet.
        Assert.Equal(AudioDeviceRecoveryAction.None, sm.Tick(Replugged(topologyChanged: true)));
        Assert.Equal(AudioDeviceRecoveryState.ReappearDebounce, sm.State);
        // Second consecutive sighting: go.
        Assert.Equal(AudioDeviceRecoveryAction.AttemptRecovery, sm.Tick(Replugged()));
        Assert.Equal(AudioDeviceRecoveryState.RecoveryPending, sm.State);
    }

    [Fact]
    public void Flapping_presence_resets_debounce()
    {
        var sm = new AudioDeviceRecoveryStateMachine(debounceTicks: 2);
        sm.Tick(Unplugged(topologyChanged: true));
        Assert.Equal(AudioDeviceRecoveryAction.None, sm.Tick(Replugged(topologyChanged: true)));
        // Gone again before the debounce completed.
        Assert.Equal(AudioDeviceRecoveryAction.None, sm.Tick(Unplugged(topologyChanged: true)));
        Assert.Equal(AudioDeviceRecoveryState.DeviceMissing, sm.State);
        // Needs the full debounce again.
        Assert.Equal(AudioDeviceRecoveryAction.None, sm.Tick(Replugged(topologyChanged: true)));
        Assert.Equal(AudioDeviceRecoveryAction.AttemptRecovery, sm.Tick(Replugged()));
    }

    [Fact]
    public void Recovery_success_returns_to_healthy()
    {
        var sm = new AudioDeviceRecoveryStateMachine(debounceTicks: 1);
        sm.Tick(Unplugged(topologyChanged: true));
        Assert.Equal(AudioDeviceRecoveryAction.AttemptRecovery, sm.Tick(Replugged(topologyChanged: true)));
        sm.NotifyRecoverySucceeded();
        Assert.Equal(AudioDeviceRecoveryState.Healthy, sm.State);
        Assert.Equal(AudioDeviceRecoveryAction.None, sm.Tick(Healthy()));
    }

    [Fact]
    public void Recovery_failure_backs_off_and_needs_topology_change_to_retry()
    {
        var sm = new AudioDeviceRecoveryStateMachine(debounceTicks: 1, cooldownTicks: 2);
        sm.Tick(EngineDead(present: true));
        Assert.Equal(AudioDeviceRecoveryAction.AttemptRecovery, sm.Tick(EngineDead(present: true)));
        sm.NotifyRecoveryFailed();
        Assert.Equal(AudioDeviceRecoveryState.Cooldown, sm.State);

        // Cooldown drains without issuing anything.
        Assert.Equal(AudioDeviceRecoveryAction.None, sm.Tick(EngineDead(present: true)));
        Assert.Equal(AudioDeviceRecoveryAction.None, sm.Tick(EngineDead(present: true)));
        Assert.Equal(AudioDeviceRecoveryState.DeviceMissing, sm.State);

        // Device "present" forever (ASIO registry ghost) but nothing changed:
        // never retry — this is what prevents periodic restart glitches.
        for (var i = 0; i < 10; i++)
            Assert.Equal(AudioDeviceRecoveryAction.None, sm.Tick(EngineDead(present: true)));

        // Real hot-plug (topology delta) re-arms the retry.
        Assert.Equal(AudioDeviceRecoveryAction.AttemptRecovery,
            sm.Tick(EngineDead(present: true, topologyChanged: true)));
    }

    [Fact]
    public void Topology_change_during_cooldown_arms_retry_after_cooldown()
    {
        var sm = new AudioDeviceRecoveryStateMachine(debounceTicks: 1, cooldownTicks: 3);
        sm.Tick(EngineDead());
        Assert.Equal(AudioDeviceRecoveryAction.AttemptRecovery, sm.Tick(EngineDead()));
        sm.NotifyRecoveryFailed();

        // Hot-plug happens while we're still cooling down.
        Assert.Equal(AudioDeviceRecoveryAction.None, sm.Tick(EngineDead(topologyChanged: true)));
        Assert.Equal(AudioDeviceRecoveryAction.None, sm.Tick(EngineDead()));
        Assert.Equal(AudioDeviceRecoveryAction.None, sm.Tick(EngineDead()));
        Assert.Equal(AudioDeviceRecoveryState.DeviceMissing, sm.State);

        // The armed flag survived the cooldown: next present probe retries.
        Assert.Equal(AudioDeviceRecoveryAction.AttemptRecovery, sm.Tick(EngineDead()));
    }

    [Fact]
    public void Fallback_engine_with_desired_device_appearing_triggers_recovery()
    {
        // Boot fell back to WASAPI default because the interface was
        // unplugged; the user plugs it in later.
        var sm = new AudioDeviceRecoveryStateMachine(debounceTicks: 2);
        Assert.Equal(AudioDeviceRecoveryAction.None, sm.Tick(OnFallback(present: false)));
        Assert.Equal(AudioDeviceRecoveryState.Healthy, sm.State);

        Assert.Equal(AudioDeviceRecoveryAction.None, sm.Tick(OnFallback(present: true, topologyChanged: true)));
        Assert.Equal(AudioDeviceRecoveryState.ReappearDebounce, sm.State);
        Assert.Equal(AudioDeviceRecoveryAction.AttemptRecovery, sm.Tick(OnFallback(present: true)));
    }

    [Fact]
    public void Fallback_engine_without_topology_change_stays_parked()
    {
        // ASIO ghost name: present in the registry enumeration on every tick,
        // but no hot-plug ever happened — never disturb the fallback audio.
        var sm = new AudioDeviceRecoveryStateMachine();
        for (var i = 0; i < 20; i++)
            Assert.Equal(AudioDeviceRecoveryAction.None, sm.Tick(OnFallback(present: true)));
        Assert.Equal(AudioDeviceRecoveryState.Healthy, sm.State);
    }

    [Fact]
    public void Recovery_pending_ticks_issue_nothing()
    {
        var sm = new AudioDeviceRecoveryStateMachine(debounceTicks: 1);
        sm.Tick(Unplugged(topologyChanged: true));
        Assert.Equal(AudioDeviceRecoveryAction.AttemptRecovery, sm.Tick(Replugged(topologyChanged: true)));
        // Caller hasn't resolved the attempt yet — no stacking.
        Assert.Equal(AudioDeviceRecoveryAction.None, sm.Tick(Replugged()));
        Assert.Equal(AudioDeviceRecoveryAction.None, sm.Tick(Unplugged()));
        Assert.Equal(AudioDeviceRecoveryState.RecoveryPending, sm.State);
    }

    [Fact]
    public void Notify_calls_outside_recovery_pending_are_ignored()
    {
        var sm = new AudioDeviceRecoveryStateMachine();
        sm.NotifyRecoverySucceeded();
        sm.NotifyRecoveryFailed();
        Assert.Equal(AudioDeviceRecoveryState.Healthy, sm.State);
        sm.Tick(Unplugged(topologyChanged: true));
        sm.NotifyRecoverySucceeded();
        Assert.Equal(AudioDeviceRecoveryState.DeviceMissing, sm.State);
    }

    [Fact]
    public void Reset_returns_to_healthy_from_any_state()
    {
        var sm = new AudioDeviceRecoveryStateMachine(debounceTicks: 1);
        sm.Tick(Unplugged(topologyChanged: true));
        Assert.Equal(AudioDeviceRecoveryAction.AttemptRecovery, sm.Tick(Replugged(topologyChanged: true)));
        sm.Reset();
        Assert.Equal(AudioDeviceRecoveryState.Healthy, sm.State);
        // After a manual settings apply the machine trusts the new engine.
        Assert.Equal(AudioDeviceRecoveryAction.None, sm.Tick(Healthy(topologyChanged: true)));
    }

    [Fact]
    public void Repeated_unplug_replug_yields_one_attempt_per_stable_return()
    {
        var sm = new AudioDeviceRecoveryStateMachine(debounceTicks: 2);
        var attempts = 0;

        for (var cycle = 0; cycle < 3; cycle++)
        {
            sm.Tick(Unplugged(topologyChanged: true));
            sm.Tick(Unplugged());
            var a1 = sm.Tick(Replugged(topologyChanged: true));
            var a2 = sm.Tick(Replugged());
            foreach (var a in new[] { a1, a2 })
                if (a == AudioDeviceRecoveryAction.AttemptRecovery) attempts++;
            sm.NotifyRecoverySucceeded();
        }

        Assert.Equal(3, attempts);
    }
}
