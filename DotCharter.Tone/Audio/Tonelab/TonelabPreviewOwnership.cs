using System.Runtime.CompilerServices;

namespace DotCharter.Tone.Audio.Tonelab;

/// <summary>One game's preview owners and restore point, ordered with every preview write.</summary>
internal sealed class TonelabPreviewOwnership
{
    private static readonly ConditionalWeakTable<ITonelabSession, Lazy<TonelabPreviewOwnership>> Sessions = [];
    private readonly ITonelabSession _session;
    private readonly object _sync = new();
    private readonly SemaphoreSlim _operations = new(1, 1);
    private readonly HashSet<object> _owners = new(ReferenceEqualityComparer.Instance);
    private object? _current;
    private bool _restore;
    private long _epoch;
    private long _revision;

    private TonelabPreviewOwnership(ITonelabSession session)
    {
        _session = session;
        session.StateChanged += OnStateChanged;
    }

    public static TonelabPreviewOwnership For(ITonelabSession session) => Sessions.GetValue(session, static s => new Lazy<TonelabPreviewOwnership>(() => new(s))).Value;

    public static long RevisionFor(ITonelabSession session)
    {
        var state = For(session);
        lock (state._sync) return state._revision;
    }

    public static ITonelabAudioLease? TryLease(ITonelabSession session, Func<Action, ITonelabAudioLease> create)
    {
        var state = For(session);
        lock (state._sync)
        {
            if (state._owners.Count > 0 || !state._operations.Wait(0)) return null;
            try
            {
                return create(() => state._operations.Release());
            }
            catch
            {
                state._operations.Release();
                throw;
            }
        }
    }

    /// <summary>Reserves ownership before waiting for the session's I/O turn, including a pending first reply.</summary>
    public async Task<TonelabResult?> ApplyAsync(object owner, Func<bool> canSend, Func<bool, Task<TonelabResult?>> send, IAppLogger logger)
    {
        long epoch;
        lock (_sync)
        {
            if (_session.State != TonelabSessionState.Ready) return TonelabResult.NotConnected("no ready preview session");
            epoch = _epoch;
            _owners.Add(owner);
            _revision++;
        }

        await _operations.WaitAsync().ConfigureAwait(false);
        try
        {
            bool current;
            lock (_sync)
            {
                if (!IsCurrent(epoch)) return TonelabResult.NotConnected("preview session changed");
                current = ReferenceEquals(_current, owner);
            }

            // Stop has reserved this owner's local release turn; skip its canceled queued write.
            if (!canSend()) return null;
            TonelabResult? result;
            try
            {
                result = await send(current).ConfigureAwait(false);
            }
            catch
            {
                await ReleaseCoreAsync(owner, epoch).ConfigureAwait(false);
                throw;
            }

            lock (_sync)
            {
                if (!IsCurrent(epoch)) return TonelabResult.NotConnected("preview session changed");
                if (result is null) return null;
                if (result.IsOk)
                {
                    _restore = true;
                    _current = owner;
                }
                else
                {
                    _current = null;
                    // A timeout can follow a command the game accepted but whose reply was lost.
                    if (result.Outcome is TonelabOutcome.Timeout or TonelabOutcome.Malformed or TonelabOutcome.NotConnected) _restore = true;
                }
            }

            if (result.IsOk)
            {
                _session.RouteAudio(owner, true);
                lock (_sync)
                {
                    if (IsCurrent(epoch)) return result;
                }

                _session.RouteAudio(owner, false);
                return TonelabResult.NotConnected("preview session changed");
            }

            var restored = await ReleaseCoreAsync(owner, epoch).ConfigureAwait(false);
            if (restored is { IsOk: false })
                logger.Warning(AppLogCategory.Audio, $"tonelab: preview failure restore failed ({restored.Outcome} {restored.Detail}).");
            return result;
        }
        finally
        {
            _operations.Release();
        }
    }

    public async Task<TonelabResult?> ReleaseAsync(object owner)
    {
        long epoch;
        lock (_sync) epoch = _epoch;
        await _operations.WaitAsync().ConfigureAwait(false);
        try
        {
            return await ReleaseCoreAsync(owner, epoch).ConfigureAwait(false);
        }
        finally
        {
            _operations.Release();
        }
    }

    private async Task<TonelabResult?> ReleaseCoreAsync(object owner, long epoch)
    {
        bool restore;
        lock (_sync)
        {
            if (!IsCurrent(epoch) || !_owners.Remove(owner)) return null;
            if (ReferenceEquals(_current, owner)) _current = null;
            restore = _owners.Count == 0 && _restore;
            if (restore) _restore = false;
        }

        _session.RouteAudio(owner, false);
        return restore ? await _session.RestoreToneAsync().ConfigureAwait(false) : null;
    }

    /// <summary>Called only while holding the ownership lock; session state reads take no session lock.</summary>
    private bool IsCurrent(long epoch) => epoch == _epoch && _session.State == TonelabSessionState.Ready;

    private void OnStateChanged(object? sender, EventArgs e)
    {
        if (_session.State == TonelabSessionState.Ready) return;
        object[] owners;
        lock (_sync)
        {
            _epoch++;
            _revision++;
            owners = [.. _owners];
            _owners.Clear();
            _current = null;
            _restore = false;
        }

        foreach (var owner in owners) _session.RouteAudio(owner, false);
    }
}
