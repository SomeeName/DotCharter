using System.Globalization;
using System.Text.Json;
using System.Text.RegularExpressions;
using DotCharter.Tone.Ai;
using DotCharter.Services.Ai.Chart;
using DotCharter.Services.Ai.Chat;
using DotCharter.Services.Editing.Fingering;
using DotCharter.Services.Editing.Quality;
using DotCharter.Tests.Ai.Chart;

namespace DotCharter.Tests.Ai.Chat;

/// <summary>
///     #808, the three parts #803 left out, on a FAKE model (no network, no provider): phrase findings fixed by
///     the sections &amp; phrases job with the WHOLE proposal judged against the targets; a typed FHP / fingering
///     message taking the scope's findings as targets; a menu verb's Accept reporting what is left in the chat.
///     <para>
///         The fixture (<see cref="AiChartFixture" />): phrases "intro" (beat 0) and "verse" (beat 16), no END
///         phrase (I26), one beat = 500 ms. The verse has hand-position findings (L05) from the start.
///     </para>
/// </summary>
public sealed partial class AiChatFindingTargetsTests
{
    private readonly AiChartFixture _f = new();
    private readonly ProjectState _state = new();
    private readonly HistoryService _history = new(new FakeAppLogger());
    private readonly FakeLocalization _loc = FakeLocalization.English();
    private readonly RigConfig _config = new() { Ai = new AiProviderSettings(AiProviderKind.ClaudeCli, "fake-model", null) };
    private readonly ChartQualityReporter _reporter = new(new ArrangementValidationService(new FakeAppLogger()));

    public AiChatFindingTargetsTests()
    {
        _state.ActiveProject = _f.Project;
        _state.ActiveArrangement = _f.Arrangement;
        _state.ActiveLevel = _f.Level;
    }

    [GeneratedRegex(@"--- chunk starts at (\d+)ms")]
    private static partial Regex ChunkStart();

    private (AiChatInstructionRunner Runner, AiChartPassRun Run) Runner(IToneDesignModel model)
    {
        var run = new AiChartPassRun(_state, _history, null, _loc, new FakeAppLogger(), new FingeringGenerationService(),
            new Factory(model), () => _config, () => null)
        {
            ChunkOptions = AiChartFixture.PerPhrase,
            Quality = _reporter
        };
        return (new AiChatInstructionRunner(run, _loc), run);
    }

