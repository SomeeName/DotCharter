using System.Globalization;
using System.Text.RegularExpressions;
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
///     The AI chart pass review card (#506), between the held review and the editor: it
///     lists one row per section, publishes the review's marks through the SAME
///     <see cref="ChordUseHighlight" /> path the chord panel's bulk-edit preview (#485) uses
///     (and wins over it while open), re-reads them after every history step, and applies
///     the accepted sections as one undo step. On a FAKE model, over the
///     <see cref="AiChartFixture" /> cut one chunk per phrase.
/// </summary>
public sealed partial class AiChartReviewViewModelTests
{
    private readonly AiChartFixture _f = new();
    private readonly ProjectState _state = new();
    private readonly HistoryService _history = new(new FakeAppLogger());
    private readonly ChartEditorViewModel _editor;
    private readonly AiChartPassRun _run;
    private readonly AiChartReviewViewModel _vm;

    public AiChartReviewViewModelTests()
    {
        _state.ActiveProject = _f.Project;
        _state.ActiveArrangement = _f.Arrangement;
        _state.ActiveLevel = _f.Level;
        var logger = new FakeAppLogger();
        var selection = new SelectionService();
        var config = new FakeConfigurationService();
        var transport = new TransportViewModel(new FakeAudioPlaybackService(), _state, config, FakeLocalization.KeysOnly());
        _editor = new ChartEditorViewModel(_state, transport, _history, selection, new TempoService(logger),
            new TrackContextRegistry(_state, selection), FakeLocalization.KeysOnly());
        var loc = FakeLocalization.KeysOnly();
        var rig = new RigConfig { Ai = new AiProviderSettings(AiProviderKind.ClaudeCli, "fake-model", null) };
        _run = new AiChartPassRun(_state, _history, null, loc, logger, new FingeringGenerationService(),
            new Factory(Model()), () => rig, () => null)
        {
            ChunkOptions = AiChartFixture.PerPhrase
        };
        _vm = new AiChartReviewViewModel(_run, _editor, _history, _state, loc);
    }

    [GeneratedRegex(@"--- chunk starts at (\d+)ms")]
    private static partial Regex ChunkStart();

    private FakeChartModel Model() => new((prompt, _) =>
    {
        var ms = int.Parse(ChunkStart().Match(prompt.Messages[0].Text).Groups[1].Value, CultureInfo.InvariantCulture);
        var chunk = _f.Chunks().Single(c => c.StartMs == ms);
        var reply = _f.EchoReply(chunk);
        AiChartReviewTests.Proposal(chunk, reply);
        return reply.Json();
    });

    [Fact]
    public async Task A_held_review_opens_the_card_and_marks_every_proposal_on_the_timeline()
    {
        Assert.False(_vm.IsOpen);

        await _run.RunAsync(AiChartJob.FhpAndFingering);

        Assert.True(_vm.IsOpen);
        Assert.Equal(2, _vm.Rows.Count);
        Assert.Equal("0:00.0 – 0:08.0 · intro", _vm.Rows[0].Title);
        Assert.Equal("AiChartReview_Fingerings(3)", _vm.Rows[0].Detail);
        Assert.Equal("AiChartReview_AnchorsOne(1) · AiChartReview_Fingerings(3)", _vm.Rows[1].Detail);
        // The card heads a row with the section's name and puts the span under it, with the detail.
        Assert.Equal("intro", _vm.Rows[0].Name);
        Assert.Equal("0:00.0 – 0:08.0 · AiChartReview_Fingerings(3)", _vm.Rows[0].Summary);
        Assert.Equal("verse", _vm.Rows[1].Name);
        Assert.StartsWith("0:08.0 · ", _vm.Rows[1].Summary, StringComparison.Ordinal);
        Assert.Equal("AiChartReview_Counts(0, 0, 2)", _vm.CountsText);
        Assert.False(_vm.ApplyCommand.CanExecute(null));

        // The marks ARE the review's: six C5, the hand shape over the intro's, the verse's
        // first anchor replaced by the six-fret one.
        var shown = _editor.UseHighlight;
        Assert.Same(_vm.Published, shown);
        Assert.Equal(6, shown.Chords.Count);
        Assert.Contains(_f.Level.HandShapes[0], shown.HandShapes);
        var live = Assert.Single(shown.Anchors);
        Assert.Equal((AiChartFixture.At(16), 3, 4), (live.Position, live.Fret, live.Width));
        var proposed = Assert.Single(shown.On(_f.Level.Chords).ProposedAnchors);
        Assert.Equal((AiChartFixture.At(16), 3, 6), (proposed.Position, proposed.Fret, proposed.Width));
    }

