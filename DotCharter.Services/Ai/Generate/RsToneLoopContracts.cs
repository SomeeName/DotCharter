using DotCharter.Services.Ai.Match;
using DotCharter.Services.Ai.Match.Similarity;
using DotCharter.Tone.Audio.Dsp.RsToneConversion;
using DotCharter.Tone.Audio.Dsp.Tones;

namespace DotCharter.Services.Ai.Generate;

// The AI tone check: propose Rocksmith tones → refine their knobs through the RS→DC clone → judge the finalists
// through the real game (tonelab) → feed the scores and the measured differences back for revision rounds → keep
// the best. The model is IRsToneMapper, the game is DotCharter.Tone's IGameToneRenderer, and these are the rest.

/// <summary>Who judged a score: the real game, or only DotCharter's clone of it.</summary>
public static class RsToneLoopJudges
{
    /// <summary>Rendered through Rocksmith itself (tonelab).</summary>
    public const string Game = "game";

    /// <summary>Rendered through DotCharter's RS→DC conversion only.</summary>
    public const string Clone = "clone";
}

/// <summary>How much work the check does.</summary>
/// <param name="Proposals">Tones the model proposes in the first round.</param>
/// <param name="RevisionRounds">Rounds after the first, each one revision of the best so far with the feedback.</param>
/// <param name="Finalists">Per round, how many of the clone's best go through the game.</param>
/// <param name="KnobEvaluations">Clone renders the knob search may spend per proposal.</param>
/// <param name="Seed">The knob search's seed: the same inputs and seed give the same result.</param>
public sealed record RsToneLoopOptions(int Proposals = 3, int RevisionRounds = 2, int Finalists = 2, int KnobEvaluations = 40, int Seed = 0);

/// <summary>One run's inputs.</summary>
/// <param name="Request">The model request the first round asks with (features, context, the charter's words).</param>
/// <param name="Target">The song's isolated stem the renders are scored against.</param>
/// <param name="Di">The dry take every tone is rendered over (<see cref="ReferenceDi" />).</param>
/// <param name="Options">The budget.</param>
public sealed record RsToneLoopInput(RsToneMapRequest Request, MonoAudio Target, MonoAudio Di, RsToneLoopOptions Options);

/// <summary>One render's score against the target: the W5b percentage (or why there is none), the distance it comes
/// from (lower is closer), and what still differs, in words the model and the charter can read.</summary>
public sealed record ToneLoopScore(int? Percent, string? UnratedReason, double Distance, IReadOnlyList<string> Differences);

/// <summary>Scores a render against the target, on the <see cref="MatchScale" /> (0 % is the reference rig's render).</summary>
public interface IToneLoopScorer
{
    ToneLoopScore Score(MonoAudio target, MonoAudio candidate, MonoAudio reference);
}

/// <summary>DotCharter's clone of a Rocksmith tone, rendered: the fast, offline half of the check.</summary>
public interface IRsToneCloneRenderer
{
    /// <summary>The tone through the RS→DC conversion, over <paramref name="di" />.</summary>
    Task<MonoAudio> RenderAsync(MonoAudio di, RsTone tone, CancellationToken ct);

    /// <summary>The 0 % anchor: <see cref="MatchScale.ReferenceRig" /> over <paramref name="di" />.</summary>
    Task<MonoAudio> RenderReferenceAsync(MonoAudio di, CancellationToken ct);
}

/// <summary>The shipping clone renderer: <see cref="RsToneConverter.ConvertFilled" /> on the match lane's renderer.</summary>
public sealed class RigCloneRsToneRenderer(IRigRenderer renderer) : IRsToneCloneRenderer
{
    public Task<MonoAudio> RenderAsync(MonoAudio di, RsTone tone, CancellationToken ct) =>
        renderer.RenderAsync(di, new RsToneConverter().ConvertFilled(tone, out _), ct);

    public Task<MonoAudio> RenderReferenceAsync(MonoAudio di, CancellationToken ct) => renderer.RenderAsync(di, MatchScale.ReferenceRig(), ct);
}

/// <summary>
///     One tone the check tried: what the model proposed, what the knob search made of it, and each judge's score.
///     <paramref name="Error" /> set means the proposal was refused (a catalogue fault, gear of the other instrument)
///     and nothing else is.
/// </summary>
/// <param name="Round">0 for the first round.</param>
/// <param name="Proposed">The model's tone, catalogue-valid.</param>
/// <param name="Refined">The same gear after the clone's knob search (the proposal when the search found nothing better).</param>
/// <param name="ModelId">The model that answered.</param>
/// <param name="Rationale">The model's own reason.</param>
/// <param name="CloneBefore">The proposal through the clone, before the knob search.</param>
/// <param name="CloneAfter">The refined tone through the clone.</param>
/// <param name="Game">The refined tone through the game; null when it was not a finalist or the game could not judge.</param>
/// <param name="GameRefusal">Why a finalist has no game score (one of <c>GameRenderRefusals</c>), or null.</param>
/// <param name="Error">Why there is no tone, or null.</param>
public sealed record RsToneLoopCandidate(
    int Round,
    RsTone? Proposed,
    RsTone? Refined,
    string ModelId,
    string Rationale,
    ToneLoopScore? CloneBefore,
    ToneLoopScore? CloneAfter,
    ToneLoopScore? Game,
    string? GameRefusal,
    string? Error)
{
    /// <summary>Which judge <see cref="Score" /> is from.</summary>
    public string Judge => Game is null ? RsToneLoopJudges.Clone : RsToneLoopJudges.Game;

    /// <summary>The score that counts: the game's when there is one, else the clone's.</summary>
    public ToneLoopScore? Score => Game ?? CloneAfter;
}

/// <summary>One round: the feedback the model was sent (null in the first round), what it proposed, and who judged.</summary>
/// <param name="Index">0 for the first round.</param>
/// <param name="FeedbackSent">The scores and differences sent with the revision request, or null.</param>
/// <param name="Candidates">Every proposal of the round, refused ones included.</param>
/// <param name="Judge">The game when any finalist of the round was judged by it, else the clone.</param>
/// <param name="CloneOnlyReason">Why the game did not judge this round (one of <c>GameRenderRefusals</c>), or null.</param>
public sealed record RsToneLoopRound(int Index, string? FeedbackSent, IReadOnlyList<RsToneLoopCandidate> Candidates, string Judge, string? CloneOnlyReason);

/// <summary>
///     What a whole check produced. <paramref name="Best" /> is the best GAME score when the game judged anything, else
///     the best clone score, and <paramref name="Judge" /> says which: a clone-only result never reads as the game's.
/// </summary>
public sealed record RsToneLoopResult(RsToneLoopCandidate? Best, string Judge, string? CloneOnlyReason, IReadOnlyList<RsToneLoopRound> Rounds)
{
    /// <summary>True when the best was judged by the game itself.</summary>
    public bool JudgedByGame => Judge == RsToneLoopJudges.Game;
}

/// <summary>The Tone designer's entry: the creator's ask, checked with audio.</summary>
public interface IRsToneFeedbackLoop
{
    /// <summary>
    ///     Measures the ask's window (cached stems only) and runs the check over it.
    ///     Throws <see cref="StemsNotCachedException" /> when the song's stems are not cached and
    ///     <see cref="ArgumentException" /> for an ask without a window: there is nothing to compare against.
    /// </summary>
    Task<RsToneLoopResult> RunAsync(ToneCreatorAsk ask, CancellationToken ct);
}
