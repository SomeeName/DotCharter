using DotCharter.Tone.Audio.Dsp.RsToneConversion;
using DotCharter.Tone.UI.Controls.Dsp;

namespace DotCharter.Tests.UI;

/// <summary>
///     The A/B compare decision model. Pure, so the rule that makes the control
///     safe — switching STORES the live rig into the slot being left — is proven
///     rather than assumed.
/// </summary>
public sealed class ToneCompareSlotsTests
{
    private static DcRigPreset Rig(string moduleId, float value = 0.5f)
        => new([new DcRigModule(moduleId, new Dictionary<string, float> { ["drive"] = value })]);

    private static string IdOf(DcRigPreset? rig) => rig?.Modules.Single().ModuleTypeId ?? "<null>";

    private static float DriveOf(DcRigPreset? rig) => rig!.Modules.Single().Params["drive"];

    /// <summary>
    ///     With nothing stored there is nothing to switch BACK to — but the
    ///     control still has to do something, because this is the state every
    ///     freshly opened rig is in. The first press FORKS: both slots come to
    ///     hold what is playing, the target goes live, and the chain is left
    ///     alone (null).
    ///     <para>
    ///         Until 0.61 this returned null with <c>Active</c> unmoved, which is
    ///         why the owner reported the A/B pair and SWITCH as buttons with no
    ///         function: on a rig nobody had seeded, they were.
    ///     </para>
    /// </summary>
    [Fact]
    public void WithNothingSeeded_TheFirstSwitchForksInsteadOfDoingNothing()
    {
        var slots = new ToneCompareSlots();

        Assert.False(slots.CanCompare);
        Assert.Equal(ToneCompareSlot.A, slots.Active);

        // Null = "do not touch the chain": a fork changes no sound.
        Assert.Null(slots.Toggle(Rig("overdrive", 0.4f)));

        Assert.Equal(ToneCompareSlot.B, slots.Active);
        Assert.Equal(0.4f, DriveOf(slots.Get(ToneCompareSlot.A)));
        Assert.Equal(0.4f, DriveOf(slots.Get(ToneCompareSlot.B)));
        Assert.True(slots.CanCompare);
    }

    [Fact]
    public void SeedingCopiesTheLiveRigIntoBothSlots_AndMakesALive()
    {
        var slots = new ToneCompareSlots();
        slots.SeedFrom(Rig("amp"));

        Assert.True(slots.HasA);
        Assert.True(slots.HasB);
        Assert.True(slots.CanCompare);
        Assert.Equal(ToneCompareSlot.A, slots.Active);
        Assert.Equal("amp", IdOf(slots.Get(ToneCompareSlot.A)));
        Assert.Equal("amp", IdOf(slots.Get(ToneCompareSlot.B)));
    }

    /// <summary>
    ///     The bug this type exists to prevent: tweak A, flip to B, flip back, and
    ///     the tweak is gone because nothing ever wrote the live rig home. Here the
    ///     round trip must return the TWEAKED rig, not the seeded one.
    /// </summary>
    [Fact]
    public void ARoundTripPreservesAnEditMadeOnTheLiveSlot()
    {
        var slots = new ToneCompareSlots();
        slots.SeedFrom(Rig("overdrive", 0.20f));

        // The player turns a knob: the live rig is now 0.90, A still holds 0.20.
        var toB = slots.Toggle(Rig("overdrive", 0.90f));
        Assert.Equal(ToneCompareSlot.B, slots.Active);
        Assert.Equal(0.20f, DriveOf(toB));            // B was the seeded copy

        // Coming back must hand back the EDIT, not the seed.
        var backToA = slots.Toggle(toB);
        Assert.Equal(ToneCompareSlot.A, slots.Active);
        Assert.Equal(0.90f, DriveOf(backToA));
    }

