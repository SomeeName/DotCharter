using DotCharter.Services.Editing.Selection;
using DotCharter.Services.Editing.Tracks;

namespace DotCharter.Tests.UI;

/// <summary>
///     <para>
///         <see cref="TrackContextRegistry" /> is an app-wide SINGLETON that keys a
///         <see cref="TrackEditorContext" /> off the <see cref="Arrangement" /> INSTANCE.
///         Until 0.48 it only ever added: <c>GetOrCreate</c> inserted, and no method on
///         the type removed. Every project the session ever opened therefore stayed
///         reachable through <c>_contexts</c>'s keys — arrangements, levels, notes,
///         chords, hand shapes, anchors, chord templates and event points — for the life
///         of the process. <c>SyncProjectOnAllContexts</c> made it worse by walking the
///         dead contexts on every switch and repointing their <c>Project</c> at the new
///         one, so they kept looking live while growing linearly with opens.
///     </para>
///     <para>
///         These pin the invariant STRUCTURALLY — "after a switch the registry holds
///         contexts only for the arrangements of the project that is open" and "the
///         previous project is collectable" — rather than asserting a byte count, which
///         would flake. The retained-bytes figure is LOGGED, not asserted.
///     </para>
/// </summary>
public sealed class TrackContextRegistryProjectSwitchTests
{
    private const int ArrangementsPerProject = 5;

    /// <summary>The registry must not carry a previous project's arrangements forward.</summary>
    [Fact]
    public void SwitchingProjects_LeavesNoContextForAnArrangementOfAClosedProject()
    {
        var state = new ProjectState();
        var selection = new SelectionService();
        using var registry = new TrackContextRegistry(state, selection);

        var first = BuildProject("A");
        OpenProject(state, registry, first);

        Assert.Equal(ArrangementsPerProject, registry.Tracks.Count);

        var second = BuildProject("B");
        OpenProject(state, registry, second);

        // Only the OPEN project's arrangements may still have a context.
        Assert.Equal(ArrangementsPerProject, registry.Tracks.Count);
        foreach (var ctx in registry.Tracks)
            Assert.Contains(ctx.Arrangement, second.Arrangements);
        foreach (var arrangement in first.Arrangements)
            Assert.Null(registry.Get(arrangement));
    }

    /// <summary>
    ///     Ten switches in a row must not grow the registry — the shape a leak takes when
    ///     nothing prunes. Count, not bytes: a count is deterministic.
    /// </summary>
    [Fact]
    public void RepeatedSwitches_DoNotGrowTheRegistry()
    {
        var state = new ProjectState();
        var selection = new SelectionService();
        using var registry = new TrackContextRegistry(state, selection);

        for (var i = 0; i < 10; i++)
        {
            OpenProject(state, registry, BuildProject($"P{i}"));
            Assert.Equal(ArrangementsPerProject, registry.Tracks.Count);
        }
    }

    /// <summary>
    ///     The point of the prune: a closed project must become collectable. This is the
    ///     assertion that would have caught the leak — the count assertions above can be
    ///     satisfied by a registry that merely stops adding, while a stale KEY still pins
    ///     the graph.
    /// </summary>
    [Fact]
    public void AClosedProjectIsCollectableOnceItsSuccessorIsOpen()
    {
        var state = new ProjectState();
        var selection = new SelectionService();
        using var registry = new TrackContextRegistry(state, selection);

        var (weakProject, weakArrangement, bytesHeld) = OpenThenAbandon(state, registry);

        GC.Collect();
        GC.WaitForPendingFinalizers();
        GC.Collect();

        Assert.False(weakProject.IsAlive,
            "the closed project is still reachable — TrackContextRegistry is pinning it through a context key");
        Assert.False(weakArrangement.IsAlive,
            "a closed project's arrangement is still reachable from the registry");

        // Logged, never asserted: this is the managed cost the registry used to pin PER
        // PROJECT SWITCH, for a deliberately small synthetic project. A real chart's
        // arrangements carry chords, hand shapes, anchors, chord templates and event
        // points on top of the notes counted here.
        TestContext.Current.TestOutputHelper?.WriteLine(
            $"one {ArrangementsPerProject}-arrangement synthetic project graph = {bytesHeld / 1024.0:F1} KiB; "
            + "before 0.48 the registry pinned one of these for every project the session opened");
    }

    // Kept in its own frame so the locals holding the first project are dead by the
    // time the caller collects; a project still on THIS method's stack would survive
    // the collect for reasons that have nothing to do with the registry.
    private static (WeakReference Project, WeakReference Arrangement, long BytesHeld) OpenThenAbandon(
        ProjectState state, TrackContextRegistry registry)
    {
        var empty = GC.GetTotalMemory(forceFullCollection: true);
        var first = BuildProject("closed");
        var graphBytes = Math.Max(0, GC.GetTotalMemory(forceFullCollection: true) - empty);

        OpenProject(state, registry, first);
        var weakProject = new WeakReference(first);
        var weakArrangement = new WeakReference(first.Arrangements[0]);

        OpenProject(state, registry, BuildProject("open"));
        return (weakProject, weakArrangement, graphBytes);
    }

    /// <summary>
    ///     What <c>ChartEditorViewModel.ApplyProject</c> plus
    ///     <c>MultiTrackOverlayViewModel.RebuildEntries</c> do between them: publish the
    ///     project, publish its first arrangement, and realize a context for every
    ///     arrangement so the multi-track overlay can draw them.
    /// </summary>
    private static void OpenProject(ProjectState state, TrackContextRegistry registry, ChartProject project)
    {
        state.ActiveProject = project;
        state.ActiveArrangement = project.Arrangements.FirstOrDefault();
        foreach (var arrangement in project.Arrangements)
            registry.GetOrCreate(arrangement);
    }

    private static ChartProject BuildProject(string tag)
    {
        var project = new ChartProject();
        for (var a = 0; a < ArrangementsPerProject; a++)
        {
            var arrangement = new Arrangement { Name = $"{tag}-{a}" };
            var level = new ChartLevel();
            for (var n = 0; n < 400; n++)
                level.Notes.Add(new GuitarNote { StringIndex = n % 6, Fret = n % 22 });
            arrangement.Levels.Add(level);
            project.Arrangements.Add(arrangement);
        }
        return project;
    }
}
