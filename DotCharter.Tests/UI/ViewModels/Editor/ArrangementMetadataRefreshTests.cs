using System.ComponentModel;
using DotCharter.Services.Editing.MultiTrack;
using DotCharter.Services.Editing.Selection;
using DotCharter.Presentation.Contracts.Dialogs;
using DotCharter.Presentation.Screens.Dialogs;
using DotCharter.UI.ViewModels.Editor;
using DotCharter.Services.Editing.Tracks;

namespace DotCharter.Tests.UI.ViewModels.Editor;

/// <summary>
///     <c>Arrangement</c> is a plain domain object — the parsers and writers share
///     it, so it deliberately raises no <c>PropertyChanged</c>. Four shell surfaces
///     nevertheless bind straight through it: the toolbar's Tracks button prints
///     <c>ChartEditor.ActiveArrangement.Name</c>, the Tracks popover's picker face
///     prints that plus <c>ArrangementTuningConverter</c> over the arrangement
///     itself, its picker ROWS print the same pair per <c>MultiTrackEntry</c>, and
///     the Tools popover's drop-tune label carries the tuning name.
///     <para>
///         So renaming or retuning an arrangement in the Arrangement Settings
///         dialog changed the model and NOTHING on screen. Re-publishing the chart
///         (which the Save already did) does not help: the name and the tuning are
///         not on <c>ChartData</c>. Every one of those cells kept the value the
///         arrangement had when it was last SWITCHED to, and only picking a
///         different arrangement and coming back fixed it.
///     </para>
/// </summary>
public sealed class ArrangementMetadataRefreshTests
{
    private sealed class Harness
    {
        public Harness()
        {
            var logger = new FakeAppLogger();
            var history = new HistoryService(logger);
            var tempo = new TempoService(logger);
            var config = new FakeConfigurationService();

            var project = new ChartProject();
            for (var i = 0; i < 8; i++)
                project.Beats.Add(new BeatMarker { Time = i * 500, Bpm = 120, BeatsInMeasure = 4, IsMeasure = i % 4 == 0 });

            Lead = NewArrangement("Lead");
            Rhythm = NewArrangement("Rhythm");
            project.Arrangements.Add(Lead);
            project.Arrangements.Add(Rhythm);

            State.ActiveProject = project;
            State.ActiveArrangement = Lead;
            State.ActiveLevel = Lead.Levels[0];

            Registry = new TrackContextRegistry(State, Selection);
            Overlay = new MultiTrackOverlayViewModel(
                new GhostArrangementsService(), new StackedArrangementsService(),
                // Stored into a MultiTrackEntry only; nothing under test invokes it.
                editingCoordinator: DotCharter.Tests.Presentation.Dialogs.RecordingProxy.Create<DotCharter.Services.Editing.IGlobalEditingCoordinator>().Proxy,
                State, Registry, config, logger);

            var transport = new TransportViewModel(new FakeAudioPlaybackService(), State, config, FakeLocalization.KeysOnly());
            Editor = new ChartEditorViewModel(State, transport, history, Selection, tempo, Registry, FakeLocalization.KeysOnly())
            {
                CurrentChart = project.Chart
            };
            Settings = new ArrangementSettingsViewModel(
                new ChartEditDialogServices(Editor, history, State, config, FakeLocalization.English(), logger));
        }

        public ProjectState State { get; } = new();
        public SelectionService Selection { get; } = new();
        public TrackContextRegistry Registry { get; }
        public MultiTrackOverlayViewModel Overlay { get; }
        public ChartEditorViewModel Editor { get; }
        public ArrangementSettingsViewModel Settings { get; }
        public Arrangement Lead { get; }
        public Arrangement Rhythm { get; }

        private static Arrangement NewArrangement(string name)
        {
            var arrangement = new Arrangement { Name = name, ArrangementType = ArrangementType.Lead };
            for (var i = 0; i < 6; i++) arrangement.TuningPitches.Add(new TuningOffset(0));
            arrangement.Levels.Add(new ChartLevel { DifficultyIndex = 0 });
            return arrangement;
        }
    }

