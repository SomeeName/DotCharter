using DotCharter.Presentation.Screens.Editor;
using DotCharter.Services.Ai.Chart;
using DotCharter.Services.Ai.Chat;
using DotCharter.Tests.Ai.Chart;
using DotCharter.UI.ViewModels.Editor.Inspector;

namespace DotCharter.Tests.UI.ViewModels.Editor;

/// <summary>
///     The Issues tab's AI row (#803): Fix with AI only for a finding a job of the AI chart pass can fix
///     (any other one says why instead), Fix all in this phrase for every fixable finding of the level in
///     the finding's phrase, Explain with AI for any finding — each sent to the AI chat as a request the
///     door receives. A fake door: no chat, no model.
/// </summary>
public sealed class IssueAiViewModelTests
{
    private readonly AiChartFixture _f = new();
    private readonly ProjectState _state = new();
    private readonly FakeDoor _door = new();
    private readonly IssueAiViewModel _vm;

    public IssueAiViewModelTests()
    {
        _state.ActiveProject = _f.Project;
        _state.ActiveArrangement = _f.Arrangement;
        _state.ActiveLevel = _f.Level;
        _vm = new IssueAiViewModel(_state, FakeLocalization.KeysOnly(), () => _door);
    }

    private static ArrangementIssue At(ArrangementIssueType type, double ms) => new(type, type.ToString(), ms);

    [Fact]
    public async Task A_fixable_finding_offers_Fix_with_AI_and_sends_its_fix_request()
    {
        var issue = At(ArrangementIssueType.AnchorInsideHandShape, 5000);
        _vm.Offer(issue, _f.Arrangement, _f.Level, [issue]);

        Assert.True(_vm.HasIssue);
        Assert.True(_vm.CanFix);
        Assert.False(_vm.HasNoJob);
        Assert.False(_vm.HasPhraseFix);
        await _vm.FixCommand.ExecuteAsync(null);

        var request = Assert.Single(_door.Requests);
        Assert.Equal(AiChartJob.FhpOnly, request.Job);
        Assert.Equal(new AiChartFindingTarget("I20", 5000), Assert.Single(request.Targets!));
        Assert.Equal("picked-model", request.ModelId);
    }

    /// <summary>#808: a phrase finding (here the untimed I26) is fixed by the sections &amp; phrases job on the
    /// whole arrangement, with the finding as its target.</summary>
    [Fact]
    public async Task A_phrase_finding_offers_fix_with_ai_through_the_sections_and_phrases_job()
    {
        var issue = new ArrangementIssue(ArrangementIssueType.NoEndPhrase, "No END phrase.");
        _vm.Offer(issue, _f.Arrangement, _f.Level, [issue]);

        Assert.True(_vm.CanFix);
        Assert.False(_vm.HasNoJob);
        await _vm.FixCommand.ExecuteAsync(null);

        var request = Assert.Single(_door.Requests);
        Assert.Equal(AiChartJob.SectionsAndPhrases, request.Job);
        Assert.Null(request.Scope.From);
        Assert.Null(request.Scope.To);
        Assert.Equal(new AiChartFindingTarget("I26", null), Assert.Single(request.Targets!));
    }

    [Fact]
    public void A_finding_no_job_can_fix_says_so_instead_of_offering_the_button()
    {
        var issue = At(ArrangementIssueType.OverlappingNotes, 8000);
        _vm.Offer(issue, _f.Arrangement, _f.Level, [issue]);

        Assert.True(_vm.HasIssue);
        Assert.False(_vm.CanFix);
        Assert.False(_vm.FixCommand.CanExecute(null));
        Assert.True(_vm.HasNoJob);
        Assert.Equal("Inspector_IssueAi_NoJob(L15)", _vm.NoJobText);
        Assert.True(_vm.ExplainCommand.CanExecute(null));
    }

    [Fact]
    public async Task Fix_all_in_this_phrase_takes_every_fixable_finding_of_the_phrase_and_no_other()
    {
        var target = At(ArrangementIssueType.AnchorInsideHandShape, 5000);          // intro
        var sameIntro = At(ArrangementIssueType.FingerSetForOpenString, 6000);      // intro
        var notFixable = At(ArrangementIssueType.EmptyPhrase, 1000);                // intro, another job (#808)
        var verse = At(ArrangementIssueType.NoteOutsideAnchorRange, 11000);         // the next phrase
        _vm.Offer(target, _f.Arrangement, _f.Level, [target, sameIntro, notFixable, verse]);

        Assert.True(_vm.HasPhraseFix);
        Assert.Equal([target, sameIntro], _vm.PhraseFindings);
        Assert.Equal("Inspector_IssueAi_FixPhrase(2)", _vm.FixPhraseLabel);
        await _vm.FixPhraseCommand.ExecuteAsync(null);

        var request = Assert.Single(_door.Requests);
        Assert.Equal(AiChartJob.FhpAndFingering, request.Job);   // an anchor AND a finger finding
        Assert.Equal(["I20", "L06"], request.Targets!.Select(t => t.Code));
    }

    [Fact]
    public async Task Explain_with_AI_sends_the_explain_job_for_any_finding()
    {
        var issue = At(ArrangementIssueType.EmptyPhrase, 8000);
        _vm.Offer(issue, _f.Arrangement, _f.Level, [issue]);

        await _vm.ExplainCommand.ExecuteAsync(null);

        var request = Assert.Single(_door.Requests);
        Assert.Equal(AiChartJob.Explain, request.Job);
        Assert.Null(request.Targets);
        Assert.Contains("D03", request.Instruction, StringComparison.Ordinal);
    }

    [Fact]
    public void A_recompute_keeps_the_offer_while_the_finding_stands_and_withdraws_it_once_it_is_gone()
    {
        var issue = At(ArrangementIssueType.AnchorInsideHandShape, 5000);
        _vm.Offer(issue, _f.Arrangement, _f.Level, [issue]);

        _vm.Refresh([issue with { }], _f.Arrangement);
        Assert.True(_vm.CanFix);

        _vm.Refresh([], _f.Arrangement);
        Assert.False(_vm.HasIssue);
        Assert.False(_vm.FixCommand.CanExecute(null));
    }

    private sealed class FakeDoor : IAiChatFindingsDoor
    {
        public List<AiChatInstructionRequest> Requests { get; } = [];

        public bool CanAsk => true;

        public string? PickedModelId => "picked-model";

        public event Action? ShowRequested { add { } remove { } }

        public Task<bool> AskAsync(AiChatInstructionRequest request)
        {
            Requests.Add(request);
            return Task.FromResult(true);
        }
    }
}
