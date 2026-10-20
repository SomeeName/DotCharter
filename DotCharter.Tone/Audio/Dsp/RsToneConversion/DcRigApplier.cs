namespace DotCharter.Tone.Audio.Dsp.RsToneConversion;

/// <summary>
/// Applies a target <see cref="DcRigPreset"/> to a live <see cref="IDspChainService"/>
/// with the LEAST churn: same-type nodes are reused in place (only changed params
/// re-sent), and only the surplus / missing / mis-ordered nodes are added,
/// removed or moved. No full teardown mid-song — a clean → high-gain switch that
/// shares a cab + reverb + trailing gain keeps those stages untouched.
///
/// <para><b>ABI-atomicity caveat (why this is minimal-diff, not a batch swap).</b>
/// The DSP ABI (<c>dca_engine_dsp_*</c>) exposes only per-node ops
/// (add / remove / move / param / bypass); each individually publishes an atomic
/// snapshot to the audio thread, but a <i>sequence</i> of them is NOT atomic as a
/// whole — the engine can render a block against an intermediate chain between two
/// of the calls below. Minimal-diff MITIGATES this (reused stages never churn, and
/// a freshly-added node is fully parameterized while it still sits at the chain
/// tail — before it is moved in-signal-path — so it never runs at catalogue-default
/// gain/master, avoiding the loud "new amp at defaults" transient) but cannot fully
/// eliminate the brief window. In practice tone changes land on phrase edges / rests
/// where the residual transient is inaudible, so this ships as-is. The escape hatch
/// (deferred behind measured evidence, NOT done here) is a native
/// <c>dca_engine_dsp_load_preset</c> batch op that builds + prepares the whole chain
/// off the audio thread and does ONE atomic chain-pointer swap; adding it is a
/// pure swap-in behind this class and needs no coordinator change. This class stays
/// MANAGED — no ABI change.</para>
///
/// Pure orchestration: never throws, degrades on any native <c>false</c> / null
/// (logs + continues, so a single bad module never aborts a tone switch). Every
/// failed native op is counted in <see cref="DcRigApplyResult.FailedOps"/> and
/// surfaced in the log (per-op for add/remove/move; one aggregated line naming
/// the rejected param ids for param/bypass).
///
/// <para><b>Serialization is ENFORCED HERE, not asked of callers.</b> A
/// rule "callers must serialize <see cref="Apply"/> (the coordinator
/// single-flights it)" would be a false safety claim: five call
/// sites reach seat 0's applier (<c>ToneSessionService.RestoreOnce</c> on the
/// engine-attach thread, the preset browser on the UI thread,
/// the since-removed couch co-op applier, <c>ProfileToneRigCoordinator</c> and
/// <c>RsTonePlaybackCoordinator</c>) and only the last single-flighted it, under
/// a PRIVATE gate no other caller could take. That matters because a PLAN is not
/// an op: the whole diff below is computed from ONE <c>GetNodes()</c> snapshot
/// while <c>JuceDspChainService._gate</c> only serializes the individual ops, so
/// an interleave removes nodes the other planned to reuse and shifts every
/// <c>MoveNode</c> index — leaving a blend of two tones with the damage counted
/// as <see cref="DcRigApplyResult.FailedOps"/> and misread as native rejection.
/// One lock per applier (i.e. per CHAIN, i.e. per seat) makes each plan
/// atomic, so the chain always ends up as exactly one of the presets — the later
/// caller's, since it re-diffs against what the first one left. It is a
/// <c>lock</c>, so a re-entrant apply from a <c>NodesChanged</c> handler on the
/// same thread still works, and the ordering is one-way (this gate never waits
/// on <c>_gate</c>'s holder) so it cannot deadlock against the chain.</para>
///
/// <para>Still run it OFF the audio thread and OFF the position-timer thread —
/// and off the UI thread: every <c>RemoveNode</c>/<c>MoveNode</c>
/// blocks in the native bypass crossfade. <c>ToneApplyQueue</c> is the seam for
/// that.</para>
/// </summary>
public sealed class DcRigApplier
{
    private const float ParamEpsilon = 1e-4f;

    private readonly IDspChainService _chain;
    private readonly IAppLogger? _logger;

    /// <summary>Single-flights <see cref="Apply"/> for this applier — see the
    /// class doc. Per-instance, and an applier is per-chain, so seat 1's tone
    /// never waits behind seat 0's.</summary>
    private readonly object _planGate = new();

