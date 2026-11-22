using System.Collections.Concurrent;

namespace DotCharter.Tests.Infrastructure;

/// <summary>
///     Runs every task queued to it on ONE dedicated background thread, so a test that
///     must see a stage of the code under test reached never waits behind a starved
///     thread pool. Dispose ends the thread once the queued tasks have run.
/// </summary>
internal sealed class DedicatedThreadScheduler : TaskScheduler, IDisposable
{
    private readonly BlockingCollection<Task> _queue = [];
    private readonly Thread _thread;

    public DedicatedThreadScheduler()
    {
        _thread = new Thread(Run) { IsBackground = true, Name = nameof(DedicatedThreadScheduler) };
        _thread.Start();
    }

    public override int MaximumConcurrencyLevel => 1;

    public void Dispose() => _queue.CompleteAdding();

    protected override void QueueTask(Task task) => _queue.Add(task);

    protected override bool TryExecuteTaskInline(Task task, bool taskWasPreviouslyQueued) =>
        !taskWasPreviouslyQueued && Thread.CurrentThread == _thread && TryExecuteTask(task);

    protected override IEnumerable<Task> GetScheduledTasks() => _queue.ToArray();

    private void Run()
    {
        foreach (var task in _queue.GetConsumingEnumerable()) TryExecuteTask(task);
    }
}
