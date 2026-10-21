using DotCharter.Core.Linking;
using DotCharter.Services.State;

namespace DotCharter.Services.Editing.Linking;

/// <summary>
///     One group the app proposes to link: <c>MeasureCount</c> measures at each of <c>Runs</c>, named
///     after the section the first run starts in. <c>FirstMeasures</c> holds the 0-based first measure of
///     each run, in the order of <c>Runs</c>; <c>FromRepeats</c> says it was found in the score's own
///     repeat structure rather than by comparing content.
/// </summary>
public sealed record LinkSuggestion(
    string Name,
    int MeasureCount,
    IReadOnlyList<LinkRunRequest> Runs,
    IReadOnlyList<int> FirstMeasures,
    bool FromRepeats);

/// <summary>
///     Finds what can be linked in arrangements that were just imported, and links it as one undo step.
///     <para>
///         Two tiers. The reliable one is the score's own repeat structure: measures that play the same
///         WRITTEN bar (the source bars handed to <see cref="Find" />) are repeats by construction.
///         Then <see cref="LinkCandidateDetector" /> over each <see cref="LinkCompatibility" /> class of
///         hardest levels. Every run is whole measures, never empty, overlaps no other suggestion and no
///         existing placement, and every suggestion is one <see cref="LinkGroupCreator" /> would accept.
///     </para>
/// </summary>
public static class LinkSuggestionFinder
{
    public const string HistoryLabelKey = "History_LinkSuggestions";

    /// <summary>The comparison rule every app-created group uses under <paramref name="config" />.</summary>
    public static MeasureContentOptions RuleFor(AppConfig config) =>
        MeasureContentOptions.Detection with { Spacing = ChartSpacingEnforcer.SpacingGapOf(config) };

    /// <summary>
    ///     The suggestions in <paramref name="arrangements" /> (those the import added).
    ///     <paramref name="sourceBars" /> holds, per measure of the project's grid, the written bar it
    ///     plays (-1 unknown); null, or a list of another length, skips the repeat tier.
    /// </summary>
    public static IReadOnlyList<LinkSuggestion> Find(ChartProject project, IReadOnlyList<Arrangement> arrangements,
        IReadOnlyList<int>? sourceBars, MeasureContentOptions rule)
    {
        ArgumentNullException.ThrowIfNull(project);
        ArgumentNullException.ThrowIfNull(arrangements);
        ArgumentNullException.ThrowIfNull(rule);
        var measures = MeasureGrid.Build(project.Beats);
        var classes = arrangements
            .Where(a => project.Arrangements.Contains(a) && LinkCompatibility.Signature(a) != null)
            .Distinct()
            .GroupBy(a => LinkCompatibility.Signature(a)!, StringComparer.Ordinal)
            .Select(g => g.ToList())
            .ToList();
        if (measures.Count == 0 || classes.Count == 0) return [];

        var finder = new Finder(project, measures, rule);
        if (sourceBars != null && sourceBars.Count == measures.Count)
        {
            foreach (var (first, length, starts) in RepeatRuns(sourceBars))
            {
                foreach (var part in classes)
                    finder.AddBuckets(part.SelectMany(a => starts.Select(s => (a, s))), length, fromRepeats: true);
            }
        }

        foreach (var part in classes)
        {
            var indexes = part.Select(finder.Index).ToList();
            var detected = LinkCandidateDetector.Detect(indexes);
            if (detected.Truncated) continue;
            foreach (var group in detected.Groups)
                finder.AddFreeSegments([.. group.Placements.Select(p => (part[p.Part], p.FirstMeasure))], group.MeasureCount);
        }

        return finder.Suggestions;
    }

