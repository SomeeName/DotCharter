using DotCharter.Tone.Audio.Dsp;
using DotCharter.Tone.Audio.Dsp.Presets;
using DotCharter.Tone.Infrastructure.Storage;
using JuceSharp;

namespace DotCharter.Tests.Services.Audio.Dsp;

/// <summary>
/// Real-native round-trip for the ABI-v22 string-param surface, exercised through
/// the PARENT's managed stack (<see cref="JuceDspChainService"/> over the real
/// <c>DspNativeInterop</c> P/Invokes) attached to a live <see cref="JuceAudioEngine"/>.
/// Mirrors <c>DcRigTargetCatalogTests</c>: everything skips cleanly with
/// <see cref="Assert.SkipWhen"/> when the native DLL / audio engine can't load in
/// the test environment, so it NEVER depends on native being present. On a machine
/// with the built DLL it proves the size-then-copy getter, the "" = revert setter,
/// and the string-param catalogue against the real engine.
/// </summary>
[Collection("NativeEngine")]
public sealed class DspStringParamNativeRoundTripTests
{
    [Fact]
    public void CabIrPath_And_MikuPhrases_RoundTrip_ThroughTheManagedStack()
    {
        using var env = NativeChainEnv.TryCreate();
        Assert.SkipWhen(env is null, "native DSP ABI (juce_sharp.dll) / audio engine not loadable in this test environment");

        var svc = env!.Chain;

        // --- cab irPath: set → read back → "" reverts -------------------------
        var cabAvailable = svc.Catalog.Any(m => m.Id == "cab");
        Assert.SkipWhen(!cabAvailable, "native catalogue has no 'cab' module");
        var cab = svc.AddNode("cab");
        Assert.NotNull(cab);

        // A real WAV so the IR loader accepts it (a bad path is rejected natively).
        var wav = TinyWav.Create();
        try
        {
            Assert.True(svc.SetNodeString(cab!.NodeId, "irPath", wav));
            Assert.Equal(wav, svc.GetNodeString(cab.NodeId, "irPath"));

            Assert.True(svc.SetNodeString(cab.NodeId, "irPath", string.Empty));
            Assert.Equal(string.Empty, svc.GetNodeString(cab.NodeId, "irPath"));
        }
        finally
        {
            TinyWav.TryDelete(wav);
        }

        // --- miku phrases: unicode free text round-trips ----------------------
        if (svc.Catalog.Any(m => m.Id == "miku"))
        {
            var miku = svc.AddNode("miku");
            Assert.NotNull(miku);
            const string phrases = "mi ku mi ku da-yo ミク";
            Assert.True(svc.SetNodeString(miku!.NodeId, "phrases", phrases));
            Assert.Equal(phrases, svc.GetNodeString(miku.NodeId, "phrases"));
        }
    }

    [Fact]
    public void StringParamCatalogue_DescribesFilePickers_FromTheNativeAbi()
    {
        using var env = NativeChainEnv.TryCreate();
        Assert.SkipWhen(env is null, "native DSP ABI (juce_sharp.dll) / audio engine not loadable in this test environment");

        var cab = env!.Chain.Catalog.FirstOrDefault(m => m.Id == "cab");
        Assert.SkipWhen(cab is null, "native catalogue has no 'cab' module");

        Assert.Equal(new[] { "irPath", "irPathB" }, cab!.StringParams.Select(p => p.Id));
        Assert.All(cab.StringParams, ir => Assert.Equal("*.wav", ir.FileFilter));

        // Free-text params (miku phrases) advertise an empty filter (no picker).
        var miku = env.Chain.Catalog.FirstOrDefault(m => m.Id == "miku");
        if (miku is not null)
        {
            var phrases = Assert.Single(miku.StringParams);
            Assert.Equal("phrases", phrases.Id);
            Assert.Equal(string.Empty, phrases.FileFilter);
        }
    }

