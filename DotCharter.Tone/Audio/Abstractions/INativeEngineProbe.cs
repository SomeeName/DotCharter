namespace DotCharter.Tone.Audio.Abstractions;

/// <summary>Whether the native audio library loads and is the build this app was compiled against.</summary>
public enum NativeEngineState
{
    Ready,

    /// <summary><c>juce_sharp.dll</c> is not there, or Windows cannot load it.</summary>
    Missing,

    /// <summary>It loads, but reports a different <c>DCA_ABI_VERSION</c> than the wrapper expects.</summary>
    AbiMismatch
}

/// <summary>The answer; the two ABI numbers are set for <see cref="NativeEngineState.AbiMismatch" />.</summary>
public sealed record NativeEngineCheck(NativeEngineState State, uint ExpectedAbi = 0, uint NativeAbi = 0, string Detail = "");

/// <summary>
///     Asks the wrapper's own ABI gate whether the native engine can run, WITHOUT
///     opening an engine or an audio device. The first-run wizard's first page shows
///     the answer.
/// </summary>
public interface INativeEngineProbe
{
    /// <summary>Blocking — it may load the DLL. Callers run it off the UI thread.</summary>
    NativeEngineCheck Probe();
}
