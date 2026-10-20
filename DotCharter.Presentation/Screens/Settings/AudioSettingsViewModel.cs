using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using DotCharter.Localization;
using DotCharter.Presentation.Contracts.Settings;
using DotCharter.Tone.Audio;
using DotCharter.Tone.Audio.Abstractions;
using DotCharter.Services.Infrastructure.Configuration;
using DotCharter.Services.Infrastructure.Shell;
using DotCharter.Services.State;
using DotCharter.Tone.Diagnostics;
using static DotCharter.Presentation.Screens.Settings.AudioSettingsRules;

namespace DotCharter.Presentation.Screens.Settings;

/// <summary>
///     Settings ▸ Audio, and the same rows in Play Mode's settings modal and the
///     first-run wizard — one shared instance. <see cref="Open" /> fills the
///     cheap fields at once and enumerates devices on a worker; <see cref="Save" />
///     persists, pushes the live trims, and rebuilds the engine only when routing
///     moved.
/// </summary>
public partial class AudioSettingsViewModel : ObservableObject, IAudioSettingsScreen
{
    private readonly IAudioDeviceControl _devices;
    private readonly ILiveInputCapture _capture;
    private readonly IConfigurationService _configService;
    private readonly ProjectState _projectState;
    private readonly ILatencyCalibrationPrompt _latencyWizard;
    private readonly IUiDispatcher _ui;
    private readonly IMainWindowHandle? _mainWindow;
    private readonly ILocalizationService _localization;

    /// <summary>The winmm id behind each MIDI row, from the SAME enumeration that built the names.</summary>
    private readonly List<int> _midiOutputDeviceIds = [];

    private AppConfig? _configSnapshot;

    [ObservableProperty] private int _asioInputChannelLeft;
    [ObservableProperty] private int _asioInputChannelRight;
    [ObservableProperty] private bool _asioInputMono;
    [ObservableProperty] private bool _inputChannelsAreSeparateGuitars;
    [ObservableProperty] private int _asioOutputChannelLeft;
    [ObservableProperty] private int _asioOutputChannelRight;

    /// <summary>An EXTRA trim on top of the device latency, which is compensated automatically; 0 is correct.</summary>
    [ObservableProperty] private double _audioDelayMs;

    [ObservableProperty] private bool _autoCalibrateTiming;
    [ObservableProperty] private bool _takeRecorderEnabled;
    [ObservableProperty] private double _measuredAudioDelayMs;
    [ObservableProperty] private bool _autoFollowDeviceSettings;
    [ObservableProperty] private int _bufferSizeFrames;
    [ObservableProperty] private bool _enableHighProcessPriority = true;
    [ObservableProperty] private bool _outOfProcessVst;
    [ObservableProperty] private bool _enableInput;
    [ObservableProperty] private int _selectedDropTunerEngineIndex;
    [ObservableProperty] private bool _isOpen;
    [ObservableProperty] private double _midiDelayMs;
    [ObservableProperty] private int _sampleRate = 44100;
    [ObservableProperty] private int _selectedAsioInputIndex;
    [ObservableProperty] private int _selectedAsioOutputIndex;
    [ObservableProperty] private string _selectedDriver = "WASAPI";
    [ObservableProperty] private string _selectedInputDriver = "ASIO";
    [ObservableProperty] private int _selectedWasapiInputIndex;
    [ObservableProperty] private int _selectedWasapiOutputIndex;
    [ObservableProperty] private string? _vstPluginName;
    [ObservableProperty] private bool _enableStreamingMirror;
    [ObservableProperty] private int _selectedStreamingMirrorIndex = -1;
    [ObservableProperty] private int _selectedMidiOutputIndex;
    [ObservableProperty] private string? _asioControlPanelError;

    public AudioSettingsViewModel(
        IAudioDeviceControl devices,
        ILiveInputCapture capture,
        IConfigurationService configService,
        ProjectState projectState,
        AudioSettingsWindows windows,
        IUiDispatcher ui,
        ILocalizationService localization)
    {
        _devices = devices;
        _localization = localization;
        DropTunerEngineOptions =
        [
            localization["DropTuner_QualityTight"],
            localization["DropTuner_QualityBalanced"],
            localization["DropTuner_QualitySmooth"]
        ];
        _capture = capture;
        _configService = configService;
        _projectState = projectState;
        _latencyWizard = windows.LatencyWizard;
        _ui = ui;
        _mainWindow = windows.MainWindow;
        _devices.DeviceBufferFramesChanged += OnDeviceBufferFramesChanged;
    }

