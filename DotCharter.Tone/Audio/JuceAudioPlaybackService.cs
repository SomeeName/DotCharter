// Partial: the root — every shared engine/source field, the constructor, the position timer and its tick, and Dispose.
using DotCharter.Localization;
using DotCharter.Tone.Audio.Diagnostics;
using DotCharter.Tone.Audio.Midi;
using DotCharter.Tone.Diagnostics;
using JuceSharp;
using Timer = System.Timers.Timer;

namespace DotCharter.Tone.Audio;

/// <summary>
/// Real <see cref="IAudioPlaybackService"/> backed by the native JUCE engine
/// (<c>JuceSharp.Native</c>). Replaces <see cref="NullAudioPlaybackService"/>
/// once the native DLL is available. Owns one <see cref="JuceAudioEngine"/>
/// (the device + master mixer) and at most one active <see cref="JuceAudioSource"/>
/// (the loaded song).
/// </summary>
public sealed partial class JuceAudioPlaybackService : IAudioPlaybackService, Abstractions.IPlayerInputRouting
{
    private readonly IAppLogger? _logger;

    private readonly IStatusBarService? _statusBar;

    // In-house DSP tone rig (0.16): attached to this engine's native chain so
    // the pedalboard UI can address it. Reattached on every engine (re)create.
    private readonly Dsp.IDspChainService? _dspChain;

    // ABI v28: EVERY seat's tone rig. _dspChain above is seat 0 (player 1) and
    // stays the surface the pedalboard and the RS in-game-tone path drive; this
    // is what makes the engine handle reach players 2..N as well. Null in a
    // native-less/headless build, where _dspChain alone is enough.
    private readonly Dsp.IPlayerDspChains? _dspChains;

    /// <summary>The live-input routing (ABI v28) as state. It is NOT part of
    /// <c>_activeEngineConfig</c>, so this is the only thing that brings the
    /// per-seat routing back across an engine rebuild — see
    /// <c>AttachPerPlayerStateToEngine</c>.</summary>
    private readonly Abstractions.PlayerInputRoutingState _playerRouting =
        new(Dsp.DspPlayers.MaxPlayers);

    /// <summary>Whether the per-seat routing has ever been written at all. It is
    /// what lets <c>PushRoutingToEngine</c> skip the whole surface while nothing
    /// has left the engine's own defaults, and still push the write that goes
    /// BACK to them when an override ends.
    /// <para>
    /// <c>volatile</c> for the same reason <c>_neuralRateAttempted</c> is: the
    /// baseline is written from a POOL thread (startup, the watchdog's recovery
    /// rebuild) and the override/restore from the UI thread. A stale
    /// <c>false</c> read on the restore path would skip the one write that ends
    /// an override's routing, leaving the callback on its seat count.
    /// </para></summary>
    private volatile bool _routingAssertedOnce;

    private readonly object _gate = new();

    // Serializes the media-OPEN work that deliberately runs OUTSIDE _gate
    // (SwapMasterSource, LoadStemsCore). Only background load paths take it, so
    // it never blocks a UI-thread member the way _gate does — its whole job is
    // to stop two loads interleaving their publish steps now that the slow
    // native part between them is unlocked.
    private readonly object _mediaLoadGate = new();

    // Serializes every engine-CONFIG change against every other one:
    // ApplyEngineConfig's in-place reconfigure, the full ReplaceEngine cycle,
    // and the 48 kHz-for-neural-amp request. Never _gate (which must not be
    // held across the reconfigure) and a Monitor rather than a semaphore,
    // because ApplyEngineConfig falls through into ReplaceEngineCore on the
    // same thread and that nested acquire must be free.
    private readonly object _engineConfigGate = new();

    /// <summary>
    ///     The 4 ms position clock. A <c>System.Threading.Timer</c>, not the
    ///     <c>System.Timers.Timer</c> the watchdog uses: that one constructs an
    ///     <c>ElapsedEventArgs</c> stamped with <c>DateTime.Now</c> (a local-time
    ///     conversion) on every raise, which at 250 Hz was 250 discarded objects
    ///     and 250 time-zone lookups a second for the whole of playback (static
    ///     perf audit). This one passes a state object and
    ///     allocates nothing per tick. Armed by <c>Play</c>, parked by
    ///     <c>Pause</c>/<c>Stop</c> (<see cref="StartPositionTimer" /> /
    ///     <see cref="StopPositionTimer" />).
    /// </summary>
    private readonly System.Threading.Timer _positionTimer;

