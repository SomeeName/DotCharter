// ExternalProcessInserts: the per-seat external process insert (ABI v47) — what is open, the one engine write a live open and the rebuild replay share, and the device-rate request a rate mismatch triggers (a collaborator of JuceAudioPlaybackService).
using JuceSharp;

namespace DotCharter.Tone.Audio.ExternalInsert;

/// <summary>
///     The managed half of the native external process insert: a seat's dry input
///     goes to another process through a named shared-memory ring and comes back
///     processed in place of the seat's rig. All audio moves natively; this only
///     opens, closes, reads counters and REPLAYS.
///     <para>
///         <b>Rebuild replay.</b> The insert is per-engine state. A reconfigure keeps
///         it (the native side bypasses itself while the device rate differs and
///         resumes when it matches), so <see cref="ReplayTo" /> re-opens only a seat
///         the engine reports as closed — i.e. after an engine REPLACE (device or
///         driver change, watchdog recovery).
///     </para>
///     <para>
///         <b>Rate.</b> The ring carries audio at the other process's rate and the
///         native side does not convert. A refusal for the rate asks the service for
///         that device rate (in place, never an engine teardown) and opens again; a
///         device that cannot give it leaves the open refused and
///         <see cref="LastOpenResult" /> says why.
///     </para>
///     <para>
///         It shares no state with the service: the engine comes in as a getter and
///         its one lock is its own, never held across an engine call.
///     </para>
/// </summary>
internal sealed class ExternalProcessInserts
{
    private sealed record Request(string MappingName, string? Magic, int SampleRate);

    private readonly IAppLogger? _logger;
    private readonly Func<IExternalInsertEngine?> _engine;
    private readonly Func<int, bool> _requestSampleRate;
    private readonly object _gate = new();
    private readonly Request?[] _requests = new Request?[Dsp.DspPlayers.MaxPlayers];
    private readonly ExternalProcessInsertOpenResult[] _last = new ExternalProcessInsertOpenResult[Dsp.DspPlayers.MaxPlayers];

    /// <param name="logger">Where opens, refusals and replays are reported.</param>
    /// <param name="engine">The current engine (null while none is up); one call per operation.</param>
    /// <param name="requestSampleRate">Asks for that device rate in place; true when the device now runs at it.
    /// Never called under the caller's engine lock (the replay defers it to the thread pool).</param>
    public ExternalProcessInserts(IAppLogger? logger, Func<IExternalInsertEngine?> engine, Func<int, bool> requestSampleRate)
    {
        _logger = logger;
        _engine = engine;
        _requestSampleRate = requestSampleRate;
        Array.Fill(_last, ExternalProcessInsertOpenResult.Failed);
    }

    /// <summary>Opens <paramref name="player" />'s insert on <paramref name="mappingName" />. When the device does
    /// not run at <paramref name="sampleRate" /> (the ring's), that rate is requested first and the open retried.
    /// Control thread; may reconfigure the device, so never from the UI thread's hot path.</summary>
    public ExternalProcessInsertOpenResult Open(int player, string mappingName, string? magic, int sampleRate)
    {
        ArgumentException.ThrowIfNullOrEmpty(mappingName);
        if (!Dsp.DspPlayers.IsValid(player)) return ExternalProcessInsertOpenResult.Failed;
        var result = OpenOn(_engine(), player, mappingName, magic);
        if (result == ExternalProcessInsertOpenResult.SampleRateMismatch && sampleRate > 0)
        {
            _logger?.Info(AppLogCategory.Audio,
                $"External process insert (seat {player + 1}): the ring runs at {sampleRate} Hz; requesting that device rate.");
            if (_requestSampleRate(sampleRate)) result = OpenOn(_engine(), player, mappingName, magic);
        }

        lock (_gate)
        {
            // Recorded only once the engine took it: a refused open must not come
            // back on the next rebuild as if it had been live.
            _requests[player] = result == ExternalProcessInsertOpenResult.Opened ? new Request(mappingName, magic, sampleRate) : null;
            _last[player] = result;
        }

        Report(player, mappingName, result, "opened");
        return result;
    }

    public void Close(int player)
    {
        if (!Dsp.DspPlayers.IsValid(player)) return;
        lock (_gate) _requests[player] = null;
        try { _engine()?.Close(player); }
        catch (Exception ex) when (ex is ObjectDisposedException or JuceAudioException)
        {
            _logger?.Warning(AppLogCategory.Audio, $"External process insert (seat {player + 1}): close failed: {ex.Message}");
        }
    }

