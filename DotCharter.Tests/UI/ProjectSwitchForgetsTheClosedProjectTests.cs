using DotCharter.Core.Domain.Vocals;
using DotCharter.Services.Editing.MultiTrack;
using DotCharter.Services.Editing.Selection;
using DotCharter.UI.ViewModels.Editor;
using DotCharter.Services.Editing.Tracks;

namespace DotCharter.Tests.UI;

/// <summary>
///     <c>ChartEditorViewModel.ApplyProject</c> is THE project-replace funnel —
///     File ▸ Open, Open Recent, a dropped <c>.rscp</c>, crash recovery, the song
///     browser, New Project, the EOF / Songsterr importers and checkpoint restore
///     all land here. These pin the two things it was still carrying forward from
///     the project it closed.
///     <para>
///         Structural assertions only, per the root <c>AGENTS.md</c> rule: which
///         object a field points at, and whether the closed graph is collectable —
///         never a byte count and never a wall clock.
///     </para>
/// </summary>
public sealed class ProjectSwitchForgetsTheClosedProjectTests
{
    // ── ActiveArrangement / ActiveLevel ──────────────────────────────────────

    /// <summary>
    ///     <b>The one that was a data-corruption bug, not a leak.</b>
    ///     <para>
    ///         <c>ApplyProject</c> guarded the assignment with
    ///         <c>if (project.Arrangements.Any())</c>, so opening a project with
    ///         none never entered <c>ProjectState.ActiveArrangement</c>'s setter —
    ///         and that setter is the thing that cascades <c>ActiveLevel</c>.
    ///         <c>ChartDocumentState.PlacedNotes/PlacedChords/HandShapes/Anchors</c>
    ///         and <c>IEditingScope</c> all resolve off <c>ActiveLevel</c>, so
    ///         every edit after such a switch landed in the CLOSED project's
    ///         level while the new project was on screen. The setter's own
    ///         comment documents this exact failure as fixed one layer down; the
    ///         guard reintroduced it from above.
    ///     </para>
    ///     <para>
    ///         Reachable: <c>XmlChartSerializer</c> handles vocals-only RS XML
    ///         explicitly (<c>VocalTracks.Count &gt; 0 &amp;&amp;
    ///         Arrangements.Count == 0</c>).
    ///     </para>
    /// </summary>
    [Fact]
    public void OpeningAProjectWithNoArrangements_DoesNotLeaveTheEditorPointedAtTheClosedProject()
    {
        var state = new ProjectState();
        var editor = BuildEditor(state);

        var first = BuildProject("A");
        editor.ApplyProject(first, resetHistory: true);

        Assert.Same(first.Arrangements[0], state.ActiveArrangement);
        Assert.Same(first.Arrangements[0].Levels[0], state.ActiveLevel);

        // A vocals-only project: no arrangements at all.
        var vocalsOnly = new ChartProject();
        vocalsOnly.VocalTracks.Add(new VocalTrack());
        editor.ApplyProject(vocalsOnly, resetHistory: true);

        Assert.Null(state.ActiveArrangement);

        // The cascade is the half that actually corrupted edits: an ActiveLevel
        // still pointing into project A is where every placement would land.
        Assert.Null(state.ActiveLevel);
    }

    /// <summary>
    ///     …and the closed project must be collectable. A stale
    ///     <c>ActiveArrangement</c> pins its whole chart graph — levels, notes,
    ///     chords, hand shapes, anchors, templates — for as long as it is held.
    /// </summary>
    [Fact]
    public void OpeningAProjectWithNoArrangements_LeavesTheClosedArrangementCollectable()
    {
        var state = new ProjectState();
        var editor = BuildEditor(state);

        var probe = OpenThenAbandon(editor);

        var vocalsOnly = new ChartProject();
        vocalsOnly.VocalTracks.Add(new VocalTrack());
        editor.ApplyProject(vocalsOnly, resetHistory: true);

        GC.Collect();
        GC.WaitForPendingFinalizers();
        GC.Collect();

        Assert.False(probe.IsAlive,
            "the arrangement of the closed project is still reachable after the switch — ActiveArrangement " +
            "or ActiveLevel is still pointing into it.");
    }

    /// <summary>
    ///     The normal case must not regress: a project WITH arrangements still
    ///     activates its first one. Without this the fix above could be
    ///     "satisfied" by never activating anything.
    /// </summary>
    [Fact]
    public void OpeningAProjectWithArrangements_StillActivatesTheFirstOne()
    {
        var state = new ProjectState();
        var editor = BuildEditor(state);

        editor.ApplyProject(BuildProject("A"), resetHistory: true);
        var second = BuildProject("B");
        editor.ApplyProject(second, resetHistory: true);

        Assert.Same(second.Arrangements[0], state.ActiveArrangement);
        Assert.Same(second.Arrangements[0].Levels[0], state.ActiveLevel);
    }

    // ── ghost + stacked arrangements ─────────────────────────────────────────

