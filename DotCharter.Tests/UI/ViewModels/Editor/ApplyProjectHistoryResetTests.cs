using DotCharter.Services.Editing.Selection;
using DotCharter.UI.ViewModels.Editor;
using DotCharter.Services.Editing.Tracks;

namespace DotCharter.Tests.UI.ViewModels.Editor;

/// <summary>
///     Loading a project must not inherit the previous project's undo stack —
///     nor its selection.
/// </summary>
/// <remarks>
///     Before this, only the checkpoints dialog cleared history on a graph swap.
///     Opening a second project left the first one's undo tree live: its actions hold
///     the OLD (now detached) collections, so Ctrl+Z silently did nothing while still
///     appearing available, the dirty flag flipped and put a phantom "*" on a project
///     the user had never touched, and the entire previous project — plus every
///     action's captured before/after state — was pinned for the session.
///     <para>
///         The flag is deliberately opt-IN, not automatic: <c>ApplyProject</c> is also
///         called from inside an undoable action (ChartMaintenanceService's snapshot
///         restore), where clearing the history would destroy the very stack being
///         replayed.
///     </para>
/// </remarks>
public sealed class ApplyProjectHistoryResetTests
{
    [Fact]
    public void Loading_a_project_drops_the_previous_projects_undo_stack()
    {
        var h = new Harness();
        h.PushAnEdit();
        Assert.True(h.History.CanUndo);

        h.Editor.ApplyProject(NewProject(), resetHistory: true);

        Assert.False(h.History.CanUndo);
        Assert.False(h.History.CanRedo);
    }

    [Fact]
    public void An_in_place_graph_swap_keeps_the_undo_stack()
    {
        // ChartMaintenanceService.RestoreProjectSnapshot swaps the graph from INSIDE an
        // undoable action. Clearing here would destroy the stack mid-replay.
        var h = new Harness();
        h.PushAnEdit();

        h.Editor.ApplyProject(NewProject()); // default: resetHistory false

        Assert.True(h.History.CanUndo);
    }

    [Fact]
    public void The_new_project_becomes_active_either_way()
    {
        var h = new Harness();
        var project = NewProject();

        h.Editor.ApplyProject(project, resetHistory: true);

        Assert.Same(project, h.State.ActiveProject);
        Assert.Equal("Fresh", h.State.ActiveArrangement?.Name);
    }

    [Fact]
    public void Loading_a_project_drops_the_previous_projects_selection()
    {
        var h = new Harness();
        h.Selection.SelectedNotes.Add(new GuitarNote());
        h.Selection.SelectedChords.Add(new Chord());

        h.Editor.ApplyProject(NewProject(), resetHistory: true);

        Assert.Empty(h.Selection.SelectedNotes);
        Assert.Empty(h.Selection.SelectedChords);
    }

    [Fact]
    public void An_in_place_graph_swap_drops_the_selection_but_keeps_the_undo_stack()
    {
        // The incoming graph is a fresh CLONE, so every selected instance belongs
        // to the graph being replaced even here — the selection must go even
        // though the undo stack has to survive.
        var h = new Harness();
        h.PushAnEdit();
        h.Selection.SelectedNotes.Add(new GuitarNote());

        h.Editor.ApplyProject(NewProject());

        Assert.Empty(h.Selection.SelectedNotes);
        Assert.True(h.History.CanUndo);
    }

    private static ChartProject NewProject()
    {
        var project = new ChartProject();
        project.Arrangements.Add(new Arrangement { Name = "Fresh" });
        return project;
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
            for (var i = 0; i < 8; i++) project.Beats.Add(new BeatMarker { Time = i * 500, Bpm = 120 });
            var arrangement = new Arrangement { Name = "Old", ArrangementType = ArrangementType.Lead };
            arrangement.Levels.Add(new ChartLevel());
            project.Arrangements.Add(arrangement);

            State.ActiveProject = project;
            State.ActiveArrangement = arrangement;
            State.ActiveLevel = arrangement.Levels[0];

            var transport = new TransportViewModel(new FakeAudioPlaybackService(), State, config, FakeLocalization.KeysOnly());
            var trackContexts = new TrackContextRegistry(State, Selection);
            Editor = new ChartEditorViewModel(State, transport, History, Selection, tempo, trackContexts, FakeLocalization.KeysOnly());
        }

        public ProjectState State { get; } = new();
        public SelectionService Selection { get; } = new();
        public HistoryService History { get; }
        public ChartEditorViewModel Editor { get; }

        /// <summary>Puts one undoable action on the stack.</summary>
        public void PushAnEdit()
        {
            var applied = false;
            History.Do(new LambdaAction(() => applied = true, () => applied = false));
            Assert.True(applied);
        }
    }

    private sealed class LambdaAction(Action execute, Action undo) : IChartAction
    {
        public void Execute() => execute();
        public void Undo() => undo();
        public bool HasEffect => true;
    }
}
