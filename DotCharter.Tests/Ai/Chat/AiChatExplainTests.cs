using System.Text.Json;
using DotCharter.Tone.Ai;
using DotCharter.Services.Ai.Chart;
using DotCharter.Services.Ai.Chat;
using DotCharter.Services.Editing.Fingering;
using DotCharter.Services.Editing.Quality;
using DotCharter.Tests.Ai.Chart;

namespace DotCharter.Tests.Ai.Chat;

/// <summary>
///     Ask / explain (#563): a question in the AI chat ("why 1·3 here?", "what's wrong with bar 6?")
///     gets a TEXT answer from the scope's listing and the chart quality report's findings in it —
///     no proposals, no review card, never a chart write; a reply that carries proposals anyway is
///     answered with its text alone. On a FAKE model and fake rules: no network, no provider.
///     <para>
///         The fixture (<see cref="AiChartFixture" />) is 8 bars of 4 beats at 500 ms a beat: the
///         intro phrase on bars 1-4, the verse on bars 5-8 (C5 on beats 16 18 26, Em on 24).
///     </para>
/// </summary>
public sealed class AiChatExplainTests
{
    private const string Question = "Why is the hand on fret 3 in the verse, and what's wrong with bar 6?";
    private const string Answer = "Bar 5 opens on a C5 power chord: index on fret 3, so the anchor sits on 3.";
    private const string VerseFinding = "VERSE-FINDING the note in bar 6 leaves the anchor";
    private const string IntroFinding = "INTRO-FINDING the note in bar 1 leaves the anchor";

    private readonly AiChartFixture _f = new();
    private readonly ProjectState _state = new();
    private readonly HistoryService _history = new(new FakeAppLogger());
    private readonly FakeLocalization _loc = FakeLocalization.English();
    private readonly RigConfig _config = new() { Ai = new AiProviderSettings(AiProviderKind.ClaudeCli, "fake-model", null) };

    public AiChatExplainTests()
    {
        _state.ActiveProject = _f.Project;
        _state.ActiveArrangement = _f.Arrangement;
        _state.ActiveLevel = _f.Level;
    }

    private static string Reply(object body) => JsonSerializer.Serialize(body);

    private (AiChatInstructionRunner Runner, AiChartPassRun Run) Runner(FakeChartModel model) =>
        Runner(model, out _);

    private (AiChatInstructionRunner Runner, AiChartPassRun Run) Runner(FakeChartModel model, out FakeRules rules)
    {
        rules = new FakeRules();
        var run = new AiChartPassRun(_state, _history, null, _loc, new FakeAppLogger(), new FingeringGenerationService(),
            new Factory(model), () => _config, () => null)
        {
            ChunkOptions = AiChartFixture.PerPhrase,
            Quality = new ChartQualityReporter(rules)
        };
        return (new AiChatInstructionRunner(run, _loc), run);
    }

    private AiChatScope Verse() => AiChatScope.SectionAt(_f.Arrangement, _f.Level, AiChartFixture.At(20))!;

    private static AiChatInstructionRequest Ask(AiChatScope scope, AiChatConversation? chat = null) =>
        new(Question, scope, chat, Job: AiChartJob.Explain);

    private void AssertChartUntouched(ChartProject before)
    {
        var diffs = DeepCompare.Diff("project", before, _f.Project, new HashSet<string>());
        Assert.True(diffs.Count == 0, "the question changed the chart:\n" + string.Join("\n", diffs.Take(20)));
        Assert.False(_history.CanUndo);
    }

    [Fact]
    public async Task A_question_gets_a_text_answer_and_no_review_card_and_the_chart_is_not_touched()
    {
        var model = new FakeChartModel((_, _) => Reply(new { answer = Answer }));
        var (runner, run) = Runner(model);
        var before = ChartProjectCloner.Clone(_f.Project);
        var reviews = 0;
        run.ReviewChanged += (_, _) => reviews++;

        var reply = await runner.RunInstructionAsync(Ask(Verse()), CancellationToken.None);

        Assert.Equal(AiChartRunStatus.Answered, reply.Status);
        Assert.Equal(Answer, reply.Text);
        Assert.Null(reply.Review);
        Assert.Null(run.Review);
        Assert.Equal(0, reviews);
        Assert.Equal(new AiChatUsage(100, 50), reply.Usage);
        var turn = reply.ToTurn(DateTimeOffset.UnixEpoch);
        Assert.Equal(AiChatRole.Assistant, turn.Role);
        Assert.Null(turn.ReviewId);
        AssertChartUntouched(before);

        // ONE request, on the explain job's own prompt: never a chunked FHP or a structure one.
        var prompt = Assert.Single(model.Prompts);
        Assert.Equal(AiExplainPrompt.SchemaName, prompt.SchemaName);
        Assert.Equal(AiExplainPrompt.Schema, prompt.JsonSchema);
        Assert.Equal(AiExplainPrompt.System, prompt.System);
        Assert.False(run.IsRunning);
    }

