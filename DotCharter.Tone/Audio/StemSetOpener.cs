using System.Buffers;
using System.Collections.Concurrent;
using System.Diagnostics;

namespace DotCharter.Tone.Audio;

/// <summary>
///     Opens a stem set as a two-stage pipeline: the files are READ from disk one at
///     a time, in order, and each stem's native open starts the moment its own read
///     is done — while the next file is being read.
///     <para>
/// <b>Why not just open them one after another.</b> The engine decodes
///         every stem into RAM whole and rate-converts it, so a sequential loop pays
///         read + decode + resample six times in a row: 3.6 s for six 64 MB WAVs on the
///         owner's machine (~600 ms each), ~160 ms each when the files were already
///         cached. The decode and the resample are CPU work that overlap freely.
///     </para>
///     <para>
///         <b>Why not just open them all at once.</b> The native WAV reader pulls a
///         few KB per read. Six of those streams against one spinning disk turn into a
///         seek storm: three cold 105 MB stems took 10.4 s opened concurrently against
///         2.1 s read sequentially and then opened (Release harness, a
///         7200 rpm SATA drive). So the READS stay sequential and large
///         (<see cref="PrefetchIntoFileCache" />, 1 MB at a time) and only the decodes
///         overlap; by the time a stem's native open runs, its bytes are in the OS file
///         cache. On a warm cache or an SSD the read is ~15 ms a file and the opens
///         simply run side by side.
///     </para>
///     <para>
///         Pure apart from the two delegates, so the shape is unit-tested without an
///         engine: <c>Services/Audio/StemSetOpenerTests</c>.
///     </para>
/// </summary>
internal static class StemSetOpener
{
    /// <summary>
    ///     How many native opens may run at once. Each one holds its decode buffer and
    ///     the resampler's output side by side for a moment (~130 MB for a 3-minute
    ///     stereo stem), and the resampler already spreads ONE open across several
    ///     cores, so three buys almost all of the win (six 64 MB stems, warm: 840 ms
    ///     sequential, ~390 ms at three, ~330 ms at six) for half the extra peak memory
    ///     (+110 MB at three against +230 MB at six).
    /// </summary>
    internal static int DefaultMaxOpensInFlight => Math.Clamp(Environment.ProcessorCount / 2, 1, 3);

    private const int PrefetchChunkBytes = 1 << 20;

    /// <summary>One stem's result, in REQUEST order whatever order the opens finished in.</summary>
    internal readonly record struct Outcome<T>(T? Source, Exception? Error, long BytesRead, TimeSpan ReadTime, TimeSpan OpenTime)
        where T : class;

    /// <summary>
    ///     Reads each path with <paramref name="prefetch" /> on THIS thread, one after
    ///     another, and hands it to <paramref name="open" /> on one of at most
    ///     <paramref name="maxOpensInFlight" /> worker threads as soon as its read
    ///     returns. Blocks until every open has finished. A failed read is not fatal
    ///     (the open reads the file itself and reports what is wrong with it); a failed
    ///     open is that stem's <see cref="Outcome{T}.Error" /> and never stops the others.
    /// </summary>
    internal static Outcome<T>[] OpenAll<T>(IReadOnlyList<string> paths, Func<string, long> prefetch,
        Func<string, T> open, int maxOpensInFlight) where T : class
    {
        ArgumentNullException.ThrowIfNull(paths);
        ArgumentNullException.ThrowIfNull(prefetch);
        ArgumentNullException.ThrowIfNull(open);

        var results = new Outcome<T>[paths.Count];
        if (paths.Count == 0) return results;

        var reads = new (long Bytes, TimeSpan Time)[paths.Count];
        using var ready = new BlockingCollection<int>();

        // Dedicated threads, not the pool: each one sits in a native decode for
        // hundreds of milliseconds, which is exactly what makes the pool inject.
        var workers = new Task[Math.Clamp(maxOpensInFlight, 1, paths.Count)];
        for (var w = 0; w < workers.Length; w++)
        {
            workers[w] = Task.Factory.StartNew(() =>
            {
                foreach (var i in ready.GetConsumingEnumerable())
                {
                    var openTimer = Stopwatch.StartNew();
                    try
                    {
                        results[i] = new Outcome<T>(open(paths[i]), null, reads[i].Bytes, reads[i].Time, openTimer.Elapsed);
                    }
                    catch (Exception ex)
                    {
                        results[i] = new Outcome<T>(null, ex, reads[i].Bytes, reads[i].Time, openTimer.Elapsed);
                    }
                }
            }, CancellationToken.None, TaskCreationOptions.LongRunning, TaskScheduler.Default);
        }

        try
        {
            for (var i = 0; i < paths.Count; i++)
            {
                var readTimer = Stopwatch.StartNew();
                long bytes;
                try { bytes = prefetch(paths[i]); }
                catch (Exception) { bytes = 0; }

                reads[i] = (bytes, readTimer.Elapsed);
                ready.Add(i);
            }
        }
        finally
        {
            // Even if the read loop dies, the workers must drain and exit — a worker
            // left waiting on an open collection is a thread nobody ever joins.
            ready.CompleteAdding();
            Task.WaitAll(workers);
        }

        return results;
    }

    /// <summary>
    ///     Reads a whole file through the OS cache in large sequential chunks and throws
    ///     the bytes away: the point is that the native open which follows finds the file
    ///     already cached instead of seeking for it a few KB at a time. Returns the number
    ///     of bytes read.
    /// </summary>
    internal static long PrefetchIntoFileCache(string path)
    {
        var buffer = ArrayPool<byte>.Shared.Rent(PrefetchChunkBytes);
        try
        {
            using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete,
                bufferSize: 1, FileOptions.SequentialScan);
            long total = 0;
            int read;
            while ((read = stream.Read(buffer, 0, PrefetchChunkBytes)) > 0) total += read;
            return total;
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(buffer);
        }
    }
}
