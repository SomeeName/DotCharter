using DotCharter.Tone.Audio;
using DotCharter.Tone.Audio.Engine;

namespace DotCharter.Tests.Services.Audio;

/// <summary>
/// Issue #1053: after an unplug the app took 7.4 s to notice, kept the transport
/// "playing" without a clock in the meantime, checked for the device only every
/// 3 s, and the rebuild spent 4.4 s before it even reached the old engine. Each
/// test here pins one of those steps with plain values, no device.
/// </summary>
public sealed class FastDeviceRecoveryTests
{
    private static readonly object Engine = new();
    private const long Confirm = AudioDeviceClosedDetector.ConfirmMs;

    // --- 1. A closed-device report from the engine declares the loss within a second.

    [Fact]
    public void A_device_that_closes_and_never_calls_back_is_reported_lost_within_a_second()
    {
        var d = new AudioDeviceClosedDetector();
        Assert.False(d.Observe(Engine, 48000, clockValid: true, 1000, nowMs: 0));
        // The unplug: the engine reports sample rate 0 while the clock still holds its last value.
        Assert.False(d.Observe(Engine, 0, clockValid: true, 1000, nowMs: 20));
        // The ASIO reset re-publishes the old rate, but the clock never becomes readable again.
        Assert.False(d.Observe(Engine, 48000, clockValid: false, 0, nowMs: 40));
        Assert.False(d.Observe(Engine, 48000, clockValid: false, 0, nowMs: 20 + Confirm - 1));
        Assert.True(d.Observe(Engine, 48000, clockValid: false, 0, nowMs: 20 + Confirm));
        Assert.True(Confirm <= 1000, "the issue asks for the loss to be noticed within about a second");
        // Reported once, not on every poll after.
        Assert.False(d.Observe(Engine, 48000, clockValid: false, 0, nowMs: 20 + Confirm + 20));
    }

    [Fact]
    public void A_driver_reset_that_starts_calling_back_again_is_not_a_loss()
    {
        var d = new AudioDeviceClosedDetector();
        d.Observe(Engine, 48000, clockValid: true, 1000, nowMs: 0);
        d.Observe(Engine, 0, clockValid: true, 1000, nowMs: 20);
        // The user changed the buffer size in the driver's panel: JUCE re-opens and the clock runs.
        d.Observe(Engine, 48000, clockValid: true, 96, nowMs: 60);
        d.Observe(Engine, 48000, clockValid: true, 192, nowMs: 80);
        for (var t = 100L; t < 3000; t += 20)
            Assert.False(d.Observe(Engine, 48000, clockValid: true, 192 + t, nowMs: t));
    }

    [Fact]
    public void A_device_that_was_never_open_is_no_evidence()
    {
        var d = new AudioDeviceClosedDetector();
        for (var t = 0L; t < 3000; t += 20)
            Assert.False(d.Observe(Engine, 0, clockValid: false, 0, nowMs: t));
    }

    [Fact]
    public void A_replaced_engine_starts_fresh()
    {
        var d = new AudioDeviceClosedDetector();
        d.Observe(Engine, 48000, clockValid: true, 1000, nowMs: 0);
        d.Observe(Engine, 0, clockValid: true, 1000, nowMs: 20);
        var rebuilt = new object();
        for (var t = 40L; t < 3000; t += 20)
            Assert.False(d.Observe(rebuilt, 0, clockValid: false, 0, nowMs: t));
    }

    // --- 2. The transport stops as soon as the loss is known.

    [Fact]
    public void Declaring_the_loss_stops_the_transport_before_anyone_hears_about_it()
    {
        var svc = new JuceAudioPlaybackService(FakeLocalization.English());
        try
        {
            typeof(JuceAudioPlaybackService).GetProperty(nameof(JuceAudioPlaybackService.IsPlaying))!
                .SetValue(svc, true);
            bool? playingWhenNotified = null;
            svc.DeviceLost += (_, _) => playingWhenNotified = svc.IsPlaying;

            svc.DeclareDeviceLost(new AppConfig());

            Assert.False(svc.IsPlaying);
            Assert.True(svc.IsDeviceLost);
            Assert.False(playingWhenNotified);
        }
        finally
        {
            svc.Dispose();
        }
    }

    // --- 3. While lost, the watchdog checks about once a second.