    private const int PositionTimerPeriodMs = 4;

    private void StartPositionTimer() => _positionTimer.Change(0, PositionTimerPeriodMs);

    private void StopPositionTimer() => _positionTimer.Change(Timeout.Infinite, Timeout.Infinite);

    private JuceAudioEngine? _engine;

    // Tracks the JuceEngineConfig that produced the currently-open device.
    // Used by ApplyEngineConfig to decide whether an incoming settings change
    // can be applied in-place via dca_engine_reconfigure (ABI v8) or needs
    // a full ReplaceEngine cycle (driver type / channel mask change).
    private JuceEngineConfig? _activeEngineConfig;

    private JuceAudioSource? _source;

    private string? _loadedPath;

    private double _volume = 1.0;

    private double _songVolume = 1.0;

    private bool _masterMixMuted;

    private double _inputVolume = 1.0;

    private double _monitorVolume = 1.0;

    // --- Stem mixer ------------------------------------------------------
    private readonly Dictionary<string, StemEntry> _stems = new(StringComparer.OrdinalIgnoreCase);

    // Streaming-mirror target. Restored across engine rebuilds.
    private string? _mirrorDeviceName;

    // Master-bus limiter. The engine ships it OFF (ABI v35), so this is the
    // app's opt-in — and like the mirror above it is NOT part of
    // JuceEngineConfig, so it has to be restored across engine rebuilds. It
    // starts at the shipped defaults so an engine built before the first
    // ApplyAudioConfiguration is bounded too.
    private Engine.MasterLimiterSetting _masterLimiter = Engine.MasterLimiterSetting.Default;

    // --- MIDI out --------------------------------------------------------
    private readonly object _midiGate = new();

    private IMidiOutput _midi = new NullMidiOutput();

    private int _midiDeviceIndex = int.MinValue; // sentinel for "not yet configured"

    private int _midiProgram = -1;

    // Internal VST3 instrument bus. When loaded, all chart MIDI is routed
    // through it (rendered in the audio thread) instead of WinMM; when no
    // VSTi is loaded we fall back to the WinMM device (e.g. MS GS Wavetable).
    private JuceAudioPlugin? _instrumentPlugin;

    private string? _instrumentPath;

    private bool _instrumentIsAmpleGuitar;

    private bool _instrumentReloadPending;

    // Latency the loaded VSTi reports via JUCE getLatencySamples(), expressed
    // in milliseconds at the engine's current sample rate. The MIDI scheduler
    // adds this to the effective playhead so chart events fire early enough
    // for the plugin's processed audio to arrive in sync with the file/stem
    // path. Re-queried after engine reconfigure (sample-rate / buffer change).
    private double _instrumentLatencyMs;

    /// <summary>Read from the position timer, the watchdog, the health monitor
    /// and the sandbox threads; written once by <see cref="Dispose" />. An
    /// <c>Interlocked</c> flag behind a property, so the check is the volatile
    /// read its comments always promised and two concurrent Disposes cannot both
    /// run the teardown (the check-then-act would race).</summary>
    private int _disposingFlag;

    private bool IsDisposing => Volatile.Read(ref _disposingFlag) != 0;

    // Pending "open editor" intent. Set when the user toggles a plugin's
    // editor *before* its async load finishes; consumed when the load
    // completes so the editor pops up as soon as the plugin is ready
    // (otherwise the UI checkbox stays "on" but no window appears, which
    // looks like the editor is broken).
    private bool _instrumentPendingEditorOpen;

    private readonly HashSet<Guid> _guitarPendingEditorOpen = [];

    // Pending VST load. The audio engine is created asynchronously after the
    // app boots, but VST restore from settings happens in parallel on a
    // worker � the load arrives while `_engine == null` and must not be
    // dropped silently (user had to switch to "None" and back to retry).
    // Stash the request so ReplaceEngine drains it once the engine exists.
    private string? _pendingInstrumentLoadPath;

    private List<VstChainEntry>? _pendingGuitarChain;

