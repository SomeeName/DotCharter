using System.Collections.ObjectModel;
using DotCharter.Services.Editing;
using DotCharter.Services.Editing.Collisions;
using DotCharter.Services.Editing.Highway;
using DotCharter.Services.Editing.Selection;
using DotCharter.Services.Editing.Targets;
using DotCharter.UI.ViewModels.Editor;

namespace DotCharter.Tests.Perf;

/// <summary>
///     Dev-machine wall-clock PROFILE of the chart editor's per-gesture cost on a
///     big chart — the numbers behind
///     <c>docs/agents/ui/rendering-perf/editor-latency.md</c>. Env-gated like
///     <see cref="RefretPerfGateTests" /> (absolute timings are meaningless on a
///     hosted runner) and asserting nothing but sanity: the printed
///     <c>[PerfHarness]</c> lines are the deliverable.
///     <para>
///         The structural guards that keep these numbers from regressing live
///         beside the code they cover — <c>BulkCollectionEditEquivalenceTests</c>,
///         <c>LargeSelectionEditCostTests</c>, <c>TimelineScanEquivalenceTests</c>,
///         <c>InspectorFlagAgreementTests</c> and
///         <c>ChartSpacingEnforcerTests</c>. This class exists only to MEASURE.
///     </para>
///     Run:
///     <code>
///     $env:DOTCHARTER_PERF_GATE="1"
///     dotnet test DotCharter.Tests\DotCharter.Tests.csproj --no-build --filter EditorLatencyProfile
///     </code>
/// </summary>
[Collection("FrameSnapshotPerfGate")]
public sealed class EditorLatencyProfileTests
{
    private readonly ITestOutputHelper _out;

    public EditorLatencyProfileTests(ITestOutputHelper output) => _out = output;

    private static void RequirePerfGate()
        => Assert.SkipWhen(Environment.GetEnvironmentVariable("DOTCHARTER_PERF_GATE") != "1",
            "Dev-machine wall-clock profile — set DOTCHARTER_PERF_GATE=1 to run.");

    /// <summary>
    ///     A live editing stack over a <see cref="StressChartFactory" /> chart:
    ///     real <see cref="ProjectState" />, real history, real selection service,
    ///     real spacing enforcer behind the highway service's post-pass.
    /// </summary>
    private sealed class Harness
    {
        public readonly List<BeatMarker> Beats;
        public readonly HistoryService History;
        public readonly ChartLevel Level;
        public readonly SelectionService Selection = new();
        public readonly HighwayEditingService Service;

        public Harness(int soundCount, double lengthMinutes = 10)
        {
            var chart = StressChartFactory.Chart(soundCount, lengthMinutes);
            Beats = chart.Beats;
            // A real chart flags its downbeats, and the spacing enforcer's
            // measure lookup walks BACKWARDS to the nearest one — so a grid
            // without them makes that lookup O(beats) per sound and the number
            // stops describing any chart a charter has. 4/4 downbeats.
            for (var i = 0; i < Beats.Count; i++)
            {
                Beats[i].IsMeasure = i % 4 == 0;
                Beats[i].BeatsInMeasure = 4;
            }

            var logger = new FakeAppLogger();
            History = new HistoryService(logger);
            var state = new ProjectState();
            var project = new ChartProject();
            foreach (var b in chart.Beats) project.Beats.Add(b);

            var arr = new Arrangement { Name = "Lead", StringCount = 6 };
            Level = new ChartLevel();
            foreach (var n in chart.Notes) Level.Notes.Add(n);
            foreach (var c in chart.Chords) Level.Chords.Add(c);
            foreach (var a in chart.Anchors) Level.Anchors.Add(a);
            foreach (var hs in chart.HandShapes) Level.HandShapes.Add(hs);
            arr.Levels.Add(Level);
            project.Arrangements.Add(arr);

            state.ActiveProject = project;
            state.ActiveArrangement = arr;
            state.ActiveLevel = Level;

            var config = new FakeConfigurationService();
            var scope = new EditingScope(state);
            Service = new HighwayEditingService(state, Selection, History, new StrictCollisionService(),
                config, scope, new EditTargetService(Selection, scope, config), logger);
        }
    }

