using DotCharter.Tone.Audio.Dsp;

namespace DotCharter.Tests.UI;

/// <summary>
///     Every engine rebuild — a device change, a buffer-size change, the audio
///     watchdog's recovery — creates a NEW native engine whose DSP chain is empty,
///     and <c>ToneSessionService</c> repopulates it on <c>EngineAttached</c> with
///     freshly-issued node ids.
///
///     <para>
///         The board is a DI singleton reconciled only when a host OPENS the rig,
///         so with the rig already on screen it kept every card bound to a node id
///         the new engine never handed out: pedals that do not exist, drawn over a
///         chain that has different ones, and every knob write refused in silence.
///     </para>
///     <para>
///         Asserted against the CHAIN's node ids, never against a card count — two
///         boards of the same size are exactly the state this bug produced.
///     </para>
/// </summary>
public sealed class PedalboardEngineRebuildTests
{
    private static IReadOnlyList<DspModuleType> Catalog() =>
        new[] { "amp", "cab", "reverb" }
            .Select(id => new DspModuleType(id, id,
                new[] { new DspParamInfo("p", "p", -100f, 100f, 0f) }))
            .ToList();

    [Fact]
    public void AnEngineRebuild_RebuildsTheBoardInsteadOfLeavingItOnDeadNodeIds()
    {
        var chain = new FakeDspChainService(Catalog());
        chain.AttachEngine(1);
        var amp = chain.AddNode("amp")!;

        // The tone session restores this seat's saved rig into the fresh native
        // chain on EngineAttached; it subscribes before the rig surface is ever
        // opened, which is the shipped order.
        chain.EngineAttached += () =>
        {
            if (chain.GetNodes().Count == 0) chain.AddNode("cab");
        };

        var rig = TestRigBuilders.Pedalboard(chain, new FakeDspTonePresetService(), FakeLocalization.English());
        Assert.Equal([amp.NodeId], rig.Nodes.Select(n => n.NodeId));

        chain.AttachEngine(2); // a device change: new engine, empty chain, restored rig

        var live = chain.GetNodes();
        Assert.Equal(["cab"], live.Select(n => n.ModuleTypeId));
        // The board mirrors the chain that exists NOW…
        Assert.Equal(live.Select(n => n.NodeId), rig.Nodes.Select(n => n.NodeId));
        // …and holds no card addressing the engine that was destroyed.
        Assert.DoesNotContain(amp.NodeId, rig.Nodes.Select(n => n.NodeId));
    }

    /// <summary>A DETACH (the handle going to zero before an engine is destroyed)
    /// empties the chain and raises no attach — the board must not be left drawing
    /// the pedals of a freed engine either.</summary>
    [Fact]
    public void ADetachThenAReattach_LeavesNoCardFromTheOldEngine()
    {
        var chain = new FakeDspChainService(Catalog());
        chain.AttachEngine(1);
        chain.AddNode("amp");
        chain.AddNode("reverb");

        var rig = TestRigBuilders.Pedalboard(chain, new FakeDspTonePresetService(), FakeLocalization.English());
        Assert.Equal(2, rig.Nodes.Count);

        chain.AttachEngine(nint.Zero); // detach-before-destroy
        chain.AttachEngine(3);         // the replacement engine, nothing restored

        Assert.Empty(chain.GetNodes());
        Assert.Empty(rig.Nodes);
    }
}
