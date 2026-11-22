using DotCharter.Tone.Audio.Dsp;
using DotCharter.Tone.Audio.Dsp.RsToneConversion;
using DotCharter.Tone.Audio.Dsp.Scenes;

namespace DotCharter.Tone.UI.ViewModels.PlayMode;

/// <summary>
///     Structural chain edits (add, move, remove, duplicate) that keep the stored rig scenes
///     in step: each edit re-keys every scene to the new chain shape, so no pedal inherits
///     another pedal's stored values.
/// </summary>
internal static class RigSceneChainEdits
{
    public static DspNode? Add(IDspChainService dsp, string moduleTypeId, bool initiallyBypassed = false)
    {
        var before = Slots(dsp);
        var node = dsp.AddNode(moduleTypeId, initiallyBypassed);
        if (node is not null) Reconcile(dsp, before);
        return node;
    }

    public static bool Move(IDspChainService dsp, int nodeId, int index)
    {
        var before = Slots(dsp);
        var moved = dsp.MoveNode(nodeId, index);
        if (moved) Reconcile(dsp, before);
        return moved;
    }

    public static bool Remove(IDspChainService dsp, int nodeId)
    {
        var before = Slots(dsp);
        var removed = dsp.RemoveNode(nodeId);
        if (removed) Reconcile(dsp, before);
        return removed;
    }

    /// <summary>Makes the duplicate behave like its source in every scene.</summary>
    public static void CopyScenes(IDspChainService dsp, int fromNodeId, int toNodeId)
    {
        if (dsp.Scenes is { } set)
            dsp.Scenes = RigScenes.CopyEntry(set, Slots(dsp)!, fromNodeId, toNodeId);
    }

    public static HashSet<int> BypassedNodes(IDspChainService dsp)
        => dsp.GetNodes().Where(n => n.Bypassed).Select(n => n.NodeId).ToHashSet();

    public static IReadOnlyList<RigControlAssignment> AssignmentsOf(IDspChainService dsp, int nodeId)
        => dsp.GetNodes().FirstOrDefault(n => n.NodeId == nodeId)?.ControlAssignments ?? Array.Empty<RigControlAssignment>();

    public static void Assign(IDspChainService dsp, int nodeId, IReadOnlyList<RigControlAssignment>? assignments)
    {
        if (assignments is { Count: > 0 }) dsp.SetControlAssignments(nodeId, assignments);
    }

    public static void CopyAssignments(IDspChainService dsp, int fromNodeId, int toNodeId)
        => Assign(dsp, toNodeId, AssignmentsOf(dsp, fromNodeId));

    private static List<RigSceneSlot>? Slots(IDspChainService dsp)
        => dsp.Scenes is null ? null : dsp.GetNodes().Select(n => new RigSceneSlot(n.NodeId, n.ModuleTypeId)).ToList();

    private static void Reconcile(IDspChainService dsp, List<RigSceneSlot>? before)
    {
        if (before is null || dsp.Scenes is not { } set) return;
        var nodes = dsp.GetNodes();
        var after = nodes.Select(n => new RigSceneSlot(n.NodeId, n.ModuleTypeId)).ToList();
        dsp.Scenes = RigScenes.Reconcile(set, before, after, DcRigSnapshot.Modules(nodes));
    }
}