    /// <summary>The probe <see cref="Open" /> started; never faults, <see cref="Task.CompletedTask" /> before the first Open.</summary>
    internal Task DeviceProbe { get; private set; } = Task.CompletedTask;

    /// <summary>
    ///     Set once the probe has landed and the device rows are real. Every
    ///     device index and NAME <see cref="Save" /> writes comes from those rows,
    ///     so a Save before it would move the user onto whatever enumerates
    ///     first — the wizard's Open-then-Save in one click is the ordinary case.
    /// </summary>
    internal bool DeviceListsLoaded { get; private set; }

    public ObservableCollection<string> DriverOptions { get; } = ["WASAPI", "ASIO"];
    public ObservableCollection<string> WasapiDevices { get; } = [];
    public ObservableCollection<string> AsioDevices { get; } = [];
    public ObservableCollection<string> WasapiInputDevicesList { get; } = [];
    public ObservableCollection<string> AsioInputDevicesList { get; } = [];
    public ObservableCollection<string> StreamingMirrorDevices { get; } = [];
    public ObservableCollection<string> MidiOutputDevices { get; } = [];
    public ObservableCollection<int> SampleRateOptions { get; } = new(SampleRateChoices);
    public ObservableCollection<int> BufferSizeOptions { get; } = new(BufferSizeChoices);

    /// <summary>Rows in <see cref="DropTunerEngineMap" /> order: Tight, Balanced, Smooth.</summary>
    public ObservableCollection<string> DropTunerEngineOptions { get; }

    public bool IsManualAudioConfig => !AutoFollowDeviceSettings;

    public string BufferLatencyDescription => _localization.Format(
        "Settings_BufferLatencyDescription", FramesToMilliseconds(BufferSizeFrames, SampleRate), SampleRate / 1000.0);

    public string ActualBufferDescription
    {
        get
        {
            var frames = _devices.ActualBufferFrames;
            if (frames <= 0) return string.Empty;
            var rate = _devices.ActiveSampleRate > 0 ? _devices.ActiveSampleRate : SampleRate;
            return _localization.Format(
                "Settings_ActualBufferDescription", frames, frames * 1000.0 / rate, rate / 1000.0);
        }
    }

    [RelayCommand]
    public void Open()
    {
        var config = _configService.LoadAppConfig();
        _configSnapshot = CloneRoutingFields(config);

        SelectedDriver = config.AudioDriver;
        AsioOutputChannelLeft = config.AsioOutputChannelLeft;
        AsioOutputChannelRight = config.AsioOutputChannelRight;
        BufferSizeFrames = NormalizeBufferSize(config.AudioBufferFrames);
        SampleRate = NormalizeSampleRate(config.AudioSampleRate);
        AudioDelayMs = config.AudioDelayMs;
        AutoCalibrateTiming = config.LivePlayAutoCalibrateTiming;
        TakeRecorderEnabled = config.LivePlayTakeRecorderEnabled;
        MeasuredAudioDelayMs = _devices.MeasuredOutputLatencyMs;
        MidiDelayMs = config.MidiDelayMs;
        EnableHighProcessPriority = config.EnableHighProcessPriority;
        OutOfProcessVst = config.OutOfProcessVst;
        AutoFollowDeviceSettings = config.AutoFollowDeviceAudioSettings;

        EnableInput = config.EnableInput;
        SelectedInputDriver = config.InputDriver;
        AsioInputChannelLeft = config.AsioInputChannelLeft;
        AsioInputChannelRight = config.AsioInputChannelRight;
        AsioInputMono = config.AsioInputMono;
        InputChannelsAreSeparateGuitars = config.InputChannelsAreSeparateGuitars;
        SelectedDropTunerEngineIndex = DropTunerEngineMap.FromConfigString(config.DropTunerEngine);
        RefreshInputJackOptions(0);
        EnableStreamingMirror = config.EnableStreamingMirror;

        IsOpen = true;
        OnPropertyChanged(nameof(ActualBufferDescription));
        Rebaseline();
        DeviceProbe = Task.Run(() => ProbeDevices(config));
    }