    public DcRigApplier(IDspChainService chain, IAppLogger? logger = null)
    {
        _chain = chain;
        _logger = logger;
    }

    /// <summary>Diffs <paramref name="preset"/> against the live chain and applies
    /// the least-churn op set so the chain becomes the preset. Returns a tally;
    /// <see cref="DcRigApplyResult.NoOp"/> is true (and zero native calls happened)
    /// when the chain already matched.</summary>
    public DcRigApplyResult Apply(DcRigPreset preset)
    {
        // The ENTIRE plan is atomic, not each op — see the class doc.
        lock (_planGate) return ApplyLocked(preset);
    }

    private DcRigApplyResult ApplyLocked(DcRigPreset preset)
    {
        var current = _chain.GetNodes();
        var target = preset.Modules;

        // ---- 1. Match target modules to current nodes by TYPE, in order -------
        // The k-th target module of type T pairs with the k-th current node of
        // type T (one queue per type). Reused nodes survive untouched (minimal
        // churn); a same-multiset reorder reuses EVERYTHING and only moves.
        var byType = new Dictionary<string, Queue<DspNode>>(StringComparer.Ordinal);
        foreach (var node in current)
        {
            if (!byType.TryGetValue(node.ModuleTypeId, out var q))
                byType[node.ModuleTypeId] = q = new Queue<DspNode>();
            q.Enqueue(node);
        }

        // reused[i] = the node reused for target module i, or null when it needs a
        // fresh node.
        var reused = new DspNode?[target.Count];
        for (var i = 0; i < target.Count; i++)
        {
            if (byType.TryGetValue(target[i].ModuleTypeId, out var q) && q.Count > 0)
                reused[i] = q.Dequeue();
        }

        var added = 0;
        var removed = 0;
        var moved = 0;
        var paramsSet = 0;
        var bypassSet = 0;
        var failedOps = 0;
        // "moduleType.paramId" per silently-degradable param/bypass failure, for
        // the one aggregated warning at the end (add/remove/move failures get
        // their own per-op warning below).
        List<string>? failedParams = null;

        // `live` mirrors the chain's node-id order so the reorder can be planned
        // without re-querying GetNodes() (which allocates). Only mutated when the
        // matching native op reports success, so it never drifts from the real
        // chain even under partial native failure.
        var live = new List<int>(current.Count);
        foreach (var node in current) live.Add(node.NodeId);

        // ---- 2. Remove current nodes with no target match --------------------
        foreach (var q in byType.Values)
        {
            while (q.Count > 0)
            {
                var node = q.Dequeue();
                if (_chain.RemoveNode(node.NodeId))
                {
                    live.Remove(node.NodeId);
                    removed++;
                }
                else
                {
                    failedOps++;
                    _logger?.Warning(AppLogCategory.Audio,
                        $"DcRigApplier: RemoveNode({node.NodeId}, '{node.ModuleTypeId}') failed; leaving it in the chain.");
                }
            }
        }

        // ---- 3. Add missing target modules; parameterize them AT THE TAIL -----
        // desiredIds[i] = the node id that should sit at chain position i.
        var desiredIds = new List<int>(target.Count);
        var activateAfterConfiguration = new List<int>();
        for (var i = 0; i < target.Count; i++)
        {
            var mod = target[i];
            if (reused[i] is { } existing)
            {
                desiredIds.Add(existing.NodeId);
                continue;
            }

            var node = _chain.AddNode(mod.ModuleTypeId, initiallyBypassed: true);
            if (node is null)
            {
                // Fidelity contract: a bad / unknown module never aborts the switch.
                failedOps++;
                _logger?.Warning(AppLogCategory.Audio,
                    $"DcRigApplier: AddNode('{mod.ModuleTypeId}') failed; module skipped for this tone.");
                continue;
            }

            added++;
            live.Add(node.NodeId); // AddNode appends at the tail
            desiredIds.Add(node.NodeId);

            // Published at zero wet, not merely appended: an appended live node
            // would already process audio with catalogue-default gain/master.
            var failuresBeforeConfiguration = failedOps;
            foreach (var (paramId, value) in mod.Params)
            {
                if (_chain.SetParam(node.NodeId, paramId, value))
                {
                    paramsSet++;
                }
                else
                {
                    failedOps++;
                    (failedParams ??= []).Add($"{mod.ModuleTypeId}.{paramId}");
                }
            }

            // String params (ABI v22 — namamp modelPath, cab/acousticsim irPath,
            // miku phrases): push after the float params, while the node is still
            // at the tail. Counted into paramsSet so a capture-backed preset is
            // never a spurious NoOp.
            PushStringParams(node.NodeId, mod, ref paramsSet, ref failedOps, ref failedParams);
            PushAssignments(node, mod, ref paramsSet, ref failedOps);

            // Branch tag (ABI v23): a native tag does NOT survive a re-add, so a
            // fresh node always starts at branch 0 — push the preset's tag now
            // (only when non-zero; a fresh node is already 0). Counted like a param.
            if (mod.Branch != 0)
                PushBranch(node.NodeId, mod, ref paramsSet, ref failedOps, ref failedParams);

            // Sidechain key (ABI v34): same rule as the branch tag - a native key
            // does NOT survive a re-add, and a fresh node is already None, so push
            // only when the preset carries one.
            if (mod.Sidechain != JuceSharp.Dsp.JuceDspSidechainSource.None)
                PushSidechain(node.NodeId, mod, ref paramsSet, ref failedOps, ref failedParams);

            if (!mod.Bypassed && failedOps == failuresBeforeConfiguration)
                activateAfterConfiguration.Add(node.NodeId);
        }

        // ---- 4. Reorder live order → desired order (selection-style, ≤n moves) -
        for (var i = 0; i < desiredIds.Count && i < live.Count; i++)
        {
            if (live[i] == desiredIds[i]) continue;
            var id = desiredIds[i];
            if (_chain.MoveNode(id, i))
            {
                live.Remove(id);
                live.Insert(i, id);
                moved++;
            }
            else
            {
                failedOps++;
                _logger?.Warning(AppLogCategory.Audio, $"DcRigApplier: MoveNode({id}, {i}) failed.");
            }
        }

        // ---- 5. Params / bypass on reused nodes (added nodes done in step 3) ---
        for (var i = 0; i < target.Count; i++)
        {
            if (reused[i] is not { } node) continue;
            var mod = target[i];
            var failuresBeforeConfiguration = failedOps;
            foreach (var (paramId, value) in mod.Params)
            {
                if (node.Values.TryGetValue(paramId, out var cur) && MathF.Abs(cur - value) <= ParamEpsilon)
                    continue; // unchanged — skip the native call
                if (_chain.SetParam(node.NodeId, paramId, value))
                {
                    paramsSet++;
                }
                else
                {
                    failedOps++;
                    (failedParams ??= []).Add($"{mod.ModuleTypeId}.{paramId}");
                }
            }

            // Reused node: re-push only CHANGED string params (diffed against the
            // node's mirrored string cache) so an identical preset stays a NoOp
            // but a modelPath/irPath that differs is corrected in place.
            PushStringParams(node.NodeId, mod, ref paramsSet, ref failedOps, ref failedParams, node.StringValues);
            PushAssignments(node, mod, ref paramsSet, ref failedOps);

            // Branch tag (ABI v23): a reused node keeps its native tag, so diff
            // against the mirror — push only when the preset's branch differs.
            // Keeps an identical preset a NoOp while correcting a moved-branch pedal.
            if (node.Branch != mod.Branch)
                PushBranch(node.NodeId, mod, ref paramsSet, ref failedOps, ref failedParams);

            // Sidechain key (ABI v34): a reused node keeps its native key, so diff
            // against the mirror - which is also what turns a key back OFF when the
            // incoming preset has none, rather than inheriting the last tone's.
            if (node.Sidechain != mod.Sidechain)
                PushSidechain(node.NodeId, mod, ref paramsSet, ref failedOps, ref failedParams);

            if (node.Bypassed != mod.Bypassed &&
                (mod.Bypassed || failedOps == failuresBeforeConfiguration))
            {
                if (_chain.SetBypass(node.NodeId, mod.Bypassed))
                {
                    bypassSet++;
                }
                else
                {
                    failedOps++;
                    (failedParams ??= []).Add($"{mod.ModuleTypeId}.bypass");
                }
            }
        }

        // Make configured regular nodes audible only after the complete plan.
        // Rejected configurations stay bypassed instead of exposing defaults.
        foreach (var nodeId in activateAfterConfiguration)
        {
            if (_chain.SetBypass(nodeId, false))
            {
                bypassSet++;
            }
            else
            {
                failedOps++;
                (failedParams ??= []).Add($"node {nodeId}.bypass");
            }
        }

        // One aggregated line per Apply (not per param) so a drifted catalogue /
        // rejecting native never floods the log on seek-driven re-applies.
        if (failedOps > 0)
        {
            _logger?.Warning(AppLogCategory.Audio,
                $"DcRigApplier: {failedOps} native op(s) failed while applying the preset" +
                (failedParams is null ? "." : $"; rejected params: {string.Join(", ", failedParams)}."));
        }

        var noOp = added == 0 && removed == 0 && moved == 0 && paramsSet == 0 && bypassSet == 0;
        // The rig's scenes ride with it, and a rig WITHOUT scenes clears them:
        // scenes stored for one tone's modules must never be projected onto
        // another's. Inside the plan gate, so a second apply cannot interleave.
        _chain.Scenes = preset.Scenes;
        return new DcRigApplyResult(added, removed, moved, paramsSet, bypassSet, noOp, failedOps);
    }

