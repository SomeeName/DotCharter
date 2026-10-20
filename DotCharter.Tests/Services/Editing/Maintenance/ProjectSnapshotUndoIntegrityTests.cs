using DotCharter.Services.Editing;
using DotCharter.Services.Editing.Fingering;
using DotCharter.Services.Editing.FretAnchors;
using DotCharter.Services.Editing.Fixing;
using DotCharter.Services.Editing.Phrases;
using DotCharter.Services.Editing.Refret;
using DotCharter.Services.Editing.Selection;
using DotCharter.UI.ViewModels.Editor;
using DotCharter.Services.Editing.Tracks;
using DotCharter.Services.Editing.Maintenance;
using DotCharter.Services.Session;

namespace DotCharter.Tests.Services.Editing.Maintenance;

/// <summary>
///     A whole-project tool (Refret, level generation, the tempo-map tools) records
///     itself as a <c>ProjectSnapshotAction</c>. That action used to CLONE the
///     snapshot and swap the fresh graph in, which detached every other action in
///     the history: <c>AddNotesAction</c> holds the <c>ObservableCollection</c> it
///     inserted into and <c>MutateNotesAction</c> holds the individual
///     <c>GuitarNote</c> instances, so after such a tool Ctrl+Z hit an orphaned
///     graph — nothing visible happened while the history claimed it had undone —
///     and redoing the tool installed yet another clone, throwing away every edit
///     recorded after it.
///     <para>
///         The snapshot is now reconciled INTO the live graph, so the instances the
///         history captured stay attached. These tests drive the real coordinator;
///         <c>AddMeasuresAtStart</c> is the tool because it rewrites every position
///         without changing any collection's length, so the whole reconcile is the
///         instance-preserving path.
///     </para>
/// </summary>
public sealed class ProjectSnapshotUndoIntegrityTests
{
    [Fact]
    public void A_snapshot_tool_keeps_the_live_project_collection_and_element_instances()
    {
        var h = new Harness();
        var project = h.State.ActiveProject!;
        var level = h.State.ActiveLevel!;
        var notes = level.Notes;
        var firstNote = notes[0];

        h.Maintenance.AddMeasuresAtStart(1);

        Assert.Same(project, h.State.ActiveProject);
        Assert.Same(level, h.State.ActiveLevel);
        Assert.Same(notes, h.State.ActiveLevel!.Notes);
        Assert.Same(firstNote, h.State.ActiveLevel!.Notes[0]);
        // ...and the tool really did its work on those same instances.
        Assert.Equal(new FractionalPosition(4), firstNote.Position);
    }

    /// <summary>
    ///     WHICH ARRANGEMENT IS ON SCREEN IS VIEW STATE, NOT CHART STATE.
    ///     <para>
    ///         The snapshot action records the active arrangement/level/vocal
    ///         track when it RUNS, and used to write all three back on undo. So
    ///         a charter who ran a tempo tool on Lead, switched to Rhythm and
    ///         pressed Ctrl+Z was thrown back to Lead — reported as "switching
    ///         arrangements is not an undo step, so undo must not make one".
    ///     </para>
    ///     <para>
    ///         The recorded indices survive as a FALLBACK for the case they were
    ///         added for: a SHRINKING snapshot drops the instance the charter had
    ///         selected, and leaving it selected would let every later edit write
    ///         into a detached object. That half is pinned below.
    ///     </para>
    /// </summary>
    [Fact]
    public void Undo_does_not_switch_the_arrangement_the_user_is_looking_at()
    {
        var h = new Harness();
        var project = h.State.ActiveProject!;
        var lead = h.State.ActiveArrangement!;

        var rhythm = new Arrangement { Name = "Rhythm", ArrangementType = ArrangementType.Rhythm };
        rhythm.Levels.Add(new ChartLevel());
        project.Arrangements.Add(rhythm);

        // A snapshot tool on Lead…
        h.Maintenance.AddMeasuresAtStart(1);

        // …then the charter moves to Rhythm and undoes it.
        h.State.ActiveArrangement = rhythm;
        h.State.ActiveLevel = rhythm.Levels[0];
        h.History.Undo();

        Assert.Same(rhythm, h.State.ActiveArrangement);
        Assert.Same(rhythm.Levels[0], h.State.ActiveLevel);
        _ = lead;
    }