    private bool _instrumentLoadInFlight;

    // Active guitar-VST chain plugins, keyed by the persistent slot Id so we
    // can diff against the next requested chain and only reload what changed.
    private readonly Dictionary<Guid, JuceAudioPlugin> _guitarChain = [];

    // ABI v28: which SEAT each loaded slot is attached to. Kept managed-side
    // rather than read back from the plugin, because a BYPASSED slot is detached
    // natively and would report -1 — and the rehydration lists below (device change,
    // sandbox mode switch) must carry a bypassed slot's seat too, or un-bypassing it
    // after a device change silently lands it on player 1.
    private readonly Dictionary<Guid, int> _guitarSlotPlayer = [];

    /// <summary>
    ///     The BYPASS the app asked for, per slot — the twin of
    ///     <see cref="_guitarSlotPlayer" /> and kept managed-side for the same
    ///     reason it is.
    ///     <para>
    ///         <b>Never read <c>JuceAudioPlugin.Bypass</c> to answer this.</b> That
    ///         property means "not in any seat's input chain", and since the wave-4
    ///         wrapper audit it is derived from the engine rather than cached — so a
    ///         plugin that has been LOADED but never attached (an instrument, a slot
    ///         mid-load, one whose attach threw) reads <c>true</c>. Three sites used
    ///         it as the app's intent and each turned that into a real fault: the
    ///         device-change and sandbox-switch snapshots wrote <c>Bypass = true</c>
    ///         into the rehydration DTO, so an ACTIVE plugin came back bypassed
    ///         after a device change; and the chain differ read it twice, skipping a
    ///         re-seat that was needed and emitting a phantom bypass op that was
    ///         not. This dictionary is written wherever the app decides a slot's
    ///         bypass and is the only thing any of them read.
    ///     </para>
    /// </summary>
    private readonly Dictionary<Guid, bool> _guitarSlotBypass = [];

    public event EventHandler<bool>? InstrumentVstLoadChanged;

    public event EventHandler<(Guid SlotId, bool Loaded)>? GuitarVstSlotLoadChanged;

    public event EventHandler<Guid>? GuitarVstSlotEditorClosed;

    public event EventHandler? InstrumentVstEditorClosed;

    private bool _instrumentChannelsSetup;

    // Single-threaded worker for VST plugin loading/unloading. Native code
    // already dispatches onto the JuceHost message thread, but we keep the
    // managed call off the UI thread (JUCE init + DSP allocation can spend
    // 100ms+) and serialize chain mutations so a flurry of UI changes
    // applies in order.
    private readonly SemaphoreSlim _vstGate = new(1, 1);
    private readonly ILocalizationService _localization;

    private readonly Engine.RsAmpTableHandover _rsAmpTable;

    // The audio health monitor's thread (Diagnostics/AudioHealthMonitor): it
    // reads _engine once per poll and nothing else of ours.
    private readonly AudioHealthMonitor _health;

    // ABI v47: each seat's external process insert (ExternalInsert/). Driven through
    // JuceExternalProcessInsert; replayed from AttachPerPlayerStateToEngine.
    private readonly ExternalInsert.ExternalProcessInserts _externalInserts;

    internal ExternalInsert.ExternalProcessInserts ExternalInserts => _externalInserts;

    /// <summary>An engine is up, so the external process insert (ABI v47) can be opened.</summary>
    public bool IsExternalInsertAvailable => _engine != null;