    /// <summary>An echo of the chunk asked about, edited by <paramref name="edit" />.</summary>
    private FakeChartModel Echo(Action<AiChartChunk, AiChartFixture.ReplyDoc>? edit = null) => new((prompt, _) =>
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
        edit?.Invoke(chunk, reply);
        return reply.Json();
    });

    private static string Structure(params object[] changes) => JsonSerializer.Serialize(new { changes });

    private static object Phrase(int bar, int beat, string name) => new { op = "add", kind = "phrase", bar, beat, name };

    private static ArrangementIssue NoEnd() =>
        new(ArrangementIssueType.NoEndPhrase, "The arrangement is missing an END phrase.");

    // ---- 1. phrase findings: the sections & phrases job, the whole proposal judged ----

    [Theory]
    [InlineData(ArrangementIssueType.EmptyPhrase)]
    [InlineData(ArrangementIssueType.PhraseChangeOnLinkNextNote)]
    [InlineData(ArrangementIssueType.FirstPhraseNotEmpty)]
    [InlineData(ArrangementIssueType.NoEndPhrase)]
    public void A_phrase_finding_is_fixed_by_the_sections_and_phrases_job(ArrangementIssueType type)
    {
        Assert.Equal(AiChartJob.SectionsAndPhrases, AiChatFindings.JobFor(type));
        Assert.True(AiChatFindings.CanFix(new ArrangementIssue(type, "x")));   // untimed I26 included
        Assert.Null(AiChatFindings.JobFor([
            new ArrangementIssue(type, "x", 1000),
            new ArrangementIssue(ArrangementIssueType.AnchorInsideHandShape, "y", 2000)
        ]));
    }

    [Fact]
    public async Task Fix_with_AI_on_a_phrase_finding_refuses_a_proposal_that_leaves_it_and_holds_the_one_that_removes_it()
    {
        var request = AiChatFindings.FixRequest([NoEnd()], _f.Arrangement, _f.Level, _f.Beats, _loc)!;
        Assert.Equal(AiChartJob.SectionsAndPhrases, request.Job);
        Assert.Null(request.Scope.From);   // the whole arrangement
        Assert.Null(request.Scope.To);
        Assert.Equal(new AiChartFindingTarget("I26", null), Assert.Single(request.Targets!));

        // The first proposal is clean change by change but leaves I26; the retry adds the END phrase.
        var model = new FakeChartModel((_, call) => call == 0
            ? Structure(Phrase(3, 1, "riff"))
            : Structure(Phrase(3, 1, "riff"), Phrase(8, 1, "END")));
        var (runner, run) = Runner(model);

        var reply = await runner.RunInstructionAsync(request, CancellationToken.None);

        Assert.Equal(2, model.Calls);
        var retry = model.Prompts[1].Messages[^1].Text;
        Assert.Contains(AiChartRejectionCodes.FindingLeft, retry, StringComparison.Ordinal);
        Assert.Contains("I26", retry, StringComparison.Ordinal);
        Assert.Equal(AiChartRunStatus.Held, reply.Status);
        Assert.Equal(2, run.Review!.Items.Count);
    }

    [Fact]
    public async Task A_phrase_fix_whose_retry_still_leaves_the_target_holds_nothing()
    {
        var before = _f.Arrangement.EventPoints.Count;
        var request = AiChatFindings.FixRequest([NoEnd()], _f.Arrangement, _f.Level, _f.Beats, _loc)!;
        var model = new FakeChartModel((_, _) => Structure(Phrase(3, 1, "riff")));
        var (runner, run) = Runner(model);

        var reply = await runner.RunInstructionAsync(request, CancellationToken.None);

        Assert.Equal(2, model.Calls);
        Assert.Equal(AiChartRunStatus.AllRejected, reply.Status);
        Assert.Null(run.Review);
        Assert.Contains("I26", reply.Text, StringComparison.Ordinal);
        Assert.Equal(before, _f.Arrangement.EventPoints.Count);
    }

    // ---- 2. a typed FHP / fingering message takes the scope's findings as targets ----

    [Fact]
    public async Task A_typed_fhp_message_takes_the_scopes_findings_as_targets()
    {
        var model = Echo();
        var (runner, _) = Runner(model);

        var reply = await runner.RunInstructionAsync(new AiChatInstructionRequest("Smooth the hand positions.",
            AiChatScope.WholeArrangement(), Job: AiChartJob.FhpOnly), CancellationToken.None);

        // The model is told the findings after the charter's words …
        var verse = model.Prompts.First(p => p.Messages[0].Text.Contains("--- chunk starts at 8000ms", StringComparison.Ordinal));
        Assert.Contains("The chart checker flags these findings", verse.Messages[0].Text, StringComparison.Ordinal);
        Assert.Contains("- L05 at", verse.Messages[0].Text, StringComparison.Ordinal);
        // … and an echo that leaves them is refused and asked again with the finding named.
        Assert.Contains(model.Prompts, p => p.Messages.Count == 3 &&
                                            p.Messages[^1].Text.Contains(AiChartRejectionCodes.FindingLeft, StringComparison.Ordinal));
        Assert.Contains("were added as targets", reply.Text, StringComparison.Ordinal);
    }

    [GeneratedRegex(@"^- ([A-Z]\d\d) at ", RegexOptions.Multiline)]
    private static partial Regex TargetLine();

    [Fact]
    public async Task A_typed_fingering_message_takes_only_the_findings_fingering_can_fix()
    {
        var model = Echo();
        var (runner, _) = Runner(model);

        await runner.RunInstructionAsync(new AiChatInstructionRequest("Use the pinky less.",
            AiChatScope.WholeArrangement(), Job: AiChartJob.FingeringOnly), CancellationToken.None);

        var codes = model.Prompts.SelectMany(p => TargetLine().Matches(p.Messages[0].Text)).Select(m => m.Groups[1].Value)
            .Distinct().ToList();
        Assert.DoesNotContain("L05", codes);   // a hand-position finding: not the fingering job's
        Assert.All(codes, code => Assert.True(AiChatFindings.Fixes(AiChartJob.FingeringOnly,
            AiChartRuleGate.Guarded.Single(t => t.GetCode() == code)), code));
    }

    [Theory]
    [InlineData(AiChartJob.FhpOnly, ArrangementIssueType.NoteOutsideAnchorRange, true)]
    [InlineData(AiChartJob.FhpOnly, ArrangementIssueType.FingerSetForOpenString, false)]
    [InlineData(AiChartJob.FingeringOnly, ArrangementIssueType.IndexFingerNotOnAnchorFret, true)]
    [InlineData(AiChartJob.FingeringOnly, ArrangementIssueType.AnchorInsideHandShape, false)]
    [InlineData(AiChartJob.FhpAndFingering, ArrangementIssueType.FingerSetForOpenString, true)]
    [InlineData(AiChartJob.FhpAndFingering, ArrangementIssueType.EmptyPhrase, false)]
    [InlineData(AiChartJob.SectionsAndPhrases, ArrangementIssueType.EmptyPhrase, true)]
    public void A_job_fixes_its_own_findings_and_the_joint_job_both(AiChartJob job, ArrangementIssueType type, bool fixes)
        => Assert.Equal(fixes, AiChatFindings.Fixes(job, type));

    // ---- 3. a menu verb's Accept reports what is left in the chat ----

    [Fact]
    public async Task A_menu_passes_accept_reports_the_findings_left_with_the_fix_on_offer()
    {
        // The intro's I20 is fixed by the menu pass; the verse's L05 stays.
        var anchor = new FretAnchor(AiChartFixture.At(10), 3, 4);
        _f.Level.Anchors.Add(anchor);
        var model = Echo((chunk, reply) =>
        {
            if (chunk.StartMs == 0) reply.Anchors.RemoveAll(a => a.At == chunk.Core.Single(s => s.Position == AiChartFixture.At(10)).Id);
        });
        var (runner, run) = Runner(model);
        AiChatApplied? applied = null;
        runner.Applied += (_, e) => applied = e;

        await run.RunAsync(AiChartJob.FhpOnly);
        run.Review!.AcceptAll();
        Assert.True(run.ApplyReview());

        Assert.NotNull(applied);
        Assert.True(applied!.FromMenu);
        Assert.StartsWith(_loc["AiChat_MenuPassApplied"], applied.Text, StringComparison.Ordinal);
        Assert.Contains("• L05", applied.Text, StringComparison.Ordinal);
        var followUp = applied.FollowUp!;
        Assert.True(followUp.Job.ProposesAnchors());   // L05 is a hand-position finding
        Assert.Contains(followUp.Targets!, t => t.Code == "L05");
    }

    [Fact]
    public async Task The_chats_own_accept_is_not_reported_as_a_menu_pass()
    {
        var anchor = new FretAnchor(AiChartFixture.At(10), 3, 4);
        _f.Level.Anchors.Add(anchor);
        var model = Echo((chunk, reply) =>
        {
            if (chunk.StartMs == 0) reply.Anchors.RemoveAll(a => a.At == chunk.Core.Single(s => s.Position == AiChartFixture.At(10)).Id);
        });
        var (runner, run) = Runner(model);
        AiChatApplied? applied = null;
        runner.Applied += (_, e) => applied = e;

        var reply = await runner.RunInstructionAsync(new AiChatInstructionRequest("Fix the intro.",
            new AiChatScope(AiChatScopeKind.Section, _f.Arrangement, AiChartFixture.At(0), AiChartFixture.At(14)),
            Job: AiChartJob.FhpOnly), CancellationToken.None);
        reply.Review!.AcceptAll();
        Assert.True(run.ApplyReview());

        Assert.NotNull(applied);
        Assert.False(applied!.FromMenu);
        Assert.Equal(reply.Review.Id, applied.ReviewId);
    }

    private sealed class Factory(IToneDesignModel model) : IToneDesignModelFactory
    {
        public IToneDesignModel Create(AiProviderSettings settings) => model;

        public bool Supports(AiProviderKind kind) => kind != AiProviderKind.None;
    }
}
