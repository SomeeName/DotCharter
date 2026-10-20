using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using DotCharter.Tone.Ai;
using DotCharter.Services.Ai.Chart;
using DotCharter.Services.Editing.Fingering;
using DotCharter.Services.Editing.Quality;

namespace DotCharter.Tests.Ai.Chart;

/// <summary>
///     The AI chat's sections &amp; phrases job (#561): the model proposes section and phrase
///     markers on the beat grid, HELD for the review card; nothing else can change. On a FAKE
///     model — no network, no provider.
///     <para>
///         The fixture (<see cref="AiChartFixture" />) is 8 bars of 4 beats (bar n starts on beat
///         4(n-1), 500 ms a beat), phrases "intro" at beat 0 and "verse" at beat 16, no section, and
///         anchors on beats 0, 8, 16 and 24.
///     </para>
/// </summary>
public sealed class AiStructureTests
{
    private const string Mark = "Mark the sections and split them into phrases.";

    private readonly AiChartFixture _f = new();
    private readonly ProjectState _state = new();
    private readonly HistoryService _history = new(new FakeAppLogger());
    private readonly RigConfig _config = new() { Ai = new AiProviderSettings(AiProviderKind.ClaudeCli, "fake-model", null) };
    private readonly ChartQualityReporter _reporter = new(new ArrangementValidationService(new FakeAppLogger()));

    public AiStructureTests()
    {
        _state.ActiveProject = _f.Project;
        _state.ActiveArrangement = _f.Arrangement;
        _state.ActiveLevel = _f.Level;
    }

    /// <summary>A reply with these changes (anonymous objects, serialized as given).</summary>
    private static string Reply(params object[] changes) => JsonSerializer.Serialize(new { changes });

    private static object Add(string kind, int bar, int? beat, string name) => new { op = "add", kind, bar, beat, name };

    private AiChartPassRun Run(FakeChartModel model) =>
        new(_state, _history, null, FakeLocalization.English(), new FakeAppLogger(), new FingeringGenerationService(),
            new Factory(model), () => _config, () => null)
        {
            Quality = _reporter
        };

    private static Task<AiChartRunOutcome> Ask(AiChartPassRun run, FractionalPosition? from = null, FractionalPosition? to = null)
        => run.RunInstructionAsync(new AiChartInstructedRun(AiChartJob.SectionsAndPhrases, new AiChartInstruction(Mark),
            null, from, to, null));

    /// <summary>Every section and phrase, by value, in order: what undo must restore exactly.</summary>
    private string Markers() =>
        string.Join(";", _f.Arrangement.EventPoints.Select(e => $"{e.Position}|{e.Section}|{e.Phrase}|{e.Events}|{e.Status}")) +
        "#" + string.Join(",", _f.Arrangement.Phrases) +
        "#" + string.Join(",", _f.Arrangement.PhraseIterations.Select(p => $"{p.Position}:{p.PhraseId}"));

    /// <summary>Everything the job may NEVER change: notes, chords, anchors, hand shapes, templates.</summary>
    private string Sounds() =>
        string.Join(";", _f.Level.Notes.Select(n => $"{n.Position}:{n.StringIndex}/{n.Fret}")) + "|" +
        string.Join(";", _f.Level.Chords.Select(c => $"{c.Position}:{c.TemplateId}")) + "|" +
        string.Join(";", _f.Level.Anchors.Select(a => $"{a.Position}:{a.Fret}/{a.Width}")) + "|" +
        string.Join(";", _f.Level.HandShapes.Select(h => $"{h.Position}-{h.EndPosition}:{h.TemplateId}")) + "|" +
        _f.Arrangement.ChordTemplates.Count;

    private Dictionary<(string, double?), int> Findings() =>
        _reporter.Build(_f.Project).Findings.GroupBy(f => (f.Code, f.TimeMs)).ToDictionary(g => g.Key, g => g.Count());

    [Fact]
    public async Task Sections_land_on_bar_lines_and_phrases_on_beats()
    {
        var model = new FakeChartModel((_, _) => Reply(
            Add("section", 1, null, "intro"),
            Add("section", 5, 1, "Verse"),
            Add("phrase", 3, 1, "riff"),
            Add("phrase", 7, 1, "fill")));
        var run = Run(model);

        var outcome = await Ask(run);

        Assert.Equal(AiChartRunStatus.Held, outcome.Status);
        var review = Assert.IsType<AiChartReview>(run.Review);
        Assert.Empty(review.Chunks);
        Assert.Equal(4, review.Items.Count);
        var grid = AiStructureGrid.Of(_f.Beats);
        foreach (var item in review.Items)
        {
            var at = grid.Locate(item.Change.At);
            Assert.NotNull(at);
            Assert.True(at.Value.OnBeat);
            if (item.Change.Kind == AiStructureKind.Section) Assert.Equal(1, at.Value.Beat);
        }

        Assert.Equal(["intro@0", "riff@8", "verse@16", "fill@24"],
            review.Items.Select(i => $"{i.Change.NameAfter}@{i.Change.At.BeatId}"));
        // "Verse" is stored the way the exporters and the editor spell it.
        Assert.Contains(review.Items, i => i.Change is { Kind: AiStructureKind.Section, Name: "verse" });
        // Held, not written.
        Assert.Equal(2, _f.Arrangement.EventPoints.Count);
    }

