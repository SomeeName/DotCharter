namespace DotCharter.UI.ViewModels.CommandPalette;

/// <summary>
///     The outcome of matching a query against one candidate string.
///     <see cref="IsMatch" /> is false when the query is not a (case-insensitive)
///     subsequence of the candidate; otherwise <see cref="Score" /> ranks the
///     quality of the match (higher = better) and <see cref="MatchedIndices" />
///     lists the candidate character positions that matched, in order.
/// </summary>
public readonly struct FuzzyMatch
{
    public FuzzyMatch(bool isMatch, int score, int[] matchedIndices)
    {
        IsMatch = isMatch;
        Score = score;
        MatchedIndices = matchedIndices;
    }

    public bool IsMatch { get; }
    public int Score { get; }
    public int[] MatchedIndices { get; }

    public static FuzzyMatch NoMatch { get; } = new(false, int.MinValue, []);
}

/// <summary>
///     Pure, deterministic fuzzy matcher for the command palette. A query
///     matches when its characters appear in order (a subsequence) inside the
///     candidate, ignoring case. Matches are scored so that word-boundary hits,
///     consecutive runs, a leading-prefix and full equality float to the top —
///     the standard "type the initials of a command" behaviour. No allocation
///     beyond the returned index array; safe to call per keystroke over the
///     whole catalogue.
/// </summary>
public static class CommandFuzzyMatcher
{
    // Scoring weights. Tuned so that (a) an exact prefix beats a scattered
    // subsequence and (b) initials of words ("op" -> "Open Project") rank well.
    private const int MatchBaseBonus = 2;
    private const int FirstCharBonus = 20;
    private const int WordBoundaryBonus = 12;
    private const int ConsecutiveBonus = 8;
    private const int PrefixBonus = 24;
    private const int ExactBonus = 40;

    /// <summary>
    ///     Matches <paramref name="query" /> against <paramref name="candidate" />.
    ///     An empty/whitespace query matches everything with a neutral score of 0
    ///     (callers keep the source order in that case).
    /// </summary>
    public static FuzzyMatch Match(string? query, string? candidate)
    {
        candidate ??= string.Empty;
        var q = (query ?? string.Empty).Trim();

        if (q.Length == 0)
            return new FuzzyMatch(true, 0, []);
        if (candidate.Length == 0)
            return FuzzyMatch.NoMatch;

        // Greedy left-to-right subsequence scan (deterministic).
        var indices = new int[q.Length];
        var qi = 0;
        for (var ci = 0; ci < candidate.Length && qi < q.Length; ci++)
        {
            if (char.ToLowerInvariant(candidate[ci]) == char.ToLowerInvariant(q[qi]))
                indices[qi++] = ci;
        }

        if (qi < q.Length)
            return FuzzyMatch.NoMatch;

        return new FuzzyMatch(true, Score(candidate, q, indices), indices);
    }

    /// <summary>
    ///     Filters + ranks <paramref name="items" /> by how well the query matches
    ///     each item's title (and, as a weaker fallback, an optional secondary
    ///     field such as the category). Non-matches are dropped; ties keep the
    ///     original order (stable), so an empty query returns the source list
    ///     unchanged.
    /// </summary>
    public static List<T> Rank<T>(
        string? query,
        IEnumerable<T> items,
        Func<T, string> titleSelector,
        Func<T, string?>? secondarySelector = null)
    {
        var scored = new List<(T Item, int Score, int Order)>();
        var order = 0;
        foreach (var item in items)
        {
            var primary = Match(query, titleSelector(item));
            var best = primary.IsMatch ? primary.Score : int.MinValue;

            if (!primary.IsMatch && secondarySelector != null)
            {
                var secondary = Match(query, secondarySelector(item));
                // A category-only hit is real but ranks below any title hit.
                if (secondary.IsMatch) best = secondary.Score - 100;
            }

            if (best != int.MinValue) scored.Add((item, best, order));
            order++;
        }

        scored.Sort((a, b) =>
        {
            var byScore = b.Score.CompareTo(a.Score);
            return byScore != 0 ? byScore : a.Order.CompareTo(b.Order);
        });

        var result = new List<T>(scored.Count);
        foreach (var s in scored) result.Add(s.Item);
        return result;
    }

    private static int Score(string candidate, string query, int[] indices)
    {
        var score = 0;
        for (var k = 0; k < indices.Length; k++)
        {
            var pos = indices[k];
            score += MatchBaseBonus;

            if (pos == 0)
            {
                score += FirstCharBonus;
            }
            else
            {
                var prev = candidate[pos - 1];
                var boundary = !char.IsLetterOrDigit(prev)
                               || (char.IsLower(prev) && char.IsUpper(candidate[pos]));
                if (boundary) score += WordBoundaryBonus;
            }

            if (k > 0 && indices[k] == indices[k - 1] + 1) score += ConsecutiveBonus;
        }

        // The closer the first match sits to the start, the better.
        score -= indices[0];
        // Gentle tie-break toward tighter (shorter) candidates.
        score -= candidate.Length / 16;

        if (candidate.StartsWith(query, StringComparison.OrdinalIgnoreCase))
        {
            score += PrefixBonus;
            if (candidate.Length == query.Length) score += ExactBonus;
        }

        return score;
    }
}
