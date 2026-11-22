using System.Diagnostics;

namespace DotCharter.Tests.Infrastructure;

/// <summary>
///     One call on a thread of its own, so a test can SEE whether the call is
///     blocked or has returned (#607).
///     <para>
///         "X waits for Y" tests kept releasing Y after starting X on the pool:
///         with the wait removed from the product, X had usually not run yet, Y
///         was released first, and the order the test asserts came out right
///         anyway. <see cref="WaitUntilParkedOrReturnedAsync" /> closes that gap
///         on a STATE: the call's thread is parked in a managed wait (a lock, a
///         join, a wait handle: <see cref="System.Threading.ThreadState.WaitSleepJoin" />), or the
///         call has returned. A product that no longer waits returns, and the test
///         fails there, before it releases anything.
///     </para>
/// </summary>
internal sealed class ParkedCall
{
    private readonly Thread _thread;
    private readonly TaskCompletionSource _returned = new(TaskCreationOptions.RunContinuationsAsynchronously);

    private ParkedCall(Action call)
    {
        _thread = new Thread(() =>
        {
            try
            {
                call();
                _returned.SetResult();
            }
            catch (Exception ex)
            {
                _returned.SetException(ex);
            }
        })
        { IsBackground = true, Name = nameof(ParkedCall) };
    }

    /// <summary>Completes when the call returns; faults with what it threw.</summary>
    public Task Returned => _returned.Task;

    public static ParkedCall Start(Action call)
    {
        var parked = new ParkedCall(call);
        parked._thread.Start();
        return parked;
    }

    /// <summary>
    ///     True once the call is parked in a wait, false once it has returned.
    ///     Fails when it does neither within <paramref name="cap" /> (a deadlock
    ///     detector): the box was starved, or the call is spinning.
    ///     <paramref name="what" /> names the call in that failure, e.g. "Stop()".
    /// </summary>
    public async Task<bool> WaitUntilParkedOrReturnedAsync(string what, TimeSpan cap, CancellationToken ct)
    {
        var clock = Stopwatch.StartNew();
        while (true)
        {
            if (_returned.Task.IsCompleted) return false;
            if (_thread.ThreadState.HasFlag(System.Threading.ThreadState.WaitSleepJoin)) return true;
            Assert.True(clock.Elapsed < cap,
                $"{what} neither parked in a wait nor returned within {cap.TotalSeconds:0} s: nothing can be said about it.");
            await Task.Delay(1, ct);
        }
    }
}
