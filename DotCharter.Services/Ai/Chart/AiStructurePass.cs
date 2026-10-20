using System.Diagnostics;
using System.Globalization;
using DotCharter.Services.Editing.Quality;
using DotCharter.Tone.Ai;

namespace DotCharter.Services.Ai.Chart;

/// <summary>One structure request.</summary>
/// <param name="Project">A SNAPSHOT of the project the caller owns: the pass reads it off the
/// calling thread.</param>
/// <param name="Facts">The song; its <see cref="AiChartSongFacts.Arrangement" /> is one of
/// <paramref name="Project" />'s arrangements.</param>
/// <param name="Level">The level whose sounds the prompt sums up (one of that arrangement's).</param>
/// <param name="From">First position of the scope, or null for the song's start.</param>
/// <param name="To">Last position of the scope, or null for its end.</param>
/// <param name="Instruction">What the charter asked for.</param>
public sealed record AiStructureRequest(
    ChartProject Project,
    AiChartSongFacts Facts,
    ChartLevel Level,
    FractionalPosition? From,
    FractionalPosition? To,
    AiChartInstruction? Instruction)
{
    /// <summary>Fix with AI: the phrase findings the WHOLE proposal must remove; null for none.</summary>
    public IReadOnlyList<AiChartFindingTarget>? Targets { get; init; }
}

/// <summary>
/// The structure job: sections and phrases. ONE model request over the scope (a song's
///     form is only visible whole), read strictly by <see cref="AiStructureReply" /> — a reply that
///     names anything but a section or phrase change is refused whole and asked once more with the
///     reason, then dropped — and every change judged alone by <see cref="AiStructureGate" /> (it may
///     not add a chart-quality-report finding). With <see cref="AiStructureRequest.Targets" /> (Fix with
/// AI) the WHOLE proposal is judged too: every target must be gone with all accepted changes in
///     place (<see cref="AiStructureGate.TargetsLeft" />), else the reply is refused
///     (<see cref="AiChartRejectionCodes.FindingLeft" />), asked once more with the findings named, and
///     dropped whole when the retry leaves one too. It WRITES NOTHING: the result's
///     <see cref="AiChartPassResult.Structure" /> is what <see cref="AiChartReview.HoldStructure" />
///     holds for the review card.
/// </summary>
public sealed class AiStructurePass
{
    private readonly ChartQualityReporter _reporter;
    private readonly IAppLogger? _logger;

    /// <summary>Builds the pass.</summary>
    /// <param name="reporter">The chart quality report the gate compares.</param>
    /// <param name="logger">Outcomes go here (never the prompt or the reply).</param>
    public AiStructurePass(ChartQualityReporter reporter, IAppLogger? logger = null)
    {
        _reporter = reporter ?? throw new ArgumentNullException(nameof(reporter));
        _logger = logger;
    }

    /// <summary>Runs the job. Throws <see cref="OperationCanceledException" /> on cancel and
    /// <see cref="ToneDesignException" /> when the provider cannot be used at all.</summary>
    public async Task<AiChartPassResult> RunAsync(AiStructureRequest request, IToneDesignModel model, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentNullException.ThrowIfNull(model);
        var clock = Stopwatch.StartNew();
        var arrangement = request.Facts.Arrangement;
        var beats = request.Project.Beats;
        var context = AiStructureContext.Of(arrangement, beats, request.From, request.To);
        var prompt = AiStructurePrompt.Build(request.Facts, request.Level, beats, context, request.Instruction);

        int inTokens = 0, outTokens = 0, retried = 0;
        var modelId = "";
        var (reply, error) = await AskAsync(model, prompt, ct).ConfigureAwait(false);
        Tally(reply);
        var judged = Judge(request, reply, error, context, ct);
        if (judged.Whole is { } first && reply is { } firstReply)
        {
            retried = 1;
            _logger?.Info(AppLogCategory.Editing,
                $"AI structure pass: reply refused ({first.Code}); asking once more with the reason.");
            (reply, error) = await AskAsync(model, AiStructurePrompt.Retry(prompt, firstReply.Json, first), ct)
                .ConfigureAwait(false);
            Tally(reply);
            judged = Judge(request, reply, error, context, ct);
        }

        ct.ThrowIfCancellationRequested();
        var (changes, accepted, rejected, whole) = judged;
        // Refused whole: nothing is held, and the count of changes reads 0 (the run's "refused" line).
        if (whole is not null)
        {
            changes = [];
            accepted = [];
            rejected = [whole];
        }

        _logger?.Info(AppLogCategory.Editing, string.Create(CultureInfo.InvariantCulture,
            $"AI structure pass ({modelId}): {accepted.Count} of {changes.Count} change(s) accepted by the checks, " +
            $"{rejected.Count} refused ({string.Join(" ", rejected.Select(r => r.Code).Distinct())}); " +
            $"{inTokens} in / {outTokens} out tokens, {clock.Elapsed.TotalSeconds:F1} s."));
        return new AiChartPassResult(AiChartJob.SectionsAndPhrases, changes.Count, [], rejected, inTokens, outTokens, modelId,
            clock.Elapsed, retried, accepted);

        void Tally(ToneDesignModelReply? r)
        {
            if (r is null) return;
            inTokens += r.Usage.InputTokens;
            outTokens += r.Usage.OutputTokens;
            modelId = r.Usage.ModelId;
        }
    }

