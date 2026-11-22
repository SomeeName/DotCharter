using DotCharter.Tone.Diagnostics;

namespace DotCharter.Tests.Infrastructure;

/// <summary>
///     An <see cref="IUiDispatcher" /> that HOLDS every post until the test runs it,
///     in any order: two worker reads that finish out of order are staged by
///     running the newer one's post first.
/// </summary>
internal sealed class HeldPostsDispatcher : IUiDispatcher
{
    private readonly Lock _gate = new();
    private readonly List<Action> _held = [];

    public int HeldCount
    {
        get
        {
            lock (_gate) return _held.Count;
        }
    }

    public bool CheckAccess() => true;

    public void Post(Action action) => Hold(action);

    public void PostNormal(Action action) => Hold(action);

    public Task InvokeAsync(Action action)
    {
        action();
        return Task.CompletedTask;
    }

    public Task<T> InvokeAsync<T>(Func<T> func) => Task.FromResult(func());

    public Task<T> InvokeAsync<T>(Func<Task<T>> func) => func();

    /// <summary>Waits (yielding, 60 s deadlock cap, asserted) until at least <paramref name="count" /> posts are held.</summary>
    public async Task WaitForHeldAsync(int count)
    {
        var cap = DateTime.UtcNow.AddSeconds(60);
        while (HeldCount < count && DateTime.UtcNow < cap) await Task.Delay(5);
        Assert.True(HeldCount >= count, $"only {HeldCount} of {count} posts arrived in 60 s; the box was starved or the work never posted");
    }

    /// <summary>Runs the held posts newest first, then forgets them.</summary>
    public void RunNewestFirst()
    {
        Action[] posts;
        lock (_gate)
        {
            posts = [.. _held];
            _held.Clear();
        }

        for (var i = posts.Length - 1; i >= 0; i--) posts[i]();
    }

    private void Hold(Action action)
    {
        lock (_gate) _held.Add(action);
    }
}