    /// <summary>
    ///     Forking into an empty slot is NON-DESTRUCTIVE — the half the old
    ///     "do nothing" rule was protecting. The slot being left is stored on
    ///     the way out exactly as on a normal switch, so the sound that was
    ///     playing survives in BOTH slots and a trip back returns it verbatim.
    /// </summary>
    [Fact]
    public void ForkingIntoAnEmptySlot_LosesNothingThatWasPlaying()
    {
        var slots = new ToneCompareSlots();
        slots.Store(ToneCompareSlot.A, Rig("amp", 0.3f));

        // "fuzz" is what is actually on the chain; A's 0.3 snapshot is stale.
        Assert.Null(slots.SelectSlot(ToneCompareSlot.B, Rig("fuzz", 0.9f)));

        Assert.Equal(ToneCompareSlot.B, slots.Active);
        Assert.Equal("fuzz", IdOf(slots.Get(ToneCompareSlot.A)));
        Assert.Equal("fuzz", IdOf(slots.Get(ToneCompareSlot.B)));

        // …and the way back hands the live sound over unchanged.
        Assert.Equal(0.9f, DriveOf(slots.SelectSlot(ToneCompareSlot.A, Rig("fuzz", 0.9f))));
    }

    /// <summary>
    ///     The A/B pair addresses a slot BY NAME (the header's two lamps), which
    ///     is the gesture SWITCH cannot make. Picking the slot already live is a
    ///     no-op on the chain — but it still stores, so an edit made since the
    ///     last switch belongs to the slot the player is sitting on.
    /// </summary>
    [Fact]
    public void SelectingTheLiveSlot_TouchesNothingButKeepsItCurrent()
    {
        var slots = new ToneCompareSlots();
        slots.SeedFrom(Rig("amp", 0.2f));

        Assert.Null(slots.SelectSlot(ToneCompareSlot.A, Rig("amp", 0.7f)));

        Assert.Equal(ToneCompareSlot.A, slots.Active);
        Assert.Equal(0.7f, DriveOf(slots.Get(ToneCompareSlot.A)));
        Assert.Equal(0.2f, DriveOf(slots.Get(ToneCompareSlot.B)));
    }

    /// <summary>Naming the OTHER slot is the switch, with the same store-on-exit
    /// rule — the lamps and SWITCH are one behaviour, deliberately.</summary>
    [Fact]
    public void SelectingTheOtherSlot_SwitchesAndStoresLikeToggleDoes()
    {
        var slots = new ToneCompareSlots();
        slots.SeedFrom(Rig("overdrive", 0.20f));

        var toB = slots.SelectSlot(ToneCompareSlot.B, Rig("overdrive", 0.90f));

        Assert.Equal(ToneCompareSlot.B, slots.Active);
        Assert.Equal(0.20f, DriveOf(toB));
        Assert.Equal(0.90f, DriveOf(slots.Get(ToneCompareSlot.A)));
    }

    [Fact]
    public void StoringIntoTheInactiveSlot_DoesNotChangeWhatIsLive()
    {
        var slots = new ToneCompareSlots();
        slots.SeedFrom(Rig("amp"));
        slots.Store(ToneCompareSlot.B, Rig("namamp"));

        Assert.Equal(ToneCompareSlot.A, slots.Active);
        Assert.Equal("amp", IdOf(slots.Get(ToneCompareSlot.A)));
        Assert.Equal("namamp", IdOf(slots.Get(ToneCompareSlot.B)));
    }

    [Fact]
    public void ReSeeding_CollapsesBothSlotsOntoTheNewSound()
    {
        var slots = new ToneCompareSlots();
        slots.SeedFrom(Rig("amp"));
        slots.Toggle(Rig("amp"));                     // now on B
        slots.SeedFrom(Rig("namamp"));                // e.g. a preset was applied

        Assert.Equal(ToneCompareSlot.A, slots.Active);
        Assert.Equal("namamp", IdOf(slots.Get(ToneCompareSlot.A)));
        Assert.Equal("namamp", IdOf(slots.Get(ToneCompareSlot.B)));
    }
}
