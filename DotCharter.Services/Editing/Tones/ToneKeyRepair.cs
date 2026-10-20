using DotCharter.Services.State;

namespace DotCharter.Services.Editing.Tones;

/// <summary>A repair <see cref="ToneKeyRepair.Propose" /> found: rename the defined tone <see cref="From" />
/// onto the key <see cref="To" /> that the chart uses but nothing defines. <see cref="Action" /> is
/// <see cref="ToneKeyEdits.PlanRename" />'s, so applying it is ONE undo step.</summary>
public sealed record ToneKeyRepairProposal(string From, string To, IChartAction Action);

/// <summary>
///     Proposes the repair of an undefined tone key ONLY when the project leaves no doubt which tone
/// it is. The old partial renames left exactly two shapes behind, and both are one
///     undefined key next to one defined tone that nothing PLAYS:
///     <list type="bullet">
///         <item>the Song tab moved the tone changes and the starting tone, and left the definition and
///         the Base/A–D slots on the old key;</item>
///         <item>the Tone designer moved the definition alone, so it is referenced by nothing at all.</item>
///     </list>
///     A defined tone is a candidate when no tone change names it, it is no arrangement's starting tone
///     (the base slot stands in for an empty one), and no arrangement holds it AND the undefined key in
///     two of its Base/A–D slots — renaming would fold two slots of one arrangement into one tone. The
///     proposal exists when there is exactly one undefined key and exactly one candidate; anything else
///     stays a report. The defined tone takes the key the chart uses, never the other way round: that is
///     the one direction <see cref="ToneKeyEdits.PlanRename" /> allows onto a key in use.
/// </summary>
public static class ToneKeyRepair
{
    /// <summary>The unambiguous repair, or null. Pure: nothing changes until the history runs the action.</summary>
    public static ToneKeyRepairProposal? Propose(ChartProject project)
    {
        ArgumentNullException.ThrowIfNull(project);
        var keys = ToneKeyEdits.Keys(project);
        var undefined = keys.Where(k => !k.IsDefined).Select(k => k.Key).ToList();
        if (undefined.Count != 1) return null;

        var target = undefined[0];
        var candidates = keys.Where(k => k.IsDefined && IsUnplayed(project, k.Key, target)).Select(k => k.Key).ToList();
        if (candidates.Count != 1) return null;

        var plan = ToneKeyEdits.PlanRename(project, candidates[0], target);
        return plan.IsReady ? new ToneKeyRepairProposal(candidates[0], plan.NewKey, plan.Action!) : null;
    }

    /// <summary>The proposal, only when it repairs <paramref name="undefinedKey" /> (the key an export finding names).</summary>
    public static ToneKeyRepairProposal? ProposeFor(ChartProject project, string? undefinedKey) =>
        !string.IsNullOrWhiteSpace(undefinedKey) && Propose(project) is { } proposal && ToneKeyEdits.Same(proposal.To, undefinedKey)
            ? proposal
            : null;

    private static bool IsUnplayed(ChartProject project, string defined, string target)
    {
        foreach (var arrangement in project.Arrangements)
        {
            var starting = string.IsNullOrWhiteSpace(arrangement.StartingTone) ? arrangement.ToneBase : arrangement.StartingTone;
            if (ToneKeyEdits.Same(starting, defined)) return false;
            if (arrangement.ToneChanges.Any(c => ToneKeyEdits.Same(c.ToneName, defined))) return false;

            string[] slots = [arrangement.ToneBase, arrangement.ToneA, arrangement.ToneB, arrangement.ToneC, arrangement.ToneD];
            if (slots.Any(s => ToneKeyEdits.Same(s, defined)) && slots.Any(s => ToneKeyEdits.Same(s, target))) return false;
        }

        return true;
    }
}
