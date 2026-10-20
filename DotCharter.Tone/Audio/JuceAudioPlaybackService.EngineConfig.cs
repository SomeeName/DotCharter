// Partial: RigConfig → JuceEngineConfig, the in-place reconfigure, and the one-shot 48 kHz request a neural amp triggers.
using JuceSharp;
using JuceDriver = JuceSharp.AudioDriver;

namespace DotCharter.Tone.Audio;

public sealed partial class JuceAudioPlaybackService
{
    // --- 48 kHz-for-neural-amp policy ----------
    // NAM/AIDA-X captures are 48 kHz-native. When a namamp node appears in the DSP
    // chain we REQUEST the 48 kHz device rate in place (never a teardown, never
    // overwrites the user's persisted AudioSampleRate).
    // **This is an OPTIMIZATION, not a correctness requirement.** `namamp` resamples
    // around the capture itself (windowed-sinc, flat within 0.005 dB 20 Hz-16 kHz,
    // ~1.4 ms round trip at 44.1 kHz), so a device that refuses 48 kHz still HEARS
    // the amp — the request just buys back that 1.4 ms and the converter's CPU by
    // letting the capture run at the device rate. Failure is inaudible.
    // Do NOT delete the request to "simplify": zero added latency is still the
    // better outcome when the device can give it, and this path is source-pinned by
    // EngineConfigPolicyTests for the v0.17.1 teardown bug.
    private const int NeuralAmpSampleRate = 48000;

    private const string NeuralAmpModuleTypeId = "namamp";

    private readonly object _neuralRateGate = new();

    // Reset on every engine (re)create so a fresh device gets exactly one 48 kHz
    // attempt; prevents fighting a device physically stuck at 44.1 kHz.
    // Written from three threads under three different (or no) locks: the
    // NodesChanged handler, the _neuralRateGate worker, and ReplaceEngineGated
    // under _gate. volatile so the one-shot guard is actually observed.
    private volatile bool _neuralRateAttempted;

    public void ApplyAudioConfiguration(RigConfig config)
        => ApplyAudioConfiguration(config, forceEngineReplace: false);

    /// <summary>
    /// Core apply path. <paramref name="forceEngineReplace"/> is used by the
    /// device watchdog when recovering from a lost device: the in-place
    /// reconfigure path can silently "succeed" against a dead device (JUCE
    /// sees no setup delta), so recovery always tears the engine down and
    /// rebuilds it from scratch.
    /// </summary>
    private void ApplyAudioConfiguration(RigConfig config, bool forceEngineReplace)
    {
        // Nothing may open a device once dispose has started — not a settings
        // apply racing window close, not the watchdog's recovery. The deeper
        // guards in ReplaceEngineGated are the backstop; this is the cheap one
        // that also keeps _watchdogConfig from being re-armed on the way out.
        if (IsDisposing) return;
        // Let the watchdog know a (manual or recovery) restart is in flight
        // so its probe timer never fights a settings-dialog apply.
        Interlocked.Increment(ref _configApplyDepth);
        try
        {
            ApplyAudioConfigurationCore(config, forceEngineReplace);
        }
        finally
        {
            _watchdogConfig = config;
            if (!forceEngineReplace)
            {
                // A manual apply supersedes whatever the watchdog thought it
                // knew (new device, new driver, ...). Start observing fresh.
                _recovery.Reset();
                _deviceLost = false;
                _outputTopologySignature = null;
                // Re-scan on the very next tick: a manual apply just changed the
                // device world we are watching, so the thinned healthy-tick
                // enumeration must not skip past it.
                _healthyTickIndex = 0;
            }
            Interlocked.Decrement(ref _configApplyDepth);
        }
    }

