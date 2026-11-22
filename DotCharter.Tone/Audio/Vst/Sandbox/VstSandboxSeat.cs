using JuceSharp;

namespace DotCharter.Tone.Audio.Vst.Sandbox;

/// <summary>Owns one seat's child, live slots, desired state and independent recovery.</summary>
internal sealed class VstSandboxSeat : IDisposable
{
    private readonly object _gate;
    private readonly Func<IJuceAudioEngine?> _engine;
    private readonly Func<IVstSandboxHost> _createHost;
    private readonly IAppLogger? _logger;
    private bool _bridgeArmed;

    public int Player { get; }
    public IVstSandboxHost? Host { get; private set; }
    public VstSandboxChainState Desired { get; } = new();
    public VstSandboxLoadRejections RejectedLoads { get; } = new();
    public Dictionary<Guid, (string Path, bool Bypass, int Player)> Slots { get; } = [];
    public volatile bool InstrumentLoaded;
    public bool GaveUp { get; set; }
    public bool IsReplaying { get; set; }
    public VstSandboxRecovery Recovery { get; set; } = null!;
    public event Action<string>? EditorClosed;

    public VstSandboxSeat(int player, object gate, Func<IJuceAudioEngine?> engine,
        Func<IVstSandboxHost> createHost, IAppLogger? logger)
    {
        Player = player;
        _gate = gate;
        _engine = engine;
        _createHost = createHost;
        _logger = logger;
    }

    /// <summary>Starts only this seat's child and arms its own uniquely named bridge.</summary>
    public async Task<IVstSandboxHost> EnsureAsync(IJuceAudioEngine engine)
    {
        lock (_gate)
        {
            if (_bridgeArmed && Host is { IsRunning: true }) return Host;
            if (GaveUp)
            {
                GaveUp = false;
                Recovery.ResetBudget();
            }
        }

        if (Host != null)
        {
            Recovery.CancelPendingRestart();
            await ShutdownAsync(disarmBridge: true).ConfigureAwait(false);
        }
        var host = _createHost();
        var sr = engine.SampleRate is var s and > 0 ? s : 44100;
        var block = engine.BufferFrames is var b and > 0 ? b : 512;
        host.ChildLog += line => _logger?.Debug(AppLogCategory.Audio, $"[sandbox seat {Player}] {line}");
        host.EditorClosedByUser += slot => EditorClosed?.Invoke(slot);
        host.Exited += requested => { if (!requested) Recovery.OnCrashed(host); };
        try
        {
            await host.StartAsync(sr, block).WaitAsync(VstSandboxCalls.StartBound).ConfigureAwait(false);
            if (!engine.SetRemoteBridge(Player, host.ShmName, host.RequestEventName, host.ResponseEventName, true))
                throw new InvalidOperationException($"Failed to arm seat {Player}'s native shared-memory bridge.");
        }
        catch
        {
            try { host.Dispose(); } catch { /* ignored */ }
            throw;
        }

        lock (_gate)
        {
            Host = host;
            _bridgeArmed = true;
            ClearLive();
            Recovery.ResetHeartbeat();
        }
        Recovery.EnsureHealthTimer();
        return host;
    }

    /// <summary>Disarms and stops this seat, preserving its desired state for recovery.</summary>
    public async Task ShutdownAsync(bool disarmBridge)
    {
        var host = TakeHostForShutdown(disarmBridge);
        if (host == null) return;
        await VstSandboxCalls.StopAsync(host).ConfigureAwait(false);
        try { host.Dispose(); } catch { /* ignored */ }
    }

    public async Task StopIfEmptyAsync()
    {
        bool empty;
        lock (_gate) empty = !IsReplaying && Host != null && Slots.Count == 0 && !InstrumentLoaded;
        if (empty) await ShutdownAsync(disarmBridge: true).ConfigureAwait(false);
    }

    /// <summary>Replays this seat's bridge onto a replacement engine without a pipe wait.</summary>
    public void Rearm(IJuceAudioEngine engine)
    {
        IVstSandboxHost? host;
        lock (_gate) host = Host;
        if (host is not { IsRunning: true }) return;
        lock (_gate) _bridgeArmed = false;
        try
        {
            if (!engine.SetRemoteBridge(Player, host.ShmName, host.RequestEventName, host.ResponseEventName, true))
                throw new InvalidOperationException("SetRemoteBridge returned false");
            lock (_gate) _bridgeArmed = true;
        }
        catch (Exception ex)
        {
            _logger?.Warning(AppLogCategory.Audio, $"Failed to re-arm sandbox bridge for seat {Player}", ex);
            try { host.Kill(); } catch { /* ignored */ }
            Recovery.OnCrashed(host);
        }
    }

    public uint ReadHeartbeat() => _engine() is { } engine
        && engine.TryGetRemoteBridgeStats(Player, out _, out _, out var beat) ? beat : 0u;

    public List<string> LiveSlotIds()
    {
        lock (_gate)
        {
            var ids = Slots.Keys.Select(id => id.ToString()).ToList();
            if (InstrumentLoaded) ids.Add(VstSandboxProtocol.InstrumentSlotId);
            return ids;
        }
    }

    public (List<Guid> Slots, bool Instrument) ClearLive()
    {
        lock (_gate)
        {
            var slots = Slots.Keys.ToList();
            var instrument = InstrumentLoaded;
            Slots.Clear();
            InstrumentLoaded = false;
            return (slots, instrument);
        }
    }

    public IVstSandboxHost? TakeHostForShutdown(bool disarmBridge)
    {
        Recovery.StopHealthTimer();
        var host = DetachHost();
        if (disarmBridge) Disarm();
        return host;
    }

    private IVstSandboxHost? DetachHost()
    {
        lock (_gate)
        {
            var host = Host;
            Host = null;
            _bridgeArmed = false;
            ClearLive();
            return host;
        }
    }

    private void Disarm()
    {
        lock (_gate) _bridgeArmed = false;
        try { _engine()?.SetRemoteBridge(Player, null, null, null, false); } catch { /* ignored */ }
    }

    public void Dispose() => VstSandboxShutdown.Dispose([this], new VstSandboxStopWait());
}