    /// <summary>
    ///     <see cref="IGhostArrangementsService" /> and
    ///     <see cref="IStackedArrangementsService" /> are app-wide singletons
    ///     keyed by <c>Arrangement</c> INSTANCE. Their <c>Clear()</c> had exactly
    ///     one product caller — <c>TimelineSolo</c> — and none on any
    ///     project-replace path, so every arrangement the session ever ghosted or
    ///     stacked stayed pinned for the life of the process.
    ///     <para>
    ///         Ghosts were the visible half: <c>ChartRenderer</c> walks
    ///         <c>VisibleGhosts</c> directly and reads <c>Levels[0].Notes</c> off
    ///         each entry, so the closed project's notes were drawn over the open
    ///         project's timeline.
    ///     </para>
    /// </summary>
    [Fact]
    public void SwitchingProjects_ClearsGhostAndStackedArrangements()
    {
        var state = new ProjectState();
        var ghosts = new GhostArrangementsService();
        var stacked = new StackedArrangementsService();
        var overlay = BuildOverlay(state, ghosts, stacked);

        var first = BuildProject("A", arrangements: 3);
        state.ActiveProject = first;
        state.ActiveArrangement = first.Arrangements[0];

        ghosts.SetVisible(first.Arrangements[1], true);
        stacked.SetStacked(first.Arrangements[2], true);
        Assert.Single(ghosts.VisibleGhosts);
        Assert.Single(stacked.StackedArrangements);

        state.ActiveProject = BuildProject("B", arrangements: 3);

        Assert.Empty(ghosts.VisibleGhosts);
        Assert.Empty(stacked.StackedArrangements);

        // Keep the overlay alive to the end of the case: it is the subscriber
        // under test, and nothing else references it.
        Assert.NotNull(overlay);
    }

    /// <summary>
    ///     Solo REMEMBERS what it hid so releasing puts exactly that back. Across
    ///     a switch those are the closed project's arrangements, so a solo left
    ///     engaged would both hold them and push them back into the services on
    ///     release. The mode drops with the memory.
    /// </summary>
    [Fact]
    public void SwitchingProjectsWhileSoloed_DropsTheSoloAndWhatItWasHolding()
    {
        var state = new ProjectState();
        var ghosts = new GhostArrangementsService();
        var stacked = new StackedArrangementsService();
        var overlay = BuildOverlay(state, ghosts, stacked);

        var first = BuildProject("A", arrangements: 3);
        state.ActiveProject = first;
        state.ActiveArrangement = first.Arrangements[0];
        ghosts.SetVisible(first.Arrangements[1], true);

        overlay.IsSoloed = true;
        Assert.True(overlay.IsSoloed);
        Assert.Empty(ghosts.VisibleGhosts); // solo hid it and remembered it

        state.ActiveProject = BuildProject("B", arrangements: 3);

        Assert.False(overlay.IsSoloed);

        // Releasing must not resurrect project A's arrangement.
        overlay.IsSoloed = true;
        overlay.IsSoloed = false;
        Assert.Empty(ghosts.VisibleGhosts);
    }

    // ── helpers ──────────────────────────────────────────────────────────────

    /// <summary>Kept out of the caller's frame so the locals it creates cannot
    /// be held alive by a stack slot the JIT has not yet reused.</summary>
    private static WeakReference OpenThenAbandon(ChartEditorViewModel editor)
    {
        var project = BuildProject("A");
        var probe = new WeakReference(project.Arrangements[0]);
        editor.ApplyProject(project, resetHistory: true);
        return probe;
    }

    private static ChartEditorViewModel BuildEditor(ProjectState state)
    {
        var logger = new FakeAppLogger();
        var selection = new SelectionService();
        var config = new FakeConfigurationService();
        var transport = new TransportViewModel(new FakeAudioPlaybackService(), state, config, FakeLocalization.KeysOnly());
        return new ChartEditorViewModel(
            state, transport, new HistoryService(logger), selection, new TempoService(logger),
            new TrackContextRegistry(state, selection), FakeLocalization.KeysOnly());
    }

    private static MultiTrackOverlayViewModel BuildOverlay(
        ProjectState state, IGhostArrangementsService ghosts, IStackedArrangementsService stacked)
    {
        var logger = new FakeAppLogger();
        var selection = new SelectionService();
        return new MultiTrackOverlayViewModel(
            ghosts, stacked,
            // Only ever stored into a MultiTrackEntry; none of the paths under
            // test invoke it, and stubbing its 25 members would be noise.
            editingCoordinator: DotCharter.Tests.Presentation.Dialogs.RecordingProxy.Create<DotCharter.Services.Editing.IGlobalEditingCoordinator>().Proxy,
            state,
            new TrackContextRegistry(state, selection),
            new FakeConfigurationService(),
            logger);
    }

    private static ChartProject BuildProject(string tag, int arrangements = 1)
    {
        var project = new ChartProject();
        for (var i = 0; i < arrangements; i++)
        {
            var arrangement = new Arrangement { Name = $"{tag}{i}", ArrangementType = ArrangementType.Lead };
            arrangement.Levels.Add(new ChartLevel());
            project.Arrangements.Add(arrangement);
        }

        return project;
    }
}
