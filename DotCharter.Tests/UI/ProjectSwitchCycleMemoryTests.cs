using System.Diagnostics;
using DotCharter.Services.Editing.MultiTrack;
using DotCharter.Services.Editing.Selection;
using DotCharter.Services.Parsing.Rscp;
using DotCharter.UI.ViewModels.Editor;
using DotCharter.Services.Editing.Tracks;
using DotCharter.Services.Audio.Coordination;

namespace DotCharter.Tests.UI;

/// <summary>
///     <para>
///         The whole open ▸ play ▸ return ▸ switch loop, five times, over the
///         app-wide SINGLETONS a project graph flows through. Every other
///         retention guard in this suite pins ONE type; this one is the
///         integration: it wires the same set the composition root does and asks
///         the only question that matters across a session — after five songs,
///         is anything of the first four still reachable?
///     </para>
///     <para>
///         Structural assertions only (root <c>AGENTS.md</c>): a
///         <see cref="WeakReference" /> per closed project, plus flat counts on
///         the two registries that key off the chart graph. The BYTES are
///         logged, never asserted — a heap figure under a parallel gate is not a
///         deterministic quantity, and the numbers that matter to a human live
///         in <c>docs/agents/ui/project-lifecycle.md</c>.
///     </para>
///     <para>
///         The real-corpus case is the one that produced the table in that
///         guide. A worktree DOES see <c>TestData/</c> (the walk from
///         <c>AppContext.BaseDirectory</c> reaches the main checkout), but a
///         clone without it must skip rather than silently pass.
///     </para>
/// </summary>
public sealed class ProjectSwitchCycleMemoryTests
{
    private const int Cycles = 5;

    /// <summary>
    ///     Five synthetic cycles. Runs everywhere, so this is the case that
    ///     actually gates: a singleton that starts pinning the closed project
    ///     fails here on the very next run.
    /// </summary>
    [Fact]
    public void FiveOpenPlayReturnSwitchCycles_LeaveEveryClosedProjectCollectable()
    {
        var rig = new SwitchRig();
        var rows = new List<string>();
        var probes = new List<(int Cycle, WeakReference Project, WeakReference Arrangement)>();

        for (var i = 0; i < Cycles; i++)
            probes.Add(RunOneSyntheticCycle(rig, i, rows));

        // The sixth open is the one that closes cycle 5 — without it the last
        // project is legitimately still the open one and proves nothing.
        rig.Open(BuildProject("done", arrangements: 1, notesPerLevel: 1));

        Collect();
        Report(rows, probes, rig);

        foreach (var (cycle, project, arrangement) in probes)
        {
            Assert.False(project.IsAlive,
                $"cycle {cycle}'s project is still reachable after {Cycles} switches — a singleton in the rig "
                + "is pinning the closed project's graph.");
            Assert.False(arrangement.IsAlive,
                $"cycle {cycle}'s arrangement is still reachable after {Cycles} switches.");
        }

        // Non-vacuity's other half: the registries must not merely have stopped
        // adding. After the final open they hold exactly the open project.
        Assert.Single(rig.Registry.Tracks);
        Assert.Empty(rig.Ghosts.VisibleGhosts);
        Assert.Empty(rig.Stacked.StackedArrangements);
    }

    /// <summary>
    ///     The same five cycles over REAL charts from <c>TestData/RsCharter</c> —
    ///     600 KB+ <c>.rscp</c> files, which is what the owner's session actually
    ///     opens. This is the case whose table is quoted in the guide.
    /// </summary>
    [Fact]
    public void FiveCyclesOverTheRealCorpus_LeaveEveryClosedProjectCollectable()
    {
        var charts = FindCorpusCharts();
        if (charts.Count < 2)
            Assert.Skip("No .rscp corpus: TestData/RsCharter is absent (TestData/ is git-ignored).");

        var rig = new SwitchRig();
        var rows = new List<string>();
        var probes = new List<(int Cycle, WeakReference Project, WeakReference Arrangement)>();

        for (var i = 0; i < Cycles; i++)
            probes.Add(RunOneCorpusCycle(rig, i, charts[i % charts.Count], rows));

        rig.Open(BuildProject("done", arrangements: 1, notesPerLevel: 1));

        Collect();
        Report(rows, probes, rig);

        foreach (var (cycle, project, arrangement) in probes)
        {
            Assert.False(project.IsAlive,
                $"cycle {cycle}'s project is still reachable after {Cycles} switches.");
            Assert.False(arrangement.IsAlive,
                $"cycle {cycle}'s arrangement is still reachable after {Cycles} switches.");
        }
    }

