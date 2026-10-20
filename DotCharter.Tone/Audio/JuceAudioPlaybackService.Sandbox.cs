// Partial: out-of-process VST sandbox (0.12.0) — plugin hosting in a child
// process (juce_vst_host.exe), audio bridged over shared memory by the
// native engine (ABI v19), control over a named pipe. See
// JuceAudioPlaybackService.cs for the shared state and locking rules.
using System.Runtime.InteropServices;
using DotCharter.Tone.Audio.Midi;
using DotCharter.Tone.Audio.Vst.Sandbox;
using JuceSharp;
using Timer = System.Timers.Timer;

namespace DotCharter.Tone.Audio;

public sealed partial class JuceAudioPlaybackService
{
    // Feature flag (RigConfig.OutOfProcessVst). Flipped by ApplySandboxMode;
    // read on many paths, hence volatile.
    private volatile bool _outOfProcessVst;

    // All fields below are guarded by _sandboxGate unless noted.
    private readonly object _sandboxGate = new();
    private VstSandboxHost? _sandbox;
    private readonly VstSandboxChainState _sandboxDesired = new();
    private readonly Dictionary<Guid, (string Path, bool Bypass)> _sandboxSlots = [];
    private volatile bool _sandboxInstrumentLoaded;
    private bool _sandboxGaveUp;

    private readonly VstSandboxRestartPolicy _sandboxRestartPolicy = new();
    private readonly System.Diagnostics.Stopwatch _sandboxClock = System.Diagnostics.Stopwatch.StartNew();
    private int _sandboxRestartInFlight;
    private Timer? _sandboxHealthTimer;
    private uint _sandboxLastHeartbeat;
    private bool _sandboxHeartbeatPrimed;

    /// <summary>
    /// True when the app may take the clean (non-TerminateNow) shutdown
    /// path: all plugins live out-of-process, so no VST code runs inside
    /// this process during teardown.
    /// </summary>
    public bool SupportsCleanShutdown
    {
        get
        {
            if (!_outOfProcessVst || _instrumentPlugin != null) return false;
            lock (_gate) return _guitarChain.Count == 0;
        }
    }

    [DllImport("user32.dll")]
    private static extern bool AllowSetForegroundWindow(int dwProcessId);

    // ------------------------------------------------------------------
    //  Mode switching (called from ApplyAudioConfiguration)
    // ------------------------------------------------------------------

    private void ApplySandboxMode(bool enabled)
    {
        if (enabled == _outOfProcessVst) return;
        if (enabled && (!OperatingSystem.IsWindows() || VstSandboxHost.FindExecutable() == null))
        {
            _logger?.Warning(AppLogCategory.Audio,
                "Out-of-process VST hosting requested but juce_vst_host.exe is unavailable — staying in-process.");
            return;
        }

        _logger?.Info(AppLogCategory.Audio,
            enabled ? "Switching VST hosting to the sandbox process."
                    : "Switching VST hosting back in-process.");

        // Capture the current plugin intent from whichever side is active,
        // flip the flag, then re-apply through the (now re-routed) public
        // entry points. SetGuitarVstChain / LoadInstrumentVst serialize on
        // _vstGate themselves.
        List<VstChainEntry> chain;
        string? instrumentPath;
        if (enabled)
        {
            lock (_gate)
            {
                chain = _guitarChain
                    .Select(kv => new VstChainEntry
                    {
                        Id = kv.Key, PluginPath = kv.Value.Path,
                        // The APP's intent, not `kv.Value.Bypass`: the wrapper's
                        // property means "not in any seat's input chain", so a
                        // slot that is loaded but not (yet) attached reads
                        // bypassed — and this snapshot is what the sandbox is
                        // rebuilt FROM, so an active plugin came back bypassed
                        // after a mode switch.
                        Bypass = _guitarSlotBypass.GetValueOrDefault(kv.Key),
                        Player = _guitarSlotPlayer.GetValueOrDefault(kv.Key),
                    })
                    .ToList();
                instrumentPath = _instrumentPath;
            }
            // Tear down the in-proc plugins first (strict order, engine alive).
            TearDownInProcPluginsForModeSwitch();
            _outOfProcessVst = true;
            _sandboxRestartPolicy.Reset();
            lock (_sandboxGate) _sandboxGaveUp = false;
        }
        else
        {
            lock (_sandboxGate)
            {
                chain = _sandboxDesired.Chain.Select(e => new VstChainEntry
                { Id = e.Id, PluginPath = e.PluginPath, Bypass = e.Bypass, Player = e.Player }).ToList();
                instrumentPath = _sandboxDesired.InstrumentPath;
            }
            _outOfProcessVst = false;
            _ = Task.Run(async () =>
            {
                await _vstGate.WaitAsync().ConfigureAwait(false);
                try { await ShutdownSandboxLockedAsync(disarmBridge: true).ConfigureAwait(false); }
                catch (Exception ex) { _logger?.Warning(AppLogCategory.Audio, $"Sandbox shutdown on mode switch failed: {ex.Message}"); }
                finally { _vstGate.Release(); }
            });
        }

        if (chain.Count > 0) SetGuitarVstChain(chain);
        if (!string.IsNullOrEmpty(instrumentPath))
        {
            LoadInstrumentVst(instrumentPath);
        }
        else if (!enabled)
        {
            // leaving sandbox mode with no instrument: nothing to do.
        }
    }