    [Fact]
    public async Task The_prompt_holds_the_scope_listing_its_sections_and_its_quality_findings_and_the_question()
    {
        var model = new FakeChartModel((_, _) => Reply(new { answer = Answer }));
        var (runner, _) = Runner(model, out var rules);
        var at = new DateTimeOffset(2026, 10, 10, 9, 0, 0, TimeSpan.Zero);
        var chat = AiChatConversation.Start("chart", "fake-model", at)
            .Append(new AiChatTurn(AiChatRole.User, "Is the verse playable?", at))
            .Append(new AiChatTurn(AiChatRole.Assistant, "Yes, in 3rd position.", at.AddMinutes(1)));

        await runner.RunInstructionAsync(Ask(Verse(), chat), CancellationToken.None);

        var text = Assert.Single(model.Prompts).Messages[0].Text.ReplaceLineEndings("\n");
        // The scope's chart: anchors, notes, and each chord's frets, fingers and name.
        Assert.Contains("--- chunk starts at 8000ms", text, StringComparison.Ordinal);
        Assert.Contains("anchor 8000ms fret 3 width 4", text, StringComparison.Ordinal);
        Assert.Contains("chord frets [-1,3,5,5,-1,-1] fingers [-1,1,3,4,-1,-1] name \"C5\"", text, StringComparison.Ordinal);
        Assert.Contains("chord frets [0,2,2,0,0,0] fingers [-1,2,3,-1,-1,-1] name \"Emin\"", text, StringComparison.Ordinal);
        Assert.Contains("note string 3 fret 6", text, StringComparison.Ordinal);
        Assert.DoesNotContain("[fix]", text, StringComparison.Ordinal);
        // Its sections and phrases, and the bars it spans with their times.
        Assert.Contains(AiExplainPrompt.MarkersHeader + "\nphrase \"intro\" at bar 1 beat 1 (0:00.0)\nphrase \"verse\" at bar 5 beat 1 (0:08.0)",
            text, StringComparison.Ordinal);
        Assert.Contains("bar 6 at 10000ms (0:10.0), 4 beats", text, StringComparison.Ordinal);

        // The quality report's findings IN THE SCOPE: the verse's, not the intro's.
        Assert.True(rules.Calls > 0, "the quality report never ran");
        var findings = text[text.IndexOf(AiExplainPrompt.FindingsHeader, StringComparison.Ordinal)..];
        Assert.Contains(VerseFinding, findings, StringComparison.Ordinal);
        Assert.Contains("at 0:10.5 bar 6: " + VerseFinding, findings, StringComparison.Ordinal);
        Assert.DoesNotContain(IntroFinding, text, StringComparison.Ordinal);

        // The question last, with the conversation before it.
        Assert.Contains(AiExplainPrompt.QuestionHeader + "\n" + Question, text, StringComparison.Ordinal);
        Assert.Contains("charter: Is the verse playable?", text, StringComparison.Ordinal);
        Assert.Contains("assistant: Yes, in 3rd position.", text, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Over_the_whole_arrangement_every_finding_is_in_the_prompt()
    {
        var model = new FakeChartModel((_, _) => Reply(new { answer = Answer }));
        var (runner, _) = Runner(model);

        await runner.RunInstructionAsync(Ask(AiChatScope.WholeArrangement()), CancellationToken.None);

        var text = Assert.Single(model.Prompts).Messages[0].Text;
        Assert.Contains(VerseFinding, text, StringComparison.Ordinal);
        Assert.Contains(IntroFinding, text, StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_reply_carrying_proposals_is_answered_with_its_text_alone_and_nothing_is_held()
    {
        var model = new FakeChartModel((_, _) => Reply(new
        {
            answer = Answer,
            anchors = new[] { new { at = "start", fret = 7, width = 4 } },
            chords = new[] { new { id = "s0", ms = 0, frets = new[] { -1, 3, 5, 5, -1, -1 }, fingers = new[] { -1, 1, 1, 1, -1, -1 } } },
            changes = new[] { new { op = "add", kind = "section", bar = 5, beat = 1, name = "chorus" } }
        }));
        var (runner, run) = Runner(model);
        var before = ChartProjectCloner.Clone(_f.Project);

        var reply = await runner.RunInstructionAsync(Ask(AiChatScope.WholeArrangement()), CancellationToken.None);

        Assert.Equal(AiChartRunStatus.Answered, reply.Status);
        Assert.Equal(Answer, reply.Text);
        Assert.Null(reply.Review);
        Assert.Null(run.Review);
        Assert.False(run.ApplyReview());
        AssertChartUntouched(before);
    }

    [Fact]
    public async Task A_question_does_not_discard_the_card_waiting_on_the_same_pass()
    {
        var calls = 0;
        var model = new FakeChartModel((prompt, _) =>
        {
            if (prompt.SchemaName == AiExplainPrompt.SchemaName) return Reply(new { answer = Answer, anchors = Array.Empty<object>() });
            Interlocked.Increment(ref calls);
            var chunk = _f.Chunks(AiChartFixture.PerPhrase).Single(c =>
                prompt.Messages[0].Text.Contains($"--- chunk starts at {c.StartMs}ms", StringComparison.Ordinal));
            var doc = _f.EchoReply(chunk);
            AiChartReviewTests.Proposal(chunk, doc);
            return doc.Json();
        });
        var (runner, run) = Runner(model);
        await runner.RunInstructionAsync(new AiChatInstructionRequest("Use 1·3 here.", AiChatScope.WholeArrangement()),
            CancellationToken.None);
        var waiting = run.Review;
        Assert.NotNull(waiting);
        var before = ChartProjectCloner.Clone(_f.Project);

        var reply = await runner.RunInstructionAsync(Ask(Verse()), CancellationToken.None);

        Assert.Equal(AiChartRunStatus.Answered, reply.Status);
        Assert.Equal(Answer, reply.Text);
        Assert.Null(reply.Review);
        Assert.Same(waiting, run.Review);
        Assert.True(calls > 0);
        var diffs = DeepCompare.Diff("project", before, _f.Project, new HashSet<string>());
        Assert.Empty(diffs);
    }

    [Theory]
    [InlineData("{\"anchors\":[{\"at\":\"start\",\"fret\":7,\"width\":4}]}")]
    [InlineData("{\"answer\":\"   \"}")]
    [InlineData("[1,2]")]
    public async Task A_reply_without_an_answer_says_so_and_holds_nothing(string json)
    {
        var (runner, run) = Runner(new FakeChartModel((_, _) => json));
        var before = ChartProjectCloner.Clone(_f.Project);

        var reply = await runner.RunInstructionAsync(Ask(Verse()), CancellationToken.None);

        Assert.Equal(AiChartRunStatus.Failed, reply.Status);
        Assert.Equal(_loc["AiChat_ExplainNoAnswer"], reply.Text);
        Assert.Null(reply.Review);
        Assert.Null(run.Review);
        AssertChartUntouched(before);
    }

    [Fact]
    public void The_reader_takes_the_answer_and_flags_anything_else_as_ignored()
    {
        Assert.True(AiExplainPrompt.TryReadAnswer("{\"answer\":\" Because. \"}", out var answer, out var proposed));
        Assert.Equal("Because.", answer);
        Assert.False(proposed);

        Assert.True(AiExplainPrompt.TryReadAnswer("{\"answer\":\"Because.\",\"changes\":[]}", out answer, out proposed));
        Assert.Equal("Because.", answer);
        Assert.True(proposed);

        // A provider that answered in plain prose: the prose is the answer.
        Assert.True(AiExplainPrompt.TryReadAnswer("Because the index sits on fret 3.", out answer, out _));
        Assert.Equal("Because the index sits on fret 3.", answer);
        Assert.False(AiExplainPrompt.TryReadAnswer("{\"answer\": ", out _, out _));
    }

    /// <summary>The arrangement rules, faked: one finding in the intro (bar 1) and one in the verse
    /// (bar 6), on the notes of whatever copy of the chart they are asked about.</summary>
    private sealed class FakeRules : IArrangementValidationService
    {
        public int Calls { get; private set; }

        public IReadOnlyList<ArrangementIssue> Validate(Arrangement? arrangement, IList<BeatMarker> beats,
            double? totalAudioLengthMs = null, ChartProject? project = null)
        {
            Calls++;
            if (arrangement is null) return [];
            var level = arrangement.Levels[0];
            var intro = level.Notes.Single(n => n.Position == AiChartFixture.At(2));
            var verse = level.Notes.Single(n => n.Position == AiChartFixture.At(21));
            return
            [
                new ArrangementIssue(ArrangementIssueType.NoteOutsideAnchorRange, IntroFinding, 1000, Note: intro),
                new ArrangementIssue(ArrangementIssueType.NoteOutsideAnchorRange, VerseFinding, 10500, Note: verse)
            ];
        }
    }

    private sealed class Factory(IToneDesignModel model) : IToneDesignModelFactory
    {
        public IToneDesignModel Create(AiProviderSettings settings) => model;

        public bool Supports(AiProviderKind kind) => kind != AiProviderKind.None;
    }
}