    [Fact]
    public async Task A_section_off_the_bar_line_is_refused_and_asked_once_more_with_the_reason()
    {
        var model = new FakeChartModel((_, call) => call == 0
            ? Reply(Add("section", 2, 3, "chorus"))
            : Reply(Add("section", 2, null, "chorus")));
        var run = Run(model);

        var outcome = await Ask(run);

        Assert.Equal(2, model.Calls);
        Assert.Contains(AiChartRejectionCodes.OffGrid, model.Prompts[1].Messages[^1].Text);
        Assert.Equal(AiChartRunStatus.Held, outcome.Status);
        var item = Assert.Single(run.Review!.Items);
        Assert.Equal(new FractionalPosition(4), item.Change.At);
    }

    [Theory]
    [InlineData("section", "chorus 2", AiChartRejectionCodes.SectionName)]
    [InlineData("section", "Refrain", AiChartRejectionCodes.SectionName)]
    [InlineData("section", "COUNT", AiChartRejectionCodes.SectionName)]
    [InlineData("phrase", "Solo!", AiChartRejectionCodes.PhraseName)]
    [InlineData("phrase", "ソロ", AiChartRejectionCodes.PhraseName)]
    [InlineData("phrase", "", AiChartRejectionCodes.PhraseName)]
    public async Task Invalid_names_are_refused_and_nothing_is_held(string kind, string name, string code)
    {
        var before = Markers();
        var model = new FakeChartModel((_, _) => Reply(Add(kind, 3, 1, name)));
        var run = Run(model);

        var outcome = await Ask(run);

        Assert.Equal(AiChartRunStatus.AllRejected, outcome.Status);
        Assert.Null(run.Review);
        Assert.Equal(code, Assert.Single(outcome.Result!.Rejected).Code);
        Assert.Equal(before, Markers());
    }

