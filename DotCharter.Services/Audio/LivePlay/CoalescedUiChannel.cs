namespace DotCharter.Services.Audio.LivePlay;

/// <summary>
///     Single-producer coalescing channel for marshaling worker-thread state to
///     the UI thread: at most ONE posted callback is in flight no matter how fast
///     <see cref="Publish"/> is called, and a publish that lands while a payload
///     is still pending REPLACES it (via <c>merge</c>, so list-carrying payloads
///     can fold the unapplied items forward instead of dropping them). This is
///     the house pattern of <c>AudioPlaybackCoordinator</c>'s playhead publish,
///     generalized to a payload object.
///     <para>
///     The <c>post</c> delegate is the UI marshaller (in the app:
///     <c>Dispatcher.UIThread.Post</c>); injecting it keeps this class free of
///     dispatcher dependencies and unit-testable. <c>apply</c> runs on whatever
///     thread <c>post</c> schedules. Producer side is single-threaded by
///     contract (the detection worker); <c>merge</c> therefore never races
///     itself.
///     </para>
/// </summary>
public sealed class CoalescedUiChannel<T> where T : class
{
    private readonly Action<Action> _post;
    private readonly Action<T> _apply;
    private readonly Func<T, T, T>? _merge;
    private readonly Action _drain;
    private T? _pending;
    private int _queued;

    /// <param name="post">Schedules a callback onto the consumer thread.</param>
    /// <param name="apply">Applies a payload on the consumer thread.</param>
    /// <param name="merge">Folds a still-pending payload into its replacement:
    /// <c>(pendingOld, incoming) → payload to keep</c>. Null = plain replace.</param>
    public CoalescedUiChannel(Action<Action> post, Action<T> apply, Func<T, T, T>? merge = null)
    {
        _post = post;
        _apply = apply;
        _merge = merge;
        _drain = Drain; // cached: no per-publish delegate allocation
    }

    /// <summary>Publishes a payload (producer thread). Replaces any pending one
    /// (after <c>merge</c>) and posts a drain unless one is already queued.</summary>
    public void Publish(T payload)
    {
        var prev = Interlocked.Exchange(ref _pending, null);
        var next = prev == null || _merge == null ? payload : _merge(prev, payload);
        Interlocked.Exchange(ref _pending, next);
        // A drain racing between the two exchanges above sees null and no-ops;
        // the queued flag it reset re-arms here, so the payload is never lost.
        if (Interlocked.Exchange(ref _queued, 1) == 0) _post(_drain);
    }

    private void Drain()
    {
        Interlocked.Exchange(ref _queued, 0);
        var payload = Interlocked.Exchange(ref _pending, null);
        if (payload != null) _apply(payload);
    }
}
