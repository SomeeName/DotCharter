namespace DotCharter.Tests.Infrastructure;

/// <summary>
///     A clock that only moves when the test calls <see cref="Advance" />. Its timers fire
///     when the advance reaches their due time, so a test decides exactly when a delay or a
///     paced tick elapses; nothing depends on wall-clock time.
/// </summary>
public sealed class StepTimeProvider : TimeProvider
{
    private readonly List<StepTimer> _timers = [];
    private long _elapsedTicks;

    /// <summary>Timers created and neither fired nor disposed.</summary>
    public int PendingTimers
    {
        get
        {
            lock (_timers) return _timers.Count(t => !t.Done);
        }
    }

    public override long TimestampFrequency => TimeSpan.TicksPerSecond;

    public override long GetTimestamp() => Interlocked.Read(ref _elapsedTicks);

    public override ITimer CreateTimer(TimerCallback callback, object? state, TimeSpan dueTime, TimeSpan period)
    {
        var timer = new StepTimer(callback, state, Interlocked.Read(ref _elapsedTicks) + dueTime.Ticks);
        lock (_timers) _timers.Add(timer);
        return timer;
    }

    public void Advance(TimeSpan by)
    {
        StepTimer[] due;
        lock (_timers)
        {
            var now = Interlocked.Add(ref _elapsedTicks, by.Ticks);
            due = [.. _timers.Where(t => !t.Done && t.DueTicks <= now)];
            foreach (var timer in due) timer.MarkDone();
        }

        foreach (var timer in due) timer.Fire();
    }

    private sealed class StepTimer(TimerCallback callback, object? state, long dueTicks) : ITimer
    {
        private volatile bool _done;

        public long DueTicks { get; } = dueTicks;
        public bool Done => _done;

        public void MarkDone() => _done = true;

        public void Fire() => callback(state);

        public bool Change(TimeSpan dueTime, TimeSpan period) => false;

        public void Dispose() => _done = true;

        public ValueTask DisposeAsync()
        {
            _done = true;
            return ValueTask.CompletedTask;
        }
    }
}
