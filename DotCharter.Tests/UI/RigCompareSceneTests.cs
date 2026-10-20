using DotCharter.Tone.Audio.Dsp;
using DotCharter.Tone.UI.ViewModels.PlayMode;

namespace DotCharter.Tests.UI;

/// <summary>
///     The Amp &amp; FX rig header's SCENE strip — the A and B lamps, SWITCH and
///     "Copy to both".
///
///     <para>
///         <b>The owner bug this ends (v0.48.5).</b> "Several buttons in the Amp
///         &amp; FX rig have no function: A/B, Copy, Both, Switch." All four are
///         that one strip, and two things were true at once: the A/B pair was
///         drawn as two <c>Border</c>s, so it could not be clicked at all, and
///         SWITCH sat behind <c>CanCompare</c>, which is FALSE on every rig
///         nobody had pressed "Copy to both" on first. The strip therefore read
///         as three dead controls in the one state every player meets it in.
///     </para>
///     <para>
///         These drive the COMMANDS the markup binds, and assert against the
///         preset service (what was pushed, and to WHICH seat) rather than
///         against the strip's own lamp state — a selector that changes a
///         property nobody reads is the shape this repo has shipped four times.
///         The markup half (the pair is buttons, every button resolves a real
///         command) is <see cref="AmpFxRigNoDeadButtonsTests" />.
///     </para>
/// </summary>
public sealed class RigCompareSceneTests
{
    private static IReadOnlyList<DspModuleType> Catalog() =>
        new[] { "amp", "cab", "gain", "reverb" }
            .Select(id => new DspModuleType(id, id,
                new[] { new DspParamInfo("p", "p", -100f, 100f, 0f) }))
            .ToList();

    /// <summary>Two independent in-memory chains behind a per-player surface.</summary>
    private sealed class FakeChains : IPlayerDspChains
    {
        private readonly List<IDspChainService> _all;

        public FakeChains(int seats) =>
            _all = Enumerable.Range(0, seats)
                .Select(_ => (IDspChainService)new FakeDspChainService(Catalog()))
                .ToList();

        public int MaxPlayers => _all.Count;
        public IReadOnlyList<IDspChainService> All => _all;
        public IDspChainService For(int player) => _all[Math.Clamp(player, 0, _all.Count - 1)];
        public void AttachEngineToAll(nint engine) { }
    }

    private static (PedalboardViewModel Rig, FakeDspTonePresetService Presets) NewRig(
        FakeChains? chains = null)
    {
        var presets = new FakeDspTonePresetService();
        var chain = chains?.For(0) ?? new FakeDspChainService(Catalog());
        return (TestRigBuilders.Pedalboard(chain, presets, FakeLocalization.English(), chains: chains), presets);
    }

    // ──────────────────────────────────────────────────── the A/B pair

    /// <summary>
    ///     THE BUG. On a rig straight out of the constructor — nothing seeded,
    ///     which is what every player sees — pressing B must move the strip. It
    ///     used to be impossible to press at all.
    /// </summary>
    [Fact]
    public void OnAFreshRig_PressingB_MovesTheStripToB()
    {
        var (rig, _) = NewRig();
        rig.AddGear(Catalog().First(m => m.Id == "amp"));

        Assert.True(rig.IsCompareSlotA);

        rig.SelectCompareSlotCommand.Execute("B");

        Assert.True(rig.IsCompareSlotB);
        Assert.False(rig.IsCompareSlotA);
    }

    /// <summary>
    ///     …and the way back RESTORES what was on the board when B was left, not
    ///     the snapshot it started from. The chain is pushed through the preset
    ///     service, so the assertion is on what got applied.
    /// </summary>
    [Fact]
    public void ARoundTripThroughTheLampsPushesTheOtherScenesRigBack()
    {
        var (rig, presets) = NewRig();
        rig.AddGear(Catalog().First(m => m.Id == "amp"));

        // A → B forks: same sound, so nothing is applied.
        rig.SelectCompareSlotCommand.Execute("B");
        Assert.Empty(presets.Applied);

        // Build a different sound on B, then go back to A.
        rig.AddGear(Catalog().First(m => m.Id == "reverb"));
        rig.SelectCompareSlotCommand.Execute("A");

        Assert.Equal(["compare"], presets.Applied);
        var back = presets.AppliedPerPlayer.Single();
        Assert.Equal(0, back.Player);
        Assert.True(rig.IsCompareSlotA);
    }

