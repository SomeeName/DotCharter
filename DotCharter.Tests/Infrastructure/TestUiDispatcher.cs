using DotCharter.Tone.Diagnostics;

namespace DotCharter.Tests.Infrastructure;

/// <summary>
///     An <see cref="IUiDispatcher" /> for a host with no UI loop: the calling
///     thread counts as the UI thread, posted and invoked work runs inline, and a
///     periodic tick is accepted but never fires — the same as a
///     <c>DispatcherTimer</c> nobody pumps. For services that need a dispatcher to
///     construct but whose tests drive their timers by hand.
/// </summary>
internal sealed class TestUiDispatcher : IUiDispatcher
{
    public bool CheckAccess() => true;
    public void Post(Action action) => action();
    public void PostNormal(Action action) => action();

    public Task InvokeAsync(Action action)
    {
        action();
        return Task.CompletedTask;
    }

    public Task<T> InvokeAsync<T>(Func<T> func) => Task.FromResult(func());
    public Task<T> InvokeAsync<T>(Func<Task<T>> func) => func();

    public IDisposable SchedulePeriodic(TimeSpan period, Action tick) => new Inert();

    private sealed class Inert : IDisposable
    {
        public void Dispose() { }
    }
}
