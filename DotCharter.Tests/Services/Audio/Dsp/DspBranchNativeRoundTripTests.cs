using DotCharter.Tone.Audio.Dsp;
using JuceSharp;

namespace DotCharter.Tests.Services.Audio.Dsp;

/// <summary>
/// Real-native round-trip for the ABI-v23 split/parallel branch-tag surface,
/// exercised through the PARENT's managed stack (<see cref="JuceDspChainService"/>
/// over the real <c>DspNativeInterop</c> P/Invokes) attached to a live
/// <see cref="JuceAudioEngine"/>. Mirrors <c>DspStringParamNativeRoundTripTests</c>:
/// everything skips cleanly with <see cref="Assert.SkipWhen"/> when the native DLL
/// / audio engine can't load, so it NEVER depends on native being present. On a
/// machine with the built DLL it proves the branch setter/getter and the
/// out-of-range reject against the real engine.
/// </summary>
[Collection("NativeEngine")]
public sealed class DspBranchNativeRoundTripTests
{
    [Fact]
    public void NodeBranch_RoundTrips_ThroughTheManagedStack()
    {
        using var env = NativeChainEnv.TryCreate();
        Assert.SkipWhen(env is null, "native DSP ABI (juce_sharp.dll) / audio engine not loadable in this test environment");

        var svc = env!.Chain;
        Assert.SkipWhen(!svc.Catalog.Any(m => m.Id == "overdrive"), "native catalogue has no 'overdrive' module");

        var od = svc.AddNode("overdrive");
        Assert.NotNull(od);

        // Default is 0 (unassigned → branch 1).
        Assert.Equal(0, svc.GetNodeBranch(od!.NodeId));

        // Set each valid branch and read it back.
        foreach (var b in new[] { 1, 2, 3, 0 })
        {
            Assert.True(svc.SetNodeBranch(od.NodeId, b));
            Assert.Equal(b, svc.GetNodeBranch(od.NodeId));
        }

        // Out-of-range ids are rejected native-side (InvalidArg) and keep the
        // previous tag.
        Assert.True(svc.SetNodeBranch(od.NodeId, 2));
        Assert.False(svc.SetNodeBranch(od.NodeId, -1));
        Assert.False(svc.SetNodeBranch(od.NodeId, 4));
        Assert.Equal(2, svc.GetNodeBranch(od.NodeId));

        // The tag survives a reorder (move) natively.
        var amp = svc.Catalog.Any(m => m.Id == "amp") ? svc.AddNode("amp") : null;
        if (amp is not null)
        {
            Assert.True(svc.MoveNode(od.NodeId, 1));
            Assert.Equal(2, svc.GetNodeBranch(od.NodeId));
        }
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