    private void ApplyAudioConfigurationCore(RigConfig config, bool forceEngineReplace)
    {
        // Out-of-process VST hosting toggle FIRST: the engine bring-up below
        // drains any queued plugin loads (startup restore), and those must
        // already route to the sandbox when the flag is on — otherwise the
        // very first startup pays for an in-proc load that the mode switch
        // immediately tears down and repeats out-of-process.
        ApplySandboxMode(config.OutOfProcessVst);

        // NOT the chart synth's instruments: they follow the player
        // profile (and a playing song tone's override), set by DotCharter.Services'
        // ChartSynthInstrumentCoordinator. Reading the config here would put the
        // app-wide value back on every settings apply and watchdog rebuild; the
        // service's own request is what the per-player attach replays.

        // …and the process priority the "Process priority" row promises. This is
        // the row's ONLY consumer: the flag is loaded, saved and
        // compared for routing changes by AudioSettingsViewModel and read by
        // nothing else, so a switch that ships ON by default and advertises
        // "helps avoid glitches / dropouts" would otherwise do nothing whatsoever. It lives here
        // for the reason the seat baseline does — startup, every settings apply and
        // the watchdog's recovery rebuild all reach this one method.
        ApplyProcessPriority(config.EnableHighProcessPriority);

        var driver = config.AudioDriver.Equals("ASIO", StringComparison.OrdinalIgnoreCase)
            ? JuceDriver.Asio
            : JuceDriver.Wasapi;

        var outputDeviceId = ResolveOutputDeviceId(config, driver);
        var inputDeviceId = config.EnableInput
            ? ResolveInputDeviceId(config, driver)
            : string.Empty;

        // ASIO is single-driver-exclusive on Windows: only one ASIO driver
        // can be active at a time, so the input and output device names MUST
        // refer to the same physical interface. If the user has e.g.
        // output='Focusrite USB ASIO' / input='Focusrite Thunderbolt ASIO'
        // (typical when two interfaces are plugged in and the saved config
        // mixed them), the open will fail with "No such device". Force the
        // input to match the output here so monitoring still works on the
        // same interface; the user can flip them via the settings UI.
        if (driver == JuceDriver.Asio
            && !string.IsNullOrEmpty(outputDeviceId)
            && !string.IsNullOrEmpty(inputDeviceId)
            && !string.Equals(outputDeviceId, inputDeviceId, StringComparison.OrdinalIgnoreCase))
        {
            _logger?.Warning(AppLogCategory.Audio,
                $"ASIO requires the same driver for input and output. " +
                $"Saved input='{inputDeviceId}' differs from output='{outputDeviceId}'. " +
                $"Forcing input to '{outputDeviceId}' for this engine instance.");
            inputDeviceId = outputDeviceId;
        }

        // WASAPI device labels carry an optional " (Exclusive)" / " (Low Latency)"
        // suffix (see LabelWasapiModes). Split it off so OutputDeviceId is the
        // bare JUCE device name and OutputTypeName is the matching
        // AudioIODeviceType ("Windows Audio (Exclusive Mode)" etc.).
        var outputTypeName = string.Empty;
        var inputTypeName = string.Empty;
        if (driver == JuceDriver.Wasapi)
        {
            (outputDeviceId, outputTypeName) = ParseWasapiLabel(outputDeviceId);
            (inputDeviceId, inputTypeName) = ParseWasapiLabel(inputDeviceId);
        }

        var cfg = new JuceEngineConfig
        {
            PreferredDriver = driver,
            SampleRate = config.AudioSampleRate,
            BufferFrames = config.AudioBufferFrames,
            OutputChannels = 2,
            // ONE expression, shared with every consumer that has to know how
            // many input channels are open — see LiveInputSeatLayout.OpenInputChannels.
            InputChannels = Abstractions.LiveInputSeatLayout.OpenInputChannels(config),
            OutputChannelMask = driver == JuceDriver.Asio
                ? BuildStereoMask(config.AsioOutputChannelLeft, config.AsioOutputChannelRight)
                : 0u,
            InputChannelMask = (config.EnableInput && driver == JuceDriver.Asio)
                ? (config.AsioInputMono
                    ? BuildMonoMask(config.AsioInputChannelLeft)
                    : BuildStereoMask(config.AsioInputChannelLeft, config.AsioInputChannelRight))
                : 0u,
            OutputDeviceId = outputDeviceId,
            InputDeviceId = inputDeviceId,
            OutputTypeName = outputTypeName,
            InputTypeName = inputTypeName,
        };

        if (forceEngineReplace) ReplaceEngine(cfg);
        else ApplyEngineConfig(cfg);

        // How the OPEN input channels are shared out between live seats — the
        // resting per-player routing, derived from the same three config fields
        // the InputChannels/InputChannelMask above are. It lives here so every
        // producer of an engine config reaches it: startup, a settings apply and
        // the watchdog's recovery rebuild. A single run-scoped writer of the seat count would leave the engine,
        // outside that run, on its
        // single-seat stereo fill with one guitar in each ear.
        // An override in flight is NOT disturbed: the baseline is recorded under
        // it and taken up when it ends.
        SetLiveInputBaseline(LiveInputSeatLayout.FromInputConfig(config));

        // Push user-tunable audio/MIDI offsets into the running engine so the
        // position interpolator + MIDI scheduler can apply them right away.
        AudioDelayMs = config.AudioDelayMs;
        MidiOffsetMs = config.MidiDelayMs;

        // Drop-tune pedal engine selection: an atomic engine-side swap, no
        // device restart involved. Applied here so both startup (the initial
        // ApplyAudioConfiguration) and settings changes push it through.
        // 0 = Tight (10 ms lag ceiling), 1 = Balanced (20 ms), 2 = Smooth (35 ms).
        // Unknown/legacy values ("rubberband", "timedomain") land on Balanced.
        // Shared with the settings UI so the live push and this after-restart read
        // can never disagree again — they did, and Smooth was unreachable live.
        InputPitchShiftMode = DropTunerEngineMap.FromConfigString(config.DropTunerEngine);

        // Apply / clear the streaming mirror against the (possibly new) engine.
        _mirrorDeviceName = ResolveMirrorDeviceName(config);
        ApplyMirrorDevice();

        // …and the master-bus ceiling, for the same reason and in the same
        // place: the engine ships the limiter OFF (ABI v35), so unless the app
        // opts in here nothing bounds the sum of the song bus, every seat's
        // monitor mix, the click bus and both instrument legs — and the mirror
        // the line above just opened carries FLOAT, so samples past ±1.0 survive
        // into OBS / Discord and wrap in their encoder.
        _masterLimiter = Engine.MasterLimiterSetting.FromConfig(config);
        ApplyMasterLimiter();

        // (Re)open the configured MIDI output device.
        ApplyMidiDevice(config.MidiOutputDevice);
    }

