using DotCharter.Tone.Audio.Abstractions;
using DotCharter.Tone.Audio.Engine;
using DotCharter.Tone.Audio.Vst;

namespace DotCharter.Tests.Infrastructure;

/// <summary>
///     Inert implementation of <see cref="IAudioPlaybackService" /> for view-model
///     tests. Records the most recent seek, supports volume/EQ assignment, and
///     no-ops on every operation that would touch native audio plumbing.
/// </summary>
internal class FakeAudioPlaybackService : IAudioPlaybackService
{
    public double LastSeekToMs { get; private set; }
    public bool PlayCalled { get; private set; }
    public bool PauseCalled { get; private set; }
    public bool StopCalled { get; private set; }
    public List<double> SeekHistory { get; } = [];

    public double CurrentTimeMs { get; private set; }
    public double Volume { get; set; } = 1.0;
    public double SongVolume { get; set; } = 1.0;
    public double InputVolume { get; set; } = 1.0;
    public double MonitorVolume { get; set; } = 1.0;
    public double InputPitchShiftSemitones { get; set; }
    public int InputPitchShiftMode { get; set; }
    public double Pan { get; set; }
    public double PitchShift { get; set; }
    // ---- Parametric EQ (ABI v29) ------------------------------------------
    // A stand-in for an engine that HAS the six-band section, so a test can
    // exercise the live module. It records rather than processes: the DSP is
    // native and untestable from here, but "was the section pushed, and with
    // what" is exactly what the managed side owes.

    /// <summary>Set false to stand in for a build whose engine has no
    /// parametric EQ — the state the module must ship visibly disabled in.</summary>
    public bool SupportsParametricEq { get; set; } = true;

    /// <summary>The last section handed to <see cref="ApplyEq" />, and how many
    /// times. A push that never happened is the failure worth catching.</summary>
    public DotCharter.Tone.Audio.Equalizer.EqSection? LastEqSection { get; private set; }

    public int EqPushCount { get; private set; }

    public void ApplyEq(DotCharter.Tone.Audio.Equalizer.EqSection section)
    {
        LastEqSection = section;
        EqPushCount++;
    }

    /// <summary>Mirrors what a real engine would report: the audible-band
    /// predicate applied to whatever was last pushed, so a test that asserts the
    /// badge is asserting the same rule the native side runs.</summary>
    public int EngineEqActiveCount => LastEqSection?.ActiveCount ?? -1;

    /// <summary>
    ///     A guitar-shaped average spectrum: a body hump near 190 Hz, a midrange
    ///     shelf near 2 kHz and a little air, log-spaced 20 Hz … 20 kHz in dBFS.
    ///     <para>
    ///         SILENCE would be the more literal stand-in and it is the wrong
    ///         one: a flat −120 dB floor draws as a straight line at the bottom
    ///         of the well, so any test that renders the analyser would be
    ///         reviewing an empty box and its whole styling would never be
    ///         looked at. This is a test double for an engine, and producing a
    ///         plausible signal is what a double for an engine is for.
    ///     </para>
    /// </summary>
    /// <summary>
    ///     False models a STOPPED transport: the engine still answers, and every
    ///     bin sits on the floor.
    ///     <para>
    ///         Deliberately NOT the same as returning 0. "The engine cannot
    ///         answer" and "the engine answered, and there is nothing playing"
    ///         are different facts, and the plot has to reach its marked preview
    ///         spectrum from BOTH — a silence test that only covered the first
    ///         would leave a stopped song drawing a flat line at the bottom of
    ///         the well, which is the behaviour the preview replaces.
    ///     </para>
    /// </summary>
    public bool EqAnalyserHasSignal { get; set; } = true;

    public int ReadEqSpectrum(float[] destinationDb)
    {
        if (!SupportsParametricEq) return 0;

        if (!EqAnalyserHasSignal)
        {
            Array.Fill(destinationDb, -120f);
            return destinationDb.Length;
        }

        for (var i = 0; i < destinationDb.Length; i++)
        {
            var f = 20.0 * Math.Pow(1000.0, i / (double)Math.Max(1, destinationDb.Length - 1));
            var low = Math.Exp(-Math.Pow(Math.Log(f / 190.0), 2) / 1.05) * 0.86;
            var mid = Math.Exp(-Math.Pow(Math.Log(f / 2100.0), 2) / 2.3) * 0.50;
            var air = Math.Exp(-Math.Pow(Math.Log(f / 9000.0), 2) / 2.6) * 0.16;
            var level = Math.Clamp(low + mid + air + 0.05, 0.0, 1.0);
            destinationDb[i] = (float)(level * 72.0 - 72.0);
        }

        return destinationDb.Length;
    }