    /// <summary>
    ///     Links every suggestion as ONE undo step (a <see cref="CompositeAction" />), each group with
    ///     its own letter; null when none can be linked any more. The project is left as it was: the
    ///     history runs the step.
    /// </summary>
    public static IChartAction? LinkAll(ChartProject project, IEnumerable<LinkSuggestion> suggestions,
        MeasureContentOptions rule)
    {
        ArgumentNullException.ThrowIfNull(project);
        ArgumentNullException.ThrowIfNull(suggestions);
        var actions = new List<IChartAction>();
        try
        {
            // Each group must see the ones before it, for its letter and the overlap check.
            foreach (var suggestion in suggestions)
            {
                if (LinkGroupCreator.TryBuild(project, suggestion.Runs, suggestion.Name, rule, out var action) !=
                    LinkCreateRefusal.None)
                {
                    continue;
                }

                action!.Execute();
                actions.Add(action);
            }
        }
        finally
        {
            for (var i = actions.Count - 1; i >= 0; i--) actions[i].Undo();
        }

        return actions.Count == 0 ? null : new CompositeAction(actions) { LabelKey = HistoryLabelKey };
    }

    /// <summary>
    ///     The repeated runs of written bars, left to right: each the longest run of measures whose
    ///     written bars recur, in the same order, at every later start of its first bar, without the
    ///     occurrences overlapping each other.
    /// </summary>
    internal static List<(int First, int Length, List<int> Starts)> RepeatRuns(IReadOnlyList<int> sourceBars)
    {
        var n = sourceBars.Count;
        var covered = new bool[n];
        var runs = new List<(int, int, List<int>)>();
        for (var i = 0; i < n; i++)
        {
            if (covered[i] || sourceBars[i] < 0) continue;
            var starts = new List<int> { i };
            for (var j = i + 1; j < n; j++)
            {
                if (!covered[j] && sourceBars[j] == sourceBars[i]) starts.Add(j);
            }

            if (starts.Count < 2) continue;
            var length = 1;
            while (Extends(sourceBars, covered, starts, length)) length++;
            foreach (var start in starts)
            {
                for (var m = start; m < start + length; m++) covered[m] = true;
            }

            runs.Add((i, length, starts));
        }

        return runs;
    }

    /// <summary>Every occurrence can grow by one more measure: same written bar, still free, and short of the next occurrence.</summary>
    private static bool Extends(IReadOnlyList<int> bars, bool[] covered, List<int> starts, int length)
    {
        for (var k = 0; k < starts.Count; k++)
        {
            var at = starts[k] + length;
            if (at >= bars.Count || covered[at] || bars[at] < 0 || bars[at] != bars[starts[0] + length]) return false;
            if (k + 1 < starts.Count && at >= starts[k + 1]) return false;
        }

        return true;
    }

    /// <summary>The suggestions found so far, and which measures of which arrangement they (or existing groups) hold.</summary>
    private sealed class Finder
    {
        private readonly ChartProject _project;
        private readonly IReadOnlyList<MeasureSpan> _measures;
        private readonly MeasureContentOptions _rule;
        private readonly Dictionary<Arrangement, MeasureContentIndex> _indexes = [];
        private readonly Dictionary<Arrangement, bool[]> _taken = [];

        public Finder(ChartProject project, IReadOnlyList<MeasureSpan> measures, MeasureContentOptions rule)
        {
            _project = project;
            _measures = measures;
            _rule = rule;
            foreach (var placement in project.LinkGroups.SelectMany(g => g.Placements))
            {
                if (placement.Arrangement is { } arrangement && LinkRuns.TryResolve(measures, placement, out var first, out var count))
                    Take(arrangement, first, count);
            }
        }

        public List<LinkSuggestion> Suggestions { get; } = [];

        public MeasureContentIndex Index(Arrangement arrangement)
        {
            if (!_indexes.TryGetValue(arrangement, out var index))
                _indexes[arrangement] = index = MeasureContentIndex.ForHardestLevel(arrangement, _project.Beats, _rule);
            return index;
        }

