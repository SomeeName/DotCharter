using System.Diagnostics;
using System.Globalization;
using DotCharter.Localization;
using DotCharter.Services.Editing.Quality;
using DotCharter.Tone.Ai;

namespace DotCharter.Services.Ai.Chart;

/// <summary>One ask / explain question.</summary>
/// <param name="Project">A SNAPSHOT of the project the caller owns: the pass reads it, and runs the
/// chart quality report on it, off the calling thread.</param>
/// <param name="Facts">The song; its <see cref="AiChartSongFacts.Arrangement" /> is one of
/// <paramref name="Project" />'s arrangements.</param>
/// <param name="Level">The level whose sounds are listed (one of that arrangement's).</param>
/// <param name="From">First sound position of the scope, or null for the start.</param>
/// <param name="To">Last sound position of the scope, or null for the end.</param>
/// <param name="Question">The question and the conversation excerpt.</param>
public sealed record AiExplainRequest(
    ChartProject Project,
    AiChartSongFacts Facts,
    ChartLevel Level,
    FractionalPosition? From,
    FractionalPosition? To,
    AiChartInstruction Question);

/// <summary>What an ask / explain run got back.</summary>
/// <param name="Answer">The model's text, or null when the reply had none.</param>
/// <param name="ProposalsIgnored">True when the reply also carried proposals; they were not read.</param>
/// <param name="Pass">The tokens and the model, in the shape the other jobs report them.</param>
public sealed record AiExplainResult(string? Answer, bool ProposalsIgnored, AiChartPassResult Pass);

/// <summary>
/// The ask / explain job: ONE model request over the scope — the listing, the chart quality
///     report's findings in it and the question (<see cref="AiExplainPrompt" />) — and a TEXT answer.
///     It proposes, holds and writes nothing: there is no reader for anchors, chords or markers on this
///     path, so a reply that carries them is answered with its text alone.
/// </summary>
public sealed class AiExplainPass
{
    private readonly ChartQualityReporter _reporter;
    private readonly ILocalizationService _localization;
    private readonly IAppLogger? _logger;

    /// <summary>Builds the pass.</summary>
    /// <param name="reporter">The chart quality report whose findings the prompt lists.</param>
    /// <param name="localization">The findings' messages, in the checks' own words.</param>
    /// <param name="logger">Outcomes go here (never the prompt or the reply).</param>
    public AiExplainPass(ChartQualityReporter reporter, ILocalizationService localization, IAppLogger? logger = null)
    {
        _reporter = reporter ?? throw new ArgumentNullException(nameof(reporter));
        _localization = localization ?? throw new ArgumentNullException(nameof(localization));
        _logger = logger;
    }

    /// <summary>Asks the question. Throws <see cref="OperationCanceledException" /> on cancel and
    /// <see cref="ToneDesignException" /> when the provider fails.</summary>
    public async Task<AiExplainResult> RunAsync(AiExplainRequest request, IToneDesignModel model, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentNullException.ThrowIfNull(model);
        var clock = Stopwatch.StartNew();
        var beats = request.Project.Beats;
        var report = _reporter.Build(request.Project);
        var findings = AiExplainPrompt.Findings(report, request.Facts.Arrangement, request.Level, beats, request.From,
            request.To, _localization);
        var prompt = AiExplainPrompt.Build(request.Facts, request.Level, beats, request.From, request.To, findings,
            request.Question);
        ct.ThrowIfCancellationRequested();

        var reply = await model.CompleteAsync(prompt, ct).ConfigureAwait(false);
        var answered = AiExplainPrompt.TryReadAnswer(reply.Json, out var answer, out var proposed);
        _logger?.Info(AppLogCategory.Editing, string.Create(CultureInfo.InvariantCulture,
            $"AI chart question ({reply.Usage.ModelId}): {(answered ? "answered" : "no answer")}, {findings.Count} finding(s) " +
            $"in the prompt{(proposed ? ", proposals in the reply ignored" : "")}; {reply.Usage.InputTokens} in / " +
            $"{reply.Usage.OutputTokens} out tokens, {clock.Elapsed.TotalSeconds:F1} s."));
        var pass = new AiChartPassResult(AiChartJob.Explain, 1, [], [], reply.Usage.InputTokens, reply.Usage.OutputTokens,
            reply.Usage.ModelId, clock.Elapsed);
        return new AiExplainResult(answered ? answer : null, proposed, pass);
    }
}
