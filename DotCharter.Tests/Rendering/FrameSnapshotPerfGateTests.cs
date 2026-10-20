using System.Collections.ObjectModel;
using DotCharter.Rendering.Data;

namespace DotCharter.Tests.Rendering;

/// <summary>
///     CI-runnable perf gates for the 0.14 targets. FrameSnapshot.BuildSnapshot
///     is a pure static over Core collections — no GL/Vulkan, no display, no
///     headless app session — so unlike *UiPerfSmokeTests these run inside the
///     default gate. Hosted CI runners have jittery shared CPUs, so the gates
///     assert deterministic channels only: allocation bytes and the number of
///     chart items a rebuild READS (never wall-clock; timings are logged).
/// </summary>
[Collection("FrameSnapshotPerfGate")]
public sealed class FrameSnapshotPerfGateTests(ITestOutputHelper output)
{
    private sealed record Harness(
        SnapshotUpdateContext Ctx,
        FrameSnapshot Snapshot,
        HashSet<(long timeMs, int stringIdx)> LinkedTargets,
        double LengthMs);

    private static Harness CreateHarness(int soundCount, double lengthMinutes = 10)
    {
        var chart = StressChartFactory.Chart(soundCount, lengthMinutes);
        var ctx = new SnapshotUpdateContext
        {
            ActiveBeats = chart.Beats,
            ActiveNotes = chart.Notes,
            ActiveChords = chart.Chords,
            ActiveAnchors = chart.Anchors,
            ActiveHandShapes = chart.HandShapes,
            // Length-scaled decorations so the gates cover the event/tone/
            // lyric/showlight projections too (windowed since 0.14 P5).
            EventPoints = chart.EventPoints,
            Tones = chart.Tones,
            LyricPhrases = chart.LyricPhrases,
            Showlights = chart.Showlights
        };
        return new Harness(ctx, new FrameSnapshot(), [], chart.LengthMs);
    }

    /// <summary>4 s viewport shaped like the 3D window (-1 s / +3 s).</summary>
    private static void Rebuild(Harness h, double timeMs)
        => FrameSnapshot.BuildSnapshot(timeMs - 1000, timeMs + 3000, timeMs, h.Snapshot, h.Ctx, h.LinkedTargets);

    private static double MeasureMsPerRebuild(Harness h, int iterations)
    {
        var mid = h.LengthMs / 2;
        var best = double.MaxValue;
        for (var run = 0; run < 5; run++)
        {
            var sw = System.Diagnostics.Stopwatch.StartNew();
            for (var i = 0; i < iterations; i++) Rebuild(h, mid + i); // moving playhead
            sw.Stop();
            best = Math.Min(best, sw.Elapsed.TotalMilliseconds / iterations);
        }

        return best;
    }

    [Fact]
    public void WarmRebuild_StaysWithinAllocationBudget()
    {
        var h = CreateHarness(20_000);
        var mid = h.LengthMs / 2;

        // Cold builds grow the pooled Render*[] arrays and warm the caches.
        Rebuild(h, mid);
        Rebuild(h, mid + 1);

        const int iterations = 200;
        // THREAD-LOCAL allocation meter (v0.31 root-cause fix): the old
        // GC.GetTotalAllocatedBytes(precise) is PROCESS-WIDE, so background
        // threads from earlier suite tests — timers, socket receive loops,
        // finalizers, the test host's own diagnostics — leaked into the
        // 200 × 128 B budget and flaked the gate under full-suite load even
        // though this collection is serialized. The rebuild loop runs
        // synchronously on THIS thread, so the thread-local counter measures
        // exactly the claim ("the rebuild allocates nothing") and is immune
        // to everything else in the process.
        var before = GC.GetAllocatedBytesForCurrentThread();
        for (var i = 0; i < iterations; i++) Rebuild(h, mid + i);
        var perRebuild = (GC.GetAllocatedBytesForCurrentThread() - before) / (double)iterations;

        // The warm rebuild is allocation-free since the 0.14 zero-GC pass
        // (boxed selection/bend enumerators removed — measured 0 bytes/frame).
        // 128 still fails on a single reintroduced boxed enumerator (~40+
        // bytes/frame).
        Assert.True(perRebuild <= 128,
            $"FrameSnapshot warm rebuild allocates {perRebuild:F0} bytes/frame (budget 128). " +
            "A regression here becomes steady-state GC during playback.");
    }

