using System.Globalization;
using System.Text;
using DotCharter.Services.Ai.Match;
using DotCharter.Services.Ai.Match.Search;
using DotCharter.Services.Ai.Match.Similarity;
using DotCharter.Tone.Audio.Dsp.Tones;
using DotCharter.Tone.Audio.Tonelab;

namespace DotCharter.Services.Ai.Generate;

/// <summary>
///     The AI tone check: Rocksmith tones that are judged by how they SOUND, not by one look at eleven numbers.
///     <list type="number">
///         <item><b>Propose</b>: the model proposes <see cref="RsToneLoopOptions.Proposals" /> catalogue-valid tones.</item>
///         <item><b>Refine</b>: per proposal, a CMA-ES knob search through the RS→DC clone; the gear stays fixed and the
///         search space is the catalogue's own ranges (<see cref="RsToneKnobSpace" />).</item>
///         <item><b>Judge</b>: the clone's best <see cref="RsToneLoopOptions.Finalists" /> go through the real game
///         (<see cref="IGameToneRenderer" />), scored on the same <see cref="MatchScale" />.</item>
///         <item><b>Revise</b>: the best tone so far, its score and what still differs go back to the model for
///         <see cref="RsToneLoopOptions.RevisionRounds" /> more rounds.</item>
///         <item><b>Pick</b>: the best GAME score wins when the game judged anything; else the best clone score, and the
///         result says it is clone-only and why (<see cref="RsToneLoopResult.CloneOnlyReason" />).</item>
///     </list>
///     The game is asked per round, so a game that becomes ready (or a live preview that is turned off) mid-run is used
///     from the next round on; a live preview that routes audio is never interrupted: that round is clone-only, with the
///     reason.
/// </summary>
public sealed class RsToneFeedbackLoop : IRsToneFeedbackLoop
{
    /// <summary>The prompt section header the revision rounds' feedback goes under.</summary>
    public const string FeedbackHeader = "FEEDBACK FROM LISTENING";

    private readonly IRsToneMapper _mapper;
    private readonly IRsToneCloneRenderer _clone;
    private readonly IToneLoopScorer _scorer;
    private readonly IGameToneRenderer? _game;
    private readonly IStemIsolator? _isolator;
    private readonly RsGearCatalog _catalog;

    /// <param name="mapper">The model.</param>
    /// <param name="clone">The fast renderer the knob search runs on.</param>
    /// <param name="scorer">The score and the differences.</param>
    /// <param name="game">The real game, or null in a build without one (every round is then clone-only).</param>
    /// <param name="isolator">The stems <see cref="RunAsync(ToneCreatorAsk, CancellationToken)" /> measures; cache-only in the app.</param>
    /// <param name="catalog">The gear catalogue; null for the shipped one.</param>
    public RsToneFeedbackLoop(IRsToneMapper mapper, IRsToneCloneRenderer clone, IToneLoopScorer scorer, IGameToneRenderer? game,
        IStemIsolator? isolator = null, RsGearCatalog? catalog = null)
    {
        _mapper = mapper;
        _clone = clone;
        _scorer = scorer;
        _game = game;
        _isolator = isolator;
        _catalog = catalog ?? RsGearCatalog.Shared;
    }

    /// <summary>The app's loop: the stem isolator narrowed to its cache, the match lane's renderer and metric.</summary>
    public static RsToneFeedbackLoop CacheOnly(IRsToneMapper mapper, IRigRenderer renderer, IToneSimilarity similarity, IGameToneRenderer? game,
        IStemIsolator isolator) =>
        new(mapper, new RigCloneRsToneRenderer(renderer), new MatchScaleToneLoopScorer(similarity), game,
            isolator is StemIsolator stems ? stems.CacheOnly() : isolator);

    public async Task<RsToneLoopResult> RunAsync(ToneCreatorAsk ask, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(ask);
        if (ask.Window is not { } window || _isolator is null)
            throw new ArgumentException("The audio check needs a span of the song to compare against.", nameof(ask));
        var target = await _isolator.IsolateAsync(window.SongAudioPath, ask.Instrument, TimeSpan.FromMilliseconds(window.FromMs),
            TimeSpan.FromMilliseconds(window.ToMs), null, ct).ConfigureAwait(false);
        var features = (await new MeasuredToneEvidenceSource().DescribeAsync(target, ct).ConfigureAwait(false))?.Features;
        var request = new RsToneMapRequest(features, ask.Context, ask.ToneKey, ask.ToneName, ask.Instrument, ask.SongTitle, ask.Artist,
            ToneCreator.InstructionFor(ask), ask.ArrangementName, ask.Previous is null ? null : RsToneJsonWriter.Write(ask.Previous));
        return await RunAsync(new RsToneLoopInput(request, target, ReferenceDi.Generate(), new RsToneLoopOptions()), ct).ConfigureAwait(false);
    }