    private void TearDownInProcPluginsForModeSwitch()
    {
        _ = Task.Run(async () =>
        {
            await _vstGate.WaitAsync().ConfigureAwait(false);
            try
            {
                try { DetachInstrumentLocked(); } catch { /* ignored */ }
                List<(Guid Id, JuceAudioPlugin Plugin)> snapshot;
                lock (_gate)
                {
                    snapshot = _guitarChain.Select(kv => (kv.Key, kv.Value)).ToList();
                    _guitarChain.Clear();
                }
                foreach (var (id, plugin) in snapshot)
                {
                    try { plugin.HideEditor(); } catch { /* ignored */ }
                    try { plugin.DetachFromInput(); } catch { /* ignored */ }
                    try { plugin.Dispose(); } catch { /* ignored */ }
                    try { GuitarVstSlotLoadChanged?.Invoke(this, (id, false)); } catch { /* ignored */ }
                }
            }
            finally { _vstGate.Release(); }
        });
    }

    // ------------------------------------------------------------------
    //  Sandbox lifecycle (caller holds _vstGate unless noted)
    //
    //  Every pipe round trip is AWAITED through VstSandboxCalls, never
    //  blocked on: _vstGate is a SemaphoreSlim, so a worker may hold it
    //  across an await (it serializes VST work against the engine swap,
    //  whatever thread the continuation lands on). No Monitor — _gate,
    //  _sandboxGate, _midiGate — is ever held across one.
    //  SandboxBlockingWaitLintTests forbids a blocking wait in this file.
    // ------------------------------------------------------------------

    /// <summary>Spawn the child (if needed) and arm the native bridge.
    /// Caller holds _vstGate; engine must be non-null.</summary>
    private async Task<VstSandboxHost> EnsureSandboxLockedAsync(JuceAudioEngine engine)
    {
        lock (_sandboxGate)
        {
            if (_sandbox is { IsRunning: true }) return _sandbox;
            if (_sandboxGaveUp)
            {
                // The crash budget was exhausted earlier, but a fresh
                // user-driven plugin request is an explicit "try again" —
                // grant a new budget.
                _sandboxGaveUp = false;
                _sandboxRestartPolicy.Reset();
            }
        }

        var host = new VstSandboxHost();
        var sr = SafeRead(e => e.SampleRate) is var s and > 0 ? s : 44100;
        var blk = SafeRead(e => e.BufferFrames) is var b and > 0 ? b : 512;

        _logger?.Info(AppLogCategory.Audio,
            $"Starting VST sandbox ({VstSandboxHost.ExecutableName}, sr={sr}, block={blk}).");
        host.ChildLog += line => _logger?.Debug(AppLogCategory.Audio, $"[sandbox] {line}");
        host.EditorClosedByUser += OnSandboxEditorClosed;
        host.Exited += requested => { if (!requested) OnSandboxCrashed(host); };

        try
        {
            // Bounded by the host (15 s connect + 10 s ping) and again here; a
            // timeout throws into the catch, which kills the half-started child.
            await host.StartAsync(sr, blk).WaitAsync(VstSandboxCalls.StartBound).ConfigureAwait(false);
        }
        catch
        {
            try { host.Dispose(); } catch { /* ignored */ }
            throw;
        }

        if (!engine.SetRemoteBridge(host.ShmName, host.RequestEventName, host.ResponseEventName, true))
        {
            try { host.Dispose(); } catch { /* ignored */ }
            throw new InvalidOperationException("Failed to arm the native shared-memory bridge.");
        }

        lock (_sandboxGate)
        {
            _sandbox = host;
            _sandboxSlots.Clear();
            _sandboxInstrumentLoaded = false;
            _sandboxHeartbeatPrimed = false;
        }
        EnsureSandboxHealthTimer();
        _logger?.Info(AppLogCategory.Audio,
            $"VST sandbox running (pid={host.ChildProcessId?.ToString() ?? "?"}).");
        return host;
    }

