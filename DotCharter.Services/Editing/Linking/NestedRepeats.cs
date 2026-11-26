namespace DotCharter.Services.Editing.Linking;

/// <summary>
///     Short repeats linked INSIDE longer groups: bars 15, 17 and 19 of a section that is
///     itself linked to bars 66-73 hold the same riff, so an edit in bar 17 must reach bars 15, 19,
///     66, 68 and 70 as well. Every bar is placed in an orbit with the bars it is already linked
///     to (same offset in another copy of a group); a class of identical bars that spans two or
///     more orbits becomes a one-bar group of ALL its bars.
/// </summary>
internal static class NestedRepeats
{
    /// <param name="bars">Flat exact-content ids; an empty bar has an id of its own.</param>
    /// <param name="groups">Every group, proposed or existing, as the flat ids of its copies' first bars and its length.</param>
    /// <param name="nestable">Whether a flat bar may be a one-bar placement.</param>
    /// <param name="verify">The bar's key under the group rule; bars with equal exact ids are split by it.</param>
    public static List<List<int>> Find(int[] bars, IEnumerable<(IReadOnlyList<int> Firsts, int Length)> groups,
        Func<int, bool> nestable, Func<int, object> verify)
    {
        var orbit = Enumerable.Range(0, bars.Length).ToArray();
        foreach (var (firsts, length) in groups)
        {
            for (var offset = 0; offset < length; offset++)
            {
                foreach (var first in firsts.Skip(1)) Union(orbit, firsts[0] + offset, first + offset);
            }
        }

        var found = new List<List<int>>();
        var classes = Enumerable.Range(0, bars.Length).Where(nestable).GroupBy(at => bars[at]).Where(c => c.Skip(1).Any());
        foreach (var exact in classes)
        {
            foreach (var verified in exact.GroupBy(verify))
            {
                var members = verified.ToList();
                if (members.Select(at => Root(orbit, at)).Distinct().Skip(1).Any()) found.Add(members);
            }
        }

        return found;
    }

    /// <summary>
    ///     Joins one-bar classes that always follow each other into one longer nested run, so the
    ///     offer shows one group per repeated figure, not one per bar. A run grows only while its
    ///     copies stay inside one part, do not overlap each other, sit whole inside (or hold whole)
    ///     every placement they touch and still match under the group rule.
    /// </summary>
    /// <param name="found">The one-bar classes from <see cref="Find" />.</param>
    /// <param name="count">Measures per part.</param>
    /// <param name="groups">The same groups <see cref="Find" /> took.</param>
    /// <param name="proposed">How many of <paramref name="groups" />, from the start, are proposals that may fold into a run.</param>
    /// <param name="sameRun">Whether the copies match as runs of the given length under the group rule.</param>
    public static List<(List<int> Members, int Length)> Merge(List<List<int>> found, int count,
        IReadOnlyList<(IReadOnlyList<int> Firsts, int Length)> groups, int proposed, Func<List<int>, int, bool> sameRun)
    {
        var byFirst = found.Select(m => m.Order().ToList()).ToDictionary(m => m[0]);
        var taken = new HashSet<int>();
        var merged = new List<(List<int>, int)>();
        foreach (var start in byFirst.Keys.Order())
        {
            if (!taken.Add(start)) continue;
            var members = byFirst[start];
            var length = 1;
            while (byFirst.TryGetValue(start + length, out var next) && Follows(members, next, length, count) &&
                   Fits(members, length + 1, groups, proposed) && sameRun(members, length + 1))
            {
                taken.Add(start + length++);
            }

            merged.Add((members, length));
        }

        return merged;
    }

    private static bool Follows(List<int> members, List<int> next, int length, int count)
    {
        if (next.Count != members.Count) return false;
        for (var i = 0; i < members.Count; i++)
        {
            if (next[i] != members[i] + length || next[i] % count == 0) return false;
            if (i > 0 && members[i] - members[i - 1] <= length) return false;
        }

        return true;
    }

    /// <summary>Whether every copy nests with every placement it touches; an equal placement is fine only when its whole proposed group folds in.</summary>
    private static bool Fits(List<int> members, int length, IReadOnlyList<(IReadOnlyList<int> Firsts, int Length)> groups,
        int proposed)
    {
        for (var g = 0; g < groups.Count; g++)
        {
            var (firsts, span) = groups[g];
            var folds = g < proposed && span == length && firsts.All(members.Contains);
            foreach (var first in firsts)
            {
                foreach (var at in members)
                {
                    if (at >= first + span || first >= at + length) continue;
                    if (at == first && span == length ? !folds : !(first <= at && at + length <= first + span) &&
                                                               !(at <= first && first + span <= at + length))
                    {
                        return false;
                    }
                }
            }
        }

        return true;
    }

    private static int Root(int[] orbit, int at)
    {
        while (orbit[at] != at) at = orbit[at] = orbit[orbit[at]];
        return at;
    }

    private static void Union(int[] orbit, int a, int b)
    {
        var (ra, rb) = (Root(orbit, a), Root(orbit, b));
        if (ra != rb) orbit[Math.Max(ra, rb)] = Math.Min(ra, rb);
    }
}