    /// <summary>A USB driver enumeration can take 200+ ms, so it runs here and posts the rows back.</summary>
    private void ProbeDevices(AppConfig config)
    {
        try
        {
            _devices.EnsureWasapiDevicesProbed(config);
            _devices.EnsureAsioDevicesProbed(config);
            var wasapiOuts = _devices.GetWasapiOutputDevices();
            var asioOuts = _devices.GetAsioDevices();
            var wasapiIns = _devices.GetWasapiInputDevices();
            var asioIns = _devices.GetAsioInputDevices();
            var midi = _devices.GetMidiOutputDevices();
            // The SELECTED input driver's jack count: asking a driver the session will never open is a wasted round trip.
            var inputIsAsio = string.Equals(config.InputDriver, "ASIO", StringComparison.OrdinalIgnoreCase);
            var jacks = _devices.GetInputDeviceChannelCount(
                inputIsAsio ? "ASIO" : "WASAPI", inputIsAsio ? config.AsioInputDeviceName : config.WasapiInputDeviceName);

            _ui.PostNormal(() =>
            {
                // To -1 first: re-selecting the row it already held raises nothing and leaves a blank header.
                SelectedWasapiOutputIndex = SelectedAsioOutputIndex = -1;
                SelectedWasapiInputIndex = SelectedAsioInputIndex = -1;
                SelectedStreamingMirrorIndex = SelectedMidiOutputIndex = -1;

                Refill(WasapiDevices, wasapiOuts);
                Refill(AsioDevices, asioOuts);
                Refill(WasapiInputDevicesList, wasapiIns);
                Refill(AsioInputDevicesList, asioIns);
                Refill(StreamingMirrorDevices, wasapiOuts);

                SelectedWasapiOutputIndex = ResolveIndex(WasapiDevices, config.WasapiOutputDeviceName, config.WasapiOutputDevice);
                SelectedAsioOutputIndex = ResolveIndex(AsioDevices, config.AsioOutputDeviceName, config.AsioOutputDevice);
                SelectedWasapiInputIndex = ResolveIndex(WasapiInputDevicesList, config.WasapiInputDeviceName, config.WasapiInputDevice);
                SelectedAsioInputIndex = ResolveIndex(AsioInputDevicesList, config.AsioInputDeviceName, config.AsioInputDevice);
                SelectedStreamingMirrorIndex = ResolveIndex(
                    StreamingMirrorDevices, config.StreamingMirrorDeviceName, config.StreamingMirrorDevice);
                RefreshInputJackOptions(jacks);
                DeviceListsLoaded = true;

                MidiOutputDevices.Clear();
                _midiOutputDeviceIds.Clear();
                foreach (var d in midi)
                {
                    MidiOutputDevices.Add(d.Name);
                    _midiOutputDeviceIds.Add(d.Index);
                }

                var row = 0;
                for (var i = 0; i < midi.Count; i++)
                {
                    if (midi[i].Index == config.MidiOutputDevice || (config.MidiOutputDeviceName.Length > 0 &&
                            string.Equals(midi[i].Name, config.MidiOutputDeviceName, StringComparison.OrdinalIgnoreCase)))
                    {
                        row = i;
                        break;
                    }
                }

                SelectedMidiOutputIndex = row;
            });
        }
        catch
        {
            // A failed probe leaves the rows empty; the dialog must still open.
        }
    }

    private static void Refill(ObservableCollection<string> target, IEnumerable<string> rows)
    {
        target.Clear();
        foreach (var row in rows) target.Add(row);
    }

