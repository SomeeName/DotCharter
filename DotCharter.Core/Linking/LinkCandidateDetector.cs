namespace DotCharter.Core.Linking;

/// <summary>One placement of a link group: <see cref="LinkCandidateGroup.MeasureCount" /> measures from
/// <paramref name="FirstMeasure" /> in the part at <paramref name="Part" /> (an index into the
/// list handed to <see cref="LinkCandidateDetector.Detect" />).</summary>
public readonly record struct LinkCandidatePlacement(int Part, int FirstMeasure);

/// <summary>A run of measures whose content (<see cref="MeasureContentKey" />) is the same at
/// every placement.</summary>
public sealed record LinkCandidateGroup(string Letter, MeasureContentKey Key, int MeasureCount, IReadOnlyList<LinkCandidatePlacement> Placements)
{
    /// <summary>The group has placements in more than one part.</summary>
    public bool SpansParts => Placements.Select(p => p.Part).Distinct().Skip(1).Any();
}

/// <summary>What <see cref="LinkCandidateDetector.Detect" /> found.</summary>
public sealed record LinkDetectionResult(IReadOnlyList<LinkCandidateGroup> Groups, bool Truncated)
{
    /// <summary>Per measure of <paramref name="part" />: does a group cover it?</summary>
    public bool[] Covered(int part, int measureCount, bool spanningOnly = false)
    {
        var covered = new bool[measureCount];
        foreach (var group in Groups)
        {
            if (spanningOnly && !group.SpansParts) continue;
            foreach (var placement in group.Placements)
            {
                if (placement.Part != part) continue;
                for (var m = placement.FirstMeasure; m < placement.FirstMeasure + group.MeasureCount && m < measureCount; m++)
                    covered[m] = true;
            }
        }

        return covered;
    }

    /// <summary>The group holding measure <paramref name="measure" /> of <paramref name="part" />, or null.</summary>
    public LinkCandidateGroup? GroupAt(int part, int measure) =>
        Groups.FirstOrDefault(g => g.Placements.Any(p =>
            p.Part == part && measure >= p.FirstMeasure && measure < p.FirstMeasure + g.MeasureCount));
}

/// <summary>
///     Finds runs of whole measures that repeat — within one part, or across several parts
///     on one beat grid. Pure and deterministic: the same input gives the same
///     groups in the same order.
///     <para>
///         Greedy, longest first: the longest run that occurs twice without overlapping
///         itself wins, every other non-overlapping occurrence of exactly that run joins it,
///         and its measures are taken. Runs never start or end on an empty measure (one with
///         no sound), and a run is compared as a WHOLE (<see cref="MeasureContentIndex.KeyFor" />
///         over the run) before it is grouped — equal single measures are only the candidate
///         filter, because a sustain held across an inner barline is compared too.
///     </para>
///     <para>
///         The caller decides what may share: hand it ONE part for repeats within an
///         arrangement, or the parts of one <see cref="LinkCompatibility" /> class (hardest
///         levels, <see cref="MeasureContentIndex.ForHardestLevel" />) for shared runs.
///     </para>
/// </summary>
public static class LinkCandidateDetector
{
    /// <summary>Above this many equal-measure pairs the search stops (a drone of hundreds of
    /// identical bars) and the result says <see cref="LinkDetectionResult.Truncated" />.</summary>
    public const int MaxPairs = 4_000_000;

    public static LinkDetectionResult Detect(IReadOnlyList<MeasureContentIndex> parts)
    {
        ArgumentNullException.ThrowIfNull(parts);
        var flat = Flatten(parts);
        var positionsById = StartsById(flat);

        long pairCount = 0;
        foreach (var list in positionsById.Values) pairCount += (long)list.Count * (list.Count - 1) / 2;
        if (pairCount > MaxPairs) return new LinkDetectionResult([], Truncated: true);

        var search = new Search(parts, flat, LongestCommonRuns(flat.Ids, flat.Ids.Length), positionsById, Pairs(positionsById, pairCount));
        var found = new List<(MeasureContentKey Key, int Length, List<LinkCandidatePlacement> Placements)>();
        while (search.Next() is { } group) found.Add(group);

        var groups = found
            .OrderBy(g => g.Placements.Min(p => (p.Part, p.FirstMeasure)))
            .Select((g, i) => new LinkCandidateGroup(Letter(i), g.Key, g.Length, g.Placements))
            .ToList();
        return new LinkDetectionResult(groups, Truncated: false);
    }

