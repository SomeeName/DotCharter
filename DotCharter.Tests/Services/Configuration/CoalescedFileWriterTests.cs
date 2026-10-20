using System.Collections.Concurrent;

namespace DotCharter.Tests.Services.Configuration;

/// <summary>
///     A settings change must cost the UI thread nothing but a field write.
///     <para>
///         <c>appconfig.json</c> is ~17 KB and the atomic write + fsync + rename
///         measured 4.40 ms median / 9.68 ms p90 / 13.48 ms max on the owner's
///         config drive — the fsync alone is ~75% of it. Twelve
///         <c>partial void On&lt;X&gt;Changed</c> setters paid that inline on the
///         UI thread, and <c>OnSheetZoomChanged</c> fires once per zoom step: at
///         10–20 steps/s that is 47–94 ms of UI thread PER SECOND inside fsync,
///         against a 150 ms view-switch budget.
///     </para>
///     <para>
///         These are STRUCTURE assertions in the spirit of
///         <c>ProjectSessionSaveThreadingTests</c> — which thread, how many
///         writes, which value — never a wall-clock budget. The debounce window
///         is held open by a test <c>delay</c> seam rather than by sleeping, so
///         nothing here races the scheduler.
///     </para>
/// </summary>
public sealed class CoalescedFileWriterTests
{
    /// <summary>Holds the debounce window open forever; only Flush/Dispose
    /// (which cancel it) can let a write through. Makes "did it write yet?" a
    /// deterministic question instead of a timing one.</summary>
    private static Task HeldOpen(TimeSpan _, CancellationToken ct)
        => Task.Delay(Timeout.InfiniteTimeSpan, ct);

    /// <summary>(a) The write never runs on the caller's thread. Driven from a
    /// dedicated, NON-pool thread standing in for the UI thread, exactly as
    /// <c>ProjectSessionSaveThreadingTests</c> does — nothing queued to the
    /// thread pool can land back on it, so "caller" versus "worker" is a
    /// deterministic comparison.</summary>
    [Fact]
    public void TheWriteNeverRunsOnTheCallersThread()
    {
        // EVERY write's thread, not just the last one: a writer that wrote
        // inline AND armed a background drain would overwrite a single field
        // with the pool thread's id and the assertion would pass by luck.
        // Threads are compared as OBJECTS, never by managed thread id (#148): the
        // caller has exited by the time we assert, and .NET reuses a dead thread's
        // id, so under load a freshly injected pool thread could carry the caller's
        // id and a correct background write would read as "ran on the caller".
        var writeThreads = new ConcurrentBag<Thread>();
        var wrote = new ManualResetEventSlim(false);
        using var writer = new CoalescedFileWriter<string>(
            (_, _) =>
            {
                writeThreads.Add(Thread.CurrentThread);
                wrote.Set();
            },
            debounce: TimeSpan.Zero);

        var callerThread = RunOnADedicatedThread(() => writer.Queue("cfg", "v1"));

        // A deadlock detector, never a budget.
        Assert.True(wrote.Wait(TimeSpan.FromSeconds(60), TestContext.Current.CancellationToken),
            "The armed write never ran within 60 s (a deadlock detector; a starved box can be slow, not this slow).");
        writer.Flush();

        Assert.NotEmpty(writeThreads);
        Assert.DoesNotContain(callerThread, writeThreads);
    }

    /// <summary>(b) A burst of changes — a zoom spin, a slider drag — costs ONE
    /// write, and it carries the LAST value. Without the coalescer this is 20
    /// fsyncs; with a coalescer that kept the FIRST payload the user's setting
    /// would silently revert on the next launch, so both halves are asserted.</summary>
    [Fact]
    public void ABurstOfChangesProducesOneWriteCarryingTheLastValue()
    {
        var writes = new ConcurrentQueue<string>();
        using var writer = new CoalescedFileWriter<string>(
            (_, payload) => writes.Enqueue(payload),
            delay: HeldOpen);

        for (var i = 1; i <= 20; i++) writer.Queue("cfg", "v" + i);

        Assert.Empty(writes); // nothing has touched the disk yet
        writer.Flush();

        Assert.Equal("v20", Assert.Single(writes));
    }

    /// <summary>Separate targets are not collapsed into each other — the
    /// coalescing key is the file, not the writer.</summary>
    [Fact]
    public void DifferentTargetsEachGetTheirOwnWrite()
    {
        var writes = new ConcurrentDictionary<string, string>();
        using var writer = new CoalescedFileWriter<string>(
            (path, payload) => writes[path] = payload,
            delay: HeldOpen);

        writer.Queue("appconfig", "a1");
        writer.Queue("keybinds", "k1");
        writer.Queue("appconfig", "a2");
        writer.Flush();

        Assert.Equal(2, writes.Count);
        Assert.Equal("a2", writes["appconfig"]);
        Assert.Equal("k1", writes["keybinds"]);
    }

    /// <summary>(c) The exit paths' half: a pending write must survive the way
    /// out. <c>App.TerminateNow</c> calls Flush (it TerminateProcess-es, so no
    /// Dispose ever runs) and <c>App.RunCleanShutdown</c> reaches Dispose through
    /// the container — both are pinned here.</summary>
    [Fact]
    public void FlushDrainsAPendingWrite()
    {
        var writes = new ConcurrentQueue<string>();
        using var writer = new CoalescedFileWriter<string>(
            (_, payload) => writes.Enqueue(payload),
            delay: HeldOpen);

        writer.Queue("cfg", "unsaved");
        Assert.Empty(writes);

        writer.Flush();

        Assert.Equal("unsaved", Assert.Single(writes));
    }