    /// <summary>
    /// The FACTORY-PRESET leg of the string-param contract on the real engine: the
    /// Miku lyric presets carry their syllables in <c>DcRigModule.StringParams</c>,
    /// and <c>DcRigApplier</c> must push them onto the freshly-added native node —
    /// so applying the preset over a live chain leaves the lyrics readable back out
    /// of the native module (this is what makes a re-added node sing the preset's
    /// words instead of its built-in default pattern).
    /// </summary>
    [Fact]
    public void ApplyingAMikuLyricPreset_LandsThePhrasesOnTheNativeNode()
    {
        using var env = NativeChainEnv.TryCreate();
        Assert.SkipWhen(env is null, "native DSP ABI (juce_sharp.dll) / audio engine not loadable in this test environment");

        var svc = env!.Chain;
        Assert.SkipWhen(svc.Catalog.All(m => m.Id != "mikustomp"), "native catalogue has no 'mikustomp' module");

        var preset = DspTonePresetLibrary.All.Single(p => p.Id == "miku-stomp-hatsune-phrase");
        new DspTonePresetService(svc, new FakeConfigurationService()).Apply(preset);

        var node = Assert.Single(svc.GetNodes(), n => n.ModuleTypeId == "mikustomp");
        Assert.Equal("ha tsu ne mi ku", svc.GetNodeString(node.NodeId, "phrases"));
        Assert.Equal("a ri ga to u", svc.GetNodeString(node.NodeId, "phrase2"));
        Assert.Equal("sa yo na ra", svc.GetNodeString(node.NodeId, "phrase3"));
        // Phrase 1 is the selected pattern — those lyrics are what it will sing.
        Assert.Equal(6f, svc.GetParam(node.NodeId, "pattern"));
    }

    /// <summary>A live engine + attached chain, or null when native can't load.
    /// Owns disposal of the engine so the test body stays clean.</summary>
    private sealed class NativeChainEnv : IDisposable
    {
        public required JuceAudioEngine Engine { get; init; }
        public required JuceDspChainService Chain { get; init; }

        public static NativeChainEnv? TryCreate()
        {
            JuceAudioEngine? engine = null;
            try
            {
                engine = new JuceAudioEngine(new JuceEngineConfig { EnableConsoleLog = false });
                var chain = new JuceDspChainService();
                chain.LoadCatalog();
                chain.AttachEngine(engine);
                return new NativeChainEnv { Engine = engine, Chain = chain };
            }
            catch
            {
                engine?.Dispose();
                return null;
            }
        }

        public void Dispose()
        {
            try { Chain.AttachEngine(nint.Zero); } catch { /* ignored */ }
            try { Engine.Dispose(); } catch { /* ignored */ }
        }
    }

    /// <summary>Writes a tiny valid mono 16-bit PCM WAV so the native IR loader
    /// accepts it; the round-trip only cares that the path is stored, not the IR
    /// content.</summary>
    private static class TinyWav
    {
        public static string Create()
        {
            var path = Path.Combine(Path.GetTempPath(), "dc-ir-" + Guid.NewGuid().ToString("N") + ".wav");
            const int sampleRate = 48000;
            const int samples = 64; // ~1.3 ms
            var dataBytes = samples * 2;
            using var fs = new FileStream(path, FileMode.CreateNew, FileAccess.Write);
            using var w = new BinaryWriter(fs);
            w.Write("RIFF"u8.ToArray());
            w.Write(36 + dataBytes);
            w.Write("WAVE"u8.ToArray());
            w.Write("fmt "u8.ToArray());
            w.Write(16);              // PCM fmt chunk size
            w.Write((short)1);        // PCM
            w.Write((short)1);        // mono
            w.Write(sampleRate);
            w.Write(sampleRate * 2);  // byte rate
            w.Write((short)2);        // block align
            w.Write((short)16);       // bits per sample
            w.Write("data"u8.ToArray());
            w.Write(dataBytes);
            for (var i = 0; i < samples; i++)
                w.Write((short)(i == 0 ? short.MaxValue / 2 : 0)); // a tiny impulse
            return path;
        }

        public static void TryDelete(string path) => BestEffortDelete.File(path);
    }
}
