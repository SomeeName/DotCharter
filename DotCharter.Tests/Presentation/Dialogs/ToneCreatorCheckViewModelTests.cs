using CommunityToolkit.Mvvm.Input;
using DotCharter.Services.Ai.Generate;
using DotCharter.Services.Ai.Match;
using DotCharter.Tests.Ai.Generate;
using DotCharter.Tone.Audio.Tonelab;

namespace DotCharter.Tests.Presentation.Dialogs;

/// <summary>
///     The creator's Check with audio over a FAKE loop: greyed with its reason without a span of the song, the kept tone
///     becomes the proposal, the judge line says the clone judged it and why the game did not (never as if the game
///     had), one line per round, the creator's own Audition stopped before the check, and uncached stems said plainly.
/// </summary>
public sealed class ToneCreatorCheckViewModelTests : IDisposable
{
    private readonly string _audio = Path.Combine(Path.GetTempPath(), $"dotcharter-creator-check-{Guid.NewGuid():N}.wav");

    public ToneCreatorCheckViewModelTests() => File.WriteAllBytes(_audio, [0]);

    public void Dispose() => File.Delete(_audio);

    [Fact]
    public void Without_song_audio_the_check_is_greyed_with_its_reason()
    {
        var vm = new ToneCreatorViewModelTests.Harness().Open(loop: new CannedLoop(CloneOnly()));

        Assert.False(vm.CanCheck);
        Assert.False(vm.CheckCommand.CanExecute(null));
        Assert.Equal("Needs the song's audio and beat map at the playhead to compare against", vm.CheckUnavailableReason);
    }

    [Fact]
    public async Task A_clone_only_check_keeps_its_best_says_who_judged_and_why_the_game_did_not()
    {
        var harness = new ToneCreatorViewModelTests.Harness();
        harness.State.CurrentAudioFileName = _audio;
        var loop = new CannedLoop(CloneOnly());
        var vm = harness.Open(loop: loop);
        vm.Description = "tight rhythm";
        await ((IAsyncRelayCommand)vm.CreateCommand).ExecuteAsync(null);
        Assert.True(vm.HasProposal);
        vm.IsAuditioning = true;

        Assert.True(vm.CanCheck);
        await ((IAsyncRelayCommand)vm.CheckCommand).ExecuteAsync(null);

        Assert.Equal(1, harness.Preview.Stops);                 // the audition let go of the game before the check
        Assert.NotNull(loop.Asked!.Window);                     // the span at the playhead is what it compares against
        Assert.NotNull(loop.Asked.Previous);                    // and it starts from the proposal on screen
        Assert.True(vm.HasProposal);
        Assert.Equal("", vm.Status);
        Assert.Equal("Judged by DotCharter's clone only, not by the game " +
                     "(a live Rocksmith preview is playing through the game; turn it off to check there): 41 %", vm.CheckJudgeText);
        Assert.Equal(2, vm.CheckRounds.Count);
        Assert.StartsWith("Round 1: ", vm.CheckRounds[0], StringComparison.Ordinal);
        Assert.EndsWith("(clone 41 %; the game did not judge it)", vm.CheckRounds[0], StringComparison.Ordinal);
        Assert.Equal("Round 2: every proposal was refused (unknown gear 'Pedal_Invented')", vm.CheckRounds[1]);
    }

    [Fact]
    public async Task A_game_judged_check_names_both_scores()
    {
        var harness = new ToneCreatorViewModelTests.Harness();
        harness.State.CurrentAudioFileName = _audio;
        var tone = RsToneLoopFakes.MakeTone(RsToneLoopFakes.AmpA, 40);
        var best = new RsToneLoopCandidate(0, tone, tone, "m", "why", null, Score(30), Score(58), null, null);
        var vm = harness.Open(loop: new CannedLoop(new RsToneLoopResult(best, RsToneLoopJudges.Game, null,
            [new RsToneLoopRound(0, null, [best], RsToneLoopJudges.Game, null)])));

        await ((IAsyncRelayCommand)vm.CheckCommand).ExecuteAsync(null);

        Assert.Equal("Judged by Rocksmith itself: 58 %", vm.CheckJudgeText);
        Assert.EndsWith("(clone 30 %, Rocksmith 58 %)", Assert.Single(vm.CheckRounds), StringComparison.Ordinal);
        Assert.Equal("why", vm.Rationale);
    }

    [Fact]
    public async Task Uncached_stems_are_said_plainly_and_nothing_is_kept()
    {
        var harness = new ToneCreatorViewModelTests.Harness();
        harness.State.CurrentAudioFileName = _audio;
        var vm = harness.Open(loop: new CannedLoop(null));

        await ((IAsyncRelayCommand)vm.CheckCommand).ExecuteAsync(null);

        Assert.Equal("The song's stems are not separated yet, so there is nothing to compare against. Separate them first.", vm.Status);
        Assert.False(vm.HasProposal);
        Assert.False(vm.HasCheckResult);
    }

    private static ToneLoopScore Score(int percent) => new(percent, null, 1 - (percent / 100.0), []);

    private static RsToneLoopResult CloneOnly()
    {
        var tone = RsToneLoopFakes.MakeTone(RsToneLoopFakes.AmpA, 40);
        var best = new RsToneLoopCandidate(0, tone, tone, "m", "", Score(20), Score(41), null, GameRenderRefusals.LivePreview, null);
        var refused = new RsToneLoopCandidate(1, null, null, "", "", null, null, null, null, "unknown gear 'Pedal_Invented'");
        return new RsToneLoopResult(best, RsToneLoopJudges.Clone, GameRenderRefusals.LivePreview,
        [
            new RsToneLoopRound(0, null, [best], RsToneLoopJudges.Clone, GameRenderRefusals.LivePreview),
            new RsToneLoopRound(1, "feedback", [refused], RsToneLoopJudges.Clone, null)
        ]);
    }

    /// <summary>Answers a prepared result, or throws <see cref="StemsNotCachedException" /> for null.</summary>
    private sealed class CannedLoop(RsToneLoopResult? result) : IRsToneFeedbackLoop
    {
        public ToneCreatorAsk? Asked { get; private set; }

        public Task<RsToneLoopResult> RunAsync(ToneCreatorAsk ask, CancellationToken ct)
        {
            Asked = ask;
            return result is null ? throw new StemsNotCachedException("no stems") : Task.FromResult(result);
        }
    }
}
