using DotCharter.Tone.Audio.Dsp.RsToneConversion;

namespace DotCharter.Tone.UI.Controls.Dsp;

/// <summary>Which of the two compare slots is live.</summary>
public enum ToneCompareSlot
{
    A,
    B
}

/// <summary>
///     The A/B compare model: two rig snapshots and which one is live, with the
///     rule that makes switching non-destructive.
///     <para>
///         Comparing two tones by ear is the only way to choose between them, and
///         it has to be instant — a difference you can only hear across ten
///         seconds of menu navigation is a difference you cannot hear. Every amp
///         sim has this control for that reason. It matters more here than usual
///         because the catalogue ships a capture-backed and an
///         algorithmic version of the same 70 tones and the whole point is to
///         pick between them.
///     </para>
///     <para>
///         <b>The rule:</b> switching slots first STORES the live rig back into
///         the slot being left, and an EMPTY target is forked from it rather
///         than refused. Without that, tweaking a knob on A, flipping to B
///         and flipping back silently discards the tweak — the classic A/B bug,
///         and the reason this is a tested pure type rather than two fields on a
///         view model. Storing on the way out is also what makes the control safe
///         to use as a scratchpad: nothing you can hear is ever lost by pressing
///         the button.
///     </para>
///     <para>
///         Pure and Avalonia-free: it decides, it never applies. The caller reads
///         the live chain and pushes the returned rig.
///     </para>
/// </summary>
public sealed class ToneCompareSlots
{
    private DcRigPreset? _a;
    private DcRigPreset? _b;

    /// <summary>The slot currently live on the chain.</summary>
    public ToneCompareSlot Active { get; private set; } = ToneCompareSlot.A;

    /// <summary>True once the inactive slot holds a stored sound, so a switch
    /// brings that back rather than FORKING the live one into it. Both cases are
    /// live actions — this is an inspector for tests and callers that want to
    /// word the difference, never a reason to disable a control.</summary>
    public bool CanCompare => Other(Active) is not null;

    public bool HasA => _a is not null;

    public bool HasB => _b is not null;

    /// <summary>Stores a rig into a slot without changing which one is live. Used
    /// to seed the other slot from the current sound ("copy A to B") so the two
    /// start from a common point and only the change under test differs.</summary>
    public void Store(ToneCompareSlot slot, DcRigPreset? rig)
    {
        if (slot == ToneCompareSlot.A) _a = rig;
        else _b = rig;
    }

    public DcRigPreset? Get(ToneCompareSlot slot) => slot == ToneCompareSlot.A ? _a : _b;

    /// <summary>
    ///     Makes <paramref name="slot" /> the live one, saving
    ///     <paramref name="liveRig" /> into the slot being left. Returns the rig
    ///     to APPLY, or null when the chain must not be touched (already there,
    ///     or the target was empty and has just been FORKED from what is
    ///     playing).
    ///     <para>
    ///         <b>The fork rule.</b> An empty target is filled from the live rig
    ///         and becomes active. That is what makes the control usable from the
    ///         first frame: an empty slot would otherwise mean "do nothing at all",
    ///         so on a freshly opened rig the A/B pair and the SWITCH button would
    ///         both be inert until somebody found "Copy to both" first.
    ///         Forking is not destructive: the slot
    ///         being left is stored on the way out exactly as on a normal switch,
    ///         so both slots hold the sound that was playing and nothing audible
    ///         is lost.
    ///     </para>
    /// </summary>
    public DcRigPreset? SelectSlot(ToneCompareSlot slot, DcRigPreset? liveRig)
    {
        // Already here: keep the slot current (an edit made since the last
        // switch belongs to THIS slot) and tell the caller to change nothing.
        if (slot == Active)
        {
            Store(Active, liveRig);
            return null;
        }

        var incoming = Get(slot);
        Store(Active, liveRig);
        Active = slot;

        // Fork: the target was empty, so it starts as a copy of what is
        // playing. The sound does not change, hence null — applying a rig
        // identical to the live one would tear the board down and rebuild it
        // for nothing.
        if (incoming is null)
        {
            Store(slot, liveRig);
            return null;
        }

        return incoming;
    }

    /// <summary>Switches to the OTHER slot — the SWITCH button. One rule with
    /// <see cref="SelectSlot" />, deliberately: two controls for the same verb
    /// that disagreed about the empty case is how the pair came to read as
    /// broken.</summary>
    public DcRigPreset? Toggle(DcRigPreset? liveRig)
        => SelectSlot(Active == ToneCompareSlot.A ? ToneCompareSlot.B : ToneCompareSlot.A, liveRig);

    /// <summary>Copies the live rig into BOTH slots and makes A live — the "start
    /// comparing from here" action. After this, changing the sound and pressing
    /// compare toggles between the change and the original.</summary>
    public void SeedFrom(DcRigPreset? liveRig)
    {
        _a = liveRig;
        _b = liveRig;
        Active = ToneCompareSlot.A;
    }

    /// <summary>
    ///     Empties BOTH slots and returns to A — for when the board these two
    ///     snapshots describe is gone from the screen (the rig is
    ///     repointed at another seat, or the player profile switched).
    ///     <para>
    ///         Deliberately not <see cref="SeedFrom" />. Seeding would store the
    ///         new board twice, and the first press would then apply a rig
    ///         identical to the live one — tearing the chain down and rebuilding
    ///         it for no audible change, which is the very thing the fork rule
    ///         returns null to avoid. Emptying leaves the FORK to happen on the
    ///         first press, from whatever is playing then.
    ///     </para>
    /// </summary>
    public void Reset()
    {
        _a = null;
        _b = null;
        Active = ToneCompareSlot.A;
    }

    private DcRigPreset? Other(ToneCompareSlot slot)
        => slot == ToneCompareSlot.A ? _b : _a;
}
