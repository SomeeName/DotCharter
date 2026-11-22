using System.Collections.Concurrent;

namespace DotCharter.Tests.Infrastructure;

/// <summary>
///     One thread with a <see cref="SynchronizationContext" /> that posts back to
///     it: an async flow started on it resumes on it after every await that
///     captures the context, like a flow started on the UI thread.
///     <para>
///         What a test gets from that is a QUIESCENCE signal a pool flow cannot
///         give (#607): work posted behind a continuation runs only after that
///         continuation has run to its next await, so <see cref="DrainAsync" />
///         answers "the flow is now parked on something" without a sleep.
///     </para>
/// </summary>
internal sealed class SingleThreadPump : IDisposable
{
    private readonly BlockingCollection<Action> _queue = [];
    private readonly Thread _thread;

    public SingleThreadPump()
    {
        _thread = new Thread(() =>
        {
            SynchronizationContext.SetSynchronizationContext(new Context(this));
            foreach (var work in _queue.GetConsumingEnumerable()) work();
        })
        { IsBackground = true, Name = nameof(SingleThreadPump) };
        _thread.Start();
    }

    /// <summary>Starts <paramref name="work" /> on the pump thread, after everything
    /// already posted. Completes (or faults) with the task it returns.</summary>
    public Task RunAsync(Func<Task> work)
    {
        var done = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        _queue.Add(() => _ = CompleteAsync(work, done));
        return done.Task;
    }

    /// <summary>Completes once everything posted before this call has run.</summary>
    public Task DrainAsync() => RunAsync(() => Task.CompletedTask);

    public void Dispose()
    {
        _queue.CompleteAdding();
        // A deadlock detector: the thread is a background one, so a stuck callback
        // cannot hold the process either way.
        _thread.Join(TimeSpan.FromSeconds(60));
    }

    private static async Task CompleteAsync(Func<Task> work, TaskCompletionSource done)
    {
        try
        {
            await work();
            done.SetResult();
        }
        catch (Exception ex)
        {
            done.SetException(ex);
        }
    }

    private sealed class Context(SingleThreadPump pump) : SynchronizationContext
    {
        public override void Post(SendOrPostCallback d, object? state)
        {
            try
            {
                pump._queue.Add(() => d(state));
            }
            catch (InvalidOperationException)
            {
                // The pump was disposed with a continuation still to come: run it on the
                // pool, as an await without a context would.
                ThreadPool.QueueUserWorkItem(_ => d(state));
            }
        }

        public override void Send(SendOrPostCallback d, object? state) => d(state);
    }
}