    /// <summary>Caller holds _vstGate.</summary>
    private async Task ShutdownSandboxLockedAsync(bool disarmBridge)
    {
        // The health timer monitors THIS sandbox; without stopping it here,
        // switching back to in-process VST hosting (or exhausting the crash
        // budget) left a 3 s no-op timer ticking for the rest of the session —
        // SandboxHealthTick early-returns on !_outOfProcessVst, so it did
        // nothing except wake a pool thread twenty times a minute forever.
        // EnsureSandboxLockedAsync re-arms it whenever a fresh host is spawned.
        StopSandboxHealthTimer();
        VstSandboxHost? host;
        lock (_sandboxGate)
        {
            host = _sandbox;
            _sandbox = null;
            _sandboxSlots.Clear();
            _sandboxInstrumentLoaded = false;
        }
        if (disarmBridge)
        {
            try { _engine?.SetRemoteBridge(null, null, null, false); } catch { /* ignored */ }
        }
        if (host != null)
        {
            await VstSandboxCalls.StopAsync(host).ConfigureAwait(false); // bounded; kills on overrun
            try { host.Dispose(); } catch { /* ignored */ }
        }
    }

    /// <summary>
    /// Called from ReplaceEngineGated after a fresh engine is up: the child
    /// (and its loaded plugins) survives the swap — re-arm the new engine's
    /// bridge against the existing kernel object names and re-prepare the
    /// child for the (possibly new) sample rate / block size.
    /// Caller holds _vstGate — and, through ReplaceEngineCore, the
    /// _engineConfigGate Monitor, so this stays SYNCHRONOUS: the bridge re-arm
    /// is one native call. The "prepare" round trip (up to 30 s against a busy
    /// child) is queued behind the gate the caller holds rather than run here,
    /// so a slow child cannot stretch the device change that the UI is
    /// waiting on. It is the same worker the in-place reconfigure uses.
    /// </summary>
    private void RearmSandboxAfterEngineSwapLocked(JuceAudioEngine engine)
    {
        VstSandboxHost? host;
        lock (_sandboxGate) host = _sandbox;
        if (host is not { IsRunning: true }) return;
        try
        {
            if (!engine.SetRemoteBridge(host.ShmName, host.RequestEventName, host.ResponseEventName, true))
                throw new InvalidOperationException("SetRemoteBridge returned false");
            NotifySandboxEngineReconfigured();
            if (_sandboxInstrumentLoaded) RebindSandboxInstrumentMidi(engine);
            _logger?.Info(AppLogCategory.Audio, "VST sandbox bridge re-armed after engine swap.");
        }
        catch (Exception ex)
        {
            _logger?.Warning(AppLogCategory.Audio, $"Failed to re-arm sandbox bridge after engine swap: {ex.Message}");
        }
    }

    private async Task SendSandboxPrepareAsync(IVstSandboxChannel host)
    {
        var sr = SafeRead(e => e.SampleRate);
        var blk = SafeRead(e => e.BufferFrames);
        if (sr <= 0 || blk <= 0) return;
        await VstSandboxCalls.TrySendAsync(host,
            new VstSandboxRequest { Op = "prepare", Sr = sr, Block = blk }, TimeSpan.FromSeconds(30),
            ex => _logger?.Warning(AppLogCategory.Audio, $"Sandbox prepare({sr}/{blk}) failed: {ex.Message}"))
            .ConfigureAwait(false);
    }