    /// <summary>
    ///     Bulk delete and its undo — the "delete 5,000 of 20,000 notes, then
    ///     Ctrl+Z" gesture. Both halves used to be O(k·n): <c>Remove</c> is an
    ///     <c>IndexOf</c> scan per item and <c>InsertSorted</c> walked from index 0
    ///     per item.
    /// </summary>
    [Theory]
    [InlineData(5_000, 1_000)]
    [InlineData(20_000, 5_000)]
    public void Profile_BulkDeleteAndUndo(int soundCount, int deleteCount)
    {
        RequirePerfGate();

        var h = new Harness(soundCount);
        var victims = new List<GuitarNote>();
        var stride = Math.Max(1, h.Level.Notes.Count / deleteCount);
        for (var i = 0; i < h.Level.Notes.Count && victims.Count < deleteCount; i += stride)
            victims.Add(h.Level.Notes[i]);

        var total = h.Level.Notes.Count;

        // No post-pass: the COLLECTION cost on its own, with the spacing
        // enforcer's per-edit cost (profiled separately) out of the picture.
        var bare = new RemoveNotesAction(h.Level.Notes, victims, null);
        var toggle = false;
        RenderPerfHarness.Measure(_out,
            $"bulk delete+undo, {victims.Count} of {total} notes (no post-pass)",
            warmup: 2, iterations: 20, () =>
            {
                if (toggle) bare.Undo();
                else bare.Execute();
                toggle = !toggle;
            });

        var action = new RemoveNotesAction(h.Level.Notes, victims, h.Service.GetLinkNextUpdater());
        toggle = false;
        RenderPerfHarness.Measure(_out,
            $"bulk delete+undo, {victims.Count} of {total} notes (with post-pass)",
            warmup: 2, iterations: 20, () =>
            {
                if (toggle) action.Undo();
                else action.Execute();
                toggle = !toggle;
            });
    }

    /// <summary>
    ///     One keyboard mutation verb (toggle accent) over a large live selection —
    ///     the shape every fret/technique keybind takes.
    /// </summary>
    [Theory]
    [InlineData(20_000, 5_000)]
    public void Profile_SelectedMutationVerb(int soundCount, int selectionCount)
    {
        RequirePerfGate();

        var h = new Harness(soundCount);
        var picked = new List<GuitarNote>();
        for (var i = 0; i < h.Level.Notes.Count && picked.Count < selectionCount; i++)
            picked.Add(h.Level.Notes[i]);
        SelectionBulkOps.ReplaceWith(h.Selection.SelectedNotes, picked);

        RenderPerfHarness.Measure(_out,
            $"ToggleSelectedAccent over {picked.Count} selected notes",
            warmup: 2, iterations: 20, () => h.Service.ToggleSelectedAccent());
    }

    /// <summary>
    ///     Replacing the whole selection — the post-drag reselection and every
    ///     marquee commit.
    /// </summary>
    [Theory]
    [InlineData(20_000, 5_000)]
    public void Profile_SelectionReplace(int soundCount, int selectionCount)
    {
        RequirePerfGate();

        var h = new Harness(soundCount);
        var picked = new List<GuitarNote>();
        for (var i = 0; i < h.Level.Notes.Count && picked.Count < selectionCount; i++)
            picked.Add(h.Level.Notes[i]);

        var toggle = false;
        RenderPerfHarness.Measure(_out,
            $"selection replace, {picked.Count} notes (bulk)",
            warmup: 2, iterations: 20, () =>
            {
                SelectionBulkOps.ReplaceWith(h.Selection.SelectedNotes,
                    toggle ? picked : Array.Empty<GuitarNote>());
                toggle = !toggle;
            });

        // The loop shape the bulk helper replaces, measured on the SAME data so
        // the delta in the doc is reproducible.
        var sink = new ObservableCollection<GuitarNote>();
        var bumps = 0;
        sink.CollectionChanged += (_, _) => bumps++;
        RenderPerfHarness.Measure(_out,
            $"selection replace, {picked.Count} notes (one Add at a time)",
            warmup: 1, iterations: 3, () =>
            {
                sink.Clear();
                foreach (var n in picked) sink.Add(n);
            });
        _out.WriteLine($"[PerfHarness] …that loop raised {bumps} CollectionChanged events.");
    }