    public double HighPassFreq { get; set; } = 20;
    public double HighPassQ { get; set; } = 0.707;
    public double LowPassFreq { get; set; } = 20000;
    public double LowPassQ { get; set; } = 0.707;
    public double BandPassFreq { get; set; } = 1000;
    public double BandPassQ { get; set; } = 0.707;
    public double AudioDelayMs { get; set; }
    public double EstimatedOutputLatencyMs => 0;
    public double MeasuredOutputLatencyMs => 0;
    public int ActiveSampleRate { get; set; } = 44100;
    public int ActualBufferFrames { get; set; }
    /// <summary>
    ///     Mirrors <c>JuceAudioPlaybackService.PlaybackSpeed</c>, whose setter
    ///     DROPS the engine's scheduled-click queue: those clicks were mapped to
    ///     render frames using the old speed, so their frames no longer
    ///     correspond to their chart times. A fake that kept them would hide
    ///     exactly the re-anchoring bug this models.
    /// </summary>
    public double PlaybackSpeed
    {
        get => _playbackSpeed;
        set
        {
            if (Math.Abs(_playbackSpeed - value) < 1e-9) return;
            _playbackSpeed = value;
            ScheduledClicks.Clear();
            ClearScheduledClicks();
        }
    }

    private double _playbackSpeed = 1.0;
    // Publicly settable so tests can model "paused but seeking" directly without
    // driving the full Play/Pause transport (Play/Pause/Stop still assign it).
    public bool IsPlaying { get; set; }
    public bool IsVstEffectEnabled { get; set; }
    public double VstEffectVolume { get; set; } = 1.0;
    public bool IsVstInstrumentEnabled { get; set; }
    public double VstInstrumentVolume { get; set; } = 1.0;
    public double MidiOffsetMs { get; set; }
    public DeviceManager DeviceManager { get; } = new();
    public bool IsDeviceLost => false;
    public AudioDiagnostics Diagnostics { get; } = new();

    public event EventHandler<double>? PositionChanged;
#pragma warning disable CS0067 // Events exist purely to satisfy the interface contract for tests.
    public event EventHandler<int>? DeviceBufferFramesChanged;
    public event EventHandler? DeviceLost;
    public event EventHandler<AudioDeviceReconnectedEventArgs>? DeviceReconnected;
    public event EventHandler<bool>? InstrumentVstLoadChanged;
    public event EventHandler<(Guid SlotId, bool Loaded)>? GuitarVstSlotLoadChanged;
    public event EventHandler<Guid>? GuitarVstSlotEditorClosed;
    public event EventHandler? InstrumentVstEditorClosed;
#pragma warning restore CS0067

    public bool IsInstrumentVstLoaded => false;
    public bool IsInstrumentVstLoading => false;
    public bool IsGuitarVstSlotLoaded(Guid id) => false;
    public virtual int ReadInputCapture(float[] destination) => 0;
    /// <summary>The samples the next windowed read returns — how a level is put
    /// in front of the Mix popover's meter without an audio device.</summary>
    public float[] InputCaptureWindow { get; set; } = [];

    /// <summary>When set, the window a read returns for the LAST applied
    /// configuration: what a guitar on one jack sounds like as the configured
    /// input moves from jack to jack.</summary>
    public Func<RigConfig?, float[]>? InputCaptureFor { get; set; }

