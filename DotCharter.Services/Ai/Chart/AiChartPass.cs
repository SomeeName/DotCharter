using System.Diagnostics;
using System.Globalization;
using DotCharter.Services.Editing.Fingering;
using DotCharter.Tone.Ai;

namespace DotCharter.Services.Ai.Chart;

/// <summary>One pass over a level: what to fix, on what, and where.</summary>
/// <param name="Job">FHP + fingering, FHP only, fingering only, or chord names.</param>
/// <param name="Style">The naming style (the name job only).</param>
/// <param name="Facts">The song. Its <see cref="AiChartSongFacts.Arrangement" /> and
/// <paramref name="Level" /> must be a SNAPSHOT the caller owns: the pass reads them off
/// the calling thread for minutes.</param>
/// <param name="Level">The level to fix (one of the snapshot's levels).</param>
/// <param name="Beats">The beat map.</param>
/// <param name="From">First position of the selection, or null for the whole level.</param>
/// <param name="To">Last position of the selection, or null for the whole level.</param>
/// <param name="Options">Chunk sizes, or null for the defaults.</param>
/// <param name="Instruction">The AI chat's instruction, added to every chunk's prompt;
/// null for the menu verbs, whose prompts stay exactly as they were.</param>
public sealed record AiChartPassRequest(
    AiChartJob Job,
    ChordNamingStyle Style,
    AiChartSongFacts Facts,
    ChartLevel Level,
    IList<BeatMarker> Beats,
    FractionalPosition? From = null,
    FractionalPosition? To = null,
    AiChartChunkOptions? Options = null,
    AiChartInstruction? Instruction = null)
{
    /// <summary>Fix with AI: findings a chunk reaching them must remove; null for none.</summary>
    public IReadOnlyList<AiChartFindingTarget>? Targets { get; init; }
}

/// <summary>
/// The AI chart pass: chunk the level by phrase, ask the configured provider
///     about every chunk, and keep only the chunks whose reply survives
///     <see cref="AiChartReplyReader" />, <see cref="AiChartValidator" /> and
///     <see cref="AiChartRuleGate" />. A chunk whose reply is rejected is asked ONCE more,
///     with its own reply and the rejection in the prompt (<see cref="AiChartPrompt.Retry" />);
///     a second rejection is final. It WRITES NOTHING — the result is a list of plans
///     the caller turns into one undo step with <see cref="AiChartApplier" />.
///     <para>
///         The model calls for different chunks run concurrently (each chunk is self-
///         contained: its first anchor is at its own start); validation runs afterwards
///         in chunk order on one scratch level, so each chunk is judged with every earlier
///         accepted chunk in place.
///     </para>
/// </summary>
public sealed class AiChartPass
{
    private readonly IFingeringGenerationService _fingering;
    private readonly IAppLogger? _logger;
    private readonly int _concurrency;

    /// <summary>Builds the pass.</summary>
    /// <param name="fingering">The template resolver the apply step uses.</param>
    /// <param name="logger">Per-chunk outcomes go here (never the prompt or the reply).</param>
    /// <param name="concurrency">Model calls in flight at once.</param>
    public AiChartPass(IFingeringGenerationService fingering, IAppLogger? logger = null, int concurrency = 3)
    {
        _fingering = fingering ?? throw new ArgumentNullException(nameof(fingering));
        _logger = logger;
        _concurrency = Math.Max(1, concurrency);
    }