    public bool IsOpen(int player)
    {
        if (!Dsp.DspPlayers.IsValid(player)) return false;
        lock (_gate) return _requests[player] != null;
    }

    /// <summary>What the last open (or replay) of the seat answered; <see cref="ExternalProcessInsertOpenResult.Failed" /> before any.</summary>
    public ExternalProcessInsertOpenResult LastOpenResult(int player)
    {
        if (!Dsp.DspPlayers.IsValid(player)) return ExternalProcessInsertOpenResult.Failed;
        lock (_gate) return _last[player];
    }

    /// <summary>The native counters while the seat's insert is open, else null.</summary>
    public ExternalProcessInsertStats? ReadStats(int player)
    {
        if (!IsOpen(player)) return null;
        try { return _engine() is { } e && e.TryGetStats(player, out var st) ? st : null; }
        catch (Exception ex) when (ex is ObjectDisposedException or JuceAudioException) { return null; }
    }

    /// <summary>Re-opens every recorded insert the engine does not have. Called from the per-seat attach of every
    /// engine build and reconfigure (under the service's engine lock): no device-rate request happens here, a rate
    /// refusal is deferred to the thread pool.</summary>
    public void ReplayTo(IExternalInsertEngine engine)
    {
        for (var player = 0; player < _requests.Length; player++)
        {
            Request? request;
            lock (_gate) request = _requests[player];
            if (request == null) continue;
            try
            {
                // A reconfigure keeps the native session: nothing to do.
                if (engine.TryGetStats(player, out var st) && st.State != ExternalProcessInsertState.Closed) continue;
                var result = engine.Open(player, request.MappingName, request.Magic);
                lock (_gate) _last[player] = result;
                Report(player, request.MappingName, result, "re-opened after the engine rebuild");
                if (result == ExternalProcessInsertOpenResult.SampleRateMismatch && request.SampleRate > 0)
                {
                    var seat = player;
                    _ = Task.Run(() => ReopenAtRate(seat, request));
                }
                else if (result != ExternalProcessInsertOpenResult.Opened)
                {
                    // The other process is gone or changed: forget it; its owner opens again.
                    lock (_gate) if (ReferenceEquals(_requests[player], request)) _requests[player] = null;
                }
            }
            catch (Exception ex) when (ex is ObjectDisposedException or JuceAudioException)
            {
                _logger?.Warning(AppLogCategory.Audio,
                    $"External process insert (seat {player + 1}): replay after the engine rebuild failed: {ex.Message}");
            }
        }
    }

    private void ReopenAtRate(int player, Request request)
    {
        try
        {
            // The request reconfigures the device, and that reconfigure's own replay
            // re-opens the seat; open here only if it did not.
            var rateOk = _requestSampleRate(request.SampleRate);
            lock (_gate) if (!ReferenceEquals(_requests[player], request)) return;
            var engine = _engine();
            if (engine == null) return;
            if (engine.TryGetStats(player, out var st) && st.State != ExternalProcessInsertState.Closed) return;
            var result = rateOk ? engine.Open(player, request.MappingName, request.Magic) : ExternalProcessInsertOpenResult.SampleRateMismatch;
            lock (_gate) _last[player] = result;
            Report(player, request.MappingName, result, "re-opened at the ring's rate");
        }
        catch (Exception ex) when (ex is ObjectDisposedException or JuceAudioException)
        {
            _logger?.Warning(AppLogCategory.Audio, $"External process insert (seat {player + 1}): re-open failed: {ex.Message}");
        }
    }

    private ExternalProcessInsertOpenResult OpenOn(IExternalInsertEngine? engine, int player, string mappingName, string? magic)
    {
        if (engine == null) return ExternalProcessInsertOpenResult.Failed;
        try { return engine.Open(player, mappingName, magic); }
        catch (Exception ex) when (ex is ObjectDisposedException or JuceAudioException)
        {
            _logger?.Warning(AppLogCategory.Audio, $"External process insert (seat {player + 1}): open failed: {ex.Message}");
            return ExternalProcessInsertOpenResult.Failed;
        }
    }

    private void Report(int player, string mappingName, ExternalProcessInsertOpenResult result, string what)
    {
        if (result == ExternalProcessInsertOpenResult.Opened)
            _logger?.Info(AppLogCategory.Audio, $"External process insert (seat {player + 1}) {what} on '{mappingName}'.");
        else
            _logger?.Warning(AppLogCategory.Audio, $"External process insert (seat {player + 1}) on '{mappingName}' refused: {result}.");
    }
}