    public int ReadInputCaptureWindow(float[] destination)
    {
        var window = InputCaptureFor?.Invoke(AppliedConfigs.Count > 0 ? AppliedConfigs[^1] : null) ?? InputCaptureWindow;
        var n = Math.Min(destination.Length, window.Length);
        Array.Copy(window, destination, n);
        return n;
    }
    public virtual int ReadInputCapture(float[] destination, out ulong streamPosition)
    {
        streamPosition = 0;
        return 0;
    }
    public virtual bool TryGetInputCaptureSync(out ulong capturedSamples, out long hostTicks)
    {
        capturedSamples = 0;
        hostTicks = 0;
        return false;
    }
    /// <summary>0 — no engine — by default, which is what every existing caller
    /// assumed. Settable so a test can stand in for a LIVE input without opening
    /// a device: the tuner refuses to run at all below 1.</summary>
    public int InputSampleRate { get; set; }

    public Task InitializeAsync() => Task.CompletedTask;

    public Task ShutdownAsync() => Task.CompletedTask;

    /// <summary>Every config handed to <see cref="ApplyAudioConfiguration" />,
    /// in order - the evidence that a setting REACHED the engine rather than
    /// being saved to a file nothing re-reads.</summary>
    public List<RigConfig> AppliedConfigs { get; } = [];

    public void ApplyAudioConfiguration(RigConfig config) => AppliedConfigs.Add(config);

    public void EnsureAsioDevicesProbed(RigConfig config)
    {
    }

    public void EnsureWasapiDevicesProbed(RigConfig config)
    {
    }

    /// <summary>The WASAPI playback devices the fake machine lists.</summary>
    public List<string> WasapiOutputDevices { get; set; } = [];

    public List<string> GetWasapiOutputDevices() => [.. WasapiOutputDevices];

    /// <summary>The ASIO drivers installed on the fake machine. Like the real
    /// engine, they are only listed once the engine has been opened FOR ASIO —
    /// a list read before the re-open (or during it) comes back empty.</summary>
    public List<string> AsioDevicesInstalled { get; set; } = [];

    public List<string> GetAsioDevices() =>
        AppliedConfigs.Count > 0 && AppliedConfigs[^1].AudioDriver == "ASIO" ? [.. AsioDevicesInstalled] : [];

    public List<string> WasapiInputDevices { get; set; } = [];

    public List<string> AsioInputDevicesInstalled { get; set; } = [];

    public List<string> GetWasapiInputDevices() => [.. WasapiInputDevices];
    public List<string> GetAsioInputDevices() => [.. AsioInputDevicesInstalled];

    /// <summary>What <see cref="GetMidiOutputDevices" /> answers; winmm's shape, the mapper first.</summary>
    public IReadOnlyList<DotCharter.Tone.Audio.Midi.MidiOutputDeviceInfo> MidiOutputs { get; set; } =
        [new(-1, "Default MIDI Mapper")];

    public IReadOnlyList<DotCharter.Tone.Audio.Midi.MidiOutputDeviceInfo> GetMidiOutputDevices() => MidiOutputs;

    /// <summary>Jacks the fake "device" exposes — what the guitar-input pickers
    /// enumerate. 0 (the default) stands in for an engine that cannot say.</summary>
    public int InputDeviceChannelCount { get; set; }

    /// <summary>Every jack-count probe, in order. The real one is a LIVE device
    /// enumeration — the round trip the settings dialog runs a whole background
    /// worker for — so which driver it is asked about is part of the
    /// behaviour, not an implementation detail.</summary>
    /// <remarks>Concurrent, because every caller of this probe in the product
    /// runs it on a worker to keep the device round trip off the UI thread —
    /// a plain <c>List</c> here would be a data race the test itself
    /// introduces.</remarks>
    public System.Collections.Concurrent.ConcurrentQueue<(string Driver, string Device)>
        InputChannelCountProbes
    { get; } = new();

    public int GetInputDeviceChannelCount(string driver, string deviceName)
    {
        InputChannelCountProbes.Enqueue((driver, deviceName));
        return InputDeviceChannelCount;
    }

    /// <summary>The output count <see cref="GetOutputDeviceChannelCount" /> reports; 0 = unknown.</summary>
    public int OutputDeviceChannelCount { get; set; }

    /// <summary>Every (driver, device) the output pair picker asked the engine about.</summary>
    public System.Collections.Concurrent.ConcurrentQueue<(string Driver, string Device)> OutputChannelCountProbes
    { get; } = new();

    public int GetOutputDeviceChannelCount(string driver, string deviceName)
    {
        OutputChannelCountProbes.Enqueue((driver, deviceName));
        return OutputDeviceChannelCount;
    }

