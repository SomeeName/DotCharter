using System.Text.RegularExpressions;

namespace DotCharter.Tests.Infrastructure;

/// <summary>
///     Keeps the whole CLASS of allocation assertions honest, rather than the
///     four individual tests that have now flaked on it.
///     <para>
///         Two failure modes, both already seen here:
///         <c>FrameSnapshotPerfGateTests</c> metered PROCESS-WIDE
///         <c>GC.GetTotalAllocatedBytes</c> and inherited every background
///         thread's allocations; <c>TunerDisplaySmootherTests</c> metered the
///         right thread but over a SINGLE window, and reported "expected 0, got
///         24" once under heavy load while passing 11/11 on an idle box.
///     </para>
///     <para>
///         <see cref="AllocationProbe" /> closes both. This lint is what stops
///         the next one being written by hand — the pattern is easy to retype
///         and it looks correct right up until the suite is loaded.
///     </para>
///     <para>
///         <b>Both facts carry non-vacuity floors.</b> Until 2026-08-29 neither
///         did: each enumerated files, filtered, and ended at
///         <c>offenders.Count == 0</c>, so a broken glob or a widened
///         <c>IsExempt</c> would have passed over an empty set — the exact
///         failure mode every sibling lint here defends against
///         (<c>HeadlessCollectionLintTests</c>, <c>LocalizationSweepTests</c>,
///         <c>LanPortRangeTests</c>, <c>ViewSubscriptionLeakTests</c> all have
///         one). The floors are a file count AND a positive count of files that
///         actually reach <see cref="AllocationProbe" />, so the lint fails when
///         its own subject disappears rather than when only its glob does.
///     </para>
/// </summary>
public sealed class AllocationAssertionLintTests
{
    /// <summary>
    ///     The exemption is <b>the headless suite</b>, not the whole
    ///     <c>Perf/</c> folder — that folder is not synonymous with the
    ///     exclusion filter and this file used to say it was. Four classes in
    ///     <c>Perf/</c> do NOT match <c>*UiPerfSmokeTests</c> and therefore DO
    ///     run in the default gate: <c>RefretPerfGateTests</c>,
    ///     <c>StartupPerfGateTests</c>, <c>RenderPerfBaselineTests</c> and
    ///     <c>DisplayRefreshProbeTests</c>. Exempting them was wider than the
    ///     stated reason and left the one unskipped resident
    ///     (<c>DisplayRefreshProbeTests</c>) inside a carve-out justified by
    ///     "it is outside the default gate", which is false of it.
    ///     <para>
    ///         What IS exempt: <c>*UiPerfSmokeTests</c>, because
    ///         <c>UiPerfSmokeTests.Quiesce</c> deliberately reads the
    ///         process-wide counter over a booted app heap, where per-thread
    ///         would measure the wrong thing. <see cref="AllocationProbe" />
    ///         itself is the sanctioned per-thread reader.
    ///     </para>
    /// </summary>
    /// <remarks>This file exempts ITSELF: it names both patterns in its own
    /// failure messages, and a lint that fails on its own error text is a lint
    /// nobody can make green.</remarks>
    private static readonly string[] ExemptFiles =
        ["AllocationProbe.cs", "AllocationAssertionLintTests.cs"];

    private static bool IsExempt(string file)
    {
        var name = Path.GetFileName(file);
        return ExemptFiles.Contains(name, StringComparer.Ordinal) ||
               name.Contains("UiPerfSmokeTests", StringComparison.Ordinal);
    }