    /// <summary>
    ///     The BARE JUCE device name the streaming mirror should open, or
    ///     <c>null</c> for "closed".
    ///     <para>
    ///         <b>The mode suffix has to come off here.</b> The mirror dropdown is
    ///         filled from <see cref="GetWasapiOutputDevices" />, i.e. from
    ///         <c>LabelWasapiModes</c>, so a device JUCE registers under more than
    ///         one WASAPI type reaches the config as
    ///         <c>"Speakers (Realtek) (Exclusive)"</c> — a LABEL, not a device.
    ///         <c>MirrorOutput::open</c> assigns it straight to
    ///         <c>AudioDeviceSetup.outputDeviceName</c>, so the open failed with
    ///         "No such device" and the mirror silently stayed off for exactly the
    ///         users whose interface exposes several modes. The suffix could not
    ///         have meant anything to it either: the mirror always forces the
    ///         shared "Windows Audio" type (ASIO would deadlock against the
    ///         primary engine on the same hardware), so the MODE is the primary
    ///         device's business and only the NAME is the mirror's.
    ///     </para>
    /// </summary>
    internal static string? ResolveMirrorDeviceName(RigConfig config)
    {
        ArgumentNullException.ThrowIfNull(config);
        if (!config.EnableStreamingMirror) return null;
        var label = config.StreamingMirrorDeviceName;
        if (string.IsNullOrWhiteSpace(label)) return null;
        var bare = ParseWasapiLabel(label).DeviceName;
        return string.IsNullOrWhiteSpace(bare) ? null : bare;
    }

    private void ApplyMirrorDevice()
    {
        var engine = _engine;
        if (engine == null) return;
        var target = _mirrorDeviceName;
        try
        {
            if (engine.SetMirrorDevice(target))
            {
                if (!string.IsNullOrEmpty(target))
                    _logger?.Info(AppLogCategory.Audio, $"Streaming mirror enabled. Device='{target}'.");
                else
                    _logger?.Info(AppLogCategory.Audio, "Streaming mirror disabled.");
            }
            else
            {
                _logger?.Warning(AppLogCategory.Audio,
                    $"Failed to open streaming mirror device '{target ?? "<none>"}'. The mirror will stay off.");
            }
        }
        catch (Exception ex)
        {
            _logger?.Error(AppLogCategory.Audio, "Streaming mirror apply threw.", ex);
        }
    }