    [Fact]
    public void Redo_does_not_switch_it_either()
    {
        var h = new Harness();
        var project = h.State.ActiveProject!;

        var rhythm = new Arrangement { Name = "Rhythm", ArrangementType = ArrangementType.Rhythm };
        rhythm.Levels.Add(new ChartLevel());
        project.Arrangements.Add(rhythm);

        h.Maintenance.AddMeasuresAtStart(1);
        h.History.Undo();

        h.State.ActiveArrangement = rhythm;
        h.State.ActiveLevel = rhythm.Levels[0];
        h.History.Redo();

        Assert.Same(rhythm, h.State.ActiveArrangement);
    }

    /// <summary>
    ///     The fallback the recorded index is FOR: when the arrangement the
    ///     charter had selected did not survive the restore, undo has to land
    ///     them somewhere real rather than leave a detached object selected.
    /// </summary>
    [Fact]
    public void AnArrangementThatDidNotSurviveTheRestore_IsReplacedNotKept()
    {
        var h = new Harness();
        var project = h.State.ActiveProject!;

        h.Maintenance.AddMeasuresAtStart(1);

        // Something the snapshot has never heard of.
        var ghost = new Arrangement { Name = "Ghost" };
        ghost.Levels.Add(new ChartLevel());
        h.State.ActiveArrangement = ghost;

        h.History.Undo();

        Assert.NotSame(ghost, h.State.ActiveArrangement);
        Assert.Contains(h.State.ActiveArrangement!, project.Arrangements);
    }

    [Fact]
    public void Undo_after_a_snapshot_tool_still_reaches_the_edit_recorded_before_it()
    {
        var h = new Harness();
        var level = h.State.ActiveLevel!;

        // An ordinary edit. MutateNotesAction captures the NOTE INSTANCE, which is
        // what a graph swap orphaned.
        h.History.Do(new MutateNotesAction(level.Notes, new[] { level.Notes[0] }, n => n.Fret = 21));
        Assert.Equal(21, level.Notes[0].Fret);

        h.Maintenance.AddMeasuresAtStart(1);
        Assert.Equal(36, h.State.ActiveProject!.Beats.Count);

        h.History.Undo(); // the tool
        Assert.Equal(32, h.State.ActiveProject!.Beats.Count);

        h.History.Undo(); // the edit before it — used to mutate a detached graph
        Assert.Equal(3, h.State.ActiveLevel!.Notes[0].Fret);
    }

    [Fact]
    public void Redoing_a_snapshot_tool_does_not_lose_the_edits_recorded_after_it()
    {
        var h = new Harness();

        h.Maintenance.AddMeasuresAtStart(1);
        var level = h.State.ActiveLevel!;
        h.History.Do(new MutateNotesAction(level.Notes, new[] { level.Notes[1] }, n => n.Fret = 19));

        h.History.Undo(); // the later edit
        h.History.Undo(); // the tool
        h.History.Redo(); // the tool
        h.History.Redo(); // the later edit — used to land in an orphaned clone

        Assert.Equal(36, h.State.ActiveProject!.Beats.Count);
        Assert.Equal(19, h.State.ActiveLevel!.Notes[1].Fret);
    }

