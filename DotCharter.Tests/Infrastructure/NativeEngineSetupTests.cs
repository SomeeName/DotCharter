using JuceSharp;

namespace DotCharter.Tests.Infrastructure;

/// <summary>What <see cref="NativeEngineSetup.TryOpen" /> answers with null, and
/// what it lets through. No engine is opened: the factory throws.</summary>
public sealed class NativeEngineSetupTests
{
    [Fact]
    public void AMissingNativeLibrary_IsUnavailable()
    {
        Assert.Null(NativeEngineSetup.TryOpen(() => throw new DllNotFoundException("juce_sharp"), out var reason));
        Assert.Contains("juce_sharp.dll is not next to the test binary", reason, StringComparison.Ordinal);

        Assert.Null(NativeEngineSetup.TryOpen(
            () => throw new TypeInitializationException("NativeMethods", new DllNotFoundException("juce_sharp")), out reason));
        Assert.Contains("juce_sharp.dll is not next to the test binary", reason, StringComparison.Ordinal);
    }

    [Fact]
    public void AnEngineThatCannotOpenADevice_IsUnavailable()
    {
        Assert.Null(NativeEngineSetup.TryOpen(
            () => throw new JuceAudioException("no device", JuceAudioStatus.Device, "dca_engine_create"), out var reason));
        Assert.Contains("could not open an audio device", reason, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData(typeof(InvalidOperationException))]
    [InlineData(typeof(EntryPointNotFoundException))]
    [InlineData(typeof(NullReferenceException))]
    public void AnyOtherFailure_IsNotAvailabilityAndPropagates(Type failure)
    {
        var thrown = Assert.Throws(failure,
            () => NativeEngineSetup.TryOpen(() => throw (Exception)Activator.CreateInstance(failure)!, out _));
        Assert.IsType(failure, thrown);

        // A type initializer that failed for any reason but a missing library is a broken build.
        Assert.Throws<TypeInitializationException>(() => NativeEngineSetup.TryOpen(
            () => throw new TypeInitializationException("NativeMethods", new InvalidOperationException("boom")), out _));
    }
}
