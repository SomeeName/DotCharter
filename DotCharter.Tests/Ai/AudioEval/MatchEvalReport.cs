using System.Globalization;
using System.Text;

namespace DotCharter.Tests.Ai.AudioEval;

/// <summary>
///     The head-to-head half of the audio eval's report: for every song, how the
///     matcher's best candidate did against the song's own Rocksmith tone.
///     <para>
///         <b>The aggregate tables cannot answer the owner's question.</b> "The
///         matcher should be as close as the authored rig, or closer, on MOST songs"
///         is a per-row comparison, and a mean hides it in both directions — one
///         catastrophic row drags a mean that 20 wins produced, and one runaway win
///         flatters a mean that 20 losses produced. So the tally is counted per row,
///         and the rows are printed.
///     </para>
///     <para>
///         Pure: no audio, no engine, no clock. That is what lets it run in the
///         DEFAULT gate while the eval that feeds it stays opt-in — the formatting and
///         the arithmetic are exactly the parts that would otherwise only ever be
///         exercised by a 25-minute run nobody repeats.
///     </para>
/// </summary>
public static class MatchEvalReport
{
    /// <summary>
    ///     One song's head-to-head. Distances are <c>ToneSimilarity</c> over the SAME
    ///     reference DI for every candidate; <see cref="double.NaN" /> means the
    ///     candidate did not score (a render that threw, a matcher that was given no
    ///     budget), and a row with a NaN on either side is counted as
    ///     <see cref="HeadToHead.Unscored" /> rather than as a win or a loss.
    /// </summary>
    /// <param name="Song">Artist — title, for the row label.</param>
    /// <param name="Tone">The elected tone's name.</param>
    /// <param name="Span">The scored span, already formatted.</param>
    /// <param name="SpanSource">Where the span came from (<c>charted:…</c>,
    /// <c>tonebase:…</c>, <c>head-of-song</c>, <c>naive:…</c>).</param>
    /// <param name="RsTruth">The ceiling: the converted Rocksmith tone's distance.</param>
    /// <param name="MatcherTop1">The matcher's first candidate, re-scored.</param>
    /// <param name="MatcherBestN">The best of every candidate the matcher returned,
    /// re-scored. Never worse than <paramref name="MatcherTop1" />: the matcher's own
    /// ranking uses a loss the report does not.</param>
    /// <param name="Floor">The target's self-distance floor, or NaN.</param>
    /// <param name="Renders">How many renders the match actually spent.</param>
    /// <param name="Seconds">Wall time for the match, in seconds.</param>
    /// <param name="Origin">The winning candidate's origin (<c>retrieval</c>,
    /// <c>retrieval+cmaes</c>, …) — the one column that says whether refinement ran
    /// at all.</param>
    /// <param name="MatchedRig">The module ids the matcher chose, in chain order, and
    /// the truth's beside them. <b>Without this column a loss cannot be diagnosed</b>
    /// — "retrieval found the wrong family" and "retrieval found the right family and
    /// refinement could not tune it" are the same number and completely different
    /// bugs.</param>
    /// <param name="Blocks">The feature blocks <paramref name="MatcherTop1" />'s
    /// remaining distance is mostly in, largest first — which says whether the search
    /// missed the FAMILY or missed the tuning.</param>
    public sealed record MatchRow(
        string Song,
        string Tone,
        string Span,
        string SpanSource,
        double RsTruth,
        double MatcherTop1,
        double MatcherBestN,
        double Floor,
        int Renders,
        double Seconds,
        string Origin,
        string MatchedRig = "",
        string Blocks = "");

    /// <summary>The tally over a set of rows.</summary>
    /// <param name="Wins">Rows where the matcher's top-1 is strictly closer than the
    /// authored rig.</param>
    /// <param name="Losses">Rows where it is strictly further.</param>
    /// <param name="Unscored">Rows where either side has no finite distance.</param>
    public sealed record HeadToHead(int Wins, int Losses, int Unscored)
    {
        /// <summary>Rows that produced a verdict.</summary>
        public int Scored => Wins + Losses;

        /// <summary>Rows considered, verdict or not.</summary>
        public int Total => Wins + Losses + Unscored;

