using DotCharter.Presentation.Contracts.Dialogs;
using DotCharter.Presentation.Screens.Dialogs;
using DotCharter.Core.Domain.Vocals;
using DotCharter.Services.Editing.Selection;
using DotCharter.UI.ViewModels.Editor;
using DotCharter.Services.Editing.Tracks;

namespace DotCharter.Tests.UI.ViewModels.Dialogs;

/// <summary>
///     The settings-style dialogs' Save goes through <c>IHistoryService</c>
///     (2026-08-11): ONE Save is ONE undo node and Ctrl+Z restores the prior
///     values. Before this, Song Metadata, Arrangement Settings and Vocal Track
///     Settings wrote the domain directly, so a dialog Save was the one edit in
///     the app that undo could not see. (Level Generation was already recorded
///     as a project snapshot by <c>ChartMaintenanceService.GenerateLevels</c>;
///     the Pattern Builder edits the app-level pattern STORE, not the chart, so
///     chart undo does not apply to it — both verified, not retrofitted.)
/// </summary>
public sealed class DialogSaveUndoTests
{
    private sealed class Harness
    {
        public Harness()
        {
            var logger = new FakeAppLogger();
            History = new HistoryService(logger);
            var config = new FakeConfigurationService();
            var selection = new SelectionService();

            var arrangement = new Arrangement { Name = "Lead", ArrangementType = ArrangementType.Lead };
            for (var i = 0; i < 6; i++) arrangement.TuningPitches.Add(new TuningOffset(0));
            arrangement.Levels.Add(new ChartLevel { DifficultyIndex = 0 });

            Project = new ChartProject();
            Project.Arrangements.Add(arrangement);
            Project.VocalTracks.Add(new VocalTrack { Name = "Vocals", ColorR = 1, ColorG = 2, ColorB = 3, ColorA = 255 });

            State.ActiveProject = Project;
            State.ActiveArrangement = arrangement;

            var transport = new TransportViewModel(new FakeAudioPlaybackService(), State, config, FakeLocalization.KeysOnly());
            Editor = new ChartEditorViewModel(State, transport, History, selection,
                new TempoService(logger), new TrackContextRegistry(State, selection), FakeLocalization.KeysOnly())
            {
                CurrentChart = Project.Chart,
                ActiveArrangement = arrangement,
                ActiveVocalTrack = Project.VocalTracks[0]
            };

            Logger = logger;
        }

        public ProjectState State { get; } = new();
        public ChartProject Project { get; }
        public ChartEditorViewModel Editor { get; }
        public HistoryService History { get; }
        public FakeAppLogger Logger { get; }
    }

    // ── Song metadata ─────────────────────────────────────────────────────

    [Fact]
    public void SongMetadataSave_IsOneUndoNode_AndUndoRestoresEveryField()
    {
        var h = new Harness();
        h.Project.Chart.Artist = "Old Artist";
        h.Project.Chart.SongName = "Old Song";
        h.Project.Chart.Genre = "Rock";
        h.Project.Chart.KeyAccidentals = 2;

        var vm = new SongMetadataViewModel(h.State, h.History, FakeLocalization.English(), h.Logger)
        {
            Artist = "New Artist",
            SongName = "New Song",
            Genre = "Jazz",
            KeyAccidentals = -3
        };
        vm.SaveCommand.Execute(null);

        Assert.Equal("New Artist", h.Project.Chart.Artist);
        Assert.Equal(-3, h.Project.Chart.KeyAccidentals);
        Assert.True(h.History.CanUndo, "the Save recorded no undo node");

        h.History.Undo();

        Assert.Equal("Old Artist", h.Project.Chart.Artist);
        Assert.Equal("Old Song", h.Project.Chart.SongName);
        Assert.Equal("Rock", h.Project.Chart.Genre);
        Assert.Equal(2, h.Project.Chart.KeyAccidentals);
        Assert.False(h.History.CanUndo, "one Save must be exactly one node");

        h.History.Redo();
        Assert.Equal("New Artist", h.Project.Chart.Artist);
    }

    [Fact]
    public void SongMetadataSave_WithNothingChanged_RecordsNothing()
    {
        var h = new Harness();
        var vm = new SongMetadataViewModel(h.State, h.History, FakeLocalization.English(), h.Logger);
        vm.SaveCommand.Execute(null);

        Assert.False(h.History.CanUndo, "an unchanged Save grew the undo tree");
    }

    // ── Arrangement settings ──────────────────────────────────────────────

    private static ArrangementSettingsViewModel NewArrangementSettings(Harness h) =>
        new(new ChartEditDialogServices(h.Editor, h.History, h.State, new FakeConfigurationService(),
            FakeLocalization.English(), h.Logger));

    [Fact]
    public void ArrangementSave_UndoRestoresTheArrangementInPlace()
    {
        var h = new Harness();
        var arrangement = h.Project.Arrangements[0];
        var vm = NewArrangementSettings(h);
        vm.Configure(new ArrangementSettingsRequest());
        vm.Name = "Rhythm";
        vm.Capo = 4;
        vm.SaveCommand.Execute(null);

        Assert.Equal("Rhythm", arrangement.Name);
        Assert.Equal(4, arrangement.Capo);
        Assert.True(h.History.CanUndo, "the Save recorded no undo node");

        h.History.Undo();

        // Restored IN PLACE: same object, prior values — bindings stay attached.
        Assert.Same(arrangement, h.Project.Arrangements[0]);
        Assert.Equal("Lead", arrangement.Name);
        Assert.Equal(0, arrangement.Capo);
    }

    [Fact]
    public void NewArrangementSave_IsUndoable_AndUndoRestoresTheActivePick()
    {
        var h = new Harness();
        var previous = h.Editor.ActiveArrangement;
        var vm = NewArrangementSettings(h);
        vm.Configure(new ArrangementSettingsRequest(CreateNew: true));
        vm.Name = "Bass";
        vm.SaveCommand.Execute(null);

        Assert.Equal(2, h.Project.Arrangements.Count);
        Assert.Equal("Bass", h.Editor.ActiveArrangement?.Name);

        h.History.Undo();
        Assert.Single(h.Project.Arrangements);
        Assert.Same(previous, h.Editor.ActiveArrangement);
    }
}
