namespace DotCharter.Tone.Audio.Dsp.RsToneConversion;

/// <summary>
///     The ONE way a live chain becomes a <see cref="DcRigPreset" />.
///     <para>
///         <b>Every field a node carries has to come with it, or the rig cannot
///         be put back.</b> A module's identity is not just its type id, its
///         knobs and its bypass flag: the ABI-v22 STRING params
///         (<c>namamp</c> <c>modelPath</c>, <c>cab</c>/<c>acousticsim</c>
///         <c>irPath</c>, <c>miku</c> <c>phrases</c>), the ABI-v23 BRANCH tag
///         and the ABI-v34 SIDECHAIN key are equally part of it, and <see cref="DcRigApplier" /> only re-pushes
///         what the preset actually CARRIES when it re-adds a node. A snapshot
///         that drops them therefore restores a MODELLESS namamp — a silent amp —
///         and flattens a split region back onto one lane, and both failures are
///         reported nowhere.
///     </para>
///     <para>
///         This loop existed four times: twice correctly
///         (<c>PedalboardViewModel.BuildCurrentRig</c>,
///         <c>ToneSessionService.SnapshotLiveChain</c>) and twice WITHOUT the
///         strings and the branch — the RS in-game-tone run's manual-chain
///         restore and the Tone Designer's preview restore, i.e. exactly the two
///         that hand the player's own board back to them. One implementation is
///         the point.
///     </para>
///     Pure: no I/O, no clocks, never mutates its input.
/// </summary>
public static class DcRigSnapshot
{
    /// <summary>The live chain as ordered rig modules, carrying params, bypass,
    /// string params and branch tags.</summary>
    public static List<DcRigModule> Modules(IReadOnlyList<DspNode>? nodes)
    {
        var modules = new List<DcRigModule>(nodes?.Count ?? 0);
        if (nodes is null) return modules;

        foreach (var node in nodes)
        {
            if (node is null) continue;
            // An EMPTY map reads as "none" so a round-trip never turns a null
            // into an empty dictionary (the persisted DTO does the same).
            IReadOnlyDictionary<string, string>? strings = node.StringValues.Count > 0
                ? new Dictionary<string, string>(node.StringValues, StringComparer.Ordinal)
                : null;
            modules.Add(new DcRigModule(
                node.ModuleTypeId,
                new Dictionary<string, float>(node.Values, StringComparer.Ordinal),
                node.Bypassed)
            {
                StringParams = strings,
                Branch = node.Branch,
                // Same rule as the branch tag and for the same reason:
                // a native key does not survive a remove/re-add, so a snapshot
                // that drops it restores a ducker that does not duck.
                Sidechain = node.Sidechain,
                ControlAssignments = node.ControlAssignments
            });
        }

        return modules;
    }

    /// <summary><see cref="Modules" /> wrapped as a preset — what every restore
    /// and save-as-a-tone path hands to <see cref="DcRigApplier" />.</summary>
    public static DcRigPreset FromNodes(IReadOnlyList<DspNode>? nodes) => new(Modules(nodes));

    /// <summary>
    ///     The live rig WITH its scenes: <see cref="FromNodes" /> plus the chain's
    ///     <see cref="IDspChainService.Scenes" />, the live values folded into the
    ///     scene that is playing (edits go into the active scene). Every path that
    ///     hands the player's own board back — undo, save, the persisted live rig,
    ///     a preview's restore — snapshots through this, or the scenes are lost.
    /// </summary>
    public static DcRigPreset FromChain(IDspChainService chain)
    {
        var modules = Modules(chain.GetNodes());
        return new DcRigPreset(modules) { Scenes = Scenes.RigScenes.Fold(chain.Scenes, modules) };
    }
}