    /// <summary>Runs the pass. Throws <see cref="OperationCanceledException" /> on cancel and
    /// <see cref="ToneDesignException" /> when the provider cannot be used at all
    /// (not configured, not signed in); any other per-chunk failure is a rejection.</summary>
    /// <param name="request">What to fix.</param>
    /// <param name="model">The provider.</param>
    /// <param name="progress">Chunks answered so far, and the total.</param>
    /// <param name="ct">Cancels the whole pass; nothing is kept.</param>
    public async Task<AiChartPassResult> RunAsync(
        AiChartPassRequest request, IToneDesignModel model, IProgress<(int Done, int Total)>? progress,
        CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentNullException.ThrowIfNull(model);

        var clock = Stopwatch.StartNew();
        var chunks = AiChartChunker.Plan(request.Facts.Arrangement, request.Level, request.Beats,
            request.From, request.To, request.Options);
        var templates = new Dictionary<int, ChordTemplate>();
        foreach (var t in request.Facts.Arrangement.ChordTemplates) templates.TryAdd(t.Id, t);

        // A chunk with nothing the job can change is not worth a request: no fretted
        // sound for a job that moves anchors, no templated chord for the fingering-only
        // and name jobs.
        var worth = chunks.Where(c => request.Job.ProposesAnchors()
                ? c.Core.Any(s => s.Note is { } n && n.Fret > request.Facts.Arrangement.Capo || AiChartPrompt.IsFixable(s, templates))
                : c.Core.Any(s => AiChartPrompt.IsFixable(s, templates)))
            .ToList();
        if (worth.Count == 0)
            return new AiChartPassResult(request.Job, 0, [], [], 0, 0, "", clock.Elapsed);

        var replies = new (ToneDesignModelReply? Reply, string? Error)[worth.Count];
        var prompts = new ToneDesignPrompt?[worth.Count];
        var done = 0;
        progress?.Report((0, worth.Count));
        using (var gate = new SemaphoreSlim(_concurrency))
        {
            var tasks = worth.Select(async (chunk, i) =>
            {
                await gate.WaitAsync(ct).ConfigureAwait(false);
                try
                {
                    var prompt = AiChartPrompt.Build(request.Job, request.Style, request.Facts, request.Level,
                        request.Beats, chunk, request.Instruction);
                    prompts[i] = prompt;
                    replies[i] = (await model.CompleteAsync(prompt, ct).ConfigureAwait(false), null);
                }
                catch (ToneDesignException ex) when (ex.Fault is not (ToneDesignFault.NotConfigured or ToneDesignFault.Authentication))
                {
                    replies[i] = (null, ex.Message);
                }
                finally
                {
                    gate.Release();
                    progress?.Report((Interlocked.Increment(ref done), worth.Count));
                }
            }).ToList();
            await Task.WhenAll(tasks).ConfigureAwait(false);
        }

        ct.ThrowIfCancellationRequested();

        var ruleGate = new AiChartRuleGate(request.Facts.Arrangement, request.Level, request.Beats, _fingering,
            request.Job, request.Targets);
        var accepted = new List<AiChartChunkPlan>();
        var rejected = new List<AiChartRejection>();
        int inTokens = 0, outTokens = 0, retried = 0;
        var modelId = "";
        void Tally(ToneDesignModelReply? r)
        {
            if (r is null) return;
            inTokens += r.Usage.InputTokens;
            outTokens += r.Usage.OutputTokens;
            modelId = r.Usage.ModelId;
        }

        for (var i = 0; i < worth.Count; i++)
        {
            var chunk = worth[i];
            var (reply, error) = replies[i];
            Tally(reply);

            var rejection = Judge(request, chunk, reply, error, ruleGate, out var plan);

            // ONE retry, with the rejection in the prompt. In chunk order, so the
            // retry is judged against exactly the scratch the first reply was. A model that
            // did not answer at all is not asked again: there is no reply to correct, and a
            // provider fault is the provider's to retry.
            if (rejection is not null && reply is not null && prompts[i] is { } first)
            {
                retried++;
                _logger?.Info(AppLogCategory.Editing, string.Create(CultureInfo.InvariantCulture,
                    $"AI chart pass: chunk {chunk.Index} rejected ({rejection.Code}); asking once more with the reason."));
                ToneDesignModelReply? second = null;
                string? secondError = null;
                try
                {
                    second = await model.CompleteAsync(AiChartPrompt.Retry(first, reply.Json, rejection), ct)
                        .ConfigureAwait(false);
                }
                catch (ToneDesignException ex) when (ex.Fault is not (ToneDesignFault.NotConfigured or ToneDesignFault.Authentication))
                {
                    secondError = ex.Message;
                }

                Tally(second);
                rejection = Judge(request, chunk, second, secondError, ruleGate, out plan);
            }

            if (rejection is null)
            {
                accepted.Add(plan!);
                continue;
            }

            rejected.Add(rejection);
            _logger?.Info(AppLogCategory.Editing, string.Create(CultureInfo.InvariantCulture,
                $"AI chart pass: chunk {chunk.Index} ({chunk.StartMs} ms, {chunk.Core.Count} sounds) rejected — {rejection.Code}: {rejection.Reason}"));
        }

        _logger?.Info(AppLogCategory.Editing, string.Create(CultureInfo.InvariantCulture,
            $"AI chart pass ({request.Job}, {modelId}): {accepted.Count} of {worth.Count} chunk(s) accepted " +
            $"({retried} retried), {inTokens} in / {outTokens} out tokens, {clock.Elapsed.TotalSeconds:F1} s."));

        // what accepting every held chunk would leave in the pass's range, for the chat.
        var leftovers = ruleGate.FindingsIn(request.From?.GetPositionMs(request.Beats), request.To?.GetPositionMs(request.Beats));
        var result = new AiChartPassResult(request.Job, worth.Count, accepted, rejected, inTokens, outTokens, modelId,
            clock.Elapsed, retried);
        return result with { Leftovers = leftovers };
    }

    /// <summary>Reader, validator, rule gate — in that order; the first refusal is the
    /// chunk's rejection.</summary>
    private static AiChartRejection? Judge(
        AiChartPassRequest request, AiChartChunk chunk, ToneDesignModelReply? reply, string? error,
        AiChartRuleGate ruleGate, out AiChartChunkPlan? plan)
    {
        plan = null;
        if (reply is null)
            return new AiChartRejection(chunk.Index, AiChartRejectionCodes.ModelFailed, error ?? "no reply");
        if (!AiChartReplyReader.TryRead(reply.Json, request.Job, out var decoded, out var why))
            return new AiChartRejection(chunk.Index, AiChartRejectionCodes.Malformed, why);
        if (!AiChartValidator.TryPlan(decoded!, chunk, request.Job, request.Facts, request.Level, out plan,
                out var rejection))
        {
            return rejection;
        }

        return ruleGate.TryAccept(plan!, out rejection) ? null : rejection;
    }
}
