using DotCharter.Core.Domain.Project;

namespace DotCharter.Core.Linking;

/// <summary>Why a load check unlinked placements.</summary>
public enum LinkLoadReason
{
    /// <summary>The copy does not match the group's saved content (edited by an older build or by hand).</summary>
    ContentChanged,

    /// <summary>The placement does not span whole measures, names no arrangement, overlaps another
    /// placement, or its arrangement cannot hold a link.</summary>
    PlacementInvalid
}

/// <summary>One group the load check changed: how many of its placements were unlinked, and why.</summary>
public sealed record LinkLoadNotice(string Letter, string Name, int Unlinked, LinkLoadReason Reason);

/// <summary>
///     The load check of linked measures. A link is a rule that keeps real copies equal, so a
///     file can carry copies that stopped being equal — an older build (which preserves the
///     <c>dcLinkGroups</c> node verbatim but does not propagate), a hand edit, a stale
///     arrangement index. Every copy is compared under its group's rule; the copies that match
///     the saved content hash stay linked, the others are UNLINKED with their content kept
///     (copies that still agree with each other keep a group of their own), and the caller gets
///     a notice per group. Nothing is ever rewritten to make it match.
/// </summary>
public static class LinkGroupVerifier
{
    /// <param name="project">The freshly loaded project.</param>
    /// <param name="storedHashes">Each group's saved content hash (<see cref="MeasureContentKey.Hash" />), when it had one.</param>
    public static IReadOnlyList<LinkLoadNotice> Verify(ChartProject project, IReadOnlyDictionary<LinkGroup, string> storedHashes)
    {
        ArgumentNullException.ThrowIfNull(project);
        ArgumentNullException.ThrowIfNull(storedHashes);
        var notices = new List<LinkLoadNotice>();
        var indexes = new Dictionary<(Arrangement, string), MeasureContentIndex>();
        var claimed = new Dictionary<Arrangement, List<LinkPlacement>>();

        foreach (var group in project.LinkGroups.ToList())
        {
            var invalid = DropInvalid(project, group, claimed) + LinkRuns.DropIncompatible(group);
            var rule = LinkRuns.RuleOf(group) ?? MeasureContentOptions.Detection;
            var keys = new Dictionary<LinkPlacement, string>();
            foreach (var placement in group.Placements.ToList())
            {
                var key = KeyOf(project, placement, rule, indexes);
                if (key == null)
                {
                    group.Placements.Remove(placement);
                    invalid++;
                    continue;
                }

                keys[placement] = key.Canonical;
            }

            var keep = KeepClass(group, keys, storedHashes.GetValueOrDefault(group));
            var letter = group.Letter;
            var name = group.Name;
            var diverged = LinkRuns.Partition(project, group, keys, keep);
            if (invalid > 0) notices.Add(new LinkLoadNotice(letter, name, invalid, LinkLoadReason.PlacementInvalid));
            if (diverged > 0) notices.Add(new LinkLoadNotice(letter, name, diverged, LinkLoadReason.ContentChanged));
        }

        return notices;
    }

    /// <summary>
    ///     The class that keeps the group: the one whose key matches the saved hash; without a
    ///     match (no hash, another rule, or every copy edited), the largest class, earliest first.
    /// </summary>
    private static string? KeepClass(LinkGroup group, Dictionary<LinkPlacement, string> keys, string? storedHash)
    {
        if (keys.Count == 0) return null;
        if (storedHash != null)
        {
            foreach (var placement in group.Placements)
            {
                if (keys.TryGetValue(placement, out var canonical) && HashOf(canonical) == storedHash) return canonical;
            }
        }

        return keys
            .GroupBy(k => k.Value, StringComparer.Ordinal)
            .OrderByDescending(g => g.Count())
            .ThenBy(g => g.Min(k => k.Key.Position))
            .First().Key;
    }

    private static string HashOf(string canonical) => new MeasureContentKey(canonical, 0).Hash;

    private static int DropInvalid(ChartProject project, LinkGroup group, Dictionary<Arrangement, List<LinkPlacement>> claimed)
    {
        var dropped = 0;
        foreach (var placement in group.Placements.ToList())
        {
            var ok = LinkRuns.IsLive(project, placement) && placement.EndPosition > placement.Position;
            if (ok)
            {
                if (!claimed.TryGetValue(placement.Arrangement!, out var taken)) claimed[placement.Arrangement!] = taken = [];
                ok = !taken.Any(t => t.Position < placement.EndPosition && placement.Position < t.EndPosition);
                if (ok) taken.Add(placement);
            }

            if (ok) continue;
            group.Placements.Remove(placement);
            dropped++;
        }

        return dropped;
    }

    private static MeasureContentKey? KeyOf(ChartProject project, LinkPlacement placement, MeasureContentOptions rule,
        Dictionary<(Arrangement, string), MeasureContentIndex> indexes)
    {
        var cacheKey = (placement.Arrangement!, rule.Header());
        if (!indexes.TryGetValue(cacheKey, out var index))
        {
            index = MeasureContentIndex.ForHardestLevel(placement.Arrangement!, project.Beats, rule);
            indexes[cacheKey] = index;
        }

        return LinkRuns.TryResolve(index.Measures, placement, out var first, out var count) ? index.KeyFor(first, count) : null;
    }
}
