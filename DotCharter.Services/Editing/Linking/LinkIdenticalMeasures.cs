using DotCharter.Core.Linking;
using DotCharter.Services.State;

namespace DotCharter.Services.Editing.Linking;

/// <summary>What <see cref="LinkIdenticalMeasures.Plan" /> found for a selection.</summary>
/// <param name="Refusal">Why nothing can be linked, or <see cref="LinkCreateRefusal.None" />.</param>
/// <param name="Action">The undo step that creates the group, when there is one.</param>
/// <param name="Runs">Every run the group would link, the selection's first.</param>
/// <param name="Name">The group's name (the section the selection starts in).</param>
public sealed record LinkIdenticalPlan(LinkCreateRefusal Refusal, IChartAction? Action, IReadOnlyList<LinkRunRequest> Runs, string Name);

/// <summary>
///     "Link identical measures": the selection (its first and last sound, both inclusive), widened
///     to whole measures, is linked with every
///     other run of the same measures that already plays the same — in the same arrangement and in
///     every arrangement that may share with it — that no group holds yet. Runs are taken left to
///     right without overlapping each other; <see cref="LinkGroupCreator" /> checks the rest.
/// </summary>
public static class LinkIdenticalMeasures
{
    public static LinkIdenticalPlan Plan(ChartProject project, Arrangement arrangement, FractionalPosition first,
        FractionalPosition last, MeasureContentOptions rule)
    {
        ArgumentNullException.ThrowIfNull(project);
        ArgumentNullException.ThrowIfNull(arrangement);
        ArgumentNullException.ThrowIfNull(rule);
        var measures = MeasureGrid.Build(project.Beats);
        if (!TrySpan(measures, first, last, out var firstMeasure, out var count))
            return new LinkIdenticalPlan(LinkCreateRefusal.NotWholeMeasures, null, [], "");

        var source = MeasureContentIndex.ForHardestLevel(arrangement, project.Beats, rule);
        var wanted = source.KeyFor(firstMeasure, count);
        var name = SectionAt(arrangement, measures[firstMeasure].Start);
        var selection = new LinkRunRequest(arrangement, measures[firstMeasure].Start, measures[firstMeasure + count - 1].End);
        if (wanted.IsEmpty) return new LinkIdenticalPlan(LinkCreateRefusal.Empty, null, [selection], name);

        var geometry = Geometry(measures, firstMeasure, count);
        var runs = new List<LinkRunRequest> { selection };
        var taken = project.LinkGroups.SelectMany(g => g.Placements).ToList();
        foreach (var other in project.Arrangements)
        {
            if (!ReferenceEquals(other, arrangement) && !LinkCompatibility.CanShare(arrangement, other)) continue;
            var index = ReferenceEquals(other, arrangement) ? source : MeasureContentIndex.ForHardestLevel(other, project.Beats, rule);
            for (var m = 0; m + count <= measures.Count; m++)
            {
                var run = new LinkRunRequest(other, measures[m].Start, measures[m + count - 1].End);
                if (Overlaps(runs, run) || Overlaps(taken, run)) continue;
                if (Geometry(measures, m, count) != geometry || !index.KeyFor(m, count).Equals(wanted)) continue;
                runs.Add(run);
                m += count - 1;
            }
        }

        var refusal = LinkGroupCreator.TryBuild(project, runs, name, rule, out var action);
        return new LinkIdenticalPlan(refusal, action, runs, name);
    }

    /// <summary>The whole measures from the one holding <paramref name="from" /> to the one holding <paramref name="to" />.</summary>
    private static bool TrySpan(IReadOnlyList<MeasureSpan> measures, FractionalPosition from, FractionalPosition to,
        out int first, out int count)
    {
        first = -1;
        var last = -1;
        for (var m = 0; m < measures.Count; m++)
        {
            if (measures[m].End > from && first < 0) first = m;
            if (measures[m].Start <= to) last = m;
        }

        count = last - first + 1;
        return first >= 0 && count >= 1;
    }

    private static string Geometry(IReadOnlyList<MeasureSpan> measures, int first, int count) =>
        string.Join(",", Enumerable.Range(first, count).Select(i => $"{measures[i].BeatsInMeasure}/{measures[i].NoteDenominator}x{measures[i].BeatCount}"));

    private static bool Overlaps(IEnumerable<LinkRunRequest> runs, LinkRunRequest run) =>
        runs.Any(r => ReferenceEquals(r.Arrangement, run.Arrangement) && r.Start < run.End && run.Start < r.End);

    private static bool Overlaps(IEnumerable<LinkPlacement> placements, LinkRunRequest run) =>
        placements.Any(p => ReferenceEquals(p.Arrangement, run.Arrangement) && p.Position < run.End && run.Start < p.EndPosition);

    /// <summary>The section in effect at <paramref name="at" />, or "".</summary>
    private static string SectionAt(Arrangement arrangement, FractionalPosition at) =>
        arrangement.EventPoints
            .Where(e => !string.IsNullOrEmpty(e.Section) && e.Position <= at)
            .MaxBy(e => e.Position)?.Section ?? "";
}
