using JuceSharp;

namespace DotCharter.Tone.Audio.Engine;

/// <summary>
///     <see cref="INativeEngineProbe" /> over the wrapper's own ABI gate.
///     <para>
///         The wrapper exposes that gate only at the head of something that opens:
///         <c>new JuceAudioEngine</c> (which also opens an AUDIO DEVICE) and
///         <c>JuceWemDecoder.Open*</c>. The decoder is the one that touches no
///         device, so the probe hands it four bytes that are not a .wem: the DLL
///         loads, the ABI is compared for equality, and the native side then
///         refuses the bytes with a <see cref="JuceAudioException" /> — which is
///         the READY answer, because it can only come from a loaded library that
///         passed the gate.
///     </para>
/// </summary>
public sealed class JuceNativeEngineProbe : INativeEngineProbe
{
    private static readonly byte[] NotAWem = [0x44, 0x43, 0x41, 0x3F];

    public NativeEngineCheck Probe()
    {
        try
        {
            using var decoder = JuceWemDecoder.OpenMemory(NotAWem);
            return new NativeEngineCheck(NativeEngineState.Ready);
        }
        catch (Exception ex)
        {
            return Classify(ex);
        }
    }

    /// <summary>The pure half: what an exception out of the gate means.</summary>
    public static NativeEngineCheck Classify(Exception ex) => ex switch
    {
        JuceAbiMismatchException abi => new NativeEngineCheck(NativeEngineState.AbiMismatch,
            abi.ExpectedAbiVersion, abi.NativeAbiVersion, abi.Message),
        JuceAudioException => new NativeEngineCheck(NativeEngineState.Ready),
        _ => new NativeEngineCheck(NativeEngineState.Missing, Detail: $"{ex.GetType().Name}: {ex.Message}")
    };
}
