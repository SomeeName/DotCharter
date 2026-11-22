using DotCharter.Tests.Services.Audio.Dsp;
using DotCharter.Tone.Audio;
using DotCharter.Tone.Audio.Dsp;
using JuceSharp;

namespace DotCharter.Tests.Audio;

/// <summary>
///     <see cref="NativeEnginePrefetch" /> (#793) moves the tone-rig catalogue read off the
///     UI thread's startup path. It is only worth anything if the chains ADOPT the worker's
///     catalogue (no second read on the calling thread) and if the worker's read is the
///     real native catalogue. Structural: what is read and shared, never a timing.
/// </summary>
public sealed class NativeEnginePrefetchTests
{
    [Fact]
    public void ThePlayerChainsAdoptAPrefetchedCatalogueWithoutReadingItAgain()
    {
        var native = new FakeDspNativeInterop();
        var source = new JuceDspChainService(native);
        source.LoadCatalog();
        Assert.NotEmpty(source.Catalog);

        // Seat services over the real interop: a catalogue read here would P/Invoke, and
        // in a test host without the DLL it would throw. Adopting reads nothing.
        var chains = PlayerDspChains.CreateNative(DspPlayers.MaxPlayers, null, source);

        for (var seat = 0; seat < DspPlayers.MaxPlayers; seat++)
            Assert.Same(source.Catalog, chains.For(seat).Catalog);
    }

    [Fact]
    public async Task TheWorkerReadsTheRealNativeCatalogueOnceAndHandsItOver()
    {
        var dll = Path.Combine(Path.GetDirectoryName(typeof(JuceAudioException).Assembly.Location)!, "juce_sharp.dll");
        Assert.SkipUnless(File.Exists(dll), "juce_sharp.dll is not in the test output: " + dll);

        var first = NativeEnginePrefetch.Start();
        var prefetched = await first;

        Assert.Same(first, NativeEnginePrefetch.Start());
        Assert.NotNull(prefetched);
        Assert.Same(prefetched, NativeEnginePrefetch.TakeCatalogueSource());

        var direct = new JuceDspChainService();
        direct.LoadCatalog();
        Assert.NotEmpty(direct.Catalog);
        Assert.Equal(direct.Catalog.Select(m => m.Id), prefetched.Catalog.Select(m => m.Id));
    }
}
