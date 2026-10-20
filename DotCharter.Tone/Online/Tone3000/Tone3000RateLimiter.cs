namespace DotCharter.Tone.Online.Tone3000;

/// <summary>
///     Pure client-side sliding-window throttle for the TONE3000 API (server
///     limit: 100 requests/min). The client asks <see cref="MsUntilNextPermit"/>
///     before each request and <see cref="Record"/>s afterwards; time is always
///     injected (monotonic ms) so the policy unit-tests without clocks.
///     Defaults keep headroom below the server limit so a parallel browser
///     session never trips a 429.
/// </summary>
public sealed class Tone3000RateLimiter
{
    private readonly int _maxRequests;
    private readonly long _windowMs;
    private readonly Queue<long> _stamps = new();
    private readonly object _gate = new();

    public Tone3000RateLimiter(int maxRequests = 80, long windowMs = 60_000)
    {
        _maxRequests = Math.Max(1, maxRequests);
        _windowMs = Math.Max(1, windowMs);
    }

    /// <summary>Milliseconds the caller must wait before the next request is
    /// within budget; 0 when it may go now.</summary>
    public long MsUntilNextPermit(long nowMs)
    {
        lock (_gate)
        {
            Evict(nowMs);
            if (_stamps.Count < _maxRequests) return 0;
            return Math.Max(0, _stamps.Peek() + _windowMs - nowMs);
        }
    }

    /// <summary>Records one issued request at <paramref name="nowMs"/>.</summary>
    public void Record(long nowMs)
    {
        lock (_gate)
        {
            Evict(nowMs);
            _stamps.Enqueue(nowMs);
        }
    }

    private void Evict(long nowMs)
    {
        while (_stamps.Count > 0 && nowMs - _stamps.Peek() >= _windowMs)
            _stamps.Dequeue();
    }
}
