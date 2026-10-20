using System.Reflection;
using DotCharter.Tone.Audio.Abstractions;
using DotCharter.Tone.Audio.Engine;
using JuceSharp;

namespace DotCharter.Tests.Services.Audio;

/// <summary>
///     The pure half of the onboarding engine probe (#288): what each exception out of
///     the wrapper's ABI gate means. No DLL is loaded here — the exceptions are built
///     directly (the ABI one through its internal constructor, the wrapper's only one).
/// </summary>
public sealed class JuceNativeEngineProbeTests
{
    [Fact]
    public void AnAbiMismatch_CarriesBothVersions()
    {
        var mismatch = (JuceAbiMismatchException)Activator.CreateInstance(typeof(JuceAbiMismatchException),
            BindingFlags.Instance | BindingFlags.NonPublic, null, [42u, 41u], null)!;

        var check = JuceNativeEngineProbe.Classify(mismatch);

        Assert.Equal(NativeEngineState.AbiMismatch, check.State);
        Assert.Equal(42u, check.ExpectedAbi);
        Assert.Equal(41u, check.NativeAbi);
    }

    [Theory]
    [InlineData(typeof(DllNotFoundException))]
    [InlineData(typeof(BadImageFormatException))]
    [InlineData(typeof(EntryPointNotFoundException))]
    public void ALoadFailure_IsMissing(Type exception)
    {
        var check = JuceNativeEngineProbe.Classify((Exception)Activator.CreateInstance(exception, "juce_sharp")!);

        Assert.Equal(NativeEngineState.Missing, check.State);
        Assert.Contains(exception.Name, check.Detail, StringComparison.Ordinal);
    }

    /// <summary>The probe's own bytes are not a .wem, so the native side refusing them is the
    /// proof the library loaded and passed the gate — the READY answer.</summary>
    [Fact]
    public void TheNativeSideRefusingTheBytes_IsReady()
    {
        var check = JuceNativeEngineProbe.Classify(new JuceAudioException(JuceAudioStatus.Decode, "wem_open_memory"));

        Assert.Equal(NativeEngineState.Ready, check.State);
    }
}
