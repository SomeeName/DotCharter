using DotCharter.Tone.Audio.Dsp.RsToneConversion;

namespace DotCharter.Tone.Audio.Dsp.Scenes;

/// <summary>
///     One module's knobs and switch inside a <see cref="RigScene" />: the values
///     a scene writes back onto the live node it matches. A module is matched by
///     its catalogue type and its OCCURRENCE among modules of that type (the k-th
///     overdrive of the scene onto the k-th overdrive of the chain) — the same rule
///     <see cref="DcRigApplier" /> reuses nodes by, so a unit added or removed
///     elsewhere in the chain never shifts a scene's values onto the wrong pedal.
/// </summary>
public sealed record RigSceneModule(string ModuleTypeId, IReadOnlyDictionary<string, float> Params, bool Bypassed);

/// <summary>
///     One scene (a Helix "snapshot", a Quad Cortex scene): a name the player
///     gave it and, once it has been used, every module's params and bypass.
///     <see cref="Modules" /> null = the slot has never been used; selecting it
///     starts it as a copy of what is playing.
/// </summary>
public sealed record RigScene(string? Name, IReadOnlyList<RigSceneModule>? Modules)
{
    public static readonly RigScene Empty = new(null, null);

    /// <summary>True once the slot holds values.</summary>
    public bool IsStored => Modules is not null;
}

/// <summary>
///     A rig's eight scenes (A–H) and which one is playing. Carried by
///     <see cref="DcRigPreset.Scenes" /> (so it saves with a tone, travels through
///     undo and the tone file) and held on the chain by
///     <see cref="IDspChainService.Scenes" /> (so the live rig keeps it).
///     <para>
///         <b>The ACTIVE slot is the live rig.</b> Edits go into the scene that is
///         playing (Helix's "snapshot edits: recall"): every snapshot of the chain
///         (<see cref="DcRigSnapshot.FromChain" />) folds the live values into
///         <see cref="Active" />, so there is no separate "store" to forget before
///         switching away, and nothing is lost by a switch.
///     </para>
/// </summary>
public sealed record RigSceneSet(IReadOnlyList<RigScene> Slots, int Active)
{
    /// <summary>A–H.</summary>
    public const int Count = 8;

    /// <summary>The longest name a scene keeps (the scene button prints about this much).</summary>
    public const int MaxNameLength = 24;
}

/// <summary>
///     The scene rules, pure: capture, fold, switch, rename, store, clear, and the
///     comparisons the undo history and the EDITED badge need. No I/O, no clocks,
///     never mutates its inputs.
/// </summary>
public static class RigScenes
{
    private const float Epsilon = 1e-4f;

    /// <summary>The scene letter for a slot index ("A".."H").</summary>
    public static string Letter(int index) => ((char)('A' + index)).ToString();

    /// <summary>The chain's params and bypass, as scene modules (string params,
    /// branch tags and the routing are the PRESET's, never a scene's — a scene
    /// switch never changes the rig's structure).</summary>
    public static IReadOnlyList<RigSceneModule> Capture(IReadOnlyList<DcRigModule> modules)
    {
        var captured = new List<RigSceneModule>(modules.Count);
        foreach (var module in modules)
        {
            captured.Add(new RigSceneModule(
                module.ModuleTypeId,
                new Dictionary<string, float>(module.Params, StringComparer.Ordinal),
                module.Bypassed));
        }

        return captured;
    }

    /// <summary>A fresh set over <paramref name="live" />: scene A holds what is
    /// playing and is active, B–H are unused.</summary>
    public static RigSceneSet Start(IReadOnlyList<DcRigModule> live)
    {
        var slots = Enumerable.Repeat(RigScene.Empty, RigSceneSet.Count).ToArray();
        slots[0] = new RigScene(null, Capture(live));
        return new RigSceneSet(slots, 0);
    }

    /// <summary>
    ///     Writes the live values into the ACTIVE scene (edits go into the scene
    ///     that is playing). A set that says nothing beyond the live rig — only the
    ///     active slot used and no slot named — folds to null, so a rig whose
    ///     scenes were all cleared is again exactly a rig without scenes (no
    ///     EDITED badge, no scenes in the saved JSON).
    /// </summary>
    public static RigSceneSet? Fold(RigSceneSet? set, IReadOnlyList<DcRigModule> live)
    {
        if (set is null) return null;
        set = Normalize(set);
        var slots = set.Slots.ToArray();
        slots[set.Active] = slots[set.Active] with { Modules = Capture(live) };
        var folded = new RigSceneSet(slots, set.Active);
        return IsTrivial(folded) ? null : folded;
    }

