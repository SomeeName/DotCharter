using DotCharter.Services.Export.Rocksmith;

namespace DotCharter.Services.Editing.Validation.Rules;

/// <summary>
///     Checks the dynamic-difficulty ladder phrase by phrase (community charting guides): repeats
///     of one phrase with different level counts, a playable phrase with fewer than
///     <see cref="MinLevels" /> levels and one with more than <see cref="MaxLevels" />.
///     <para>
///         It reads what the export ships, never a reader of its own: the export's phrase
///         iterations and their owning starts (<see cref="RsPhraseExportData" />), and each
///         iteration's max difficulty from <see cref="RsLevelLadder.IterationMaxima" /> (the
///         authored max raised to the highest level whose whole content inside the iteration
///         differs from the level below). An iteration's level COUNT is that max plus one. A
///         PLAYABLE iteration holds notes or chords at its own max level; an empty one
///         (COUNT, noguitar, END) has no ladder to judge. An arrangement with one level has no
///         authored ladder at all (the export generates one), so it is not checked.
///     </para>
/// </summary>
public sealed class DifficultyLevelRule : IArrangementRule
{
    private const int MinLevels = 2;
    private const int MaxLevels = 30;

    public void Validate(ArrangementValidationContext context, ICollection<ArrangementIssue> issues)
    {
        var arrangement = context.Arrangement;
        if (arrangement.Levels.Count < MinLevels) return;

        var data = RsPhraseExportData.Unresolved(arrangement, context.Beats);
        var (ordered, spans) = RsPhraseExportData.Order(data.Phrases,
            data.Iterations.Select((iteration, index) => (iteration.PhraseId, data.IterationStartMs[index])),
            context.Beats);
        if (ordered.Count == 0) return;

        var maxima = RsLevelLadder.IterationMaxima(arrangement, spans);
        var playable = Playable(arrangement, spans, maxima);
        var reported = new HashSet<(string, int)>(); // (finding, phrase id): once per phrase
        for (var i = 0; i < ordered.Count; i++)
        {
            if (!playable[i]) continue;
            var (id, startMs) = ordered[i];
            var count = maxima[i] + 1;
            if (count < MinLevels && reported.Add(("few", id)))
                issues.Add(Make("Validation_PhraseLevelsTooFew", startMs, [data.Phrases[id], count]));
            if (count > MaxLevels && reported.Add(("many", id)))
                issues.Add(Make("Validation_PhraseLevelsTooMany", startMs, [data.Phrases[id], count]));
        }

        ReportMismatches(data.Phrases, ordered, maxima, playable, issues);
    }

    /// <summary>Per export phrase: the first playable repeat whose count differs from the most
    /// common count (a tie goes to the higher count, the fuller ladder).</summary>
    private static void ReportMismatches(IReadOnlyList<string> phrases, List<(int PhraseId, int StartMs)> ordered,
        int[] maxima, bool[] playable, ICollection<ArrangementIssue> issues)
    {
        var byPhrase = new Dictionary<int, List<int>>();
        for (var i = 0; i < ordered.Count; i++)
        {
            if (!playable[i]) continue;
            if (!byPhrase.TryGetValue(ordered[i].PhraseId, out var repeats))
                byPhrase[ordered[i].PhraseId] = repeats = [];
            repeats.Add(i);
        }

        foreach (var (id, repeats) in byPhrase)
        {
            var common = repeats.GroupBy(i => maxima[i])
                .OrderByDescending(g => g.Count()).ThenByDescending(g => g.Key).First().Key;
            var odd = repeats.FindIndex(i => maxima[i] != common);
            if (odd < 0) continue;
            var at = repeats[odd];
            issues.Add(Make("Validation_PhraseLevelsMismatch", ordered[at].StartMs,
                [phrases[id], maxima[at] + 1, common + 1]));
        }
    }

    private static ArrangementIssue Make(string key, int startMs, object[] args) =>
        ArrangementIssues.Make(ArrangementIssueType.DifficultyLevelCount, key, startMs, args: args);

    /// <summary>Per iteration: notes or chords at its own max level (the highest level at or
    /// below it, as the game plays a sparse ladder).</summary>
    private static bool[] Playable(Arrangement arrangement, RsLadderSpans spans, int[] maxima)
    {
        var levels = arrangement.Levels.OrderBy(l => l.DifficultyIndex).ToList();
        var target = maxima.Select(max => levels.LastOrDefault(l => l.DifficultyIndex <= max)).ToArray();
        var playable = new bool[maxima.Length];
        foreach (var level in levels)
        {
            var positions = level.Notes.Select(n => n.Position).Concat(level.Chords.Select(c => c.Position));
            foreach (var position in positions)
            {
                var span = spans.SpanOf(position);
                if (span >= 0 && !playable[span] && ReferenceEquals(target[span], level)) playable[span] = true;
            }
        }

        return playable;
    }
}