    /// <summary>
    ///     Push <c>_masterLimiter</c> at the running engine.
    ///     <para>
    ///         <b>The latency cache has to be dropped afterwards.</b> The limiter's
    ///         look-ahead delay line is only in the path while the stage is ON, so
    ///         <c>OutputLatencySamples</c> — and with it
    ///         <see cref="EstimatedOutputLatencyMs" />, which the 250 Hz position
    ///         tick and both renderers read — MOVES on a toggle (48 samples at
    ///         48 kHz). The cache expires on its own within
    ///         <c>OutputLatencyCache.RefreshInterval</c>, but a second of the
    ///         playhead reading the other engine's latency is exactly the kind of
    ///         drift nobody would trace back to here.
    ///     </para>
    /// </summary>
    private void ApplyMasterLimiter()
    {
        var engine = _engine;
        if (engine == null) return;
        var limiter = _masterLimiter;
        try
        {
            engine.SetMasterLimiter(limiter.Enabled, limiter.CeilingDb, limiter.ReleaseMs);
            _latencyCache.Invalidate();
            _logger?.Info(AppLogCategory.Audio, limiter.Enabled
                ? $"Master limiter enabled. ceiling={limiter.CeilingDb:0.##} dBFS, release={limiter.ReleaseMs:0} ms."
                : "Master limiter disabled; the master bus runs unlimited.");
        }
        catch (Exception ex)
        {
            _logger?.Warning(AppLogCategory.Audio,
                $"Master limiter apply failed ({ex.GetType().Name}: {ex.Message}). The bus runs unlimited.");
        }
    }

    private static uint BuildMonoMask(int channelIndex)
    {
        if (channelIndex is < 0 or > 31) return 0u;
        return 1u << channelIndex;
    }

    private static uint BuildStereoMask(int leftIndex, int rightIndex)
    {
        var m = 0u;
        if (leftIndex is >= 0 and <= 31) m |= 1u << leftIndex;
        if (rightIndex is >= 0 and <= 31) m |= 1u << rightIndex;
        return m;
    }

    private static string ResolveOutputDeviceId(RigConfig cfg, JuceDriver driver)
    {
        return driver == JuceDriver.Asio
            ? cfg.AsioOutputDeviceName
            : cfg.WasapiOutputDeviceName;
    }

    private static string ResolveInputDeviceId(RigConfig cfg, JuceDriver driver)
    {
        return driver == JuceDriver.Asio
            ? cfg.AsioInputDeviceName
            : cfg.WasapiInputDeviceName;
    }

    /// <summary>
    /// Apply an incoming engine config. Tries the lightweight in-place
    /// reconfigure path first (just changes sample rate / buffer size /
    /// device name on the existing AudioDeviceManager — preserves loaded
    /// VSTs, transport sources, mixer state). Falls back to a full
    /// ReplaceEngine cycle when the driver type, channel layout or channel
    /// mask actually changes, or when the running ABI is too old for the
    /// reconfigure export.
    ///
    /// <paramref name="allowReplaceFallback"/> = false makes a failed in-place
    /// reconfigure a logged no-op instead. The 48 kHz-for-neural-amp request
    /// needs that: it is a best-effort optimisation the user never asked for,
    /// and it must never cost them a full engine teardown.
    /// </summary>
    private void ApplyEngineConfig(JuceEngineConfig cfg, bool allowReplaceFallback = true)
    {
        // Serialized against every other engine-config change — the settings
        // dialog's apply, the watchdog's recovery rebuild, and the NAM 48 kHz
        // request can all arrive on different threads. Unserialized, two of them
        // could be inside engine.Reconfigure at once and both write
        // _activeEngineConfig (last writer wins); if the LOSER's config is the
        // one that actually took, _activeEngineConfig does not describe the
        // open device — which then feeds CanReconfigureInPlace and the
        // watchdog's EngineOnDesiredDevice probe, so a spurious "not on the
        // desired device" can trigger a recovery rebuild. Worse, a settings
        // apply could be inside Reconfigure while ReplaceEngineGated disposes
        // the very engine it is reconfiguring.
        //
        // Deliberately NOT _gate: that must not be held across the reconfigure
        // (see the media-open rule). Monitor, not a semaphore, because
        // ApplyEngineConfig falls through to ReplaceEngine -> ReplaceEngineCore,
        // which takes the same gate — same-thread reentrancy has to be free.
        // Bounded like the _vstGate wait for the same reason: a wedged device
        // change must not brick the next one forever.
        var configGateHeld = Monitor.TryEnter(_engineConfigGate, TimeSpan.FromSeconds(60));
        if (!configGateHeld)
        {
            _logger?.Warning(AppLogCategory.Audio,
                "Engine reconfigure proceeding without the config gate (another engine change appears wedged after 60 s).");
        }

        try
        {
            ApplyEngineConfigGated(cfg, allowReplaceFallback);
        }
        finally
        {
            if (configGateHeld) Monitor.Exit(_engineConfigGate);
        }
    }