    [Fact]
    public async Task Rejecting_a_section_takes_its_marks_away_and_accepting_enables_apply()
    {
        await _run.RunAsync(AiChartJob.FhpAndFingering);

        _vm.Rows[0].RejectCommand.Execute(null);

        Assert.True(_vm.Rows[0].IsRejected);
        Assert.Equal("AiChartReview_StateRejected", _vm.Rows[0].StateText);
        Assert.Equal(3, _editor.UseHighlight.Chords.Count);
        Assert.All(_editor.UseHighlight.Chords, c => Assert.True(c.Position >= AiChartFixture.At(16)));
        Assert.Empty(_editor.UseHighlight.HandShapes);
        Assert.False(_vm.ApplyCommand.CanExecute(null));

        _vm.Rows[1].AcceptCommand.Execute(null);

        Assert.Equal("AiChartReview_Counts(1, 1, 0)", _vm.CountsText);
        Assert.Equal("AiChartReview_Apply(1)", _vm.ApplyText);
        Assert.True(_vm.ApplyCommand.CanExecute(null));
    }

    [Fact]
    public async Task Apply_writes_one_undo_step_closes_the_card_and_clears_the_marks()
    {
        await _run.RunAsync(AiChartJob.FhpAndFingering);
        _vm.AcceptAllCommand.Execute(null);

        _vm.ApplyCommand.Execute(null);

        Assert.False(_vm.IsOpen);
        Assert.Empty(_vm.Rows);
        Assert.True(_editor.UseHighlight.IsEmpty);
        Assert.NotEqual(AiChartFixture.PowerC, _f.Level.Chords[0].TemplateId);
        _history.Undo();
        Assert.False(_history.CanUndo);
        Assert.Equal(AiChartFixture.PowerC, _f.Level.Chords[0].TemplateId);
    }

    [Fact]
    public async Task The_review_wins_over_the_chord_panel_preview_and_hands_it_back_on_discard()
    {
        var panel = ChordUseHighlight.From([_f.Level.Chords[6]], [], _f.Arrangement);
        _editor.PublishUseHighlight(panel);
        Assert.Same(panel, _editor.UseHighlight);

        await _run.RunAsync(AiChartJob.FhpAndFingering);
        Assert.Equal(6, _editor.UseHighlight.Chords.Count);

        _vm.DiscardCommand.Execute(null);

        Assert.False(_vm.IsOpen);
        Assert.Same(panel, _editor.UseHighlight);
        Assert.False(_history.CanUndo);
    }

    [Fact]
    public async Task An_edit_while_the_card_is_open_re_reads_the_marks()
    {
        await _run.RunAsync(AiChartJob.FhpAndFingering);
        Assert.Equal(6, _editor.UseHighlight.Chords.Count);

        // The charter deletes the verse's second hand position: the verse is stale now.
        var em = _f.Level.Anchors.Single(a => a.Position == AiChartFixture.At(24));
        Assert.True(_history.Do(new RemoveAnchorsAction(_f.Level.Anchors, [em])));

        Assert.Equal(3, _editor.UseHighlight.Chords.Count);
        Assert.All(_editor.UseHighlight.Chords, c => Assert.True(c.Position < AiChartFixture.At(16)));
        Assert.Empty(_editor.UseHighlight.Anchors);
    }

    [Fact]
    public async Task Go_to_moves_the_playhead_to_the_section()
    {
        await _run.RunAsync(AiChartJob.FhpAndFingering);

        _vm.Rows[1].ShowCommand.Execute(null);

        Assert.Equal(8000, _state.CurrentTimeMs, 3);
        Assert.Same(_vm.Rows[1], _vm.CurrentRow);
        Assert.True(_vm.Rows[1].IsCurrent);
        Assert.False(_vm.Rows[0].IsCurrent);
    }

    [Fact]
    public async Task Up_and_down_step_the_current_row_go_to_it_and_stop_at_the_ends()
    {
        await _run.RunAsync(AiChartJob.FhpAndFingering);
        Assert.Null(_vm.CurrentRow);
        _state.CurrentTimeMs = 30_000;

        // With no current row either key starts at the first.
        Assert.Same(_vm.Rows[0], _vm.Step(-1));
        Assert.Equal(0, _state.CurrentTimeMs, 3);
        Assert.Same(_vm.Rows[1], _vm.Step(1));
        Assert.Equal(8000, _state.CurrentTimeMs, 3);
        Assert.Same(_vm.Rows[1], _vm.Step(1));
        Assert.Equal([false, true], _vm.Rows.Select(r => r.IsCurrent));
        Assert.Same(_vm.Rows[0], _vm.Step(-1));
        Assert.Equal([true, false], _vm.Rows.Select(r => r.IsCurrent));
    }

