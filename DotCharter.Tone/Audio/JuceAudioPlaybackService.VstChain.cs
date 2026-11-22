// Partial: guitar VST chain + instrument VST — see JuceAudioPlaybackService.cs for the shared state and locking rules.
using DotCharter.Tone.Audio.Midi;
using JuceSharp;

namespace DotCharter.Tone.Audio;

public sealed partial class JuceAudioPlaybackService
{
    private long _guitarChainGeneration;
    private List<Guid> _guitarChainOrder = [];

    public bool IsInstrumentVstLoaded => _instrumentPlugin != null || SandboxInstrumentLoaded;
    public bool IsInstrumentVstLoading
        => _instrumentLoadInFlight || _instrumentReloadPending || _pendingInstrumentLoadPath != null;
    public bool IsGuitarVstSlotLoaded(Guid id)
    {
        if (_outOfProcessVst)
        {
            lock (_sandboxGate) return SandboxSlotOwner(id) != null;
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
                _logger?.Warning(AppLogCategory.Audio, $"VST3 scan skipped '{root}'", ex);
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
            State = e.State?.ToArray(),
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
        long generation;
        lock (_gate)
        {
            generation = ++_guitarChainGeneration;
            // Keep the newest request even while an engine exists: a rebuild
            // may already own _vstGate and be about to tear that engine down.
            _pendingGuitarChain = snapshot;
            if (_engine == null)
            {
                _logger?.Info(AppLogCategory.Audio,
                    $"VST3 chain queued ({snapshot.Count} slot(s)): audio engine not yet initialised.");
                return;
            }
        }

        QueueGuitarVstChain(snapshot, generation);
    }

    private void QueueGuitarVstChain(IReadOnlyList<VstChainEntry> snapshot, long generation)
    {
        const string taskId = "guitar-vst-chain-apply";
        _statusBar?.BeginTask(taskId,
            snapshot.Count == 0
                ? _localization["Progress_ClearingGuitarVstChain"]
                : _localization.Plural("Progress_LoadingGuitarVstChainOne", "Progress_LoadingGuitarVstChain", snapshot.Count),
            indeterminate: true);
        _ = Task.Run(async () =>
        {
            try
            {
                await VstChainWork.RunAsync(_vstGate, () =>
                {
                    lock (_gate) return generation == _guitarChainGeneration;
                }, async () =>
                {
                    if (_outOfProcessVst) await ApplySandboxGuitarChainCoreAsync(snapshot).ConfigureAwait(false);
                    else ApplyGuitarVstChainCore(snapshot);
                    lock (_gate)
                    {
                        if (_engine is not null && generation == _guitarChainGeneration) _pendingGuitarChain = null;
                    }
                }).ConfigureAwait(false);
            }
            catch (Exception ex) { _logger?.Error(AppLogCategory.Audio, "VST3 chain apply failed", ex); }
            finally { _statusBar?.CompleteTask(taskId); }
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
        lock (_gate)
        {
            _guitarChainOrder = entries.Select(e => e.Id).ToList();
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
                if (_guitarChain.ContainsKey(entry.Id))
                {
                    // Record intent only. The unlocked final ordering pass
                    // applies seat, bypass and order together to the live objects.
                    _guitarSlotPlayer[entry.Id] = entry.Player;
                    _guitarSlotBypass[entry.Id] = entry.Bypass;
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
                _logger?.Error(AppLogCategory.Audio, $"VST3 chain: failed to load '{entry.PluginPath}'", ex);
                continue;
            }
            try
            {
                // ABI v28: the slot declares which seat's input chain it
                // processes. Player 0 is the single-player chain, unchanged.
                VstRebuildState.Restore(plugin, entry.State, () =>
                {
                    if (!entry.Bypass) plugin.AttachToInput(entry.Player);
                    else plugin.Bypass = true;
                }); // syncs wrapper state; native detach is a no-op when not attached
            }
            catch (Exception ex)
            {
                _logger?.Error(AppLogCategory.Audio, $"VST3 chain: attach failed for '{entry.PluginPath}'", ex);
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
                        _logger?.Warning(AppLogCategory.Audio, "Deferred VST3 chain editor open failed", ex);
                    }
                });
            }
        }

        ReapplyGuitarInputOrder();
    }

