namespace DotCharter.Tone.Audio.Tonelab;

/// <summary>Runs a callback once after a delay. The seam that keeps the debounce testable without a wall clock.</summary>
public interface ITonelabScheduler
{
    /// <summary>Schedules <paramref name="callback" />; disposing the handle before it fires cancels it.</summary>
    IDisposable Schedule(TimeSpan delay, Action callback);
}

/// <summary>The production scheduler: a one-shot thread-pool timer, so the callback never runs on the UI thread.</summary>
public sealed class TimerTonelabScheduler : ITonelabScheduler
{
    public static TimerTonelabScheduler Instance { get; } = new();

    public IDisposable Schedule(TimeSpan delay, Action callback) => new Timer(_ => callback(), null, delay, Timeout.InfiniteTimeSpan);
}

/// <summary>
///     Latest-wins debounce: every <see cref="Post" /> restarts the delay, and only the
///     last posted work runs, once, when the posts stop for <see cref="Delay" />. A burst of
///     knob moves therefore becomes one send.
/// </summary>
public sealed class TonelabDebouncer(ITonelabScheduler scheduler, TimeSpan delay) : IDisposable
{
    /// <summary>Inside the protocol's 30–50 ms window.</summary>
    public static readonly TimeSpan DefaultDelay = TimeSpan.FromMilliseconds(40);

    private readonly object _sync = new();
    private IDisposable? _pending;
    private Action? _work;
    private int _generation;

    public TimeSpan Delay { get; } = delay;

    public bool IsPending
    {
        get
        {
            lock (_sync) return _work is not null;
        }
    }

    public void Post(Action work)
    {
        lock (_sync)
        {
            _pending?.Dispose();
            _work = work;
            var generation = ++_generation;
            _pending = scheduler.Schedule(Delay, () => Fire(generation));
        }
    }

    /// <summary>Drops whatever is waiting; true when something was.</summary>
    public bool Cancel()
    {
        lock (_sync)
        {
            _generation++;
            _pending?.Dispose();
            _pending = null;
            var had = _work is not null;
            _work = null;
            return had;
        }
    }

    public void Dispose() => Cancel();

    private void Fire(int generation)
    {
        Action? work;
        lock (_sync)
        {
            if (generation != _generation) return;
            work = _work;
            _work = null;
            _pending?.Dispose();
            _pending = null;
        }

        work?.Invoke();
    }
}
