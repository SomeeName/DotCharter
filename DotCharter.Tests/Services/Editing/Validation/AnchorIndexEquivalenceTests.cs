using System.Diagnostics;
using System.Text.RegularExpressions;

namespace DotCharter.Tests.Services.Editing.Validation;

/// <summary>
///     Pins the sorted anchor index inside <see cref="ArrangementValidationContext" />
///     against the linear scans it replaced.
/// </summary>
/// <remarks>
///     <para>
///         This one changes a VERDICT path — every "no anchor coverage", "outside
///         the anchor range", "position shift into a pull-off" and "finger changes
///         during a slide" finding is derived from these three lookups — so the
///         evidence is EQUIVALENCE, not a benchmark. The reference implementations
///         are the repo's own `FindActiveAnchor`/`FindPreviousAnchor`/
///         `FindAnchorAt`, kept verbatim in production and used here as the spec.
///     </para>
///     <para>
///         Timings are LOGGED, never asserted (see the root AGENTS.md rule).
///     </para>
/// </remarks>
public sealed class AnchorIndexEquivalenceTests(ITestOutputHelper output)
{
    private static FretAnchor Anchor(int beat, int num, int den, int fret) => new()
    {
        Position = new FractionalPosition(beat, new Fraction(num, den)),
        Fret = fret,
        Width = 4
    };

    private static ArrangementValidationContext ContextOver(ChartLevel level)
    {
        var arrangement = new Arrangement();
        arrangement.Levels.Add(level);
        return ArrangementValidationContext.Build(
            arrangement, [new BeatMarker { Time = 0 }, new BeatMarker { Time = 500 }], null);
    }

    /// <summary>
    ///     The whole claim: over randomly PERMUTED anchor lists carrying duplicate
    ///     positions, queried at every interesting position (before the first
    ///     anchor, exactly on one, between two, past the last), the indexed
    ///     lookups return the SAME INSTANCE the linear scan returns.
    /// </summary>
    [Fact]
    public void TheIndexedLookups_ReturnWhatTheLinearScansReturn()
    {
        var rng = new Random(20260803);
        var comparisons = 0;
        var stopwatch = Stopwatch.StartNew();

        for (var trial = 0; trial < 300; trial++)
        {
            // Deliberately dense in duplicates: two anchors at one position are
            // ordinary chart state, and the tie-break is what a binary search is
            // most likely to get wrong.
            var count = rng.Next(0, 24);
            var anchors = new List<FretAnchor>();
            for (var i = 0; i < count; i++)
                anchors.Add(Anchor(rng.Next(0, 6), rng.Next(0, 4), 4, rng.Next(1, 20)));

            // …and PERMUTED, because HighwayEditingService enforces the level
            // with runCompatibilityCleanup: false, which skips the sort.
            for (var i = anchors.Count - 1; i > 0; i--)
            {
                var j = rng.Next(i + 1);
                (anchors[i], anchors[j]) = (anchors[j], anchors[i]);
            }

            var level = new ChartLevel();
            foreach (var a in anchors) level.Anchors.Add(a);
            var context = ContextOver(level);

            foreach (var time in QueryPositions(anchors))
            {
                Assert.Same(ArrangementValidationContext.FindActiveAnchor(level, time),
                    context.ActiveAnchor(level, time));
                Assert.Same(ArrangementValidationContext.FindPreviousAnchor(level, time),
                    context.PreviousAnchor(level, time));
                Assert.Same(ArrangementValidationContext.FindAnchorAt(level, time),
                    context.AnchorAt(level, time));
                comparisons += 3;
            }
        }

        output.WriteLine($"{comparisons:N0} indexed-vs-linear comparisons in {stopwatch.ElapsedMilliseconds} ms.");
        Assert.True(comparisons > 10_000, $"only {comparisons} comparisons ran — the sweep stopped sweeping.");
    }

    /// <summary>Every position worth asking about for this anchor list: one
    /// before the earliest, each anchor's own position, a point just after it,
    /// and one past the last.</summary>
    private static IEnumerable<FractionalPosition> QueryPositions(IReadOnlyList<FretAnchor> anchors)
    {
        yield return new FractionalPosition(-1);
        yield return FractionalPosition.Zero;
        foreach (var a in anchors)
        {
            yield return a.Position;
            yield return a.Position + new FractionalPosition(0, new Fraction(1, 8));
        }

        yield return new FractionalPosition(99);
    }

