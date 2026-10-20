using System.Reflection;
using DotCharter.Tone.Audio;
using DotCharter.Tone.Audio.Abstractions;
using JuceSharp;

namespace DotCharter.Tests.Services.Audio;

/// <summary>
///     The rig footer's CPU readout end to end through <see cref="JuceAudioPlaybackService" />
///     on the JUCE Null device (JuceSharp #29, ABI v46): no engine reads as "nothing
///     measured", a running engine reports the load its callback measured, and an engine
///     REBUILD (device change, NAM 48 kHz request, watchdog) still reports one.
/// </summary>
[Collection("NativeEngine")]
public sealed class CallbackLoadNullDeviceTests
{
    private static JuceEngineConfig NullConfig() => new()
    {
        PreferredDriver = AudioDriver.Wasapi,
        OutputTypeName = "Null",
        SampleRate = 48_000,
        BufferFrames = 480,
        OutputChannels = 2,
        EnableNullDevice = true,
    };

    private static void Rebuild(JuceAudioPlaybackService svc)
        => typeof(JuceAudioPlaybackService)
            .GetMethod("ReplaceEngine", BindingFlags.NonPublic | BindingFlags.Instance)!
            .Invoke(svc, [NullConfig()]);

    /// <summary>Polls until the callback has measured something. 60 s is a deadlock
    /// detector, not a budget: a starved gate can hold the paced device back.</summary>
    private static AudioCallbackLoad AwaitLoad(JuceAudioPlaybackService svc)
    {
        var deadline = DateTime.UtcNow.AddSeconds(60);
        AudioCallbackLoad load = default;
        while (DateTime.UtcNow < deadline)
        {
            if (svc.TryGetCallbackLoad(out load) && load.Average > 0) return load;
            Thread.Sleep(20);
        }

        Assert.Fail($"the Null device ran for 60 s and the callback load stayed {load} — the box was starved or the meter is dead");
        return load;
    }

    [Fact]
    public void TheEngine_ReportsItsCallbackLoad_AndARebuildKeepsReporting()
    {
        using var svc = new JuceAudioPlaybackService(FakeLocalization.English());
        Assert.False(svc.TryGetCallbackLoad(out var none), "a service with no engine reported a load");
        Assert.Equal(default, none);

        try { Rebuild(svc); }
        catch (TargetInvocationException ex) { Assert.Skip($"no null device here: {ex.InnerException?.Message}"); }

        var load = AwaitLoad(svc);
        Assert.True(load.Peak >= load.Average * 0.999, $"peak {load.Peak} below the average {load.Average}");
        Assert.True(load.Peak < 50, $"a peak of {load.Peak} buffer periods is a unit error, not a load");

        Rebuild(svc);
        AwaitLoad(svc);
    }
}
