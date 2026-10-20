using DotCharter.Tone.Audio.Dsp;
using DotCharter.Tone.Audio.Dsp.Presets;
using DotCharter.Tone.Audio.Dsp.RsToneConversion;
using DotCharter.Tone.Infrastructure.Storage;

namespace DotCharter.Tests.Audio.Dsp;

/// <summary>
///     Per-seat tone persistence (ABI v28). Player 2's board has to survive an
///     engine rebuild and a quit exactly as player 1's does — and, more
///     importantly, the two must not be able to overwrite each other.
///     <para>
///         Since 0.45 the seats live inside a PROFILE's <c>tone-rig.json</c>
///         (<see cref="ProfileToneRigStore" />) rather than in <c>AppConfig</c>.
///         Profile and seat are different axes: these tests hold the profile
///         fixed (one store directory) and vary the seat. The other axis is
///         <c>ProfileToneRigTests</c>.
///     </para>
/// </summary>
public sealed class PerPlayerToneSessionTests : IDisposable
{
    private const nint EngineA = 0x1000;
    private const nint EngineB = 0x2000;

    /// <summary>A temp directory, never the real config root: this store writes a
    /// file, and pointing it at a production path would write into live owner
    /// data.</summary>
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "DotCharterTests", Guid.NewGuid().ToString("N"));

    private ProfileToneRigStore NewStore() => new(_dir);

    public void Dispose() => BestEffortDelete.Directory(_dir);

    private static PlayerDspChains Chains(int seats = 2)
        => new(Enumerable.Range(0, seats)
            .Select(p => (IDspChainService)new FakeDspChainService { Player = p })
            .ToList());

    /// <summary>Wires the same shape DI does: the preset service knows about the
    /// per-player chains, so <c>ApplyToPlayer</c> can reach seat 2's rig, and ONE
    /// rig store spans every seat.</summary>
    private ToneSessionSet Build(PlayerDspChains chains, FakeConfigurationService config,
        IProfileToneRigStore? store = null)
        => ToneSessionSet.Create(chains, config,
            new DspTonePresetService(chains.For(0), config, null, null, chains),
            store ?? NewStore());

    [Fact]
    public void SeatZeroPersistsToItsOwnSlot_AndTouchesNoOtherSeat()
    {
        var chains = Chains();
        var config = new FakeConfigurationService();
        config.Update(c => c.DefaultTonePresetId = string.Empty); // no first-run default

        var store = NewStore();
        using var set = Build(chains, config, store);
        set.StartAll();

        chains.For(0).AddNode("overdrive");
        set.FlushAll();

        Assert.NotNull(store.GetRig(0));
        Assert.Null(store.GetRig(1));
    }

    [Fact]
    public void SeatOnePersistsToItsOwnSlot_AndTheTwoBoardsStayDistinct()
    {
        var chains = Chains();
        var config = new FakeConfigurationService();
        config.Update(c => c.DefaultTonePresetId = string.Empty);

        var store = NewStore();
        using var set = Build(chains, config, store);
        set.StartAll();

        chains.For(0).AddNode("overdrive");
        chains.For(1).AddNode("delay");
        set.FlushAll();

        Assert.Equal(["overdrive"], store.GetRig(0)!.Modules.Select(m => m.ModuleTypeId));
        Assert.Equal(["delay"], store.GetRig(1)!.Modules.Select(m => m.ModuleTypeId));
    }

    [Fact]
    public void AnEmptySeatIsNeverPersistedOverAGoodSavedRig()
    {
        // The rule that matters most. An empty chain is indistinguishable from
        // "the engine is down", so writing it would let player 2 never plugging
        // in wipe player 2's saved board.
        var chains = Chains();
        var config = new FakeConfigurationService();
        config.Update(c => c.DefaultTonePresetId = string.Empty);

        using (var set = Build(chains, config))
        {
            set.StartAll();
            chains.For(1).AddNode("delay");
            set.FlushAll();
        }

        Assert.Equal(["delay"], NewStore().GetRig(1)!.Modules.Select(m => m.ModuleTypeId));

        // A fresh set over EMPTY chains must not overwrite it.
        var empty = Chains();
        using (var set = Build(empty, config))
        {
            set.FlushAll();
        }

        Assert.Equal(["delay"], NewStore().GetRig(1)!.Modules.Select(m => m.ModuleTypeId));
    }

    [Fact]
    public void EachSeatRestoresItsOwnRigWhenItsEngineAttaches()
    {
        var config = new FakeConfigurationService();
        config.Update(c => c.DefaultTonePresetId = string.Empty);

        // Session 1: build two different boards and persist them.
        var first = Chains();
        using (var set = Build(first, config))
        {
            set.StartAll();
            first.AttachEngineToAll(EngineA);
            first.For(0).AddNode("overdrive");
            first.For(1).AddNode("delay");
            set.FlushAll();
        }

        // Session 2: a fresh engine (the device-change case) restores each seat
        // to ITS OWN board, not to a copy of player 1's.
        var second = Chains();
        using var restored = Build(second, config);
        restored.StartAll();
        second.AttachEngineToAll(EngineB);

        Assert.Equal(["overdrive"], second.For(0).GetNodes().Select(n => n.ModuleTypeId));
        Assert.Equal(["delay"], second.For(1).GetNodes().Select(n => n.ModuleTypeId));
    }

    [Fact]
    public void ASeatWithNothingStored_DoesNotInheritPlayerOnesBoard()
    {
        // Two players who have not set anything up should start from the same
        // DEFAULT tone — not from a copy of each other's live rig, which the very
        // next poll would then persist as if they had chosen it.
        var config = new FakeConfigurationService();
        config.Update(c => c.DefaultTonePresetId = string.Empty);

        var first = Chains();
        using (var set = Build(first, config))
        {
            set.StartAll();
            first.AttachEngineToAll(EngineA);
            first.For(0).AddNode("overdrive");
            set.FlushAll();
        }

        Assert.Null(NewStore().GetRig(1));

        var second = Chains();
        using var restored = Build(second, config);
        restored.StartAll();
        second.AttachEngineToAll(EngineB);

        Assert.Equal(["overdrive"], second.For(0).GetNodes().Select(n => n.ModuleTypeId));
        Assert.Empty(second.For(1).GetNodes());
    }

    [Fact]
    public void TheFirstRunDefaultIsSeatZerosOnly_SoAnUnusedSeatLoadsNothing()
    {
        // The shipped default is capture-backed. Restoring it on every seat would
        // load a NAM model per UNUSED seat on every launch and every device
        // change, for a player who is not playing co-op at all. A co-op seat gets
        // the default from CoopRigApplier instead — only when someone sits in it.
        var chains = Chains(4);
        var config = new FakeConfigurationService();
        config.Update(c => c.DefaultTonePresetId = "fac-clean-1");
        var presets = new FakeDspTonePresetService();

        using var set = ToneSessionSet.Create(chains, config, presets, NewStore());
        set.StartAll();
        chains.AttachEngineToAll(EngineA);

        // Seat 0 gets the default; every other seat is untouched. (Start() also
        // restores inline when the chain is already empty, so seat 0 can legally
        // appear more than once — the claim here is about WHICH seats, not how
        // many times.)
        Assert.NotEmpty(presets.AppliedPerPlayer);
        Assert.Equal([0], presets.AppliedPerPlayer.Select(a => a.Player).Distinct());
        Assert.All(presets.AppliedPerPlayer, a => Assert.Equal("fac-clean-1", a.PresetId));
    }

    [Fact]
    public void WithNoPerPlayerChains_ASecondSeatsToneIsANoOp_NotAWriteOntoSeatZero()
    {
        // The dangerous fallback. A preset service with no registry has ONE
        // chain; applying seat 2's tone to it would overwrite player 1's board,
        // and the next persist would then save player 2's rig as player 1's.
        var chain = new FakeDspChainService();
        var config = new FakeConfigurationService();
        var presets = new DspTonePresetService(chain, config); // no IPlayerDspChains

        chain.AddNode("overdrive");
        var before = chain.GetNodes().Select(n => n.ModuleTypeId).ToArray();

        var tone = new DspTonePreset("x", "X", DspToneCategory.Clean, "",
            new DcRigPreset([new DcRigModule("delay", new Dictionary<string, float>())]));

        var result = presets.ApplyToPlayer(1, tone);

        Assert.True(result.NoOp);
        Assert.Equal(before, chain.GetNodes().Select(n => n.ModuleTypeId).ToArray());

        // ...while seat 0 still applies normally.
        Assert.False(presets.ApplyToPlayer(0, tone).NoOp);
    }
}