    /// <summary>
    ///     The tie-break, called out on its own because it is the one rule a
    ///     naive binary search silently gets wrong: the scan replaces its
    ///     candidate only on a STRICTLY greater position, so among anchors at one
    ///     position the FIRST in the collection's own order wins — and the
    ///     collection is not sorted.
    /// </summary>
    [Fact]
    public void ATieIsResolvedByCollectionOrder_NotByWhicheverTheSearchLandsOn()
    {
        var later = Anchor(4, 0, 1, 12);
        var earlyDuplicate = Anchor(2, 0, 1, 5);
        var lateDuplicate = Anchor(2, 0, 1, 9);

        var level = new ChartLevel();
        // Out of order AND tied, the state a mid-session edit really leaves.
        level.Anchors.Add(later);
        level.Anchors.Add(earlyDuplicate);
        level.Anchors.Add(lateDuplicate);
        var context = ContextOver(level);

        var at3 = new FractionalPosition(3);
        Assert.Same(earlyDuplicate, context.ActiveAnchor(level, at3));
        Assert.Same(earlyDuplicate, ArrangementValidationContext.FindActiveAnchor(level, at3));

        Assert.Same(earlyDuplicate, context.PreviousAnchor(level, new FractionalPosition(4)));
        Assert.Same(earlyDuplicate, context.AnchorAt(level, new FractionalPosition(2)));
    }

    /// <summary>
    ///     The per-call cost the scans carried: <c>level.Anchors</c> is an
    ///     <c>ObservableCollection</c>, whose <c>foreach</c> goes through
    ///     <c>Collection&lt;T&gt;</c>'s explicitly implemented
    ///     <c>IEnumerable&lt;T&gt;.GetEnumerator()</c> — so every one of the
    ///     ~15,000 lookups a validation pass makes BOXED an enumerator. The index
    ///     is built once and its lookups allocate nothing at all.
    /// </summary>
    [Fact]
    public void AnIndexedLookupAllocatesNothing_WhereTheScanBoxedAnEnumeratorPerCall()
    {
        var level = new ChartLevel();
        for (var i = 0; i < 500; i++) level.Anchors.Add(Anchor(i, 0, 1, 1 + i % 18));
        var context = ContextOver(level);

        var probe = new FractionalPosition(250);
        context.ActiveAnchor(level, probe); // build the index outside the meter

        // Cheapest of several batches on the per-thread counter (AllocationProbe): a single
        // window flaked under a loaded full gate, 2026-09-22, and passed alone.
        var scanned = AllocationProbe.MeasureCheapest(() => () =>
        {
            for (var i = 0; i < 2_000; i++) ArrangementValidationContext.FindActiveAnchor(level, probe);
        });

        output.WriteLine($"2,000 lookups: linear scan {scanned} bytes (cheapest batch).");
        Assert.True(scanned > 0, "the linear scan no longer boxes — re-derive what this test is comparing.");
        AllocationProbe.AssertAllocationFree("2,000 indexed anchor lookups", () =>
        {
            for (var i = 0; i < 2_000; i++) context.ActiveAnchor(level, probe);
        });
    }

    /// <summary>
    ///     …and the rules have to keep using the index. The static scans stay
    ///     public (they are this file's spec, and the local-only charting probes
    ///     call them), so nothing but a guard stops a new rule reintroducing the
    ///     O(sounds × anchors) pass.
    /// </summary>
    [Fact]
    public void NoValidationRule_ScansTheAnchorListLinearly()
    {
        var rulesDir = Path.Combine(TestPaths.RepoRoot(), "DotCharter.Services", "Editing", "Validation", "Rules");
        Assert.True(Directory.Exists(rulesDir), $"missing {rulesDir}");

        var offenders = new List<string>();
        var scanned = 0;
        foreach (var file in Directory.EnumerateFiles(rulesDir, "*.cs"))
        {
            scanned++;
            var source = File.ReadAllText(file);
            foreach (Match m in Regex.Matches(source,
                         @"ArrangementValidationContext\.Find(ActiveAnchor|PreviousAnchor|AnchorAt)"))
            {
                offenders.Add($"{Path.GetFileName(file)}: {m.Value}");
            }

            // The template scans the context now indexes.
            foreach (Match m in Regex.Matches(source, @"ChordTemplates\.FirstOrDefault"))
                offenders.Add($"{Path.GetFileName(file)}: {m.Value}");
        }

        Assert.True(scanned >= 10, $"only {scanned} rule files found — the sweep is looking in the wrong place.");
        Assert.True(offenders.Count == 0,
            "A validation rule scans the anchor or template list linearly. Validate() runs on the 150 ms " +
            "editor debounce over EVERY level, so these are asked once per note, per chord note and per " +
            "hand shape — use the context's ActiveAnchor/PreviousAnchor/AnchorAt/ChordTemplateById:\n  " +
            string.Join("\n  ", offenders));
    }
}
