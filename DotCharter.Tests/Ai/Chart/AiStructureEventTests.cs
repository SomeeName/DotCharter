using System.Text.Json;
using DotCharter.Services.Ai.Chart;
using DotCharter.Services.Ai.Chat;
using DotCharter.Services.Editing.Fingering;
using DotCharter.Services.Editing.Quality;
using DotCharter.Tone.Ai;

namespace DotCharter.Tests.Ai.Chart;

/// <summary>
///     #481: the AI sections and phrases job sets every field of the Edit event / phrase dialog (section, status,
///     phrase, solo, tone, the Rocksmith event codes, "Apply to arrangements") through the dialog's own edit path
///     (<c>EventMarkerEdit</c>) — ONE shared change over the chosen arrangements, not a copy per arrangement.
/// </summary>
public sealed class AiStructureEventTests
{
    private const string EventHeader = "--- event markers ---";

    private readonly AiChartFixture _f = new();
    private readonly ProjectState _state = new();
    private readonly HistoryService _history = new(new FakeAppLogger());
    private readonly RigConfig _config = new() { Ai = new AiProviderSettings(AiProviderKind.ClaudeCli, "fake-model", null) };
    private readonly ChartQualityReporter _reporter = new(new ArrangementValidationService(new FakeAppLogger()));
    private readonly Arrangement _rhythm;

    public AiStructureEventTests()
    {
        _rhythm = ChartProjectCloner.CloneArrangement(_f.Arrangement);
        _rhythm.Name = "Rhythm";
        _f.Project.Arrangements.Add(_rhythm);
        // A tone the chart declares and defines: the checks refuse a tone change to one it does not.
        _f.Arrangement.ToneBase = "clean";
        _f.Arrangement.ToneA = "clean";
        _f.Project.ToneDefinitions.Add(new ToneDefinition { Key = "clean", Name = "Clean", Json = "{}" });
        _state.ActiveProject = _f.Project;
        _state.ActiveArrangement = _f.Arrangement;
        _state.ActiveLevel = _f.Level;
    }

    private static string Event(int bar, int beat, object fields) => JsonSerializer.Serialize(new
    {
        changes = new[] { new { op = "event", kind = (string?)null, id = (string?)null, bar, beat, name = (string?)null, @event = fields } }
    });

    private static object Fields(string? section = null, string? status = null, string? phrase = null, bool? solo = null,
        string? tone = null, string[]? events = null, string[]? arrangements = null)
        => new { section, status, phrase, solo, tone, events, arrangements };

    private AiChartPassRun Run(FakeChartModel model) =>
        new(_state, _history, null, FakeLocalization.English(), new FakeAppLogger(), new FingeringGenerationService(),
            new Factory(model), () => _config, () => null)
        {
            Quality = _reporter
        };

    private static AiChartInstructedRun Instruct(params Arrangement[] set)
        => new(AiChartJob.SectionsAndPhrases, new AiChartInstruction("Mark the chorus."), set[0])
        {
            EventArrangements = set
        };

    private static bool Accept(AiChartPassRun run)
    {
        foreach (var item in run.Review!.Items) run.Review.Decide(item, AiChartDecision.Accepted);
        return run.ApplyReview();
    }

    private static EventPoint? At(Arrangement a, int beat) => a.EventPoints.FirstOrDefault(e => e.Position.BeatId == beat);

    [Fact]
    public async Task Every_dialog_field_reaches_the_marker_the_dialog_would_write()
    {
        var model = new FakeChartModel((_, _) => Event(3, 1, Fields("chorus", "draft", "riff", true, null, ["B0", "e1"], ["Lead"])));
        var run = Run(model);

        var outcome = await run.RunInstructionAsync(Instruct(_f.Arrangement), TestContext.Current.CancellationToken);
        Assert.True(outcome.Status == AiChartRunStatus.Held, string.Join("; ", outcome.Result?.Rejected.Select(r => r.Code + ": " + r.Reason) ?? []));
        Assert.True(Accept(run));

        var marker = At(_f.Arrangement, 8)!;
        Assert.Equal("chorus", marker.Section);
        Assert.Equal(SectionStatus.Draft, marker.Status);
        Assert.Equal("riff", marker.Phrase);
        Assert.Equal("B0,e1", marker.Events);
        Assert.True(_f.Arrangement.PhraseInfos["riff"].Solo);
        Assert.Null(At(_rhythm, 8));
    }

    [Fact]
    public async Task A_tone_lands_as_a_tone_change_at_the_beat_like_the_dialog_places_it()
    {
        // Beat 9 holds no note (the checks refuse a tone change ON one: I30).
        var run = Run(new FakeChartModel((_, _) => Event(3, 2, Fields(tone: "clean"))));
        await run.RunInstructionAsync(Instruct(_f.Arrangement), TestContext.Current.CancellationToken);

        Assert.True(Accept(run));

        var change = Assert.Single(_f.Arrangement.ToneChanges);
        Assert.Equal("clean", change.ToneName);
        Assert.Equal(9, change.Position.BeatId);
        Assert.Null(At(_f.Arrangement, 9));
    }

    [Fact]
    public async Task A_tone_change_onto_a_note_is_refused_by_the_checks()
    {
        var run = Run(new FakeChartModel((_, _) => Event(3, 1, Fields(tone: "clean"))));

        var outcome = await run.RunInstructionAsync(Instruct(_f.Arrangement), TestContext.Current.CancellationToken);

        Assert.NotEqual(AiChartRunStatus.Held, outcome.Status);
        Assert.Empty(_f.Arrangement.ToneChanges);
    }

