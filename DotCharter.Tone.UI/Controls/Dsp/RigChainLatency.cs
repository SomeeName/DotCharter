namespace DotCharter.Tone.UI.Controls.Dsp;

/// <summary>One unit as the chain-latency sum sees it.</summary>
public readonly record struct RigLatencyUnit(bool IsSplitter, bool IsMerger, int Branch, int? LatencySamples);

/// <summary>
///     The whole chain's latency from the per-node figures the engine reports
/// (ABI v30, <c>IDspChainService.GetNodeLatencySamples</c>), pure.
///     Serial units add up. Inside a split the lanes run side by side, so the
///     region costs its SLOWEST lane, not the sum of all of them. Branch 0 is
///     "unassigned", which the engine plays as lane 1.
///     <para>
///         <b>Unknown stays unknown.</b> One unit without a reading makes the
///         total null, never a smaller number: a board that says "2 ms" while a
///         lookahead unit nobody measured adds 40 is worse than one that says
///         nothing.
///     </para>
/// </summary>
public static class RigChainLatency
{
    public static int? TotalSamples(IEnumerable<RigLatencyUnit> units)
    {
        var total = 0;
        Dictionary<int, int>? lanes = null;
        var any = false;
        foreach (var unit in units)
        {
            any = true;
            if (unit.LatencySamples is not { } samples) return null;
            if (unit.IsSplitter)
            {
                total += samples;
                lanes = [];
                continue;
            }

            if (unit.IsMerger)
            {
                total += (lanes is { Count: > 0 } ? lanes.Values.Max() : 0) + samples;
                lanes = null;
                continue;
            }

            if (lanes is null)
            {
                total += samples;
                continue;
            }

            var lane = Math.Max(1, unit.Branch);
            lanes[lane] = lanes.GetValueOrDefault(lane) + samples;
        }

        // A split left open (a merger not placed yet) still costs its slowest lane.
        if (lanes is { Count: > 0 }) total += lanes.Values.Max();
        return any ? total : null;
    }
}