    [RelayCommand]
    private void Save()
    {
        var config = _configService.LoadAppConfig();
        config.AudioDriver = SelectedDriver;
        if (DeviceListsLoaded)
        {
            config.WasapiOutputDevice = SelectedWasapiOutputIndex;
            config.AsioOutputDevice = SelectedAsioOutputIndex;
            config.WasapiOutputDeviceName = PickName(WasapiDevices, SelectedWasapiOutputIndex);
            config.AsioOutputDeviceName = PickName(AsioDevices, SelectedAsioOutputIndex);
            config.WasapiInputDevice = SelectedWasapiInputIndex;
            config.AsioInputDevice = SelectedAsioInputIndex;
            config.WasapiInputDeviceName = PickName(WasapiInputDevicesList, SelectedWasapiInputIndex);
            config.AsioInputDeviceName = PickName(AsioInputDevicesList, SelectedAsioInputIndex);
            config.StreamingMirrorDevice = SelectedStreamingMirrorIndex;
            config.StreamingMirrorDeviceName = PickName(StreamingMirrorDevices, SelectedStreamingMirrorIndex);
        }

        config.AsioOutputChannelLeft = AsioOutputChannelLeft;
        config.AsioOutputChannelRight = AsioOutputChannelRight;

        // Auto-follow persists what the driver negotiated, snapped, so a driver's own rounding cannot compound across runs.
        var frames = AutoFollowDeviceSettings && _devices.ActualBufferFrames > 0 ? _devices.ActualBufferFrames : BufferSizeFrames;
        var rate = AutoFollowDeviceSettings && _devices.ActiveSampleRate > 0 ? _devices.ActiveSampleRate : SampleRate;
        config.AudioBufferFrames = NormalizeBufferSize(frames);
        config.AudioSampleRate = NormalizeSampleRate(rate);

        config.AudioDelayMs = AudioDelayMs;
        config.LivePlayAutoCalibrateTiming = AutoCalibrateTiming;
        config.LivePlayTakeRecorderEnabled = TakeRecorderEnabled;
        config.MidiDelayMs = MidiDelayMs;
        config.EnableHighProcessPriority = EnableHighProcessPriority;
        config.OutOfProcessVst = OutOfProcessVst;
        config.AutoFollowDeviceAudioSettings = AutoFollowDeviceSettings;
        config.EnableInput = EnableInput;
        config.InputDriver = SelectedInputDriver;
        config.AsioInputChannelLeft = AsioInputChannelLeft;
        config.AsioInputChannelRight = AsioInputChannelRight;
        config.AsioInputMono = AsioInputMono;
        // Mono opens one channel, so "two guitars" cannot be persisted beside it.
        config.InputChannelsAreSeparateGuitars = !AsioInputMono && InputChannelsAreSeparateGuitars;
        config.DropTunerEngine = DropTunerEngineMap.ToConfigString(SelectedDropTunerEngineIndex);
        config.EnableStreamingMirror = EnableStreamingMirror;
        SaveMidiOutput(config);

        _configService.SaveAppConfig(config);

        // Pushed live: none of these needs a device change, so none waits on the routing check below.
        _capture.InputPitchShiftMode = DropTunerEngineMap.ToNativeMode(SelectedDropTunerEngineIndex);
        _devices.AudioDelayMs = AudioDelayMs;
        _devices.MidiOffsetMs = MidiDelayMs;

        // Rebuilding the engine with nothing changed can fast-fail inside the native driver-close path.
        if (_configSnapshot is null || HasRoutingChanged(_configSnapshot, config))
            _devices.ApplyAudioConfiguration(config);
        _configSnapshot = CloneRoutingFields(config);

        IsOpen = false;
        OnPropertyChanged(nameof(ActualBufferDescription));
        Rebaseline();
        _projectState.MainDisplayMessage = _localization["Status_AudioConfigApplied"];
    }

    /// <summary>
    ///     Resolves the row against the list <see cref="Open" /> BUILT, never a
    ///     fresh enumeration. An empty list means the probe has not landed, and
    ///     the stored device is left exactly as it was.
    /// </summary>
    private void SaveMidiOutput(AppConfig config)
    {
        if (_midiOutputDeviceIds.Count == 0) return;
        var row = SelectedMidiOutputIndex;
        var valid = row >= 0 && row < _midiOutputDeviceIds.Count;
        config.MidiOutputDevice = valid ? _midiOutputDeviceIds[row] : -1;
        config.MidiOutputDeviceName = valid ? MidiOutputDevices[row] : string.Empty;
    }

    [RelayCommand]
    private void Cancel() => IsOpen = false;

    [RelayCommand]
    private void OpenLatencyWizard() => _ = _latencyWizard.OpenAsync();

    /// <summary>
    ///     Shows the device's reported output latency. It never writes
    ///     <see cref="AudioDelayMs" />: the engine already subtracts that latency
    ///     from every position, so copying it in would compensate twice.
    /// </summary>
    [RelayCommand]
    private void MeasureLatency() => MeasuredAudioDelayMs = _devices.MeasuredOutputLatencyMs;

    /// <summary>Parents the driver's panel to the main window; zero (no window yet) centres it on screen.</summary>
    [RelayCommand]
    private void OpenAsioControlPanel()
    {
        AsioControlPanelError = _devices.TryOpenAsioControlPanel(_mainWindow?.Value ?? IntPtr.Zero, out var error)
            ? null
            : error;
        OnPropertyChanged(nameof(ActualBufferDescription));
    }
}
