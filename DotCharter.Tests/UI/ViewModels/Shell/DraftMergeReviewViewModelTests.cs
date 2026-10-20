using DotCharter.Services.Editing.DraftMerge;
using DotCharter.Services.Editing.FretAnchors;
using DotCharter.Services.Editing.Selection;
using DotCharter.Services.Editing.Tracks;
using DotCharter.Tests.Services.Editing.DraftMerge;
using DotCharter.UI.ViewModels.Editor;
using DotCharter.UI.ViewModels.Shell;
using static DotCharter.Tests.Services.Editing.DraftMerge.DraftMergeFixture;

namespace DotCharter.Tests.UI.ViewModels.Shell;

/// <summary>
///     Chart from audio's merge card (#647) between the held draft and the editor: one row per phrase
///     with its counts and confidence, the phrases marked on the timeline and the song map through the
///     editor's highlight, ✓ merging the phrase as ONE undo step, ✕ dropping it, and the card closing
///     with its last row. Fixture: <see cref="DraftMergeFixture" /> (intro 0, verse 4, chorus 8, END 12).
/// </summary>
public sealed class DraftMergeReviewViewModelTests
{
    private readonly DraftMergeFixture _f = new();
    private readonly ProjectState _state = new();
    private readonly HistoryService _history = new(new FakeAppLogger());
    private readonly ChartEditorViewModel _editor;
    private readonly DraftMergeReviewHost _host;
    private readonly DraftMergeReviewViewModel _vm;

    public DraftMergeReviewViewModelTests()
    {
        _state.ActiveProject = _f.Project;
        _state.ActiveArrangement = _f.Arrangement;
        _state.ActiveLevel = _f.Level;
        var logger = new FakeAppLogger();
        var selection = new SelectionService();
        var transport = new TransportViewModel(new FakeAudioPlaybackService(), _state, new FakeConfigurationService(), FakeLocalization.KeysOnly());
        _editor = new ChartEditorViewModel(_state, transport, _history, selection, new TempoService(logger),
            new TrackContextRegistry(_state, selection), FakeLocalization.KeysOnly());
        _host = new DraftMergeReviewHost(_history, new FretAnchorGenerationService());
        _vm = new DraftMergeReviewViewModel(_host, _editor, _history, _state, FakeLocalization.English());
    }

    private void HoldIntroVerseChorus(bool replace = false) =>
        _host.Hold(_f.Plan([Note(0, 0, 3), Note(1, 1, 5), Note(5, 2, 7), Note(9, 3, 2)], replace, [0.2, 0.4, 0.5, 0.9]));

    [Fact]
    public void A_held_draft_opens_the_card_with_one_row_per_phrase()
    {
        Assert.False(_vm.IsOpen);
        _f.Seed(Note(9, 0, 0));

        HoldIntroVerseChorus();

        Assert.True(_vm.IsOpen);
        Assert.Equal(["intro", "verse"], _vm.Rows.Select(r => r.Name));
        var intro = _vm.Rows[0];
        Assert.Equal("0:00.0–0:02.0", intro.Where);
        Assert.Equal("+2", intro.Detail);
        Assert.Equal(PercentText(0.3), intro.Confidence);
        Assert.False(intro.IsReplace);
        Assert.Contains("2 added, 0 changed, 0 removed", intro.AccessibleName, StringComparison.Ordinal);
        Assert.Equal("2 phrases", _vm.CountsText);
        Assert.True(_vm.HasOccupied);
        Assert.Equal("1 phrase with notes left alone", _vm.OccupiedText);
        Assert.True(_vm.MergeAllCommand.CanExecute(null));
    }

    [Fact]
    public void The_cards_phrases_are_marked_on_the_timeline_and_the_song_map()
    {
        HoldIntroVerseChorus();

        var (_, phrases, proposed) = _editor.UseHighlight.EventsOn(_f.Arrangement);
        Assert.Equal(3, phrases.Count);
        Assert.Contains(_f.Arrangement.EventPoints[0], phrases);
        Assert.Equal(["intro", "verse", "chorus"], proposed.Select(e => e.Phrase));
        Assert.Same(_vm.Published, _editor.UseHighlight);

        _vm.DiscardCommand.Execute(null);

        Assert.False(_vm.IsOpen);
        Assert.True(_editor.UseHighlight.IsEmpty);
        Assert.False(_history.CanUndo);
    }