    /// <summary>
    ///     The same budget with LIVE PREVIEW collections — the mid-drag state, and
    ///     the one the plain gate never covered.
    ///     <para>
    ///         The preview/anchor fields on <see cref="SnapshotUpdateContext" /> are
    ///         typed <c>IList&lt;T&gt;</c> and both production call sites
    ///         (<c>ChartRenderer</c>, <c>Preview3DRenderDriver</c>) assign
    ///         <c>ObservableCollection</c>s, whose enumerator is only reachable
    ///         through <c>IEnumerable&lt;T&gt;</c> and is therefore BOXED. The
    ///         existing gate leaves these at their <c>Array.Empty</c> default, so
    ///         five `foreach`es over them went unmetered — while the file's own
    ///         comments (`FrameSnapshot.cs`, `SnapshotUpdateContext.cs`) required
    ///         indexed walks precisely to avoid this.
    ///     </para>
    ///     <para>
    ///         A drag is exactly when the frame budget matters most, so this is not
    ///         a hypothetical path.
    ///     </para>
    /// </summary>
    [Fact]
    public void WarmRebuild_WithLivePreviewCollections_StaysWithinAllocationBudget()
    {
        var h = CreateHarness(20_000);
        var mid = h.LengthMs / 2;
        SetLivePreviews(h, mid);

        Rebuild(h, mid);
        Rebuild(h, mid + 1);

        const int iterations = 200;
        var before = GC.GetAllocatedBytesForCurrentThread();
        for (var i = 0; i < iterations; i++) Rebuild(h, mid + i);
        var perRebuild = (GC.GetAllocatedBytesForCurrentThread() - before) / (double)iterations;

        // Same 128 B/frame budget as the no-preview gate: with every preview walk
        // indexed the rebuild is allocation-free here too. Each reintroduced
        // interface `foreach` costs ~32-40 B/frame, and there were five.
        Assert.True(perRebuild <= 128,
            $"FrameSnapshot warm rebuild with live previews allocates {perRebuild:F0} bytes/frame " +
            "(budget 128). A boxed enumerator here becomes steady-state GC during a drag.");
    }

    /// <summary>
    ///     Populates the preview fields the way production does: real
    ///     <c>ObservableCollection</c>s (NOT <c>Array.Empty</c>, whose enumerator
    ///     the runtime caches), with content, so the boxing is actually exercised.
    ///     A preview anchor is required to reach the anchor-tail scan.
    /// </summary>
    private static void SetLivePreviews(Harness h, double atMs)
    {
        var beats = h.Ctx.ActiveBeats;
        var at = TimelineMath.PositionFromMs(atMs, beats);

        var notes = new ObservableCollection<GuitarNote>();
        for (var i = 0; i < 4; i++)
            notes.Add(new GuitarNote { Position = at, StringIndex = i, Fret = 5, LinkNext = true, Vibrato = true });

        var chords = new ObservableCollection<Chord>();
        var chord = new Chord { Position = at };
        for (var i = 0; i < 3; i++)
            chord.ChordNotes.Add(new ChordNote { StringIndex = i, Fret = 7, LinkNext = true, Tremolo = true });
        chords.Add(chord);

        h.Ctx.PreviewNotes = notes;
        h.Ctx.PreviewChords = chords;
        h.Ctx.PreviewAnchors = new ObservableCollection<FretAnchor> { new() { Position = at, Fret = 5, Width = 4 } };
        // The anchor-tail scan walks ActiveAnchors once per preview anchor.
        h.Ctx.ActiveAnchors = new ObservableCollection<FretAnchor>(h.Ctx.ActiveAnchors);
    }

