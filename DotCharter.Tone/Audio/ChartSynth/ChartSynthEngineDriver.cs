// ChartSynthEngineDriver: the chart synth's REQUEST (enabled, voice, gain, the last upload, the instrument route, both instruments), its serial push chain, and the ONE engine write a live change and the rebuild replay share (a collaborator of JuceAudioPlaybackService).
using JuceSharp;

namespace DotCharter.Tone.Audio.ChartSynth;

/// <summary>
///     The engine side of <see cref="IChartSynthEngine" />. <c>JuceAudioPlaybackService</c>
///     implements the seam by forwarding here, and calls <see cref="ReplayTo" /> from
///     its per-seat attach so a rebuilt engine gets the request back.
///     <para>
///         It shares no state with the service: the engine comes in as a getter (one
///         volatile read per call, the same read the partial made of <c>_engine</c>),
///         and "may the instrument go to the rig" as a predicate over the sandbox
///         flag. Its two locks are its own and are never held across an engine call.
///     </para>
/// </summary>
internal sealed class ChartSynthEngineDriver
{
    // The chart synth plays into SEAT 0 — the seat the editor and a solo Play
    // Mode run use. Every field below is the REQUEST, kept so a rebuilt engine
    // (device change, NAM 48 kHz request, watchdog recovery) gets it back
    // through the same PushTo a live change uses.
    private const int ChartSynthSeat = 0;

    private readonly IAppLogger? _logger;
    private readonly Func<JuceAudioEngine?> _engine;
    private readonly Func<bool> _canRouteInstrumentToRig;

    private readonly object _chartSynthGate = new();
    private bool _chartSynthEnabled;
    private ChartSynthVoice _chartSynthVoice = ChartSynthVoiceSelection.Default;
    private float _chartSynthGain = 1f;
    private bool _routeInstrumentToRig;
    private ChartNoteUpload? _chartUpload;
    private ChartSynthInstrument _guitarInstrument = ChartSynthInstrumentSelection.DefaultGuitar;
    private ChartSynthInstrument _bassInstrument = ChartSynthInstrumentSelection.DefaultBass;

    // Live changes are pushed to the engine OFF the caller's thread: turning
    // the synth on or changing its instrument LOADS A SAMPLE PACK natively (55 ms
    // for a Strat position, ~140 ms for the Gretsch set, measured), and the
    // callers are a UI toggle and the transport tick. One serial chain, coalesced:
    // every push writes the WHOLE current request, so a push that has not started
    // yet covers any number of later changes.
    private readonly object _chartPushGate = new();
    private Task _chartPushTail = Task.CompletedTask;
    private bool _chartPushQueued;

    // A model's info is fixed for the run (the native table, and the packs
    // installed beside the DLL at startup), and reading it stats the pack files:
    // read once per model, then from here — TrySetInstrument runs on the UI thread.
    private readonly System.Collections.Concurrent.ConcurrentDictionary<ChartSynthModel, ChartSynthModelInfo?> _chartModelInfo = new();

    public ChartSynthEngineDriver(IAppLogger? logger, Func<JuceAudioEngine?> engine, Func<bool> canRouteInstrumentToRig)
    {
        _logger = logger;
        _engine = engine;
        _canRouteInstrumentToRig = canRouteInstrumentToRig;
    }

    public bool IsEnabled
    {
        get { lock (_chartSynthGate) return _chartSynthEnabled; }
    }

    public void SetEnabled(bool enabled)
    {
        lock (_chartSynthGate) _chartSynthEnabled = enabled;
        PushToCurrentEngine("enabled changed");
    }

    public ChartSynthVoice Voice
    {
        get { lock (_chartSynthGate) return _chartSynthVoice; }
    }

    public static bool IsVoiceAvailable(ChartSynthVoice voice)
    {
        try { return JuceAudioEngine.IsChartVoiceAvailable((JuceChartVoice)voice); }
        catch (Exception ex) when (ex is DllNotFoundException or EntryPointNotFoundException or BadImageFormatException)
        {
            return false;
        }
    }

    public bool TrySetVoice(ChartSynthVoice voice)
    {
        if (!IsVoiceAvailable(voice)) return false;
        lock (_chartSynthGate) _chartSynthVoice = voice;
        PushToCurrentEngine("voice changed");
        return true;
    }

    public ChartSynthInstrument GuitarInstrument
    {
        get { lock (_chartSynthGate) return _guitarInstrument; }
    }

    public ChartSynthInstrument BassInstrument
    {
        get { lock (_chartSynthGate) return _bassInstrument; }
    }

