using System.Reflection;
using DotCharter.Tone.Audio;
using JuceSharp;

namespace DotCharter.Tests.Services.Audio;

/// <summary>
/// The 48 kHz-for-neural-amp policy (PLAN-0.16 4.5) and the engine-config
/// serialization around it. The policy shipped with a real bug in v0.17.1 and
/// `docs/agents/services/audio-engine.md` records the fix as an instruction — <i>"'Never
/// tears the engine down' is only true because of `ApplyEngineConfig(cfg,
/// allowReplaceFallback: false)` — do not drop that argument"</i> — with nothing
/// mechanical enforcing it. Dropping it means a device pinned to 44.1 kHz makes
/// `Reconfigure` throw and the catch answers by destroying and rebuilding the
/// whole engine: playback dies, the stems drop and every VST reloads, because the
/// user added a NAM pedal.
/// <para>
/// The pure halves of the policy are tested behaviourally. The two wiring
/// invariants (the argument itself, and the gate that serializes config changes)
/// are asserted on the source, because reaching them at runtime needs a real
/// audio device and a device that refuses 48 kHz.
/// </para>
/// </summary>
public sealed class EngineConfigPolicyTests
{
    private static JuceEngineConfig Distinctive() => new()
    {
        SampleRate = 44_100,
        BufferFrames = 512,
        OutputChannels = 4,
        InputChannels = 2,
        OutputChannelMask = 0b1010,
        InputChannelMask = 0b0110,
        PreferredDriver = AudioDriver.Asio,
        OutputDeviceId = "Focusrite USB ASIO",
        InputDeviceId = "Focusrite USB ASIO In",
        OutputTypeName = "ASIO",
        InputTypeName = "ASIO",
        EnableConsoleLog = false,
    };

    /// <summary>
    /// The clone must carry EVERY property except the rate. A field it forgets
    /// reads as a delta to <see cref="JuceAudioPlaybackService.CanReconfigureInPlace"/>,
    /// which turns the best-effort 48 kHz request into a full engine replace the
    /// moment anyone drops the `allowReplaceFallback: false` guard. Enumerated by
    /// reflection so a new field on the submodule's config cannot slip past.
    /// </summary>
    [Fact]
    public void CloneWithSampleRate_CarriesEveryPropertyExceptTheRate()
    {
        var source = Distinctive();
        var clone = JuceAudioPlaybackService.CloneWithSampleRate(source, 48_000);

        Assert.Equal(48_000, clone.SampleRate);

        foreach (var p in typeof(JuceEngineConfig).GetProperties(BindingFlags.Public | BindingFlags.Instance))
        {
            if (p.Name == nameof(JuceEngineConfig.SampleRate)) continue;
            Assert.True(Equals(p.GetValue(source), p.GetValue(clone)),
                $"CloneWithSampleRate dropped '{p.Name}': expected {p.GetValue(source)}, got {p.GetValue(clone)}.");
        }
    }

    /// <summary>
    /// …and the point of carrying them: a rate-only delta must stay eligible for
    /// the in-place reconfigure. This is the whole mechanism by which the NAM
    /// request avoids an engine teardown.
    /// </summary>
    [Fact]
    public void ARateOnlyDelta_StaysEligibleForTheInPlaceReconfigure()
    {
        var active = Distinctive();
        var wanted = JuceAudioPlaybackService.CloneWithSampleRate(active, 48_000);

        Assert.True(JuceAudioPlaybackService.CanReconfigureInPlace(active, wanted));
    }

    /// <summary>Anything that re-inits the JUCE device TYPE needs a full replace.</summary>
    [Theory]
    [InlineData(nameof(JuceEngineConfig.PreferredDriver))]
    [InlineData(nameof(JuceEngineConfig.OutputChannels))]
    [InlineData(nameof(JuceEngineConfig.InputChannels))]
    [InlineData(nameof(JuceEngineConfig.OutputChannelMask))]
    [InlineData(nameof(JuceEngineConfig.InputChannelMask))]
    [InlineData(nameof(JuceEngineConfig.OutputTypeName))]
    [InlineData(nameof(JuceEngineConfig.InputTypeName))]
    public void AStructuralDelta_IsNotEligibleForTheInPlaceReconfigure(string changed)
    {
        var active = Distinctive();
        var incoming = changed switch
        {
            nameof(JuceEngineConfig.PreferredDriver) => With(active, driver: AudioDriver.Wasapi),
            nameof(JuceEngineConfig.OutputChannels) => With(active, outputChannels: 6),
            nameof(JuceEngineConfig.InputChannels) => With(active, inputChannels: 1),
            nameof(JuceEngineConfig.OutputChannelMask) => With(active, outputMask: 0b0011),
            nameof(JuceEngineConfig.InputChannelMask) => With(active, inputMask: 0b1100),
            nameof(JuceEngineConfig.OutputTypeName) => With(active, outputType: "Windows Audio"),
            nameof(JuceEngineConfig.InputTypeName) => With(active, inputType: "Windows Audio"),
            _ => throw new ArgumentOutOfRangeException(nameof(changed)),
        };

        Assert.False(JuceAudioPlaybackService.CanReconfigureInPlace(active, incoming),
            $"A change to {changed} re-inits the JUCE device type and must force a full engine replace.");
    }

    /// <summary>Buffer size and device NAME are in-place changes, not replaces.</summary>
    [Fact]
    public void ABufferOrDeviceNameChange_StaysInPlace()
    {
        var active = Distinctive();
        Assert.True(JuceAudioPlaybackService.CanReconfigureInPlace(active, With(active, bufferFrames: 128)));
        Assert.True(JuceAudioPlaybackService.CanReconfigureInPlace(active, With(active, outputDevice: "Other device")));
    }