    /// <summary>The device format changed (in-place reconfigure, or a full
    /// engine swap's re-arm) — push it to the child. Fire-and-forget;
    /// serialized on _vstGate, so it runs after the caller's own gated work.</summary>
    private void NotifySandboxEngineReconfigured()
    {
        if (!_outOfProcessVst) return;
        _ = Task.Run(async () =>
        {
            await _vstGate.WaitAsync().ConfigureAwait(false);
            try
            {
                VstSandboxHost? host;
                lock (_sandboxGate) host = _sandbox;
                if (host is { IsRunning: true } && _engine != null)
                    await SendSandboxPrepareAsync(host).ConfigureAwait(false);
            }
            catch (Exception ex) { _logger?.Warning(AppLogCategory.Audio, $"Sandbox prepare after a device change failed: {ex.Message}"); }
            finally { _vstGate.Release(); }
        });
    }

    private void RebindSandboxInstrumentMidi(JuceAudioEngine engine)
    {
        lock (_midiGate)
        {
            try { _midi.Dispose(); } catch { /* ignored */ }
            var sink = new JuceInstrumentMidiOutput(engine);
            sink.Open();
            _midi = sink;
            _instrumentChannelsSetup = false;
            SetupInstrumentChannelsLocked(sink);
            if (_midiProgram >= 0)
                try { _midi.ProgramChange(0, _midiProgram); _midi.ChannelVolume(0, 100); } catch { /* ignored */ }
        }
    }

    // ------------------------------------------------------------------
    //  Chain / instrument application (sandbox side)
    // ------------------------------------------------------------------

    /// <summary>Sandbox counterpart of ApplyGuitarVstChainCore. Caller holds _vstGate.</summary>
    private async Task ApplySandboxGuitarChainCoreAsync(IReadOnlyList<VstChainEntry> entries)
    {
        var engine = _engine;
        if (engine == null) return;
        var host = await EnsureSandboxLockedAsync(engine).ConfigureAwait(false);

        // Phase 1: drop slots that are gone or whose plugin path changed.
        List<Guid> toRemove;
        lock (_sandboxGate)
        {
            toRemove = _sandboxSlots.Keys
                .Where(id =>
                {
                    var desired = entries.FirstOrDefault(e => e.Id == id);
                    return desired == null
                        || !string.Equals(desired.PluginPath, _sandboxSlots[id].Path, StringComparison.OrdinalIgnoreCase);
                })
                .ToList();
        }
        foreach (var id in toRemove)
        {
            await VstSandboxCalls.TrySendAsync(host,
                new VstSandboxRequest { Op = "unload", Slot = id.ToString() }, TimeSpan.FromSeconds(30),
                ex => _logger?.Warning(AppLogCategory.Audio, $"Sandbox unload({id}) failed: {ex.Message}"))
                .ConfigureAwait(false);
            bool removedEntirely;
            lock (_sandboxGate)
            {
                _sandboxSlots.Remove(id);
                removedEntirely = entries.All(e => e.Id != id);
            }
            if (removedEntirely)
                lock (_gate) _guitarPendingEditorOpen.Remove(id);
            try { GuitarVstSlotLoadChanged?.Invoke(this, (id, false)); } catch { /* ignored */ }
        }

        // Phase 2: load new slots / sync bypass on surviving ones.
        foreach (var entry in entries)
        {
            (string Path, bool Bypass)? existing;
            lock (_sandboxGate)
                existing = _sandboxSlots.TryGetValue(entry.Id, out var e) ? e : null;

            if (existing is { } live)
            {
                if (live.Bypass != entry.Bypass)
                {
                    var ack = await VstSandboxCalls.TrySendAsync(host,
                        new VstSandboxRequest { Op = "bypass", Slot = entry.Id.ToString(), Value = entry.Bypass },
                        TimeSpan.FromSeconds(10),
                        ex => _logger?.Warning(AppLogCategory.Audio, $"Sandbox bypass({entry.Id}) failed: {ex.Message}"))
                        .ConfigureAwait(false);
                    if (ack != null)
                        lock (_sandboxGate) _sandboxSlots[entry.Id] = (live.Path, entry.Bypass);
                }
                continue;
            }

            if (string.IsNullOrWhiteSpace(entry.PluginPath)
                || (!File.Exists(entry.PluginPath) && !Directory.Exists(entry.PluginPath)))
            {
                _logger?.Warning(AppLogCategory.Audio, $"VST3 chain (sandbox): missing plugin '{entry.PluginPath}'.");
                continue;
            }

            // 180 s: a cold first-run scan of a big amp sim (BIAS FX, Helix)
            // takes tens of seconds. Awaited — no thread waits for it.
            var response = await VstSandboxCalls.TrySendAsync(host, new VstSandboxRequest
            {
                Op = "load", Slot = entry.Id.ToString(), Path = entry.PluginPath, Bypass = entry.Bypass,
            }, TimeSpan.FromSeconds(180),
                ex => _logger?.Error(AppLogCategory.Audio, $"VST3 chain (sandbox): failed to load '{entry.PluginPath}': {ex.Message}"))
                .ConfigureAwait(false);
            if (response == null) continue;
            if (response is not { Ok: true })
            {
                _logger?.Error(AppLogCategory.Audio,
                    $"VST3 chain (sandbox): child rejected '{entry.PluginPath}': {response.Error ?? "unknown error"}");
                continue;
            }

            bool pendingEditorOpen;
            lock (_sandboxGate) _sandboxSlots[entry.Id] = (entry.PluginPath, entry.Bypass);
            lock (_gate) pendingEditorOpen = _guitarPendingEditorOpen.Remove(entry.Id);
            _logger?.Info(AppLogCategory.Audio,
                $"VST3 chain (sandbox): added '{response.Name ?? Path.GetFileName(entry.PluginPath)}' (bypassed={entry.Bypass}).");
            try { GuitarVstSlotLoadChanged?.Invoke(this, (entry.Id, true)); } catch { /* ignored */ }

            if (pendingEditorOpen)
                await SendSandboxEditorToggleAsync(host, entry.Id.ToString(), open: true).ConfigureAwait(false);
        }

        // Phase 3: enforce the requested processing order.
        await VstSandboxCalls.TrySendAsync(host, new VstSandboxRequest
        {
            Op = "reorder",
            Slots = entries.Select(e => e.Id.ToString()).ToArray(),
        }, TimeSpan.FromSeconds(10),
            ex => _logger?.Warning(AppLogCategory.Audio, $"Sandbox reorder failed: {ex.Message}"))
            .ConfigureAwait(false);

        lock (_sandboxGate)
        {
            _sandboxDesired.Chain = entries.Select(e => new VstChainEntry
            { Id = e.Id, PluginPath = e.PluginPath, Bypass = e.Bypass, Player = e.Player }).ToList();
        }
    }

