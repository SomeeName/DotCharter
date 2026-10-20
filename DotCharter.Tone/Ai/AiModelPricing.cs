using System.Globalization;

namespace DotCharter.Tone.Ai;

/// <summary>
///     What one design cost, estimated from the BILLED token counts on
///     <see cref="AiUsage" /> and a small list price table.
///
///     <para>
///         <b>An unknown model id has no price, and that is an ANSWER.</b> The
///         feature's whole other half is a local OpenAI-compatible server — LM
///         Studio, Ollama — where the marginal cost of a request is zero, so
///         guessing a per-token price for an id this table does not know would put
///         a dollar figure on a free request. <see cref="TryEstimateUsd" /> answers
///         false and the UI prints "local" instead.
///     </para>
///     <para>
///         <b>Matching is on the model FAMILY inside the id, not on equality.</b>
///         The same family ships as <c>claude-opus-5</c>, <c>claude-opus-5-latest</c>
///         and a dated <c>claude-opus-5-20260401</c>, and a table keyed by the exact
///         string would silently fall through to "local" — i.e. free — for two of
///         the three. Longest family token first, so a row is never shadowed by a
///         shorter one it contains: <c>claude-opus-5-5</c> must reach
///         <c>opus-5-5</c>, not the <c>opus-5</c> row that is also inside it.
///     </para>
///     <para>
///         <b>These are LIST prices and they go stale.</b> They are printed with a
///         <c>~</c> and are an estimate of input+output only: prompt caching (the
///         Claude provider marks the system block ephemeral) makes the real bill
///         LOWER, never higher, so this over-states rather than surprises.
///         <c>AiModelPricingTests</c> pins every row by value; re-check them
///         against the price list when a model id is added.
///     </para>
/// </summary>
public static class AiModelPricing
{
    /// <summary>One row: a family token that must appear in the model id, and its
    /// USD-per-million-token input and output list prices.</summary>
    /// <param name="Family">The lower-case family token, e.g. <c>opus-5</c>.</param>
    /// <param name="InputUsdPerMTok">Input list price, USD per million tokens.</param>
    /// <param name="OutputUsdPerMTok">Output list price, USD per million tokens.</param>
    public sealed record Row(string Family, double InputUsdPerMTok, double OutputUsdPerMTok);

    /// <summary>The price table, longest family token first (list prices checked
    /// against Anthropic's models overview and OpenAI's developer docs).</summary>
    public static IReadOnlyList<Row> Table { get; } =
    [
        new("gpt-6-astra", 10.0, 50.0),
        new("sonnet-5-5", 2.0, 10.0),
        new("gpt-6-luna", 0.1, 0.5),
        new("haiku-4-5", 1.0, 5.0),
        new("haiku-5-5", 0.1, 0.5),
        new("gpt-6-sol", 2.0, 10.0),
        new("opus-5-5", 4.0, 20.0),
        new("sonnet-5", 2.0, 10.0),
        new("opus-5", 5.0, 25.0)
    ];

    /// <summary>The row for <paramref name="modelId" />, or null when nothing in the
    /// table names it — a local model, a gateway alias, an id from a later
    /// build.</summary>
    public static Row? Find(string? modelId)
    {
        if (string.IsNullOrWhiteSpace(modelId)) return null;
        var id = modelId.ToLowerInvariant();
        foreach (var row in Table)
        {
            if (id.Contains(row.Family, StringComparison.Ordinal))
                return row;
        }

        return null;
    }

    /// <summary>Estimates the USD cost of <paramref name="usage" />. False when the
    /// model id is not in the table — the caller must then say "local", never
    /// "$0.00", which reads as a measured zero.</summary>
    public static bool TryEstimateUsd(AiUsage? usage, out double usd)
    {
        usd = 0;
        if (usage is null) return false;
        var row = Find(usage.ModelId);
        if (row is null) return false;

        usd = Math.Max(0, usage.InputTokens) / 1e6 * row.InputUsdPerMTok +
              Math.Max(0, usage.OutputTokens) / 1e6 * row.OutputUsdPerMTok;
        return true;
    }

    /// <summary>
    ///     A cost for a status line: <c>~$0.04</c>, or null when the model is not
    ///     priced. Two decimals down to a cent and FOUR below it — a design that
    ///     costs a third of a cent is a real and reassuring number, and rounding it
    ///     to <c>~$0.00</c> would read as a bug.
    /// </summary>
    public static string? FormatUsd(AiUsage? usage)
    {
        if (!TryEstimateUsd(usage, out var usd)) return null;
        return usd >= 0.01d
            ? usd.ToString("0.00", CultureInfo.InvariantCulture)
            : usd.ToString("0.0000", CultureInfo.InvariantCulture);
    }
}