    public bool TrySetInstrument(ChartSynthInstrument instrument)
    {
        var info = GetModelInfo(instrument.Model);
        if (info == null || !info.Pickups.Contains(instrument.Pickup)) return false;
        var normalized = ChartSynthInstrumentSelection.Normalize(instrument, instrument.IsBass, info);
        lock (_chartSynthGate)
        {
            if (normalized.IsBass) _bassInstrument = normalized;
            else _guitarInstrument = normalized;
        }
        PushToCurrentEngine("instrument changed");
        return true;
    }

    public ChartSynthModelInfo? GetModelInfo(ChartSynthModel model)
        => _chartModelInfo.GetOrAdd(model, ReadModelInfo);

    private static ChartSynthModelInfo? ReadModelInfo(ChartSynthModel model)
    {
        try
        {
            var i = JuceAudioEngine.GetChartModelInfo((JuceChartInstrumentModel)model);
            if (i == null) return null;
            return new ChartSynthModelInfo(model, i.IsBass, i.Pickups.Select(p => (ChartSynthPickup)p).ToArray(),
                (ChartSynthPickup)i.DefaultPickup, i.HasCoilSplit, i.IsInstalled,
                i.RecordedPickups.Select(p => (ChartSynthPickup)p).ToArray(),
                i.PartlyRecordedPickups.Select(p => (ChartSynthPickup)p).ToArray());
        }
        catch (Exception ex) when (ex is DllNotFoundException or EntryPointNotFoundException or BadImageFormatException)
        {
            return null;
        }
    }

    public float Gain
    {
        get { lock (_chartSynthGate) return _chartSynthGain; }
        set
        {
            var g = float.IsFinite(value) ? Math.Clamp(value, 0f, 4f) : 1f;
            lock (_chartSynthGate) _chartSynthGain = g;
            PushToCurrentEngine("gain changed");
        }
    }

    public long? UploadedFingerprint
    {
        get { lock (_chartSynthGate) return _chartUpload?.Fingerprint; }
    }

    public bool UploadNotes(ChartNoteUpload upload)
    {
        ArgumentNullException.ThrowIfNull(upload);
        var engine = _engine();
        if (engine == null) return false;
        ChartSynthVoice before, after;
        lock (_chartSynthGate)
        {
            before = ChartSynthVoiceSelection.Effective(_chartSynthVoice, _chartUpload);
            after = ChartSynthVoiceSelection.Effective(_chartSynthVoice, upload);
        }
        try
        {
            // A bass chart after a guitar chart (or the first chart of either)
            // swaps the built-in instrument before its notes arrive. This runs on
            // the transport tick (a pool thread), never the UI thread; the pack
            // loads here.
            if (after != before && !engine.TrySetChartVoice(ChartSynthSeat, (JuceChartVoice)after))
                _logger?.Warning(AppLogCategory.Audio, $"Chart synth: the engine has no {after} voice.");
            engine.SetChartNotes(ChartSynthSeat, upload.Notes, upload.Bends);
        }
        catch (Exception ex) when (ex is ObjectDisposedException or JuceAudioException or ArgumentException)
        {
            _logger?.Warning(AppLogCategory.Audio, $"Chart synth: the engine rejected {upload.Notes.Length} notes: {ex.Message}");
            return false;
        }
        // Recorded only once the engine took it, like a re-amp take: an upload the
        // engine refused must not come back on the next rebuild as if it had been live.
        lock (_chartSynthGate) _chartUpload = upload;
        return true;
    }

    public bool RouteInstrumentToRig
    {
        get { lock (_chartSynthGate) return _routeInstrumentToRig && _canRouteInstrumentToRig(); }
        set
        {
            lock (_chartSynthGate) _routeInstrumentToRig = value;
            PushToCurrentEngine("instrument route changed");
        }
    }

    public bool TryGetStatus(out ChartSynthStatus status)
    {
        status = default;
        var engine = _engine();
        if (engine == null) return false;
        try
        {
            if (!engine.TryGetChartSourceStatus(ChartSynthSeat, out var s)) return false;
            status = new ChartSynthStatus(s.IsEnabled, s.NoteCount, s.NotesStarted, s.ActiveVoices,
                s.IsClockBound, s.IsClockPlaying, s.IsVoiceLoaded, s.IsSampled, s.PackBytes,
                new ChartSynthInstrument((ChartSynthModel)s.Instrument.Model, (ChartSynthPickup)s.Instrument.Pickup, s.Instrument.CoilSplit));
            return true;
        }
        catch (ObjectDisposedException) { return false; }
    }