    [Fact]
    public void NoDefaultGateTest_MetersAllocationsProcessWide()
    {
        var sources = TestSources();
        AssertTheScanIsRealAndItsSubjectExists(sources);

        var offenders = sources
            .Where(f => !IsExempt(f))
            .Where(f => SourceText.StripCommentLines(File.ReadAllText(f))
                .Contains("GC.GetTotalAllocatedBytes", StringComparison.Ordinal))
            .Select(f => Path.GetRelativePath(TestsRoot(), f))
            .ToList();

        Assert.True(offenders.Count == 0,
            "GC.GetTotalAllocatedBytes is PROCESS-WIDE — in a parallel suite it charges every " +
            "background thread's allocations to whichever test happens to be measuring. Use " +
            "AllocationProbe (GC.GetAllocatedBytesForCurrentThread, best-of-N) instead:" +
            Environment.NewLine + string.Join(Environment.NewLine, offenders));
    }

    [Fact]
    public void EveryZeroAllocationClaim_GoesThroughAllocationProbe()
    {
        // A hand-rolled `Assert.Equal(0, GC.GetAllocatedBytesForCurrentThread() - before)`
        // meters the right thread but only ONE window, so a single tiered-JIT
        // perturbation fails it.
        var handRolled = new Regex(
            @"Assert\.(Equal|True)\s*\(\s*0\s*,?[^;]*GetAllocatedBytesForCurrentThread",
            RegexOptions.Singleline);

        var sources = TestSources();
        AssertTheScanIsRealAndItsSubjectExists(sources);

        var offenders = sources
            .Where(f => !IsExempt(f))
            .Where(f => handRolled.IsMatch(SourceText.StripCommentLines(File.ReadAllText(f))))
            .Select(f => Path.GetRelativePath(TestsRoot(), f))
            .ToList();

        Assert.True(offenders.Count == 0,
            "a zero-allocation claim asserted over a SINGLE measurement window flakes on one-off " +
            "tiered-JIT promotion under load (see TunerDisplaySmootherTests, 'expected 0, got 24'). " +
            "Route it through AllocationProbe.AssertAllocationFree, which asserts the cheapest of " +
            "several batches:" + Environment.NewLine + string.Join(Environment.NewLine, offenders));
    }

    /// <summary>
    ///     The two floors, shared by both facts. The first fails a broken glob
    ///     or a moved test root; the second fails a lint whose SUBJECT has gone
    ///     — if nothing routes a zero-allocation claim through
    ///     <see cref="AllocationProbe" /> any more, "zero offenders" means the
    ///     rule stopped being followed, not that it is being followed.
    ///     8 files name the probe today (the two Infrastructure files above plus
    ///     six under <c>Services/Audio/Tuner</c> and <c>Rendering</c>).
    /// </summary>
    private static void AssertTheScanIsRealAndItsSubjectExists(IReadOnlyList<string> sources)
    {
        Assert.True(sources.Count > 500,
            $"only enumerated {sources.Count} test sources — the glob is broken and both lints below " +
            "would pass over an empty set.");

        var probeUsers = sources
            .Where(f => !ExemptFiles.Contains(Path.GetFileName(f), StringComparer.Ordinal))
            .Count(f => SourceText.StripCommentLines(File.ReadAllText(f))
                .Contains("AllocationProbe", StringComparison.Ordinal));

        Assert.True(probeUsers >= 5,
            $"only {probeUsers} test files route an allocation claim through AllocationProbe — either " +
            "the sanctioned reader was renamed (so this lint now bans a pattern nothing replaces) or " +
            "the allocation tests are gone. Re-check Infrastructure/AllocationProbe.cs.");
    }

    private static List<string> TestSources() =>
        Directory.EnumerateFiles(TestsRoot(), "*.cs", SearchOption.AllDirectories)
            .Where(f => !f.Contains($"{Path.DirectorySeparatorChar}obj{Path.DirectorySeparatorChar}",
                            StringComparison.Ordinal) &&
                        !f.Contains($"{Path.DirectorySeparatorChar}bin{Path.DirectorySeparatorChar}",
                            StringComparison.Ordinal))
            .ToList();

    private static string TestsRoot() => Path.Combine(TestPaths.RepoRoot(), "DotCharter.Tests");
}