    /// <summary>True when the set holds nothing but the live rig.</summary>
    public static bool IsTrivial(RigSceneSet set)
    {
        for (var i = 0; i < set.Slots.Count; i++)
        {
            var slot = set.Slots[i];
            if (!string.IsNullOrEmpty(slot.Name)) return false;
            if (i != set.Active && slot.IsStored) return false;
        }

        return true;
    }

    /// <summary>Exactly <see cref="RigSceneSet.Count" /> slots and an active index
    /// in range, whatever a hand-edited file or an older build wrote.</summary>
    public static RigSceneSet Normalize(RigSceneSet set)
    {
        if (set.Slots.Count == RigSceneSet.Count && set.Active is >= 0 and < RigSceneSet.Count
            && set.Slots.All(s => s is not null))
        {
            return set;
        }

        var slots = new RigScene[RigSceneSet.Count];
        for (var i = 0; i < slots.Length; i++)
            slots[i] = i < set.Slots.Count && set.Slots[i] is { } s ? s : RigScene.Empty;
        return new RigSceneSet(slots, Math.Clamp(set.Active, 0, RigSceneSet.Count - 1));
    }

    /// <summary>A name as a scene keeps it: trimmed, capped, null when blank.</summary>
    public static string? CleanName(string? name)
    {
        var trimmed = name?.Trim();
        if (string.IsNullOrEmpty(trimmed)) return null;
        return trimmed.Length > RigSceneSet.MaxNameLength ? trimmed[..RigSceneSet.MaxNameLength] : trimmed;
    }

    /// <summary>
    ///     <paramref name="modules" /> with <paramref name="scene" />'s values
    ///     written over them — module by module, matched by type and occurrence.
    ///     A module the scene does not know (added after the scene was stored)
    ///     keeps its values; a param the scene does not carry keeps its value.
    ///     The structure (order, types, string params, branch, sidechain,
    ///     controls) is never touched.
    /// </summary>
    public static IReadOnlyList<DcRigModule> Project(IReadOnlyList<DcRigModule> modules, RigScene scene)
    {
        if (scene.Modules is not { } stored) return modules;

        var byType = new Dictionary<string, Queue<RigSceneModule>>(StringComparer.Ordinal);
        foreach (var module in stored)
        {
            if (module is null) continue;
            if (!byType.TryGetValue(module.ModuleTypeId, out var queue))
                byType[module.ModuleTypeId] = queue = new Queue<RigSceneModule>();
            queue.Enqueue(module);
        }

        var projected = new List<DcRigModule>(modules.Count);
        foreach (var module in modules)
        {
            if (!byType.TryGetValue(module.ModuleTypeId, out var queue) || queue.Count == 0)
            {
                projected.Add(module);
                continue;
            }

            var values = queue.Dequeue();
            var merged = new Dictionary<string, float>(module.Params, StringComparer.Ordinal);
            foreach (var (id, value) in values.Params)
            {
                if (float.IsFinite(value))
                    merged[id] = value;
            }

            projected.Add(module with { Params = merged, Bypassed = values.Bypassed });
        }

        return projected;
    }

    /// <summary>
    ///     The rig after switching to scene <paramref name="target" />, or null
    ///     when the switch changes nothing (already there, or scene A of a rig
    ///     that has no scenes yet). <paramref name="live" /> is a snapshot whose
    ///     scenes are already folded (<see cref="DcRigSnapshot.FromChain" />).
    ///     An UNUSED target is forked from the live rig — the press always lands
    ///     somewhere and nothing audible changes; a stored one is projected over
    ///     the live modules, so the switch writes params and bypass only.
    /// </summary>
    public static DcRigPreset? Switch(DcRigPreset live, int target)
    {
        if (target is < 0 or >= RigSceneSet.Count) return null;
        if (live.Scenes is null && target == 0) return null;

        var set = live.Scenes is { } existing ? Normalize(existing) : Start(live.Modules);
        if (live.Scenes is not null && set.Active == target) return null;

        var slots = set.Slots.ToArray();
        var destination = slots[target];
        if (!destination.IsStored)
        {
            slots[target] = destination with { Modules = Capture(live.Modules) };
            return live with { Scenes = new RigSceneSet(slots, target) };
        }

        return live with { Modules = Project(live.Modules, destination), Scenes = new RigSceneSet(slots, target) };
    }