    /// <summary>Sandbox counterpart of the LoadInstrumentVst worker body. Caller holds _vstGate.</summary>
    private async Task LoadInstrumentVstSandboxCoreAsync(string? pluginPath, bool openEditorAfterLoad)
    {
        var engine = _engine;
        if (engine == null) return;

        if (string.IsNullOrWhiteSpace(pluginPath))
        {
            await UnloadSandboxInstrumentLockedAsync().ConfigureAwait(false);
            return;
        }

        var host = await EnsureSandboxLockedAsync(engine).ConfigureAwait(false);

        bool alreadyLoaded;
        lock (_sandboxGate)
        {
            alreadyLoaded = _sandboxInstrumentLoaded
                && string.Equals(_sandboxDesired.InstrumentPath, pluginPath, StringComparison.OrdinalIgnoreCase);
        }

        if (alreadyLoaded)
        {
            if (openEditorAfterLoad)
                await SendSandboxEditorToggleAsync(host, VstSandboxProtocol.InstrumentSlotId, open: true).ConfigureAwait(false);
            return;
        }

        var response = await VstSandboxCalls.TrySendAsync(host,
            new VstSandboxRequest { Op = "loadInstrument", Path = pluginPath }, TimeSpan.FromSeconds(180),
            ex => _logger?.Warning(AppLogCategory.Audio, $"Failed to load instrument VST3 (sandbox) '{pluginPath}': {ex.Message}"))
            .ConfigureAwait(false);
        if (response == null) return;
        if (response is not { Ok: true })
        {
            _logger?.Warning(AppLogCategory.Audio,
                $"Instrument VST3 (sandbox) rejected '{pluginPath}': {response.Error ?? "unknown error"}");
            return;
        }

        lock (_sandboxGate)
        {
            _sandboxInstrumentLoaded = true;
            _sandboxDesired.InstrumentPath = pluginPath;
        }
        _instrumentIsAmpleGuitar = IsAmpleGuitarPath(pluginPath);
        _instrumentChannelsSetup = false;
        lock (_midiGate) { _midiScheduler.ForgetChannelNotes(); }
        try { engine.SetInstrumentGain((float)Math.Clamp(_vstInstrumentVolume, 0.0, 4.0)); } catch { /* ignored */ }

        var latencySamples = response.Latency ?? 0;
        var sr = SafeRead(e => e.SampleRate);
        _instrumentLatencyMs = latencySamples > 0 && sr > 0 ? latencySamples * 1000.0 / sr : 0.0;
        _logger?.Info(AppLogCategory.Audio,
            $"Instrument VST3 attached in sandbox: '{response.Name ?? Path.GetFileName(pluginPath)}' " +
            $"(ampleGuitar={_instrumentIsAmpleGuitar}, pluginLatency={_instrumentLatencyMs:F2}ms).");

        // Route chart MIDI to the internal bus — the native engine forwards
        // it through the shared-memory bridge to the child.
        RebindSandboxInstrumentMidi(engine);

        if (openEditorAfterLoad || _instrumentPendingEditorOpen)
            await SendSandboxEditorToggleAsync(host, VstSandboxProtocol.InstrumentSlotId, open: true).ConfigureAwait(false);
        _instrumentPendingEditorOpen = false;
    }

