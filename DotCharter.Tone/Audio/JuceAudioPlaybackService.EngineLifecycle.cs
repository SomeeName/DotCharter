// Partial: engine create/replace — the full ReplaceEngine cycle and every piece of master-bus state replayed onto the rebuilt engine.
using DotCharter.Tone.Audio.Dsp.RsToneConversion;
using DotCharter.Tone.Audio.Midi;
using JuceSharp;

namespace DotCharter.Tone.Audio;

public sealed partial class JuceAudioPlaybackService
{
    public Task InitializeAsync()
    {
        // Device enumeration is LAZY: a startup probe would
        // spin up a transient default engine and enumerate every
        // output/input (an ASIO scan costs several hundred ms) only to log
        // the counts — ApplyAudioConfiguration never consumes the results, it
        // opens the CONFIGURED device ids straight from RigConfig. Such a probe
        // would also create a hazard: JUCE's
        // default AudioIODeviceType would hold ASIO driver references from the scan
        // that make the real engine's ASIO open fail with "No such device".
        // No scan at startup ⇒ nothing to release ⇒ faster AND safer. The
        // Settings UI still enumerates on demand from the live engine
        // (EnsureAsioDevicesProbed / EnsureWasapiDevicesProbed / Get*Devices).
        return Task.Run(() =>
        {
            // Ensure the system MIDI mapper (winmm index -1) is live from t=0
            // even if no RigConfig has been applied yet. Otherwise the very
            // first chart trigger before settings round-trip would no-op into
            // NullMidiOutput.
            if (_midiDeviceIndex == int.MinValue && WinMMMidiOutput.IsSupported)
                ApplyMidiDevice(-1);
        });
    }

    public Task ShutdownAsync()
    {
        Dispose();
        return Task.CompletedTask;
    }

    private void EnsureEngine(JuceEngineConfig cfg)
    {
        if (_engine != null) return;
        ReplaceEngine(cfg);
    }

    private void ReplaceEngine(JuceEngineConfig cfg)
    {
        const string taskId = "audio-engine-restart";
        _statusBar?.BeginTask(taskId, _localization["Progress_RestartingAudioEngine"], indeterminate: true);
        try
        {
            ReplaceEngineCore(cfg);
        }
        finally
        {
            _statusBar?.CompleteTask(taskId);
            // The editor's play-the-chart toggles go live (or say why not) on this.
            RaiseChartSynthAvailabilityChanged();
        }
    }

    private void ReplaceEngineCore(JuceEngineConfig cfg)
    {
        // Same gate ApplyEngineConfig takes, so a full replace and an in-place
        // reconfigure can never overlap. Reentrant when we got here THROUGH
        // ApplyEngineConfig's fallback (Monitor, same thread).
        var configGateHeld = Monitor.TryEnter(_engineConfigGate, TimeSpan.FromSeconds(60));
        if (!configGateHeld)
        {
            _logger?.Warning(AppLogCategory.Audio,
                "Engine restart proceeding without the config gate (another engine change appears wedged after 60 s).");
        }

        try
        {
            ReplaceEngineConfigGated(cfg);
        }
        finally
        {
            if (configGateHeld) Monitor.Exit(_engineConfigGate);
        }
    }

    private void ReplaceEngineConfigGated(JuceEngineConfig cfg)
    {
        // Serialize the engine swap against in-flight VST plugin work.
        // Chain apply / instrument load run on workers under _vstGate and
        // (since the chain apply stopped holding _gate across native loads)
        // this gate is what guarantees we never destroy the engine while a
        // LoadPlugin call is still executing against it. The generous
        // timeout only exists so a wedged plugin can't brick device
        // changes forever; 60 s exceeds even a first-run BIAS/AGPF
        // sample-library scan.
        var vstGateHeld = _vstGate.Wait(TimeSpan.FromSeconds(60));
        if (!vstGateHeld)
        {
            _logger?.Warning(AppLogCategory.Audio,
                "Engine restart proceeding without the VST gate (a plugin load appears wedged after 60 s).");
        }

        try
        {
            // The queued-VST drain at the end of this method only *spawns*
            // workers (they acquire _vstGate themselves once we release it
            // below), so holding the gate across the whole body is safe.
            ReplaceEngineGated(cfg);
        }
        finally
        {
            if (vstGateHeld) _vstGate.Release();
        }
    }