    [Theory]
    [InlineData(AudioDeviceRecoveryState.DeviceMissing)]
    [InlineData(AudioDeviceRecoveryState.ReappearDebounce)]
    [InlineData(AudioDeviceRecoveryState.RecoveryPending)]
    [InlineData(AudioDeviceRecoveryState.Cooldown)]
    public void A_lost_device_is_checked_about_once_a_second(AudioDeviceRecoveryState state) =>
        Assert.Equal(1000, JuceAudioPlaybackService.WatchdogIntervalFor(state));

    [Fact]
    public void A_healthy_device_keeps_the_slow_check() =>
        Assert.Equal(3000, JuceAudioPlaybackService.WatchdogIntervalFor(AudioDeviceRecoveryState.Healthy));

    [Fact]
    public void A_replug_during_the_backoff_starts_recovery_on_the_next_check()
    {
        var sm = new AudioDeviceRecoveryStateMachine(debounceTicks: 2, cooldownTicks: 15, deadRetryTicks: 30);
        sm.Tick(Dead());
        sm.Tick(Dead());
        Assert.Equal(AudioDeviceRecoveryAction.AttemptRecovery, sm.Tick(Dead()));
        sm.NotifyRecoveryFailed();

        // The interface comes back: its endpoints appear in the enumeration.
        Assert.Equal(AudioDeviceRecoveryAction.None, sm.Tick(Dead(topologyChanged: true)));
        Assert.Equal(AudioDeviceRecoveryAction.AttemptRecovery, sm.Tick(Dead()));
    }

    [Fact]
    public void A_replug_during_a_failing_attempt_is_not_forgotten()
    {
        var sm = new AudioDeviceRecoveryStateMachine(debounceTicks: 1, cooldownTicks: 15, deadRetryTicks: 30);
        sm.Tick(Dead());
        Assert.Equal(AudioDeviceRecoveryAction.AttemptRecovery, sm.Tick(Dead()));
        sm.Tick(Dead(topologyChanged: true)); // replugged while the attempt was running
        sm.NotifyRecoveryFailed();

        Assert.Equal(AudioDeviceRecoveryAction.None, sm.Tick(Dead()));
        Assert.Equal(AudioDeviceRecoveryAction.AttemptRecovery, sm.Tick(Dead()));
    }

    [Fact]
    public void An_absent_device_is_not_rebuilt_more_than_twice_a_minute()
    {
        var sm = new AudioDeviceRecoveryStateMachine(debounceTicks: 2, cooldownTicks: 15, deadRetryTicks: 30);
        sm.Tick(Dead());
        var attempts = 0;
        for (var second = 0; second < 120; second++)
        {
            if (sm.Tick(Dead()) != AudioDeviceRecoveryAction.AttemptRecovery) continue;
            attempts++;
            sm.NotifyRecoveryFailed();
        }
        Assert.InRange(attempts, 1, 4);
    }

    // --- 4. The recovery rebuild does not rescan every driver for numbers it already has.

    [Fact]
    public void Channel_counts_seen_while_healthy_are_reused_by_the_recovery_rebuild()
    {
        var memory = new DeviceChannelCountMemory();
        memory.Remember("ASIO", "Focusrite", input: true, 8);
        memory.Remember("ASIO", "Focusrite", input: false, 10);

        Assert.True(memory.TryRecall("asio", "focusrite", input: true, out var inputs));
        Assert.Equal(8, inputs);
        Assert.True(memory.TryRecall("ASIO", "Focusrite", input: false, out var outputs));
        Assert.Equal(10, outputs);
        Assert.False(memory.TryRecall("ASIO", "Other", input: true, out _));
    }

    [Fact]
    public void An_unknown_channel_count_is_never_remembered()
    {
        var memory = new DeviceChannelCountMemory();
        memory.Remember("ASIO", "Focusrite", input: true, 8);
        memory.Remember("ASIO", "Focusrite", input: true, 0);

        Assert.True(memory.TryRecall("ASIO", "Focusrite", input: true, out var count));
        Assert.Equal(8, count);
    }

    private static AudioDeviceProbe Dead(bool topologyChanged = false) => new(
        DesiredDevicePresent: true, EngineAlive: false, EngineOnDesiredDevice: false, TopologyChanged: topologyChanged);
}