    [Fact]
    public async Task A_and_R_decide_the_current_row_and_do_nothing_without_one()
    {
        await _run.RunAsync(AiChartJob.FhpAndFingering);

        _vm.AcceptCurrent();
        _vm.RejectCurrent();
        Assert.Equal("AiChartReview_Counts(0, 0, 2)", _vm.CountsText);

        _vm.Step(1);
        _vm.RejectCurrent();
        Assert.True(_vm.Rows[0].IsRejected);
        _vm.AcceptCurrent();
        _vm.AcceptCurrent();
        Assert.True(_vm.Rows[0].IsAccepted);
        Assert.Equal("AiChartReview_Counts(1, 0, 1)", _vm.CountsText);
        Assert.True(_vm.ApplyCommand.CanExecute(null));
    }

    [Fact]
    public async Task The_row_toggles_put_a_decided_section_back_to_deciding()
    {
        await _run.RunAsync(AiChartJob.FhpAndFingering);

        _vm.Rows[0].AcceptCommand.Execute(null);
        Assert.True(_vm.Rows[0].IsAccepted);
        _vm.Rows[0].AcceptCommand.Execute(null);
        Assert.False(_vm.Rows[0].IsAccepted || _vm.Rows[0].IsRejected);
        Assert.Equal("AiChartReview_StatePending", _vm.Rows[0].StateText);

        _vm.Rows[0].RejectCommand.Execute(null);
        _vm.Rows[0].AcceptCommand.Execute(null);
        Assert.True(_vm.Rows[0].IsAccepted);
        _vm.Rows[0].RejectCommand.Execute(null);
        Assert.True(_vm.Rows[0].IsRejected);
        _vm.Rows[0].RejectCommand.Execute(null);
        Assert.Equal("AiChartReview_Counts(0, 0, 2)", _vm.CountsText);
    }

    [Fact]
    public async Task Reject_all_rejects_the_undecided_keeps_an_accepted_row_and_takes_their_marks_away()
    {
        await _run.RunAsync(AiChartJob.FhpAndFingering);
        _vm.Rows[1].AcceptCommand.Execute(null);

        _vm.RejectAllCommand.Execute(null);

        Assert.True(_vm.Rows[0].IsRejected);
        Assert.True(_vm.Rows[1].IsAccepted);
        Assert.Equal("AiChartReview_Counts(1, 1, 0)", _vm.CountsText);
        Assert.All(_editor.UseHighlight.Chords, c => Assert.True(c.Position >= AiChartFixture.At(16)));
        Assert.True(_vm.ApplyCommand.CanExecute(null));
    }

    [Fact]
    public async Task The_hint_and_the_keys_live_in_the_info_tooltip_and_a_new_review_has_no_current_row()
    {
        await _run.RunAsync(AiChartJob.FhpAndFingering);
        _vm.Step(1);

        Assert.Equal(
            string.Join(Environment.NewLine, _vm.Subtitle, "AiChartReview_Hint", "AiChartReview_Keys"),
            _vm.HintText);
        Assert.Equal($"{_vm.Rows[0].Title} · {_vm.Rows[0].Detail}{Environment.NewLine}AiChartReview_Show_Tip", _vm.Rows[0].Tip);
        Assert.Equal($"{_vm.Rows[0].Title} · {_vm.Rows[0].Detail} · AiChartReview_StatePending", _vm.Rows[0].AccessibleName);

        await _run.RunAsync(AiChartJob.FhpAndFingering);

        Assert.Null(_vm.CurrentRow);
        Assert.All(_vm.Rows, r => Assert.False(r.IsCurrent));
    }

    [Fact]
    public async Task A_named_section_shows_its_span_in_the_time_column_an_unnamed_one_only_once()
    {
        await _run.RunAsync(AiChartJob.FhpAndFingering);

        Assert.Equal("0:00.0–0:08.0", _vm.Rows[0].Where);
        var unnamed = new AiChartReviewRowViewModel(_vm, new AiChartReviewChunk(
            new AiChartChunkPlan(9, new(0), new(8), null, []), 1000, 2500, "", new AiChartApplySummary(0, 1, 0, 0, 0, 0),
            AiChartApplyTargets.None), "x");
        Assert.Equal("0:01.0 – 0:02.5", unnamed.Name);
        Assert.Equal(string.Empty, unnamed.Where);
    }

    private sealed class Factory(IToneDesignModel model) : IToneDesignModelFactory
    {
        public IToneDesignModel Create(AiProviderSettings settings) => model;

        public bool Supports(AiProviderKind kind) => kind != AiProviderKind.None;
    }
}
