using DotCharter.Tone.Diagnostics;

namespace DotCharter.Tests.Services.Diagnostics;

public sealed class StatusBarServiceTests
{
    private sealed class ImmediateDispatcher : IUiDispatcher
    {
        public bool CheckAccess() => true;
        public void Post(Action action) => action();
        public void PostNormal(Action action) => action();
        public Task InvokeAsync(Action action) { action(); return Task.CompletedTask; }
        public Task<T> InvokeAsync<T>(Func<T> func) => Task.FromResult(func());
        public Task<T> InvokeAsync<T>(Func<Task<T>> func) => func();
    }

    [Fact]
    public void BeginAndCompleteTask_AddsAndRemoves()
    {
        var svc = new StatusBarService(new ImmediateDispatcher());

        var task = svc.BeginTask("t1", "Loading…", indeterminate: true);
        Assert.Equal("t1", task.Id);
        Assert.True(task.IsIndeterminate);
        Assert.Single(svc.ActiveTasks);

        svc.ReportProgress("t1", 0.5, "Halfway");
        Assert.False(task.IsIndeterminate);
        Assert.Equal(0.5, task.Progress, 3);
        Assert.Equal("Halfway", task.Label);

        svc.CompleteTask("t1");
        Assert.Empty(svc.ActiveTasks);
    }

    [Fact]
    public void BeginTask_Twice_UpdatesExistingInsteadOfDuplicating()
    {
        var svc = new StatusBarService(new ImmediateDispatcher());
        var first = svc.BeginTask("dup", "A", true);
        var second = svc.BeginTask("dup", "B", false);

        Assert.Same(first, second);
        Assert.Single(svc.ActiveTasks);
        Assert.Equal("B", first.Label);
        Assert.False(first.IsIndeterminate);
    }

    [Fact]
    public void CompleteTask_UnknownId_IsNoOp()
    {
        var svc = new StatusBarService(new ImmediateDispatcher());
        svc.CompleteTask("never-existed");
        Assert.Empty(svc.ActiveTasks);
    }

    [Fact]
    public void ClearMessages_EmptiesCollection()
    {
        var svc = new StatusBarService(new ImmediateDispatcher());
        svc.ClearMessages();
        Assert.Empty(svc.RecentMessages);
    }

    /// <summary>
    ///     Models the real thing: producers run on thread-pool threads while
    ///     ActiveTasks is only ever mutated later, on the UI thread. Lookups must
    ///     NOT read the ObservableCollection — they must not depend on the posted
    ///     mutation having run yet either.
    /// </summary>
    private sealed class DeferredDispatcher : IUiDispatcher
    {
        private readonly Queue<Action> _pending = new();

        public bool CheckAccess() => false;
        public void Post(Action action) { lock (_pending) _pending.Enqueue(action); }
        public void PostNormal(Action action) => Post(action);
        public Task InvokeAsync(Action action) { Post(action); return Task.CompletedTask; }
        public Task<T> InvokeAsync<T>(Func<T> func) => Task.FromResult(func());
        public Task<T> InvokeAsync<T>(Func<Task<T>> func) => func();

        /// <summary>Runs everything posted so far, in order — the UI thread catching up.</summary>
        public void Drain()
        {
            while (true)
            {
                Action next;
                lock (_pending)
                {
                    if (_pending.Count == 0) return;
                    next = _pending.Dequeue();
                }

                next();
            }
        }
    }

    [Fact]
    public void ReportProgress_BeforeTheUiThreadHasAppliedTheInsert_StillFindsTheTask()
    {
        var dispatcher = new DeferredDispatcher();
        var svc = new StatusBarService(dispatcher);

        var task = svc.BeginTask("restart", "Restarting audio…", indeterminate: true);
        // The UI thread has not run yet, so ActiveTasks is still empty. Looking the
        // task up in the collection (the old FindTask) would miss it entirely.
        Assert.Empty(svc.ActiveTasks);

        svc.ReportProgress("restart", 0.75, "Almost there");
        dispatcher.Drain();

        Assert.Same(task, Assert.Single(svc.ActiveTasks));
        Assert.Equal(0.75, task.Progress, 3);
        Assert.Equal("Almost there", task.Label);
        Assert.False(task.IsIndeterminate);
    }

    [Fact]
    public void BeginTask_SameIdFromManyThreads_CreatesExactlyOneTask()
    {
        var dispatcher = new DeferredDispatcher();
        var svc = new StatusBarService(dispatcher);

        var results = new StatusBarTask[32];
        Parallel.For(0, results.Length, i => results[i] = svc.BeginTask("engine", $"Loading {i}", indeterminate: true));

        dispatcher.Drain();

        var single = Assert.Single(svc.ActiveTasks);
        Assert.All(results, t => Assert.Same(single, t));
    }

    [Fact]
    public void CompleteTask_ThenBeginTaskWithTheSameId_LeavesTheNewTaskActive()
    {
        var dispatcher = new DeferredDispatcher();
        var svc = new StatusBarService(dispatcher);

        svc.BeginTask("vst", "First", indeterminate: true);
        svc.CompleteTask("vst");
        var second = svc.BeginTask("vst", "Second", indeterminate: true);

        dispatcher.Drain();

        var single = Assert.Single(svc.ActiveTasks);
        Assert.Same(second, single);
        Assert.Equal("Second", single.Label);
    }
}