    /// <summary>Caller holds _vstGate.</summary>
    private async Task UnloadSandboxInstrumentLockedAsync()
    {
        VstSandboxHost? host;
        bool wasLoaded;
        lock (_sandboxGate)
        {
            host = _sandbox;
            wasLoaded = _sandboxInstrumentLoaded;
            _sandboxInstrumentLoaded = false;
            _sandboxDesired.InstrumentPath = null;
        }
        if (!wasLoaded) return;

        _instrumentIsAmpleGuitar = false;
        _instrumentLatencyMs = 0.0;
        _instrumentChannelsSetup = false;

        // Flush + swap the MIDI sink to null BEFORE the child unloads.
        try
        {
            lock (_midiGate)
            {
                _previewSink.Target = _midi;
                _midiScheduler.Flush(_previewSink);
                var oldSink = _midi;
                _midi = new NullMidiOutput();
                try { oldSink.Dispose(); } catch { /* ignored */ }
            }
        }
        catch { /* ignored */ }

        if (host is { IsRunning: true })
        {
            await VstSandboxCalls.TrySendAsync(host,
                new VstSandboxRequest { Op = "unloadInstrument" }, TimeSpan.FromSeconds(30),
                ex => _logger?.Warning(AppLogCategory.Audio, $"Sandbox instrument unload failed: {ex.Message}"))
                .ConfigureAwait(false);
        }

        if (!IsDisposing && _midiDeviceIndex != int.MinValue)
            ApplyMidiDevice(_midiDeviceIndex);
    }

    /// <summary>Never throws; a failure is logged. Internal for
    /// <c>SandboxNonBlockingCallTests</c>.</summary>
    internal async Task SendSandboxEditorToggleAsync(IVstSandboxChannel host, string slotId, bool open)
    {
        // Editor windows are top-level HWNDs in the child; Windows only
        // lets the child steal foreground if the current foreground
        // process delegates the right.
        if (open && host.ChildProcessId is { } pid && OperatingSystem.IsWindows())
            try { AllowSetForegroundWindow(pid); } catch { /* ignored */ }
        await VstSandboxCalls.TrySendAsync(host,
            new VstSandboxRequest { Op = open ? "showEditor" : "hideEditor", Slot = slotId }, TimeSpan.FromSeconds(20),
            ex => _logger?.Warning(AppLogCategory.Audio, $"Sandbox editor {(open ? "show" : "hide")}({slotId}) failed: {ex.Message}"))
            .ConfigureAwait(false);
    }

    private void OnSandboxEditorClosed(string slotId)
    {
        if (slotId == VstSandboxProtocol.InstrumentSlotId)
        {
            _logger?.Info(AppLogCategory.Audio, "Instrument VST editor (sandbox): closed by user.");
            try { InstrumentVstEditorClosed?.Invoke(this, EventArgs.Empty); } catch { /* ignored */ }
            return;
        }
        if (Guid.TryParse(slotId, out var id))
        {
            _logger?.Info(AppLogCategory.Audio, $"VST3 chain editor (sandbox): slot {id} closed by user.");
            try { GuitarVstSlotEditorClosed?.Invoke(this, id); } catch { /* ignored */ }
        }
    }

