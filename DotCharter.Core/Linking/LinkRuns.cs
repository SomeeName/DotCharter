using System.Globalization;
using DotCharter.Core.Domain.Project;

namespace DotCharter.Core.Linking;

/// <summary>
///     The shared vocabulary of linked measures over a live project: where a placement sits on
///     the measure grid, whether it still spans whole measures, the content key of each copy
///     under its group's rule, and how a group is split when its copies stop agreeing. Used by
///     the history's reconciler, the load check and the <c>.dcproj</c> writer, so all three read a
///     placement the same way.
/// </summary>
public static class LinkRuns
{
    /// <summary>The comparison rule <paramref name="group" /> was created under, or null when its
    /// <see cref="LinkGroup.Rule" /> is not a detection rule this build can read.</summary>
    public static MeasureContentOptions? RuleOf(LinkGroup group)
    {
        ArgumentNullException.ThrowIfNull(group);
        return MeasureContentOptions.TryParseHeader(group.Rule, out var options) && options.Kind == MeasureContentKind.Detection
            ? options
            : null;
    }

    /// <summary>A placement is live while its arrangement is still in <paramref name="project" />
    /// and can still hold a link (<see cref="LinkCompatibility" />).</summary>
    public static bool IsLive(ChartProject project, LinkPlacement placement) =>
        placement.Arrangement is { } arrangement && project.Arrangements.Contains(arrangement) &&
        LinkCompatibility.Signature(arrangement) != null;

    /// <summary>The measures <c>[first, first + count)</c> of <paramref name="measures" /> the placement
    /// spans, or false when either end is not a barline of the grid.</summary>
    public static bool TryResolve(IReadOnlyList<MeasureSpan> measures, LinkPlacement placement, out int first, out int count)
    {
        first = -1;
        count = 0;
        if (placement.Position.Fraction.Numerator != 0 || placement.EndPosition.Fraction.Numerator != 0) return false;
        if (placement.EndPosition <= placement.Position) return false;

        for (var m = 0; m < measures.Count; m++)
        {
            if (measures[m].StartBeat != placement.Position.BeatId) continue;
            for (var e = m; e < measures.Count; e++)
            {
                if (measures[e].EndBeat == placement.EndPosition.BeatId)
                {
                    first = m;
                    count = e - m + 1;
                    return true;
                }

                if (measures[e].EndBeat > placement.EndPosition.BeatId) return false;
            }

            return false;
        }

        return false;
    }

    /// <summary>
    ///     The placement's measure geometry: each measure's time signature and beat count, in
    ///     order. Null when the placement does not span whole measures. Two placements of one
    ///     group must have equal geometry; inserting or removing a beat inside a run, or changing
    ///     a time signature there, changes it — which is what unlinks that placement.
    /// </summary>
    public static string? Geometry(IReadOnlyList<MeasureSpan> measures, LinkPlacement placement)
    {
        if (!TryResolve(measures, placement, out var first, out var count)) return null;
        var parts = new string[count];
        for (var i = 0; i < count; i++)
        {
            var measure = measures[first + i];
            parts[i] = string.Create(CultureInfo.InvariantCulture,
                $"{measure.BeatsInMeasure}/{measure.NoteDenominator}x{measure.BeatCount}");
        }

        return string.Join(",", parts);
    }

    /// <summary>
    ///     The detection key of each live placement of <paramref name="group" /> under
    ///     <paramref name="rule" />; null for a placement that does not span whole measures.
    ///     <paramref name="indexes" /> caches one <see cref="MeasureContentIndex" /> per
    ///     arrangement across calls that share a rule and a chart state.
    /// </summary>
    public static Dictionary<LinkPlacement, MeasureContentKey?> Keys(ChartProject project, LinkGroup group,
        MeasureContentOptions rule, Dictionary<Arrangement, MeasureContentIndex>? indexes = null)
    {
        indexes ??= [];
        var keys = new Dictionary<LinkPlacement, MeasureContentKey?>(group.Placements.Count);
        foreach (var placement in group.Placements)
        {
            if (!IsLive(project, placement)) continue;
            var index = IndexFor(project, placement.Arrangement!, rule, indexes);
            keys[placement] = TryResolve(index.Measures, placement, out var first, out var count)
                ? index.KeyFor(first, count)
                : null;
        }

        return keys;
    }