    /// <summary>The names <see cref="GetOutputChannelNames" /> reports; empty = the driver gives none.</summary>
    public IReadOnlyList<string> OutputChannelNames { get; set; } = [];

    /// <summary>The names <see cref="GetInputChannelNames" /> reports; empty = the driver gives none.</summary>
    public IReadOnlyList<string> InputChannelNames { get; set; } = [];

    public IReadOnlyList<string> GetOutputChannelNames(string driver, string deviceName) => OutputChannelNames;

    public IReadOnlyList<string> GetInputChannelNames(string driver, string deviceName) => InputChannelNames;

    /// <summary>
    ///     When set, <see cref="LoadAudioAsync" /> fails with it — a real audio
    ///     file the engine cannot decode. That is the case every project-load path
    ///     has to survive with its dirty-state declaration intact, because the
    ///     chart has already been applied by the time the audio is touched.
    /// </summary>
    public Exception? LoadAudioFailure { get; set; }

    /// <summary>Every path handed to <see cref="LoadAudioAsync" />, in order.
    /// The engine is what a caller can only observe through what it was ASKED to
    /// load — the silent-transport fallback, for one, never touches the project's
    /// audio identity, so the file name is the only evidence it ran.</summary>
    public List<string> LoadedPaths { get; } = [];

    public Task LoadAudioAsync(string filePath)
    {
        if (LoadAudioFailure != null) return Task.FromException(LoadAudioFailure);
        LoadedPaths.Add(filePath);
        return Task.CompletedTask;
    }

    /// <summary>Every blob handed to <see cref="LoadAudioFromMemoryAsync" />, in
    /// order, with what it held AT THE CALL — so a test can check what the engine
    /// was given and, afterwards, that the caller released it (#411).</summary>
    public List<(ReadOnlyMemory<byte> Bytes, byte[] Content, string FormatHint)> MemoryLoads { get; } = [];

    /// <summary>When set, <see cref="LoadAudioFromMemoryAsync" /> fails with it
    /// (after recording the call).</summary>
    public Exception? MemoryLoadFailure { get; set; }

    /// <summary>The display path of each <see cref="LoadAudioFromMemoryAsync" /> call,
    /// parallel to <see cref="MemoryLoads" /> (#435: the song's identity, whichever
    /// file the bytes were read from).</summary>
    public List<string?> MemoryLoadDisplayPaths { get; } = [];

    /// <summary>Runs inside each <see cref="LoadAudioFromMemoryAsync" />, as if during
    /// the native open — e.g. a device that changes rate mid-open.</summary>
    public Action? DuringMemoryLoad { get; set; }

    public Task LoadAudioFromMemoryAsync(ReadOnlyMemory<byte> bytes, string formatHint, string? displayPath = null)
    {
        MemoryLoads.Add((bytes, bytes.ToArray(), formatHint));
        MemoryLoadDisplayPaths.Add(displayPath);
        DuringMemoryLoad?.Invoke();
        return MemoryLoadFailure != null ? Task.FromException(MemoryLoadFailure) : Task.CompletedTask;
    }

    /// <summary>What the "engine" reports for the loaded file. 0 is an engine
    /// that decoded nothing; a test that cares about the timeline length sets
    /// it (or a subclass derives it from the path).</summary>
    public virtual double TotalAudioLengthMs { get; set; }

    public double GetTotalAudioLengthMs() => TotalAudioLengthMs;

    /// <summary>What both generators hand back. Empty by default — set it when a
    /// test needs a stem to have a waveform of its own (the mixed-waveform
    /// coordinator sums them).</summary>
    public WaveformSamples WaveformResult { get; set; } = WaveformSamples.Empty;

    /// <summary>Paths passed to <see cref="GenerateWaveformAsync" />, in order —
    /// lets a test assert the per-stem pyramid was generated once, not per pass.</summary>
    public List<string> WaveformRequests { get; } = [];

    /// <summary>When set, <see cref="GenerateWaveformAsync" /> awaits it before it
    /// returns, as if the native pass were still reading the file (#435).</summary>
    public Func<Task>? WaveformPass { get; set; }

