using DotCharter.Tone.Audio.Dsp;
using DotCharter.Tone.Audio.Dsp.RsToneConversion;
using DotCharter.Tone.UI.ViewModels.PlayMode;

namespace DotCharter.Tests.UI;

/// <summary>
///     The command bar's master BYPASS is the rig's off switch, and it is an ARMED
///     gesture rather than a per-node state — so anything that replaces the board
///     underneath it has to keep it true.
///
///     <para>
///         These assert against the CHAIN, never against the lamp: a lamp that
///         says BYPASS over a board that is making sound is the whole bug.
///     </para>
/// </summary>
public sealed class RigMasterBypassTests
{
    private static IReadOnlyList<DspModuleType> Catalog() =>
        new[] { "amp", "cab", "reverb", "delay" }
            .Select(id => new DspModuleType(id, id,
                new[] { new DspParamInfo("p", "p", -100f, 100f, 0f) }))
            .ToList();

    private static PedalboardViewModel NewRig(out FakeDspChainService chain)
    {
        chain = new FakeDspChainService(Catalog());
        return TestRigBuilders.Pedalboard(chain, new FakeDspTonePresetService(), FakeLocalization.English());
    }

    private static DspModuleType Gear(IDspChainService chain, string id) =>
        chain.Catalog.First(m => m.Id == id);

    /// <summary>
    ///     Picking a tone while the rig is switched OFF must not switch it back on
    ///     behind a lamp that still says BYPASS.
    ///     <para>
    ///         <c>DcRigApplier</c> writes the incoming rig's OWN bypass flags onto
    ///         every node it reuses or adds, and those are un-bypassed — so a
    ///         preset apply, an A/B compare push or a capture load brought the
    ///         sound straight back while <c>IsMasterBypassed</c> stayed true. The
    ///         next press then "restored" a set of node ids that half belonged to a
    ///         board that no longer existed.
    ///     </para>
    /// </summary>
    [Fact]
    public void AToneAppliedWhileTheRigIsOff_LeavesItOff()
    {
        var rig = NewRig(out var chain);
        rig.AddGear(Gear(chain, "amp"));
        rig.AddGear(Gear(chain, "cab"))!.Bypassed = true;   // switched out by hand

        rig.ToggleMasterBypassCommand.Execute(null);
        Assert.True(rig.IsMasterBypassed);
        Assert.All(chain.GetNodes(), n => Assert.True(n.Bypassed));

        // A tone switch: exactly what the preset browser's apply does to the chain
        // (reuse the amp, drop the cab, add a reverb — all un-bypassed), then the
        // board reconciles.
        new DcRigApplier(chain).Apply(new DcRigPreset(
        [
            new DcRigModule("amp", new Dictionary<string, float>(StringComparer.Ordinal)),
            new DcRigModule("reverb", new Dictionary<string, float>(StringComparer.Ordinal))
        ]));
        rig.Sync();

        // The lamp still says the rig is off, so the rig has to BE off.
        Assert.True(rig.IsMasterBypassed);
        Assert.Equal(["amp", "reverb"], chain.GetNodes().Select(n => n.ModuleTypeId));
        Assert.All(chain.GetNodes(), n => Assert.True(n.Bypassed));

        // Turning the rig back on switches in exactly what the NEW tone asked for.
        // The hand-bypassed cab is gone, so nothing may inherit its claim.
        rig.ToggleMasterBypassCommand.Execute(null);
        Assert.False(rig.IsMasterBypassed);
        Assert.All(chain.GetNodes(), n => Assert.False(n.Bypassed));
    }

    /// <summary>A pedal dropped onto a board that is switched OFF joins it switched
    /// out — and comes back on with the rest of the rig, because the player never
    /// asked for that one to be bypassed on its own.</summary>
    [Fact]
    public void APedalAddedWhileTheRigIsOff_JoinsItSwitchedOut()
    {
        var rig = NewRig(out var chain);
        rig.AddGear(Gear(chain, "amp"));
        rig.ToggleMasterBypassCommand.Execute(null);

        var added = rig.AddGear(Gear(chain, "delay"));

        Assert.NotNull(added);
        Assert.True(added!.Bypassed);
        Assert.All(chain.GetNodes(), n => Assert.True(n.Bypassed));

        rig.ToggleMasterBypassCommand.Execute(null);
        Assert.All(chain.GetNodes(), n => Assert.False(n.Bypassed));
    }
}