    // Pushes a module's string params (ABI v22) through IDspChainService.SetNodeString.
    // <paramref name="current"/> null ⇒ always push (a freshly-added node); otherwise
    // a value equal to the node's cached one is skipped so an identical apply is a
    // NoOp. Degrades on native false exactly like the float-param path (counted +
    // named for the aggregated warning). Clears omitted values on a reused node.
    private void PushStringParams(
        int nodeId, DcRigModule mod, ref int paramsSet, ref int failedOps,
        ref List<string>? failedParams, IReadOnlyDictionary<string, string>? current = null)
    {
        // A preset owns its full string state. Omission means the default,
        // not a custom IR or capture inherited from the previous tone.
        if (current is not null)
        {
            foreach (var (paramId, previous) in current)
            {
                if (string.IsNullOrEmpty(previous) || mod.StringParams?.ContainsKey(paramId) == true)
                    continue;
                if (_chain.SetNodeString(nodeId, paramId, string.Empty))
                {
                    paramsSet++;
                }
                else
                {
                    failedOps++;
                    (failedParams ??= []).Add($"{mod.ModuleTypeId}.{paramId}");
                }
            }
        }

        if (mod.StringParams is not { Count: > 0 } strings) return;
        foreach (var (paramId, value) in strings)
        {
            var v = value ?? string.Empty;
            if (current is not null && current.TryGetValue(paramId, out var cur) &&
                string.Equals(cur, v, StringComparison.Ordinal))
            {
                continue; // unchanged — skip the native call
            }

            if (_chain.SetNodeString(nodeId, paramId, v))
            {
                paramsSet++;
            }
            else
            {
                failedOps++;
                (failedParams ??= []).Add($"{mod.ModuleTypeId}.{paramId}");
            }
        }
    }

