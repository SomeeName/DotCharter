using DotCharter.Tone.Audio.Dsp;
using JuceSharp;

namespace DotCharter.Tests.Services.Audio.Dsp;

/// <summary>
/// Real-native round-trip for the ABI-v24 enum value-label surface, loaded onto
/// <see cref="DspParamInfo.ValueLabels"/> by <see cref="JuceDspChainService.LoadCatalog"/>
/// over the real <c>DspNativeInterop</c> P/Invokes. Mirrors
/// <c>DspBranchNativeRoundTripTests</c>: everything skips cleanly with
/// <see cref="Assert.SkipWhen"/> when the native DLL can't load, so it NEVER
/// depends on native being present. On a machine with the built v24 DLL it proves
/// the amp voicing model roster (24 named voicings) reaches the managed catalogue,
/// and that continuous params stay label-free (so the UI keeps a knob).
/// </summary>
[Collection("NativeEngine")]
public sealed class DspValueLabelNativeRoundTripTests
{
    [Fact]
    public void AmpVoicing_Exposes27NamedModels_IncludingPlexi100()
    {
        using var env = NativeChainEnv.TryCreate();
        Assert.SkipWhen(env is null, "native DSP ABI (juce_sharp.dll) not loadable in this test environment");

        var svc = env!.Chain;
        var amp = svc.Catalog.FirstOrDefault(m => m.Id == "amp");
        Assert.SkipWhen(amp is null, "native catalogue has no 'amp' module");

        var voicing = amp!.Params.FirstOrDefault(p => p.Id == "voicing");
        Assert.NotNull(voicing);

        // The whole 27-model gear roster (24 + the three #70 circuit voicings)
        // is surfaced as value labels.
        Assert.NotNull(voicing!.ValueLabels);
        Assert.Equal(27, voicing.ValueLabels!.Count);
        Assert.All(voicing.ValueLabels, l => Assert.False(string.IsNullOrWhiteSpace(l)));

        // Index 0 keeps its original coefficients; the roster names it. "Plexi 100"
        // is one of the appended British soundalikes (voicings 7-23).
        Assert.Equal("Blackface Twin 85", voicing.ValueLabels[0]);
        Assert.Contains("Plexi 100", voicing.ValueLabels);
    }

    [Fact]
    public void CabAndDriveModelParams_AreLabelled_ContinuousParamsAreNot()
    {
        using var env = NativeChainEnv.TryCreate();
        Assert.SkipWhen(env is null, "native DSP ABI (juce_sharp.dll) not loadable in this test environment");

        var svc = env!.Chain;

        var cab = svc.Catalog.FirstOrDefault(m => m.Id == "cab");
        if (cab?.Params.FirstOrDefault(p => p.Id == "model") is { } cabModel)
        {
            Assert.NotNull(cabModel.ValueLabels);
            Assert.Equal(26, cabModel.ValueLabels!.Count); // 12-25 appended by #74
        }

        var overdrive = svc.Catalog.FirstOrDefault(m => m.Id == "overdrive");
        if (overdrive?.Params.FirstOrDefault(p => p.Id == "model") is { } odModel)
        {
            Assert.NotNull(odModel.ValueLabels);
            Assert.Equal(4, odModel.ValueLabels!.Count);
        }

        // A continuous (numeric) param carries NO labels — the UI keeps its knob.
        var amp = svc.Catalog.FirstOrDefault(m => m.Id == "amp");
        if (amp?.Params.FirstOrDefault(p => p.Id == "gain") is { } gain)
            Assert.Null(gain.ValueLabels);
    }

    /// <summary>A live engine + attached chain, or null when native can't load.</summary>
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
}