    public Task<WaveformSamples> GenerateWaveformAsync(string filePath)
    {
        WaveformRequests.Add(filePath);
        return WaveformPass == null ? Task.FromResult(WaveformResult) : AfterPass(WaveformPass);

        async Task<WaveformSamples> AfterPass(Func<Task> pass)
        {
            await pass().ConfigureAwait(false);
            return WaveformResult;
        }
    }

    public void FreeAudio()
    {
    }

    /// <summary>Models a stale/unreadable VST3 folder: the real scan throws
    /// UnauthorizedAccessException / DirectoryNotFoundException there.</summary>
    public bool ThrowOnScanVstPlugins { get; set; }

    /// <summary>What the next scan finds. Settable so "Rescan plugin folders"
    /// can be shown to actually re-read: the list changes between calls exactly
    /// as it would when a plugin is installed while the app is running.</summary>
    public List<VstPluginInfo> VstPluginsOnDisk { get; set; } = [];

    /// <summary>How many times the VST3 folders have been walked. The one
    /// observable proof that a rescan did something.</summary>
    public int VstScans { get; private set; }

    public List<VstPluginInfo> ScanVstPlugins()
    {
        VstScans++;
        if (ThrowOnScanVstPlugins) throw new UnauthorizedAccessException(@"D:\VST3 is not accessible");
        return [.. VstPluginsOnDisk];
    }

    /// <summary>Every parent handle the app asked the driver panel to centre on.</summary>
    public List<IntPtr> AsioControlPanelParents { get; } = [];

    /// <summary>Set to make the panel refuse, so the caller's error path is reachable.</summary>
    public string? AsioControlPanelError { get; set; }

    public bool TryOpenAsioControlPanel(IntPtr parentWindowHandle, out string? error)
    {
        AsioControlPanelParents.Add(parentWindowHandle);
        error = AsioControlPanelError;
        return error is null;
    }

    /// <summary>Every chain the app has pushed, in order. A reorder is only
    /// real once the ENGINE has been told about it, so the test asserts on this
    /// rather than on the view model's own collection.</summary>
    public List<IReadOnlyList<VstChainEntry>> PushedGuitarChains { get; } = [];

    public void SetGuitarVstChain(IReadOnlyList<VstChainEntry> entries) => PushedGuitarChains.Add([.. entries]);

    public void ToggleGuitarVstSlotEditor(Guid id, bool open)
    {
    }

    public void SetGuitarVstSlotBypass(Guid id, bool bypass)
    {
    }

    public void LoadInstrumentVst(string pluginPath, bool openEditorAfterLoad = false)
    {
    }

    public void ToggleInstrumentVstEditor(bool open)
    {
    }

    public void SetMidiInstrument(int program)
    {
    }

    public void Play()
    {
        PlayCalled = true;
        IsPlaying = true;
    }

    public void Pause()
    {
        PauseCalled = true;
        IsPlaying = false;
    }

    public void Stop()
    {
        StopCalled = true;
        IsPlaying = false;
        CurrentTimeMs = 0;
    }

    public void SeekTo(double timeMs)
    {
        LastSeekToMs = timeMs;
        CurrentTimeMs = timeMs;
        // …and the RAW playhead, exactly as JuceAudioPlaybackService.SeekTo does:
        // seeking sets the transport to timeMs, so that is the song time the very
        // next block renders. Leaving it at the PRE-seek value hid the whole
        // re-entrant path below — a subscriber reached from this Invoke reads the
        // raw playhead, and with a stale one it read the position we just left.
        RawPlayheadMs = timeMs;
        SeekHistory.Add(timeMs);
        // Synchronous, like the real service — the loop wrap calls SeekTo from
        // INSIDE a PositionChanged handler, so this re-enters that handler on the
        // same thread. Modelling it is the point.
        PositionChanged?.Invoke(this, timeMs);
    }

    /// <summary>Clicks fired through the IMMEDIATE (fire-at-the-next-block, hence
    /// one-output-latency-late) path: 0 = beat, 1 = downbeat, 2 = clap.</summary>
    public List<int> ImmediateClicks { get; } = [];

    public void PlayMetronomeTick(bool isMeasure) => ImmediateClicks.Add(isMeasure ? 1 : 0);

    public void PlayClap() => ImmediateClicks.Add(2);

