namespace DotCharter.Services.Editing.ChordShapes;

/// <summary>
///     Finds what Clean up shapes would tidy in an arrangement. Pure: it reads the chart, writes nothing.
///     <list type="bullet">
///         <item>
///             <b>Duplicates.</b> Used shapes with the same frets, fingers and flags. The copies join the
///             shape that keeps a name (then the most uses, then list order). Shapes under two different
///             names are never one group: each name is its own group, and the pair is left for review.
///         </item>
///         <item><b>Unused.</b> No chord, hand shape or notation-voice chord on any level uses them.</item>
///         <item>
///             <b>Review.</b> Used shapes with the same frets (and arpeggio flavour) that are not the same
///             grip: other fingers, another name or other flags. The one with the most uses is the first of
///             every pair it is in.
///         </item>
///     </list>
///     A shape with ghost gems never takes part: the merge key (<see cref="ChordTemplate.HasFrets" />) refuses it.
/// </summary>
public static class ShapeCleanupFinder
{
    public static ShapeCleanupFindings Find(Arrangement arrangement) =>
        Find(arrangement, ChordShapeCatalog.Build(arrangement, null));

    /// <summary>The same, over catalog entries the caller already built for this arrangement.</summary>
    public static ShapeCleanupFindings Find(Arrangement arrangement, IReadOnlyList<ChordShapeEntry> entries)
    {
        ArgumentNullException.ThrowIfNull(arrangement);
        ArgumentNullException.ThrowIfNull(entries);

        var unused = ChordShapeEdits.UnusedTemplates(arrangement);
        var unusedSet = new HashSet<ChordTemplate>(unused, ReferenceEqualityComparer.Instance);
        var uses = new Dictionary<ChordTemplate, ChordShapeUses>(ReferenceEqualityComparer.Instance);
        foreach (var entry in entries) uses[entry.Template] = entry.Uses;

        var merges = new List<ShapeCleanupMerge>();
        var near = new List<ShapeCleanupNearDuplicate>();
        var buckets = entries.Select(e => e.Template)
            .Where(t => t.GhostMask == 0 && !unusedSet.Contains(t))
            .GroupBy(FretsKey);
        foreach (var bucket in buckets)
        {
            var units = UnitsOf([.. bucket], uses);
            merges.AddRange(units.Where(u => u.Copies.Count > 0).Select(u => u.ToMerge(uses)));
            near.AddRange(PairsOf(units));
        }

        var order = new Dictionary<ChordTemplate, int>(ReferenceEqualityComparer.Instance);
        foreach (var (entry, index) in entries.Select((e, i) => (e, i))) order[entry.Template] = index;
        return new ShapeCleanupFindings(
            [.. merges.OrderBy(m => order[m.Keeper])], unused, [.. near.OrderBy(n => order[n.First]).ThenBy(n => order[n.Second])]);
    }

    /// <summary>One group of identical shapes (or a lone shape): the keeper and the copies that join it.</summary>
    private sealed record Unit(ChordTemplate Keeper, List<ChordTemplate> Copies, int Uses)
    {
        public ShapeCleanupMerge ToMerge(IReadOnlyDictionary<ChordTemplate, ChordShapeUses> uses) => new(
            Keeper, Copies, Copies.Sum(c => uses[c].ChordsAll), Copies.Sum(c => uses[c].HandShapesAll));
    }

    private static string FretsKey(ChordTemplate template) =>
        string.Join(',', template.Frets.OrderBy(f => f.Key).Select(f => $"{f.Key}:{f.Value}"));

    private static bool SameGrip(ChordTemplate a, ChordTemplate b) =>
        a.HasSameShapeAs(b) && ArpeggioFlavour.Of(a).Matches(b) && a.NopInRs == b.NopInRs && a.Fingerless == b.Fingerless;

    private static bool SameName(ChordTemplate a, ChordTemplate b) =>
        string.Equals(a.Name.Trim(), b.Name.Trim(), StringComparison.OrdinalIgnoreCase);

    private static List<Unit> UnitsOf(List<ChordTemplate> bucket, IReadOnlyDictionary<ChordTemplate, ChordShapeUses> uses)
    {
        var groups = new List<List<ChordTemplate>>();
        foreach (var template in bucket)
        {
            var group = groups.FirstOrDefault(g => SameGrip(g[0], template));
            if (group is null) groups.Add([template]);
            else group.Add(template);
        }

        var order = new Dictionary<ChordTemplate, int>(ReferenceEqualityComparer.Instance);
        foreach (var (template, index) in bucket.Select((t, i) => (t, i))) order[template] = index;
        return [.. groups.SelectMany(g => NameClasses(g)).Select(members => UnitOf(members, uses, order))];
    }

    /// <summary>
    ///     Splits identical shapes by name: with at most one name they are one class (the unnamed copies join
    ///     the named one); with two or more, each name is a class and the unnamed copies are another.
    /// </summary>
    private static IEnumerable<List<ChordTemplate>> NameClasses(List<ChordTemplate> group)
    {
        var named = group.Where(t => !ChordShapeNames.IsUnnamed(t)).ToList();
        var classes = new List<List<ChordTemplate>>();
        foreach (var template in named)
        {
            var existing = classes.FirstOrDefault(c => SameName(c[0], template));
            if (existing is null) classes.Add([template]);
            else existing.Add(template);
        }

        var unnamed = group.Where(ChordShapeNames.IsUnnamed).ToList();
        if (classes.Count <= 1)
        {
            yield return group;
            yield break;
        }

        foreach (var c in classes) yield return c;
        if (unnamed.Count > 0) yield return unnamed;
    }

    private static Unit UnitOf(
        List<ChordTemplate> members, IReadOnlyDictionary<ChordTemplate, ChordShapeUses> uses,
        IReadOnlyDictionary<ChordTemplate, int> order)
    {
        var keeper = members
            .OrderBy(t => ChordShapeNames.IsUnnamed(t) ? 1 : 0)
            .ThenByDescending(t => uses[t].All)
            .ThenBy(t => order[t])
            .First();
        return new Unit(keeper, [.. members.Where(t => !ReferenceEquals(t, keeper))], members.Sum(t => uses[t].All));
    }

    /// <summary>The pairs of one frets bucket: its busiest unit against every other unit of the same flavour.</summary>
    private static IEnumerable<ShapeCleanupNearDuplicate> PairsOf(List<Unit> units)
    {
        var remaining = new List<Unit>(units);
        while (remaining.Count > 0)
        {
            var family = remaining.Where(u => ArpeggioFlavour.Of(remaining[0].Keeper).Matches(u.Keeper)).ToList();
            remaining.RemoveAll(family.Contains);
            if (family.Count < 2) continue;

            var primary = family.OrderByDescending(u => u.Uses).First();
            foreach (var other in family.Where(u => !ReferenceEquals(u, primary)))
                yield return Pair(primary, other);
        }
    }

    private static ShapeCleanupNearDuplicate Pair(Unit first, Unit second)
    {
        var a = first.Keeper;
        var b = second.Keeper;
        var difference = !a.HasSameShapeAs(b) ? ShapeCleanupDifference.Fingers
            : !SameName(a, b) ? ShapeCleanupDifference.Name
            : ShapeCleanupDifference.Flags;
        var strings = difference != ShapeCleanupDifference.Fingers
            ? []
            : a.Frets.Keys.Where(s => a.Fingers.GetValueOrDefault(s) != b.Fingers.GetValueOrDefault(s)).Order().ToList();
        return new ShapeCleanupNearDuplicate(a, b, first.Uses, second.Uses, difference, strings);
    }
}