    private void ApplyEngineConfigGated(JuceEngineConfig cfg, bool allowReplaceFallback)
    {
        var engine = _engine;
        var active = _activeEngineConfig;
        if (engine != null && active != null && CanReconfigureInPlace(active, cfg))
        {
            try
            {
                var sr = cfg.SampleRate > 0 ? cfg.SampleRate : 0;
                var buf = cfg.BufferFrames > 0 ? cfg.BufferFrames : 0;
                var outDev = string.IsNullOrEmpty(cfg.OutputDeviceId) ? null : cfg.OutputDeviceId;
                var inDev = string.IsNullOrEmpty(cfg.InputDeviceId) ? null : cfg.InputDeviceId;

                const string taskId = "audio-engine-reconfigure";
                _statusBar?.BeginTask(taskId, _localization["Progress_UpdatingAudioDevice"], indeterminate: true);
                try
                {
                    engine.Reconfigure(sr, buf, outDev, inDev);
                    _activeEngineConfig = cfg;
                    // Same engine reference, new device: the cached output
                    // latency is for the old one.
                    _latencyCache.Invalidate();
                    // …and the driver may not have honoured the requested block
                    // size (Focusrite ASIO is pinned from its own control panel).
                    // Tell the buffer row and the status bar what it actually
                    // negotiated.
                    ReportDeviceBufferFrames(engine);
                    // An in-place reconfigure keeps the SAME native engine handle, but
                    // re-assert the DSP-rig attachment defensively so the in-house tone
                    // rig is guaranteed pointed at a live engine whenever one exists
                    // (idempotent when the handle is unchanged). The per-player input
                    // routing rides along: a Reconfigure re-opens the device, and the
                    // seat channels are not part of the config it re-applies.
                    try { AttachPerPlayerStateToEngine(engine); }
                    catch (Exception ex) { _logger?.Warning(AppLogCategory.Audio, $"DSP rig re-attach after reconfigure failed: {ex.Message}"); }
                    // Re-query the VSTi's reported latency: many plugins
                    // re-report after prepareToPlay with a new sample rate.
                    if (_instrumentPlugin != null)
                        _instrumentLatencyMs = ComputeInstrumentLatencyMs(engine, _instrumentPlugin);
                    // Sandbox child (if any) must re-prepare its plugins for
                    // the new sample rate / block size.
                    NotifySandboxEngineReconfigured();
                    _logger?.Info(AppLogCategory.Audio,
                        $"JUCE engine reconfigured in-place. sr={engine.SampleRate}, buf={engine.BufferFrames}, output='{cfg.OutputDeviceId}', input='{cfg.InputDeviceId}'.");
                    return;
                }
                finally
                {
                    _statusBar?.CompleteTask(taskId);
                }
            }
            catch (EntryPointNotFoundException)
            {
                // ABI v7 or older DLL — silently fall back to ReplaceEngine.
                _logger?.Info(AppLogCategory.Audio,
                    "Native DLL predates ABI v8; falling back to full engine restart for settings change.");
            }
            catch (Exception ex)
            {
                if (!allowReplaceFallback)
                {
                    _logger?.Warning(AppLogCategory.Audio,
                        $"In-place reconfigure failed ({ex.Message}); leaving the engine as it is.");
                    return;
                }
                _logger?.Warning(AppLogCategory.Audio,
                    $"In-place reconfigure failed ({ex.Message}); falling back to full engine restart.");
            }
        }
        if (!allowReplaceFallback) return;
        ReplaceEngine(cfg);
    }

