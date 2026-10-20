using System.Globalization;
using System.Text.RegularExpressions;
using DotCharter.Tone.Ai;
using DotCharter.Services.Ai.Chart;
using DotCharter.Services.Editing.Fingering;

namespace DotCharter.Tests.Ai.Chart;

/// <summary>
///     Tools ▸ AI ▸ Fix FHP &amp; fingering, as the user sees it (#497): the configured
///     provider or a message naming Settings ▸ AI; a status-rail task; ONE undo step; the
///     outcome on the rail's main line.
/// </summary>
public sealed partial class AiChartPassRunTests
{
    private readonly AiChartFixture _f = new();
    private readonly ProjectState _state = new();
    private readonly HistoryService _history = new(new FakeAppLogger());
    private readonly RecordingStatusBarService _rail = new();
    private readonly FakeLocalization _loc = FakeLocalization.English();
    private RigConfig _config = new() { Ai = new AiProviderSettings(AiProviderKind.ClaudeCli, "fake-model", null) };
    private (FractionalPosition, FractionalPosition)? _selection;

    public AiChartPassRunTests()
    {
        _state.ActiveProject = _f.Project;
        _state.ActiveArrangement = _f.Arrangement;
        _state.ActiveLevel = _f.Level;
    }

    [GeneratedRegex(@"--- chunk starts at (\d+)ms")]
    private static partial Regex ChunkStart();

    private FakeChartModel Model(Action<AiChartChunk, AiChartFixture.ReplyDoc>? edit = null)
        => new((prompt, _) =>
        {
            var ms = int.Parse(ChunkStart().Match(prompt.Messages[0].Text).Groups[1].Value, CultureInfo.InvariantCulture);
            var chunk = _f.Chunks(AiChartChunkOptions.Default, _selection?.Item1, _selection?.Item2)
                .Single(c => c.StartMs == ms);
            var reply = _f.EchoReply(chunk);
            edit?.Invoke(chunk, reply);
            return reply.Json();
        });

    private AiChartPassRun Run(FakeFactory factory) => new(
        _state, _history, _rail, _loc, new FakeAppLogger(), new FingeringGenerationService(), factory,
        () => _config, () => _selection);

    /// <summary>Every C5 the model is offered gets a ring-finger barre.</summary>
    private static void Rebarre(AiChartChunk _, AiChartFixture.ReplyDoc reply)
    {
        foreach (var c in reply.Chords.Where(c => c.Frets.SequenceEqual([-1, 3, 5, 5, -1, -1])))
            c.Fingers = [-1, 1, 3, 3, -1, -1];
    }

    [Fact]
    public async Task Without_a_provider_the_verb_says_so_and_names_the_ai_settings()
    {
        _config = new RigConfig();
        var factory = new FakeFactory(Model(Rebarre));

        await Run(factory).RunAsync(AiChartJob.FhpAndFingering);

        Assert.Equal(_loc["AiChart_NotConfigured"], _state.MainDisplayMessage);
        Assert.Contains("Settings ▸ AI", _state.MainDisplayMessage, StringComparison.Ordinal);
        Assert.Equal(StatusSeverity.Warning, _state.MainDisplaySeverity);
        Assert.Equal(0, factory.Created);
        Assert.False(_history.CanUndo);
        Assert.Empty(_rail.Log);
    }