    [Fact]
    public async Task A_field_left_null_keeps_what_the_marker_has_and_a_blank_clears_it()
    {
        _f.Arrangement.EventPoints.Add(new EventPoint
        {
            Position = new FractionalPosition(8), Section = "verse", Phrase = "riff", Events = "B0,X9", Status = SectionStatus.Todo
        });
        var model = new FakeChartModel((_, _) => Event(3, 1, Fields(status: "done", events: ["e0"], phrase: "")));
        var run = Run(model);
        await run.RunInstructionAsync(Instruct(_f.Arrangement), TestContext.Current.CancellationToken);

        Assert.True(Accept(run));

        var marker = At(_f.Arrangement, 8)!;
        Assert.Equal("verse", marker.Section);
        Assert.Equal(SectionStatus.Done, marker.Status);
        Assert.Equal("", marker.Phrase);
        // The dialog keeps a code outside the catalogue verbatim; so does the AI.
        Assert.Equal("e0,X9", marker.Events);
    }

    [Fact]
    public async Task One_event_change_over_two_arrangements_is_ONE_review_row_and_ONE_undo_step()
    {
        var model = new FakeChartModel((_, _) => Event(3, 1, Fields("chorus", phrase: "riff")));
        var run = Run(model);
        await run.RunInstructionAsync(Instruct(_f.Arrangement, _rhythm), TestContext.Current.CancellationToken);

        var review = run.Review!;
        Assert.Single(review.Items);
        Assert.True(Accept(run));

        Assert.Equal("chorus", At(_f.Arrangement, 8)!.Section);
        Assert.Equal("chorus", At(_rhythm, 8)!.Section);
        Assert.Equal("riff", At(_rhythm, 8)!.Phrase);
        _history.Undo();
        Assert.Null(At(_f.Arrangement, 8));
        Assert.Null(At(_rhythm, 8));
    }

    [Fact]
    public async Task The_model_may_narrow_the_set_to_the_arrangements_it_names()
    {
        var model = new FakeChartModel((_, _) => Event(3, 1, Fields("chorus", arrangements: ["Rhythm"])));
        var run = Run(model);
        await run.RunInstructionAsync(Instruct(_f.Arrangement, _rhythm), TestContext.Current.CancellationToken);

        Assert.True(Accept(run));

        Assert.Null(At(_f.Arrangement, 8));
        Assert.Equal("chorus", At(_rhythm, 8)!.Section);
    }

    [Theory]
    [InlineData("section", "pre-chorus")]
    [InlineData("phrase", "no good!")]
    [InlineData("status", "finished")]
    [InlineData("events", "Z9")]
    [InlineData("arrangements", "Bass")]
    public async Task An_invalid_field_refuses_the_whole_reply(string field, string value)
    {
        var fields = field switch
        {
            "section" => Fields(section: value),
            "phrase" => Fields(phrase: value),
            "status" => Fields(status: value),
            "events" => Fields(events: [value]),
            _ => Fields(arrangements: [value])
        };
        var run = Run(new FakeChartModel((_, _) => Event(3, 1, fields)));

        var outcome = await run.RunInstructionAsync(Instruct(_f.Arrangement), TestContext.Current.CancellationToken);

        Assert.NotEqual(AiChartRunStatus.Held, outcome.Status);
        Assert.Null(run.Review);
        Assert.Null(At(_f.Arrangement, 8));
    }

    [Fact]
    public async Task A_section_off_the_bar_line_is_refused()
    {
        var run = Run(new FakeChartModel((_, _) => Event(3, 2, Fields("chorus"))));

        Assert.NotEqual(AiChartRunStatus.Held, (await run.RunInstructionAsync(Instruct(_f.Arrangement), TestContext.Current.CancellationToken)).Status);
    }

    [Fact]
    public async Task A_run_that_allows_no_event_edit_refuses_one_and_does_not_describe_it()
    {
        var model = new FakeChartModel((_, _) => Event(3, 1, Fields("chorus")));
        var run = Run(model);

        var outcome = await run.RunInstructionAsync(new AiChartInstructedRun(AiChartJob.SectionsAndPhrases,
            new AiChartInstruction("Mark."), _f.Arrangement), TestContext.Current.CancellationToken);

        Assert.NotEqual(AiChartRunStatus.Held, outcome.Status);
        Assert.DoesNotContain(EventHeader, model.Prompts[0].Messages[0].Text, StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_chat_request_over_two_arrangements_asks_for_the_event_once_and_writes_one_shared_marker()
    {
        var model = new FakeChartModel((prompt, _) => prompt.Messages[0].Text.Contains(EventHeader, StringComparison.Ordinal)
            ? Event(3, 1, Fields("chorus", phrase: "riff"))
            : JsonSerializer.Serialize(new { changes = Array.Empty<object>() }));
        var run = Run(model);
        var runner = new AiChatInstructionRunner(run, FakeLocalization.English());
        var request = new AiChatInstructionRequest("Add a chorus at bar 3.", AiChatScope.WholeArrangement(_f.Arrangement),
            null, null, AiChartJob.SectionsAndPhrases)
        {
            MoreScopes = [AiChatScope.WholeArrangement(_rhythm)]
        };

        await runner.RunInstructionAsync(request, TestContext.Current.CancellationToken);

        Assert.Equal(2, model.Calls);
        Assert.Equal(1, model.Prompts.Count(p => p.Messages[0].Text.Contains(EventHeader, StringComparison.Ordinal)));
        Assert.True(Accept(run));
        Assert.Equal("chorus", At(_f.Arrangement, 8)!.Section);
        Assert.Equal("chorus", At(_rhythm, 8)!.Section);
    }

    private sealed class Factory(IToneDesignModel model) : IToneDesignModelFactory
    {
        public IToneDesignModel Create(AiProviderSettings settings) => model;

        public bool Supports(AiProviderKind kind) => kind != AiProviderKind.None;
    }
}