    /// <summary>A lamp that is already lit must not push a rig — re-applying the
    /// live chain tears the board down and rebuilds it for nothing.</summary>
    [Fact]
    public void PressingTheLampThatIsAlreadyLit_AppliesNothing()
    {
        var (rig, presets) = NewRig();
        rig.SeedCompareCommand.Execute(null);

        rig.SelectCompareSlotCommand.Execute("A");

        Assert.Empty(presets.Applied);
        Assert.True(rig.IsCompareSlotA);
    }

    /// <summary>The parameter comes from markup as a bare string; anything that
    /// is not a slot name must be ignored rather than throw on a click.</summary>
    [Fact]
    public void AnUnknownSlotName_IsIgnored()
    {
        var (rig, presets) = NewRig();
        rig.SeedCompareCommand.Execute(null);

        rig.SelectCompareSlotCommand.Execute("C");
        rig.SelectCompareSlotCommand.Execute(null);

        Assert.True(rig.IsCompareSlotA);
        Assert.Empty(presets.Applied);
    }

    // ──────────────────────────────────────────────────── SWITCH

    /// <summary>
    ///     SWITCH is never refused. It used to be bound to <c>CanCompare</c> in
    ///     markup (<c>IsEnabled</c>), false until something seeded the slots — so
    ///     the first press of a button labelled SWITCH, on the rig as it opens,
    ///     did nothing.
    /// </summary>
    [Fact]
    public void OnAFreshRig_SwitchIsPressableAndMovesTheStrip()
    {
        var (rig, _) = NewRig();

        Assert.True(rig.ToggleCompareCommand.CanExecute(null));

        rig.ToggleCompareCommand.Execute(null);
        Assert.True(rig.IsCompareSlotB);

        rig.ToggleCompareCommand.Execute(null);
        Assert.True(rig.IsCompareSlotA);
    }

    /// <summary>"Copy to both" collapses both scenes onto what is playing and
    /// lands on A — the "start comparing from here" action.</summary>
    [Fact]
    public void CopyToBoth_CollapsesTheScenesAndReturnsToA()
    {
        var (rig, presets) = NewRig();
        rig.ToggleCompareCommand.Execute(null);
        Assert.True(rig.IsCompareSlotB);

        rig.SeedCompareCommand.Execute(null);

        Assert.True(rig.IsCompareSlotA);
        // Seeding is a store, never a push: the sound the player is hearing is
        // already the one being copied.
        Assert.Empty(presets.Applied);
    }

    // ──────────────────────────────────────────────────── the seat

    /// <summary>
    ///     THE WRONG-GUITAR BUG. Couch co-op gives every seat its own chain (ABI
    ///     v28) and the rig's seat picker repoints the board at it — but the
    ///     compare push went through <c>Apply</c>, which is seat 0 for ever. On
    ///     player 2's board, SWITCH compared player 1's tone: the exact shape the
    ///     in-rig tuner carried until 0.59.
    /// </summary>
    [Fact]
    public void TheComparePush_LandsOnTheSelectedSeat_NotSeatZero()
    {
        var chains = new FakeChains(2);
        var (rig, presets) = NewRig(chains);
        rig.SetSeats([new RigSeatOption(0, "Ada"), new RigSeatOption(1, "Bo")]);

        rig.SelectedSeat = rig.Seats[1];
        Assert.Equal(1, rig.SelectedPlayer);

        // Fork onto B (nothing pushed), build a different sound, come back.
        rig.SelectCompareSlotCommand.Execute("B");
        rig.AddGear(Catalog().First(m => m.Id == "reverb"));
        rig.SelectCompareSlotCommand.Execute("A");

        var push = Assert.Single(presets.AppliedPerPlayer);
        Assert.Equal(1, push.Player);
        Assert.Equal("compare", push.PresetId);
    }
}
