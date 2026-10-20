namespace DotCharter.Tests.Infrastructure;

/// <summary>
///     The one way to assert a hot path allocates nothing.
///     <para>
///         Two things make a naive <c>Assert.Equal(0, after - before)</c> flake,
///         and this repo has now been bitten by both:
///     </para>
///     <para>
///         <b>1. Metering the wrong thing.</b> <c>GC.GetTotalAllocatedBytes</c> is
///         PROCESS-WIDE, so every background thread in a parallel suite lands in
///         the budget — that is what broke
///         <c>FrameSnapshotPerfGateTests.WarmRebuild_StaysWithinAllocationBudget</c>
///         (see <c>DotCharter.Tests/AGENTS.md</c>).
///         <see cref="GC.GetAllocatedBytesForCurrentThread" /> measures exactly
///         the claim, and it is the only counter used here.
///     </para>
///     <para>
///         <b>2. Measuring a SINGLE window.</b> Per-thread metering is still not
///         enough, because a one-off perturbation of the pooled test thread lands
///         inside whichever window happens to contain it — tiered JIT is the
///         usual source, since tier-0 code does not get the escape analysis that
///         lets tier-1 stack-allocate, and on a loaded box promotion arrives
///         later. <c>TunerDisplaySmootherTests.Process_IsAllocationFree_AfterWarmup</c>
///         reported "expected 0, got 24" exactly once, under ~12 concurrent
///         agents, and passed 11/11 runs on an idle machine.
///     </para>
///     <para>
///         So the claim is asserted on the MINIMUM of several measured batches. A
///         real per-call allocation shows up in EVERY batch, so the minimum is
///         still a hard zero-byte claim — it only declines to fail on a one-off
///         that a steady-state hot path would never repeat. Never "fix" a
///         reading here by widening the budget to a non-zero number: that hides
///         the regression this exists to catch.
///     </para>
/// </summary>
public static class AllocationProbe
{
    private const int DefaultWarmups = 3;
    private const int DefaultBatches = 3;

    /// <summary>
    ///     Runs <paramref name="batch" /> to warm it, then measures it several
    ///     times and asserts the cheapest run allocated nothing on this thread.
    ///     <para>
    ///         <paramref name="batch" /> must be REPEATABLE — it is invoked
    ///         several times, so anything it drives with a timestamp or a
    ///         sequence number has to keep advancing across calls rather than
    ///         replaying the same values, or a second run takes a different
    ///         branch than the one being measured.
    ///     </para>
    /// </summary>
    public static void AssertAllocationFree(
        string what, Action batch, int warmups = DefaultWarmups, int batches = DefaultBatches)
    {
        // The delegate (and anything it closes over) is allocated by the CALLER,
        // before the first measurement window opens.
        for (var i = 0; i < warmups; i++) batch();

        var cheapest = long.MaxValue;
        var readings = new long[batches];
        for (var i = 0; i < batches; i++)
        {
            var before = GC.GetAllocatedBytesForCurrentThread();
            batch();
            readings[i] = GC.GetAllocatedBytesForCurrentThread() - before;
            cheapest = Math.Min(cheapest, readings[i]);
        }

        Assert.True(cheapest == 0,
            $"{what} allocated {cheapest} B on the measuring thread in its cheapest of {batches} " +
            $"batches (expected 0). Every batch: [{string.Join(", ", readings)}] B. " +
            "A steady-state hot path allocates in EVERY batch, so this is a real regression rather " +
            "than a scheduling artefact.");
    }

    /// <summary>
    ///     The measuring half on its own, for the claims that are a RATIO rather
    ///     than a hard zero ("the bulk path allocates at least 20× less than the
    ///     loop it replaced"). Same two rules as
    ///     <see cref="AssertAllocationFree" /> — per-thread counter, cheapest of
    ///     several batches — so a one-off tiered-JIT perturbation on a loaded box
    ///     cannot decide the comparison. Compare two of these, never two single
    ///     windows.
    ///     <para>
    ///         <paramref name="prepare" /> builds a FRESH scenario and returns the
    ///         action to meter; it runs OUTSIDE the measurement window, which is
    ///         what makes repeated batches legal for a batch that would otherwise
    ///         be a no-op the second time (adding the same items to a collection
    ///         that already holds them, say).
    ///     </para>
    /// </summary>
    public static long MeasureCheapest(
        Func<Action> prepare, int warmups = DefaultWarmups, int batches = DefaultBatches)
    {
        for (var i = 0; i < warmups; i++) prepare()();

        var cheapest = long.MaxValue;
        for (var i = 0; i < batches; i++)
        {
            var batch = prepare();
            var before = GC.GetAllocatedBytesForCurrentThread();
            batch();
            cheapest = Math.Min(cheapest, GC.GetAllocatedBytesForCurrentThread() - before);
        }

        return cheapest;
    }
}
