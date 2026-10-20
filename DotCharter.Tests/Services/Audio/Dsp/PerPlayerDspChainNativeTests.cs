using DotCharter.Tone.Audio.Dsp;
using JuceSharp;

namespace DotCharter.Tests.Services.Audio.Dsp;

/// <summary>
///     Per-player tone rigs (ABI v28) driven through the SHIPPING managed stack —
///     <see cref="PlayerDspChains" /> → <see cref="JuceDspChainService" /> →
///     <c>DspNativeInterop</c> → the real native DLL — and verified OFFLINE.
///     <para>
///         Headless by construction: the engine is created and immediately
///         stopped, and every render goes through
///         <c>dca_engine_dsp_process_buffer_for_player</c>. No device is ever
///         opened, so this never touches the machine's sound card.
///     </para>
///     <para>
///         <c>DspPerPlayerChainTests</c> in the JuceSharp suite pins the
///         same properties at the raw ABI. THIS file exists because the managed
///         path in between is where per-player-ness can silently collapse: one
///         interop instance shared across seats, a mirror that reports another
///         seat's nodes, or an <c>AddNode</c> that forgets its player would all
///         leave the native side perfectly correct and the app still giving both
///         players one tone.
///     </para>
/// </summary>
[Collection("NativeEngine")]
public sealed class PerPlayerDspChainNativeTests : IClassFixture<PerPlayerDspChainNativeTests.NativeFixture>
{
    private const int Sr = 48000;
    private const int N = 4096;

    private readonly NativeFixture _fx;

    public PerPlayerDspChainNativeTests(NativeFixture fx) => _fx = fx;

    private static float[] Probe()
    {
        var buf = new float[N];
        for (var i = 0; i < N; i++) buf[i] = 0.25f * MathF.Sin(2f * MathF.PI * 110f * i / Sr);
        return buf;
    }

    private static float Peak(float[] a)
    {
        var p = 0f;
        foreach (var v in a) p = MathF.Max(p, MathF.Abs(v));
        return p;
    }

    /// <summary>Renders one seat's rig offline and returns the left channel.</summary>
    private static float[] Render(JuceDspChainService chain)
    {
        float[] l = Probe(), r = Probe();
        Assert.True(chain.ProcessOfflineBuffer(l, r, N, Sr));
        return l;
    }

    private (JuceDspChainService P1, JuceDspChainService P2) Seats()
    {
        Assert.SkipWhen(_fx.Chains is null, "Native DSP surface unavailable.");
        var chains = _fx.Chains!;
        Assert.SkipWhen(chains.MaxPlayers < 2, "Native engine hosts fewer than two rigs.");

        var p1 = Assert.IsType<JuceDspChainService>(chains.For(0));
        var p2 = Assert.IsType<JuceDspChainService>(chains.For(1));
        _fx.ClearAll();
        return (p1, p2);
    }

    [Fact]
    public void EachSeatDrivesItsOwnNativeChain_SoTwoPlayersGetTwoDifferentSounds()
    {
        var (p1, p2) = Seats();

        // Player 1: +12 dB. Player 2: -12 dB. One shared chain cannot do this.
        var n1 = p1.AddNode("gain");
        var n2 = p2.AddNode("gain");
        Assert.NotNull(n1);
        Assert.NotNull(n2);
        Assert.True(p1.SetParam(n1!.NodeId, "gainDb", 12f));
        Assert.True(p2.SetParam(n2!.NodeId, "gainDb", -12f));

        var peakIn = Peak(Probe());
        Assert.InRange(Peak(Render(p1)) / peakIn, 3.9f, 4.1f);
        Assert.InRange(Peak(Render(p2)) / peakIn, 0.24f, 0.26f);
    }

    [Fact]
    public void OneSeatsPedalboardEdits_LeaveTheOtherSeatsAudioBitIdentical()
    {
        var (p1, p2) = Seats();

        var n1 = p1.AddNode("gain")!;
        var n2 = p2.AddNode("gain")!;
        Assert.True(p1.SetParam(n1.NodeId, "gainDb", 0f));
        Assert.True(p2.SetParam(n2.NodeId, "gainDb", 6f));

        var before = Render(p2);

        // Everything a player can do to their own board.
        Assert.True(p1.SetParam(n1.NodeId, "gainDb", 18f));
        var extra = p1.AddNode("gain")!;
        Assert.True(p1.SetParam(extra.NodeId, "gainDb", -24f));
        Assert.True(p1.SetBypass(n1.NodeId, true));
        Assert.True(p1.MoveNode(extra.NodeId, 0));
        Assert.True(p1.RemoveNode(extra.NodeId));

        Assert.Equal(before, Render(p2));
    }

