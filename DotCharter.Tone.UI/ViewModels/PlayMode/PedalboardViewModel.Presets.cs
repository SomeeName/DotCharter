// PedalboardViewModel: the live-rig snapshot the preset browser saves and re-applies, plus the A/B compare slots.

using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using DotCharter.Tone.Audio.Dsp.Presets;
using DotCharter.Tone.Audio.Dsp.RsToneConversion;
using DotCharter.Tone.UI.Controls.Dsp;

namespace DotCharter.Tone.UI.ViewModels.PlayMode;

public sealed partial class PedalboardViewModel
{
    /// <summary>Snapshots the live chain into a <see cref="DcRigPreset"/> for the
    /// browser's save/overwrite actions — it is handed straight to
    /// <see cref="IDspTonePresetService.SaveUser"/>, with an existing user id for
    /// overwrite. <b>The snapshot LOOP itself is shared</b>
    /// (<see cref="DcRigSnapshot"/>): it existed four times, and the two copies
    /// that dropped the ABI-v22 string params and the ABI-v23 branch tag were the
    /// two that hand the player's own board back to them (the RS in-game-tone
    /// run's restore and the Tone Designer preview's), which is a MODELLESS —
    /// i.e. silent — namamp. The service's own dead fifth copy
    /// (<c>CaptureCurrent</c>) is gone for the same reason.
    /// <para>
    ///     WITH the rig's scenes (<see cref="DcRigSnapshot.FromChain" />), the live
    ///     values folded into the scene that is playing — so a save keeps every
    ///     scene, and an undo step can put a scene switch back.
    /// </para></summary>
    internal DcRigPreset BuildCurrentRig() => DcRigSnapshot.FromChain(_dsp);

    /// <summary>Applying a preset diffs a whole new chain onto the service; rebuild
    /// the pedal cards so the board mirrors the post-apply chain.</summary>
    private void OnPresetApplied()
    {
        Sync();
        // A preset apply is a whole new chain, so the unit the player removed off
        // the PREVIOUS one has nowhere to go back to — the recorded slot belongs
        // to a board that is gone.
        ClearRemovedUnitSnapshot();
        // A preset apply replaces the sound wholesale, so it is a new starting
        // point, not a step in the comparison that was running. Re-seeding here
        // is what keeps the button honest: after picking a tone, "compare" means
        // "against this tone", never against something the player last heard
        // three tones ago and has no way to identify.
        SeedCompare();
        // …and a scene asked for while this tone was arriving lands on it now.
        ApplyDeferredScene();
    }

    // ---- A/B compare -----------------------------------------------------------

    private readonly ToneCompareSlots _compare = new();

    /// <summary>The live compare slot, as a letter for the button face.</summary>
    [ObservableProperty] private string _compareSlotLabel = "A";

    /// <summary>
    ///     Swaps the live rig for the other compare slot (the header's SWITCH),
    ///     storing what is playing into the slot being left. That store is the
    ///     whole design: without it a knob moved on A is discarded the moment you
    ///     flip to B and back, which is exactly when a player is least likely to
    ///     notice they lost it.
    ///     <para>
    ///         <b>Never gated.</b> It must not sit behind <c>CanCompare</c>, which
    ///         is false on every freshly opened rig — so the first press
    ///         of a button labelled SWITCH would do nothing at all. The empty case is
    ///         a FORK now (<see cref="ToneCompareSlots.SelectSlot" />): the other
    ///         slot starts as a copy of what is playing, so the press always
    ///         lands somewhere and nothing audible is lost.
    ///     </para>
    /// </summary>
    [RelayCommand]
    private void ToggleCompare() => ApplyCompareSlot(_compare.Toggle(CompareLevel.Snapshot()));