    public JuceAudioPlaybackService(ILocalizationService localization, IAppLogger? logger = null, IStatusBarService? statusBar = null,
        Dsp.IDspChainService? dspChain = null, Dsp.IPlayerDspChains? dspChains = null)
    {
        _localization = localization;
        _logger = logger;
        _statusBar = statusBar;
        // The chart synth's request and the re-amp takes: each reads the engine
        // through this getter and replays itself from AttachPerPlayerStateToEngine.
        _chartSynth = new ChartSynth.ChartSynthEngineDriver(logger, () => _engine, () => CanRouteInstrumentToRig);
        _reampTakes = new Reamp.ReampTakes(logger, () => _engine);
        // ABI v47: the external process insert, replayed from the same per-seat attach.
        _externalInserts = new ExternalInsert.ExternalProcessInserts(logger,
            () => _engine is { } e ? new ExternalInsert.JuceExternalInsertEngine(e) : null,
            RequestExternalInsertSampleRate);
        // Seat 0 is the one everything pre-v28 talks to; prefer the registry's
        // seat 0 when both are supplied so the two can never disagree.
        _dspChains = dspChains;
        _dspChain = dspChains?.For(0) ?? dspChain;
        // Apply the 48 kHz-for-neural-amp policy whenever the chain composition
        // changes (a namamp node added via the pedalboard, the NAM browser, or a
        // preset apply — every path funnels through JuceDspChainService.AddNode).
        _dspChain?.NodesChanged += OnDspNodesChanged;
        // 4 ms instead of 10 ms: the timer is now the drift-correction
        // source rather than the primary position clock. Higher cadence
        // keeps the low-pass error window small so the smoothed playhead
        // tracks the audio thread tightly without ever overshooting by a
        // perceptible amount.
        _positionTimer = new System.Threading.Timer(static state => ((JuceAudioPlaybackService)state!).PositionTick(),
            this, Timeout.Infinite, Timeout.Infinite);

        // Device disconnect/reconnect watchdog. Cheap probe (device
        // enumeration + a couple of int reads) every few seconds; no-ops
        // until the first ApplyAudioConfiguration provides a config to watch.
        _watchdogTimer = new Timer(DeviceWatchdogIntervalMs) { AutoReset = true };
        _watchdogTimer.Elapsed += (_, _) => WatchdogTick();
        _watchdogTimer.Start();

        // Audio health logging (dropouts / stutter / device config). Cheap
        // render-clock poll on a background thread; gated on the Audio log
        // category so the logging switch turns it on and off.
        _health = new AudioHealthMonitor(logger, () => _engine, () => IsDisposing);
        _health.Start();

        // Rocksmith's own amp chains: handed to the engine when the
        // game tables are published, and replayed on every engine build.
        _rsAmpTable = new Engine.RsAmpTableHandover(logger, () => !IsDisposing && _engine is not null);
    }

    public bool IsVstEffectEnabled { get; set; }

    public double VstEffectVolume { get; set; }

    public bool IsVstInstrumentEnabled { get; set; }

    private double _vstInstrumentVolume = 1.0;

    public double VstInstrumentVolume
    {
        get => _vstInstrumentVolume;
        set
        {
            _vstInstrumentVolume = value;
            try { _engine?.SetInstrumentGain((float)Math.Clamp(value, 0.0, 4.0)); }
            catch { /* ignored */ }
        }
    }

    public double MidiOffsetMs { get; set; }

    public bool IsDeviceLost => _deviceLost;

    public AudioDiagnostics Diagnostics { get; } = new();

    public event EventHandler<double>? PositionChanged;

    // Raised by ReportDeviceBufferFrames (JuceAudioPlaybackService.Devices.cs)
    // whenever the DEVICE settles on a different block size than the last one
    // published. The Audio settings buffer dropdown and the status bar both subscribe, so
    // it must not carry a `#pragma warning disable CS0067`: with warnings-as-errors,
    // deleting the invoke fails the build.
    public event EventHandler<int>? DeviceBufferFramesChanged;

    // Raised by the device watchdog (JuceAudioPlaybackService.DeviceWatchdog.cs).
    public event EventHandler? DeviceLost;

    public event EventHandler<AudioDeviceReconnectedEventArgs>? DeviceReconnected;

