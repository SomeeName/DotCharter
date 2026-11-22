// Partial: per-seat sandbox routing, mode switching and recovery replay (ABI v50).
using System.Runtime.InteropServices;
using DotCharter.Tone.Audio.Midi;
using DotCharter.Tone.Audio.Vst.Sandbox;
using JuceSharp;

namespace DotCharter.Tone.Audio;

public sealed partial class JuceAudioPlaybackService
{
    // Feature flag (RigConfig.OutOfProcessVst). Flipped by ApplySandboxMode;
    // read on many paths, hence volatile.
    private volatile bool _outOfProcessVst;
    private readonly Func<IJuceAudioEngine?> _sandboxEngine;
    private readonly Func<IVstSandboxHost> _sandboxHostFactory = () => new VstSandboxHost();
    private readonly Func<TimeSpan, Task> _sandboxRestartDelay = delay => Task.Delay(delay);
    private readonly IVstSandboxStopWait _sandboxStopWait = new VstSandboxStopWait();
    private readonly TimeSpan _sandboxHealthInterval = VstSandboxRecoveryLink.DefaultHealthInterval;

    internal JuceAudioPlaybackService(DotCharter.Localization.ILocalizationService localization,
        Func<IJuceAudioEngine?> engine, Func<IVstSandboxHost> createHost,
        Func<TimeSpan, Task>? restartDelay, IVstSandboxStopWait? stopWait,
        IAppLogger? logger, TimeSpan? healthInterval = null) : this(localization, logger)
    {
        _sandboxEngine = engine;
        _sandboxHostFactory = createHost;
        if (restartDelay != null) _sandboxRestartDelay = restartDelay;
        if (stopWait != null) _sandboxStopWait = stopWait;
        if (healthInterval != null) _sandboxHealthInterval = healthInterval.Value;
        _outOfProcessVst = true;
    }

    // Each context owns its live slots, desired state and recovery policy.
    private readonly object _sandboxGate = new();
    private readonly VstSandboxSeat[] _sandboxSeats;
    private bool SandboxInstrumentLoaded => _sandboxSeats[0].InstrumentLoaded;

    private VstSandboxSeat[] CreateSandboxSeats()
    {
        return Enumerable.Range(0, JuceAudioEngine.MaxPlayers).Select(player =>
        {
            var seat = new VstSandboxSeat(player, _sandboxGate, () => _sandboxEngine(), () => _sandboxHostFactory(), _logger);
            seat.EditorClosed += slot => OnSandboxEditorClosed(seat, slot);
            seat.Recovery = new VstSandboxRecovery(_sandboxGate, seat.Desired, _vstGate, _logger, _localization, _statusBar,
                new VstSandboxRecoveryLink
                {
                    TaskId = $"vst-sandbox-restart-{player}",
                    IsActive = () => !IsDisposing && _outOfProcessVst && !seat.Desired.IsEmpty,
                    IsCurrentHost = host => { lock (_sandboxGate) return ReferenceEquals(seat.Host, host); },
                    MarkGaveUp = () => { lock (_sandboxGate) seat.GaveUp = true; },
                    NotifySlotsUnloaded = () => NotifySlotsUnloadedAfterCrash(seat),
                    LiveSlotIds = seat.LiveSlotIds,
                    CurrentHost = () => { lock (_sandboxGate) return seat.Host; },
                    ReadHeartbeat = seat.ReadHeartbeat,
                    HealthInterval = () => _sandboxHealthInterval,
                    ShutdownLocked = () => seat.ShutdownAsync(disarmBridge: true),
                    RebuildLocked = () => RebuildSandboxLockedAsync(seat),
                }, delay => _sandboxRestartDelay(delay));
            return seat;
        }).ToArray();
    }