    // Pushes a module's split/parallel branch tag (ABI v23) through
    // IDspChainService.SetNodeBranch. Degrades on native false exactly like the
    // float/string-param paths (counted + named for the aggregated warning), and
    // is counted into paramsSet so a branch-only add is never a spurious NoOp.
    private void PushBranch(
        int nodeId, DcRigModule mod, ref int paramsSet, ref int failedOps, ref List<string>? failedParams)
    {
        if (_chain.SetNodeBranch(nodeId, mod.Branch))
        {
            paramsSet++;
        }
        else
        {
            failedOps++;
            (failedParams ??= []).Add($"{mod.ModuleTypeId}.branch");
        }
    }
    // Pushes a module's ABI-v34 detector key through
    // IDspChainService.SetNodeSidechain. Degrades exactly like PushBranch - and
    // note what that means on the ADD path: the engine REJECTS a key on a module
    // type that does not advertise the capability, so a hand-edited tone that
    // keys a `rat` leaves that rat BYPASSED with `rat.sidechain` named in the
    // aggregated warning. That is the existing "a rejected configuration stays
    // bypassed instead of exposing defaults" rule, and it is the right one here:
    // a ducker that silently stopped ducking is the failure this field exists to
    // prevent.
    private void PushSidechain(
        int nodeId, DcRigModule mod, ref int paramsSet, ref int failedOps, ref List<string>? failedParams)
    {
        if (_chain.SetNodeSidechain(nodeId, mod.Sidechain))
        {
            paramsSet++;
        }
        else
        {
            failedOps++;
            (failedParams ??= []).Add($"{mod.ModuleTypeId}.sidechain");
        }
    }

    private void PushAssignments(DspNode node, DcRigModule module, ref int changed, ref int failed)
    {
        if (node.ControlAssignments.SequenceEqual(module.ControlAssignments)) return;
        if (_chain.SetControlAssignments(node.NodeId, module.ControlAssignments)) changed++;
        else failed++;
    }

}