    /// <summary>The whole check over a measured target.</summary>
    public async Task<RsToneLoopResult> RunAsync(RsToneLoopInput input, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(input);
        var options = input.Options;
        var reference = await _clone.RenderReferenceAsync(input.Di, ct).ConfigureAwait(false);
        var rounds = new List<RsToneLoopRound>(options.RevisionRounds + 1);
        RsToneLoopCandidate? best = null;
        for (var round = 0; round <= options.RevisionRounds; round++)
        {
            string? feedback = null;
            List<RsToneMapRequest> asks;
            if (round == 0)
            {
                asks = FirstAsks(input.Request, options.Proposals);
            }
            else if (best?.Refined is { } previous)
            {
                feedback = Feedback(best, round, options.RevisionRounds);
                asks = [Revision(input.Request, previous, feedback)];
            }
            else
            {
                break;
            }

            var candidates = new List<RsToneLoopCandidate>(asks.Count);
            foreach (var ask in asks)
                candidates.Add(await ProposeAndRefineAsync(round, ask, input, reference, options, ct).ConfigureAwait(false));

            var (judged, refusal) = await JudgeAsync(candidates, input, reference, options.Finalists, ct).ConfigureAwait(false);
            var roundJudge = judged.Any(c => c.Game is not null) ? RsToneLoopJudges.Game : RsToneLoopJudges.Clone;
            rounds.Add(new RsToneLoopRound(round, feedback, judged, roundJudge, roundJudge == RsToneLoopJudges.Game ? null : refusal));
            best = Better(best, judged);
        }

        var judge = best?.Judge ?? RsToneLoopJudges.Clone;
        var cloneOnly = judge == RsToneLoopJudges.Game ? null : rounds.Select(r => r.CloneOnlyReason).LastOrDefault(r => r is not null);
        return new RsToneLoopResult(best, judge, cloneOnly, rounds);
    }

    /// <summary>The revision request: the best tone as the previous proposal, the feedback after the charter's words.</summary>
    internal static RsToneMapRequest Revision(RsToneMapRequest first, RsTone best, string feedback) =>
        first with
        {
            PreviousProposal = RsToneJsonWriter.Write(best),
            Instruction = string.IsNullOrWhiteSpace(first.Instruction) ? feedback : first.Instruction + "\n\n" + feedback
        };

    /// <summary>What the revision round tells the model: whose ears judged, the score, and what still differs.</summary>
    internal static string Feedback(RsToneLoopCandidate best, int round, int rounds)
    {
        var score = best.Score;
        var sb = new StringBuilder(512);
        sb.Append(FeedbackHeader).Append(" (revision ").Append(round.ToString(CultureInfo.InvariantCulture)).Append(" of ")
            .Append(rounds.ToString(CultureInfo.InvariantCulture)).Append(")\n");
        sb.Append("Your best tone so far is the previous proposal above. Rendered through ")
            .Append(best.Judge == RsToneLoopJudges.Game ? "Rocksmith itself" : "DotCharter's clone of Rocksmith (the game could not judge it)")
            .Append(score?.Percent is { } p
                ? string.Create(CultureInfo.InvariantCulture, $", it scores {p} % against the song (0 % is a plain amp and cabinet, 100 % identical).\n")
                : $", it could not be rated ({score?.UnratedReason ?? "no score"}).\n");
        var differences = score?.Differences ?? [];
        if (differences.Count == 0)
        {
            sb.Append("No measured quantity differs clearly; refine the knobs for a closer match.\n");
        }
        else
        {
            sb.Append("What still differs from the song:\n");
            foreach (var line in differences) sb.Append("- ").Append(line).Append('\n');
        }

        sb.Append("Revise the gear or the knobs to close these differences, and keep what already matches.");
        return sb.ToString();
    }

    private static List<RsToneMapRequest> FirstAsks(RsToneMapRequest request, int proposals)
    {
        var count = Math.Max(1, proposals);
        if (count == 1) return [request];
        return Enumerable.Range(1, count).Select(i =>
        {
            var line = string.Create(CultureInfo.InvariantCulture,
                $"Proposal {i} of {count}: make it a genuinely different rig (another amp family, cabinet or pedal set) from what an obvious first answer would be.");
            return request with { Instruction = string.IsNullOrWhiteSpace(request.Instruction) ? line : request.Instruction + "\n" + line };
        }).ToList();
    }

