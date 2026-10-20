using System.Collections.Concurrent;
using DotCharter.Tone.Audio;

namespace DotCharter.Tests.Services.Audio;

/// <summary>
///     <para>
///         #203: a project's stem mixer arrived 3.6 s after the project, because
///         <c>LoadStemsCore</c> opened six 64 MB stems one after another — read,
///         decode, resample, then the next. <see cref="StemSetOpener" /> reads the files
///         in order on the calling thread and overlaps the native opens with the reads
///         and with each other. Measuring the gain is a log line, not a test; what the
///         gate pins is the SHAPE, with fake read/open delegates and rendezvous waits
///         that can only be satisfied when the work really overlaps.
///     </para>
///     <para>
///         The reads must NOT go parallel along with the opens: three cold 105 MB stems
///         opened concurrently against one spinning disk took 10.4 s, against 2.1 s read
///         one at a time. That half is pinned by where and in what order the reads run.
///     </para>
///     <para>
///         Every wait is capped at 60 s as a deadlock detector and its result is
///         asserted, so a shape that cannot overlap fails red instead of hanging.
///     </para>
/// </summary>
public sealed class StemSetOpenerTests
{
    private static readonly TimeSpan DeadlockCap = TimeSpan.FromSeconds(60);

    private static string[] Paths(int count) => Enumerable.Range(0, count).Select(i => $"stem{i}.wav").ToArray();

    [Fact]
    public void TheOpensRunAtTheSameTime()
    {
        var paths = Paths(3);
        using var allEntered = new CountdownEvent(paths.Length);
        var satisfied = new ConcurrentBag<bool>();

        var results = StemSetOpener.OpenAll(paths, _ => 0, path =>
        {
            allEntered.Signal();
            // Only reachable when all three opens are in flight together. A loop
            // that waits for one open before starting the next never gets here.
            satisfied.Add(allEntered.Wait(DeadlockCap));
            return path;
        }, maxOpensInFlight: 3);

        Assert.Equal(3, satisfied.Count);
        Assert.True(satisfied.All(s => s),
            "The stem opens did not overlap: each one waited for the previous one to finish, which is the "
            + "sequential load #203 measured at 3.6 s for six stems.");
        Assert.All(results, r => Assert.Null(r.Error));
    }

    [Fact]
    public void TheReadsRunAheadOfTheOpens_EvenWithOneOpenInFlight()
    {
        var paths = Paths(3);
        using var lastRead = new ManualResetEventSlim();
        var firstOpenSawTheLastRead = (bool?)null;

        StemSetOpener.OpenAll(paths, path =>
        {
            if (path == paths[^1]) lastRead.Set();
            return 0;
        }, path =>
        {
            // The first open holds its (only) slot until the reader has reached the
            // LAST file: a read-open-read-open loop deadlocks here instead.
            if (path == paths[0]) firstOpenSawTheLastRead = lastRead.Wait(DeadlockCap);
            return path;
        }, maxOpensInFlight: 1);

        Assert.True(firstOpenSawTheLastRead,
            "The next stem's read waited for the previous stem's open. The disk then idles through every "
            + "decode — the reads have to run ahead of the opens.");
    }

    [Fact]
    public void TheReadsRunOneAtATime_InOrder_OnTheCallingThread_EachBeforeItsOwnOpen()
    {
        var paths = Paths(5);
        var events = new ConcurrentQueue<string>();
        var readThreads = new ConcurrentBag<int>();
        var caller = Environment.CurrentManagedThreadId;

        StemSetOpener.OpenAll(paths, path =>
        {
            readThreads.Add(Environment.CurrentManagedThreadId);
            events.Enqueue("read-start " + path);
            events.Enqueue("read-end " + path);
            return 0;
        }, path =>
        {
            events.Enqueue("open " + path);
            return path;
        }, maxOpensInFlight: 3);

        // One reader: concurrent reads of several stems against one spinning disk are
        // a seek storm (10.4 s against 2.1 s for three cold 105 MB stems).
        Assert.All(readThreads, id => Assert.Equal(caller, id));

        var log = events.ToArray();
        var reads = log.Where(e => e.StartsWith("read-", StringComparison.Ordinal)).ToArray();
        var expected = paths.SelectMany(p => new[] { "read-start " + p, "read-end " + p }).ToArray();
        Assert.Equal(expected, reads);

        foreach (var p in paths)
        {
            Assert.True(Array.IndexOf(log, "read-end " + p) < Array.IndexOf(log, "open " + p),
                $"{p} was opened before its read finished — the open would race the prefetch for the disk.");
        }
    }

    [Fact]
    public void NoMoreThanTheCapOpenAtOnce()
    {
        var paths = Paths(6);
        var inFlight = 0;
        var maxInFlight = 0;
        using var pair = new Barrier(2);
        var satisfied = new ConcurrentBag<bool>();

        StemSetOpener.OpenAll(paths, _ => 0, path =>
        {
            var now = Interlocked.Increment(ref inFlight);
            InterlockedMax(ref maxInFlight, now);
            satisfied.Add(pair.SignalAndWait(DeadlockCap));
            Interlocked.Decrement(ref inFlight);
            return path;
        }, maxOpensInFlight: 2);

        Assert.True(satisfied.All(s => s), "Two opens never ran together under a cap of two.");
        Assert.Equal(2, maxInFlight);
    }

