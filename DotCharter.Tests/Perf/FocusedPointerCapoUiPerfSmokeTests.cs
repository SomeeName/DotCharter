using Avalonia;
using Avalonia.Input;
using DotCharter.Services.Editing;
using DotCharter.Services.Editing.Highway;
using DotCharter.Tests.Services.Editing.Highway;
using DotCharter.UI;
using DotCharter.UI.Controls;
using DotCharter.UI.Editing.Tracks;
using DotCharter.UI.Input;
using DotCharter.UI.Input.Tools;
using DotCharter.UI.ViewModels.Shell;
using Microsoft.Extensions.DependencyInjection;

namespace DotCharter.Tests.Perf;

[Collection("HeadlessUI")]
public sealed class FocusedPointerCapoUiPerfSmokeTests
{
    [Fact]
    public Task WheelStepsAcrossTheFocusedCapo_AndPlacementUndoRedoRestoresTheChart() =>
        HeadlessSessionFixture.Dispatch(() => WithEditor((vm, editing, history, lead, bass) =>
        {
            var state = vm.ProjectState;
            bass.Capo = 5;
            vm.ChartEditor.TrackContexts.Focus(bass);
            state.PreviewNoteFret = 5;
            vm.ChartEditor.PreviewNotes.Add(new GuitarNote { Fret = 5 });
            var original = FocusedHighwayEditingTests.ChartBytes(state.ActiveProject!);
            var beat = 2;
            foreach (var (delta, expected) in new[] { (-1, 0), (1, 5) })
            {
                vm.ChartEditor.PreviewNotes.Clear();
                vm.ChartEditor.PreviewNotes.Add(new GuitarNote { Fret = state.PreviewNoteFret });
                Assert.Equal(WheelAdjustmentResult.Changed, TimelineWheelAdjuster.TryAdjustPreviewNoteFret(vm, delta));
                Assert.Equal(expected, state.PreviewNoteFret);
                Assert.Equal(expected, Assert.Single(vm.ChartEditor.PreviewNotes).Fret);
                Assert.Equal(original, FocusedHighwayEditingTests.ChartBytes(state.ActiveProject!));
                Assert.False(history.CanUndo);
                editing.ApplyPlacement(new FractionalPosition(beat++), 0);
                Assert.Equal(expected, Assert.Single(bass.Levels[0].Notes).Fret);
                Assert.Empty(lead.Levels[0].Notes);
                AssertUndoRedo(state, history, original);
                history.Undo();
                history.Clear();
            }
        }), CancellationToken.None);

    [Fact]
    public Task HoveringAnotherTrack_AdvertisesThePlacedFretWithoutChangingFocus() =>
        HeadlessSessionFixture.Dispatch(() => WithEditor((vm, editing, history, lead, bass) =>
        {
            foreach (var (leadCapo, bassCapo) in new[] { (5, 0), (0, 5) })
            {
                history.Clear();
                lead.Levels[0].Notes.Clear();
                lead.Capo = leadCapo;
                bass.Capo = bassCapo;
                var tracks = vm.ChartEditor.TrackContexts;
                tracks.Focus(bassCapo == 0 ? bass : lead);
                vm.ProjectState.PreviewNoteFret = 3;
                tracks.Focus(bass);
                var track = tracks.GetOrCreate(lead);
                var context = new ToolContext(vm, new Viewport(), tracks)
                {
                    Track = track, Position = new Point(200, 120), StringIndex = 0, ExactTimeMs = 1000,
                    Properties = default
                };
                track.Selection.PreviewArrangement = lead;
                using var tool = new HighwayInteractionTool(editing, history, new FakeAppLogger());
                tool.OnPointerMoved(null!, context);
                var advertised = Assert.Single(track.Selection.PreviewNotes).Fret;
                Assert.Same(bass, tracks.FocusedTrack!.Arrangement);
                Assert.Same(lead, vm.ProjectState.ActiveArrangement);
                var before = FocusedHighwayEditingTests.ChartBytes(vm.ProjectState.ActiveProject!);

                tracks.Focus(lead);
                var verb = HighwayRightClick.Resolve(null, null, 0, KeyModifiers.None);
                Assert.True(HighwayRightClick.Apply(editing, verb, null, null, context.SnappedPosition, 0));
                var note = Assert.Single(lead.Levels[0].Notes);
                Assert.Equal(advertised, note.Fret);
                Assert.Equal(leadCapo == 0 ? 3 : 5, note.Fret);
                AssertUndoRedo(vm.ProjectState, history, before);
            }
        }), CancellationToken.None);

    private static void AssertUndoRedo(ProjectState state, IHistoryService history, byte[] before)
    {
        var after = FocusedHighwayEditingTests.ChartBytes(state.ActiveProject!);
        history.Undo();
        Assert.Equal(before, FocusedHighwayEditingTests.ChartBytes(state.ActiveProject!));
        history.Redo();
        Assert.Equal(after, FocusedHighwayEditingTests.ChartBytes(state.ActiveProject!));
    }

    private static void WithEditor(Action<MainWindowViewModel, IHighwayEditingService, IHistoryService, Arrangement, Arrangement> body)
    {
        var services = App.Services!;
        var vm = services.GetRequiredService<MainWindowViewModel>();
        var state = vm.ProjectState;
        var history = services.GetRequiredService<IHistoryService>();
        var editing = services.GetRequiredService<IHighwayEditingService>();
        var previous = state.ActiveProject;
        var arrangement = state.ActiveArrangement;
        var level = state.ActiveLevel;
        var previewFret = state.EditorUi.PreviewNoteFret;
        var project = new ChartProject();
        var lead = new Arrangement { Name = "Lead", Levels = { new ChartLevel() } };
        var bass = new Arrangement { Name = "Bass", Tuning = ["E", "A", "D", "G"], Levels = { new ChartLevel() } };
        project.Arrangements.Add(lead);
        project.Arrangements.Add(bass);
        for (var i = 0; i < 16; i++) project.Beats.Add(new BeatMarker { Time = i * 500, Bpm = 120 });
        state.ActiveProject = project;
        state.ActiveArrangement = lead;
        state.ActiveLevel = lead.Levels[0];
        services.GetRequiredService<IEditingScope>().SetResolver(() =>
            vm.ChartEditor.TrackContexts.FocusedTrack is { Level: { } targetLevel } track
                ? new EditingTarget(track.Arrangement, targetLevel)
                : null);
        history.Clear();
        vm.ChartEditor.Selection.ClearSelections();
        vm.ChartEditor.Selection.PreviewNotes.Clear();
        vm.ChartEditor.Selection.PreviewArrangement = null;
        try { body(vm, editing, history, lead, bass); }
        finally
        {
            history.Clear();
            vm.ChartEditor.Selection.PreviewNotes.Clear();
            state.ActiveProject = previous;
            state.ActiveArrangement = arrangement;
            state.ActiveLevel = level;
            state.EditorUi.PreviewNoteFret = previewFret;
        }
    }

    private sealed class Viewport : IChartViewport
    {
        public double ViewportWidth => 800;
        public double ViewportHeight => 300;
        public double TopMargin => 40;
        public double BottomMargin => 40;
        public bool LeftyFlip => false;
        public bool InvertStrings => false;
        public RibbonLayout TopRibbon => default;
        public void RequestNextFrameRendering() { }
        public void SetMarquee(Rect? marquee) { }
        public void ShowContextMenu(IReadOnlyList<TimelineMenuItem> items) { }
    }
}