        /// <summary>"matcher beats rs-truth on X of Y".</summary>
        public override string ToString() => string.Create(
            CultureInfo.InvariantCulture,
            $"matcher beats rs-truth on {Wins} of {Scored}" +
            $"{(Unscored > 0 ? $" ({Unscored} unscored)" : string.Empty)}");
    }

    /// <summary>
    ///     Counts wins and losses on <see cref="MatchRow.MatcherTop1" /> against
    ///     <see cref="MatchRow.RsTruth" />.
    ///     <para>
    ///         <b>A tie is a LOSS, deliberately.</b> The bar is "as close or closer",
    ///         but two independent renders never produce bit-identical distances — an
    ///         exact tie in practice means both sides are the same rig, or that one of
    ///         them is a constant the arithmetic produced by accident. Counting it as a
    ///         win would be the only way a broken comparison could read as a good
    ///         result.
    ///     </para>
    /// </summary>
    public static HeadToHead Tally(IEnumerable<MatchRow> rows)
    {
        ArgumentNullException.ThrowIfNull(rows);
        int wins = 0, losses = 0, unscored = 0;
        foreach (var row in rows)
        {
            if (!double.IsFinite(row.RsTruth) || !double.IsFinite(row.MatcherTop1)) unscored++;
            else if (row.MatcherTop1 < row.RsTruth) wins++;
            else losses++;
        }

        return new HeadToHead(wins, losses, unscored);
    }

    /// <summary>The per-song head-to-head table, in the report's markdown.</summary>
    public static string RenderTable(IReadOnlyList<MatchRow> rows)
    {
        ArgumentNullException.ThrowIfNull(rows);
        var sb = new StringBuilder();
        sb.AppendLine("| song | tone | span | rs-truth | matcher-top1 | matcher-bestN | floor | verdict | renders | s | origin | rig (matched / truth) |");
        sb.AppendLine("|---|---|---|---:|---:|---:|---:|:--:|---:|---:|---|---|");
        foreach (var row in rows)
        {
            var verdict = !double.IsFinite(row.RsTruth) || !double.IsFinite(row.MatcherTop1)
                ? "—"
                : row.MatcherTop1 < row.RsTruth
                    ? "**win**"
                    : "loss";
            sb.AppendLine(CultureInfo.InvariantCulture,
                $"| {row.Song} | {row.Tone} | {row.Span} ({row.SpanSource}) | {Fmt(row.RsTruth)} | " +
                $"{Fmt(row.MatcherTop1)} | {Fmt(row.MatcherBestN)} | {Fmt(row.Floor)} | {verdict} | " +
                $"{row.Renders} | {row.Seconds:F1} | {row.Origin} | {row.MatchedRig} |");
        }

        return sb.ToString();
    }

    /// <summary>
    ///     Which feature BLOCKS the matcher's remaining distance is in, per row —
    ///     the difference between "the search found the wrong family" (the distortion
    ///     and dynamics blocks dominate) and "it found the family and could not tune
    ///     it" (one narrow spectral block does).
    /// </summary>
    public static string RenderBlocks(IReadOnlyList<MatchRow> rows)
    {
        ArgumentNullException.ThrowIfNull(rows);
        var sb = new StringBuilder();
        sb.AppendLine("| song | matcher-top1 block contributions (largest first) |");
        sb.AppendLine("|---|---|");
        foreach (var row in rows.Where(r => r.Blocks.Length > 0))
            sb.AppendLine(CultureInfo.InvariantCulture, $"| {row.Song} | {row.Blocks} |");

        return sb.ToString();
    }

    /// <summary>The N rows the matcher lost by the most — the diagnosis list.</summary>
    public static IReadOnlyList<MatchRow> Worst(IReadOnlyList<MatchRow> rows, int count)
    {
        ArgumentNullException.ThrowIfNull(rows);
        return rows
            .Where(r => double.IsFinite(r.RsTruth) && double.IsFinite(r.MatcherTop1))
            .OrderByDescending(r => r.MatcherTop1 - r.RsTruth)
            .Take(Math.Max(0, count))
            .ToList();
    }

    private static string Fmt(double value) =>
        double.IsFinite(value) ? value.ToString("F4", CultureInfo.InvariantCulture) : "—";
}
