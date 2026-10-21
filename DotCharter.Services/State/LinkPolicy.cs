namespace DotCharter.Services.State;

/// <summary>
///     What the history does with linked measures after an action changed them. Every
///     chart action declares one (<see cref="ILinkPolicyAction" />);
///     <c>LinkPolicyReachabilityTests</c> fails for a product action that does not.
/// </summary>
public enum LinkPolicy
{
    /// <summary>
    ///     An editing gesture. When the copies an edit changed all agree, the change is copied to
    ///     every other placement of the group in the SAME undo step; copies changed in different
    ///     ways are split into groups by content.
    /// </summary>
    Propagate,

    /// <summary>
    ///     A tool that applies outside truth to the chart (import, sync, refret, AI passes, the
    ///     tempo-map tools). It is never propagated: every placement that comes out different
    ///     from its group is unlinked (copies that still agree with each other stay linked).
    /// </summary>
    UnlinkOnDivergence,

    /// <summary>
    ///     A generator that computes its result for the whole chart (FHP, fingering, the AI chat,
    ///     difficulty levels). Its result is kept ONCE per group: the first placement, in group
    ///     order, whose copy the action changed is copied to every other placement in the SAME
    ///     undo step, so every link survives even where the generator came out differently per
    ///     copy. Content outside the placements keeps exactly what the generator wrote.
    /// </summary>
    ShareFirstChanged,

    /// <summary>
    ///     The action cannot change linked content in beats (tempo, metadata, tones, lyrics,
    ///     sections, phrases, the link groups themselves). Nothing is propagated or unlinked;
    ///     the history only re-reads the copies. A change to a placement's measures (a time
    ///     signature, a beat added inside it) still unlinks that placement — under every policy.
    /// </summary>
    Retain
}

/// <summary>A chart action's declared <see cref="LinkPolicy" />.</summary>
public interface ILinkPolicyAction
{
    LinkPolicy LinkPolicy { get; }
}

/// <summary>The policy the history applies for an action.</summary>
public static class LinkPolicies
{
    /// <summary>
    ///     The declared policy; a composite without its own takes its children's strictest
    ///     (unlink, then share, then propagate, then retain). An action that declares nothing is treated as
    ///     <see cref="LinkPolicy.UnlinkOnDivergence" />: an unknown tool must never fan its
    ///     change out to every copy.
    /// </summary>
    public static LinkPolicy Of(IChartAction action)
    {
        ArgumentNullException.ThrowIfNull(action);
        if (action is CompositeAction { LinkPolicyOverride: { } forced }) return forced;
        if (action is CompositeAction composite)
        {
            var policy = LinkPolicy.Retain;
            foreach (var child in composite.Actions)
            {
                var childPolicy = Of(child);
                if (childPolicy == LinkPolicy.UnlinkOnDivergence) return childPolicy;
                if (childPolicy == LinkPolicy.ShareFirstChanged) policy = childPolicy;
                else if (childPolicy == LinkPolicy.Propagate && policy == LinkPolicy.Retain) policy = childPolicy;
            }

            return policy;
        }

        return action is ILinkPolicyAction declared ? declared.LinkPolicy : LinkPolicy.UnlinkOnDivergence;
    }
}