    // Caller owns _vstGate. Detaching and attaching reuse the plugin objects,
    // including their opaque state and editor; no reload is needed to reorder.
    private void ReapplyGuitarInputOrder()
    {
        List<(JuceAudioPlugin Plugin, int Player, bool BypassIntent)> slots;
        lock (_gate)
        {
            slots = _guitarChainOrder.Where(_guitarChain.ContainsKey)
                .Select(id => (_guitarChain[id], _guitarSlotPlayer.GetValueOrDefault(id),
                    _guitarSlotBypass.GetValueOrDefault(id))).ToList();
        }
        VstInputOrder.Apply(slots, plugin =>
        {
            try { plugin.DetachFromInput(); }
            catch (Exception ex) { _logger?.Warning(AppLogCategory.Audio, "VST3 chain detach for reorder failed", ex); }
        }, (plugin, player) =>
        {
            try { plugin.AttachToInput(player); }
            catch (Exception ex) { _logger?.Warning(AppLogCategory.Audio, "VST3 chain attach for reorder failed", ex); }
        });
    }

    public void ToggleGuitarVstSlotEditor(Guid id, bool open)
    {
        if (_outOfProcessVst)
        {
            Vst.Sandbox.IVstSandboxHost? host;
            bool loaded;
            lock (_sandboxGate)
            {
                var seat = SandboxSlotOwner(id);
                host = seat?.Host;
                loaded = seat != null;
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
            _ = Task.Run(() => SendSandboxOwnedEditorToggleAsync(id.ToString(), open));
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
                _logger?.Warning(AppLogCategory.Audio, "VST3 editor toggle failed", ex);
            }
        });
    }

    public void SetGuitarVstSlotBypass(Guid id, bool bypass)
    {
        if (_outOfProcessVst)
        {
            lock (_sandboxGate)
            {
                var seat = SandboxSlotOwner(id);
                if (seat == null) return;
                var slot = seat.Slots[id];
                seat.Slots[id] = (slot.Path, bypass, slot.Player);
                var desired = seat.Desired.Chain.FirstOrDefault(e => e.Id == id);
                desired?.Bypass = bypass;
            }
            _ = Task.Run(() => SendSandboxOwnedBypassAsync(id));
            return;
        }
        lock (_gate)
        {
            if (!_guitarChain.ContainsKey(id)) return;
            _guitarSlotBypass[id] = bypass;
        }

        // Serialize against chain updates and engine teardown. Read current
        // intent on the worker so older bypass workers cannot undo a newer toggle.
        _ = Task.Run(async () =>
        {
            if (!await _vstGate.TryEnterAsync().ConfigureAwait(false)) return;
            try { ReapplyGuitarInputOrder(); }
            finally { _vstGate.Release(); }
        });
    }

    public void LoadInstrumentVst(string pluginPath, bool openEditorAfterLoad = false)
        => LoadInstrumentVstCore(pluginPath, openEditorAfterLoad, null);

    private void LoadInstrumentVstCore(string pluginPath, bool openEditorAfterLoad, byte[]? state)
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
            if (!await _vstGate.TryEnterAsync().ConfigureAwait(false))
            {
                _instrumentReloadPending = false;
                _instrumentLoadInFlight = false;
                _statusBar?.CompleteTask(statusTaskId);
                return;
            }
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
                    _logger?.Warning(AppLogCategory.Audio, $"Failed to load instrument VST3 '{pluginPath}'", ex);
                    return;
                }
                try
                {
                    VstRebuildState.Restore(plugin, state, plugin.AttachToInstrument);
                }
                catch (Exception ex)
                {
                    _logger?.Warning(AppLogCategory.Audio, $"Failed to attach instrument VST3 '{pluginPath}' to bus", ex);
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
            Vst.Sandbox.IVstSandboxHost? host;
            lock (_sandboxGate) host = _sandboxSeats[0].Host;
            if (!SandboxInstrumentLoaded || host is not { IsRunning: true })
            {
                _instrumentPendingEditorOpen = open && (_instrumentReloadPending || _instrumentLoadInFlight);
                _logger?.Warning(AppLogCategory.Audio,
                    _instrumentPendingEditorOpen
                        ? "Instrument VST editor toggle deferred (sandbox): plugin still loading (will auto-open on completion)."
                        : "Instrument VST editor toggle ignored (sandbox): no instrument VST loaded.");
                return;
            }
            _ = Task.Run(() => SendSandboxOwnedEditorToggleAsync(Vst.Sandbox.VstSandboxProtocol.InstrumentSlotId, open));
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
            if (!SandboxInstrumentLoaded) _instrumentIsAmpleGuitar = false;
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