    /// <summary>The index over <paramref name="arrangement" />'s hardest level under <paramref name="rule" />.</summary>
    public static MeasureContentIndex IndexFor(ChartProject project, Arrangement arrangement, MeasureContentOptions rule,
        Dictionary<Arrangement, MeasureContentIndex> indexes)
    {
        if (!indexes.TryGetValue(arrangement, out var index))
        {
            index = MeasureContentIndex.ForHardestLevel(arrangement, project.Beats, rule);
            indexes[arrangement] = index;
        }

        return index;
    }

    /// <summary>
    ///     Splits <paramref name="group" /> by content: the placements whose class is
    ///     <paramref name="keepClass" /> stay in the group, every other class of two or more
    ///     becomes a group of its own (a new id and letter, the same name and rule), and a
    ///     placement alone in its class is unlinked. A group left with fewer than two placements
    ///     is dissolved. Returns how many placements left <paramref name="group" />.
    /// </summary>
    public static int Partition(ChartProject project, LinkGroup group, IReadOnlyDictionary<LinkPlacement, string> classOf,
        string? keepClass)
    {
        var moved = 0;
        var byClass = new Dictionary<string, List<LinkPlacement>>(StringComparer.Ordinal);
        foreach (var placement in group.Placements.ToList())
        {
            if (!classOf.TryGetValue(placement, out var cls))
            {
                group.Placements.Remove(placement);
                moved++;
                continue;
            }

            if (cls == keepClass) continue;
            group.Placements.Remove(placement);
            moved++;
            if (!byClass.TryGetValue(cls, out var list)) byClass[cls] = list = [];
            list.Add(placement);
        }

        var at = project.LinkGroups.IndexOf(group);
        foreach (var (_, placements) in byClass.OrderBy(c => c.Value[0].Position))
        {
            if (placements.Count < 2) continue;
            var split = new LinkGroup { Letter = NextLetter(project), Name = group.Name, Rule = group.Rule };
            foreach (var placement in placements) split.Placements.Add(placement);
            project.LinkGroups.Insert(at < 0 ? project.LinkGroups.Count : ++at, split);
        }

        DissolveIfAlone(project, group);
        return moved;
    }

    /// <summary>
    ///     Unlinks every live placement whose arrangement does not share the group's
    ///     <see cref="LinkCompatibility" /> class (a tuning, capo or string-count change): the class
    ///     most placements are in keeps the group (ties: the earliest placement's). Returns how many left.
    /// </summary>
    public static int DropIncompatible(LinkGroup group)
    {
        var classes = group.Placements
            .Where(p => p.Arrangement != null)
            .Select((p, i) => (Placement: p, Index: i, Signature: LinkCompatibility.Signature(p.Arrangement!)))
            .Where(c => c.Signature != null)
            .ToList();
        var keep = classes
            .GroupBy(c => c.Signature!, StringComparer.Ordinal)
            .OrderByDescending(g => g.Count())
            .ThenBy(g => g.Min(c => c.Index))
            .Select(g => g.Key)
            .FirstOrDefault();
        var dropped = 0;
        foreach (var (placement, _, signature) in classes)
        {
            if (signature == keep) continue;
            group.Placements.Remove(placement);
            dropped++;
        }

        return dropped;
    }

    /// <summary>Removes <paramref name="group" /> when it links nothing (fewer than two placements).</summary>
    public static bool DissolveIfAlone(ChartProject project, LinkGroup group)
    {
        if (group.Placements.Count >= 2) return false;
        project.LinkGroups.Remove(group);
        return true;
    }

    /// <summary>The first form letter (A…Z, then AA, AB…) no group of <paramref name="project" /> uses.</summary>
    public static string NextLetter(ChartProject project) => NextLetter(project.LinkGroups);

    /// <summary>The first form letter none of <paramref name="groups" /> uses.</summary>
    public static string NextLetter(IEnumerable<LinkGroup> groups)
    {
        var used = groups.Select(g => g.Letter).ToHashSet(StringComparer.Ordinal);
        for (var n = 0; ; n++)
        {
            var letter = Letter(n);
            if (!used.Contains(letter)) return letter;
        }
    }

    private static string Letter(int n) => n < 26 ? ((char)('A' + n)).ToString() : Letter(n / 26 - 1) + (char)('A' + n % 26);
}