    /// <summary>Every (clickType, transportMs) handed to <see cref="TryScheduleClick"/>.</summary>
    public List<(int ClickType, double TransportMs)> ScheduledClicks { get; } = [];

    public int ClearScheduledClicksCount { get; private set; }

    /// <summary>Raw (uncompensated) playhead. The fake applies no latency, so it
    /// tracks <see cref="CurrentTimeMs"/> unless a test sets it explicitly.</summary>
    public double RawPlayheadMs { get; set; }

    public bool TryScheduleClick(int clickType, double transportMs)
    {
        ScheduledClicks.Add((clickType, transportMs));
        return true;
    }

    public void ClearScheduledClicks() => ClearScheduledClicksCount++;

    /// <summary>When non-zero, <see cref="TryScheduleCalibrationClap"/> succeeds and
    /// returns this host tick; when zero it fails (as if no engine).</summary>
    public long CalibrationClapAudibleTicks { get; set; }

    public int CalibrationClapCount { get; private set; }

    public bool TryScheduleCalibrationClap(out long audibleHostTicks)
    {
        CalibrationClapCount++;
        audibleHostTicks = CalibrationClapAudibleTicks;
        return CalibrationClapAudibleTicks != 0;
    }

    /// <summary>Every transport time handed to <see cref="UpdateMidiNotes"/>. The
    /// MIDI/VSTi preview is a trigger-NOW sink, so this must be the RAW playhead —
    /// recording it is how the axis is pinned.</summary>
    public List<double> MidiUpdateTimes { get; } = [];

    public void UpdateMidiNotes(double rawPlayheadMs, IList<GuitarNote> activeChartNotes,
        IList<Chord> activeChartChords, Arrangement currentArrangement, double bpm, IList<BeatMarker> beats,
        IList<HandShape>? handShapes = null) => MidiUpdateTimes.Add(rawPlayheadMs);

    public bool HasStems => StemMutes.Count > 0;
    public IReadOnlyList<string> LoadedStemNames => [.. StemMutes.Keys];
    public Dictionary<string, double> StemVolumes { get; } = [];
    public Dictionary<string, bool> StemMutes { get; } = [];

    public Dictionary<string, bool> StemSolos { get; } = [];
    public bool MasterMixMuted { get; private set; }

    /// <summary>Names passed to each <see cref="LoadStemsAsync" /> call, in
    /// order — lets a test assert WHICH stem set the engine ended up with.</summary>
    public List<string[]> StemLoadCalls { get; } = [];

    /// <summary>When set, <see cref="LoadStemsAsync" /> does not complete until
    /// this is completed — for exercising "a rebuild arrives while a load is in
    /// flight" without sleeping.</summary>
    public TaskCompletionSource? StemLoadGate { get; set; }

    /// <summary>When set, <see cref="LoadStemsAsync" /> throws it — a missing
    /// stem file, an engine failure, an IO error. Exists so a test can drive the
    /// stem-mixer rebuild loop's ERROR path, which faulted a discarded task with
    /// no log at all until 0.61.</summary>
    public Exception? StemLoadFault { get; set; }

    public async Task LoadStemsAsync(IReadOnlyList<StemLoadRequest> stems)
    {
        StemLoadCalls.Add(stems.Select(s => s.Name).ToArray());
        var gate = StemLoadGate;
        if (gate != null) await gate.Task.ConfigureAwait(false);
        if (StemLoadFault is not null) throw StemLoadFault;

        StemMutes.Clear();
        StemVolumes.Clear();
        StemSolos.Clear();
        foreach (var s in stems)
        {
            StemMutes[s.Name] = false;
            StemVolumes[s.Name] = 1.0;
            StemSolos[s.Name] = false;
        }
    }
    public void ClearStems() { StemMutes.Clear(); StemVolumes.Clear(); StemSolos.Clear(); }
    public void SetStemVolume(string name, double linearGain) => StemVolumes[name] = linearGain;
    public void SetStemMute(string name, bool muted) => StemMutes[name] = muted;
    public void SetStemSolo(string name, bool soloed) => StemSolos[name] = soloed;
    public void SetMasterMixMuted(bool muted) => MasterMixMuted = muted;

    public void Dispose()
    {
    }
}
