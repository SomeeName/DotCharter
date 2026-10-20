using DotCharter.Tone.Audio;

namespace DotCharter.Tests.Services.Audio;

/// <summary>
/// A7. The device watchdog ran a full live device enumeration on EVERY 3 s tick
/// — the very scan `InitializeAsync` was rewritten to avoid, whose own comment
/// records both costs: "an ASIO scan costs several hundred ms", and holding ASIO
/// driver references from a scan made the real engine's ASIO open fail with "No
/// such device". The watchdog re-introduced it on a pool thread, twenty times a
/// minute, for the life of the process.
/// <para>
/// The scan is only genuinely needed when something is already off. While the
/// engine is alive and bound to the configured device, the cheap liveness signal
/// (one P/Invoke for the sample rate) is what actually catches an unplug, so the
/// enumeration is THINNED rather than dropped — still periodic, so the narrow
/// "endpoint vanished but JUCE has not noticed" case stays detectable.
/// </para>
/// </summary>
public sealed class DeviceWatchdogEnumerationTests
{
    private const int Every = JuceAudioPlaybackService.HealthyEnumerationEveryNTicks;

    [Fact]
    public void AHealthyEngineOnItsOwnDeviceSkipsMostScans()
    {
        var scans = 0;
        for (var tick = 0; tick < Every * 3; tick++)
        {
            if (JuceAudioPlaybackService.ShouldEnumerateDevices(
                    AudioDeviceRecoveryState.Healthy, engineAlive: true, engineOnDesiredDevice: true,
                    healthyTickIndex: tick % Every))
            {
                scans++;
            }
        }

        Assert.Equal(3, scans);
    }

    /// <summary>The first healthy tick after a reset still scans — a manual apply
    /// or a recovery just changed the device world we are watching.</summary>
    [Fact]
    public void TheFirstHealthyTickAfterAResetStillScans()
    {
        Assert.True(JuceAudioPlaybackService.ShouldEnumerateDevices(
            AudioDeviceRecoveryState.Healthy, engineAlive: true, engineOnDesiredDevice: true,
            healthyTickIndex: 0));
    }

    /// <summary>A dead engine always scans: that is the loss we are watching for.</summary>
    [Fact]
    public void ADeadEngineAlwaysScans()
    {
        for (var tick = 0; tick < Every * 2; tick++)
        {
            Assert.True(JuceAudioPlaybackService.ShouldEnumerateDevices(
                AudioDeviceRecoveryState.Healthy, engineAlive: false, engineOnDesiredDevice: false,
                healthyTickIndex: tick));
        }
    }

    /// <summary>
    /// Running on a FALLBACK device always scans: the enumeration is the only way
    /// to see the configured device come back, and it is the hot-plug proxy for
    /// ASIO (whose registry names persist while the hardware is unplugged).
    /// </summary>
    [Fact]
    public void AFallbackDeviceAlwaysScans()
    {
        for (var tick = 0; tick < Every * 2; tick++)
        {
            Assert.True(JuceAudioPlaybackService.ShouldEnumerateDevices(
                AudioDeviceRecoveryState.Healthy, engineAlive: true, engineOnDesiredDevice: false,
                healthyTickIndex: tick));
        }
    }

    /// <summary>Every non-healthy state scans on every tick — debounce, cooldown
    /// and the pending-attempt window are all driven by the enumeration.</summary>
    [Theory]
    [InlineData(AudioDeviceRecoveryState.DeviceMissing)]
    [InlineData(AudioDeviceRecoveryState.ReappearDebounce)]
    [InlineData(AudioDeviceRecoveryState.RecoveryPending)]
    [InlineData(AudioDeviceRecoveryState.Cooldown)]
    public void EveryUnhealthyStateScansOnEveryTick(AudioDeviceRecoveryState state)
    {
        for (var tick = 0; tick < Every * 2; tick++)
        {
            Assert.True(JuceAudioPlaybackService.ShouldEnumerateDevices(
                state, engineAlive: true, engineOnDesiredDevice: true, healthyTickIndex: tick));
        }
    }

    /// <summary>
    /// The substitution the skip makes must not change what the state machine
    /// decides. A skipped tick reports DesiredDevicePresent=true /
    /// TopologyChanged=false; from Healthy, with the engine alive and on its own
    /// device, that is a no-op — which is exactly what a real scan of a working
    /// setup produces.
    /// </summary>
    [Fact]
    public void ASkippedTickIsANoOpForTheRecoveryStateMachine()
    {
        var sm = new AudioDeviceRecoveryStateMachine();
        var skipped = new AudioDeviceProbe(
            DesiredDevicePresent: true, EngineAlive: true,
            EngineOnDesiredDevice: true, TopologyChanged: false);

        for (var i = 0; i < 20; i++)
            Assert.Equal(AudioDeviceRecoveryAction.None, sm.Tick(skipped));

        Assert.Equal(AudioDeviceRecoveryState.Healthy, sm.State);
    }

    /// <summary>
    /// …and the loss the cheap signal catches still reports immediately, without
    /// any enumeration having happened.
    /// </summary>
    [Fact]
    public void ADeadEngineStillReportsLossOnTheVeryNextTick()
    {
        var sm = new AudioDeviceRecoveryStateMachine();
        sm.Tick(new AudioDeviceProbe(true, true, true, false));

        var action = sm.Tick(new AudioDeviceProbe(
            DesiredDevicePresent: true, EngineAlive: false,
            EngineOnDesiredDevice: false, TopologyChanged: false));

        Assert.Equal(AudioDeviceRecoveryAction.ReportLost, action);
    }
}
