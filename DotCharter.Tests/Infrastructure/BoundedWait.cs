namespace DotCharter.Tests.Infrastructure;

/// <summary>
///     A wait on another thread's work that cannot hang and cannot expire in
///     silence (#607): it ends when the work signals, or fails after
///     <see cref="DeadlockCap" /> with a message that names what was waited for.
///     The cap is a deadlock detector, never a budget: nothing here asserts how
///     long the work took.
/// </summary>
internal static class BoundedWait
{
    public static readonly TimeSpan DeadlockCap = TimeSpan.FromSeconds(60);

    /// <summary>Awaits <paramref name="task" />, rethrowing what it threw.</summary>
    public static async Task ForAsync(Task task, string what, CancellationToken ct)
    {
        using var stopDelay = CancellationTokenSource.CreateLinkedTokenSource(ct);
        var winner = await Task.WhenAny(task, Task.Delay(DeadlockCap, stopDelay.Token));
        stopDelay.Cancel();
        if (!ReferenceEquals(winner, task)) ct.ThrowIfCancellationRequested();
        Assert.True(ReferenceEquals(winner, task), Expired(what));
        await task;
    }

    /// <summary>Awaits <paramref name="task" /> and returns its result.</summary>
    public static async Task<T> ForAsync<T>(Task<T> task, string what, CancellationToken ct)
    {
        await ForAsync((Task)task, what, ct);
        return await task;
    }

    /// <summary>Blocks the calling thread until <paramref name="signal" /> is set.
    /// For a thread that is not the test's own flow (a callback, a hook).</summary>
    public static void For(ManualResetEventSlim signal, string what) =>
        Assert.True(signal.Wait(DeadlockCap), Expired(what));

    private static string Expired(string what) =>
        $"Waited {DeadlockCap.TotalSeconds:0} s for {what}: it never happened (a deadlock, or a starved box).";
}