    /// <summary>Every non-empty measure start, by its single-measure key id.</summary>
    private static Dictionary<int, List<int>> StartsById(Flattened flat)
    {
        var positionsById = new Dictionary<int, List<int>>();
        for (var x = 0; x < flat.Ids.Length; x++)
        {
            if (flat.Ids[x] < 0 || flat.Empty[x]) continue;
            if (!positionsById.TryGetValue(flat.Ids[x], out var list)) positionsById[flat.Ids[x]] = list = [];
            list.Add(x);
        }

        return positionsById;
    }

    /// <summary>Every pair of equal starts, ordered by first then second start.</summary>
    private static List<(int I, int J)> Pairs(Dictionary<int, List<int>> positionsById, long pairCount)
    {
        var pairs = new List<(int I, int J)>((int)pairCount);
        foreach (var list in positionsById.Values)
        {
            for (var a = 0; a < list.Count; a++)
            {
                for (var b = a + 1; b < list.Count; b++)
                    pairs.Add((list[a], list[b]));
            }
        }

        pairs.Sort(static (p, q) => p.I != q.I ? p.I.CompareTo(q.I) : p.J.CompareTo(q.J));
        return pairs;
    }

    /// <summary>The greedy search: each <see cref="Next" /> takes the longest run still free.</summary>
    private sealed class Search(IReadOnlyList<MeasureContentIndex> parts, Flattened flat, PairRuns lcp,
        Dictionary<int, List<int>> positionsById, List<(int I, int J)> pairs)
    {
        private readonly bool[] _covered = new bool[flat.Ids.Length];
        private readonly int[] _freeRun = new int[flat.Ids.Length + 1];
        private readonly Dictionary<(int, int), int> _caps = [];

        public (MeasureContentKey Key, int Length, List<LinkCandidatePlacement> Placements)? Next()
        {
            while (true)
            {
                for (var x = flat.Ids.Length - 1; x >= 0; x--)
                    _freeRun[x] = flat.Ids[x] < 0 || _covered[x] ? 0 : 1 + _freeRun[x + 1];

                var (length, i, j) = Best();
                if (length == 0) return null;

                // Compare the WHOLE run; shorten (and remember the cap) if an inner barline differs.
                var verified = Verified(i, j, length);
                if (verified < length)
                {
                    _caps[(i, j)] = verified;
                    continue;
                }

                var key = RunKey(parts, flat, i, length);
                var placements = Placements(i, length, key);
                if (placements.Count < 2)
                {
                    // An earlier occurrence overlapped the pair: try the pair one measure shorter.
                    _caps[(i, j)] = length - 1;
                    continue;
                }

                foreach (var placement in placements)
                {
                    var x = flat.Offset[placement.Part] + placement.FirstMeasure;
                    for (var m = 0; m < length; m++) _covered[x + m] = true;
                }

                return (key, length, placements);
            }
        }

        private (int Length, int I, int J) Best()
        {
            var best = (Length: 0, I: -1, J: -1);
            foreach (var (i, j) in pairs)
            {
                var length = Usable(i, j);
                if (length > best.Length) best = (length, i, j);
            }

            return best;
        }

        private int Usable(int i, int j)
        {
            if (_freeRun[i] == 0 || _freeRun[j] == 0) return 0;
            var length = Math.Min(lcp.Get(i, j), Math.Min(_freeRun[i], _freeRun[j]));
            if (flat.Part[i] == flat.Part[j]) length = Math.Min(length, j - i);
            if (_caps.TryGetValue((i, j), out var cap)) length = Math.Min(length, cap);
            while (length > 0 && flat.Empty[i + length - 1]) length--;
            return length;
        }

        private int Verified(int i, int j, int length)
        {
            var verified = length;
            while (verified > 0 && (flat.Empty[i + verified - 1] ||
                                    !RunKey(parts, flat, i, verified).Equals(RunKey(parts, flat, j, verified))))
            {
                verified--;
            }

            return verified;
        }

        private List<LinkCandidatePlacement> Placements(int first, int length, MeasureContentKey key)
        {
            var placements = new List<LinkCandidatePlacement>();
            var lastEndInPart = new Dictionary<int, int>();
            foreach (var x in positionsById[flat.Ids[first]])
            {
                if (_freeRun[x] < length) continue;
                if (x != first && lcp.Get(Math.Min(x, first), Math.Max(x, first)) < length) continue;
                var part = flat.Part[x];
                if (lastEndInPart.TryGetValue(part, out var lastEnd) && x < lastEnd) continue;
                if (x != first && !RunKey(parts, flat, x, length).Equals(key)) continue;
                placements.Add(new LinkCandidatePlacement(part, flat.Measure[x]));
                lastEndInPart[part] = x + length;
            }

            return placements;
        }
    }