    // ------------------------------------------------------------------
    //  Crash detection + auto-restart
    // ------------------------------------------------------------------

    private void OnSandboxCrashed(VstSandboxHost crashed)
    {
        if (IsDisposing || !_outOfProcessVst) return;
        lock (_sandboxGate)
        {
            if (!ReferenceEquals(_sandbox, crashed)) return; // stale instance
        }
        if (Interlocked.Exchange(ref _sandboxRestartInFlight, 1) != 0) return;

        var delay = _sandboxRestartPolicy.OnCrash(_sandboxClock.Elapsed);
        if (delay == null)
        {
            Interlocked.Exchange(ref _sandboxRestartInFlight, 0);
            lock (_sandboxGate) _sandboxGaveUp = true;
            _logger?.Error(AppLogCategory.Audio, _localization["Vst_SandboxGaveUp"]);
            _ = Task.Run(async () =>
            {
                await _vstGate.WaitAsync().ConfigureAwait(false);
                try { await ShutdownSandboxLockedAsync(disarmBridge: true).ConfigureAwait(false); }
                catch (Exception ex) { _logger?.Warning(AppLogCategory.Audio, $"Sandbox shutdown after giving up failed: {ex.Message}"); }
                finally { _vstGate.Release(); }
            });
            return;
        }

        _logger?.Warning(AppLogCategory.Audio, _localization["Vst_SandboxCrashed"]);
        NotifySlotsUnloadedAfterCrash();
        _ = Task.Run(() => RestartSandboxAsync(delay.Value));
    }

    /// <summary>
    /// Raises the "slot unloaded" events for everything the dead child was
    /// hosting — and CLEARS the slot table while doing it.
    /// <para>
    /// It must not raise the events and leave `_sandboxSlots` populated: until
    /// `RestartSandboxAsync` reaches `ShutdownSandboxLockedAsync` (a backoff of up to
    /// several seconds) `IsGuitarVstSlotLoaded(id)` would report TRUE for
    /// a plugin whose process is gone, and a chain apply landing in that window
    /// would see an `existing` entry and SKIP the load. It would converge — the next
    /// `EnsureSandboxLockedAsync` clears the dictionary when it spawns a fresh host —
    /// but the intermediate state is a lie, and one the chain apply acts on.
    /// </para>
    /// </summary>
    private void NotifySlotsUnloadedAfterCrash()
    {
        List<Guid> slots;
        bool instrument;
        lock (_sandboxGate)
        {
            slots = _sandboxSlots.Keys.ToList();
            instrument = _sandboxInstrumentLoaded;
            _sandboxSlots.Clear();
            _sandboxInstrumentLoaded = false;
        }
        foreach (var id in slots)
            try { GuitarVstSlotLoadChanged?.Invoke(this, (id, false)); } catch { /* ignored */ }
        if (instrument)
            try { InstrumentVstLoadChanged?.Invoke(this, false); } catch { /* ignored */ }
    }

    private async Task RestartSandboxAsync(TimeSpan delay)
    {
        const string taskId = "vst-sandbox-restart";
        try
        {
            if (delay > TimeSpan.Zero) await Task.Delay(delay).ConfigureAwait(false);
            if (IsDisposing || !_outOfProcessVst) return;

            _statusBar?.BeginTask(taskId, _localization["Vst_SandboxRestarting"], indeterminate: true);
            await _vstGate.WaitAsync().ConfigureAwait(false);
            try
            {
                if (IsDisposing || !_outOfProcessVst) return;
                // Drop the dead instance + bridge, then bring everything back.
                await ShutdownSandboxLockedAsync(disarmBridge: true).ConfigureAwait(false);
                var engine = _engine;
                if (engine == null) return; // engine down; chain re-applies when it returns

                VstSandboxChainState replay;
                lock (_sandboxGate) replay = _sandboxDesired.Clone();

                await EnsureSandboxLockedAsync(engine).ConfigureAwait(false);
                if (replay.Chain.Count > 0) await ApplySandboxGuitarChainCoreAsync(replay.Chain).ConfigureAwait(false);
                if (!string.IsNullOrEmpty(replay.InstrumentPath))
                {
                    await LoadInstrumentVstSandboxCoreAsync(replay.InstrumentPath, openEditorAfterLoad: false).ConfigureAwait(false);
                    try { InstrumentVstLoadChanged?.Invoke(this, IsInstrumentVstLoaded); } catch { /* ignored */ }
                }
                _logger?.Warning(AppLogCategory.Audio, _localization["Vst_SandboxRestored"]);
            }
            finally
            {
                _vstGate.Release();
            }
        }
        catch (Exception ex)
        {
            _logger?.Error(AppLogCategory.Audio, $"VST sandbox restart failed: {ex.Message}");
            // The new instance may have crashed during replay; its own
            // Exited handler re-enters the crash path with backoff.
        }
        finally
        {
            _statusBar?.CompleteTask(taskId);
            Interlocked.Exchange(ref _sandboxRestartInFlight, 0);
        }
    }

