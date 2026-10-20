using Avalonia.Threading;
using DotCharter.Tone.Diagnostics;

namespace DotCharter.Tone.UI.Platform;

/// <summary>
/// Avalonia-backed implementation of <see cref="IUiDispatcher"/> so the
/// status-bar service (which lives in DotCharter.Services and must not
/// reference Avalonia) can marshal observable-collection mutations back
/// to the UI thread.
/// </summary>
public sealed class AvaloniaUiDispatcher : IUiDispatcher
{
    public bool CheckAccess() => Dispatcher.UIThread.CheckAccess();

    public void Post(Action action)
        => Dispatcher.UIThread.Post(action, DispatcherPriority.Background);

    public void PostNormal(Action action)
        => Dispatcher.UIThread.Post(action);

    public Task InvokeAsync(Action action)
        => Dispatcher.UIThread.InvokeAsync(action).GetTask();

    public Task<T> InvokeAsync<T>(Func<T> func)
        => Dispatcher.UIThread.InvokeAsync(func).GetTask();

    public Task<T> InvokeAsync<T>(Func<Task<T>> func)
        => Dispatcher.UIThread.InvokeAsync(func);

    /// <summary>A <c>DispatcherTimer</c>: the tick runs on the UI thread and is
    /// owned by the dispatcher, so it stops with it — no pool thread ever posts
    /// into a torn-down dispatcher (see <see cref="IUiDispatcher.SchedulePeriodic" />).</summary>
    public IDisposable SchedulePeriodic(TimeSpan period, Action tick)
    {
        var timer = CreatePeriodicTimer(period, tick);
        timer.Start();
        return new StopOnDispose(timer);
    }

    /// <summary>The timer <see cref="SchedulePeriodic" /> starts: BACKGROUND
    /// priority, below Render, so a periodic tick (the crash-recovery snapshot
    /// deep-clones the project in one) never goes ahead of a frame. Not started.</summary>
    internal static DispatcherTimer CreatePeriodicTimer(TimeSpan period, Action tick)
    {
        var timer = new DispatcherTimer(DispatcherPriority.Background) { Interval = period };
        timer.Tick += (_, _) => tick();
        return timer;
    }

    private sealed class StopOnDispose(DispatcherTimer timer) : IDisposable
    {
        public void Dispose() => timer.Stop();
    }
}