    private void ReplaceEngineGated(JuceEngineConfig cfg)
    {
        // Dispose has started: never build an engine we would then leak. A
        // watchdog recovery tick (or a settings apply) racing window close used
        // to walk straight through here and publish a fresh engine into _engine
        // AFTER DisposeCore destroyed the old one and nulled the field — nothing
        // disposes that one, so on ASIO the process exits still holding the
        // exclusive device. Checked here AND again around the publish below,
        // because the flag can be set at any point during the multi-second
        // teardown+rebuild in between.
        if (IsDisposing) return;
        string? reloadPath;
        string? instrumentReload = null;
        List<VstChainEntry>? guitarChainReload = null;
        // FreeAudioLocked below clears the stem dictionary, so it is snapshotted
        // first — otherwise ANY device change, buffer-size change or
        // watchdog recovery would silently drop a user's stem mix and dump them
        // back onto the original track (the master un-mutes, so it would be silent
        // data loss rather than silence). Take the whole mixer state now; it is
        // re-issued after the rebuild. See RestoreStemsAfterEngineRebuild.
        StemMixerSnapshot? stemReload;
        lock (_gate)
        {
            reloadPath = _loadedPath;
            stemReload = SnapshotStemMixerLocked();
        }

        // Tear plugins down BEFORE we touch the engine. The native
        // dca_engine_destroy has a "force-unload orphaned plugins" fallback,
        // but it runs on whatever thread happens to be executing the destroy
        // and is unsafe for VST3 plugins that still have live editors / state
        // (BIAS FX, Helix, Neural DSP). Skipping the explicit teardown here
        // is what made buffer-size changes fast-fail with STATUS_STACK_BUFFER_OVERRUN
        // (0xC0000409) when a guitar chain or instrument VSTi was loaded.
        //
        // DetachInstrumentLocked / HidePluginEditor / DetachPluginFromInput /
        // UnloadPlugin must run while the engine is still alive and on a
        // non-MM thread (same contract as Dispose()). Snapshot the chain
        // and instrument path first so we can rebuild them against the new
        // engine after the swap.
        JuceAudioEngine? engineForTeardown;
        List<(Guid Id, JuceAudioPlugin Plugin, int Player, bool BypassIntent)> chainSnapshot;
        lock (_gate)
        {
            engineForTeardown = _engine;
            chainSnapshot = _guitarChain
                .Select(kv => (kv.Key, kv.Value, Player: _guitarSlotPlayer.GetValueOrDefault(kv.Key),
                    BypassIntent: _guitarSlotBypass.GetValueOrDefault(kv.Key)))
                .ToList();
        }

        if (engineForTeardown != null)
        {
            // Build the rehydration list NOW (before we clear state).
            if (chainSnapshot.Count > 0)
            {
                guitarChainReload = chainSnapshot
                    .Select(s => new VstChainEntry
                    {
                        Id = s.Id, PluginPath = s.Plugin.Path,
                        // The APP's recorded intent, never `s.Plugin.Bypass`.
                        // That property answers "not in any seat's input chain",
                        // so a slot the app considers ACTIVE but that is not
                        // attached at this instant — one whose attach threw, one
                        // still parked from a load — snapshots as bypassed and
                        // comes back bypassed on the rebuilt engine, with the UI
                        // still drawing it lit. Same class as the EQ and per-seat
                        // routing replay bugs this file already pays for.
                        Bypass = s.BypassIntent,
                        // Carry the seat across the engine rebuild (ABI v28).
                        Player = s.Player,
                    })
                    .ToList();
            }

            // Capture the instrument path before DetachInstrumentLocked
            // clears it.
            lock (_gate) { instrumentReload = _instrumentPath; }

            // Stop the audio source so processBlock() can't race with
            // plugin destructors.
            try { _source?.Stop(); } catch { /* ignored */ }

            // Instrument VSTi: clean teardown (flushes MIDI, swaps to null
            // sink, hides editor, detaches, unloads).
            try { DetachInstrumentLocked(); }
            catch (Exception ex)
            {
                _logger?.Warning(AppLogCategory.Audio,
                    $"Instrument VSTi teardown before engine swap failed: {ex.Message}");
            }

            // Guitar chain: strict-order unload while engine is still alive.
            foreach (var slot in chainSnapshot)
            {
                try { slot.Plugin.HideEditor(); } catch { /* ignored */ }
                try { slot.Plugin.DetachFromInput(); } catch { /* ignored */ }
                try { slot.Plugin.Dispose(); } catch { /* ignored */ }
            }
        }

        // Held OUTSIDE the try so the catch can destroy an engine that was
        // created and then failed something later in the bring-up. Without it
        // the catch nulled `_engine` and walked away from a live native engine
        // that still OWNS THE DEVICE — and an ASIO device is typically
        // exclusive, so every later rebuild (including the watchdog's recovery)
        // then failed to reopen it for the rest of the process.
        JuceAudioEngine? createdEngine = null;
        JuceAudioEngine? outgoingEngine = null;
        // Three phases, and only the first and last hold _gate. Closing the
        // old device and opening the new one are driver calls: an ASIO open that
        // fails takes ~6.6 s per attempt, and the fallback walk makes two before
        // WASAPI. Done under _gate, every UI-thread member that reads it (volume,
        // the loaded path, the stem checks, Play/Pause/SeekTo) froze the window
        // for the whole walk: 13 s at startup with an interface half-back, and
        // again on every watchdog recovery attempt after an unplug. Nothing can
        // swap the engine meanwhile (the caller holds _engineConfigGate and
        // _vstGate), and _engine is null in between, which every member already
        // handles: it is null before the first init and after a failed one.
        try
        {
            lock (_gate)
            {
                FreeAudioLocked();
                // Plugins have been disposed above against the soon-to-be-destroyed engine.
                _guitarChain.Clear();
                _guitarSlotPlayer.Clear();
                _guitarSlotBypass.Clear();
                _instrumentPlugin = null;
                instrumentReload ??= _instrumentPath;
                _instrumentPath = null;
                if (_engine != null)
                {
                    // Detach the DSP tone rig BEFORE the engine dies. It holds a
                    // bare dca_engine* (JuceAudioEngine.NativeHandle is unpinned),
                    // and the pedalboard writes to it from the UI thread. Leaving
                    // it pointed at the old engine across the dispose + rebuild
                    // below — hundreds of ms — means any knob turn in that window
                    // lands in freed native memory. Dispose() already does this;
                    // this path did not.
                    try { AttachDspChainsToEngine(null); } catch { /* ignored */ }
                    try { CloseRigMidiInputLocked(); } catch { /* engine disposal also owns MIDI children */ }
                    // Disposed below, UNLOCKED: closing a device is a driver call.
                    outgoingEngine = _engine;
                    _engine = null;
                    // A new device's block size is news even when it happens to
                    // equal the outgoing one's — the subscribers have been shown
                    // nothing since the last report and the row must agree with
                    // the device that is actually open.
                    _bufferReporter.Forget();
                }
            }

            try { outgoingEngine?.Dispose(); } catch { /* ignored */ }
            outgoingEngine = null;

            var engine = CreateEngineWithFallback(cfg, out var activeCfg)
                ?? throw new InvalidOperationException("Unable to create a JUCE audio engine with the requested configuration or fallbacks.");
            createdEngine = engine;
            lock (_gate)
            {
                // Re-check under the lock, immediately after the create: the
                // teardown above takes hundreds of ms and Dispose may have run
                // its whole DisposeCore in that window. Publishing now would
                // strand a live engine holding the device. Same shape as
                // SwapMasterSource's ReferenceEquals(_engine, engine) discard —
                // the throw is caught by the handler below, which disposes
                // createdEngine and clears _activeEngineConfig for us.
                if (IsDisposing)
                {
                    throw new ObjectDisposedException(nameof(JuceAudioPlaybackService),
                        "The audio service was disposed while the engine was being rebuilt; discarding it.");
                }

                _engine = engine;
                _activeEngineConfig = activeCfg;
                RestoreRigMidiInputLocked(engine);
                RigMidiInputStatusChanged?.Invoke();
                // Fresh device ⇒ allow one new 48 kHz-for-neural-amp attempt. If
                // the user (re)loads a NAM tone the chain re-adds namamp and the
                // NodesChanged handler re-requests 48 kHz against this device.
                _neuralRateAttempted = false;
                // Point the in-house DSP tone rig at this engine's native chain
                // (0.16). A device change recreates the engine, so the native
                // chain is empty afterwards — reattaching resets the managed
                // mirror to match.
                _logger?.Info(AppLogCategory.Audio,
                    $"Attaching DSP tone rig to fresh engine. dspChain={(_dspChain?.GetType().Name ?? "<none>")}, handle=0x{engine.NativeHandle:X}.");
                // …and with it the per-player live-input routing, which is NOT in
                // _activeEngineConfig: a fresh engine is back to ONE rig with every
                // seat on its default channel, so without this a device change
                // mid-song silently ends the per-seat routing.
                try { AttachPerPlayerStateToEngine(engine); }
                catch (Exception ex) { _logger?.Warning(AppLogCategory.Audio, $"DSP rig attach failed: {ex.Message}"); }
                // Every remaining piece of MASTER-BUS state, each individually
                // guarded — see ReapplyMasterBusStateLocked.
                ReapplyMasterBusStateLocked(engine);
                // The device may have refused the requested block size (a
                // fallback device, or an ASIO driver pinned from its own panel).
                // Tell the buffer row and the status bar what it settled on.
                ReportDeviceBufferFrames(engine);
                _logger?.Info(AppLogCategory.Audio,
                    $"JUCE engine started. Driver={activeCfg.PreferredDriver}, sr={engine.SampleRate}, buf={engine.BufferFrames}, output='{activeCfg.OutputDeviceId}', input='{activeCfg.InputDeviceId}'.");
            }
        }
        catch (Exception ex)
        {
            _logger?.Error(AppLogCategory.Audio,
                $"Failed to start JUCE engine (driver={cfg.PreferredDriver}, output='{cfg.OutputDeviceId}'). {ex.Message}", ex);
            lock (_gate)
            {
                _engine = null;
                // The rebuild failed, so the AttachEngine above never ran. Without
                // this the rig would keep whatever handle it last had, and a
                // failed rebuild is exactly the unplugged-interface case, where
                // the user then keeps poking a pedalboard wired to a dead engine.
                try { AttachDspChainsToEngine(null); } catch { /* ignored */ }
                // The config described the device that engine had open. With no
                // engine there is nothing it describes, and leaving it set makes
                // the watchdog's recovery log name a device we do not hold.
                _activeEngineConfig = null;
            }

            // A throw in the first phase can leave the outgoing engine undisposed.
            try { outgoingEngine?.Dispose(); } catch { /* ignored */ }
            // If we got far enough to CREATE an engine, it is still holding the
            // device. Dropping the reference does not close it: the native engine
            // outlives the managed wrapper's field, keeps the (typically
            // exclusive) ASIO device open, and every later attempt, the
            // watchdog's recovery included, then fails with "device busy". Destroy
            // it in the same order Dispose uses (rig detached first, above),
            // unlocked like the create.
            if (createdEngine != null)
            {
                try { createdEngine.Dispose(); }
                catch (Exception disposeEx)
                {
                    _logger?.Warning(AppLogCategory.Audio,
                        $"Disposing the half-built engine also threw: {disposeEx.Message}");
                }
            }
        }

        // Re-arm the sandbox bridge against the new engine. The child process
        // (and every plugin loaded in it) survives engine swaps — only the
        // kernel-object wiring and the prepared sample rate need refreshing.
        if (_engine != null && _outOfProcessVst)
            RearmSandboxAfterEngineSwapLocked(_engine);

        // Re-open the loaded song so playback survives device changes.
        // The OpenFile itself runs UNLOCKED (1–3 s inside JUCE's format readers
        // on a long file) — a device change already froze the UI for the whole
        // teardown+rebuild, and this decode must not be tacked onto the end of it
        // while every UI-thread transport member waits on _gate. Only the
        // publish is locked. We are the engine's exclusive owner here (the caller
        // holds _vstGate), so the engine cannot be swapped under us.
        var reloadEngine = _engine;
        if (!string.IsNullOrWhiteSpace(reloadPath) && File.Exists(reloadPath) && reloadEngine != null)
        {
            try
            {
                var reopened = reloadEngine.OpenFile(reloadPath);
                lock (_gate)
                {
                    _source = reopened;
                    _loadedPath = reloadPath;
                    BindSongClock(reloadEngine, reopened);
                    ApplyGain();
                    try { reopened.Pan = (float)Math.Clamp(_pan, -1.0, 1.0); } catch { /* ignored */ }
                    try { reopened.PitchSemitones = (float)Math.Clamp(_pitchShift, -24.0, 24.0); } catch { /* ignored */ }
                    try { reopened.TempoRatio = (float)(_playbackSpeed <= 0 ? 1.0 : _playbackSpeed); } catch { /* ignored */ }
                }
                _logger?.Info(AppLogCategory.Audio, $"Re-opened '{reloadPath}' after engine rebuild.");
            }
            catch (Exception ex)
            {
                _logger?.Error(AppLogCategory.Audio, $"Failed to re-open '{reloadPath}' after engine rebuild.", ex);
            }
        }

        // Re-open the stem set. Must come AFTER the song re-open: LoadStemsCore
        // syncs every stem to the master source's position.
        RestoreStemsAfterEngineRebuild(stemReload);

        // Re-attach the instrument VSTi against the freshly-built engine so
        // MIDI routing survives device / driver changes.
        if (!string.IsNullOrEmpty(instrumentReload) && _engine != null)
        {
            // Set the pending flag *synchronously* so any ApplyMidiDevice
            // call that follows this method (e.g. from ApplyAudioConfiguration)
            // will defer to the VSTi route instead of opening WinMM in the gap.
            _instrumentReloadPending = true;
            LoadInstrumentVst(instrumentReload, openEditorAfterLoad: false);
        }

        // Drain any VST work that was queued while the engine was being
        // built. This is the startup auto-load path: settings restore fires
        // before the engine exists and stashes the desired plugin/chain
        // here; without this drain the user has to switch the VST dropdown
        // to "None" and back to retry.
        // Take the queues under _gate — the enqueue side (SetGuitarVstChain /
        // LoadInstrumentVst) tests _engine and writes them under the same lock,
        // so this is the other half of that check-then-act. Apply them OUTSIDE
        // the lock: both appliers take _gate themselves.
        string? queuedInstrument = null;
        List<VstChainEntry>? queuedChain = null;
        lock (_gate)
        {
            if (_engine != null)
            {
                queuedInstrument = _pendingInstrumentLoadPath;
                _pendingInstrumentLoadPath = null;
                queuedChain = _pendingGuitarChain ?? guitarChainReload;
                _pendingGuitarChain = null;
            }
        }

        if (!string.IsNullOrWhiteSpace(queuedInstrument)
            && !string.Equals(queuedInstrument, instrumentReload, StringComparison.OrdinalIgnoreCase))
        {
            _logger?.Info(AppLogCategory.Audio,
                $"Applying queued instrument VST3 load now that engine is up: '{Path.GetFileName(queuedInstrument)}'.");
            LoadInstrumentVst(queuedInstrument, openEditorAfterLoad: false);
        }

        if (queuedChain != null && queuedChain.Count > 0)
        {
            _logger?.Info(AppLogCategory.Audio,
                $"Applying queued guitar VST3 chain now that engine is up ({queuedChain.Count} slot(s)).");
            SetGuitarVstChain(queuedChain);
        }
    }