    /// <summary>
    ///     Same note DENSITY (identical visible-window content), 10x the chart
    ///     length: a viewport-windowed rebuild VISITS the same chart items on
    ///     both; an O(total chart) walk visits ~10x as many on the long one.
    ///     Every chart collection on the context is wrapped in a
    ///     <see cref="ReadCountingList{T}" /> that records which items were read
    ///     (indexer, enumeration, copy, search), so the assertion is a
    ///     deterministic COUNT of distinct items visited, not a wall-clock ratio
    ///     (#788: the old 2.0x timing ratio flaked at 3.7x under parallel load).
    ///     Distinct, not total: per-item time conversions binary-search the beat
    ///     grid hundreds of times a frame, and every search legitimately grows by
    ///     log2(10) probes on the 10x grid; those probes land on a few path items,
    ///     while a walk lands on every one. Timings are still logged.
    ///     <para>
    ///         The short chart is 5 min, not 1: on a 1 min grid the search paths
    ///         already cover most of its 121 beats, so the baseline saturates and
    ///         the comparison measures the grid size, not the rebuild.
    ///     </para>
    /// </summary>
    [Fact]
    public void RebuildCost_IsViewportBound_NotChartBound()
    {
        var small = CreateHarness(10_000, lengthMinutes: 5);
        var large = CreateHarness(100_000, lengthMinutes: 50);

        // Timed on the bare lists, before the counters wrap them.
        var smallMs = MeasureMsPerRebuild(small, 200);
        var largeMs = MeasureMsPerRebuild(large, 200);
        output.WriteLine($"Rebuild timing (logged, not asserted): {smallMs:F3} ms (5 min chart) vs " +
                         $"{largeMs:F3} ms (50 min chart), {largeMs / Math.Max(smallMs, 1e-6):F2}x.");

        var smallCounters = WrapWithReadCounters(small);
        var largeCounters = WrapWithReadCounters(large);
        var smallVisits = CountSteadyStateVisits(small, smallCounters);
        var largeVisits = CountSteadyStateVisits(large, largeCounters);

        var failures = new List<string>();
        foreach (var (name, _) in smallCounters)
        {
            var (smallDistinct, smallReads) = smallVisits[name];
            var (largeDistinct, largeReads) = largeVisits[name];
            output.WriteLine($"{name}: {smallDistinct} -> {largeDistinct} distinct items visited, " +
                             $"{smallReads} -> {largeReads} reads, over {SteadyStateFrames} frames.");
            var allowed = smallDistinct * SearchPathGrowth + SearchPathSlack;
            if (largeDistinct > allowed)
                failures.Add($"{name}: {largeDistinct} distinct items vs {smallDistinct} (allowed {allowed})");
        }

        Assert.True(failures.Count == 0,
            "A 10x-longer chart makes the rebuild visit more chart items for the same viewport — the " +
            "snapshot is no longer viewport-bound (0.14 target: 20k notes at 144 fps): " +
            string.Join("; ", failures));

        // And it emits the same window on both charts (the snapshot holds the
        // last counted frame, at the same beat offset on both).
        Assert.Equal(
            (small.Snapshot.NoteCount, small.Snapshot.ChordCount, small.Snapshot.AnchorCount),
            (large.Snapshot.NoteCount, large.Snapshot.ChordCount, large.Snapshot.AnchorCount));
    }

    private const int SteadyStateFrames = 50;

    // The windowed items are the same on both charts; only search paths grow.
    // Measured 2026-10-13 (5 min -> 50 min): notes 1,367 -> 1,369, chords and
    // hand shapes +1, the sparse lanes +7 each, and beats 156 -> 523 — every
    // probe of every other collection's binary search converts its position
    // through a search of the beat grid, so beat paths grow polylog (3.4x).
    // An O(total) walk grows with the chart, 10x (it visits all 90k notes, 6k
    // beats, 750 anchors / event points / lyric phrases / showlights). 5x sits
    // between the two; the +16 covers the sparse lanes' handful of items.
    private const int SearchPathGrowth = 5;
    private const int SearchPathSlack = 16;

    /// <summary>
    ///     Steady-state frames, the way playback drives them: one warm-up build,
    ///     then <see cref="SteadyStateFrames" /> rebuilds with the playhead moving
    ///     1 ms each, all inside one beat (beats are 500 ms apart and the walk
    ///     starts 100 ms past one), so the cached measure prefix legitimately
    ///     stays warm. The snapshot is left at the last frame.
    /// </summary>
    private static Dictionary<string, (int Distinct, long Reads)> CountSteadyStateVisits(Harness h,
        List<(string Name, IReadCounter Counter)> counters)
    {
        var start = h.LengthMs / 2 + 100;
        Rebuild(h, start);
        foreach (var (_, counter) in counters) counter.Reset();
        for (var i = 1; i <= SteadyStateFrames; i++) Rebuild(h, start + i);
        return counters.ToDictionary(c => c.Name, c => (c.Counter.Distinct, c.Counter.Reads));
    }

