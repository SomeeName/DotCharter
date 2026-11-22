using DotCharter.Tone.Audio.Dsp;

namespace DotCharter.Tone.Audio;

/// <summary>
///     Reads the tone rig's static module catalogue, and with it loads the native engine
///     DLL (<c>juce_sharp.dll</c>, ~14 MB), on a worker thread while the UI thread builds
///     the DI container.
///     <para>
///         <b>Why.</b> That read was the first P/Invoke of the boot and ran on the UI
///         thread inside the startup <c>services</c> phase (<c>IPlayerDspChains</c>, a
///         dependency of <c>TransportViewModel</c>, which the first frame needs): 7 ms warm
///         and ~73 ms on a cold disk, the largest step of the phase. Measured per step on
///         the R2R release build, hidden desktop.
///     </para>
///     <para>
///         <b>No behaviour change, no race.</b> The catalogue is static native data
///         (<c>dca_dsp_module_*</c> takes no engine and no seat; the native side is a
///         function-local static, built thread-safely once), so the same read gives the
///         same answer on any thread, and nothing here creates an engine, a device or a
///         JUCE message thread. <see cref="PlayerDspChains.CreateNative" /> adopts the
///         result through <see cref="TakeCatalogueSource" />, which WAITS for the worker if
///         it is still running, so the chains never exist without their catalogue. A read
///         that failed (no DLL, a missing entry point) yields null and the factory reads
///         the catalogue itself, failing exactly as it did before.
///     </para>
/// </summary>
public static class NativeEnginePrefetch
{
    private static readonly object Gate = new();
    private static Task<JuceDspChainService?>? _read;

    /// <summary>
    ///     Starts the background read once per process and returns it; later calls return
    ///     the same task. Never throws, never blocks.
    /// </summary>
    public static Task<JuceDspChainService?> Start()
    {
        lock (Gate)
            return _read ??= Task.Run(ReadCatalogue);
    }

    /// <summary>
    ///     The catalogue the worker read (waiting for it if it is still running), or null
    ///     when <see cref="Start" /> never ran or the read failed — then the caller reads
    ///     the catalogue itself.
    /// </summary>
    public static JuceDspChainService? TakeCatalogueSource()
    {
        Task<JuceDspChainService?>? read;
        lock (Gate)
            read = _read;
        return read?.GetAwaiter().GetResult();
    }

    private static JuceDspChainService? ReadCatalogue()
    {
        try
        {
            var source = new JuceDspChainService();
            source.LoadCatalog();
            return source;
        }
        catch (Exception)
        {
            return null;
        }
    }
}