    private static List<string> Record(INotifyPropertyChanged source)
    {
        var seen = new List<string>();
        source.PropertyChanged += (_, e) => seen.Add(e.PropertyName ?? "");
        return seen;
    }

    [Fact]
    public void SavingARename_AnnouncesTheActiveArrangement_SoTheToolbarAndPickerFaceReread()
    {
        var h = new Harness();
        var seen = Record(h.Editor);

        h.Settings.Configure(new ArrangementSettingsRequest());
        h.Settings.Name = "Rhythm Guitar";
        h.Settings.SaveCommand.Execute(null);

        Assert.Equal("Rhythm Guitar", h.Lead.Name);
        Assert.Contains(nameof(ChartEditorViewModel.ActiveArrangement), seen);
    }

    [Fact]
    public void SavingARename_RefreshesTheTracksPopoversOwnPickerRow()
    {
        var h = new Harness();
        var row = Assert.Single(h.Overlay.AllEntries, e => ReferenceEquals(e.Arrangement, h.Lead));
        var seen = Record(row);

        h.Settings.Configure(new ArrangementSettingsRequest());
        h.Settings.Name = "Rhythm Guitar";
        h.Settings.SaveCommand.Execute(null);

        // AllEntries is the collection the PICKER binds — it has to include the
        // row you are already on, so it is never rebuilt by the active-flag pass
        // and its cells go stale without an explicit announcement.
        Assert.Equal("Rhythm Guitar", row.Name);
        Assert.Contains(nameof(MultiTrackEntry.Name), seen);
        Assert.Contains(nameof(MultiTrackEntry.Arrangement), seen);
    }

    [Fact]
    public void SavingARetune_RefreshesTheRowsThatPrintTheTuning()
    {
        var h = new Harness();
        var row = Assert.Single(h.Overlay.AllEntries, e => ReferenceEquals(e.Arrangement, h.Lead));
        var seen = Record(row);

        h.Settings.Configure(new ArrangementSettingsRequest());
        h.Settings.TuningOffsets[0].Step = -2;   // Drop D
        h.Settings.SaveCommand.Execute(null);

        Assert.Equal(-2, h.Lead.TuningPitches[0].Step);
        // The tuning cell binds the ARRANGEMENT through a converter, so the
        // arrangement itself is what has to be re-announced.
        Assert.Contains(nameof(MultiTrackEntry.Arrangement), seen);
    }

    [Fact]
    public void UndoingTheRename_RefreshesTheSameRows()
    {
        var h = new Harness();
        h.Settings.Configure(new ArrangementSettingsRequest());
        h.Settings.Name = "Rhythm Guitar";
        h.Settings.SaveCommand.Execute(null);

        var row = Assert.Single(h.Overlay.AllEntries, e => ReferenceEquals(e.Arrangement, h.Lead));
        var seen = Record(row);

        h.Editor.UndoCommand.Execute(null);

        Assert.Equal("Lead", h.Lead.Name);
        Assert.Contains(nameof(MultiTrackEntry.Name), seen);
    }

    [Fact]
    public void TheAnnouncementDoesNotChangeWhichArrangementIsActive()
    {
        var h = new Harness();

        h.Settings.Configure(new ArrangementSettingsRequest());
        h.Settings.Name = "Rhythm Guitar";
        h.Settings.SaveCommand.Execute(null);

        Assert.Same(h.Lead, h.State.ActiveArrangement);
        Assert.True(Assert.Single(h.Overlay.AllEntries, e => ReferenceEquals(e.Arrangement, h.Lead)).IsActive);
        Assert.False(Assert.Single(h.Overlay.AllEntries, e => ReferenceEquals(e.Arrangement, h.Rhythm)).IsActive);
    }
}