    // ------------------------------------------------------------------
    //  Health monitor (hung-but-alive children)
    // ------------------------------------------------------------------

    // Both of these run under _vstGate (EnsureSandboxLockedAsync / ShutdownSandboxLockedAsync
    // / DisposeSandbox are all gated callers), which is what keeps the
    // null-check-then-assign from racing itself and leaking a live timer.
    private void EnsureSandboxHealthTimer()
    {
        if (_sandboxHealthTimer != null) return;
        var timer = new Timer(3000) { AutoReset = true };
        timer.Elapsed += (_, _) => SandboxHealthTick();
        timer.Start();
        _sandboxHealthTimer = timer;
    }

    private void StopSandboxHealthTimer()
    {
        var timer = _sandboxHealthTimer;
        _sandboxHealthTimer = null;
        if (timer == null) return;
        try { timer.Stop(); timer.Dispose(); } catch { /* ignored */ }
    }

    private void SandboxHealthTick()
    {
        if (IsDisposing || !_outOfProcessVst) return;
        VstSandboxHost? host;
        lock (_sandboxGate) host = _sandbox;
        if (host is not { IsRunning: true }) return;

        var engine = _engine;
        if (engine == null) return;
        try
        {
            if (!engine.TryGetRemoteBridgeStats(out _, out _, out var heartbeat) || heartbeat == 0)
                return; // bridge disarmed / not yet beating

            if (!_sandboxHeartbeatPrimed)
            {
                _sandboxHeartbeatPrimed = true;
                _sandboxLastHeartbeat = heartbeat;
                return;
            }
            if (heartbeat != _sandboxLastHeartbeat)
            {
                _sandboxLastHeartbeat = heartbeat;
                _sandboxRestartPolicy.OnHealthy(_sandboxClock.Elapsed);
                return;
            }
            // The worker beats ~10x/s even when idle; two identical readings
            // 3 s apart mean the child is alive but wedged. Kill it — the
            // Exited handler takes the normal crash/restart path.
            _logger?.Warning(AppLogCategory.Audio,
                "VST sandbox heartbeat stalled — terminating the wedged child for restart.");
            _sandboxHeartbeatPrimed = false;
            try { host.Kill(); } catch { /* ignored */ }
            OnSandboxCrashed(host);
        }
        catch { /* stats are best-effort */ }
    }

    // ------------------------------------------------------------------
    //  Dispose hook
    // ------------------------------------------------------------------

    /// <summary>
    /// Called from <c>Dispose()</c>, which is synchronous — so this is the one
    /// sandbox path that WAITS for the child. It is bounded: the host's stop is
    /// 3 s for "quit" plus a 5 s grace before it kills, and
    /// <see cref="VstSandboxCalls.StopForDispose"/> kills outright at 10 s. Not
    /// fire-and-forget on purpose: the app may be exiting, and an un-awaited
    /// stop would leave the child to its own parent-death watchdog.
    /// </summary>
    private void DisposeSandbox()
    {
        StopSandboxHealthTimer();
        VstSandboxHost? host;
        lock (_sandboxGate)
        {
            host = _sandbox;
            _sandbox = null;
        }
        if (host == null) return;
        try { _engine?.SetRemoteBridge(null, null, null, false); } catch { /* ignored */ }
        VstSandboxCalls.StopForDispose(host, gracePeriod: TimeSpan.FromSeconds(5), bound: TimeSpan.FromSeconds(10));
        try { host.Dispose(); } catch { /* ignored */ }
    }
}
