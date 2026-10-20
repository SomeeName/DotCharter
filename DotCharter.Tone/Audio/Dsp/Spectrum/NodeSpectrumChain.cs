using DotCharter.Tone.Audio.Dsp.RsToneConversion;

namespace DotCharter.Tone.Audio.Dsp.Spectrum;

/// <summary>Which side of a unit the spectrum listens at.</summary>
public enum NodeSpectrumTap
{
    /// <summary>The signal arriving at the unit: every module BEFORE it.</summary>
    Pre,

    /// <summary>The signal leaving the unit: every module up to and including it.</summary>
    Post
}

/// <summary>
///     The pre/post selection, pure: which modules of a rig a node's spectrum is
///     rendered through. The engine reports no per-node spectrum (only peak / RMS
///     meters), so a node is heard by cutting the chain short and rendering the
///     cut offline (no new native DSP).
///     <para>
///         <b>Not inside a split.</b> A unit between a <c>splitter</c> and its
///         <c>merger</c> (and the two fans themselves) has no single "everything
///         before it": cutting there leaves a splitter without its merger. Those
///         answer null and the view says why, rather than rendering a chain that
///         is not the one playing.
///     </para>
/// </summary>
public static class NodeSpectrumChain
{
    /// <summary>The modules to render for <paramref name="tap" /> at
    /// <paramref name="nodeIndex" />, or null when the unit cannot be cut at
    /// (out of range, inside a split region, or a fan itself). An empty list is
    /// valid: the first unit's PRE is the dry input.</summary>
    public static IReadOnlyList<DcRigModule>? Select(IReadOnlyList<DcRigModule> modules, int nodeIndex, NodeSpectrumTap tap)
    {
        if (nodeIndex < 0 || nodeIndex >= modules.Count || IsInSplitRegion(modules, nodeIndex)) return null;
        var count = tap == NodeSpectrumTap.Pre ? nodeIndex : nodeIndex + 1;
        return [.. modules.Take(count)];
    }

    /// <summary>True for a splitter, a merger, and every unit between an open
    /// splitter and its merger.</summary>
    public static bool IsInSplitRegion(IReadOnlyList<DcRigModule> modules, int nodeIndex)
    {
        var open = false;
        for (var i = 0; i <= nodeIndex && i < modules.Count; i++)
        {
            var id = modules[i].ModuleTypeId;
            if (id == DcRigTargetCatalog.Splitter) open = true;
            if (i == nodeIndex) return open || id == DcRigTargetCatalog.Merger;
            if (id == DcRigTargetCatalog.Merger) open = false;
        }

        return false;
    }
}