    /// <summary>
    ///     Re-applies every piece of MASTER-BUS state this service owns onto a
    ///     freshly built engine. Call under <c>_gate</c>, from the rebuild path
    ///     only — a new engine starts on the native defaults, so anything the
    ///     user set that is not part of <see cref="JuceEngineConfig" /> is gone
    ///     unless it is replayed here.
    ///     <para>
    ///         <b>Each group is individually guarded, and that is the point.</b>
    ///         These must not be bare calls inside <c>ReplaceEngineGated</c>'s one
    ///         big try: a single throwing setter — an export a degraded DLL
    ///         does not have, an <see cref="ObjectDisposedException" /> from a
    ///         raced teardown — would be reported as "Failed to start JUCE engine",
    ///         null <c>_engine</c> and leave the just-created engine holding the
    ///         device. Losing one setting must cost the user that setting, never
    ///         the whole engine.
    ///     </para>
    ///     <para>
    ///         <b>Adding master-bus state means adding a line here.</b> The EQ
    ///         and the per-seat input routing both shipped without one and both
    ///         vanished silently on the first device change. Per-PLAYER state
    ///         goes in <see cref="AttachPerPlayerStateToEngine" /> instead, which
    ///         runs just before this. Enumerated by
    ///         <c>EngineRebuildStateReplayTests</c>: every <c>engine.Set*</c> the
    ///         service calls anywhere must be replayed by one of the two, or be
    ///         exempt with a stated reason.
    ///     </para>
    /// </summary>
    private void ReapplyMasterBusStateLocked(JuceAudioEngine engine)
    {
        Reapply("input gain", () => engine.SetInputGain((float)Math.Clamp(_inputVolume, 0.0, 8.0)));
        Reapply("monitor gain", () => engine.SetMonitorGain((float)Math.Clamp(_monitorVolume, 0.0, 4.0)));
        Reapply("drop tuner", () =>
        {
            engine.SetInputPitchShift((float)_inputPitchShiftSemitones);
            engine.SetInputPitchShiftMode((JuceDropTuneMode)_inputPitchShiftMode);
        });
        Reapply("master gain", () => engine.SetMasterGain((float)Math.Clamp(_volume, 0.0, 4.0)));
        Reapply("instrument gain", () => engine.SetInstrumentGain((float)Math.Clamp(_vstInstrumentVolume, 0.0, 4.0)));
        Reapply("master filters", () =>
        {
            engine.SetHighPass((float)Math.Max(0.0, _hpFreq), (float)Math.Max(0.05, _hpQ));
            engine.SetLowPass((float)Math.Max(0.0, _lpFreq), (float)Math.Max(0.05, _lpQ));
            engine.SetBandPass((float)Math.Max(0.0, _bpFreq), (float)Math.Max(0.05, _bpQ));
        });
        // ABI v29: the six-band EQ is master-bus state too, so it belongs in this
        // replay for exactly the reason the three filters above do — a device
        // change otherwise silently drops it and the user hears their EQ vanish
        // when they switch interfaces.
        Reapply("parametric EQ", () => { if (_eqSection is { } eq) PushEqTo(engine, eq, _logger); });
        // ABI v34, default-OFF native-side since v35: the master-bus limiter is
        // master-bus state and a fresh engine is UNLIMITED until this runs.
        // Without it a device change / NAM 48 kHz request / watchdog recovery
        // silently hands the driver AND the float streaming mirror an unbounded
        // sum for the rest of the session, with nothing on screen saying so —
        // exactly the argument the EQ above makes.
        Reapply("master limiter", () =>
        {
            var limiter = _masterLimiter;
            engine.SetMasterLimiter(limiter.Enabled, limiter.CeilingDb, limiter.ReleaseMs);
            // The look-ahead delay line is only in the path while the stage is
            // on, so OutputLatencySamples moves with it; the cache keys on the
            // engine REFERENCE and this is a fresh one, but dropping it is free
            // and keeps the rule in one place.
            _latencyCache.Invalidate();
        });
        // Restore mirror across device swaps so the user doesn't lose their
        // stream feed every time the audio device changes.
        Reapply("streaming mirror", () =>
        {
            if (!string.IsNullOrEmpty(_mirrorDeviceName)) engine.SetMirrorDevice(_mirrorDeviceName);
        });
        // Rocksmith's own amp chains. The native table is process-wide
        // and survives the rebuild, but the tables may have been published before
        // any engine existed (nothing was handed over then), so every build hands
        // them over; an identical table is a no-op on both sides.
        Reapply("Rocksmith amp chains", () => _rsAmpTable.Push(RsGameTables.Process, "engine built"));

        void Reapply(string what, Action apply)
        {
            try { apply(); }
            catch (Exception ex)
            {
                _logger?.Warning(AppLogCategory.Audio,
                    $"Re-applying the {what} after the engine rebuild failed ({ex.GetType().Name}: {ex.Message}). " +
                    "The engine stays up; that one setting is back at the device default.");
            }
        }
    }