    /// <summary>
    ///     One reply judged: read strictly, every change by the gate alone, then the WHOLE accepted set
    /// against the targets. <c>Whole</c> is the rejection of the reply as a whole (unreadable,
    ///     or a target left), the one a retry is asked for.
    /// </summary>
    private Judged Judge(AiStructureRequest request, ToneDesignModelReply? reply, string? error,
        AiStructureContext context, CancellationToken ct)
    {
        var accepted = new List<AiStructureChange>();
        var rejected = new List<AiChartRejection>();
        if (Read(reply, error, context, out var changes) is { } unreadable) return new Judged(changes, accepted, rejected, unreadable);

        var gate = new AiStructureGate(request.Project, request.Facts.Arrangement, _reporter);
        for (var i = 0; i < changes.Count; i++)
        {
            ct.ThrowIfCancellationRequested();
            if (gate.TryAccept(changes[i], i, out var change, out var refused)) accepted.Add(change);
            else rejected.Add(refused!);
        }

        if (request.Targets is not { Count: > 0 } targets || gate.TargetsLeft(targets) is not { Count: > 0 } left)
            return new Judged(changes, accepted, rejected, null);

        var named = string.Join(", ", left.Select(t => t.TimeMs is { } ms
            ? string.Create(CultureInfo.InvariantCulture, $"{t.Code} at {ms / 1000.0:F2} s")
            : t.Code));
        var refusedChanges = rejected.Count == 0 ? "" : $" ({rejected.Count} change(s) were refused by the checks)";
        return new Judged(changes, accepted, rejected, new AiChartRejection(0, AiChartRejectionCodes.FindingLeft,
            $"with every accepted change in place the chart still has {left.Count} finding(s) it was asked to fix: " +
            named + refusedChanges));
    }

    private sealed record Judged(
        IReadOnlyList<AiStructureChange> Changes,
        List<AiStructureChange> Accepted,
        List<AiChartRejection> Rejected,
        AiChartRejection? Whole);

    private static async Task<(ToneDesignModelReply? Reply, string? Error)> AskAsync(
        IToneDesignModel model, ToneDesignPrompt prompt, CancellationToken ct)
    {
        try
        {
            return (await model.CompleteAsync(prompt, ct).ConfigureAwait(false), null);
        }
        catch (ToneDesignException ex) when (ex.Fault is not (ToneDesignFault.NotConfigured or ToneDesignFault.Authentication))
        {
            return (null, ex.Message);
        }
    }

    private static AiChartRejection? Read(ToneDesignModelReply? reply, string? error, AiStructureContext context,
        out IReadOnlyList<AiStructureChange> changes)
    {
        changes = [];
        if (reply is null) return new AiChartRejection(0, AiChartRejectionCodes.ModelFailed, error ?? "no reply");
        return AiStructureReply.TryRead(reply.Json, context, out changes, out var rejection) ? null : rejection;
    }
}