    private static List<(string Name, IReadCounter Counter)> WrapWithReadCounters(Harness h)
    {
        var ctx = h.Ctx;
        var counters = new List<(string, IReadCounter)>();
        ctx.ActiveBeats = Wrap(ctx.ActiveBeats, "beats", counters);
        ctx.ActiveNotes = Wrap(ctx.ActiveNotes, "notes", counters);
        ctx.ActiveChords = Wrap(ctx.ActiveChords, "chords", counters);
        ctx.ActiveAnchors = Wrap(ctx.ActiveAnchors, "anchors", counters);
        ctx.ActiveHandShapes = Wrap(ctx.ActiveHandShapes, "hand shapes", counters);
        ctx.EventPoints = Wrap(ctx.EventPoints, "event points", counters);
        ctx.LyricPhrases = Wrap(ctx.LyricPhrases, "lyric phrases", counters);
        ctx.Showlights = Wrap(ctx.Showlights, "showlights", counters);
        // ctx.Tones is NOT wrapped: FrameSnapshot.ResolveArrangementTone walks
        // the whole (possibly unsorted) tone-change list on purpose; a chart
        // carries a handful of tone changes, not thousands.
        return counters;
    }

    private static IList<T> Wrap<T>(IList<T> inner, string name, List<(string, IReadCounter)> counters)
    {
        var wrapped = new ReadCountingList<T>(inner);
        counters.Add((name, wrapped));
        return wrapped;
    }

    private interface IReadCounter
    {
        long Reads { get; }
        int Distinct { get; }
        void Reset();
    }

    /// <summary>
    ///     Read-only <see cref="IList{T}" /> decorator that counts every element
    ///     it hands out. Reading <c>Count</c> is free; the indexer, enumeration,
    ///     <c>CopyTo</c> and the linear searches are charged per element touched.
    /// </summary>
    private sealed class ReadCountingList<T>(IList<T> inner) : IList<T>, IReadCounter
    {
        private readonly HashSet<int> _touched = [];

        public long Reads { get; private set; }

        public int Distinct => _touched.Count;

        public void Reset()
        {
            Reads = 0;
            _touched.Clear();
        }

        private void Touch(int index)
        {
            Reads++;
            _touched.Add(index);
        }

        public T this[int index]
        {
            get
            {
                Touch(index);
                return inner[index];
            }
            set => throw new NotSupportedException();
        }

        public int Count => inner.Count;

        public bool IsReadOnly => true;

        public IEnumerator<T> GetEnumerator()
        {
            for (var i = 0; i < inner.Count; i++)
            {
                Touch(i);
                yield return inner[i];
            }
        }

        System.Collections.IEnumerator System.Collections.IEnumerable.GetEnumerator() => GetEnumerator();

        public int IndexOf(T item)
        {
            var comparer = EqualityComparer<T>.Default;
            for (var i = 0; i < inner.Count; i++)
            {
                Touch(i);
                if (comparer.Equals(inner[i], item)) return i;
            }

            return -1;
        }

        public bool Contains(T item) => IndexOf(item) >= 0;

        public void CopyTo(T[] array, int arrayIndex)
        {
            for (var i = 0; i < inner.Count; i++) Touch(i);
            inner.CopyTo(array, arrayIndex);
        }

        public void Add(T item) => throw new NotSupportedException();

        public void Clear() => throw new NotSupportedException();

        public void Insert(int index, T item) => throw new NotSupportedException();

        public bool Remove(T item) => throw new NotSupportedException();

        public void RemoveAt(int index) => throw new NotSupportedException();
    }
}

/// <summary>
///     Runs exclusively (no parallel collections) so allocation and timing
///     measurements aren't polluted by concurrent tests — same pattern as the
///     AlphaTab sheet collection.
/// </summary>
[CollectionDefinition("FrameSnapshotPerfGate", DisableParallelization = true)]
public sealed class FrameSnapshotPerfGateCollection;