    private int SafeRead(Func<JuceAudioEngine, int> getter)
    {
        var e = _engine;
        if (e == null) return 0;
        try { return getter(e); }
        catch { return 0; }
    }

    /// <summary>
    ///     The ordered engine configurations <see cref="CreateEngineWithFallback" />
    ///     tries: the caller's own, then progressively weaker ones.
    ///     <para>
    ///         <b>Every attempt carries the CHANNEL COUNTS.</b> They are counts,
    ///         not device-specific indexes, so they survive a change of device and
    ///         of driver — and dropping them was not a shrug: the native side reads
    ///         <c>input_channels &lt;= 0</c> as "open no capture channels at all"
    ///         (<c>initialiseWithDefaultDevices(inputChannels, outputChannels)</c>
    ///         in <c>AudioEngine.cpp</c>, taken whenever both device ids are empty
    ///         — which is every fallback below). The WASAPI and Default fallbacks
    ///         left them at 0, so an ASIO interface that was offline at launch cost
    ///         the user their LIVE INPUT entirely — no tuner, no input meter, no
    ///         judged run — while the log said only that the driver had changed.
    ///         That is the "a capability the app cannot honour must never look
    ///         live" rule with the capability silently withdrawn instead.
    ///     </para>
    ///     <para>
    ///         The channel MASKS deliberately do NOT travel: a mask names physical
    ///         channels of the device the user picked, and a fallback device may
    ///         not have them. <c>OutputChannels</c>/<c>InputChannels</c> only ask
    ///         for the first N, which every device can answer.
    ///     </para>
    ///     Pure, so <c>EngineDeviceWiringTests</c> pins the whole ladder without a
    ///     device.
    /// </summary>
    internal static List<JuceEngineConfig> BuildEngineAttempts(JuceEngineConfig cfg)
    {
        var attempts = new List<JuceEngineConfig> { cfg };

        // Intermediate fallback for ASIO: same driver but with an empty device
        // id, letting JUCE pick the default ASIO device. This recovers from
        // device-name mismatches (e.g. a selected ASIO interface
        // that's been renamed by the driver or temporarily offline) without
        // dropping the user out of ASIO entirely.
        if (cfg.PreferredDriver == AudioDriver.Asio
            && !string.IsNullOrEmpty(cfg.OutputDeviceId))
        {
            attempts.Add(new JuceEngineConfig
            {
                EnableConsoleLog = cfg.EnableConsoleLog,
                PreferredDriver = AudioDriver.Asio,
                OutputDeviceId = string.Empty,
                InputDeviceId = string.Empty,
                SampleRate = cfg.SampleRate,
                BufferFrames = cfg.BufferFrames,
                OutputChannels = cfg.OutputChannels,
                InputChannels = cfg.InputChannels,
            });
        }

        // First non-ASIO fallback: switch to the OS-native low-latency driver.
        // Drop both device ids: the ASIO device name almost never matches a
        // WASAPI endpoint name (e.g. "Focusrite USB ASIO" vs. "Lautsprecher
        // (Focusrite USB Audio)"), so passing it through just produces a
        // confusing "No such device" error in the log before the next attempt
        // succeeds. Let JUCE pick the OS defaults.
        if (cfg.PreferredDriver == AudioDriver.Asio)
        {
            attempts.Add(new JuceEngineConfig
            {
                EnableConsoleLog = cfg.EnableConsoleLog,
                PreferredDriver = AudioDriver.Wasapi,
                OutputDeviceId = string.Empty,
                InputDeviceId = string.Empty,
                SampleRate = cfg.SampleRate,
                BufferFrames = cfg.BufferFrames,
                OutputChannels = cfg.OutputChannels,
                InputChannels = cfg.InputChannels,
            });
        }

        // Last-chance fallback: ask JUCE to pick whatever it can find. The rate
        // and the block size go, because a device that refused them is why we are
        // here at all; the channel COUNTS stay, for the reason above.
        if (cfg.PreferredDriver != AudioDriver.Default)
        {
            attempts.Add(new JuceEngineConfig
            {
                EnableConsoleLog = cfg.EnableConsoleLog,
                PreferredDriver = AudioDriver.Default,
                OutputDeviceId = string.Empty,
                InputDeviceId = string.Empty,
                SampleRate = 0,
                BufferFrames = 0,
                OutputChannels = cfg.OutputChannels,
                InputChannels = cfg.InputChannels,
            });
        }

        return attempts;
    }

