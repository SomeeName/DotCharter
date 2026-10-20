using System.Text.Json;
using DotCharter.Tone.Ai;
using DotCharter.Services.Ai.Chart;
using DotCharter.Services.Editing.Fingering;
using DotCharter.Services.Editing.Quality;
using DotCharter.Services.Editing.FretAnchors;

namespace DotCharter.Tests.Ai.Chart;

/// <summary>
///     The sections &amp; phrases job places the hand position a phrase needs (#606): a phrase that
///     plays notes must own an anchor from its start to its first note (E06), so the gate attaches the
///     one the phrase needs — the anchor in force restated at the phrase start, or the FHP generator's
///     where none is in force — judges the change WITH it and the write adds both in ONE undo step.
///     On a FAKE model — no network, no provider.
///     <para>
///         The fixture (<see cref="AiChartFixture" />): 8 bars of 4 beats, phrases "intro" at beat 0
///         and "verse" at beat 16, anchors on beats 0 (fret 5), 8 (fret 3), 16 (fret 3) and 24
///         (fret 1), a C5 hand shape over beats 8-13, single notes on beats 0-7 (frets 5/7).
///     </para>
/// </summary>
public sealed class AiStructurePhraseAnchorTests
{
    private readonly AiChartFixture _f = new();
    private readonly ProjectState _state = new();
    private readonly HistoryService _history = new(new FakeAppLogger());
    private readonly RigConfig _config = new() { Ai = new AiProviderSettings(AiProviderKind.ClaudeCli, "fake-model", null) };
    private readonly ChartQualityReporter _reporter = new(new ArrangementValidationService(new FakeAppLogger()));

    public AiStructurePhraseAnchorTests()
    {
        _state.ActiveProject = _f.Project;
        _state.ActiveArrangement = _f.Arrangement;
        _state.ActiveLevel = _f.Level;
    }

    private static string Reply(params object[] changes) => JsonSerializer.Serialize(new { changes });

    private static object Phrase(int bar, int beat, string name) => new { op = "add", kind = "phrase", bar, beat, name };

    private AiChartPassRun Run(FakeChartModel model) =>
        new(_state, _history, null, FakeLocalization.English(), new FakeAppLogger(), new FingeringGenerationService(),
            new Factory(model), () => _config, () => null)
        {
            Quality = _reporter
        };

    private static Task<AiChartRunOutcome> Ask(AiChartPassRun run)
        => run.RunInstructionAsync(new AiChartInstructedRun(AiChartJob.SectionsAndPhrases,
            new AiChartInstruction("Split the intro into phrases."), null, null, null, null));

    private Dictionary<(string, double?), int> Findings() =>
        _reporter.Build(_f.Project).Findings.GroupBy(f => (f.Code, f.TimeMs)).ToDictionary(g => g.Key, g => g.Count());

    private static string Anchors(ChartLevel level) =>
        string.Join(";", level.Anchors.OrderBy(a => a.Position).Select(a => $"{a.Position.BeatId}:{a.Fret}/{a.Width}"));

    private static AiStructureChange AddPhrase(int beat, string name) =>
        new(AiStructureOp.Add, AiStructureKind.Phrase, null, null, new FractionalPosition(beat), name);