    /// <summary>One position tick — see <see cref="_positionTimer" />.</summary>
    private void PositionTick()
    {
        // Timer.Dispose() without a wait handle is NOT a barrier: a tick already
        // inside this handler keeps running concurrently with DisposeCore,
        // touching a _source that is being disposed and an _engine that is
        // being destroyed. It survived only incidentally — the handler
        // catches ObjectDisposedException and swallows everything else, and
        // NativeHandleOwner drains. Checking the flag makes the survival
        // designed rather than lucky, and costs one volatile read.
        if (IsDisposing) return;
        try
        {
            var src = _source;
            if (src == null) return;
            var nowTicks = System.Diagnostics.Stopwatch.GetTimestamp();
            // Read BEFORE the native position: a seek that lands after this
            // line moves the epoch, and TryTick then refuses the reading
            // instead of publishing the pre-seek position as the new burst.
            var epoch = _playhead.Epoch;
            double nativeMs;
            double lengthMs;
            try
            {
                nativeMs = src.AudiblePosition.TotalMilliseconds;
                lengthMs = src.Length.TotalMilliseconds;
            }
            catch (ObjectDisposedException)
            {
                // Race with LoadAudioAsync/FreeAudio. Bail this tick;
                // the next tick will see the fresh source (or null).
                return;
            }
            if (lengthMs > 0) _lastKnownLengthMs = lengthMs;
            var speed = _playbackSpeed <= 0 ? 1.0 : _playbackSpeed;
            // First tick of a burst hard-syncs; afterwards extrapolate by
            // elapsed × speed, low-pass toward native, snap on >50 ms drift.
            // The maths and the one-immutable-sample state live in
            // PlayheadInterpolator (pinned at 2× with device-buffer
            // quantization by PlayheadInterpolatorTests).
            if (!_playhead.TryTick(nativeMs, nowTicks, speed, epoch, out var reportMs)) return;
            // Publish the pre-compensation value: it is the song time being
            // rendered right now, which is what anything scheduling INTO the
            // output stream (the click scheduler) must aim at.
            _rawPlayheadMs = reportMs;
            // Compensate for the audio that's still sitting in the device
            // output buffer + driver/hardware delay + any user-tunable
            // offset. Without this the on-screen playhead leads what the
            // user actually hears by ~2 ms (ASIO low-buffer) to ~50 ms
            // (Steam Streaming Speakers mirror).
            var lat = ApplyLatencyCompensation(reportMs);
            // Clamp to the known song length so the playhead never runs
            // past the end of the audio (the native side keeps ticking
            // its transport position even after EOF, and stems may
            // disagree on length, so we clamp using the master length
            // and stop the timer once we've reached the end).
            if (lengthMs > 0 && lat >= lengthMs)
            {
                lat = _lastKnownPosition.Publish(lengthMs, epoch);
                // EOF: pause everything, similar to a natural transport
                // end, so the user can hit Play again to restart instead
                // of being stuck with a phantom-playing playhead beyond
                // the audio.
                //
                // BEFORE the publish, and that ordering is load-bearing: this is
                // the LAST PositionChanged of the pass (Pause stops this timer),
                // so a subscriber that reads IsPlaying in response to it gets no
                // second chance. Raising first left AudioPlaybackCoordinator's
                // coalesced UI publish racing a flag that was about to become
                // false, and the transport bar could stay stuck on "playing"
                // for the rest of the session.
                try { Pause(); } catch { /* ignored */ }
                PositionChanged?.Invoke(this, lat);
                return;
            }
            lat = _lastKnownPosition.Publish(lat, epoch);
            PositionChanged?.Invoke(this, lat);
        }
        catch { /* swallow during teardown */ }
    }

    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposingFlag, 1) != 0) return;
        _dspChain?.NodesChanged -= OnDspNodesChanged;
        _rsAmpTable.Dispose();
        _health.Stop();
        _watchdogTimer.Stop();
        _watchdogTimer.Dispose();
        // Timer.Stop()/Dispose() is not a barrier: a recovery tick already
        // inside WatchdogTick keeps running, and its AttemptDeviceRecovery ->
        // ApplyAudioConfiguration -> ReplaceEngineGated walk would publish a
        // BRAND-NEW engine into _engine after DisposeCore has destroyed the old
        // one and nulled the field. Nothing would ever dispose that engine, so
        // the process exits still holding the (exclusive, on ASIO) sound card —
        // the "one survived 32 hours" mode. Wait for the in-flight tick, bounded
        // so a wedged native probe cannot turn a clean exit into a hang; the
        // IsDisposing bails inside ReplaceEngineGated are the second half of
        // this guard and hold even if the wait times out.
        WaitForWatchdogIdle(TimeSpan.FromSeconds(5));
        StopPositionTimer();
        _positionTimer.Dispose();
        // Drain any pending VST plugin load/unload work, and KEEP the gate for
        // the whole teardown (ReplaceEngineCore already does exactly this).
        //
        // The old code was `_vstGate.Wait(5s); _vstGate.Release();` — it threw
        // the Wait's bool away and released unconditionally, which broke both
        // ways. On timeout it released a gate it did not own, so mutual
        // exclusion was simply gone and teardown walked into a live
        // engine.LoadPlugin(); the loader's own finally then pushed the count
        // past max and threw SemaphoreFullException out of a finally, faulting
        // the task unobserved. And even on success it released immediately, so
        // a chain apply already blocked on the gate would wake up and load
        // plugins into an engine we are in the middle of destroying — after
        // DisposeSandbox had already run.
        //
        // 5 s was never enough anyway: a first-run AGPF/BIAS load takes 25 s+,
        // and the sandbox load path waits up to 180 s.
        var vstGateHeld = false;
        try { vstGateHeld = _vstGate.Wait(TimeSpan.FromSeconds(30)); } catch { /* ignored */ }
        if (!vstGateHeld)
        {
            _logger?.Warning(AppLogCategory.Audio,
                "VST gate still held after 30 s at dispose; tearing down anyway.");
        }

        try
        {
            DisposeCore();
        }
        finally
        {
            if (vstGateHeld) { try { _vstGate.Release(); } catch { /* ignored */ } }
            // Only dispose the gate if we actually own it. A loader wedged past
            // the 30 s wait still holds it, and disposing a SemaphoreSlim out
            // from under a waiter turns a slow teardown into an
            // ObjectDisposedException on a pool thread.
            if (vstGateHeld) { try { _vstGate.Dispose(); } catch { /* ignored */ } }
        }
    }

    private void DisposeCore()
    {
        // Stop the sandbox child first (graceful quit + kill fallback) and
        // disarm the shared-memory bridge while the engine is still alive.
        try { DisposeSandbox(); } catch { /* ignored */ }
        // Stop the audio callback FIRST so plugin destructors (HidePluginEditor /
        // DetachPluginFromInput / UnloadPlugin below) cannot race with the
        // audio thread still calling processBlock through the plugin. Doing
        // this in the previous order (detach plugin first, stop source second)
        // could cause STATUS_STACK_BUFFER_OVERRUN crashes inside the VST DLL
        // when the host UI was closed while a song was loaded.
        try { _source?.Stop(); } catch { /* ignored */ }
        lock (_gate)
        {
            foreach (var s in _stems.Values) { try { s.Source?.Stop(); } catch { /* ignored */ } }
        }
        // Now detach the instrument VST; the audio path is silent so the
        // plugin's editor + processor can be torn down on this thread without
        // racing the mixer.
        try { DetachInstrumentLocked(); } catch { /* ignored */ }
        // Proactively unload the guitar VST chain on this (non-message-pump)
        // thread, in known order, while the engine is still alive. Hosts
        // like BIAS FX, Helix and Neural DSP can access-violate during DLL
        // detach if their editor/AudioPluginInstance teardown is interleaved
        // with the device-manager shutdown that happens inside engine
        // destroy � explicit unload first keeps the order strict.
        try
        {
            List<JuceAudioPlugin> chainPlugins;
            lock (_gate)
            {
                chainPlugins = _guitarChain.Values.ToList();
                _guitarChain.Clear();
                _guitarSlotPlayer.Clear();
                _guitarSlotBypass.Clear();
            }
            foreach (var p in chainPlugins)
            {
                try { p.HideEditor(); } catch { /* ignored */ }
                try { p.DetachFromInput(); } catch { /* ignored */ }
                try { p.Dispose(); } catch { /* ignored */ }
            }
        }
        catch (Exception ex)
        {
            _logger?.Warning(AppLogCategory.Audio, $"Guitar chain teardown failed during dispose: {ex.Message}");
        }
        lock (_midiGate)
        {
            try { _midi.Dispose(); } catch { /* ignored */ }
            _midi = new NullMidiOutput();
        }
        lock (_gate)
        {
            FreeAudioLocked();
            try { AttachDspChainsToEngine(null); } catch { /* ignored */ }
            try { CloseRigMidiInputLocked(); } catch { /* engine disposal also owns MIDI children */ }
            try { _engine?.Dispose(); }
            catch (Exception ex)
            {
                _logger?.Warning(AppLogCategory.Audio, $"Engine dispose threw: {ex.Message}");
            }
            _engine = null;
        }
    }
}