    [Fact]
    public void EachSeatsManagedMirror_ShowsOnlyItsOwnPedals()
    {
        // The mirror is authoritative for the UI (native has no "module type of
        // node" query), so a mirror that leaked across seats would render player
        // 2's pedalboard with player 1's pedals on it.
        var (p1, p2) = Seats();

        p1.AddNode("gain");
        p1.AddNode("delay");
        p2.AddNode("reverb");

        Assert.Equal(new[] { "gain", "delay" }, p1.GetNodes().Select(n => n.ModuleTypeId).ToArray());
        Assert.Equal(new[] { "reverb" }, p2.GetNodes().Select(n => n.ModuleTypeId).ToArray());
    }

    [Fact]
    public void NodeIdsAreEngineGlobal_SoASeatsIdsCarryItsPlayer()
    {
        var (p1, p2) = Seats();

        var a = p1.AddNode("gain")!;
        var b = p2.AddNode("gain")!;

        Assert.Equal(0, DspPlayers.PlayerOfNodeId(a.NodeId));
        Assert.Equal(1, DspPlayers.PlayerOfNodeId(b.NodeId));
        Assert.NotEqual(a.NodeId, b.NodeId);

        // A node-scoped op needs no player argument and still lands on the right
        // seat — that is the contract the whole small ABI delta rests on.
        Assert.True(p2.SetParam(b.NodeId, "gainDb", -6f));
        Assert.Equal(-6f, p2.GetParam(b.NodeId, "gainDb")!.Value, 4);
        Assert.Equal(0f, p1.GetParam(a.NodeId, "gainDb")!.Value, 4);
    }

    [Fact]
    public void EverySeatSharesOneCatalogue_LoadedOnce()
    {
        Assert.SkipWhen(_fx.Chains is null, "Native DSP surface unavailable.");
        var chains = _fx.Chains!;

        var head = chains.For(0).Catalog;
        Assert.NotEmpty(head);
        foreach (var chain in chains.All)
            Assert.Same(head, chain.Catalog);
    }

    [Fact]
    public void DetachingTheEngine_ClearsEverySeatsMirror()
    {
        // The detach-before-destroy rule is per SEAT since v28: a seat left
        // pointing at a freed engine is a use-after-free the moment its
        // pedalboard moves a knob.
        Assert.SkipWhen(_fx.Chains is null, "Native DSP surface unavailable.");
        var chains = _fx.Chains!;
        _fx.ClearAll();

        chains.For(0).AddNode("gain");
        if (chains.MaxPlayers > 1) chains.For(1).AddNode("gain");

        chains.AttachEngineToAll(null);
        foreach (var chain in chains.All) Assert.Empty(chain.GetNodes());

        // Re-attach for the next test in the fixture.
        chains.AttachEngineToAll(_fx.Engine);
    }

    public sealed class NativeFixture : IDisposable
    {
        private readonly JuceAudioEngine? _engine;

        public NativeFixture()
        {
            try
            {
                _engine = new JuceAudioEngine(new JuceEngineConfig { EnableConsoleLog = false });
                _engine.Stop(); // offline path must be the only processor — no device
                Handle = _engine.NativeHandle;

                var chains = PlayerDspChains.CreateNative(DspPlayers.MaxPlayers);
                chains.AttachEngineToAll(_engine);
                Chains = chains;
            }
            catch
            {
                _engine?.Dispose();
                _engine = null;
                Handle = nint.Zero;
                Chains = null;
            }
        }

        public nint Handle { get; }

        /// <summary>The wrapper object the seats are attached THROUGH — every rig
        /// call pins it, so a chain op racing this fixture's Dispose is a caught
        /// ObjectDisposedException rather than a native crash.</summary>
        public JuceAudioEngine? Engine => _engine;
        public PlayerDspChains? Chains { get; }

        /// <summary>Empties every seat so tests never inherit each other's rigs
        /// (the fixture is shared across the class).</summary>
        public void ClearAll()
        {
            if (Chains is null) return;
            foreach (var chain in Chains.All)
            {
                foreach (var node in chain.GetNodes())
                    chain.RemoveNode(node.NodeId);
            }
        }

        public void Dispose()
        {
            try { Chains?.AttachEngineToAll(null); } catch { /* ignored */ }
            try { _engine?.Dispose(); } catch { /* ignored */ }
        }
    }
}