    /// <summary>
    ///     Arrow-key seek/select — <c>SelectNextNote</c>'s scan shape, isolated
    ///     from the view model so it needs no Avalonia session.
    /// </summary>
    [Theory]
    [InlineData(20_000)]
    public void Profile_SeekTargetScan(int soundCount)
    {
        RequirePerfGate();

        var h = new Harness(soundCount);
        var notes = h.Level.Notes;
        var beats = h.Beats;
        var midMs = beats[beats.Count / 2].Time;

        RenderPerfHarness.Measure(_out,
            $"next-sound scan over {notes.Count} notes (Where+OrderBy+First)",
            warmup: 2, iterations: 20, () =>
            {
                var _ = notes.Where(n => n.Position.GetPositionMs(beats) > midMs)
                    .OrderBy(n => n.Position).FirstOrDefault();
            });

        RenderPerfHarness.Measure(_out,
            $"next-sound scan over {notes.Count} notes (binary search)",
            warmup: 2, iterations: 20,
            () => { var _ = TimelineMath.FirstAfter(notes, midMs, beats); });
    }

    /// <summary>
    ///     The inspector's tri-state chips: what one selection edit costs to fold.
    ///     Twice this, in practice — the sync runs on both the scalar and the
    ///     collection signal.
    /// </summary>
    [Theory]
    [InlineData(5_000)]
    public void Profile_InspectorFlagFold(int selectionCount)
    {
        RequirePerfGate();

        var h = new Harness(20_000);
        var notes = new List<GuitarNote>();
        for (var i = 0; i < h.Level.Notes.Count && notes.Count < selectionCount; i++)
            notes.Add(h.Level.Notes[i]);

        RenderPerfHarness.Measure(_out,
            $"inspector chip fold over {notes.Count} notes (seven walks + a copy)",
            warmup: 5, iterations: 100, () =>
            {
                var snapshot = notes.ToList();
                _ = InspectorViewModel.AllSameNullable(snapshot, n => n.Accent);
                _ = InspectorViewModel.AllSameNullable(snapshot, n => n.Vibrato);
                _ = InspectorViewModel.AllSameNullable(snapshot, n => n.LinkNext);
                _ = InspectorViewModel.AllSameNullable(snapshot, n => n.PassOtherNotes);
                _ = InspectorViewModel.AllSameNullable(snapshot, n => n.Ignore);
                _ = InspectorViewModel.AllSameNullable(snapshot, n => n.Tremolo);
                _ = InspectorViewModel.AllSameNullable(snapshot, n => n.UnpitchedSlide);
            });

        RenderPerfHarness.Measure(_out,
            $"inspector chip fold over {notes.Count} notes (one pass)",
            warmup: 5, iterations: 100,
            () => { _ = InspectorViewModel.NoteFlagAgreement.Over(notes); });
    }

    /// <summary>
    ///     The spacing enforcer's post-pass — run once per committed edit, on the
    ///     UI thread, over the WHOLE active level.
    /// </summary>
    [Theory]
    [InlineData(20_000)]
    public void Profile_SpacingEnforcerPostPass(int soundCount)
    {
        RequirePerfGate();

        var h = new Harness(soundCount);
        var post = h.Service.GetLinkNextUpdater();
        RenderPerfHarness.Measure(_out,
            $"ChartSpacingEnforcer post-pass over {h.Level.Notes.Count} notes + {h.Level.Chords.Count} chords",
            warmup: 2, iterations: 20, () => post());
    }
}