    /// <summary>Queues a push of the whole request onto the serial background
    /// chain (coalesced — see the field comment).</summary>
    private void PushToCurrentEngine(string reason)
    {
        lock (_chartPushGate)
        {
            if (_chartPushQueued) return;
            _chartPushQueued = true;
            _chartPushTail = _chartPushTail.ContinueWith(_ =>
            {
                lock (_chartPushGate) _chartPushQueued = false;
                var engine = _engine();
                if (engine != null) PushTo(engine, reason, includeNotes: false);
            }, CancellationToken.None, TaskContinuationOptions.None, TaskScheduler.Default);
        }
    }

    /// <summary>
    ///     Blocks until every queued chart-synth push has reached the engine (60 s
    ///     cap — a deadlock detector). For tests and for a caller that must read
    ///     the engine state its own change produced.
    /// </summary>
    public void WaitForPushes()
    {
        Task tail;
        lock (_chartPushGate) tail = _chartPushTail;
        tail.Wait(TimeSpan.FromSeconds(60));
    }

    /// <summary>
    ///     The ONE write of the chart synth's engine state, shared by every live
    ///     change and by the rebuild replay (<see cref="ReplayTo" />), so
    ///     the two can never drift into writing different surfaces. Each write is
    ///     guarded on its own: losing one setting costs that setting, never the
    ///     rebuild. The notes go only on a replay (<paramref name="includeNotes" />):
    ///     re-uploading them on a live change would choke and re-start every
    ///     sounding note instead of cross-fading.
    /// </summary>
    private void PushTo(JuceAudioEngine engine, string reason, bool includeNotes)
    {
        bool enabled, route;
        ChartSynthVoice voice;
        float gain;
        ChartNoteUpload? upload;
        ChartSynthInstrument guitar, bass;
        lock (_chartSynthGate)
        {
            enabled = _chartSynthEnabled;
            voice = ChartSynthVoiceSelection.Effective(_chartSynthVoice, _chartUpload);
            gain = _chartSynthGain;
            route = _routeInstrumentToRig && _canRouteInstrumentToRig();
            upload = _chartUpload;
            guitar = _guitarInstrument;
            bass = _bassInstrument;
        }

        // The instruments FIRST: an enable or a voice change below then builds the
        // voice once, with its pack, rather than building the default and swapping.
        Guard("guitar instrument", () => SetInstrument(guitar));
        Guard("bass instrument", () => SetInstrument(bass));
        Guard("voice", () =>
        {
            if (!engine.TrySetChartVoice(ChartSynthSeat, (JuceChartVoice)voice))
                _logger?.Warning(AppLogCategory.Audio, $"Chart synth: the engine has no {voice} voice.");
        });
        Guard("params", () => engine.SetChartSourceParams(ChartSynthSeat, gain, 0.0, JuceChartSourceMode.Mix));
        if (includeNotes && upload != null) Guard("notes", () => engine.SetChartNotes(ChartSynthSeat, upload.Notes, upload.Bends));
        Guard("enabled", () => engine.SetChartSourceEnabled(ChartSynthSeat, enabled));
        Guard("instrument route", () => engine.SetInstrumentRoute(route ? ChartSynthSeat : -1));
        return;

        void SetInstrument(ChartSynthInstrument i)
        {
            if (!engine.TrySetChartInstrument(ChartSynthSeat,
                    new JuceChartInstrument((JuceChartInstrumentModel)i.Model, (JuceChartPickup)i.Pickup, i.CoilSplit)))
            {
                _logger?.Warning(AppLogCategory.Audio, $"Chart synth: the engine refused the instrument {i}.");
            }
        }

        void Guard(string what, Action write)
        {
            try { write(); }
            catch (Exception ex) when (ex is ObjectDisposedException or JuceAudioException or ArgumentException)
            {
                _logger?.Warning(AppLogCategory.Audio, $"Chart synth: writing the {what} failed ({reason}): {ex.Message}");
            }
        }
    }

    /// <summary>
    ///     Called from <c>JuceAudioPlaybackService.AttachPerPlayerStateToEngine</c>: a
    ///     rebuilt engine starts with the chart source off and empty and its instruments
    ///     at the engine defaults. Nothing requested and nothing uploaded = nothing to
    ///     say, so a user who never touched the synth costs a rebuild no P/Invokes —
    ///     the instruments are written by the next push that needs a voice.
    /// </summary>
    public void ReplayTo(JuceAudioEngine engine)
    {
        lock (_chartSynthGate)
        {
            if (!_chartSynthEnabled && _chartUpload == null && !_routeInstrumentToRig
                && _chartSynthVoice == ChartSynthVoiceSelection.Default && _chartSynthGain == 1f)
            {
                return;
            }
        }

        PushTo(engine, "replayed after engine rebuild", includeNotes: true);
    }
}