    /// <summary>
    /// Try to create a <see cref="JuceAudioEngine"/> with the requested driver,
    /// then progressively fall back if the device or driver is not available.
    /// Order: requested ? WASAPI (Windows) / CoreAudio (mac) / ALSA (linux) ? Default.
    /// Returns null only if every attempt failed.
    /// </summary>
    private JuceAudioEngine? CreateEngineWithFallback(JuceEngineConfig cfg, out JuceEngineConfig usedConfig)
    {
        usedConfig = cfg;
        var attempts = BuildEngineAttempts(cfg);

        Exception? lastError = null;
        for (var i = 0; i < attempts.Count; i++)
        {
            var attempt = attempts[i];
            try
            {
                var engine = new JuceAudioEngine(attempt);
                usedConfig = attempt;
                if (i > 0)
                {
                    if (attempt.PreferredDriver == cfg.PreferredDriver)
                    {
                        _logger?.Warning(AppLogCategory.Audio,
                            $"Requested ASIO device '{cfg.OutputDeviceId}' unavailable. " +
                            $"Audio engine started on the default ASIO device.");
                    }
                    else
                    {
                        _logger?.Warning(AppLogCategory.Audio,
                            $"Requested driver {cfg.PreferredDriver} (output='{cfg.OutputDeviceId}') unavailable. " +
                            $"Audio engine started on fallback driver {attempt.PreferredDriver}.");
                    }
                }
                return engine;
            }
            catch (Exception ex)
            {
                lastError = ex;
                _logger?.Warning(AppLogCategory.Audio,
                    $"JUCE engine_create failed (driver={attempt.PreferredDriver}, output='{attempt.OutputDeviceId}'): {ex.Message}");

                // ASIO drivers can take a short moment to release after a
                // failed open. Sleep briefly before the next attempt so the
                // retry doesn't bounce off the same "device busy" state.
                if (attempt.PreferredDriver == AudioDriver.Asio
                    && i + 1 < attempts.Count)
                {
                    Thread.Sleep(250);
                }
            }
        }

        if (lastError != null)
        {
            _logger?.Error(AppLogCategory.Audio,
                "JUCE engine could not be created with any driver.", lastError);
        }

        return null;
    }
}
