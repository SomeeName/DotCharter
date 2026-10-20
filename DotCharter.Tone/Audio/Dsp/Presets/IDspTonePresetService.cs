using DotCharter.Tone.Audio.Dsp.RsToneConversion;

namespace DotCharter.Tone.Audio.Dsp.Presets;

/// <summary>
/// Read/apply/manage surface over the tone-preset library: the immutable
/// built-in factory catalogue (<see cref="Presets"/>) plus the player's saved
/// profiles (<see cref="UserPresets"/>, persisted over
/// <c>RigConfig.UserTonePresets</c>). Applying a preset diffs its rig onto the
/// live <see cref="IDspChainService"/> via <see cref="DcRigApplier"/> (minimal
/// churn). Every member is throw-free — mirrors the RS-tone code's contract so a
/// missing native chain or a bad store read degrades to a no-op, never a crash.
/// </summary>
public interface IDspTonePresetService
{
    /// <summary>The immutable built-in factory presets.</summary>
    IReadOnlyList<DspTonePreset> Presets { get; }

    /// <summary>The player's saved user presets (persisted; may be empty).</summary>
    IReadOnlyList<DspTonePreset> UserPresets { get; }

    /// <summary>Factory presets followed by user presets — the full pickable set.</summary>
    IReadOnlyList<DspTonePreset> AllPresets { get; }

    // There is deliberately NO ByCategory here (deleted 0.60, zero callers). The
    // preset browser's category rail filters through
    // DotCharter.Tone.UI.Controls.Dsp.PresetBrowserFilter.Filter, which does STRICTLY
    // MORE than a category match: it also hides a tone whose algorithmic original
    // has a capture-backed `nam-` twin. A plain category filter here would list
    // both halves of 70 pairs — anything using it would be WRONG, not merely
    // redundant. Filter over AllPresets is the only category surface.

    /// <summary>Finds a preset by id across factory + user (factory wins a tie).
    /// Null when unknown.</summary>
    DspTonePreset? FindById(string id);

    /// <summary>Applies a preset's rig to the live chain (minimal-diff). Params a
    /// module omits are pinned to their catalogue defaults, so a sparse preset
    /// never inherits the values of the tone applied before. Returns the churn
    /// tally.</summary>
    DcRigApplyResult Apply(DspTonePreset preset);

    /// <summary>Finds the preset by id and applies it; null when no such preset.</summary>
    DcRigApplyResult? ApplyById(string id);

    /// <summary>
    ///     Applies a preset to ONE seat's chain (ABI v28) — this is what
    ///     gives each player their own sound.
    ///     <para>
    ///         Player 0 is identical to <see cref="Apply" />. Without a
    ///         per-player chain registry (a native-less build, a headless test)
    ///         a seat above 0 has NO chain, and this is a logged NO-OP — never a
    ///         fall-back onto the single chain. Applying there would be worse than
    ///         doing nothing: it is how player 2's tone silently overwrites player
    ///         1's board, and how player 1's next persist then saves player 2's
    ///         rig as if the player had built it.
    ///     </para>
    /// </summary>
    DcRigApplyResult ApplyToPlayer(int player, DspTonePreset preset);

    /// <summary>Finds the preset by id and applies it to one seat; null when no
    /// such preset.</summary>
    DcRigApplyResult? ApplyToPlayerById(int player, string id);

    // There is deliberately NO CaptureCurrent here (deleted 0.60, zero app
    // callers). "Save my pedalboard as a tone" is SaveUser(…, BuildCurrentRig()):
    // the caller snapshots the chain it is looking at
    // (PedalboardViewModel.BuildCurrentRig) and hands the rig over. A second
    // snapshot implementation inside this service was a standing drift hazard —
    // it had already had to be retro-fitted with the ABI-v22 StringParams (a
    // namamp's loaded capture, a cab's IR) and the ABI-v23 Branch tag that
    // DcRigApplier only re-pushes when the preset CARRIES them, and nothing would
    // have caught it lagging again, because nothing called it.

    /// <summary>Saves <paramref name="rig"/> as a user preset. With
    /// <paramref name="overwriteId"/> naming an existing USER preset, replaces it
    /// in place (same id); a built-in id is refused (the built-in is returned
    /// unchanged, nothing persisted). Otherwise a new user preset with a generated
    /// id is created. Returns the stored (or refused-existing) preset.</summary>
    DspTonePreset SaveUser(
        string name, DspToneCategory category, string description, DcRigPreset rig, string? overwriteId = null);

    /// <summary>Deletes a user preset by id. False for an unknown id or a built-in
    /// id (factory presets are immutable).</summary>
    bool DeleteUser(string id);
}