    private VstSandboxSeat? SandboxSlotOwner(Guid id) => _sandboxSeats.FirstOrDefault(seat => seat.Slots.ContainsKey(id));

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
                    .OrderBy(kv => _guitarChainOrder.IndexOf(kv.Key))
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
            foreach (var seat in _sandboxSeats)
            {
                seat.Recovery.ResetBudget();
                lock (_sandboxGate) seat.GaveUp = false;
            }
        }
        else
        {
            lock (_sandboxGate)
            {
                chain = _sandboxSeats.SelectMany(seat => seat.Desired.Chain).Select(e => new VstChainEntry
                { Id = e.Id, PluginPath = e.PluginPath, Bypass = e.Bypass, Player = e.Player }).ToList();
                instrumentPath = _sandboxSeats[0].Desired.InstrumentPath;
            }
            _outOfProcessVst = false;
            _ = Task.Run(async () =>
            {
                if (!await _vstGate.TryEnterAsync().ConfigureAwait(false)) return;
                try { await ShutdownSandboxLockedAsync(disarmBridge: true).ConfigureAwait(false); }
                catch (Exception ex) { _logger?.Warning(AppLogCategory.Audio, "Sandbox shutdown on mode switch failed", ex); }
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
            if (!await _vstGate.TryEnterAsync().ConfigureAwait(false)) return;
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

    /// <summary>Stops all seat children and disarms every seat. Caller holds _vstGate.</summary>
    private Task ShutdownSandboxLockedAsync(bool disarmBridge)
        => VstSandboxShutdown.StopAsync(_sandboxSeats, disarmBridge, _sandboxStopWait);

    /// <summary>Re-arms surviving children synchronously; pipe preparation waits behind _vstGate.</summary>
    private void RearmSandboxAfterEngineSwapLocked(IJuceAudioEngine engine)
    {
        foreach (var seat in _sandboxSeats) seat.Rearm(engine);
        NotifySandboxEngineReconfigured();
        if (SandboxInstrumentLoaded && engine is JuceAudioEngine native) RebindSandboxInstrumentMidi(native);
    }

    private async Task SendSandboxPrepareAsync(IVstSandboxChannel host)
    {
        var sr = _sandboxEngine()?.SampleRate ?? 0;
        var blk = _sandboxEngine()?.BufferFrames ?? 0;
        if (sr <= 0 || blk <= 0) return;
        await VstSandboxCalls.TrySendAsync(host,
            new VstSandboxRequest { Op = "prepare", Sr = sr, Block = blk }, TimeSpan.FromSeconds(30),
            ex => _logger?.Warning(AppLogCategory.Audio, $"Sandbox prepare({sr}/{blk}) failed", ex))
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
            if (!await _vstGate.TryEnterAsync().ConfigureAwait(false)) return;
            try
            {
                foreach (var seat in _sandboxSeats)
                {
                    IVstSandboxHost? host;
                    lock (_sandboxGate) host = seat.Host;
                    if (host is { IsRunning: true } && _sandboxEngine() != null)
                        await SendSandboxPrepareAsync(host).ConfigureAwait(false);
                }
            }
            catch (Exception ex) { _logger?.Warning(AppLogCategory.Audio, "Sandbox prepare after a device change failed", ex); }
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
        var engine = _sandboxEngine();
        if (engine == null) return;
        if (entries.Any(e => e.Player < 0 || e.Player >= _sandboxSeats.Length))
            throw new ArgumentOutOfRangeException(nameof(entries));

        await VstSandboxChainEdit.ApplyAsync(_sandboxGate, _sandboxSeats, entries,
            UnloadSandboxSlotAsync, ApplySandboxSeatChainCoreAsync, _logger).ConfigureAwait(false);
    }

    private async Task UnloadSandboxSlotAsync(VstSandboxSeat seat, Guid id, bool removedEntirely)
    {
        if (seat.Host is { IsRunning: true } host)
        {
            var reply = await VstSandboxCalls.TrySendAsync(host,
                new VstSandboxRequest { Op = "unload", Slot = id.ToString() }, TimeSpan.FromSeconds(30),
                ex => _logger?.Warning(AppLogCategory.Audio, $"Sandbox unload({id}) failed", ex)).ConfigureAwait(false);
            if (reply is not { Ok: true })
                throw new IOException($"Sandbox unload({id}) was not acknowledged: {reply?.Error ?? "no reply"}.");
        }
        lock (_sandboxGate)
        {
            seat.Slots.Remove(id);
            seat.Desired.PluginStates.Remove(id.ToString());
        }
        if (removedEntirely)
            lock (_gate) _guitarPendingEditorOpen.Remove(id);
        try { GuitarVstSlotLoadChanged?.Invoke(this, (id, false)); } catch { /* ignored */ }
    }

    private async Task ApplySandboxSeatChainCoreAsync(VstSandboxSeat seat, IReadOnlyList<VstChainEntry> entries)
    {
        if (entries.Count == 0)
        {
            await seat.StopIfEmptyAsync().ConfigureAwait(false);
            return;
        }
        var engine = _sandboxEngine();
        if (engine == null) return;
        var host = await seat.EnsureAsync(engine).ConfigureAwait(false);

        // Phase 2: load new slots / sync bypass on surviving ones.
        foreach (var entry in entries)
        {
            (string Path, bool Bypass, int Player)? existing;
            lock (_sandboxGate)
                existing = seat.Slots.TryGetValue(entry.Id, out var e) ? e : null;

            if (existing is { } live)
            {
                if (live.Bypass != entry.Bypass)
                {
                    var ack = await VstSandboxCalls.TrySendAsync(host,
                        new VstSandboxRequest { Op = "bypass", Slot = entry.Id.ToString(), Value = entry.Bypass },
                        TimeSpan.FromSeconds(10),
                        ex => _logger?.Warning(AppLogCategory.Audio, $"Sandbox bypass({entry.Id}) failed", ex))
                        .ConfigureAwait(false);
                    if (ack != null)
                        lock (_sandboxGate) seat.Slots[entry.Id] = (live.Path, entry.Bypass, live.Player);
                }
                continue;
            }

            if (seat.RejectedLoads.ShouldSkip(entry, _logger)) continue;

            // 180 s: a cold first-run scan of a big amp sim (BIAS FX, Helix)
            // takes tens of seconds. Awaited — no thread waits for it.
            var response = await VstSandboxCalls.TrySendAsync(host, VstSandboxChainPlan.LoadRequest(entry),
                TimeSpan.FromSeconds(180),
                ex => _logger?.Error(AppLogCategory.Audio, $"VST3 chain (sandbox): failed to load '{entry.PluginPath}'", ex))
                .ConfigureAwait(false);
            if (response == null) continue;
            if (response is not { Ok: true })
            {
                seat.RejectedLoads.Remember(entry, response);
                _logger?.Error(AppLogCategory.Audio,
                    $"VST3 chain (sandbox): child rejected '{entry.PluginPath}': {response.Error ?? "unknown error"}");
                continue;
            }

            bool pendingEditorOpen;
            lock (_sandboxGate) seat.Slots[entry.Id] = (entry.PluginPath, entry.Bypass, entry.Player);
            if (!seat.IsReplaying)
                await seat.Recovery.ReplayStateAsync(host, SnapshotSandboxSlotState(seat, entry.Id)).ConfigureAwait(false);
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
            ex => _logger?.Warning(AppLogCategory.Audio, "Sandbox reorder failed", ex))
            .ConfigureAwait(false);

        await seat.StopIfEmptyAsync().ConfigureAwait(false);
    }

    private Dictionary<string, string> SnapshotSandboxSlotState(VstSandboxSeat seat, Guid id)
    {
        lock (_sandboxGate)
        {
            var slot = id.ToString();
            return seat.Desired.PluginStates.TryGetValue(slot, out var state)
                ? new Dictionary<string, string> { [slot] = state } : [];
        }
    }

    /// <summary>Sandbox counterpart of the LoadInstrumentVst worker body. Caller holds _vstGate.</summary>
    private async Task LoadInstrumentVstSandboxCoreAsync(string? pluginPath, bool openEditorAfterLoad)
    {
        var engine = _sandboxEngine();
        if (engine == null) return;

        if (string.IsNullOrWhiteSpace(pluginPath))
        {
            await UnloadSandboxInstrumentLockedAsync().ConfigureAwait(false);
            return;
        }

        var seat = _sandboxSeats[0];
        var host = await seat.EnsureAsync(engine).ConfigureAwait(false);

        bool alreadyLoaded;
        lock (_sandboxGate)
        {
            alreadyLoaded = SandboxInstrumentLoaded
                && string.Equals(_sandboxSeats[0].Desired.InstrumentPath, pluginPath, StringComparison.OrdinalIgnoreCase);
        }

        if (alreadyLoaded)
        {
            if (openEditorAfterLoad)
                await SendSandboxEditorToggleAsync(host, VstSandboxProtocol.InstrumentSlotId, open: true).ConfigureAwait(false);
            return;
        }

        var response = await VstSandboxCalls.TrySendAsync(host,
            new VstSandboxRequest { Op = "loadInstrument", Path = pluginPath }, TimeSpan.FromSeconds(180),
            ex => _logger?.Warning(AppLogCategory.Audio, $"Failed to load instrument VST3 (sandbox) '{pluginPath}'", ex))
            .ConfigureAwait(false);
        if (response == null)
        {
            await seat.StopIfEmptyAsync().ConfigureAwait(false);
            return;
        }
        if (response is not { Ok: true })
        {
            _logger?.Warning(AppLogCategory.Audio,
                $"Instrument VST3 (sandbox) rejected '{pluginPath}': {response.Error ?? "unknown error"}");
            await seat.StopIfEmptyAsync().ConfigureAwait(false);
            return;
        }

        lock (_sandboxGate)
        {
            _sandboxSeats[0].InstrumentLoaded = true;
            _sandboxSeats[0].Desired.InstrumentPath = pluginPath;
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
        if (engine is JuceAudioEngine native) RebindSandboxInstrumentMidi(native);

        if (openEditorAfterLoad || _instrumentPendingEditorOpen)
            await SendSandboxEditorToggleAsync(host, VstSandboxProtocol.InstrumentSlotId, open: true).ConfigureAwait(false);
        _instrumentPendingEditorOpen = false;
    }

    /// <summary>Caller holds _vstGate.</summary>
    private async Task UnloadSandboxInstrumentLockedAsync()
    {
        IVstSandboxHost? host;
        bool wasLoaded;
        lock (_sandboxGate)
        {
            host = _sandboxSeats[0].Host;
            wasLoaded = SandboxInstrumentLoaded;
            _sandboxSeats[0].InstrumentLoaded = false;
            _sandboxSeats[0].Desired.InstrumentPath = null;
        }
        if (!wasLoaded)
        {
            await _sandboxSeats[0].StopIfEmptyAsync().ConfigureAwait(false);
            return;
        }

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
                ex => _logger?.Warning(AppLogCategory.Audio, "Sandbox instrument unload failed", ex))
                .ConfigureAwait(false);
        }

        await _sandboxSeats[0].StopIfEmptyAsync().ConfigureAwait(false);
        if (!IsDisposing && _midiDeviceIndex != int.MinValue)
            ApplyMidiDevice(_midiDeviceIndex);
    }

    private async Task SendSandboxOwnedEditorToggleAsync(string slotId, bool open)
    {
        if (!await _vstGate.TryEnterAsync().ConfigureAwait(false)) return;
        try
        {
            IVstSandboxHost? host;
            lock (_sandboxGate)
            {
                host = slotId == VstSandboxProtocol.InstrumentSlotId ? _sandboxSeats[0].Host
                    : Guid.TryParse(slotId, out var id) ? SandboxSlotOwner(id)?.Host : null;
            }
            if (_outOfProcessVst && host is { IsRunning: true })
                await SendSandboxEditorToggleAsync(host, slotId, open).ConfigureAwait(false);
        }
        finally { _vstGate.Release(); }
    }

    private async Task SendSandboxOwnedBypassAsync(Guid id)
    {
        if (!await _vstGate.TryEnterAsync().ConfigureAwait(false)) return;
        try
        {
            IVstSandboxHost? host;
            bool bypass;
            lock (_sandboxGate)
            {
                var seat = SandboxSlotOwner(id);
                if (seat == null) return;
                host = seat.Host;
                var entry = seat.Slots[id];
                bypass = entry.Bypass;
            }
            if (_outOfProcessVst && host is { IsRunning: true })
            {
                await VstSandboxCalls.TrySendAsync(host,
                    new VstSandboxRequest { Op = "bypass", Slot = id.ToString(), Value = bypass }, TimeSpan.FromSeconds(10),
                    ex => _logger?.Warning(AppLogCategory.Audio, $"Sandbox bypass({id}) failed", ex)).ConfigureAwait(false);
            }
        }
        finally { _vstGate.Release(); }
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
            ex => _logger?.Warning(AppLogCategory.Audio, $"Sandbox editor {(open ? "show" : "hide")}({slotId}) failed", ex))
            .ConfigureAwait(false);
    }

    private void OnSandboxEditorClosed(VstSandboxSeat seat, string slotId)
    {
        if (seat.Host is { } running) seat.Recovery.CaptureStateInBackground(running);
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

    /// <summary>Clears and reports only the failed child's live slots.</summary>
    private void NotifySlotsUnloadedAfterCrash(VstSandboxSeat seat)
    {
        var (slots, instrument) = seat.ClearLive();
        foreach (var id in slots)
            try { GuitarVstSlotLoadChanged?.Invoke(this, (id, false)); } catch { /* ignored */ }
        if (instrument)
            try { InstrumentVstLoadChanged?.Invoke(this, false); } catch { /* ignored */ }
    }

    /// <summary>Rebuilds and replays a single seat. Caller holds _vstGate.</summary>
    private async Task<(IVstSandboxHost Host, IReadOnlyDictionary<string, string> States)?> RebuildSandboxLockedAsync(VstSandboxSeat seat)
    {
        await seat.ShutdownAsync(disarmBridge: true).ConfigureAwait(false);
        var engine = _sandboxEngine();
        if (engine == null) return null;
        VstSandboxChainState replay;
        lock (_sandboxGate) replay = seat.Desired.Clone();
        if (replay.IsEmpty) return null;

        seat.IsReplaying = true;
        try
        {
            var host = await seat.EnsureAsync(engine).ConfigureAwait(false);
            if (replay.Chain.Count > 0) await ApplySandboxSeatChainCoreAsync(seat, replay.Chain).ConfigureAwait(false);
            if (seat.Player == 0 && !string.IsNullOrEmpty(replay.InstrumentPath))
            {
                await LoadInstrumentVstSandboxCoreAsync(replay.InstrumentPath, openEditorAfterLoad: false).ConfigureAwait(false);
                try { InstrumentVstLoadChanged?.Invoke(this, IsInstrumentVstLoaded); } catch { /* ignored */ }
            }
            return (host, replay.PluginStates);
        }
        finally
        {
            seat.IsReplaying = false;
            await seat.StopIfEmptyAsync().ConfigureAwait(false);
        }
    }

    /// <summary>Synchronous, bounded teardown of every seat before the engine is destroyed.</summary>
    private void DisposeSandbox() => VstSandboxShutdown.Dispose(_sandboxSeats, _sandboxStopWait);
}