    [Fact]
    public async Task A_phrase_at_a_bar_without_an_anchor_is_accepted_with_the_anchor_it_needs_and_undo_removes_both()
    {
        var before = ChartProjectCloner.Clone(_f.Project);
        var findings = Findings();
        // Bar 2 (beat 4) plays notes and has no anchor of its own: the anchor in force is fret 5 from beat 0.
        var run = Run(new FakeChartModel((_, _) => Reply(Phrase(2, 1, "riff"))));

        var outcome = await Ask(run);

        Assert.Equal(AiChartRunStatus.Held, outcome.Status);
        Assert.Empty(outcome.Result!.Rejected);
        var item = Assert.Single(run.Review!.Items);
        Assert.Equal([new AiStructureAnchor(0, new FractionalPosition(4), 5, 4)], item.Change.Anchors);
        // Held, not written: neither the phrase nor its anchor is on the chart yet.
        Assert.Empty(DeepCompare.Diff("project", before, _f.Project, new HashSet<string>()));

        run.Review.AcceptAll();
        var node = _history.CurrentNodeId;
        Assert.True(run.ApplyReview());

        Assert.Equal("0:5/4;4:5/4;8:3/4;16:3/4;24:1/4", Anchors(_f.Level));
        Assert.Equal(["0||intro", "4||riff", "16||verse"],
            _f.Arrangement.EventPoints.Select(e => $"{e.Position.BeatId}|{e.Section}|{e.Phrase}"));
        Assert.All(Findings(), kv => Assert.True(kv.Value <= findings.GetValueOrDefault(kv.Key),
            $"the accepted phrase added {kv.Key.Item1} at {kv.Key.Item2}"));

        // ONE undo step takes back the phrase AND its anchor: the project is exactly what it was.
        _history.Undo();

        Assert.Equal(node, _history.CurrentNodeId);
        Assert.Empty(DeepCompare.Diff("project", before, _f.Project, new HashSet<string>()));
    }

    [Fact]
    public void Without_its_anchor_the_same_phrase_is_refused_for_E06()
    {
        var change = AddPhrase(4, "riff");

        var bare = new AiStructureGate(_f.Project, _f.Arrangement, _reporter) { PlacesAnchors = false };
        Assert.False(bare.TryAccept(change, 0, out _, out var rejection));
        Assert.Contains(ArrangementIssueType.AnchorMissingAtPhraseStart.GetCode(), rejection!.Reason, StringComparison.Ordinal);

        var gate = new AiStructureGate(_f.Project, _f.Arrangement, _reporter);
        Assert.True(gate.TryAccept(change, 0, out var accepted, out rejection), rejection?.Reason);
        Assert.Single(accepted.Anchors);
    }

    [Fact]
    public void A_phrase_that_already_starts_on_an_anchor_brings_none()
    {
        var gate = new AiStructureGate(_f.Project, _f.Arrangement, _reporter);

        // Beat 8 carries the fret-3 anchor; beat 24 the fret-1 one.
        Assert.True(gate.TryAccept(AddPhrase(8, "riff"), 0, out var onAnchor, out var rejection), rejection?.Reason);
        Assert.True(gate.TryAccept(AddPhrase(24, "out"), 1, out var onLast, out rejection), rejection?.Reason);

        Assert.Empty(onAnchor.Anchors);
        Assert.Empty(onLast.Anchors);
    }

    [Fact]
    public async Task An_existing_anchor_is_never_moved_or_removed()
    {
        var live = _f.Level.Anchors.ToList();
        var values = Anchors(_f.Level);
        var run = Run(new FakeChartModel((_, _) => Reply(Phrase(2, 1, "riff"), Phrase(6, 1, "bridge"))));
        await Ask(run);
        var review = run.Review!;
        Assert.Equal(2, review.Items.Count);
        // The charter places a hand position of their own on beat 4 while the card is open.
        var theirs = new FretAnchor(new FractionalPosition(4), 7, 4);
        _f.Level.Anchors.Insert(1, theirs);

        review.AcceptAll();
        Assert.True(run.ApplyReview());

        // Every anchor that was there is still there, the same object with the same values; the charter's
        // beat-4 anchor is neither replaced nor doubled. Bar 6 (beat 20) gets the fret-3 anchor in force.
        Assert.All(live, a => Assert.Contains(a, _f.Level.Anchors));
        Assert.Same(theirs, Assert.Single(_f.Level.Anchors, a => a.Position == new FractionalPosition(4)));
        Assert.Equal("0:5/4;4:7/4;8:3/4;16:3/4;20:3/4;24:1/4", Anchors(_f.Level));

        _history.Undo();

        Assert.Equal(values, Anchors(_f.Level).Replace("4:7/4;", "", StringComparison.Ordinal));
        Assert.Same(theirs, _f.Level.Anchors.Single(a => a.Position == new FractionalPosition(4)));
    }

