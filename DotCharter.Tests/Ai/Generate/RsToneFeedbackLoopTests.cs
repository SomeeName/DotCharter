using DotCharter.Services.Ai.Generate;
using DotCharter.Tone.Audio.Dsp.Tones;
using DotCharter.Tone.Audio.Tonelab;
using static DotCharter.Tests.Ai.Generate.RsToneLoopFakes;

namespace DotCharter.Tests.Ai.Generate;

/// <summary>
///     The AI tone check over a fake model, a fake clone, a fake game and a scorer whose numbers can be worked out by
///     hand (<see cref="RsToneLoopFakes" />): rounds, the knob search, the game's verdict, the feedback the model gets,
///     the provenance, the clone-only fallback and the live-preview refusal.
/// </summary>
public sealed class RsToneFeedbackLoopTests
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public async Task A_ready_game_judges_the_finalists_and_its_best_wins_over_the_clones_favourite()
    {
        // Both proposals tune to the target through the clone; the game hears amp B darker, so A is the real winner.
        var mapper = new Mapper(_ => MakeTone(AmpB, 10), _ => MakeTone(AmpA, 20));
        var game = new Game { Offsets = { [AmpB] = 0.3 } };
        var loop = new RsToneFeedbackLoop(mapper, new Clone(), new Scorer(), game);

        var result = await loop.RunAsync(Input(0.7), Ct);

        Assert.True(result.JudgedByGame);
        Assert.Null(result.CloneOnlyReason);
        Assert.Equal(AmpA, result.Best!.Refined!.Amp.GearKey);
        Assert.Equal(RsToneLoopJudges.Game, result.Best.Judge);
        Assert.InRange(result.Best.Game!.Percent!.Value, 95, 100);
        Assert.Equal(3, result.Rounds.Count);
        Assert.All(result.Rounds, r => Assert.Equal(RsToneLoopJudges.Game, r.Judge));
        // One batch per round, never more than the finalists, and only REFINED tones go to the game.
        Assert.Equal(3, game.Batches.Count);
        Assert.All(game.Batches, b => Assert.InRange(b.Count, 1, 2));
        var bJudged = result.Rounds[0].Candidates.Single(c => c.Proposed!.Amp.GearKey == AmpB);
        Assert.True(bJudged.Game!.Distance > bJudged.CloneAfter!.Distance + 0.2, "the game's verdict on B is its own, not the clone's");
    }

    [Fact]
    public async Task Without_a_game_every_round_is_clone_only_and_the_result_says_why()
    {
        var loop = new RsToneFeedbackLoop(new Mapper(_ => MakeTone(AmpA, 10)), new Clone(), new Scorer(), null);

        var result = await loop.RunAsync(Input(0.7), Ct);

        Assert.False(result.JudgedByGame);
        Assert.Equal(RsToneLoopJudges.Clone, result.Judge);
        Assert.Equal(GameRenderRefusals.NoRenderer, result.CloneOnlyReason);
        Assert.All(result.Rounds, r =>
        {
            Assert.Equal(RsToneLoopJudges.Clone, r.Judge);
            Assert.Equal(GameRenderRefusals.NoRenderer, r.CloneOnlyReason);
        });
        Assert.All(result.Rounds.SelectMany(r => r.Candidates), c =>
        {
            Assert.Null(c.Game);
            Assert.Equal(GameRenderRefusals.NoRenderer, c.GameRefusal);
        });
        Assert.NotNull(result.Best!.CloneAfter);
    }

    [Fact]
    public async Task A_live_preview_routing_audio_is_never_interrupted_the_check_runs_clone_only_and_says_so()
    {
        var session = new FakeTonelabSession { State = TonelabSessionState.Ready, GameHoldsAudioDevice = false };
        var preview = new object();
        session.RouteAudio(preview, true);
        var game = new TonelabOfflineRenderer(session, null, Path.Combine(Path.GetTempPath(), "dotcharter-tonelab-loop-tests"));
        var loop = new RsToneFeedbackLoop(new Mapper(_ => MakeTone(AmpA, 10)), new Clone(), new Scorer(), game);

        var result = await loop.RunAsync(Input(0.7), Ct);

        Assert.Equal(RsToneLoopJudges.Clone, result.Judge);
        Assert.Equal(GameRenderRefusals.LivePreview, result.CloneOnlyReason);
        Assert.Empty(session.AppliedFiles);     // no tone reached the game
        Assert.Null(session.Lease);             // and the exchange was never taken
        Assert.Contains(preview, session.RouteOwners);
    }

    [Fact]
    public async Task A_game_that_becomes_free_mid_run_judges_the_later_rounds()
    {
        var game = new Game { Refusal = GameRenderRefusals.LivePreview };
        var calls = 0;
        // Round 0 asks twice and finds the game busy; the preview is off by the revision round's ask.
        var mapper = new Mapper(_ =>
        {
            if (++calls == 3) game.Refusal = null;
            return MakeTone(AmpA, 10);
        });
        var loop = new RsToneFeedbackLoop(mapper, new Clone(), new Scorer(), game);

        var result = await loop.RunAsync(Input(0.7), Ct);

        Assert.Equal(GameRenderRefusals.LivePreview, result.Rounds[0].CloneOnlyReason);
        Assert.Equal(RsToneLoopJudges.Clone, result.Rounds[0].Judge);
        Assert.Equal(RsToneLoopJudges.Game, result.Rounds[1].Judge);
        Assert.True(result.JudgedByGame);
        Assert.Equal(1, result.Best!.Round);
    }

    [Fact]
    public async Task A_revision_round_sends_the_best_tone_its_score_and_what_still_differs()
    {
        var mapper = new Mapper(_ => MakeTone(AmpA, 10));
        var options = new RsToneLoopOptions(Proposals: 2, RevisionRounds: 1, Finalists: 1, KnobEvaluations: 0);
        var loop = new RsToneFeedbackLoop(mapper, new Clone(), new Scorer(), null);

        var result = await loop.RunAsync(Input(0.7, options), Ct);

        Assert.Equal(3, mapper.Requests.Count);
        Assert.All(mapper.Requests.Take(2), r => Assert.Null(r.PreviousProposal));
        Assert.Contains("Proposal 1 of 2", mapper.Requests[0].Instruction, StringComparison.Ordinal);
        var revision = mapper.Requests[2];
        Assert.StartsWith("tight rhythm", revision.Instruction, StringComparison.Ordinal);
        Assert.Contains(RsToneFeedbackLoop.FeedbackHeader, revision.Instruction, StringComparison.Ordinal);
        // Bass 10 sits at 9/99 of its travel against a target of 0.7: 13 % on the scale, and darker than the song.
        Assert.Contains("scores 13 %", revision.Instruction, StringComparison.Ordinal);
        Assert.Contains("- too dark: fake", revision.Instruction, StringComparison.Ordinal);
        Assert.Contains("DotCharter's clone of Rocksmith", revision.Instruction, StringComparison.Ordinal);
        Assert.Equal(RsToneJsonWriter.Write(result.Rounds[0].Candidates[0].Refined!), revision.PreviousProposal);
        Assert.Null(result.Rounds[0].FeedbackSent);
        Assert.Contains(result.Rounds[1].FeedbackSent!, revision.Instruction, StringComparison.Ordinal);
    }

    [Fact]
    public async Task The_knob_search_keeps_the_gear_moves_knobs_inside_the_catalogue_and_gets_closer()
    {
        var options = new RsToneLoopOptions(Proposals: 1, RevisionRounds: 0, Finalists: 1, KnobEvaluations: 24);
        var loop = new RsToneFeedbackLoop(new Mapper(_ => MakeTone(AmpA, 10)), new Clone(), new Scorer(), null);

        var candidate = (await loop.RunAsync(Input(0.7, options), Ct)).Rounds[0].Candidates.Single();

        Assert.True(candidate.CloneAfter!.Distance < candidate.CloneBefore!.Distance / 4,
            $"the search moved {candidate.CloneBefore.Distance:0.###} to {candidate.CloneAfter.Distance:0.###}");
        Assert.Equal(candidate.Proposed!.Amp.GearKey, candidate.Refined!.Amp.GearKey);
        Assert.Equal(candidate.Proposed.Cabinet.GearKey, candidate.Refined.Cabinet.GearKey);
        Assert.Equal(candidate.Proposed.Amp.KnobValues.Keys.Order(), candidate.Refined.Amp.KnobValues.Keys.Order());
        Assert.True(RsToneCatalogValidation.IsValid(candidate.Refined, RsGearCatalog.Shared));
        Assert.All(candidate.Refined.Amp.KnobValues.Values, v => Assert.Equal(MathF.Round(v), v));   // the catalogue's step is 1
    }

    [Fact]
    public async Task A_refused_proposal_is_recorded_by_name_and_the_round_goes_on()
    {
        var bassAmp = RsToneAuthoring.GearOptions(RsGearCatalog.Shared, RsGearType.Amp).First(g => g.Key.StartsWith("Bass_", StringComparison.Ordinal)).Key;
        var mapper = new Mapper(_ => MakeTone(AmpA, 10), _ => MakeTone(bassAmp, 10), _ => MakeTone(AmpA, 30));
        var options = new RsToneLoopOptions(Proposals: 2, RevisionRounds: 0, Finalists: 2, KnobEvaluations: 0);

        var result = await new RsToneFeedbackLoop(mapper, new Clone(), new Scorer(), null).RunAsync(Input(0.7, options), Ct);

        var refused = result.Rounds[0].Candidates.Single(c => c.Error is not null);
        Assert.Contains(bassAmp, refused.Error, StringComparison.Ordinal);
        Assert.Null(refused.Refined);
        Assert.Equal(AmpA, result.Best!.Refined!.Amp.GearKey);
    }

    [Fact]
    public async Task The_same_inputs_and_seed_give_the_same_tone()
    {
        static RsToneFeedbackLoop Loop() => new(new Mapper(_ => MakeTone(AmpA, 10), _ => MakeTone(AmpB, 80)), new Clone(), new Scorer(), new Game());

        var first = await Loop().RunAsync(Input(0.55), Ct);
        var second = await Loop().RunAsync(Input(0.55), Ct);

        Assert.Equal(RsToneJsonWriter.Write(first.Best!.Refined!), RsToneJsonWriter.Write(second.Best!.Refined!));
    }

    [Fact]
    public void The_feedback_names_who_judged_it()
    {
        var tone = MakeTone(AmpA, 10);
        var score = new ToneLoopScore(40, null, 0.3, ["too bright: x"]);
        var byGame = new RsToneLoopCandidate(0, tone, tone, "m", "", score, score, score, null, null);
        var byClone = byGame with { Game = null, GameRefusal = GameRenderRefusals.NotReady };

        Assert.Contains("through Rocksmith itself, it scores 40 %", RsToneFeedbackLoop.Feedback(byGame, 1, 2), StringComparison.Ordinal);
        Assert.Contains("clone of Rocksmith (the game could not judge it)", RsToneFeedbackLoop.Feedback(byClone, 1, 2), StringComparison.Ordinal);
        Assert.Contains("(revision 2 of 2)", RsToneFeedbackLoop.Feedback(byClone, 2, 2), StringComparison.Ordinal);
    }
}