        /// <summary>Groups the free, non-empty runs by content and time signatures; each bucket of two or more is a suggestion.</summary>
        public void AddBuckets(IEnumerable<(Arrangement Arrangement, int First)> runs, int length, bool fromRepeats)
        {
            var buckets = new Dictionary<string, List<(Arrangement Arrangement, int First)>>(StringComparer.Ordinal);
            var order = new List<string>();
            foreach (var (arrangement, first) in runs)
            {
                if (first < 0 || first + length > _measures.Count || IsTaken(arrangement, first, length)) continue;
                var key = Index(arrangement).KeyFor(first, length);
                if (key.IsEmpty) continue;
                var bucket = key.Canonical + "|" + Geometry(first, length);
                if (!buckets.TryGetValue(bucket, out var list))
                {
                    buckets[bucket] = list = [];
                    order.Add(bucket);
                }

                if (list.Any(r => ReferenceEquals(r.Arrangement, arrangement) && r.First < first + length && first < r.First + length)) continue;
                list.Add((arrangement, first));
            }

            foreach (var list in order.Select(b => buckets[b]).Where(l => l.Count >= 2))
            {
                var requests = list.Select(r => new LinkRunRequest(r.Arrangement, _measures[r.First].Start,
                    _measures[r.First + length - 1].End)).ToList();
                var name = SectionAt(list[0].Arrangement, requests[0].Start);
                if (LinkGroupCreator.TryBuild(_project, requests, name, _rule, out _) != LinkCreateRefusal.None) continue;
                Suggestions.Add(new LinkSuggestion(name, length, requests, [.. list.Select(r => r.First)], fromRepeats));
                foreach (var (arrangement, first) in list) Take(arrangement, first, length);
            }
        }

        /// <summary>
        ///     A detected run that crosses measures already taken (a repeat, an existing group) still
        ///     offers what is left: each stretch of offsets where the same two or more placements are
        ///     free, without empty measures at its edges.
        /// </summary>
        public void AddFreeSegments(List<(Arrangement Arrangement, int First)> runs, int length)
        {
            var offset = 0;
            while (offset < length)
            {
                var free = FreeAt(runs, offset);
                if (free.Count < 2)
                {
                    offset++;
                    continue;
                }

                var end = offset + 1;
                while (end < length && FreeAt(runs, end).SequenceEqual(free)) end++;
                var (lead, index) = (runs[free[0]], Index(runs[free[0]].Arrangement));
                var (start, stop) = (offset, end);
                while (start < stop && index.KeyFor(lead.First + start).IsEmpty) start++;
                while (stop > start && index.KeyFor(lead.First + stop - 1).IsEmpty) stop--;
                if (stop > start) AddBuckets(free.Select(i => (runs[i].Arrangement, runs[i].First + start)), stop - start, fromRepeats: false);
                offset = end;
            }
        }

        private List<int> FreeAt(List<(Arrangement Arrangement, int First)> runs, int offset) =>
            [.. Enumerable.Range(0, runs.Count).Where(i => !IsTaken(runs[i].Arrangement, runs[i].First + offset, 1))];

        private string Geometry(int first, int length) =>
            string.Join(",", Enumerable.Range(first, length).Select(i =>
                $"{_measures[i].BeatsInMeasure}/{_measures[i].NoteDenominator}x{_measures[i].BeatCount}"));

        private bool IsTaken(Arrangement arrangement, int first, int length) =>
            _taken.TryGetValue(arrangement, out var taken) && Enumerable.Range(first, length).Any(m => taken[m]);

        private void Take(Arrangement arrangement, int first, int length)
        {
            if (!_taken.TryGetValue(arrangement, out var taken)) _taken[arrangement] = taken = new bool[_measures.Count];
            for (var m = first; m < first + length && m < taken.Length; m++) taken[m] = true;
        }
    }

    /// <summary>The section in effect at <paramref name="at" />, or "".</summary>
    internal static string SectionAt(Arrangement arrangement, FractionalPosition at) =>
        arrangement.EventPoints
            .Where(e => !string.IsNullOrEmpty(e.Section) && e.Position <= at)
            .MaxBy(e => e.Position)?.Section ?? "";
}
