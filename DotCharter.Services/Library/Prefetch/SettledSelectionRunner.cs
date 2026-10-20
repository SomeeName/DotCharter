namespace DotCharter.Services.Library.Prefetch;

/// <summary>
/// Runs background work for whatever a selection SETTLES on: each
///     <see cref="Select" /> cancels the previous item's wait or work and starts a
///     new settle delay, so scrolling through a list runs nothing. At most one
///     piece of work runs at a time — the next waits for the cancelled one to
///     unwind (work past its point of no return may finish; it is never torn).
///     Pure: the clock is the injected <c>delay</c>, so tests drive it by hand.
/// </summary>
public sealed class SettledSelectionRunner<T> : IDisposable
    where T : class
{
    private readonly TimeSpan _settleDelay;
    private readonly Func<TimeSpan, CancellationToken, Task> _delay;
    private readonly Func<T, CancellationToken, Task> _work;
    private readonly Action<T, Exception> _onFault;
    private readonly object _gate = new();
    private CancellationTokenSource? _current;
    private Task _tail = Task.CompletedTask;
    private bool _disposed;

    public SettledSelectionRunner(
        TimeSpan settleDelay,
        Func<TimeSpan, CancellationToken, Task> delay,
        Func<T, CancellationToken, Task> work,
        Action<T, Exception> onFault)
    {
        _settleDelay = settleDelay;
        _delay = delay;
        _work = work;
        _onFault = onFault;
    }

    /// <summary>
    ///     The selection moved to <paramref name="item" /> (null: nothing to run).
    ///     Never blocks and never throws; the returned task completes when this
    ///     item's run has finished, been cancelled or been superseded.
    /// </summary>
    public Task Select(T? item)
    {
        lock (_gate)
        {
            _current?.Cancel();
            _current = null;
            if (item is null || _disposed) return _tail;

            var cts = new CancellationTokenSource();
            _current = cts;
            _tail = RunAsync(item, _tail, cts);
            return _tail;
        }
    }

    private async Task RunAsync(T item, Task previous, CancellationTokenSource cts)
    {
        try
        {
            await _delay(_settleDelay, cts.Token).ConfigureAwait(false);
            await AwaitQuietly(previous).ConfigureAwait(false);
            cts.Token.ThrowIfCancellationRequested();
            await _work(item, cts.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (cts.IsCancellationRequested)
        {
            // Superseded: the next selection's run takes over.
        }
        catch (Exception ex)
        {
            _onFault(item, ex);
        }
        finally
        {
            lock (_gate)
            {
                if (ReferenceEquals(_current, cts)) _current = null;
                cts.Dispose();
            }
        }
    }

    private static async Task AwaitQuietly(Task task)
    {
        try
        {
            await task.ConfigureAwait(false);
        }
        catch
        {
            // The previous run reports its own faults; this one only waits for it.
        }
    }

    /// <summary>Cancels whatever is pending or running; later selections run nothing.</summary>
    public void Dispose()
    {
        lock (_gate)
        {
            _disposed = true;
            _current?.Cancel();
            _current = null;
        }
    }
}
