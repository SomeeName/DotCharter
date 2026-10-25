using DotCharter.Tone.Audio.Engine;

namespace DotCharter.Tone.Audio;

/// <summary>
/// No-op <see cref="IAudioPlaybackService"/>. Used during the JUCE migration
/// while the new native graph is being built out. The full surface area
/// resolves but every audio-producing call is a deliberate no-op.
/// </summary>
public sealed class NullAudioPlaybackService : IAudioPlaybackService
{
    public double CurrentTimeMs => 0;
    public double Volume { get; set; }
    public double SongVolume { get; set; } = 1.0;
    public double InputVolume { get; set; }
    public double MonitorVolume { get; set; }
    public double InputPitchShiftSemitones { get; set; }
    public int InputPitchShiftMode { get; set; }
    public double Pan { get; set; }
    public double PitchShift { get; set; }
    public double HighPassFreq { get; set; }
    public double HighPassQ { get; set; }
    public double LowPassFreq { get; set; }
    public double LowPassQ { get; set; }
    public double BandPassFreq { get; set; }
    public double BandPassQ { get; set; }
    public double AudioDelayMs { get; set; }
    public double EstimatedOutputLatencyMs => 0;
    public double MeasuredOutputLatencyMs => 0;
    public int ActiveSampleRate => 0;
    public int ActualBufferFrames => 0;
    public double PlaybackSpeed { get; set; } = 1.0;
    public bool IsPlaying => false;
    public long OutputFramesWritten => -1;
    public bool IsVstEffectEnabled { get; set; }
    public double VstEffectVolume { get; set; }
    public bool IsVstInstrumentEnabled { get; set; }
    public double VstInstrumentVolume { get; set; }
    public double MidiOffsetMs { get; set; }
    public DeviceManager DeviceManager { get; } = new();
    public bool IsDeviceLost => false;
    public AudioDiagnostics Diagnostics { get; } = new();

#pragma warning disable CS0067 // events are intentionally never raised by the null impl
    public event EventHandler<double>? PositionChanged;
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
    public int ReadInputCapture(float[] destination) => 0;
    public int ReadInputCaptureWindow(float[] destination) => 0;
    public int ReadInputCapture(float[] destination, out ulong streamPosition)
    {
        streamPosition = 0;
        return 0;
    }
    public bool TryGetInputCaptureSync(out ulong capturedSamples, out long hostTicks)
    {
        capturedSamples = 0;
        hostTicks = 0;
        return false;
    }
    public int InputSampleRate => 0;

    public Task InitializeAsync() => Task.CompletedTask;
    public Task ShutdownAsync() => Task.CompletedTask;
    public void ApplyAudioConfiguration(RigConfig config) { }
    public void EnsureAsioDevicesProbed(RigConfig config) { }
    public void EnsureWasapiDevicesProbed(RigConfig config) { }
    public bool TryOpenAsioControlPanel(IntPtr parentWindowHandle, out string? error)
    {
        error = "Audio engine not yet implemented (Phase-1 stub).";
        return false;
    }

    public List<string> GetWasapiOutputDevices() => [];
    public List<string> GetAsioDevices() => [];
    public List<string> GetWasapiInputDevices() => [];
    public List<string> GetAsioInputDevices() => [];
    public int GetInputDeviceChannelCount(string driver, string deviceName) => 0;
    public int GetOutputDeviceChannelCount(string driver, string deviceName) => 0;
    public IReadOnlyList<string> GetOutputChannelNames(string driver, string deviceName) => [];
    public IReadOnlyList<string> GetInputChannelNames(string driver, string deviceName) => [];
    public IReadOnlyList<Midi.MidiOutputDeviceInfo> GetMidiOutputDevices() => Midi.WinMMMidiOutput.EnumerateDevices();
    public Task LoadAudioAsync(string filePath) => Task.CompletedTask;
    public Task LoadAudioFromMemoryAsync(ReadOnlyMemory<byte> bytes, string formatHint, string? displayPath = null) => Task.CompletedTask;
    public double GetTotalAudioLengthMs() => 0;
    public Task<WaveformSamples> GenerateWaveformAsync(string filePath)
        => Task.FromResult(WaveformSamples.Empty);
    public void FreeAudio() { }

    public List<VstPluginInfo> ScanVstPlugins() => [];
    public void SetGuitarVstChain(IReadOnlyList<VstChainEntry> entries) { }
    public void ToggleGuitarVstSlotEditor(Guid id, bool open) { }
    public void SetGuitarVstSlotBypass(Guid id, bool bypass) { }
    public void LoadInstrumentVst(string pluginPath, bool openEditorAfterLoad = false) { }
    public void ToggleInstrumentVstEditor(bool open) { }
    public void SetMidiInstrument(int program) { }

    public void Play() { }
    public void Pause() { }
    public void Stop() { }
    public void SeekTo(double timeMs) { }
    public void PlayMetronomeTick(bool isMeasure) { }
    public void PlayClap() { }
    public double RawPlayheadMs => 0.0;
    public bool TryScheduleClick(int clickType, double transportMs) => false;
    public void ClearScheduledClicks() { }
    public bool TryScheduleCalibrationClap(out long audibleHostTicks) { audibleHostTicks = 0; return false; }

    public void UpdateMidiNotes(double rawPlayheadMs, IList<GuitarNote> activeChartNotes,
        IList<Chord> activeChartChords, Arrangement currentArrangement, double bpm, IList<BeatMarker> beats,
        IList<HandShape>? handShapes = null)
    { }

    public bool HasStems => false;
    public IReadOnlyList<string> LoadedStemNames => Array.Empty<string>();
    public Task LoadStemsAsync(IReadOnlyList<StemLoadRequest> stems) => Task.CompletedTask;
    public void ClearStems() { }
    public void SetStemVolume(string name, double linearGain) { }
    public void SetStemMute(string name, bool muted) { }
    public void SetStemSolo(string name, bool soloed) { }
    public void SetMasterMixMuted(bool muted) { }

    public void Dispose() { }
}