    [Fact]
    public void DisposeDrainsAPendingWrite()
    {
        var writes = new ConcurrentQueue<string>();
        var writer = new CoalescedFileWriter<string>(
            (_, payload) => writes.Enqueue(payload),
            delay: HeldOpen);

        writer.Queue("cfg", "unsaved");
        Assert.Empty(writes);

        writer.Dispose();

        Assert.Equal("unsaved", Assert.Single(writes));
    }

    /// <summary>Anything saved AFTER the writer is disposed still has to reach
    /// the disk — teardown ordering is not something a save site can see.</summary>
    [Fact]
    public void AQueueAfterDisposeWritesThrough()
    {
        var writes = new ConcurrentQueue<string>();
        var writer = new CoalescedFileWriter<string>(
            (_, payload) => writes.Enqueue(payload),
            delay: HeldOpen);
        writer.Dispose();

        writer.Queue("cfg", "late");

        Assert.Equal("late", Assert.Single(writes));
    }

    /// <summary>A pending payload can be ABANDONED: a config-backup import
    /// replaces the file behind the service, and a payload queued before the
    /// import predates it — writing it would silently undo the import.</summary>
    [Fact]
    public void DiscardDropsAPendingWriteWithoutPerformingIt()
    {
        var writes = new ConcurrentQueue<string>();
        using var writer = new CoalescedFileWriter<string>(
            (_, payload) => writes.Enqueue(payload),
            delay: HeldOpen);

        writer.Queue("cfg", "pre-import");
        writer.Discard("cfg");
        writer.Flush();

        Assert.Empty(writes);
    }

    /// <summary>A failing write must not take the drain thread — or the app —
    /// down with it; it is reported and the writer stays usable.</summary>
    [Fact]
    public void AThrowingWriteIsReportedAndTheWriterKeepsWorking()
    {
        var errors = new ConcurrentQueue<Exception>();
        var writes = new ConcurrentQueue<string>();
        var fail = true;
        using var writer = new CoalescedFileWriter<string>(
            (_, payload) =>
            {
                if (fail) throw new IOException("the drive went away");
                writes.Enqueue(payload);
            },
            errors.Enqueue,
            delay: HeldOpen);

        writer.Queue("cfg", "boom");
        writer.Flush();
        Assert.Single(errors);

        fail = false;
        writer.Queue("cfg", "after");
        writer.Flush();
        Assert.Equal("after", Assert.Single(writes));
    }

    private static Thread RunOnADedicatedThread(Action body)
    {
        var thread = new Thread(() => body()) { IsBackground = true, Name = "fake-ui-thread" };
        thread.Start();
        thread.Join();
        return thread;
    }

    /// <summary>
    ///     A write queued while another is IN FLIGHT arms a second drain — and the
    ///     two must not write at once. They did (2026-09-26): both drains ran the
    ///     callback concurrently, two <c>AtomicFile.WriteAllText</c> calls fought
    ///     over one <c>.tmp</c>, and the loser was often the NEWER payload, so the
    ///     last setting a player changed never reached the disk. RigLab's AI tab
    ///     writes on every change and hit it at once.
    ///     <para>
    ///         Sequenced by events, never by time. It used to sleep 50 ms "to give
    ///         the second drain a chance" and then trust <see cref="CoalescedFileWriter{T}.Flush" />
    ///         to have finished both writes — but Flush gives up after the
    ///         product's 3 s exit-path timeout, so on a starved pool (the full
    ///         gate) it returned before v2, or even v1, had landed and the order
    ///         assertion read a half-finished queue (3 failures in 7 full-gate
    ///         runs, green alone). Now the delay seam reports when the SECOND
    ///         drain is actually running before v1 is released, and the test
    ///         waits for v2 itself to land.
    ///     </para>
    /// </summary>
    [Fact]
    public async Task AWriteQueuedWhileOneIsInFlight_WaitsForIt_AndTheNewestPayloadLandsLast()
    {
        var ct = TestContext.Current.CancellationToken;
        var detector = TimeSpan.FromSeconds(60); // deadlock detector, never a budget
        var inside = 0;
        var overlapped = false;
        var drains = 0;
        var firstStarted = new ManualResetEventSlim();
        var secondDrainRunning = new ManualResetEventSlim();
        var releaseFirst = new ManualResetEventSlim();
        var secondLanded = new ManualResetEventSlim();
        var landed = new ConcurrentQueue<string>();
        using var writer = new CoalescedFileWriter<string>(
            (_, payload) =>
            {
                if (Interlocked.Increment(ref inside) > 1) overlapped = true;
                if (payload == "v1")
                {
                    firstStarted.Set();
                    releaseFirst.Wait(detector, ct);
                }
                landed.Enqueue(payload);
                Interlocked.Decrement(ref inside);
                if (payload == "v2") secondLanded.Set();
            },
            debounce: TimeSpan.Zero,
            // Each drain waits its debounce out through this seam exactly once, so
            // the second call is the second drain, already on a pool thread and
            // about to take the write gate v1's drain is holding.
            delay: (_, _) =>
            {
                if (Interlocked.Increment(ref drains) == 2) secondDrainRunning.Set();
                return Task.CompletedTask;
            });

        writer.Queue("cfg", "v1");
        Assert.True(firstStarted.Wait(detector, ct), "the first write never started");
        writer.Queue("cfg", "v2"); // arms a second drain while v1 is being written
        Assert.True(secondDrainRunning.Wait(detector, ct),
            "the second drain never started while v1 was held (the box was starved for a minute?)");
        var second = Task.Run(writer.Flush, ct);
        releaseFirst.Set();
        await second.WaitAsync(detector, ct);
        Assert.True(secondLanded.Wait(detector, ct), "v2 never landed after v1 was released");

        Assert.False(overlapped, "two writes to one file ran at the same time");
        Assert.Equal(["v1", "v2"], landed.ToArray());
    }
}
