using System.Globalization;
using System.Text.Json;
using System.Text.RegularExpressions;
using DotCharter.Tone.Ai;
using DotCharter.Services.Ai.Chart;
using DotCharter.Services.Ai.Chat;
using DotCharter.Services.Editing.Fingering;
using DotCharter.Tests.Ai.Chart;

namespace DotCharter.Tests.Ai.Chat;

/// <summary>
///     #803 in the chat: which job fixes a finding, the request the Issues tab sends for Fix with AI and
///     Explain with AI, the findings a pass or an Accept left in its range, and the follow-up that fixes
///     them. On a FAKE model: no network, no provider.
///     <para>
///         The fixture (<see cref="AiChartFixture" />): the intro (beats 0-15, a C5 hand shape over beats
///         8-13) and the verse (beats 16-31); one beat = 500 ms. The verse already has hand-position
///         findings (frets 7 and 8 under an anchor at fret 3, a C5 under the fret-1 anchor).
///     </para>
/// </summary>
public sealed partial class AiChatFindingsTests
{
    private readonly AiChartFixture _f = new();
    private readonly ProjectState _state = new();
    private readonly HistoryService _history = new(new FakeAppLogger());
    private readonly FakeLocalization _loc = FakeLocalization.English();
    private readonly RigConfig _config = new() { Ai = new AiProviderSettings(AiProviderKind.ClaudeCli, "fake-model", null) };

    public AiChatFindingsTests()
    {
        _state.ActiveProject = _f.Project;
        _state.ActiveArrangement = _f.Arrangement;
        _state.ActiveLevel = _f.Level;
    }

    [GeneratedRegex(@"--- chunk starts at (\d+)ms")]
    private static partial Regex ChunkStart();

    /// <summary>An echo of the chunk asked about, edited by <paramref name="edit" /> (told whether it is the retry).</summary>
    private FakeChartModel Model(Action<AiChartChunk, AiChartFixture.ReplyDoc, bool>? edit = null) => new((prompt, _) =>
    {
        var ms = int.Parse(ChunkStart().Match(prompt.Messages[0].Text).Groups[1].Value, CultureInfo.InvariantCulture);
        var chunk = _f.Chunks(AiChartFixture.PerPhrase).Single(c => c.StartMs == ms);
        var job = prompt.SchemaName switch
        {
            "dotcharter_fhp" => AiChartJob.FhpOnly,
            "dotcharter_fingering" => AiChartJob.FingeringOnly,
            _ => AiChartJob.FhpAndFingering
        };
        var reply = _f.EchoReply(chunk, job);
        edit?.Invoke(chunk, reply, prompt.Messages.Count == 3);
        return reply.Json();
    });

    private (AiChatInstructionRunner Runner, AiChartPassRun Run) Runner(IToneDesignModel model)
    {
        var run = new AiChartPassRun(_state, _history, null, _loc, new FakeAppLogger(), new FingeringGenerationService(),
            new Factory(model), () => _config, () => null)
        {
            ChunkOptions = AiChartFixture.PerPhrase
        };
        return (new AiChatInstructionRunner(run, _loc), run);
    }

    /// <summary>The chart's I20 after an anchor is put on the C5 at beat 10, inside the 8-13 shape.</summary>
    private ArrangementIssue AnchorInsideShape()
    {
        var anchor = new FretAnchor(AiChartFixture.At(10), 3, 4);
        _f.Level.Anchors.Add(anchor);
        return new ArrangementIssue(ArrangementIssueType.AnchorInsideHandShape,
            "Anchor is placed inside a hand shape — this can break the hand shape rendering.", 5000, Anchor: anchor,
            HandShape: _f.Level.HandShapes[0]);
    }

    private static string IdAt(AiChartChunk chunk, int beat) => chunk.Core.Single(s => s.Position == AiChartFixture.At(beat)).Id;

    [Theory]
    [InlineData(ArrangementIssueType.AnchorInsideHandShape, AiChartJob.FhpOnly)]
    [InlineData(ArrangementIssueType.AnchorMissingAtPhraseStart, AiChartJob.FhpOnly)]
    [InlineData(ArrangementIssueType.FingerSetForOpenString, AiChartJob.FingeringOnly)]
    [InlineData(ArrangementIssueType.BarreOverOpenStrings, AiChartJob.FingeringOnly)]
    [InlineData(ArrangementIssueType.IndexFingerNotOnAnchorFret, AiChartJob.FhpAndFingering)]
    public void A_finding_is_fixed_by_the_job_that_changes_what_its_rule_reads(ArrangementIssueType type, AiChartJob job)
        => Assert.Equal(job, AiChatFindings.JobFor(type));

    [Theory]
    [InlineData(ArrangementIssueType.OverlappingNotes)]
    [InlineData(ArrangementIssueType.LinkNextFretMismatch)]
    public void A_note_finding_has_no_job(ArrangementIssueType type)
    {
        Assert.Null(AiChatFindings.JobFor(type));
        Assert.False(AiChatFindings.CanFix(new ArrangementIssue(type, "x", 1000)));
    }

    [Fact]
    public void Anchor_and_finger_findings_together_take_the_joint_job()
        => Assert.Equal(AiChartJob.FhpAndFingering, AiChatFindings.JobFor([
            new ArrangementIssue(ArrangementIssueType.AnchorInsideHandShape, "a", 1),
            new ArrangementIssue(ArrangementIssueType.FingerSetForOpenString, "b", 2)
        ]));

