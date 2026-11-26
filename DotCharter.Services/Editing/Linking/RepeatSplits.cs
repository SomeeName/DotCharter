namespace DotCharter.Services.Editing.Linking;

/// <summary>
///     Where a proposed link group must be cut so a shorter repeat inside it can be linked to its
///     other copies too. Taking the longest run first can swallow a section that also
///     plays elsewhere: a 20-bar unison run of two parts holds bars 15-22, which come back at
///     66-73, and a bar may belong to one section group only. The finder cuts the long run where
///     the shorter repeat starts and ends, in every copy of its group and at the other copy, and
///     searches again.
///     <para>
///         Bars are flat ids (<c>part * measureCount + measure</c>) of exact content; an empty bar
///         carries an id no other bar has, so rests never make a repeat.
///     </para>
/// </summary>
internal static class RepeatSplits
{
    /// <summary>The shortest repeat that cuts a section; a shorter one is linked nested inside it
    /// instead (<see cref="NestedRepeats" />).</summary>
    public const int MinimumLength = 4;

    /// <summary>The cuts as (part, measure boundary), possibly with duplicates.</summary>
    public static List<(int Part, int Boundary)> Find(int[] bars, int measureCount,
        IEnumerable<(IReadOnlyList<(int Part, int First)> Copies, int Length)> groups, Action<int> charge)
    {
        var starts = new Dictionary<int, List<int>>();
        for (var at = 0; at < bars.Length; at++)
        {
            if (!starts.TryGetValue(bars[at], out var list)) starts[bars[at]] = list = [];
            list.Add(at);
        }

        var cuts = new List<(int Part, int Boundary)>();
        foreach (var (copies, length) in groups)
        {
            if (length <= MinimumLength) continue;
            var lead = copies[0];
            for (var offset = 0; offset < length; offset++)
            {
                var at = lead.Part * measureCount + lead.First + offset;
                foreach (var other in starts[bars[at]])
                {
                    charge(1);
                    var (part, first) = (other / measureCount, other % measureCount);
                    if (first > 0 && offset > 0 && bars[at - 1] == bars[other - 1]) continue;
                    var reach = 0;
                    while (offset + reach < length && first + reach < measureCount && bars[at + reach] == bars[other + reach])
                        reach++;
                    if (reach < MinimumLength || reach == length) continue;
                    if (copies.Any(c => c.Part == part && first < c.First + length && c.First < first + reach)) continue;
                    foreach (var copy in copies)
                    {
                        cuts.Add((copy.Part, copy.First + offset));
                        cuts.Add((copy.Part, copy.First + offset + reach));
                    }

                    cuts.Add((part, first));
                    cuts.Add((part, first + reach));
                }
            }
        }

        return cuts;
    }
}
