using System.Diagnostics;

namespace DotCharter.Tests.Perf;

/// <summary>One harness measurement: wall-clock stats + per-iteration managed
/// allocations. The printed line is the citable artifact.</summary>
public readonly record struct PerfResult(double MedianMs, double P95Ms, long AllocBytesPerIter, int Iterations);

/// <summary>
///     B7 (v0.30): the measurement core every Engine-room perf claim must cite.
///     Not a budget gate — it PRINTS a stable <c>[PerfHarness]</c> line (median +
///     p95 wall ms, allocated managed bytes/iteration) so a before/after delta is
///     a reproducible one-liner, per the roadmap's re-measure rule ("perf claims
///     without harness numbers don't ship"). Wall clocks are dev-machine-only —
///     callers gate on <c>DOTCHARTER_PERF_GATE=1</c> like the other Perf tests.
/// </summary>
public static class RenderPerfHarness
{
    /// <summary>Runs <paramref name="body"/> <paramref name="warmup"/> times
    /// unmeasured (JIT/caches), then <paramref name="iterations"/> measured
    /// runs. Allocation figure is per iteration, measured across the whole
    /// batch on the current thread.</summary>
    public static PerfResult Measure(ITestOutputHelper output, string name, int warmup, int iterations,
        Action body)
    {
        for (var i = 0; i < warmup; i++) body();

        GC.Collect();
        GC.WaitForPendingFinalizers();
        GC.Collect();

        var samplesMs = new double[iterations];
        var allocBefore = GC.GetAllocatedBytesForCurrentThread();
        for (var i = 0; i < iterations; i++)
        {
            var t0 = Stopwatch.GetTimestamp();
            body();
            samplesMs[i] = Stopwatch.GetElapsedTime(t0).TotalMilliseconds;
        }

        var allocPerIter = (GC.GetAllocatedBytesForCurrentThread() - allocBefore) / iterations;

        Array.Sort(samplesMs);
        var result = new PerfResult(
            Percentile(samplesMs, 0.50),
            Percentile(samplesMs, 0.95),
            allocPerIter,
            iterations);

        output.WriteLine(
            $"[PerfHarness] {name}: median={result.MedianMs:0.000} ms p95={result.P95Ms:0.000} ms " +
            $"alloc={result.AllocBytesPerIter:N0} B/iter ({iterations} iters, {warmup} warmup)");
        return result;
    }

    private static double Percentile(double[] sorted, double p)
    {
        if (sorted.Length == 0) return 0;
        var rank = p * (sorted.Length - 1);
        var low = (int)Math.Floor(rank);
        var high = (int)Math.Ceiling(rank);
        if (low == high) return sorted[low];
        return sorted[low] + (sorted[high] - sorted[low]) * (rank - low);
    }
}