    [Fact]
    public void A_fix_request_names_the_finding_its_rule_and_its_phrase_and_targets_it()
    {
        var issue = AnchorInsideShape();

        var request = AiChatFindings.FixRequest([issue], _f.Arrangement, _f.Level, _f.Beats, _loc)!;

        Assert.Equal(AiChartJob.FhpOnly, request.Job);
        Assert.Equal(new AiChartFindingTarget("I20", 5000), Assert.Single(request.Targets!));
        Assert.Equal(AiChartFixture.At(0), request.Scope.From);   // the intro's marker …
        Assert.Equal(AiChartFixture.At(14), request.Scope.To);    // … to its last sound
        Assert.StartsWith(_loc["AiChat_Fix_Header"], request.Instruction, StringComparison.Ordinal);
        Assert.Contains("I20", request.Instruction, StringComparison.Ordinal);
        Assert.Contains(issue.Message, request.Instruction, StringComparison.Ordinal);
        Assert.Contains(AiChartPrompt.RuleText("I20"), request.Instruction, StringComparison.Ordinal);
    }

    [Fact]
    public void A_finding_no_job_can_fix_gets_no_fix_request()
        => Assert.Null(AiChatFindings.FixRequest([new ArrangementIssue(ArrangementIssueType.OverlappingNotes, "overlap", 8000)],
            _f.Arrangement, _f.Level, _f.Beats, _loc));

    [Fact]
    public async Task Fix_with_AI_holds_a_proposal_that_removes_the_finding_and_the_accept_reports_what_is_left()
    {
        var issue = AnchorInsideShape();
        // The first answer restates the chart (I20 stays) and is refused; the retry drops the anchor.
        var model = Model((chunk, reply, retry) =>
        {
            if (retry) reply.Anchors.RemoveAll(a => a.At == IdAt(chunk, 10));
        });
        var (runner, run) = Runner(model);
        var request = AiChatFindings.FixRequest([issue], _f.Arrangement, _f.Level, _f.Beats, _loc)!;
        AiChatApplied? applied = null;
        runner.Applied += (_, e) => applied = e;

        var reply = await runner.RunInstructionAsync(request, CancellationToken.None);

        // Only the finding's phrase is asked about, and nothing is written until Accept.
        Assert.All(model.Prompts, p => Assert.Contains("--- chunk starts at 0ms", p.Messages[0].Text, StringComparison.Ordinal));
        Assert.Equal(AiChartRunStatus.Held, reply.Status);
        Assert.Contains(_f.Level.Anchors, a => a.Position == AiChartFixture.At(10));
        var review = reply.Review!;

        review.AcceptAll();
        Assert.True(run.ApplyReview());

        Assert.DoesNotContain(_f.Level.Anchors, a => a.Position == AiChartFixture.At(10));
        Assert.True(_history.CanUndo);
        _history.Undo();
        Assert.Contains(_f.Level.Anchors, a => a.Position == AiChartFixture.At(10));   // one undo step
        Assert.NotNull(applied);
        Assert.Equal(review.Id, applied!.ReviewId);
        Assert.Equal(_loc["AiChat_Leftovers_None"], applied.Text);   // the intro is clean now
        Assert.Null(applied.FollowUp);
    }

    [Fact]
    public async Task A_pass_that_holds_nothing_lists_the_findings_left_and_offers_to_fix_them()
    {
        var (runner, _) = Runner(Model());

        var reply = await runner.RunInstructionAsync(
            new AiChatInstructionRequest("Check the hand positions.", AiChatScope.WholeArrangement()), CancellationToken.None);

        Assert.Null(reply.Review);
        Assert.Contains("findings are still left in this range", reply.Text, StringComparison.Ordinal);
        Assert.Contains("• L05", reply.Text, StringComparison.Ordinal);
        var followUp = reply.FollowUp!;
        Assert.Equal(AiChartJob.FhpAndFingering, followUp.Job);
        Assert.Contains(followUp.Targets!, t => t.Code == "L05");
        Assert.StartsWith(_loc["AiChat_Fix_Header"], followUp.Instruction, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Explain_with_AI_answers_in_words_and_writes_nothing()
    {
        var issue = AnchorInsideShape();
        var model = new FakeChartModel((_, _) => JsonSerializer.Serialize(new { answer = "The anchor splits the shape." }));
        var (runner, run) = Runner(model);
        var before = ChartProjectCloner.Clone(_f.Project);
        var request = AiChatFindings.ExplainRequest(issue, _f.Arrangement, _f.Level, _f.Beats, _loc)!;

        var reply = await runner.RunInstructionAsync(request, CancellationToken.None);

        Assert.Equal(AiChartJob.Explain, request.Job);
        Assert.Equal(AiChartRunStatus.Answered, reply.Status);
        Assert.Equal("The anchor splits the shape.", reply.Text);
        Assert.Null(reply.Review);
        Assert.Null(run.Review);
        var prompt = Assert.Single(model.Prompts).Messages[0].Text;
        Assert.Contains("I20", prompt, StringComparison.Ordinal);
        Assert.Contains(AiChartPrompt.RuleText("I20"), prompt, StringComparison.Ordinal);
        var diffs = DeepCompare.Diff("project", before, _f.Project, new HashSet<string>());
        Assert.True(diffs.Count == 0, "Explain changed the chart:\n" + string.Join("\n", diffs.Take(20)));
        Assert.False(_history.CanUndo);
    }

    private sealed class Factory(IToneDesignModel model) : IToneDesignModelFactory
    {
        public IToneDesignModel Create(AiProviderSettings settings) => model;

        public bool Supports(AiProviderKind kind) => kind != AiProviderKind.None;
    }
}
