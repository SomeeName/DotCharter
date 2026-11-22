using DotCharter.Core.Linking;

namespace DotCharter.Tests.Services.Parsing.GuitarPro;

internal sealed record GpAutoLinkScorecard(
    string Placements, int Groups, int ShortGroups, double MeanLength, int LinkedBars, int EditsSaved,
    int TwinnedSoundingBars, int LinkedTwins, int CrossedEdges, int ShapeCountDelta, bool NotesEqual,
    int RecalledPairs, int RepeatPairs, int MarkerAlignedRuns)
{
    public static GpAutoLinkScorecard Measure(ChartProject project, Arrangement part, ChartLevel before,
        IReadOnlyList<int>? source, IReadOnlySet<int>? markers = null)
    {
        var level = part.Levels.MaxBy(l => l.DifficultyIndex)!;
        var index = new MeasureContentIndex(part, before, project.Beats,
            MeasureContentOptions.Detection with { IncludeAnchors = false, IncludeHandShapes = false });
        var groups = Runs(project, part, index.Measures);
        var covered = groups.SelectMany(g => g.Starts.SelectMany(s => Enumerable.Range(s, g.Length))).ToHashSet();
        var twins = Enumerable.Range(0, index.Measures.Count).Where(i => !index.KeyFor(i).IsEmpty)
            .GroupBy(i => index.KeyFor(i).Canonical).Where(g => g.Count() >= 2).SelectMany(g => g).ToHashSet();
        var (recalled, pairs) = Recall(source, groups);
        var aligned = groups.Sum(g => g.Starts.Count(s => markers?.Contains(s) == true && markers.Contains(s + g.Length)));
        return new GpAutoLinkScorecard(
            string.Join(";", groups.Select(g => $"{g.Length}:{string.Join(",", g.Starts)}")), groups.Count,
            groups.Count(g => g.Length < 4), groups.Count == 0 ? 0 : groups.Average(g => g.Length),
            covered.Count, groups.Sum(g => g.Length * (g.Starts.Count - 1)), twins.Count, covered.Count(twins.Contains),
            CountCrossedEdges(before, groups, index.Measures), level.HandShapes.Count - before.HandShapes.Count,
            DeepCompare.Diff("Notes", before.Notes, level.Notes, new HashSet<string>()).Count == 0 &&
            DeepCompare.Diff("Chords", before.Chords, level.Chords, new HashSet<string>()).Count == 0,
            recalled, pairs, aligned);
    }

    private static List<Run> Runs(ChartProject project, Arrangement part, IReadOnlyList<MeasureSpan> measures)
    {
        var groups = new List<Run>();
        foreach (var group in project.LinkGroups)
        {
            var starts = new List<int>();
            var length = 0;
            foreach (var placement in group.Placements.Where(p => ReferenceEquals(p.Arrangement, part)))
            {
                if (LinkRuns.TryResolve(measures, placement, out var first, out length)) starts.Add(first);
            }

            if (starts.Count > 0) groups.Add(new Run(length, starts));
        }

        return groups;
    }

    private static int CountCrossedEdges(ChartLevel level, List<Run> groups, IReadOnlyList<MeasureSpan> measures) =>
        groups.SelectMany(g => g.Starts.SelectMany(s => new[] { measures[s].Start, measures[s + g.Length - 1].End }))
            .Count(edge => level.HandShapes.Any(h => h.Position < edge && h.EndPosition > edge) ||
                           level.Notes.Any(n => n.Position < edge && n.EndPosition > edge) ||
                           level.Chords.Any(c => c.Position < edge && c.ChordNotes.Any(n => c.Position + n.Sustain > edge)));

    private static (int Recalled, int Pairs) Recall(IReadOnlyList<int>? source, List<Run> groups)
    {
        if (source == null) return (0, 0);
        var pairs = source.Where(s => s >= 0).GroupBy(s => s).Sum(g => Pairs(g.Count()));
        var recalled = groups.Sum(g => Enumerable.Range(0, g.Length).Sum(offset =>
            g.Starts.Where(s => s + offset < source.Count).Select(s => source[s + offset]).Where(s => s >= 0)
                .GroupBy(s => s).Sum(s => Pairs(s.Count()))));
        return (recalled, pairs);
    }

    private static int Pairs(int copies) => copies * (copies - 1) / 2;

    private sealed record Run(int Length, List<int> Starts);
}