    [Fact]
    public void A_rows_merge_is_one_undo_step_and_the_row_leaves()
    {
        var before = ChartProjectCloner.Clone(_f.Project);
        HoldIntroVerseChorus();

        _vm.Rows[1].MergeCommand.Execute(null);

        Assert.Equal(["intro", "chorus"], _vm.Rows.Select(r => r.Name));
        Assert.Single(_f.Level.Notes);
        Assert.Single(_history.GetUndoSteps());
        Assert.Equal(FakeLocalization.English().Format("DraftMerge_Merged", "verse"), _state.MainDisplayMessage);
        Assert.Equal(2, _editor.UseHighlight.EventsOn(_f.Arrangement).Proposed.Count);

        _history.Undo();
        Assert.Empty(DeepCompare.Diff("project", before, _f.Project, new HashSet<string>()));
    }

    [Fact]
    public void A_rows_reject_writes_nothing_and_the_last_row_closes_the_card()
    {
        _host.Hold(_f.Plan([Note(0, 0, 3)]));

        _vm.Rows[0].RejectCommand.Execute(null);

        Assert.False(_vm.IsOpen);
        Assert.Empty(_vm.Rows);
        Assert.Empty(_f.Level.Notes);
        Assert.False(_history.CanUndo);
    }

    [Fact]
    public void The_keyboard_goes_to_a_row_then_merges_or_drops_it()
    {
        HoldIntroVerseChorus();

        Assert.Same(_vm.Rows[0], _vm.Step(1));
        Assert.Same(_vm.Rows[1], _vm.Step(1));
        Assert.Equal(2000, _state.CurrentTimeMs, 1);
        _vm.MergeCurrent();
        Assert.Single(_history.GetUndoSteps());
        Assert.Null(_vm.CurrentRow);

        _vm.Step(-1);
        _vm.RejectCurrent();
        Assert.Equal(["chorus"], _vm.Rows.Select(r => r.Name));
        Assert.Single(_history.GetUndoSteps());
    }

    [Fact]
    public void After_a_decision_down_goes_to_the_row_that_took_its_place()
    {
        HoldIntroVerseChorus();
        _vm.Step(1);
        _vm.Step(1);

        _vm.RejectCurrent();

        Assert.Null(_vm.CurrentRow);
        Assert.Equal("chorus", _vm.Step(1)!.Name);
        _vm.RejectCurrent();
        Assert.Equal("intro", _vm.Step(-1)!.Name);
    }

    [Fact]
    public void A_replacing_row_says_so_and_merge_all_is_one_undo_step()
    {
        _f.Seed(Note(5, 2, 5));
        HoldIntroVerseChorus(replace: true);
        Assert.True(_vm.Rows[1].IsReplace);
        Assert.Equal("~1", _vm.Rows[1].Detail);
        Assert.False(_vm.HasOccupied);

        _vm.MergeAllCommand.Execute(null);

        Assert.False(_vm.IsOpen);
        Assert.Single(_history.GetUndoSteps());
        Assert.Equal(4, _f.Level.Notes.Count);
    }

    [Fact]
    public void An_unnamed_span_is_named_for_what_it_is()
    {
        _f.Arrangement.EventPoints[0].Position = new FractionalPosition(2);
        _host.Hold(_f.Plan([Note(0, 0, 3)]));
        Assert.Equal(FakeLocalization.English()["DraftMerge_BeforeFirstPhrase"], _vm.Rows[0].Name);

        _f.Arrangement.EventPoints.Clear();
        _host.Hold(_f.Plan([Note(0, 0, 3)]));
        Assert.Equal(FakeLocalization.English()["DraftMerge_WholeSong"], _vm.Rows[0].Name);
    }

    private static string PercentText(double share) => DotCharter.Presentation.Shared.PercentText.Format(share, 0);
}