    [Fact]
    public async Task A_phrase_that_still_adds_a_finding_with_its_anchor_is_refused()
    {
        var before = ChartProjectCloner.Clone(_f.Project);
        // Bar 3 beat 2 (beat 9) is inside the C5 hand shape (beats 8-13): the anchor it needs would sit in
        // the shape at a phrase boundary (I21).
        var run = Run(new FakeChartModel((_, _) => Reply(Phrase(3, 2, "inside"))));

        var outcome = await Ask(run);

        Assert.Equal(AiChartRunStatus.NoChange, outcome.Status);
        Assert.Null(run.Review);
        var refused = Assert.Single(outcome.Result!.Rejected);
        Assert.Equal(AiChartRejectionCodes.Rules, refused.Code);
        Assert.Contains(ArrangementIssueType.AnchorInsideHandShapeAtPhraseBoundary.GetCode(), refused.Reason,
            StringComparison.Ordinal);
        Assert.Contains("even with the hand position it needs at fret 3", refused.Reason, StringComparison.Ordinal);
        Assert.Empty(DeepCompare.Diff("project", before, _f.Project, new HashSet<string>()));
    }

    [Fact]
    public void With_no_anchor_in_force_the_phrase_takes_the_fhp_generators_hand_position()
    {
        // No anchor before beat 8 any more: the bar-2 phrase has nothing to restate.
        _f.Level.Anchors.RemoveAt(0);
        var expected = new FretAnchorGenerationService()
            .GenerateAnchors(_f.Level, chordTemplates: _f.Arrangement.ChordTemplates, tuning: _f.Arrangement)
            .Last(a => a.Position <= new FractionalPosition(4));
        var gate = new AiStructureGate(_f.Project, _f.Arrangement, _reporter);

        Assert.True(gate.TryAccept(AddPhrase(4, "riff"), 0, out var accepted, out var rejection), rejection?.Reason);

        var anchor = Assert.Single(accepted.Anchors);
        Assert.Equal(new AiStructureAnchor(0, new FractionalPosition(4), expected.Fret, expected.Width), anchor);
        Assert.Equal(5, anchor.Fret);
    }

    [Fact]
    public void Every_level_that_plays_in_the_phrase_gets_its_own_anchor_and_one_that_does_not_gets_none()
    {
        // Level 1: the same intro notes under a fret-7 hand position. Level 2: only the verse.
        var easy = new ChartLevel();
        foreach (var n in _f.Level.Notes.Where(n => n.Position.BeatId < 8))
            easy.Notes.Add(new GuitarNote { Position = n.Position, StringIndex = n.StringIndex, Fret = n.Fret });
        easy.Anchors.Add(new FretAnchor(new FractionalPosition(0), 7, 4));
        var verseOnly = new ChartLevel();
        verseOnly.Notes.Add(new GuitarNote { Position = new FractionalPosition(20), StringIndex = 3, Fret = 5 });
        verseOnly.Anchors.Add(new FretAnchor(new FractionalPosition(16), 3, 4));
        _f.Arrangement.Levels.Add(easy);
        _f.Arrangement.Levels.Add(verseOnly);
        var gate = new AiStructureGate(_f.Project, _f.Arrangement, _reporter);

        var accepted = gate.TryAccept(AddPhrase(4, "riff"), 0, out var change, out var rejection);

        Assert.True(accepted, rejection?.Reason);
        Assert.Equal([new AiStructureAnchor(0, new FractionalPosition(4), 5, 4), new AiStructureAnchor(1, new FractionalPosition(4), 7, 4)],
            change.Anchors);
        Assert.Equal("AiStructure_Add · AiStructure_AnchorsAdded(5, 7)",
            AiStructureText.Detail(new AiStructureReviewItem(change, 2000, null), FakeLocalization.KeysOnly()));
    }

    private sealed class Factory(IToneDesignModel model) : IToneDesignModelFactory
    {
        public IToneDesignModel Create(AiProviderSettings settings) => model;

        public bool Supports(AiProviderKind kind) => kind != AiProviderKind.None;
    }
}