    private async Task<RsToneLoopCandidate> ProposeAndRefineAsync(int round, RsToneMapRequest ask, RsToneLoopInput input, MonoAudio reference,
        RsToneLoopOptions options, CancellationToken ct)
    {
        RsToneMapping mapping;
        try
        {
            mapping = await _mapper.MapAsync(ask, ct).ConfigureAwait(false);
            if (ToneCreator.InstrumentFault(mapping.Tone, ask.Instrument) is { } fault) throw new RsToneCatalogException(fault, "the proposed tone");
        }
        catch (RsToneCatalogException ex)
        {
            return new RsToneLoopCandidate(round, null, null, "", "", null, null, null, null, ex.Message);
        }

        var proposed = RsToneCatalogValidation.Require(mapping.Tone, _catalog, "the proposed tone");
        var before = await CloneScoreAsync(proposed, input, reference, ct).ConfigureAwait(false);
        var space = RsToneKnobSpace.Of(proposed, _catalog);
        var (refined, after) = (proposed, before);
        if (space.Count > 0 && options.KnobEvaluations > 0)
        {
            var search = await CmaEs.MinimizeAsync(
                async (x, token) => (await CloneScoreAsync(space.Decode(proposed, x), input, reference, token).ConfigureAwait(false)).Distance,
                space.Encode(proposed), new CmaEsOptions(options.Seed + round, InitialSigma: 0.2, MaxEvaluations: options.KnobEvaluations), ct)
                .ConfigureAwait(false);
            if (search.BestValue < before.Distance)
            {
                refined = space.Decode(proposed, search.Best);
                after = await CloneScoreAsync(refined, input, reference, ct).ConfigureAwait(false);
            }
        }

        return new RsToneLoopCandidate(round, proposed, refined, mapping.ModelId, mapping.Rationale, before, after, null, null, null);
    }

    private async Task<ToneLoopScore> CloneScoreAsync(RsTone tone, RsToneLoopInput input, MonoAudio reference, CancellationToken ct) =>
        _scorer.Score(input.Target, await _clone.RenderAsync(input.Di, tone, ct).ConfigureAwait(false), reference);

    /// <summary>The round's finalists through the game, when it can judge now; the refusal otherwise.</summary>
    private async Task<(List<RsToneLoopCandidate> Candidates, string? Refusal)> JudgeAsync(List<RsToneLoopCandidate> candidates,
        RsToneLoopInput input, MonoAudio reference, int finalists, CancellationToken ct)
    {
        var finals = candidates.Where(c => c.Refined is not null).OrderBy(c => c.CloneAfter!.Distance).Take(Math.Max(1, finalists)).ToList();
        if (finals.Count == 0) return (candidates, null);
        var refusal = _game is null ? GameRenderRefusals.NoRenderer : _game.Availability().Refusal;
        IReadOnlyList<GameRenderResult> renders = [];
        if (refusal is null)
        {
            var di = MonoResampler.To(input.Di, _game!.SampleRate);
            renders = await _game.RenderAsync(di.Samples, finals.Select(c => c.Refined!).ToList(), ct).ConfigureAwait(false);
            refusal = renders.FirstOrDefault(r => r.Samples is null)?.Refusal;
        }

        var judged = candidates.ToList();
        for (var i = 0; i < finals.Count; i++)
        {
            var index = judged.IndexOf(finals[i]);
            judged[index] = i < renders.Count && renders[i].Samples is { } samples
                ? finals[i] with { Game = _scorer.Score(input.Target, new MonoAudio(samples, _game!.SampleRate), reference) }
                : finals[i] with { GameRefusal = i < renders.Count ? renders[i].Refusal : refusal };
        }

        return (judged, refusal);
    }

    /// <summary>A game score beats any clone score; within one judge, the smaller distance wins.</summary>
    private static RsToneLoopCandidate? Better(RsToneLoopCandidate? best, IEnumerable<RsToneLoopCandidate> round)
    {
        foreach (var candidate in round.Where(c => c.Score is not null))
        {
            if (best is null) best = candidate;
            else if (candidate.Judge == RsToneLoopJudges.Game && best.Judge != RsToneLoopJudges.Game) best = candidate;
            else if (candidate.Judge == best.Judge && candidate.Score!.Distance < best.Score!.Distance) best = candidate;
        }

        return best;
    }
}