    /// <summary>
    /// THE v0.17.1 PIN. The neural-amp rate request must never be able to cost
    /// the user an engine teardown, and the only thing standing between it and
    /// one is this argument at this call site.
    /// </summary>
    [Fact]
    public void TheNeuralAmpRateRequest_ForbidsTheEngineReplaceFallback()
    {
        var body = MethodBody("private void RequestNeuralAmpSampleRate()");

        Assert.Contains("ApplyEngineConfig(cfg, allowReplaceFallback: false)", body, StringComparison.Ordinal);
        Assert.DoesNotContain("ReplaceEngine(", body, StringComparison.Ordinal);
    }

    /// <summary>
    /// And the other half: every exit of ApplyEngineConfig that would reach
    /// ReplaceEngine has to be guarded by the flag. There are three — a generic
    /// Reconfigure failure, the EntryPointNotFoundException (old ABI) path, and
    /// the case where CanReconfigureInPlace was false to begin with.
    /// </summary>
    [Fact]
    public void ApplyEngineConfig_NeverReplacesTheEngineWhenTheFallbackIsForbidden()
    {
        var body = MethodBody("private void ApplyEngineConfigGated(");

        Assert.Contains("if (!allowReplaceFallback) return;", body, StringComparison.Ordinal);

        // The generic catch must return rather than fall through to the replace.
        var genericCatch = body.IndexOf("catch (Exception ex)", StringComparison.Ordinal);
        Assert.True(genericCatch >= 0);
        var guardInCatch = body.IndexOf("if (!allowReplaceFallback)", genericCatch, StringComparison.Ordinal);
        var replaceCall = body.IndexOf("ReplaceEngine(cfg)", StringComparison.Ordinal);
        Assert.True(guardInCatch >= 0 && guardInCatch < replaceCall,
            "The generic reconfigure-failure catch must honour allowReplaceFallback before falling through.");

        // The single ReplaceEngine call must be preceded by the unconditional guard.
        var lastGuard = body.LastIndexOf("if (!allowReplaceFallback) return;", StringComparison.Ordinal);
        Assert.True(lastGuard >= 0 && lastGuard < replaceCall,
            "The fall-through to ReplaceEngine must be guarded by allowReplaceFallback.");
    }

    /// <summary>
    /// A8. Every engine-CONFIG change must go through one gate, or a settings
    /// apply and a watchdog recovery (or the NAM request) can both be inside
    /// engine.Reconfigure at once — both writing `_activeEngineConfig`, last
    /// writer wins. If the loser's config is the one that took,
    /// `_activeEngineConfig` no longer describes the open device, which then
    /// feeds CanReconfigureInPlace and the watchdog's device probe.
    /// </summary>
    [Fact]
    public void EveryEngineConfigChangeTakesTheConfigGate()
    {
        foreach (var signature in new[]
                 {
                     "private void ApplyEngineConfig(JuceEngineConfig cfg, bool allowReplaceFallback = true)",
                     "private void ReplaceEngineCore(JuceEngineConfig cfg)",
                 })
        {
            var body = MethodBody(signature);
            Assert.Contains("_engineConfigGate", body, StringComparison.Ordinal);
        }
    }

    // ----- helpers -----------------------------------------------------------

    private static JuceEngineConfig With(
        JuceEngineConfig c,
        int? bufferFrames = null, int? outputChannels = null, int? inputChannels = null,
        uint? outputMask = null, uint? inputMask = null, AudioDriver? driver = null,
        string? outputDevice = null, string? outputType = null, string? inputType = null) => new()
        {
            SampleRate = c.SampleRate,
            BufferFrames = bufferFrames ?? c.BufferFrames,
            OutputChannels = outputChannels ?? c.OutputChannels,
            InputChannels = inputChannels ?? c.InputChannels,
            OutputChannelMask = outputMask ?? c.OutputChannelMask,
            InputChannelMask = inputMask ?? c.InputChannelMask,
            PreferredDriver = driver ?? c.PreferredDriver,
            OutputDeviceId = outputDevice ?? c.OutputDeviceId,
            InputDeviceId = c.InputDeviceId,
            OutputTypeName = outputType ?? c.OutputTypeName,
            InputTypeName = inputType ?? c.InputTypeName,
            EnableConsoleLog = c.EnableConsoleLog,
        };

    private static string MethodBody(string signature)
    {
        // EVERY partial of the service, concatenated: the class is split across a
        // dozen files, so pinning one filename makes this lint go vacuous the
        // moment a method moves to a neighbouring partial.
        var src = ServiceSource();
        var start = src.IndexOf(signature, StringComparison.Ordinal);
        Assert.True(start >= 0, $"Could not find '{signature}' — has it been renamed?");
        var open = src.IndexOf('{', start + signature.Length);
        var depth = 0;
        var i = open;
        for (; i < src.Length; i++)
        {
            if (src[i] == '{') depth++;
            else if (src[i] == '}' && --depth == 0) break;
        }
        return src[open..Math.Min(i + 1, src.Length)];
    }

    /// <summary>Every `JuceAudioPlaybackService*.cs` partial and its extracted
    /// collaborators (<see cref="PlaybackServiceSourceFiles" />), concatenated in a
    /// stable order — derived from the directory so a new partial is covered the
    /// day it appears.</summary>
    private static string ServiceSource() =>
        string.Concat(PlaybackServiceSourceFiles.All().Select(File.ReadAllText));
}
