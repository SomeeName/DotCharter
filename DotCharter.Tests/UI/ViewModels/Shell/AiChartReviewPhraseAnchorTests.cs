using System.Text.Json;
using DotCharter.Tone.Ai;
using DotCharter.Services.Ai.Chart;
using DotCharter.Services.Editing.Fingering;
using DotCharter.Services.Editing.Selection;
using DotCharter.Services.Editing.Tracks;
using DotCharter.Tests.Ai.Chart;
using DotCharter.UI.ViewModels.Editor;
using DotCharter.UI.ViewModels.Shell;

namespace DotCharter.Tests.UI.ViewModels.Shell;

/// <summary>
///     The review card over a phrase that brings the hand position it needs (#606): the row says so
///     ("+ hand position at fret N"), the anchor is marked on its level like the FHP review's proposed
///     anchors, and a rejected row takes its anchor mark with it.
/// </summary>
public sealed class AiChartReviewPhraseAnchorTests
{
    private readonly AiChartFixture _f = new();
    private readonly ProjectState _state = new();
    private readonly HistoryService _history = new(new FakeAppLogger());
    private readonly AiChartPassRun _run;
    private readonly AiChartReviewViewModel _vm;

    public AiChartReviewPhraseAnchorTests()
    {
        _state.ActiveProject = _f.Project;
        _state.ActiveArrangement = _f.Arrangement;
        _state.ActiveLevel = _f.Level;
        var logger = new FakeAppLogger();
        var selection = new SelectionService();
        var transport = new TransportViewModel(new FakeAudioPlaybackService(), _state, new FakeConfigurationService(), FakeLocalization.KeysOnly());
        var editor = new ChartEditorViewModel(_state, transport, _history, selection, new TempoService(logger),
            new TrackContextRegistry(_state, selection), FakeLocalization.KeysOnly());
        var loc = FakeLocalization.KeysOnly();
        var rig = new RigConfig { Ai = new AiProviderSettings(AiProviderKind.ClaudeCli, "fake-model", null) };
        // Bar 2 (beat 4) has no anchor of its own (the fret-5 one from beat 0 is in force); bar 3 (beat 8) has one.
        var model = new FakeChartModel((_, _) => JsonSerializer.Serialize(new
        {
            changes = new object[]
            {
                new { op = "add", kind = "phrase", bar = 2, beat = 1, name = "riff" },
                new { op = "add", kind = "phrase", bar = 3, beat = 1, name = "chords" }
            }
        }));
        _run = new AiChartPassRun(_state, _history, null, loc, logger, new FingeringGenerationService(),
            new Factory(model), () => rig, () => null);
        _vm = new AiChartReviewViewModel(_run, editor, _history, _state, loc);
    }

    private Task Ask() => _run.RunInstructionAsync(new AiChartInstructedRun(AiChartJob.SectionsAndPhrases,
        new AiChartInstruction("Split the intro into phrases.")));

    [Fact]
    public async Task The_row_names_the_hand_position_the_phrase_brings_and_the_anchor_is_marked_on_its_level()
    {
        await Ask();

        Assert.True(_vm.IsOpen);
        Assert.Equal(["AiStructure_Add · AiStructure_AnchorAdded(5)", "AiStructure_Add"], _vm.Rows.Select(r => r.Detail));
        Assert.Contains("AiStructure_AnchorAdded(5)", _vm.Rows[0].AccessibleName, StringComparison.Ordinal);

        var proposed = _vm.Published.On(_f.Level.Chords).ProposedAnchors;
        var anchor = Assert.Single(proposed);
        Assert.Equal((4, 5, 4), (anchor.Position.BeatId, anchor.Fret, anchor.Width));
        // Proposed, not written.
        Assert.DoesNotContain(_f.Level.Anchors, a => a.Position == anchor.Position);
        Assert.Equal(["4||riff", "8||chords"],
            _vm.Published.EventsOn(_f.Arrangement).Proposed.Select(e => $"{e.Position.BeatId}|{e.Section}|{e.Phrase}"));

        _vm.Rows[0].RejectCommand.Execute(null);

        Assert.Empty(_vm.Published.On(_f.Level.Chords).ProposedAnchors);
        Assert.Equal(["8||chords"], _vm.Published.EventsOn(_f.Arrangement).Proposed.Select(e => $"{e.Position.BeatId}|{e.Section}|{e.Phrase}"));
    }

    [Fact]
    public async Task Apply_writes_the_phrase_and_its_anchor_as_one_undo_step()
    {
        await Ask();
        var node = _history.CurrentNodeId;
        var anchors = _f.Level.Anchors.Count;

        _vm.AcceptAllCommand.Execute(null);
        _vm.ApplyCommand.Execute(null);

        Assert.False(_vm.IsOpen);
        Assert.True(_vm.Published.IsEmpty);
        Assert.Equal(anchors + 1, _f.Level.Anchors.Count);
        Assert.Contains(_f.Level.Anchors, a => a is { Position.BeatId: 4, Fret: 5, Width: 4 });

        _history.Undo();

        Assert.Equal(node, _history.CurrentNodeId);
        Assert.Equal(anchors, _f.Level.Anchors.Count);
        Assert.Equal(["0||intro", "16||verse"],
            _f.Arrangement.EventPoints.Select(e => $"{e.Position.BeatId}|{e.Section}|{e.Phrase}"));
    }

    private sealed class Factory(IToneDesignModel model) : IToneDesignModelFactory
    {
        public IToneDesignModel Create(AiProviderSettings settings) => model;

        public bool Supports(AiProviderKind kind) => kind != AiProviderKind.None;
    }
}