    [Fact]
    public async Task A_run_is_a_rail_task_one_undo_step_and_a_summary_line()
    {
        var factory = new FakeFactory(Model(Rebarre));
        var idsBefore = _f.Level.Chords.Select(c => c.TemplateId).ToList();
        var run = Run(factory);

        await run.RunAsync(AiChartJob.FhpAndFingering);

        // HELD (#506): nothing is written until the charter applies.
        Assert.Equal(_loc.Format("AiChart_ReviewReady", 1, 0), _state.MainDisplayMessage);
        Assert.False(_history.CanUndo);
        Assert.Equal(idsBefore, _f.Level.Chords.Select(c => c.TemplateId));
        run.Review!.AcceptAll();
        Assert.True(run.ApplyReview());
        Assert.Null(run.Review);

        Assert.Equal(
            [$"begin:{AiChartPassRun.TaskId}:{_loc.Format("AiChart_Running", "Lead")}", $"complete:{AiChartPassRun.TaskId}"],
            _rail.Log);
        Assert.NotNull(_rail.LastTask!.CancelAction);
        Assert.Contains(_rail.Progress, p => p.Label == _loc.Format("AiChart_Progress", 1, 1));
        Assert.Equal(_loc.Format("AiChart_Done", 0, 6, 0, 1), _state.MainDisplayMessage);
        Assert.Equal(StatusSeverity.Success, _state.MainDisplaySeverity);
        Assert.False(factory.Settings!.WebResearch);

        // ONE node: a single Ctrl+Z puts every chord back on its shared template.
        Assert.Equal(6, _f.Level.Chords.Count(c => c.TemplateId == 3));
        _history.Undo();
        Assert.False(_history.CanUndo);
        Assert.Equal(idsBefore, _f.Level.Chords.Select(c => c.TemplateId));
        Assert.Equal(3, _f.Arrangement.ChordTemplates.Count);
    }

    [Fact]
    public async Task A_selection_is_the_only_part_offered_and_changed()
    {
        _selection = (AiChartFixture.At(16), AiChartFixture.At(18));
        var model = Model(Rebarre);
        var run = Run(new FakeFactory(model));

        await run.RunAsync(AiChartJob.FhpAndFingering);
        run.Review!.AcceptAll();
        run.ApplyReview();

        var prompt = Assert.Single(model.Prompts).Messages[0].Text;
        Assert.Contains("s12 8000ms", prompt, StringComparison.Ordinal);
        // The rest of the verse is CONTEXT: shown, never offered.
        Assert.Contains("ctx s19 13000ms", prompt, StringComparison.Ordinal);
        Assert.DoesNotContain(prompt.ReplaceLineEndings("\n").Split('\n'), l => l.StartsWith("s19 ", StringComparison.Ordinal));
        Assert.Equal([AiChartFixture.PowerC, AiChartFixture.PowerC, AiChartFixture.PowerC],
            _f.Level.Chords.Take(3).Select(c => c.TemplateId));
        Assert.Equal(3, _f.Level.Chords[4].TemplateId);
        Assert.Equal(3, _f.Level.Chords[5].TemplateId);
        Assert.Equal(AiChartFixture.PowerC, _f.Level.Chords[7].TemplateId);
    }

    [Fact]
    public async Task When_every_chunk_is_rejected_nothing_is_written_and_the_rail_says_why()
    {
        var model = Model((_, reply) => reply.Chords[0].Frets[1] = 4);
        var run = Run(new FakeFactory(model));

        await run.RunAsync(AiChartJob.FhpAndFingering);

        Assert.Equal(2, model.Calls); // the one chunk, and its one retry (#506)
        Assert.Null(run.Review);

        Assert.StartsWith(_loc.Format("AiChart_AllRejected", 1, "").TrimEnd(), _state.MainDisplayMessage, StringComparison.Ordinal);
        Assert.Equal(StatusSeverity.Error, _state.MainDisplaySeverity);
        Assert.False(_history.CanUndo);
    }

    [Fact]
    public void A_selection_range_runs_from_the_first_selected_start_to_the_last()
    {
        Assert.Null(AiChartPassRun.RangeOf([], []));
        var range = AiChartPassRun.RangeOf(
            [new GuitarNote { Position = AiChartFixture.At(9) }],
            [new Chord { Position = AiChartFixture.At(4) }, new Chord { Position = AiChartFixture.At(12) }]);
        Assert.Equal((AiChartFixture.At(4), AiChartFixture.At(12)), range);
    }

    private sealed class FakeFactory(IToneDesignModel model) : IToneDesignModelFactory
    {
        public int Created { get; private set; }

        public AiProviderSettings? Settings { get; private set; }

        public IToneDesignModel Create(AiProviderSettings settings)
        {
            Created++;
            Settings = settings;
            return model;
        }

        public bool Supports(AiProviderKind kind) => kind != AiProviderKind.None;
    }
}
