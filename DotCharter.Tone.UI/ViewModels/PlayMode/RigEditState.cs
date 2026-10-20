using DotCharter.Tone.Audio.Dsp.RsToneConversion;
using DotCharter.Tone.Audio.Dsp.Scenes;

namespace DotCharter.Tone.UI.ViewModels.PlayMode;

/// <summary>
///     Whether the live rig still IS the preset that was applied to it — the
///     "EDITED" badge beside the preset name on the Amp &amp; FX rig screen.
///
///     <para>
///         It is a COMPARISON, not a flag. A flag has to be raised by every
///         mutation path (add, remove, move, bypass, knob, model picker, IR
///         path, branch tag, master bypass, A/B compare) and the badge is wrong
///         the first time one of them is added without remembering it. Comparing
///         the snapshot the save path already builds
///         (<c>PedalboardViewModel.BuildCurrentRig</c>) against the snapshot the
///         preset carries cannot miss a mutation path, because every one of them
///         ends up in that snapshot by construction.
///     </para>
///
///     <para>
///         Pure and Avalonia-free so it is unit-testable without a chain.
///         <c>DcRigPreset</c> is a record whose only member is a LIST, and a
///         record's generated equality compares list REFERENCES — two snapshots
///         of an identical rig are never <c>==</c>. That is the whole reason
///         this exists rather than an <c>Equals</c> call at the call site.
///     </para>
/// </summary>
internal static class RigEditState
{
    /// <summary>
    ///     True when <paramref name="current" /> differs from
    ///     <paramref name="applied" /> in any way a preset can carry.
    ///     <para>
    ///         With no applied preset the rig is edited exactly when it is not
    ///         empty: an empty board is the state a fresh engine starts in, so
    ///         calling it "edited" would badge every cold start.
    ///     </para>
    /// </summary>
    public static bool IsEdited(DcRigPreset? current, DcRigPreset? applied)
    {
        var live = current?.Modules ?? [];
        if (applied is null) return live.Count > 0;

        var liveScenes = current?.Scenes;
        if (liveScenes is null && applied.Scenes is null) return ModulesDiffer(live, applied.Modules);

        // SCENES. Switching scene is not an edit: the scenes must be
        // the ones the tone carries (names, used slots, values — the one playing
        // excluded), and the live modules must be the tone ON the scene that is
        // playing. A knob moved in any scene still shows, because edits go into
        // the active scene and that scene is then not the tone's.
        if (!RigScenes.SameScenes(liveScenes, applied.Scenes, ignoreActive: true)) return true;
        var expected = applied.Modules;
        var playing = RigScenes.Normalize(liveScenes!);
        var saved = RigScenes.Normalize(applied.Scenes!);
        if (playing.Active != saved.Active && saved.Slots[playing.Active].IsStored)
            expected = RigScenes.Project(applied.Modules, saved.Slots[playing.Active]);
        return ModulesDiffer(live, expected);
    }

    /// <summary>
    ///     True when two rig states are the SAME in every way undo can put back —
    ///     modules as <see cref="IsEdited" /> compares them, and the scenes
    ///     exactly, including which one is playing (a scene switch is an undo
    ///     step even though it is no edit).
    /// </summary>
    public static bool IsSameState(DcRigPreset a, DcRigPreset b)
        => !ModulesDiffer(a.Modules, b.Modules) && RigScenes.SameScenes(a.Scenes, b.Scenes);

    private static bool ModulesDiffer(IReadOnlyList<DcRigModule> live, IReadOnlyList<DcRigModule> saved)
    {
        if (live.Count != saved.Count) return true;

        for (var i = 0; i < live.Count; i++)
        {
            if (!SameModule(live[i], saved[i])) return true;
        }

        return false;
    }

    private static bool SameModule(DcRigModule a, DcRigModule b)
        => string.Equals(a.ModuleTypeId, b.ModuleTypeId, StringComparison.Ordinal)
           && a.Bypassed == b.Bypassed
           && a.Branch == b.Branch
           // ABI v34. Without this, keying a ducker off the song mix is a change
           // the badge cannot see, and "EDITED" is exactly the signal that stops
           // a player overwriting a tone they meant to keep.
           && a.Sidechain == b.Sidechain
           && a.ControlAssignments.SequenceEqual(b.ControlAssignments)
           && SameFloats(a.Params, b.Params)
           && SameStrings(a.StringParams, b.StringParams);

    private static bool SameFloats(
        IReadOnlyDictionary<string, float>? a, IReadOnlyDictionary<string, float>? b)
    {
        var left = a ?? EmptyFloats;
        var right = b ?? EmptyFloats;
        if (left.Count != right.Count) return false;

        foreach (var (key, value) in left)
        {
            if (!right.TryGetValue(key, out var other)) return false;
            // A knob is a float that came back off the native engine, so an
            // exact bit compare would badge a rig EDITED for a value that
            // round-tripped. One part in ten thousand of a unit range is far
            // under anything the readouts print.
            if (Math.Abs(value - other) > 1e-4f) return false;
        }

        return true;
    }

    private static bool SameStrings(
        IReadOnlyDictionary<string, string>? a, IReadOnlyDictionary<string, string>? b)
    {
        var left = a ?? EmptyStrings;
        var right = b ?? EmptyStrings;
        if (left.Count != right.Count) return false;

        foreach (var (key, value) in left)
        {
            if (!right.TryGetValue(key, out var other)) return false;
            if (!string.Equals(value, other, StringComparison.Ordinal)) return false;
        }

        return true;
    }

    private static readonly IReadOnlyDictionary<string, float> EmptyFloats =
        new Dictionary<string, float>();

    private static readonly IReadOnlyDictionary<string, string> EmptyStrings =
        new Dictionary<string, string>();
}
