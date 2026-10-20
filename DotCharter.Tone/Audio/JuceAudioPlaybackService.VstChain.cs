// Partial: guitar VST chain + instrument VST — see JuceAudioPlaybackService.cs for the shared state and locking rules.
using DotCharter.Tone.Audio.Midi;
using JuceSharp;

namespace DotCharter.Tone.Audio;

public sealed partial class JuceAudioPlaybackService
{
    public bool IsInstrumentVstLoaded => _instrumentPlugin != null || _sandboxInstrumentLoaded;
    public bool IsInstrumentVstLoading
        => _instrumentLoadInFlight || _instrumentReloadPending || _pendingInstrumentLoadPath != null;
    public bool IsGuitarVstSlotLoaded(Guid id)
    {
        if (_outOfProcessVst)
        {
            lock (_sandboxGate) return _sandboxSlots.ContainsKey(id);
        }
        lock (_gate)
            return _guitarChain.ContainsKey(id);
    }

    public List<VstPluginInfo> ScanVstPlugins()
    {
        // VST3 bundles live in well-known system directories on Windows. We
        // just walk those for *.vst3 entries (both directory bundles and
        // single-file modules). Loading each plugin to read its metadata is
        // expensive (and not all plugins survive a probe-load), so for now
        // we return the path + display name only and let the user pick.
        var roots = new List<string>();
        foreach (var env in new[] { "CommonProgramFiles", "ProgramFiles", "ProgramFiles(x86)" })
        {
            var p = Environment.GetEnvironmentVariable(env);
            if (!string.IsNullOrWhiteSpace(p))
            {
                var v = Path.Combine(p, "VST3");
                if (Directory.Exists(v)) roots.Add(v);
            }
        }
        var local = UserDataFolders.OtherProgramsLocalAppData;
        if (!string.IsNullOrEmpty(local))
        {
            var v = Path.Combine(local, "Programs", "Common", "VST3");
            if (Directory.Exists(v)) roots.Add(v);
        }

        var result = new List<VstPluginInfo>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var root in roots)
        {
            try
            {
                // Directory-style bundles (the modern .vst3 layout) and the rare
                // single-file modules.
                foreach (var entry in Directory.EnumerateFileSystemEntries(root, "*.vst3", SearchOption.AllDirectories))
                {
                    if (!seen.Add(entry)) continue;
                    var name = Path.GetFileNameWithoutExtension(entry);
                    result.Add(new VstPluginInfo
                    {
                        Name = name,
                        PluginPath = entry,
                        Vendor = string.Empty,
                        IsEffect = true,
                    });
                }
            }
            catch (Exception ex)
            {
                _logger?.Warning(AppLogCategory.Audio, $"VST3 scan skipped '{root}': {ex.Message}");
            }
        }
        result.Sort((a, b) => string.Compare(a.Name, b.Name, StringComparison.OrdinalIgnoreCase));
        return result;
    }

    public void SetGuitarVstChain(IReadOnlyList<VstChainEntry> entries)
    {
        // Take an immutable snapshot so the worker thread doesn't race with
        // further UI edits before it runs.
        var snapshot = entries.Select(e => new VstChainEntry
        {
            Id = e.Id,
            PluginPath = e.PluginPath,
            Bypass = e.Bypass,
            // ABI v28: a slot's SEAT travels with it. Dropping it here would
            // silently re-seat every per-player slot onto player 1 the moment
            // anything re-applied the chain — the same shape of bug as a model
            // field with no writer.
            Player = e.Player,
        }).ToList();

        // Engine may not exist yet at startup (settings restore happens on a
        // worker that can fire before InitializeAudioEngineAsync completes).
        // Queue the chain so ReplaceEngine can apply it once the engine
        // comes up; otherwise the user would see an empty FX chain even
        // though their saved chain is non-empty.
        //
        // Under _gate because this is a check-then-act against _engine, and the
        // drain (ReplaceEngineGated) runs on the engine worker. Unsynchronized,
        // the restore worker could read _engine == null, the engine worker could
        // then publish _engine and drain a still-empty queue, and only then would
        // the restore worker write _pendingGuitarChain — which nothing reads
        // again. The user's saved FX chain silently never loads, which is the
        // exact bug this queue was added to fix.
        lock (_gate)
        {
            if (_engine == null)
            {
                _pendingGuitarChain = snapshot;
                _logger?.Info(AppLogCategory.Audio,
                    $"VST3 chain queued ({snapshot.Count} slot(s)): audio engine not yet initialised.");
                return;
            }
        }

        const string taskId = "guitar-vst-chain-apply";
        _statusBar?.BeginTask(taskId,
            snapshot.Count == 0
                ? _localization["Progress_ClearingGuitarVstChain"]
                : _localization.Plural("Progress_LoadingGuitarVstChainOne", "Progress_LoadingGuitarVstChain", snapshot.Count),
            indeterminate: true);
        _ = Task.Run(async () =>
        {
            await _vstGate.WaitAsync().ConfigureAwait(false);
            try
            {
                // Out-of-process mode routes the whole chain into the sandbox
                // child; in-proc otherwise. Both diff against their live state.
                if (_outOfProcessVst) await ApplySandboxGuitarChainCoreAsync(snapshot).ConfigureAwait(false);
                else ApplyGuitarVstChainCore(snapshot);
            }
            catch (Exception ex) { _logger?.Error(AppLogCategory.Audio, $"VST3 chain apply failed: {ex.Message}"); }
            finally
            {
                _vstGate.Release();
                _statusBar?.CompleteTask(taskId);
            }
        });
    }

    private void ApplyGuitarVstChainCore(IReadOnlyList<VstChainEntry> entries)
    {
        // Snapshot the engine once. It cannot be swapped out from under us:
        // our caller holds _vstGate and ReplaceEngineCore now acquires the
        // same gate before tearing the engine down.
        var engine = _engine;
        if (engine == null) return;

        // Phase 1 (locked): diff against the live chain. Detach the slots that
        // are leaving from the dictionary and collect the entries that still
        // need a native load. NOTHING native happens under the lock.
        var toLoad = new List<VstChainEntry>();
        var toUnload = new List<(Guid Id, JuceAudioPlugin Plugin)>();
        var toReseat = new List<(JuceAudioPlugin Plugin, int Player, string Path)>();
        var toBypass = new List<(JuceAudioPlugin Plugin, bool Bypass, string Path, int Player)>();
        lock (_gate)
        {
            foreach (var kv in _guitarChain.ToList())
            {
                var desired = entries.FirstOrDefault(e => e.Id == kv.Key);
                if (desired == null || !string.Equals(desired.PluginPath, kv.Value.Path, StringComparison.OrdinalIgnoreCase))
                {
                    // Removed from the map HERE so nothing can reach the slot
                    // again; the actual teardown happens unlocked below.
                    _guitarChain.Remove(kv.Key);
                    _guitarSlotPlayer.Remove(kv.Key);
                    _guitarSlotBypass.Remove(kv.Key);
                    if (desired == null) _guitarPendingEditorOpen.Remove(kv.Key);
                    toUnload.Add((kv.Key, kv.Value));
                }
            }

            foreach (var entry in entries)
            {
                if (_guitarChain.TryGetValue(entry.Id, out var existing))
                {
                    // A re-SEATED slot (ABI v28) is collected for an unlocked
                    // re-attach: dca_plugin_attach_to_input_for_player MOVES an
                    // already-attached plugin, and its native detach half waits
                    // out an in-flight audio block — which must never happen
                    // under _gate.
                    var wasPlayer = _guitarSlotPlayer.GetValueOrDefault(entry.Id);
                    // The app's OWN record of this slot's bypass, never
                    // `existing.Bypass`: the wrapper's property answers "not in
                    // any seat's input chain", so a slot whose attach threw — or
                    // one still parked from a load — reads bypassed when the app
                    // never asked for that, and both branches below would then
                    // act on a state nobody requested.
                    var wasBypass = _guitarSlotBypass.GetValueOrDefault(entry.Id);
                    if (wasPlayer != entry.Player)
                    {
                        _guitarSlotPlayer[entry.Id] = entry.Player;
                        // Only a slot that is ACTUALLY ATTACHED can be re-seated
                        // here: AttachToInput on a bypassed plugin would put it
                        // back in the signal path while its wrapper still says
                        // bypassed. A bypassed slot's new seat is remembered in
                        // _guitarSlotPlayer above and asserted at the un-bypass
                        // below, which is the only moment it can be.
                        if (!entry.Bypass && !wasBypass) toReseat.Add((existing, entry.Player, entry.PluginPath));
                    }

                    // A bypass TOGGLE is native too — the setter P/Invokes
                    // PluginDetachFromInput or PluginAttachToInputForPlayer —
                    // so it is collected for the same unlocked phase as the
                    // re-seats, and for the same reason. (At v28 the detach
                    // walks every seat in turn.) The SEAT travels with it: see
                    // the apply loop for why it has to.
                    if (wasBypass != entry.Bypass)
                    {
                        _guitarSlotBypass[entry.Id] = entry.Bypass;
                        toBypass.Add((existing, entry.Bypass, entry.PluginPath, entry.Player));
                    }

                    continue;
                }

                if (string.IsNullOrWhiteSpace(entry.PluginPath) || !File.Exists(entry.PluginPath) && !Directory.Exists(entry.PluginPath))
                {
                    _logger?.Warning(AppLogCategory.Audio, $"VST3 chain: missing plugin '{entry.PluginPath}'.");
                    continue;
                }

                toLoad.Add(entry);
            }
        }

        // Phase 1b (unlocked): native UNLOADS. Tearing a VST3 down is as slow
        // as loading one — the host has to leave the plugin's own shutdown, and
        // an editor teardown, before returning — and this must not run inside
        // _gate, which six UI-thread setters (volume, mute, speed, …) contend
        // on: removing one big amp sim would freeze the UI for as long as
        // the plugin takes to close, and violate the service's own rule that
        // _gate is never held across a P/Invoke. The slots are already out of
        // the dictionary, so nobody else can touch them.
        foreach (var (id, plugin) in toUnload)
        {
            try { plugin.DetachFromInput(); } catch { /* ignored */ }
            try { plugin.Dispose(); } catch { /* ignored */ }
            // Raised unlocked too: a subscriber that calls back into the
            // service would otherwise re-enter _gate from inside it.
            try { GuitarVstSlotLoadChanged?.Invoke(this, (id, false)); } catch { /* ignored */ }
        }

        // Phase 1c (unlocked): RE-SEAT slots that changed player (ABI v28), and
        // apply BYPASS toggles. Native attach-to-a-different-seat detaches
        // first, and that detach is a wait-one-block barrier — same reason the
        // unloads above are unlocked. `JuceAudioPlugin.Bypass`'s setter is the
        // same barrier through the same two P/Invokes, so clicking Bypass used
        // to stall whichever UI-thread member was waiting on _gate (Play, Pause,
        // Stop and SeekTo all reach it through SnapshotStems()) for tens of ms —
        // against a 150 ms budget, in the one function that otherwise enforces
        // this rule carefully.
        foreach (var (plugin, player, path) in toReseat)
        {
            try
            {
                plugin.AttachToInput(player);
                _logger?.Info(AppLogCategory.Audio,
                    $"VST3 chain: moved '{Path.GetFileName(path)}' to player {player + 1}'s input chain.");
            }
            catch (Exception ex)
            {
                _logger?.Error(AppLogCategory.Audio,
                    $"VST3 chain: re-seating '{path}' onto player {player + 1} failed: {ex.Message}");
            }
        }

        foreach (var (plugin, bypass, path, player) in toBypass)
        {
            try
            {
                // UN-bypassing must say the seat FIRST. `JuceAudioPlugin` learns
                // its seat only from AttachToInput, and a slot that was LOADED
                // while bypassed (the load path sets Bypass and never attaches)
                // or RE-SEATED while bypassed (skipped above) never called it —
                // so the Bypass setter's own re-attach would land on seat 0.
                // Player 2's amp sim would join player 1's input chain: player 1
                // hears a doubled chain, player 2 hears nothing, and the next
                // diff never corrects it because _guitarSlotPlayer already says
                // the right seat.
                if (!bypass) plugin.AttachToInput(player);
                plugin.Bypass = bypass;
            }
            catch (Exception ex)
            {
                _logger?.Error(AppLogCategory.Audio,
                    $"VST3 chain: {(bypass ? "bypassing" : "un-bypassing")} '{path}' failed: {ex.Message}");
            }
        }

        // Phase 2 (unlocked): native loads. Big amp sims (Helix, BIAS) spend
        // multiple seconds inside LoadPlugin; holding _gate across that used
        // to block SwapMasterSourceLocked — at startup the song "decode"
        // was really just waiting here for the whole Helix load. Loads stay
        // strictly serialized by _vstGate (held by our caller), which also
        // keeps ReplaceEngineCore from destroying the engine mid-load.
        foreach (var entry in toLoad)
        {
            JuceAudioPlugin plugin;
            try { plugin = engine.LoadPlugin(entry.PluginPath); }
            catch (Exception ex)
            {
                _logger?.Error(AppLogCategory.Audio, $"VST3 chain: failed to load '{entry.PluginPath}': {ex.Message}");
                continue;
            }
            try
            {
                // ABI v28: the slot declares which seat's input chain it
                // processes. Player 0 is the single-player chain, unchanged.
                if (!entry.Bypass) plugin.AttachToInput(entry.Player);
                else plugin.Bypass = true; // syncs wrapper state; native detach is a no-op when not attached
            }
            catch (Exception ex)
            {
                _logger?.Error(AppLogCategory.Audio, $"VST3 chain: attach failed for '{entry.PluginPath}': {ex.Message}");
                try { plugin.Dispose(); } catch { /* ignored */ }
                continue;
            }

            bool pendingEditorOpen;
            lock (_gate)
            {
                _guitarChain[entry.Id] = plugin;
                _guitarSlotPlayer[entry.Id] = entry.Player;
                // The intent this slot was loaded WITH. Recorded next to the
                // seat and for the same reason: the plugin cannot be asked.
                _guitarSlotBypass[entry.Id] = entry.Bypass;
                pendingEditorOpen = _guitarPendingEditorOpen.Remove(entry.Id);
            }

            // Relay native X-button closes so the slot's editor toggle
            // doesn't go stale (stale = the next click hides a window
            // that is gone instead of reopening it).
            var slotId = entry.Id;
            plugin.EditorClosedByUser += () =>
            {
                _logger?.Info(AppLogCategory.Audio, $"VST3 chain editor: slot {slotId} closed by user.");
                try { GuitarVstSlotEditorClosed?.Invoke(this, slotId); } catch { /* ignored */ }
            };
            _logger?.Info(AppLogCategory.Audio, $"VST3 chain: added '{Path.GetFileName(entry.PluginPath)}' (bypassed={entry.Bypass}).");
            try { GuitarVstSlotLoadChanged?.Invoke(this, (entry.Id, true)); } catch { /* ignored */ }

            if (pendingEditorOpen)
            {
                var pluginSnapshot = plugin;
                _logger?.Info(AppLogCategory.Audio,
                    $"VST3 chain editor: opening slot {entry.Id} from deferred toggle (plugin finished loading).");
                _ = Task.Run(() =>
                {
                    try
                    {
                        if (_engine != null) ShowPluginEditorForegrounded(pluginSnapshot);
                    }
                    catch (Exception ex)
                    {
                        _logger?.Warning(AppLogCategory.Audio, $"Deferred VST3 chain editor open failed: {ex.Message}");
                    }
                });
            }
        }
    }

    public void ToggleGuitarVstSlotEditor(Guid id, bool open)
    {
        if (_outOfProcessVst)
        {
            Vst.Sandbox.VstSandboxHost? host;
            bool loaded;
            lock (_sandboxGate)
            {
                host = _sandbox;
                loaded = _sandboxSlots.ContainsKey(id);
            }
            if (!loaded || host is not { IsRunning: true })
            {
                lock (_gate)
                {
                    if (open) _guitarPendingEditorOpen.Add(id);
                    else _guitarPendingEditorOpen.Remove(id);
                }
                _logger?.Warning(AppLogCategory.Audio,
                    $"VST3 chain editor toggle deferred (sandbox): slot {id} not loaded yet (will auto-{(open ? "open" : "stay-closed")} on completion).");
                return;
            }
            var hostSnapshot = host;
            _ = Task.Run(() => SendSandboxEditorToggleAsync(hostSnapshot, id.ToString(), open));
            return;
        }

        JuceAudioPlugin? plugin;
        bool slotKnown;
        lock (_gate)
        {
            slotKnown = _guitarChain.TryGetValue(id, out plugin);
            if (!slotKnown || plugin == null)
            {
                // Slot not (yet) loaded - chain apply runs on a worker and
                // can take seconds for big amp sims like BIAS FX / Helix.
                // Remember the intent so the editor pops up automatically
                // once ApplyGuitarVstChainCore finishes adding the slot.
                if (open) _guitarPendingEditorOpen.Add(id);
                else _guitarPendingEditorOpen.Remove(id);
            }
        }
        if (_engine == null)
        {
            _logger?.Warning(AppLogCategory.Audio,
                $"VST3 chain editor toggle ignored: engine not initialised (slot {id}).");
            return;
        }
        if (!slotKnown || plugin == null)
        {
            _logger?.Warning(AppLogCategory.Audio,
                $"VST3 chain editor toggle deferred: slot {id} not loaded yet (will auto-{(open ? "open" : "stay-closed")} on completion).");
            return;
        }

        // Editor toggle dispatches into the JuceHost message thread inside
        // the native DLL, which owns its own message loop. Run on a worker
        // so we never block the Avalonia UI thread while the plugin builds
        // its window.
        var pluginSnapshot = plugin;
        _ = Task.Run(() =>
        {
            try
            {
                _logger?.Info(AppLogCategory.Audio,
                    $"VST3 chain editor: {(open ? "show" : "hide")} slot {id}.");
                if (open) ShowPluginEditorForegrounded(pluginSnapshot);
                else pluginSnapshot.HideEditor();
            }
            catch (Exception ex)
            {
                _logger?.Warning(AppLogCategory.Audio, $"VST3 editor toggle failed: {ex.Message}");
            }
        });
    }

    public void SetGuitarVstSlotBypass(Guid id, bool bypass)
    {
        if (_engine == null) return;
        if (_outOfProcessVst)
        {
            Vst.Sandbox.VstSandboxHost? host;
            lock (_sandboxGate)
            {
                if (!_sandboxSlots.TryGetValue(id, out var slot)) return;
                _sandboxSlots[id] = (slot.Path, bypass);
                var desired = _sandboxDesired.Chain.FirstOrDefault(e => e.Id == id);
                desired?.Bypass = bypass;
                host = _sandbox;
            }
            if (host is { IsRunning: true })
            {
                _ = Task.Run(async () =>
                {
                    try
                    {
                        await host.SendAsync(new Vst.Sandbox.VstSandboxRequest
                        { Op = "bypass", Slot = id.ToString(), Value = bypass },
                            TimeSpan.FromSeconds(10)).ConfigureAwait(false);
                    }
                    catch (Exception ex)
                    { _logger?.Warning(AppLogCategory.Audio, $"Sandbox bypass({id}) failed: {ex.Message}"); }
                });
            }
            return;
        }
        JuceAudioPlugin? plugin;
        int player;
        lock (_gate)
        {
            if (!_guitarChain.TryGetValue(id, out plugin)) return;
            // The seat this slot belongs to. The plugin wrapper cannot be asked:
            // it only learns its seat from AttachToInput, so a slot loaded or
            // re-seated while bypassed still believes it is on seat 0.
            player = _guitarSlotPlayer.GetValueOrDefault(id);
            // The app's intent, recorded before the native call for the same
            // reason the seat is: this is what every snapshot and every later
            // diff reads, and `plugin.Bypass` is not it.
            _guitarSlotBypass[id] = bypass;
        }

        // Unlocked, and this is the path the Bypass BUTTON takes.
        // JuceAudioPlugin.Bypass's setter P/Invokes PluginDetachFromInput /
        // PluginAttachToInputForPlayer — a wait-one-block barrier on the audio
        // callback, walking every seat at ABI v28 — and _gate is what Play,
        // Pause, Stop and SeekTo all wait on through SnapshotStems(). Holding it
        // across the toggle stalled the UI thread for as long as the barrier
        // took, in a service whose own rule is that _gate is never held across a
        // P/Invoke. The plugin is resolved under the lock; the handle is
        // reference-counted (NativeHandleOwner), so a concurrent unload throws
        // here rather than reaching a dead handle.
        try
        {
            // Say the seat before clearing the flag — same reason as the chain
            // diff's bypass phase: the Bypass setter re-attaches to the seat the
            // WRAPPER remembers, which is 0 for anything that was never attached.
            if (!bypass) plugin.AttachToInput(player);
            plugin.Bypass = bypass;
        }
        catch (Exception ex)
        {
            _logger?.Warning(AppLogCategory.Audio,
                $"VST3 chain: {(bypass ? "bypassing" : "un-bypassing")} slot {id} failed: {ex.Message}");
        }
    }

    public void LoadInstrumentVst(string pluginPath, bool openEditorAfterLoad = false)
    {
        // Mark that the MIDI route is about to be claimed by an instrument
        // VSTi so that any concurrent ApplyMidiDevice call does not open a
        // WinMM sink that we will immediately tear down.
        if (!string.IsNullOrWhiteSpace(pluginPath))
            _instrumentReloadPending = true;

        // The engine is created lazily by ApplyAudioConfiguration. If the
        // user (or the saved-settings restore) requests an instrument VST
        // before that runs, queue the path so ReplaceEngine can apply it
        // the moment the engine comes up. Without this the very first
        // startup load was silently dropped � the user had to switch
        // the dropdown to "None" and back to retry.
        // Same check-then-act race as the guitar chain above — see the comment
        // there. Under _gate so the queue write cannot land after the engine
        // worker has already drained it.
        bool queued;
        lock (_gate)
        {
            queued = _engine == null;
            if (queued)
            {
                _pendingInstrumentLoadPath = string.IsNullOrWhiteSpace(pluginPath) ? null : pluginPath;
                if (openEditorAfterLoad) _instrumentPendingEditorOpen = true;
            }
        }
        if (queued)
        {
            _logger?.Info(AppLogCategory.Audio,
                string.IsNullOrWhiteSpace(pluginPath)
                    ? "Instrument VST3 unload queued: audio engine not yet initialised."
                    : $"Instrument VST3 load queued: '{Path.GetFileName(pluginPath)}' (audio engine not yet initialised).");
            try { InstrumentVstLoadChanged?.Invoke(this, IsInstrumentVstLoaded); } catch { /* ignored */ }
            return;
        }

        _instrumentLoadInFlight = true;
        try { InstrumentVstLoadChanged?.Invoke(this, IsInstrumentVstLoaded); } catch { /* ignored */ }
        var statusTaskId = $"vst-instrument-load:{Path.GetFileName(pluginPath)}";
        _statusBar?.BeginTask(statusTaskId,
            string.IsNullOrWhiteSpace(pluginPath)
                ? _localization["Progress_UnloadingInstrumentVst"]
                : _localization.Format("Progress_LoadingInstrumentVst", Path.GetFileName(pluginPath)),
            indeterminate: true);
        _ = Task.Run(async () =>
        {
            // WaitAsync, not Wait(): a queued load must not park a pool thread
            // behind another load that can run for minutes.
            await _vstGate.WaitAsync().ConfigureAwait(false);
            try
            {
                var engine = _engine;
                if (engine == null) return;

                if (_outOfProcessVst)
                {
                    // Sandbox route: the child hosts the VSTi; chart MIDI is
                    // forwarded through the native shared-memory bridge.
                    await LoadInstrumentVstSandboxCoreAsync(pluginPath, openEditorAfterLoad).ConfigureAwait(false);
                    return;
                }

                if (string.IsNullOrWhiteSpace(pluginPath))
                {
                    DetachInstrumentLocked();
                    return;
                }

                if (_instrumentPlugin != null &&
                    string.Equals(_instrumentPath, pluginPath, StringComparison.OrdinalIgnoreCase))
                {
                    if (openEditorAfterLoad)
                        try { ShowPluginEditorForegrounded(_instrumentPlugin); } catch { /* ignored */ }
                    return;
                }

                DetachInstrumentLocked();

                JuceAudioPlugin plugin;
                try { plugin = engine.LoadPlugin(pluginPath); }
                catch (Exception ex)
                {
                    _logger?.Warning(AppLogCategory.Audio, $"Failed to load instrument VST3 '{pluginPath}': {ex.Message}");
                    return;
                }
                try { plugin.AttachToInstrument(); }
                catch (Exception ex)
                {
                    _logger?.Warning(AppLogCategory.Audio, $"Failed to attach instrument VST3 '{pluginPath}' to bus: {ex.Message}");
                    try { plugin.Dispose(); } catch { /* ignored */ }
                    return;
                }
                plugin.EditorClosedByUser += () =>
                {
                    _logger?.Info(AppLogCategory.Audio, "Instrument VST editor: closed by user.");
                    try { InstrumentVstEditorClosed?.Invoke(this, EventArgs.Empty); } catch { /* ignored */ }
                };
                _instrumentPlugin = plugin;
                _instrumentPath = pluginPath;
                _instrumentIsAmpleGuitar = IsAmpleGuitarPath(pluginPath);
                _instrumentChannelsSetup = false;
                lock (_midiGate) { _midiScheduler.ForgetChannelNotes(); }
                try { engine.SetInstrumentGain((float)Math.Clamp(_vstInstrumentVolume, 0.0, 4.0)); } catch { /* ignored */ }
                // Query plugin-reported latency now that prepareToPlay has run
                // inside attachInstrumentPlugin. Convert to ms; the MIDI
                // scheduler will pre-fire events by this amount so the
                // plugin's processed audio aligns with the file path.
                _instrumentLatencyMs = ComputeInstrumentLatencyMs(engine, plugin);
                _logger?.Info(AppLogCategory.Audio,
                    $"Instrument VST3 attached: '{Path.GetFileName(pluginPath)}' (ampleGuitar={_instrumentIsAmpleGuitar}, pluginLatency={_instrumentLatencyMs:F2}ms).");

                // Swap MIDI route to the internal instrument bus.
                lock (_midiGate)
                {
                    try { _midi.Dispose(); } catch { /* ignored */ }
                    var sink = new JuceInstrumentMidiOutput(engine);
                    sink.Open();
                    _midi = sink;
                    SetupInstrumentChannelsLocked(sink);
                    if (_midiProgram >= 0)
                        try { _midi.ProgramChange(0, _midiProgram); _midi.ChannelVolume(0, 100); } catch { /* ignored */ }
                }

                if (openEditorAfterLoad || _instrumentPendingEditorOpen)
                {
                    try { ShowPluginEditorForegrounded(plugin); } catch { /* ignored */ }
                    if (_instrumentPendingEditorOpen)
                    {
                        _logger?.Info(AppLogCategory.Audio,
                            "Instrument VST editor: opening from deferred toggle (plugin finished loading).");
                    }
                }
                _instrumentPendingEditorOpen = false;
            }
            catch (Exception ex)
            {
                _logger?.Error(AppLogCategory.Audio, "LoadInstrumentVst failed.", ex);
            }
            finally
            {
                _instrumentReloadPending = false;
                _instrumentLoadInFlight = false;
                _vstGate.Release();
                try { InstrumentVstLoadChanged?.Invoke(this, IsInstrumentVstLoaded); } catch { /* ignored */ }
                _statusBar?.CompleteTask(statusTaskId);
            }
        });
    }

    // Wrap JuceAudioPlugin.ShowEditor with a managed-side foreground bringup
    // (Windows-only). JUCE's toFront() runs on its own message thread,
    // which Windows blocks from stealing foreground; we have to assist
    // it from the host process by snapshotting our windows, then after
    // a small delay forcing any newly-appeared non-Avalonia top-level
    // window to the front.
    private static void ShowPluginEditorForegrounded(JuceAudioPlugin plugin)
    {
        if (plugin.IsDisposed) return;
        if (OperatingSystem.IsWindows())
        {
            var before = Win32WindowForegrounder.SnapshotProcessTopLevelWindows();
            plugin.ShowEditor();
            Win32WindowForegrounder.BringNewWindowsToFront(before);
        }
        else
        {
            plugin.ShowEditor();
        }
    }

    public void ToggleInstrumentVstEditor(bool open)
    {
        if (_outOfProcessVst)
        {
            Vst.Sandbox.VstSandboxHost? host;
            lock (_sandboxGate) host = _sandbox;
            if (!_sandboxInstrumentLoaded || host is not { IsRunning: true })
            {
                _instrumentPendingEditorOpen = open && (_instrumentReloadPending || _instrumentLoadInFlight);
                _logger?.Warning(AppLogCategory.Audio,
                    _instrumentPendingEditorOpen
                        ? "Instrument VST editor toggle deferred (sandbox): plugin still loading (will auto-open on completion)."
                        : "Instrument VST editor toggle ignored (sandbox): no instrument VST loaded.");
                return;
            }
            var hostSnapshot = host;
            _ = Task.Run(() => SendSandboxEditorToggleAsync(hostSnapshot, Vst.Sandbox.VstSandboxProtocol.InstrumentSlotId, open));
            return;
        }

        var engine = _engine;
        var plugin = _instrumentPlugin;
        if (plugin == null)
        {
            // Plugin still loading (or unloaded). Stash the intent so the
            // editor opens automatically once LoadInstrumentVst finishes.
            // Without this, clicking the toggle while AGPF / BIAS / etc.
            // are still being scanned (which can take 25 s+ on first run)
            // silently drops the request - the UI checkbox stays "on" but
            // no window ever appears, which looks like the editor is
            // broken even though the user has to merely toggle again.
            _instrumentPendingEditorOpen = open && _instrumentReloadPending;
            _logger?.Warning(AppLogCategory.Audio,
                _instrumentReloadPending
                    ? $"Instrument VST editor toggle deferred: plugin still loading (will auto-{(open ? "open" : "stay-closed")} on completion)."
                    : "Instrument VST editor toggle ignored: no instrument VST loaded.");
            return;
        }
        if (engine == null)
        {
            _logger?.Warning(AppLogCategory.Audio,
                "Instrument VST editor toggle ignored: audio engine not initialised.");
            return;
        }
        _ = Task.Run(() =>
        {
            try
            {
                _logger?.Info(AppLogCategory.Audio,
                    $"Instrument VST editor: {(open ? "show" : "hide")}.");
                if (open) ShowPluginEditorForegrounded(plugin);
                else plugin.HideEditor();
            }
            catch (Exception ex) { _logger?.Error(AppLogCategory.Audio, "ToggleInstrumentVstEditor failed.", ex); }
        });
    }

    private void DetachInstrumentLocked()
    {
        var plugin = _instrumentPlugin;
        if (plugin == null)
        {
            _instrumentPath = null;
            // The sandbox instrument owns _instrumentIsAmpleGuitar while it
            // is loaded — don't clobber it from the in-proc no-op path.
            if (!_sandboxInstrumentLoaded) _instrumentIsAmpleGuitar = false;
            return;
        }
        _instrumentPlugin = null;
        _instrumentPath = null;
        _instrumentIsAmpleGuitar = false;
        _instrumentLatencyMs = 0.0;
        _instrumentChannelsSetup = false;

        // Flush stuck notes through the VSTi route, then *atomically swap*
        // the MIDI sink to a NullMidiOutput BEFORE we tear down the plugin.
        // Otherwise any in-flight UpdateMidiNotes / AllSoundOff on another
        // thread would call into the now-unloaded plugin handle and AV.
        try
        {
            lock (_midiGate)
            {
                _previewSink.Target = _midi;
                _midiScheduler.Flush(_previewSink);

                // Swap sink *before* the plugin is unloaded so subsequent
                // MIDI traffic is a no-op rather than a use-after-free.
                var oldSink = _midi;
                _midi = new NullMidiOutput();
                try { oldSink.Dispose(); } catch { /* ignored */ }
            }
        }
        catch { /* ignored */ }

        try { plugin.HideEditor(); } catch { /* ignored */ }
        try { plugin.DetachFromInstrument(); } catch { /* ignored */ }
        try { plugin.Dispose(); } catch { /* ignored */ }

        // Switch MIDI route back to the configured WinMM device - unless we
        // are tearing down the service entirely (avoid opening winmm right
        // before the engine destroy that may also close it).
        if (!IsDisposing && _midiDeviceIndex != int.MinValue)
            ApplyMidiDevice(_midiDeviceIndex);
    }

    // Read the loaded VSTi's reported processing latency and convert to
    // milliseconds at the engine's current sample rate. Returns 0.0 when
    // the engine isn't fully up yet or the plugin reports no latency.
    private static double ComputeInstrumentLatencyMs(JuceAudioEngine engine, JuceAudioPlugin plugin)
    {
        try
        {
            var samples = plugin.LatencySamples;
            if (samples <= 0) return 0.0;
            var sr = engine.SampleRate;
            if (sr <= 0) return 0.0;
            return samples * 1000.0 / sr;
        }
        catch { return 0.0; }
    }

    private static bool IsAmpleGuitarPath(string path)
    {
        var name = Path.GetFileNameWithoutExtension(path);
        if (string.IsNullOrEmpty(name)) return false;
        return name.Contains("Ample", StringComparison.OrdinalIgnoreCase)
            || name.StartsWith("AGM", StringComparison.OrdinalIgnoreCase)
            || name.StartsWith("AGL", StringComparison.OrdinalIgnoreCase)
            || name.StartsWith("AGF", StringComparison.OrdinalIgnoreCase)
            || name.StartsWith("AGP", StringComparison.OrdinalIgnoreCase)
            || name.StartsWith("AGT", StringComparison.OrdinalIgnoreCase)
            || name.StartsWith("AGG", StringComparison.OrdinalIgnoreCase);
    }
}