    /// <summary>
    ///     Goes straight to slot A or B — the header's A/B pair, which is not drawn as
    ///     two lit <c>Border</c>s (those could not be clicked
    ///     at all). They are the primary control (every amp sim's is), so they are
    ///     buttons over the same rule SWITCH uses.
    /// </summary>
    /// <param name="slot">"A" or "B", from the segment's CommandParameter.</param>
    [RelayCommand]
    private void SelectCompareSlot(string? slot)
    {
        if (!Enum.TryParse<ToneCompareSlot>(slot, ignoreCase: true, out var wanted)) return;
        ApplyCompareSlot(_compare.SelectSlot(wanted, CompareLevel.Snapshot()));
    }

    /// <summary>
    ///     Pushes the rig a slot change handed back, then republishes the lamp
    ///     state. A null rig means the model decided the chain must not be
    ///     touched (already on that slot, or a fork that changed no sound) — the
    ///     lamps still move, because <see cref="ToneCompareSlots.Active" /> did.
    ///     <para>
    ///         <b>It applies to the SELECTED SEAT, not seat 0.</b> Every seat has
    ///         its own chain (ABI v28) and the rig's seat picker
    ///         repoints <c>_dsp</c>, so a plain <c>Apply</c> here compared player
    ///         1's tone while player 2's board was on screen — the wrong-guitar
    ///         shape — the same flaw as a tuner reading the global tap.
    ///     </para>
    ///     <para>
    ///         Applied as a USER-origin preset on purpose — the factory path
    ///         loudness-normalizes, and a comparison whose two sides are silently
    ///         re-levelled is not a comparison. A compare slot is a snapshot of
    ///         what was heard and must come back verbatim.
    ///     </para>
    /// </summary>
    private void ApplyCompareSlot(DcRigPreset? incoming)
    {
        if (incoming is not null)
        {
            incoming = CompareLevel.Prepare(incoming, out var trim);
            _presets.ApplyToPlayer(SelectedPlayer, new DspTonePreset(
                "compare", "A/B", DspToneCategory.Clean, string.Empty, incoming, DspToneOrigin.User));
            CompareLevel.SetLiveTrim(trim);
            Sync();
        }

        CompareSlotLabel = _compare.Active == ToneCompareSlot.A ? "A" : "B";
        RaiseCompareState();
    }

    /// <summary>Copies the current sound into both slots — "start comparing from
    /// here". Change something afterwards and SWITCH toggles between the change
    /// and this.</summary>
    [RelayCommand]
    private void SeedCompare()
    {
        _compare.SeedFrom(CompareLevel.Snapshot());
        CompareLevel.Forget();
        CompareSlotLabel = "A";
        RaiseCompareState();
    }

    /// <summary>Empties both compare slots — the board they were snapshots OF is
    /// not the board on screen (a seat repoint or a profile switch). Called
    /// from <c>ResyncAll</c>; without it, SWITCH applied the previous seat's whole
    /// rig onto the seat now selected.</summary>
    private void ResetCompareSlots()
    {
        _compare.Reset();
        CompareLevel.Forget();
        CompareSlotLabel = "A";
        RaiseCompareState();
    }

    /// <summary>Which of the two compare slots is live — the header draws A and
    /// B as a lit pair rather than one button that renames itself.</summary>
    public bool IsCompareSlotA => _compare.Active == ToneCompareSlot.A;

    public bool IsCompareSlotB => !IsCompareSlotA;

    /// <summary>Raised explicitly rather than left to
    /// <c>OnCompareSlotLabelChanged</c>: a seed that lands back on the
    /// slot already live changes no label, and the lamps must still be told the
    /// truth about a strip the player just pressed.</summary>
    private void RaiseCompareState()
    {
        OnPropertyChanged(nameof(IsCompareSlotA));
        OnPropertyChanged(nameof(IsCompareSlotB));
    }

    partial void OnCompareSlotLabelChanged(string value) => RaiseCompareState();

    private RigCompareLevelViewModel? _compareLevel;

    /// <summary>Level-matched switching, over the same slots.</summary>
    public RigCompareLevelViewModel CompareLevel => _compareLevel ??= new(() => _dsp, () => OutputMeters, _compare, Sync);
}
