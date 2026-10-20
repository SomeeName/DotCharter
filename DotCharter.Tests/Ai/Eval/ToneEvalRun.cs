namespace DotCharter.Tests.Ai.Eval;

/// <summary>Which of an entry's two requests produced a row.</summary>
public enum ToneEvalPromptKind
{
    /// <summary><see cref="ToneEvalEntry.Prompt" /> — tone name, title and artist.</summary>
    Full = 0,

    /// <summary><see cref="ToneEvalEntry.ShortPrompt" /> — title and coarse role only.</summary>
    Short = 1
}

/// <summary>One graded request.</summary>
/// <param name="Id">The entry's <see cref="ToneEvalEntry.Id" />.</param>
/// <param name="SongTitle">Song title.</param>
/// <param name="Artist">Song artist.</param>
/// <param name="ToneName">The ground-truth tone's name.</param>
/// <param name="PromptKind">Which request shape was sent.</param>
/// <param name="Prompt">The request text, verbatim.</param>
/// <param name="Total">Weighted score, 0..100. 0 when <paramref name="Error" /> is set.</param>
/// <param name="Ceiling">
///     The best <paramref name="Total" /> a perfect model could have earned on this
///     row, after the truth has been through the draft schema and
///     <c>ToneDraftValidator</c>. See <see cref="ToneEvalCeiling" /> — a score is
///     unreadable without it.
/// </param>
/// <param name="Family">Family component, 0..1.</param>
/// <param name="Module">Module component, 0..1.</param>
/// <param name="Knob">Knob component, 0..1.</param>
/// <param name="Order">Order component, 0..1.</param>
/// <param name="ComparedParams">How many params the knob component averaged over.</param>
/// <param name="DesignedModules">The model's chain, in order, after canonicalization.</param>
/// <param name="TruthModules">The converted RS chain, in order, after canonicalization.</param>
/// <param name="Confidence">The model's own self-reported confidence.</param>
/// <param name="Warnings">What the design service had to clamp or drop.</param>
/// <param name="InputTokens">Prompt tokens billed.</param>
/// <param name="OutputTokens">Completion tokens billed.</param>
/// <param name="CostUsd">Billed cost of this row at the configured list price.</param>
/// <param name="ElapsedMs">Wall time of the call. Reported, never asserted.</param>
/// <param name="Error">The fault, when the call threw. Null on success.</param>
public sealed record ToneEvalRow(
    string Id,
    string SongTitle,
    string Artist,
    string ToneName,
    ToneEvalPromptKind PromptKind,
    string Prompt,
    double Total,
    double Ceiling,
    double Family,
    double Module,
    double Knob,
    double Order,
    int ComparedParams,
    IReadOnlyList<string> DesignedModules,
    IReadOnlyList<string> TruthModules,
    double Confidence,
    IReadOnlyList<string> Warnings,
    int InputTokens,
    int OutputTokens,
    double CostUsd,
    double ElapsedMs,
    string? Error);

/// <summary>The whole graded run: every row plus the aggregate the owner reads.</summary>
/// <param name="TimestampUtc">Round-trip UTC stamp; also the artifact folder name.</param>
/// <param name="Label">Free text naming what was evaluated (model id, or "offline fake").</param>
/// <param name="Provider">The provider kind, as configured.</param>
/// <param name="Songs">Distinct songs in the run.</param>
/// <param name="Mean">Mean <see cref="ToneEvalRow.Total" /> over successful rows.</param>
/// <param name="MeanCeiling">Mean <see cref="ToneEvalRow.Ceiling" /> over ALL rows —
/// it is a property of the corpus and the pipeline, so a failed call still has one.</param>
/// <param name="Median">Median total.</param>
/// <param name="P10">10th percentile — the tail is what a user remembers.</param>
/// <param name="MeanFamily">Mean family component, for reading WHERE the score went.</param>
/// <param name="MeanModule">Mean module component.</param>
/// <param name="MeanKnob">Mean knob component.</param>
/// <param name="MeanOrder">Mean order component.</param>
/// <param name="Failures">Rows whose call threw.</param>
/// <param name="InputTokens">Total prompt tokens.</param>
/// <param name="OutputTokens">Total completion tokens.</param>
/// <param name="CostUsd">Total billed cost at the configured list price.</param>
/// <param name="Results">Every row, in run order.</param>
/// <param name="ArtifactDirectory">Where results.json and report.md were written.</param>
/// <param name="Anchored">Whether grounding stage 2 was left ON for the scored
/// song. True means every score here is an UPPER BOUND — the model was handed the
/// chain the scorer grades against — and the report header says so in those words.
/// See <see cref="ToneEvalEnvironment.AnchorVariable" />.</param>
public sealed record ToneEvalRun(
    string TimestampUtc,
    string Label,
    string Provider,
    int Songs,
    double Mean,
    double MeanCeiling,
    double Median,
    double P10,
    double MeanFamily,
    double MeanModule,
    double MeanKnob,
    double MeanOrder,
    int Failures,
    int InputTokens,
    int OutputTokens,
    double CostUsd,
    IReadOnlyList<ToneEvalRow> Results,
    string ArtifactDirectory,
    bool Anchored = false);