    // ── the cycle ────────────────────────────────────────────────────────────

    /// <summary>
    ///     Kept out of the caller's frame — the house rule from
    ///     <see cref="ProjectSwitchForgetsTheClosedProjectTests" />: a project
    ///     still live in a stack slot the JIT has not reused survives the collect
    ///     for reasons that have nothing to do with the rig.
    /// </summary>
    private static (int, WeakReference, WeakReference) RunOneSyntheticCycle(
        SwitchRig rig, int cycle, List<string> rows)
    {
        var project = BuildProject($"song{cycle}", arrangements: 3, notesPerLevel: 2_000);
        return RunCycleCore(rig, cycle, project, $"synthetic-{cycle}", rows);
    }

    private static (int, WeakReference, WeakReference) RunOneCorpusCycle(
        SwitchRig rig, int cycle, string chartPath, List<string> rows)
    {
        var project = new RscpChartSerializer().Load(chartPath);
        return RunCycleCore(rig, cycle, project, Path.GetFileName(Path.GetDirectoryName(chartPath)) ?? "?", rows);
    }

    private static (int, WeakReference, WeakReference) RunCycleCore(
        SwitchRig rig, int cycle, ChartProject project, string label, List<string> rows)
    {
        var probeProject = new WeakReference(project);
        var probeArrangement = new WeakReference(project.Arrangements.FirstOrDefault());

        // open
        rig.Open(project);
        // …the multi-track overlay realizes a context per arrangement, and the
        // user ghosts/stacks a couple of them
        rig.RealizeAllTracks(project);
        if (project.Arrangements.Count > 1) rig.Ghosts.SetVisible(project.Arrangements[1], true);
        if (project.Arrangements.Count > 2) rig.Stacked.SetStacked(project.Arrangements[2], true);

        // "play": drive the playhead across the song the way the transport does,
        // so anything that latches per-frame chart state has been touched.
        rig.Play(project);

        // return to the editor — nothing to do; the editor never left.
        Collect();
        rows.Add(Row(cycle, label, project));
        return (cycle, probeProject, probeArrangement);
    }

    // ── reporting ────────────────────────────────────────────────────────────

    private static string Row(int cycle, string label, ChartProject project)
    {
        var notes = project.Arrangements
            .SelectMany(a => a.Levels)
            .Sum(l => (long)l.Notes.Count + l.Chords.Count);
        using var self = Process.GetCurrentProcess();
        return $"  {cycle,3}  {Trim(label, 26),-26}  {notes,8}  "
               + $"{GC.GetTotalMemory(forceFullCollection: true) / 1048576.0,9:F1}  "
               + $"{self.PrivateMemorySize64 / 1048576.0,9:F1}  "
               + $"{GC.CollectionCount(2),5}";
    }

    private static void Report(
        List<string> rows,
        List<(int Cycle, WeakReference Project, WeakReference Arrangement)> probes,
        SwitchRig rig)
    {
        var log = TestContext.Current.TestOutputHelper;
        if (log is null) return;

        log.WriteLine("  cyc  song                          sounds  heap MiB   priv MiB  gen2");
        foreach (var row in rows) log.WriteLine(row);
        using var self = Process.GetCurrentProcess();
        log.WriteLine(
            $"  after the closing switch + collect: heap {GC.GetTotalMemory(true) / 1048576.0:F1} MiB, "
            + $"private {self.PrivateMemorySize64 / 1048576.0:F1} MiB, "
            + $"gen2 {GC.CollectionCount(2)}, "
            + $"alive probes {probes.Count(p => p.Project.IsAlive)}/{probes.Count}, "
            + $"contexts {rig.Registry.Tracks.Count}");
        log.WriteLine(
            "  the heap column is the MANAGED half only — decoded audio lives in the native engine "
            + "and costs its whole length (docs/agents/services/audio-engine.md).");
    }

    private static string Trim(string s, int max) => s.Length <= max ? s : s[..(max - 1)] + "…";

    private static void Collect()
    {
        GC.Collect();
        GC.WaitForPendingFinalizers();
        GC.Collect();
    }

    // ── corpus ───────────────────────────────────────────────────────────────