    [Fact]
    public void Section_names_come_from_the_rocksmith_vocabulary_and_are_stored_lower_case()
    {
        foreach (var name in RsSectionCatalog.Names)
        {
            Assert.True(AiStructureReply.TryName(AiStructureKind.Section, name.ToUpperInvariant(), out var stored));
            Assert.Equal(name, stored);
        }

        Assert.False(AiStructureReply.TryName(AiStructureKind.Section, "pre-chorus", out _));
        Assert.Contains(string.Join(", ", RsSectionCatalog.Names), AiStructurePrompt.System, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("""{"changes":[],"notes":[{"bar":1,"beat":1,"string":2,"fret":5}]}""")]
    [InlineData("""{"changes":[{"op":"add","kind":"note","bar":1,"beat":1,"name":"x"}]}""")]
    [InlineData("""{"changes":[{"op":"add","kind":"phrase","bar":3,"beat":1,"name":"riff","fret":7}]}""")]
    [InlineData("""{"changes":[{"op":"set-fret","id":"p1"}]}""")]
    [InlineData("""{"anchors":[{"at":"start","fret":5,"width":4}],"changes":[]}""")]
    public async Task A_reply_that_touches_anything_but_sections_and_phrases_is_refused_whole(string json)
    {
        var markers = Markers();
        var sounds = Sounds();
        var model = new FakeChartModel((_, _) => json);
        var run = Run(model);

        var outcome = await Ask(run);

        Assert.Equal(AiChartRunStatus.AllRejected, outcome.Status);
        Assert.Equal(AiChartRejectionCodes.NotStructure, Assert.Single(outcome.Result!.Rejected).Code);
        Assert.Equal(2, model.Calls); // refused, retried once with the reason, refused again
        Assert.Null(run.Review);
        Assert.False(_history.CanUndo);
        Assert.Equal(markers, Markers());
        Assert.Equal(sounds, Sounds());
    }

    [Fact]
    public async Task Accepting_writes_one_undo_node_and_undo_restores_the_markers_exactly()
    {
        var before = Markers();
        var sounds = Sounds();
        var model = new FakeChartModel((_, _) => Reply(
            Add("section", 1, null, "intro"),
            Add("section", 5, null, "verse"),
            new { op = "rename", kind = (string?)null, id = "p2", bar = (int?)null, beat = (int?)null, name = "verse1" },
            Add("phrase", 3, 1, "riff")));
        var run = Run(model);
        await Ask(run);
        var node = _history.CurrentNodeId;

        run.Review!.AcceptAll();
        Assert.True(run.ApplyReview());

        Assert.Null(run.Review);
        Assert.NotEqual(node, _history.CurrentNodeId);
        Assert.Equal(["0|intro|intro", "8||riff", "16|verse|verse1"],
            _f.Arrangement.EventPoints.Select(e => $"{e.Position.BeatId}|{e.Section}|{e.Phrase}"));
        Assert.Equal(sounds, Sounds());

        _history.Undo();

        Assert.Equal(node, _history.CurrentNodeId);
        Assert.Equal(before, Markers());
        Assert.Equal(sounds, Sounds());
    }

    [Fact]
    public async Task Only_accepted_changes_are_written_and_a_rejected_one_stays_as_it_was()
    {
        var model = new FakeChartModel((_, _) => Reply(Add("section", 1, null, "intro"), Add("phrase", 3, 1, "riff")));
        var run = Run(model);
        await Ask(run);
        var review = run.Review!;

        review.Decide(review.Items.Single(i => i.Change.Kind == AiStructureKind.Section), AiChartDecision.Accepted);
        review.Decide(review.Items.Single(i => i.Change.Kind == AiStructureKind.Phrase), AiChartDecision.Rejected);
        // The marks follow the decisions: the rejected phrase is no longer proposed.
        Assert.Equal(["intro"], review.StructureMarks().Proposed.Select(e => e.Section));
        Assert.True(run.ApplyReview());

        Assert.Equal(["0|intro|intro", "16||verse"],
            _f.Arrangement.EventPoints.Select(e => $"{e.Position.BeatId}|{e.Section}|{e.Phrase}"));
    }

    [Fact]
    public async Task An_accepted_proposal_adds_no_chart_quality_report_finding_and_one_that_would_is_refused()
    {
        var before = Findings();
        // Bar 3 beat 2 (beat 9) is inside the C5 hand shape (beats 8-13): the phrase needs a hand position
        // of its own (E06), and the one it would bring sits inside the shape at a phrase boundary (I21).
        var model = new FakeChartModel((_, _) => Reply(
            Add("section", 1, null, "intro"),
            Add("phrase", 3, 2, "bad"),
            Add("phrase", 3, 1, "riff"),
            Add("section", 5, null, "verse")));
        var run = Run(model);

        var outcome = await Ask(run);

        var refused = Assert.Single(outcome.Result!.Rejected);
        Assert.Equal(AiChartRejectionCodes.Rules, refused.Code);
        Assert.Contains(ArrangementIssueType.AnchorInsideHandShapeAtPhraseBoundary.GetCode(), refused.Reason,
            StringComparison.Ordinal);
        Assert.DoesNotContain(run.Review!.Items, i => i.Change.Name == "bad");
        Assert.Equal(3, run.Review.Items.Count);

        run.Review.AcceptAll();
        Assert.True(run.ApplyReview());

        var after = Findings();
        Assert.All(after, kv => Assert.True(kv.Value <= before.GetValueOrDefault(kv.Key),
            $"the accepted proposal added {kv.Key.Item1} at {kv.Key.Item2}"));
    }

    [Fact]
    public async Task Apply_refuses_a_subset_that_would_now_add_a_finding_and_keeps_the_review_open()
    {
        var model = new FakeChartModel((_, _) => Reply(Add("phrase", 3, 1, "riff")));
        var run = Run(model);
        await Ask(run);
        var review = run.Review!;
        review.AcceptAll();
        // The charter deletes the anchor the phrase relied on while the card is open: the phrase
        // would now own no hand position (E06).
        _f.Level.Anchors.RemoveAt(1);
        var markers = Markers();

        Assert.False(run.ApplyReview());

        Assert.Same(review, run.Review);
        Assert.Equal(markers, Markers());
        Assert.False(_history.CanUndo);
        Assert.Contains("E06", _state.EditorUi.MainDisplayMessage, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Imported_phrases_are_listed_as_fixed_and_a_change_to_one_is_refused()
    {
        _f.Arrangement.Phrases.Add("outro");
        _f.Arrangement.PhraseIterations.Add(new PhraseIteration { Position = new FractionalPosition(28), PhraseId = 0 });
        var model = new FakeChartModel((_, _) => Reply(
            new { op = "rename", kind = (string?)null, id = "p3", bar = (int?)null, beat = (int?)null, name = "end2" }));
        var run = Run(model);

        var outcome = await Ask(run);

        Assert.Contains("p3 phrase \"outro\" at bar 8 beat 1 (0:14.0) [fixed]", model.Prompts[0].Messages[0].Text,
            StringComparison.Ordinal);
        Assert.Equal(AiChartRejectionCodes.ImportedMarker, Assert.Single(outcome.Result!.Rejected).Code);
        Assert.Null(run.Review);
    }

    [Fact]
    public async Task A_change_outside_the_chosen_scope_is_refused()
    {
        var model = new FakeChartModel((_, _) => Reply(Add("section", 1, null, "intro")));
        var run = Run(model);

        var outcome = await Ask(run, new FractionalPosition(16), new FractionalPosition(26));

        Assert.Equal(AiChartRejectionCodes.OutOfScope, Assert.Single(outcome.Result!.Rejected).Code);
        Assert.Contains("only from bar 5 beat 1 to bar 7 beat 3", model.Prompts[0].Messages[0].Text, StringComparison.Ordinal);
    }

    [Fact]
    public void The_listing_shows_the_bars_with_repeat_letters_and_anchors_and_never_offers_a_note()
    {
        var context = AiStructureContext.Of(_f.Arrangement, _f.Beats, null, null);
        var prompt = AiStructurePrompt.Build(_f.Facts, _f.Level, _f.Beats, context, new AiChartInstruction(Mark));
        var text = prompt.Messages[0].Text;

        Assert.Equal(AiStructurePrompt.Schema, prompt.JsonSchema);
        Assert.Contains("p1 phrase \"intro\" at bar 1 beat 1 (0:00.0)", text, StringComparison.Ordinal);
        Assert.Contains("p2 phrase \"verse\" at bar 5 beat 1 (0:08.0)", text, StringComparison.Ordinal);
        Assert.Contains("1 | 0:00.0 | 4 | 4 0 | 5-7 | A | A@1", text, StringComparison.Ordinal);
        // Bar 2 repeats bar 1 note for note (letter A again); bars 3 and 4 differ (C5 C5 vs C5 G5).
        Assert.Contains("2 | 0:02.0 | 4 | 4 0 | 5-7 | A | -", text, StringComparison.Ordinal);
        Assert.Contains("3 | 0:04.0 | 4 | 0 2 | 3-5 | B | A@1", text, StringComparison.Ordinal);
        Assert.Contains("4 | 0:06.0 | 4 | 0 2 | 3-5 | C | -", text, StringComparison.Ordinal);
        Assert.Contains(AiChartPrompt.InstructionHeader, text, StringComparison.Ordinal);
        Assert.DoesNotContain("fret\"", AiStructurePrompt.Schema, StringComparison.Ordinal);
    }

    /// <summary>
    ///     The FHP / fingering prompts are pinned BYTE FOR BYTE: system, schema and every chunk's text,
    ///     with and without an instruction, by hash — another job's change (#561 touched the job enum and
    ///     <c>ListsChords</c>) may not move them. A deliberate change re-pins it: #803 put the rule list
    ///     with its codes in the system prompts and the phrase starts and in-shape sounds in the listing.
    /// </summary>
    [Theory]
    [InlineData(AiChartJob.FhpAndFingering, "2C48BFF064498DD4")]
    [InlineData(AiChartJob.FhpOnly, "36BCE0E2EE69A8EF")]
    [InlineData(AiChartJob.FingeringOnly, "79638BD3D3D38EC8")]
    public void The_fhp_and_fingering_prompts_are_unchanged(AiChartJob job, string expected)
    {
        var sb = new StringBuilder();
        foreach (var instruction in new[] { null, new AiChartInstruction("Use 1·3 for octaves.", "charter: hi") })
        {
            foreach (var chunk in _f.Chunks())
            {
                var p = AiChartPrompt.Build(job, ChordNamingStyle.Eof, _f.Facts, _f.Level, _f.Beats, chunk, instruction);
                sb.Append(p.System).Append('\u0001').Append(p.JsonSchema).Append('\u0001').Append(p.SchemaName)
                    .Append('\u0001').Append(p.Messages[0].Text).Append('\u0002');
            }
        }

        var hash = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(sb.ToString())));
        Assert.Equal(expected, hash[..16]);
    }

    private sealed class Factory(IToneDesignModel model) : IToneDesignModelFactory
    {
        public IToneDesignModel Create(AiProviderSettings settings) => model;

        public bool Supports(AiProviderKind kind) => kind != AiProviderKind.None;
    }
}