    /// <summary>
    ///     A rig as it should start on scene <paramref name="scene" /> (a setlist
    ///     entry's starting scene): the scene's values over the modules and the
    ///     scene active. Unchanged when the rig has no scenes or that one is unused.
    /// </summary>
    public static DcRigPreset StartOn(DcRigPreset rig, int scene)
    {
        if (rig.Scenes is not { } set || scene is < 0 or >= RigSceneSet.Count) return rig;
        set = Normalize(set);
        if (!set.Slots[scene].IsStored || set.Active == scene) return rig;
        return rig with { Modules = Project(rig.Modules, set.Slots[scene]), Scenes = set with { Active = scene } };
    }

    /// <summary>The set with slot <paramref name="index" /> renamed.</summary>
    public static RigSceneSet Rename(RigSceneSet set, int index, string? name)
    {
        set = Normalize(set);
        if (index is < 0 or >= RigSceneSet.Count) return set;
        var slots = set.Slots.ToArray();
        slots[index] = slots[index] with { Name = CleanName(name) };
        return new RigSceneSet(slots, set.Active);
    }

    /// <summary>The set with the live rig stored into slot <paramref name="index" />
    /// (its name kept) — "store what I hear into scene C".</summary>
    public static RigSceneSet Store(RigSceneSet set, int index, IReadOnlyList<DcRigModule> live)
    {
        set = Normalize(set);
        if (index is < 0 or >= RigSceneSet.Count) return set;
        var slots = set.Slots.ToArray();
        slots[index] = slots[index] with { Modules = Capture(live) };
        return new RigSceneSet(slots, set.Active);
    }

    /// <summary>The set with slot <paramref name="index" /> emptied (values and
    /// name). The ACTIVE slot is never cleared: it is the rig that is playing.</summary>
    public static RigSceneSet Clear(RigSceneSet set, int index)
    {
        set = Normalize(set);
        if (index is < 0 or >= RigSceneSet.Count || index == set.Active) return set;
        var slots = set.Slots.ToArray();
        slots[index] = RigScene.Empty;
        return new RigSceneSet(slots, set.Active);
    }

    /// <summary>
    ///     Whether two sets hold the same scenes: names, used slots and every
    ///     value (floats within a knob's round-trip). With
    ///     <paramref name="ignoreActive" /> the playing slot is not compared — the
    ///     EDITED badge's question, where switching scenes is not an edit.
    /// </summary>
    public static bool SameScenes(RigSceneSet? a, RigSceneSet? b, bool ignoreActive = false)
    {
        if (a is null || b is null) return a is null && b is null;
        a = Normalize(a);
        b = Normalize(b);
        if (!ignoreActive && a.Active != b.Active) return false;
        for (var i = 0; i < RigSceneSet.Count; i++)
        {
            if (!SameScene(a.Slots[i], b.Slots[i]))
                return false;
        }

        return true;
    }

    private static bool SameScene(RigScene a, RigScene b)
    {
        if (!string.Equals(a.Name ?? string.Empty, b.Name ?? string.Empty, StringComparison.Ordinal)) return false;
        if (a.Modules is null || b.Modules is null) return a.Modules is null && b.Modules is null;
        if (a.Modules.Count != b.Modules.Count) return false;
        for (var i = 0; i < a.Modules.Count; i++)
        {
            var x = a.Modules[i];
            var y = b.Modules[i];
            if (!string.Equals(x.ModuleTypeId, y.ModuleTypeId, StringComparison.Ordinal) || x.Bypassed != y.Bypassed)
                return false;
            if (x.Params.Count != y.Params.Count) return false;
            foreach (var (id, value) in x.Params)
            {
                if (!y.Params.TryGetValue(id, out var other) || Math.Abs(value - other) > Epsilon)
                    return false;
            }
        }

        return true;
    }
}
