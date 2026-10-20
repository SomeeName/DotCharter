namespace DotCharter.Tone.Audio.Vst.Sandbox;

/// <summary>
/// What the playback service needs from a sandbox host to talk to it. The real
/// one is <see cref="VstSandboxHost"/>; tests drive <see cref="VstSandboxCalls"/>
/// with a fake whose replies never arrive.
/// </summary>
internal interface IVstSandboxChannel
{
    bool IsRunning { get; }
    int? ChildProcessId { get; }
    Task<VstSandboxMessage> SendAsync(VstSandboxRequest request, TimeSpan? timeout = null);
    Task StopAsync(TimeSpan? gracePeriod = null);
    void Kill();
}

/// <summary>
/// Every round trip the playback service makes to the sandbox child goes
/// through here, so each one is AWAITED and BOUNDED twice: the host's own reply
/// timeout, and an outer <see cref="Task.WaitAsync(TimeSpan)"/> that also covers
/// the parts of <see cref="VstSandboxHost.SendAsync"/> the reply timeout does
/// not (the send-gate wait and the pipe write, which a child that stopped
/// reading its pipe can stall). No caller is ever blocked on a pipe reply —
/// the service must not <c>.GetAwaiter().GetResult()</c> each one, which parks a
/// pool thread for up to 180 s per plugin load.
/// <para>
/// <see cref="StopForDispose"/> is the ONE synchronous wait, and it is bounded.
/// <c>SandboxBlockingWaitLintTests</c> forbids any other.
/// </para>
/// </summary>
internal static class VstSandboxCalls
{
    /// <summary>Added to a request's own reply timeout to form the outer bound.</summary>
    internal static readonly TimeSpan ReplySlack = TimeSpan.FromSeconds(2);

    /// <summary>Spawn + pipe handshake: the host bounds it at 15 s connect + 10 s ping.</summary>
    internal static readonly TimeSpan StartBound = TimeSpan.FromSeconds(30);

    /// <summary>Graceful stop: the host bounds it at 3 s quit + its grace period, then kills.</summary>
    internal static readonly TimeSpan StopSlack = TimeSpan.FromSeconds(5);

    /// <summary>
    /// Sends <paramref name="request"/> and returns the reply, or <c>null</c>
    /// after reporting the failure (timeout, dead pipe, child exit) to
    /// <paramref name="onFailure"/>. Never throws.
    /// </summary>
    public static async Task<VstSandboxMessage?> TrySendAsync(
        IVstSandboxChannel host, VstSandboxRequest request, TimeSpan timeout, Action<Exception> onFailure)
    {
        Task<VstSandboxMessage>? send = null;
        try
        {
            send = host.SendAsync(request, timeout);
            return await send.WaitAsync(timeout + ReplySlack).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            ObserveLate(send);
            onFailure(ex);
            return null;
        }
    }

    /// <summary>
    /// Graceful stop with a hard outer bound: kills the child if the stop
    /// itself overruns. Never throws.
    /// </summary>
    public static async Task StopAsync(IVstSandboxChannel host, TimeSpan? gracePeriod = null)
    {
        Task? stop = null;
        try
        {
            stop = host.StopAsync(gracePeriod);
            await stop.WaitAsync((gracePeriod ?? TimeSpan.FromSeconds(5)) + StopSlack).ConfigureAwait(false);
        }
        catch
        {
            ObserveLate(stop);
            try { host.Kill(); } catch { /* already gone */ }
        }
    }

    /// <summary>
    /// The Dispose path's stop. <c>JuceAudioPlaybackService.Dispose</c> is
    /// <see cref="IDisposable.Dispose"/> — synchronous — and it must not return
    /// with the child still attached to a bridge whose engine it is about to
    /// destroy, so this is the one place that WAITS. It is safe to wait on any
    /// thread, the UI thread included: every await under
    /// <see cref="VstSandboxHost.StopAsync"/> is <c>ConfigureAwait(false)</c>, so
    /// nothing needs the caller's context to finish. It is bounded at
    /// <paramref name="bound"/>; past that the child is killed.
    /// Returns true when the stop finished inside the bound.
    /// </summary>
    public static bool StopForDispose(IVstSandboxChannel host, TimeSpan gracePeriod, TimeSpan bound)
    {
        Task stop;
        try { stop = host.StopAsync(gracePeriod); }
        catch
        {
            try { host.Kill(); } catch { /* already gone */ }
            return false;
        }

        bool finished;
        try { finished = stop.Wait(bound); }
        catch (AggregateException) { finished = true; } // faulted = over; StopAsync already killed
        if (!finished)
        {
            ObserveLate(stop);
            try { host.Kill(); } catch { /* already gone */ }
        }
        return finished;
    }

    /// <summary>A send/stop we stopped waiting for may still fault later; observe it.</summary>
    private static void ObserveLate(Task? task)
    {
        if (task is null || task.IsCompleted && !task.IsFaulted) return;
        _ = task.ContinueWith(static t => _ = t.Exception, CancellationToken.None,
            TaskContinuationOptions.OnlyOnFaulted | TaskContinuationOptions.ExecuteSynchronously,
            TaskScheduler.Default);
    }
}