    private sealed class Harness
    {
        public Harness()
        {
            var logger = new FakeAppLogger();
            History = new HistoryService(logger);
            var tempo = new TempoService(logger);
            var config = new FakeConfigurationService();

            var project = new ChartProject();
            for (var i = 0; i < 32; i++)
            {
                project.Beats.Add(new BeatMarker
                {
                    // 2 s of room before the first beat: AddMeasuresAtStart adds only
                    // whole measures that fit before 0:00 (#282), and these tests
                    // need it to add one.
                    Time = 2000 + i * 500, Bpm = 120, BeatsInMeasure = 4, IsMeasure = i % 4 == 0
                });
            }

            var arrangement = new Arrangement { Name = "Lead", ArrangementType = ArrangementType.Lead };
            var level = new ChartLevel();
            for (var beat = 0; beat < 6; beat++)
            {
                level.Notes.Add(new GuitarNote
                {
                    Position = new FractionalPosition(beat),
                    StringIndex = beat % 6,
                    Fret = 3 + beat % 3
                });
            }

            arrangement.Levels.Add(level);
            project.Arrangements.Add(arrangement);

            State.ActiveProject = project;
            State.ActiveArrangement = arrangement;
            State.ActiveLevel = level;

            var selection = new SelectionService();
            var transport = new TransportViewModel(new FakeAudioPlaybackService(), State, config, FakeLocalization.KeysOnly());
            var trackContexts = new TrackContextRegistry(State, selection);
            var editor = new ChartEditorViewModel(State, transport, History, selection, tempo, trackContexts, FakeLocalization.KeysOnly());

            Maintenance = new ChartMaintenanceService(
                config,
                new NoOpImprovementService(),
                new NoOpSquashService(),
                new LevelGenerationService(),
                new PhraseIterationPropagationService(History, new EditingScope(State), logger),
                new NoOpAnchorGenerationService(),
                new NoOpProjectSessionService(),
                History,
                logger,
                State,
                editor,
                new NoOpAutoFixService(),
                new RefretService(new FretAnchorGenerationService(), new FingeringGenerationService()),
                FakeLocalization.English());
        }

        public ProjectState State { get; } = new();
        public HistoryService History { get; }
        public ChartMaintenanceService Maintenance { get; }
    }

    private sealed class NoOpImprovementService : IChartImprovementService
    {
        public ChartImprovementResult ImproveProject(ChartProject? project, AppConfig config,
            double totalAudioLengthMs) => new();
    }

    private sealed class NoOpSquashService : ILevelSquashService
    {
        public bool TrySquash(Arrangement arrangement) => false;
    }

    private sealed class NoOpAnchorGenerationService : IFretAnchorGenerationService
    {
        public IReadOnlyList<FretAnchor> GenerateAnchors(ChartLevel level,
            FractionalPosition? rangeStart = null, FractionalPosition? rangeEnd = null,
            IReadOnlyList<ChordTemplate>? chordTemplates = null, int capo = 0,
            bool bassArrangement = false, Func<ChordTemplate, ChordTemplate?>? freeGrip = null,
            Arrangement? tuning = null) =>
            Array.Empty<FretAnchor>();
    }

    private sealed class NoOpAutoFixService : IArrangementAutoFixService
    {
        public void Fix(Arrangement arrangement, ArrangementFixResult result) { }
    }

    private sealed class NoOpProjectSessionService : IProjectSessionService
    {
        public AppConfig AppConfig { get; } = new();
        public bool IsProjectDirty => false;

        /// <summary>No shell is listening, so a replace is always allowed.</summary>
        public Func<Task<bool>>? ConfirmDiscardAsync { get; set; }

        public Task<bool> MayReplaceOpenProjectAsync() => Task.FromResult(true);
        public void MarkProjectClean() { }
        public void MarkProjectDirty() { }
        public event Action? ProjectDirtyChanged { add { } remove { } }
        public void Initialize() { }
        public void LoadAppConfig() { }
        public void SaveAppConfig() { }
        public void FreeAudio() { }
        public Task LoadFile(string filePath) => Task.CompletedTask;

        public Task LoadAudioFile(string filePath, bool generateBeats = true, bool preferMemoryPlayback = false)
            => Task.CompletedTask;

        public Task<bool> AttachAudioAsync(string sourcePath) => Task.FromResult(true);
        public Task LoadSilentTransportAsync(double projectLengthMs) => Task.CompletedTask;
        public Task OpenProject() => Task.CompletedTask;
        public Task<bool> SaveProjectAsync() => Task.FromResult(true);
        public Task<bool> SaveProjectAsAsync() => Task.FromResult(true);
        public IReadOnlyList<RecentProjectEntry> GetRecentProjects() => [];
        public event Action? RecentProjectsChanged { add { } remove { } }
        public void Dispose() { }
    }
}
