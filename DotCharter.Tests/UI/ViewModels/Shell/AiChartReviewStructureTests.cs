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
///     The review card over the chat's sections &amp; phrases proposals (#561): one row per change,
///     its marks on the section / phrase ribbon published through the SAME
///     <see cref="ChordUseHighlight" /> path as the FHP marks (#485 / #506), and Apply writing the
///     accepted changes as one undo step.
/// </summary>
public sealed class AiChartReviewStructureTests
{
    private readonly AiChartFixture _f = new();
    private readonly ProjectState _state = new();
    private readonly HistoryService _history = new(new FakeAppLogger());
    private readonly AiChartPassRun _run;
    private readonly AiChartReviewViewModel _vm;

    public AiChartReviewStructureTests()
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
        var model = new FakeChartModel((_, _) => JsonSerializer.Serialize(new
        {
            changes = new object[]
            {
                new { op = "add", kind = "section", bar = 1, beat = (int?)null, name = "intro" },
                new { op = "rename", kind = (string?)null, id = "p2", bar = (int?)null, beat = (int?)null, name = "verse1" }
            }
        }));
        _run = new AiChartPassRun(_state, _history, null, loc, logger, new FingeringGenerationService(),
            new Factory(model), () => rig, () => null);
        _vm = new AiChartReviewViewModel(_run, editor, _history, _state, loc);
    }

    private Task Ask() => _run.RunInstructionAsync(new AiChartInstructedRun(AiChartJob.SectionsAndPhrases,
        new AiChartInstruction("Mark the sections.")));

    [Fact]
    public async Task Each_change_is_a_row_and_the_ribbon_marks_follow_the_decisions()
    {
        await Ask();

        Assert.True(_vm.IsOpen);
        Assert.Equal(["AiStructure_Section(Intro)", "AiStructure_Phrase(verse1)"], _vm.Rows.Select(r => r.Name));
        Assert.Equal(["AiStructure_Add", "AiStructure_Rename(verse)"], _vm.Rows.Select(r => r.Detail));
        Assert.Equal("0:08.0 · AiStructure_Phrase(verse1)", _vm.Rows[1].Title);

        // Proposed: the new section and the renamed phrase; replaced: the live "verse" phrase.
        var (sections, phrases, proposed) = _vm.Published.EventsOn(_f.Arrangement);
        Assert.Empty(sections);
        Assert.Same(_f.Arrangement.EventPoints[1], Assert.Single(phrases));
        Assert.Equal(["0|intro|", "16||verse1"], proposed.Select(e => $"{e.Position.BeatId}|{e.Section}|{e.Phrase}"));
        Assert.False(_vm.Published.IsEmpty);

        _vm.Rows[1].RejectCommand.Execute(null);

        (sections, phrases, proposed) = _vm.Published.EventsOn(_f.Arrangement);
        Assert.Empty(phrases);
        Assert.Equal(["intro"], proposed.Select(e => e.Section));
        // Another arrangement shows none of them.
        Assert.Empty(_vm.Published.EventsOn(new Arrangement()).Proposed);
    }

    [Fact]
    public async Task Apply_writes_the_accepted_changes_as_one_undo_step_and_clears_the_marks()
    {
        await Ask();
        var node = _history.CurrentNodeId;

        _vm.AcceptAllCommand.Execute(null);
        _vm.ApplyCommand.Execute(null);

        Assert.False(_vm.IsOpen);
        Assert.True(_vm.Published.IsEmpty);
        Assert.Equal(["0|intro|intro", "16||verse1"],
            _f.Arrangement.EventPoints.Select(e => $"{e.Position.BeatId}|{e.Section}|{e.Phrase}"));
        _history.Undo();
        Assert.Equal(node, _history.CurrentNodeId);
        Assert.Equal(["0||intro", "16||verse"],
            _f.Arrangement.EventPoints.Select(e => $"{e.Position.BeatId}|{e.Section}|{e.Phrase}"));
    }

    private sealed class Factory(IToneDesignModel model) : IToneDesignModelFactory
    {
        public IToneDesignModel Create(AiProviderSettings settings) => model;

        public bool Supports(AiProviderKind kind) => kind != AiProviderKind.None;
    }
}
