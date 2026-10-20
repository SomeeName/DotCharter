using System.Collections.Concurrent;

namespace DotCharter.Services.Library;

/// <summary>One archive's parse: the songs it holds, or why it could not be read.</summary>
internal readonly record struct PsarcParseOutcome(IReadOnlyList<LibrarySongEntry> Songs, Exception? Error);

/// <summary>
/// The cold half of a library scan: the archives the index could not
///     serve are parsed on a few dedicated threads, and every result is handed to
///     ONE writer.
///     <list type="bullet">
///         <item>
///             <b>Bounded and polite.</b> At most <see cref="MaxDegree" /> parsers and
///             never more than half the logical processors (<see cref="DegreeFor" />),
///             each a dedicated BACKGROUND thread at <see cref="ThreadPriority.Normal" />.
///             The BOUND is what leaves the rest of the machine to the UI and a game, not
///             the priority: <b>never lower it below the priority the old single-threaded
///             scan ran at</b> (a Normal pool thread). Under a synthetic 8-thread
///             normal-priority load on its cores, BelowNormal parsers were starved —
///             42–52 s against the serial scan's 4.3–6.4 s, 8–10x SLOWER than the scan
///             they replaced.
///         </item>
///         <item>
///             <b>One writer.</b> Parsers only parse; they push outcomes onto a queue,
///             and <c>commit</c> runs on the CALLING thread, one outcome at
///             a time. The SQLite scan session is therefore never touched by two
///             threads, which its contract forbids.
///         </item>
///         <item>
///             <b>Item order, not completion order.</b> Outcomes that arrive early wait
///             in a reorder buffer, so <c>commit</c> sees items 0, 1, 2…
///             whatever the thread timing. The index rows, their rowids, the
///             <c>songFound</c> batches and the result's tie order are what a serial
///             scan produces.
///         </item>
///         <item>
///             <b>Isolated failures.</b> A parse that throws becomes an outcome
///             carrying the exception; one corrupt archive never stops the others.
///         </item>
///         <item>
///             <b>Cancellable, and nothing outlives the call.</b> Parsers stop picking
///             new work on cancellation, and every parser thread is joined before this
///             returns or throws — no parse runs after the scan has ended.
///         </item>
///     </list>
/// </summary>
internal static class LibraryScanParallelParse
{
    /// <summary>The ceiling whatever the machine: a spinning disk thrashes past a few concurrent readers.</summary>
    internal const int MaxDegree = 4;

    /// <summary>Half the logical processors, at most <see cref="MaxDegree" />, at least one.</summary>
    internal static int DegreeFor(int logicalProcessors) => Math.Max(1, Math.Min(MaxDegree, logicalProcessors / 2));

    /// <summary>
    ///     Parses <paramref name="paths" /> with <paramref name="degree" /> parser threads
    ///     and hands each outcome to <c>commit</c> (with its index in
    ///     <paramref name="paths" />) on the calling thread, in index order.
    ///     <c>commit</c> may throw; the parsers are stopped and joined and
    ///     the exception propagates.
    /// </summary>
    internal static void Run(
        IReadOnlyList<string> paths,
        int degree,
        Func<string, IReadOnlyList<LibrarySongEntry>> parse,
        Action<int, PsarcParseOutcome> commit,
        CancellationToken cancellationToken)
    {
        if (paths.Count == 0) return;

        var threadCount = Math.Clamp(degree, 1, Math.Min(MaxDegree, paths.Count));
        using var stop = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        using var queue = new BlockingCollection<(int Index, PsarcParseOutcome Outcome)>();
        var next = -1;
        var running = threadCount;

        void ParseLoop()
        {
            try
            {
                while (!stop.IsCancellationRequested)
                {
                    var i = Interlocked.Increment(ref next);
                    if (i >= paths.Count) break;

                    PsarcParseOutcome outcome;
                    try
                    {
                        outcome = new PsarcParseOutcome(parse(paths[i]), null);
                    }
                    catch (Exception ex)
                    {
                        outcome = new PsarcParseOutcome([], ex);
                    }

                    queue.Add((i, outcome));
                }
            }
            finally
            {
                // The LAST parser out closes the queue, so the writer's loop ends
                // only once nothing can be added any more.
                if (Interlocked.Decrement(ref running) == 0) queue.CompleteAdding();
            }
        }

        var threads = new Thread[threadCount];
        for (var t = 0; t < threadCount; t++)
        {
            threads[t] = new Thread(ParseLoop)
            {
                IsBackground = true,
                Priority = ThreadPriority.Normal,
                Name = $"PSARC scan parser {t + 1}"
            };
        }

        try
        {
            foreach (var thread in threads)
                thread.Start();

            var pending = new Dictionary<int, PsarcParseOutcome>();
            var nextToCommit = 0;
            foreach (var (index, outcome) in queue.GetConsumingEnumerable(cancellationToken))
            {
                pending.Add(index, outcome);
                while (pending.Remove(nextToCommit, out var ready))
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    commit(nextToCommit, ready);
                    nextToCommit++;
                }
            }

            cancellationToken.ThrowIfCancellationRequested();
        }
        finally
        {
            stop.Cancel();
            foreach (var thread in threads)
            {
                // ThreadState is a flag set: an unstarted background thread reads
                // Background | Unstarted, and Join on it throws.
                if ((thread.ThreadState & ThreadState.Unstarted) == 0)
                    thread.Join();
            }
        }
    }
}