    /// <summary>A, B, … Z, AA, AB, … — the names groups are shown by.</summary>
    public static string Letter(int index)
    {
        var name = "";
        for (var i = index; i >= 0; i = (i / 26) - 1)
            name = (char)('A' + (i % 26)) + name;
        return name;
    }

    private static MeasureContentKey RunKey(IReadOnlyList<MeasureContentIndex> parts, Flattened flat, int x, int length) =>
        parts[flat.Part[x]].KeyFor(flat.Measure[x], length);

    private static PairRuns LongestCommonRuns(int[] ids, int n)
    {
        // Walk from the end so (i+1, j+1) is known when (i, j) is asked for.
        var runs = new PairRuns();
        for (var i = n - 1; i >= 0; i--)
        {
            if (ids[i] < 0) continue;
            for (var j = n - 1; j > i; j--)
            {
                if (ids[j] != ids[i]) continue;
                runs.Set(i, j, 1 + (j + 1 < n ? runs.Get(i + 1, j + 1) : 0));
            }
        }

        return runs;
    }

    private static Flattened Flatten(IReadOnlyList<MeasureContentIndex> parts)
    {
        var total = parts.Sum(p => p.Measures.Count) + Math.Max(0, parts.Count - 1);
        var flat = new Flattened(new int[total], new bool[total], new int[total], new int[total], new int[parts.Count]);
        var keyIds = new Dictionary<MeasureContentKey, int>();
        var x = 0;
        for (var p = 0; p < parts.Count; p++)
        {
            if (p > 0)
            {
                flat.Ids[x] = -1 - p;
                flat.Part[x] = -1;
                x++;
            }

            flat.Offset[p] = x;
            for (var m = 0; m < parts[p].Measures.Count; m++, x++)
            {
                var key = parts[p].KeyFor(m);
                if (!keyIds.TryGetValue(key, out var id)) keyIds[key] = id = keyIds.Count;
                flat.Ids[x] = id;
                flat.Empty[x] = key.IsEmpty;
                flat.Part[x] = p;
                flat.Measure[x] = m;
            }
        }

        return flat;
    }

    private sealed record Flattened(int[] Ids, bool[] Empty, int[] Part, int[] Measure, int[] Offset);

    /// <summary>How many measures stay equal from (i, j) on; sparse, because only equal starts are stored.</summary>
    private sealed class PairRuns
    {
        private readonly Dictionary<long, int> _runs = [];

        public int Get(int i, int j) => _runs.TryGetValue(((long)i << 32) | (uint)j, out var run) ? run : 0;

        public void Set(int i, int j, int run) => _runs[((long)i << 32) | (uint)j] = run;
    }
}
