namespace DotCharter.Tone.Infrastructure.Configuration;

/// <summary>
///     Debounced, last-write-wins background writer for the small config files
///     the UI persists on every observable-property change.
///     <para>
///         Why it exists: <c>appconfig.json</c> is ~17 KB and
///         <see cref="Storage.AtomicFile" /> write + fsync + rename measured
///         4.40 ms median / 9.68 ms p90 / 13.48 ms max on the owner's config
///         drive (the fsync is ~75% of it). Twelve
///         <c>partial void On&lt;X&gt;Changed</c> setters persisted straight
///         through that on the UI thread, and one of them — the sheet-music zoom
///         — fires per zoom step: at 10–20 steps/s that is 47–94 ms of UI thread
///         PER SECOND spent inside fsync, against a 150 ms switch budget.
///     </para>
///     <para>
///         The contract: <see cref="Queue" /> never touches the disk. It records
///         one pending payload per target path (a second queue for the same path
///         REPLACES the first — last write wins) and arms a single background
///         drain. A crash inside the debounce window loses at most that window's
///         worth of config, which is why both exit paths call
///         <see cref="Flush" /> — see <c>App.TerminateNow</c> (which
///         <c>TerminateProcess</c>es, so no <c>Dispose</c> ever runs) and
///         <c>App.RunCleanShutdown</c>.
///     </para>
///     <para>
///         The write callback runs on a thread-pool thread, NEVER on the caller.
///         Anything the callback needs to lock must therefore not already be held
///         by whoever calls <see cref="Flush" /> — <c>lock</c> is reentrant per
///         THREAD, and the drain is a different one.
///     </para>
/// </summary>
/// <typeparam name="TPayload">What the writer serializes; kept as the live
/// object rather than pre-rendered text so the serialize also moves off the
/// caller's thread and can stay inside the owner's lock.</typeparam>
internal sealed class CoalescedFileWriter<TPayload> : IDisposable where TPayload : class
{
    /// <summary>How long after the LAST change the write happens. Long enough to
    /// swallow a zoom spin or a slider drag, short enough that the worst a crash
    /// can cost is a quarter second of settings.</summary>
    public static readonly TimeSpan DefaultDebounce = TimeSpan.FromMilliseconds(250);

    /// <summary>Upper bound on how long an exit path waits for a pending write.
    /// A stalled network drive must not hang the process on quit.</summary>
    private static readonly TimeSpan FlushTimeout = TimeSpan.FromSeconds(3);

    private readonly TimeSpan _debounce;
    private readonly Func<TimeSpan, CancellationToken, Task> _delay;
    private readonly Action<Exception>? _onError;
    private readonly Dictionary<string, TPayload> _pending = new(StringComparer.OrdinalIgnoreCase);

    private readonly object _sync = new();

    /// <summary>Held across "take the next payload, write it". A Queue that lands
    /// while a write is in flight arms a SECOND drain, and without this the two
    /// ran the callback at once: two atomic writes fought over one <c>.tmp</c>
    /// and the loser was often the newer payload. Taking the payload inside the
    /// gate also keeps the order — a drain cannot pick an older payload and write
    /// it after a newer one. Never taken while holding <c>_sync</c>.</summary>
    private readonly object _writeGate = new();
    private readonly Action<string, TPayload> _write;

    private CancellationTokenSource? _armed;
    private bool _disposed;
    private Task _drain = Task.CompletedTask;

    /// <param name="write">Performs the actual serialize + atomic write. Runs on
    /// a background thread; must not throw for the caller's benefit (it is
    /// guarded, and anything it throws is reported through
    /// <paramref name="onError" />).</param>
    /// <param name="onError">Where a failed write is reported.</param>
    /// <param name="debounce">Test seam; defaults to
    /// <see cref="DefaultDebounce" />.</param>
    /// <param name="delay">Test seam: how the debounce window is waited out.
    /// Defaults to <see cref="Task.Delay(TimeSpan, CancellationToken)" />, whose
    /// cancellation is what pulls a pending write forward on
    /// <see cref="Flush" />.</param>
    public CoalescedFileWriter(
        Action<string, TPayload> write,
        Action<Exception>? onError = null,
        TimeSpan? debounce = null,
        Func<TimeSpan, CancellationToken, Task>? delay = null)
    {
        ArgumentNullException.ThrowIfNull(write);
        _write = write;
        _onError = onError;
        _debounce = debounce ?? DefaultDebounce;
        _delay = delay ?? ((d, ct) => Task.Delay(d, ct));
    }

    /// <summary>Drains anything still pending. Idempotent.</summary>
    public void Dispose()
    {
        lock (_sync)
        {
            if (_disposed) return;
            _disposed = true;
        }

        Flush();
    }

    /// <summary>Records <paramref name="payload" /> as the content
    /// <paramref name="path" /> should end up with and arms the background
    /// drain. Returns immediately; no I/O happens on this thread.</summary>
    public void Queue(string path, TPayload payload)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        ArgumentNullException.ThrowIfNull(payload);

        lock (_sync)
        {
            if (!_disposed)
            {
                _pending[path] = payload; // last write wins
                if (_armed != null) return;

                var cts = new CancellationTokenSource();
                _armed = cts;
                _drain = Task.Run(() => DrainAsync(cts));
                return;
            }
        }

        // Past Dispose there is no drain left to run this, and a save on the way
        // out still has to reach the disk. OUTSIDE _sync deliberately: the write
        // callback takes the owner's lock, and the owner queues while holding it
        // — taking _sync first here would invert that order.
        WriteGuarded(path, payload);
    }

    /// <summary>Forgets a pending write for <paramref name="path" /> without
    /// performing it. Used when something has replaced the file BEHIND the owner
    /// (a config-backup import): the queued payload predates the import and
    /// writing it would silently undo it.</summary>
    public void Discard(string path)
    {
        lock (_sync)
        {
            _pending.Remove(path);
        }
    }

    /// <summary>Performs any pending write NOW and waits for it. Must not be
    /// called while holding a lock the write callback also takes.</summary>
    public void Flush()
    {
        while (true)
        {
            Task drain;
            lock (_sync)
            {
                if (_pending.Count == 0 && _drain.IsCompleted) return;
                _armed?.Cancel(); // pull the debounce window forward
                drain = _drain;
            }

            if (!drain.Wait(FlushTimeout)) return;
        }
    }

    private async Task DrainAsync(CancellationTokenSource cts)
    {
        try
        {
            try
            {
                await _delay(_debounce, cts.Token).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                // Flush pulled the window forward — write immediately.
            }

            // Disarm BEFORE writing: a Queue that lands while the write is in
            // flight must arm a fresh drain rather than be lost.
            lock (_sync)
            {
                if (ReferenceEquals(_armed, cts)) _armed = null;
            }

            WritePending();
        }
        catch (Exception ex)
        {
            _onError?.Invoke(ex);
        }
        finally
        {
            cts.Dispose();
        }
    }

    private void WritePending()
    {
        while (true)
        {
            lock (_writeGate)
            {
                string path;
                TPayload payload;
                lock (_sync)
                {
                    if (_pending.Count == 0) return;
                    var first = _pending.First();
                    path = first.Key;
                    payload = first.Value;
                    _pending.Remove(path);
                }

                WriteGuarded(path, payload);
            }
        }
    }

    private void WriteGuarded(string path, TPayload payload)
    {
        try
        {
            _write(path, payload);
        }
        catch (Exception ex)
        {
            _onError?.Invoke(ex);
        }
    }
}