    [Fact]
    public void ResultsKeepRequestOrder_WhateverOrderTheOpensFinishIn()
    {
        var paths = Paths(4);
        var finished = paths.Select(_ => new ManualResetEventSlim()).ToArray();
        var satisfied = new ConcurrentBag<bool>();

        // Each open waits for the NEXT one to finish, so they complete last to first.
        var results = StemSetOpener.OpenAll(paths, _ => 0, path =>
        {
            var i = Array.IndexOf(paths, path);
            if (i + 1 < paths.Length) satisfied.Add(finished[i + 1].Wait(DeadlockCap));
            finished[i].Set();
            return "source of " + path;
        }, maxOpensInFlight: 4);

        Assert.True(satisfied.All(s => s));
        Assert.Equal(paths.Select(p => "source of " + p), results.Select(r => r.Source));
        foreach (var e in finished) e.Dispose();
    }

    [Fact]
    public void OneFailedOpenOrReadDoesNotStopTheOthers()
    {
        var paths = Paths(3);
        var boom = new InvalidOperationException("corrupt WAV");

        var results = StemSetOpener.OpenAll(paths,
            path => path == paths[0] ? throw new IOException("locked") : 42,
            path => path == paths[1] ? throw boom : path,
            maxOpensInFlight: 2);

        // A failed READ still opens: the native open reads the file itself and says
        // what is wrong with it.
        Assert.Equal(paths[0], results[0].Source);
        Assert.Equal(0, results[0].BytesRead);
        Assert.Null(results[1].Source);
        Assert.Same(boom, results[1].Error);
        Assert.Equal(paths[2], results[2].Source);
        Assert.Equal(42, results[2].BytesRead);
    }

    [Fact]
    public void AnEmptySetOpensNothing()
    {
        var opened = 0;
        var results = StemSetOpener.OpenAll([], _ => 0, path =>
        {
            Interlocked.Increment(ref opened);
            return path;
        }, maxOpensInFlight: 3);

        Assert.Empty(results);
        Assert.Equal(0, opened);
    }

    [Fact]
    public void PrefetchReadsTheWholeFile()
    {
        var path = Path.Combine(Path.GetTempPath(), $"dotcharter-stem-prefetch-{Guid.NewGuid():N}.bin");
        var length = (3 << 20) + 123; // three full chunks and a partial one
        File.WriteAllBytes(path, new byte[length]);
        try
        {
            Assert.Equal(length, StemSetOpener.PrefetchIntoFileCache(path));
        }
        finally
        {
            File.Delete(path);
        }

        Assert.Throws<FileNotFoundException>(() => StemSetOpener.PrefetchIntoFileCache(path));
    }

    /// <summary>
    ///     The product half: <c>LoadStemsCore</c> must open through the opener, and
    ///     its ONE <c>engine.OpenFile(</c> must be the delegate handed to it — a
    ///     second, looped call is the sequential load coming back. A source lint
    ///     because reaching <c>LoadStemsCore</c> means opening a real audio device
    ///     (see <see cref="StemSwapReleaseOrderTests" />).
    /// </summary>
    [Fact]
    public void LoadStemsCore_OpensTheSetThroughTheOpener()
    {
        var (path, body) = StemSwapReleaseOrderTests.LoadStemsCoreBody();

        var call = body.IndexOf("StemSetOpener.OpenAll(", StringComparison.Ordinal);
        Assert.True(call >= 0,
            $"{path}: LoadStemsCore no longer opens its stems through StemSetOpener.OpenAll — the six native "
            + "opens are back to running one after another (#203).");

        var callEnd = MatchingParen(body, body.IndexOf('(', call));
        var openFiles = AllIndexesOf(body, "engine.OpenFile(").ToArray();
        Assert.True(openFiles.Length == 1,
            $"{path}: LoadStemsCore calls engine.OpenFile( {openFiles.Length} time(s); the only call must be the "
            + "delegate handed to StemSetOpener.OpenAll.");
        Assert.True(openFiles[0] > call && openFiles[0] < callEnd,
            $"{path}: LoadStemsCore's engine.OpenFile( is not the delegate handed to StemSetOpener.OpenAll.");
    }

    private static void InterlockedMax(ref int target, int value)
    {
        int seen;
        while ((seen = Volatile.Read(ref target)) < value &&
               Interlocked.CompareExchange(ref target, value, seen) != seen)
        {
        }
    }

    private static int MatchingParen(string text, int open)
    {
        var depth = 0;
        for (var i = open; i < text.Length; i++)
        {
            if (text[i] == '(') depth++;
            else if (text[i] == ')' && --depth == 0) return i;
        }

        return text.Length;
    }

    private static IEnumerable<int> AllIndexesOf(string text, string value)
    {
        var i = text.IndexOf(value, StringComparison.Ordinal);
        while (i >= 0)
        {
            yield return i;
            i = text.IndexOf(value, i + value.Length, StringComparison.Ordinal);
        }
    }
}