    // ── 48 kHz-for-neural-amp policy ───────────────────────────────────────────

    /// <summary>Chain-composition changed. If a neural-amp (namamp) node is now
    /// present and the device isn't at 48 kHz, request 48 kHz off the UI thread
    /// (an in-place reconfigure can spend 100 ms+ on the device). Throw-free.</summary>
    private void OnDspNodesChanged()
    {
        // Cheap synchronous gate first so the common case (no namamp / already at
        // 48 kHz / already attempted) never spawns a task.
        try
        {
            if (IsDisposing) return;
            var chain = _dspChain;
            var engine = _engine;
            if (chain == null || engine == null) return;
            if (_neuralRateAttempted) return;
            if (ActiveSampleRate == NeuralAmpSampleRate) { _neuralRateAttempted = true; return; }
            if (!ChainHasNeuralAmp(chain)) return;
        }
        catch { return; }

        _ = Task.Run(RequestNeuralAmpSampleRate);
    }

    private static bool ChainHasNeuralAmp(Dsp.IDspChainService chain)
    {
        try
        {
            foreach (var n in chain.GetNodes())
            {
                if (string.Equals(n.ModuleTypeId, NeuralAmpModuleTypeId, StringComparison.Ordinal))
                    return true;
            }
        }
        catch { /* ignored */ }
        return false;
    }

    /// <summary>Best-effort in-place request for the 48 kHz device rate the NAM
    /// models are native to. Serialized (one attempt per engine), never tears the
    /// engine down, never persists to RigConfig, and swallows any failure so a
    /// device that refuses 48 kHz just keeps running at its current rate — where,
    /// the module resamples around the capture rather than bypassing,
    /// so the only cost of a refusal is ~1.4 ms of latency and the converter's
    /// CPU.</summary>
    private void RequestNeuralAmpSampleRate()
    {
        lock (_neuralRateGate)
        {
            try
            {
                if (IsDisposing || _neuralRateAttempted) return;
                var engine = _engine;
                var active = _activeEngineConfig;
                if (engine == null || active == null) return;
                if (ActiveSampleRate == NeuralAmpSampleRate) { _neuralRateAttempted = true; return; }
                if (_dspChain == null || !ChainHasNeuralAmp(_dspChain)) return;

                _neuralRateAttempted = true; // one shot per engine, even if the device refuses
                _logger?.Info(AppLogCategory.Audio,
                    $"Neural-amp (namamp) node present; requesting {NeuralAmpSampleRate} Hz device rate " +
                    $"(engine currently at {ActiveSampleRate} Hz). NAM captures are 48 kHz-native.");

                var cfg = CloneWithSampleRate(active, NeuralAmpSampleRate);
                // Cooperate with the device watchdog exactly like a settings apply.
                Interlocked.Increment(ref _configApplyDepth);
                // allowReplaceFallback: false is what makes the "never tears the
                // engine down" promise above actually true. A device pinned to
                // 44.1 kHz (WASAPI exclusive, or an ASIO panel setting) makes
                // Reconfigure throw, and the default fallback answers that by
                // destroying and rebuilding the engine — killing playback,
                // dropping the stems and reloading every VST, because the user
                // added a NAM pedal. ReplaceEngineGated also clears
                // _neuralRateAttempted, so the rebuilt (still-44.1 kHz) engine
                // would do it all again on the next pedal add or remove.
                try { ApplyEngineConfig(cfg, allowReplaceFallback: false); }
                finally { Interlocked.Decrement(ref _configApplyDepth); }

                if (ActiveSampleRate != NeuralAmpSampleRate)
                {
                    _logger?.Info(AppLogCategory.Audio,
                        $"Device did not honor the {NeuralAmpSampleRate} Hz request (still {ActiveSampleRate} Hz); " +
                        "the neural amp will resample around the capture instead (~1.4 ms extra latency). " +
                        "Select a 48 kHz-capable device / mode in Audio settings to avoid it.");
                }
            }
            catch (Exception ex)
            {
                _logger?.Warning(AppLogCategory.Audio,
                    $"48 kHz request for neural amp failed (non-fatal): {ex.Message}");
            }
        }
    }

