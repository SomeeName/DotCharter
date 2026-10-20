namespace DotCharter.Tests.Ai.Eval;

/// <summary>
///     How one eval run behaves. Every default is the OFFLINE default — the live
///     runner overrides them from the environment (see
///     <see cref="ToneEvalEnvironment" />), so nothing here can start a network
///     call by accident.
/// </summary>
public sealed record ToneEvalOptions
{
    /// <summary>Anthropic list price for Opus 5 input, USD per million tokens
    /// (2026-09). The report prints the price it used, so a stale constant is
    /// visible rather than silently wrong.</summary>
    public const double OpusInputUsdPerMTok = 5.0;

    /// <summary>Anthropic list price for Opus 5 output, USD per million tokens.</summary>
    public const double OpusOutputUsdPerMTok = 25.0;

    /// <summary>Hard ceiling on songs per run. The live gate defaults this to 20.</summary>
    public int MaxSongs { get; init; } = 20;

    /// <summary>Also send <see cref="ToneEvalEntry.ShortPrompt" /> for each entry,
    /// doubling the call count and the cost. Off by default.</summary>
    public bool IncludeShortPrompt { get; init; }

    /// <summary>Free text naming what was evaluated.</summary>
    public string Label { get; init; } = "offline fake";

    /// <summary>The provider kind, for the report header.</summary>
    public string Provider { get; init; } = "none";

    /// <summary>Whether grounding stage 2 was LEFT ON for the scored song. The
    /// report header says so and labels the number accordingly; see
    /// <see cref="ToneEvalEnvironment.AnchorVariable" /> for why the default run
    /// switches it off.</summary>
    public bool Anchored { get; init; }

    /// <summary>Input price, USD per million tokens.</summary>
    public double InputUsdPerMTok { get; init; } = OpusInputUsdPerMTok;

    /// <summary>Output price, USD per million tokens.</summary>
    public double OutputUsdPerMTok { get; init; } = OpusOutputUsdPerMTok;

    /// <summary>Where <c>artifacts/ai-eval/&lt;timestamp&gt;/</c> is rooted. Null
    /// resolves the repository root (the nearest ancestor holding
    /// <c>DotCharter.sln</c>), falling back to the test binaries' directory.</summary>
    public string? ArtifactRoot { get; init; }

    /// <summary>
    ///     What one request is ASSUMED to cost before it is made, for the estimate
    ///     printed ahead of the first call. Measured against the shape of the
    ///     design prompt (system prompt carrying the module catalogue + one turn).
    ///     It is an estimate and the report says so — the run reports BILLED tokens.
    /// </summary>
    public int AssumedInputTokensPerCall { get; init; } = 6_000;

    /// <summary>Assumed completion tokens per request, for the pre-flight estimate.</summary>
    public int AssumedOutputTokensPerCall { get; init; } = 900;

    /// <summary>The pre-flight cost estimate for <paramref name="calls" /> requests.</summary>
    public double EstimateUsd(int calls)
        => calls * (AssumedInputTokensPerCall / 1e6 * InputUsdPerMTok +
                    AssumedOutputTokensPerCall / 1e6 * OutputUsdPerMTok);

    /// <summary>How many requests <paramref name="entries" /> will produce.</summary>
    public int CallCount(int entries)
        => Math.Min(entries, MaxSongs) * (IncludeShortPrompt ? 2 : 1);
}
