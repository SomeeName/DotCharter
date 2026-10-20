using DotCharter.Services.Library.Prefetch;

namespace DotCharter.Tests.Services.Parsing.Psarc.Prefetch;

/// <summary>
///     #425 convert-on-select: the debounce, the one-at-a-time rule and the
///     cancellation, driven by a hand clock. Continuations run inline when the
///     test releases a delay, so every "has not started" check is made after the
///     runner has really reached its wait — never after a sleep.
/// </summary>
public sealed class SettledSelectionRunnerTests
{
    private static readonly TimeSpan Settle = TimeSpan.FromMilliseconds(400);

    [Fact]
    public async Task Scrolling_through_items_runs_only_the_one_the_selection_settles_on()
    {
        var rig = new Rig();

        _ = rig.Runner.Select("a");
        _ = rig.Runner.Select("b");
        var last = rig.Runner.Select("c");

        Assert.Equal(3, rig.Delays.Count);
        Assert.All(rig.Delays, d => Assert.Equal(Settle, d.Duration));
        Assert.True(rig.Delays[0].Task.IsCanceled);
        Assert.True(rig.Delays[1].Task.IsCanceled);
        Assert.Empty(rig.Started);

        rig.Delays[2].Release();
        rig.FinishWork("c");
        await last;

        Assert.Equal(["c"], rig.Started);
    }

    [Fact]
    public async Task A_new_selection_cancels_the_running_work_and_waits_for_it_to_unwind()
    {
        var rig = new Rig();

        var first = rig.Runner.Select("a");
        rig.Delays[0].Release();
        Assert.Equal(["a"], rig.Started);
        Assert.False(rig.Tokens["a"].IsCancellationRequested);

        var second = rig.Runner.Select("b");
        Assert.True(rig.Tokens["a"].IsCancellationRequested);

        // b has settled, but a is still unwinding (past its point of no return):
        // b must not start beside it.
        rig.Delays[1].Release();
        Assert.Equal(["a"], rig.Started);

        rig.FinishWork("a");
        await first;
        Assert.Equal(["a", "b"], rig.Started);

        rig.FinishWork("b");
        await second;
        Assert.Empty(rig.Faults);
    }

    [Fact]
    public async Task Selecting_nothing_cancels_the_pending_item()
    {
        var rig = new Rig();

        _ = rig.Runner.Select("a");
        await rig.Runner.Select(null);

        Assert.True(rig.Delays[0].Task.IsCanceled);
        Assert.Empty(rig.Started);
    }

    [Fact]
    public async Task A_faulting_run_is_reported_and_the_next_selection_still_runs()
    {
        var rig = new Rig();

        var first = rig.Runner.Select("a");
        rig.Delays[0].Release();
        rig.FailWork("a", new IOException("locked"));
        await first;

        var second = rig.Runner.Select("b");
        rig.Delays[1].Release();
        rig.FinishWork("b");
        await second;

        Assert.Equal(["a", "b"], rig.Started);
        var fault = Assert.Single(rig.Faults);
        Assert.Equal("a", fault.Item);
        Assert.IsType<IOException>(fault.Error);
    }

    [Fact]
    public async Task Dispose_cancels_the_pending_item_and_later_selections_run_nothing()
    {
        var rig = new Rig();

        var pending = rig.Runner.Select("a");
        rig.Runner.Dispose();
        await pending;
        await rig.Runner.Select("b");

        Assert.True(rig.Delays[0].Task.IsCanceled);
        Assert.Single(rig.Delays);
        Assert.Empty(rig.Started);
    }

    private sealed class Rig
    {
        private readonly Dictionary<string, TaskCompletionSource> _work = [];

        public Rig() =>
            Runner = new SettledSelectionRunner<string>(Settle, Delay, Work,
                (item, error) => Faults.Add((item, error)));

        public SettledSelectionRunner<string> Runner { get; }
        public List<ManualDelay> Delays { get; } = [];
        public List<string> Started { get; } = [];
        public Dictionary<string, CancellationToken> Tokens { get; } = [];
        public List<(string Item, Exception Error)> Faults { get; } = [];

        public void FinishWork(string item) => _work[item].SetResult();

        public void FailWork(string item, Exception error) => _work[item].SetException(error);

        private Task Delay(TimeSpan duration, CancellationToken ct)
        {
            var delay = new ManualDelay(duration, ct);
            Delays.Add(delay);
            return delay.Task;
        }

        private Task Work(string item, CancellationToken ct)
        {
            Started.Add(item);
            Tokens[item] = ct;
            var done = new TaskCompletionSource();
            _work[item] = done;
            return done.Task;
        }
    }

    private sealed class ManualDelay
    {
        private readonly TaskCompletionSource _tcs = new();

        public ManualDelay(TimeSpan duration, CancellationToken ct)
        {
            Duration = duration;
            ct.Register(() => _tcs.TrySetCanceled(ct));
        }

        public TimeSpan Duration { get; }
        public Task Task => _tcs.Task;

        public void Release() => _tcs.SetResult();
    }
}