    /// <summary>The biggest real charts first — a dense chart is the one whose
    /// retention is visible above the noise.</summary>
    private static IReadOnlyList<string> FindCorpusCharts()
    {
        var root = FindTestDataRoot();
        if (root is null) return [];
        var rsCharter = Path.Combine(root, "RsCharter");
        if (!Directory.Exists(rsCharter)) return [];

        return Directory.EnumerateFiles(rsCharter, "project.rscp", SearchOption.AllDirectories)
            .Where(p => !p.Contains($"{Path.DirectorySeparatorChar}backups{Path.DirectorySeparatorChar}",
                StringComparison.OrdinalIgnoreCase))
            .Select(p => (Path: p, Size: new FileInfo(p).Length))
            .OrderByDescending(x => x.Size)
            .Take(Cycles)
            .Select(x => x.Path)
            .ToList();
    }

    private static string? FindTestDataRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null)
        {
            var candidate = Path.Combine(dir.FullName, "TestData");
            if (Directory.Exists(candidate)) return candidate;
            dir = dir.Parent;
        }

        return null;
    }

    // ── the rig ──────────────────────────────────────────────────────────────

    /// <summary>
    ///     The app-wide singletons a project switch flows through, wired the way
    ///     the composition root wires them. Adding a singleton that keys off the
    ///     chart graph here is what makes this test cover it.
    /// </summary>
    private sealed class SwitchRig
    {
        public SwitchRig()
        {
            var logger = new FakeAppLogger();
            var config = new FakeConfigurationService();
            var selection = new SelectionService();
            State = new ProjectState();
            Registry = new TrackContextRegistry(State, selection);
            Ghosts = new GhostArrangementsService();
            Stacked = new StackedArrangementsService();
            Waveforms = new StemWaveformCoordinator(new FakeAudioPlaybackService(), State, logger);
            Editor = new ChartEditorViewModel(
                State,
                new TransportViewModel(new FakeAudioPlaybackService(), State, config, FakeLocalization.KeysOnly()),
                new HistoryService(logger), selection, new TempoService(logger), Registry, FakeLocalization.KeysOnly());
            Overlay = new MultiTrackOverlayViewModel(
                Ghosts, Stacked,
                // Stored into a MultiTrackEntry only; no path under test invokes
                // it, and stubbing its members would be noise.
                editingCoordinator: DotCharter.Tests.Presentation.Dialogs.RecordingProxy.Create<DotCharter.Services.Editing.IGlobalEditingCoordinator>().Proxy,
                State, Registry, config, logger);
        }

        public ProjectState State { get; }
        public TrackContextRegistry Registry { get; }
        public GhostArrangementsService Ghosts { get; }
        public StackedArrangementsService Stacked { get; }
        public StemWaveformCoordinator Waveforms { get; }
        public ChartEditorViewModel Editor { get; }
        public MultiTrackOverlayViewModel Overlay { get; }

        public void Open(ChartProject project)
        {
            Editor.ApplyProject(project, resetHistory: true);
            Waveforms.Reset();
            // Keep the overlay reachable for the life of the rig: it is a
            // subscriber under test and nothing else references it.
            Assert.NotNull(Overlay);
        }

        public void RealizeAllTracks(ChartProject project)
        {
            foreach (var arrangement in project.Arrangements)
                Registry.GetOrCreate(arrangement);
        }

        /// <summary>What a run does to shared state: walk the playhead across the
        /// song. Anything that latches the chart per frame has now latched it.</summary>
        public void Play(ChartProject project)
        {
            var end = project.Arrangements
                .SelectMany(a => a.Levels)
                .SelectMany(l => l.Notes)
                .Select(n => n.Position.DoubleValue)
                .DefaultIfEmpty(0)
                .Max();
            for (var i = 0; i <= 16; i++)
                State.CurrentTimeMs = end * (i / 16.0);
            State.CurrentTimeMs = 0;
        }
    }

    private static ChartProject BuildProject(string tag, int arrangements, int notesPerLevel)
    {
        var project = new ChartProject();
        for (var a = 0; a < arrangements; a++)
        {
            var arrangement = new Arrangement { Name = $"{tag}-{a}", ArrangementType = ArrangementType.Lead };
            var level = new ChartLevel();
            for (var n = 0; n < notesPerLevel; n++)
                level.Notes.Add(new GuitarNote { StringIndex = n % 6, Fret = n % 22 });
            arrangement.Levels.Add(level);
            arrangement.EventPoints.Add(new EventPoint { Section = $"{tag}-section-{a}" });
            project.Arrangements.Add(arrangement);
        }

        return project;
    }
}