    /// <summary>The external process insert's ring rate (ABI v47): the native side does
    /// not convert, so an insert whose ring runs at another rate than the device asks for
    /// that rate IN PLACE, exactly like the neural-amp request (never a teardown, never
    /// persisted to RigConfig, serialized on the same gate). True when the device now
    /// runs at <paramref name="sampleRate" />; a device that cannot is logged and the
    /// insert's open stays refused with the reason. Never call it under <c>_gate</c>.</summary>
    private bool RequestExternalInsertSampleRate(int sampleRate)
    {
        lock (_neuralRateGate)
        {
            try
            {
                if (IsDisposing || sampleRate <= 0) return false;
                var active = _activeEngineConfig;
                if (_engine == null || active == null) return false;
                if (ActiveSampleRate == sampleRate) return true;
                _logger?.Info(AppLogCategory.Audio,
                    $"External process insert: requesting {sampleRate} Hz device rate (engine currently at {ActiveSampleRate} Hz).");
                Interlocked.Increment(ref _configApplyDepth);
                try { ApplyEngineConfig(CloneWithSampleRate(active, sampleRate), allowReplaceFallback: false); }
                finally { Interlocked.Decrement(ref _configApplyDepth); }
                if (ActiveSampleRate == sampleRate) return true;
                _logger?.Warning(AppLogCategory.Audio,
                    $"Device did not honor the {sampleRate} Hz request for the external process insert (still {ActiveSampleRate} Hz). " +
                    "Select a device / mode that runs at that rate in Audio settings.");
                return false;
            }
            catch (Exception ex)
            {
                _logger?.Warning(AppLogCategory.Audio, $"{sampleRate} Hz request for the external process insert failed: {ex.Message}");
                return false;
            }
        }
    }

    /// <summary>Copies an engine config with only the sample rate changed — so
    /// <see cref="CanReconfigureInPlace"/> sees a rate-only delta and takes the
    /// in-place <c>engine.Reconfigure</c> path (JuceEngineConfig is init-only and
    /// lives in the submodule, so we can't add a <c>with</c> there).
    /// <para>
    /// <b>Every property must be carried over.</b> A field this forgets shows up
    /// as a delta to <see cref="CanReconfigureInPlace"/>, which turns the NAM
    /// 48 kHz request into a full engine REPLACE the moment anyone drops the
    /// <c>allowReplaceFallback: false</c> guard. Pinned property-by-property by
    /// <c>EngineConfigPolicyTests</c> so a new field on the submodule's config
    /// cannot be missed here.
    /// </para></summary>
    internal static JuceEngineConfig CloneWithSampleRate(JuceEngineConfig c, int sampleRate) => new()
    {
        EnableConsoleLog = c.EnableConsoleLog,
        PreferredDriver = c.PreferredDriver,
        SampleRate = sampleRate,
        BufferFrames = c.BufferFrames,
        OutputChannels = c.OutputChannels,
        InputChannels = c.InputChannels,
        OutputChannelMask = c.OutputChannelMask,
        InputChannelMask = c.InputChannelMask,
        OutputDeviceId = c.OutputDeviceId,
        InputDeviceId = c.InputDeviceId,
        OutputTypeName = c.OutputTypeName,
        InputTypeName = c.InputTypeName,
    };

    internal static bool CanReconfigureInPlace(JuceEngineConfig active, JuceEngineConfig incoming)
    {
        // Anything that touches the driver type, channel layout or channel
        // mask requires JUCE to re-init the AudioIODeviceType from scratch.
        // setAudioDeviceSetup() can change the active device + SR + buffer
        // on the same type, but switching types (WASAPI ↔ ASIO ↔
        // DirectSound) reliably needs a full destroy + create.
        if (active.PreferredDriver != incoming.PreferredDriver) return false;
        if (active.OutputChannels != incoming.OutputChannels) return false;
        if (active.InputChannels != incoming.InputChannels) return false;
        if (active.OutputChannelMask != incoming.OutputChannelMask) return false;
        if (active.InputChannelMask != incoming.InputChannelMask) return false;
        if (!string.Equals(active.OutputTypeName,
                           incoming.OutputTypeName, StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        if (!string.Equals(active.InputTypeName,
                           incoming.InputTypeName, StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        return true;
    }
}
