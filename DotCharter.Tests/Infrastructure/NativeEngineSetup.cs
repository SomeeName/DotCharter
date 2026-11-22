using JuceSharp;

namespace DotCharter.Tests.Infrastructure;

/// <summary>
///     Opens a native engine for a test, telling "no engine can exist on this
///     machine" (a reason to skip) apart from everything else (#608).
///     <para>
///         The fixtures this serves caught EVERY exception from the engine, the
///         catalogue load and the attach and returned null, and their tests skip
///         on null: a product regression in the catalogue or in
///         <c>AttachEngine</c> turned the whole class into skips, which a gate
///         reads as green. Only the two documented availability failures are
///         answered with null here: the native library is not next to the test
///         binary, or the engine could not open a device. The caller runs the
///         rest of its setup OUTSIDE this call, so anything that throws there
///         fails the test.
///     </para>
/// </summary>
internal static class NativeEngineSetup
{
    /// <summary>The engine <paramref name="open" /> built, or null with the reason no
    /// engine is available here. Any other exception propagates.</summary>
    public static JuceAudioEngine? TryOpen(Func<JuceAudioEngine> open, out string unavailable)
    {
        try
        {
            unavailable = string.Empty;
            return open();
        }
        catch (DllNotFoundException ex)
        {
            unavailable = $"juce_sharp.dll is not next to the test binary ({ex.Message})";
        }
        catch (TypeInitializationException ex) when (ex.InnerException is DllNotFoundException missing)
        {
            unavailable = $"juce_sharp.dll is not next to the test binary ({missing.Message})";
        }
        catch (JuceAudioException ex)
        {
            unavailable = $"the engine could not open an audio device on this machine ({ex.Message})";
        }

        return null;
    }
}
