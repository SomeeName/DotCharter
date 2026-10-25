using System.ComponentModel;
using System.Windows.Input;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using DotCharter.Presentation.Contracts.Settings;
using DotCharter.Tone.Audio.Abstractions;

namespace DotCharter.Presentation.Fakes.Settings;

/// <summary>A services-free Audio tab: no engine, no probe, the device rows are whatever a variant says.</summary>
public sealed partial class FakeAudioSettingsScreen : ObservableObject, IAudioSettingsScreen
{
    [ObservableProperty] private bool _isAdvancedOpen;
    [ObservableProperty] private string _selectedDriver = "WASAPI";
    [ObservableProperty] private int _selectedWasapiOutputIndex;
    [ObservableProperty] private int _selectedAsioOutputIndex;
    [ObservableProperty] private int _selectedOutputPairIndex;
    [ObservableProperty] private bool _enableInput = true;
    [ObservableProperty] private string _selectedInputDriver = "WASAPI";
    [ObservableProperty] private int _selectedWasapiInputIndex;
    [ObservableProperty] private int _selectedAsioInputIndex;
    [ObservableProperty] private int _asioInputChannelRight = 1;
    [ObservableProperty] private bool _asioInputMono = true;
    [ObservableProperty] private bool _inputChannelsAreSeparateGuitars;
    [ObservableProperty] private int _selectedInputJackIndex;
    [ObservableProperty] private int _sampleRate = 48000;
    [ObservableProperty] private int _bufferSizeFrames = 256;
    [ObservableProperty] private string _bufferLatencyDescription = "5.3 ms at 48 kHz";
    [ObservableProperty] private string _actualBufferDescription = string.Empty;
    [ObservableProperty] private bool _autoFollowDeviceSettings;
    [ObservableProperty] private double _audioDelayMs;
    [ObservableProperty] private double _measuredAudioDelayMs = 11.6;
    [ObservableProperty] private bool _autoCalibrateTiming;
    [ObservableProperty] private bool _takeRecorderEnabled;
    [ObservableProperty] private bool _enableHighProcessPriority = true;
    [ObservableProperty] private bool _outOfProcessVst;
    [ObservableProperty] private int _selectedDropTunerEngineIndex = 1;
    [ObservableProperty] private string? _asioControlPanelError = string.Empty;
    [ObservableProperty] private bool _enableStreamingMirror;
    [ObservableProperty] private int _selectedStreamingMirrorIndex = -1;
    [ObservableProperty] private int _selectedMidiOutputIndex;

    private FakeAudioSettingsScreen()
    {
    }

    public static IReadOnlyList<FakeVariant<IAudioSettingsScreen>> Variants { get; } =
    [
        new(FakeVariantNames.Default, Default),
        new(FakeVariantNames.Empty, Empty),
        new(FakeVariantNames.LongText, LongText),
        new(FakeVariantNames.Full, Full),
        new("AdvancedOpen", AdvancedOpen)
    ];

    public IReadOnlySet<string> HiddenKeys { get; set; } = new HashSet<string>();
    public IReadOnlyList<string> DriverOptions { get; } = ["WASAPI", "ASIO"];
    public IReadOnlyList<string> WasapiDevices { get; private set; } = [];
    public IReadOnlyList<string> AsioDevices { get; private set; } = [];
    public IReadOnlyList<string> WasapiInputDevicesList { get; private set; } = [];
    public IReadOnlyList<string> AsioInputDevicesList { get; private set; } = [];
    public IReadOnlyList<InputJackOption> InputJackOptions { get; private set; } = [];
    public IReadOnlyList<OutputPairOption> OutputPairOptions { get; private set; } = [];
    public IReadOnlyList<int> SampleRateOptions { get; } = [44100, 48000, 88200, 96000, 176400, 192000];
    public IReadOnlyList<int> BufferSizeOptions { get; } = [32, 64, 96, 128, 192, 256, 384, 512, 768, 1024, 2048];
    public IReadOnlyList<string> DropTunerEngineOptions { get; } = ["Tight", "Balanced", "Smooth"];
    public IReadOnlyList<string> StreamingMirrorDevices { get; private set; } = [];
    public IReadOnlyList<string> MidiOutputDevices { get; private set; } = [];

    public string ReadoutDevice { get; private set; } = "Speakers (Realtek(R) Audio)";
    public string ReadoutRate { get; private set; } = "48 kHz";
    public string ReadoutBuffer { get; private set; } = "256 samples";
    public string ReadoutLatency { get; private set; } = "5.3 ms";

    public bool IsWasapiOutput => SelectedDriver != "ASIO";
    public bool IsAsioOutput => SelectedDriver == "ASIO";
    public bool IsWasapiInput => SelectedInputDriver != "ASIO";
    public bool IsAsioInput => SelectedInputDriver == "ASIO";
    public bool IsAsioActive => IsAsioOutput || IsAsioInput;
    public bool IsWasapiDevicesNoteShown { get; set; }
    public bool IsManualAudioConfig => !AutoFollowDeviceSettings;

    public ICommand MeasureLatencyCommand { get; } = new RelayCommand(() => { });
    public ICommand OpenLatencyWizardCommand { get; } = new RelayCommand(() => { });
    public ICommand OpenAsioControlPanelCommand { get; } = new RelayCommand(() => { });

    public void Open()
    {
    }

    /// <summary>A laptop on WASAPI: speakers out, a two-input USB interface in, mono guitar.</summary>
    public static FakeAudioSettingsScreen Default()
    {
        var screen = With(
            ["Speakers (Realtek(R) Audio)", "Scarlett 2i2 USB"],
            ["Focusrite USB ASIO"],
            ["Microphone Array (Realtek(R) Audio)", "Analogue 1 + 2 (Scarlett 2i2 USB)"],
            ["Default MIDI Mapper", "Microsoft GS Wavetable Synth"]);
        screen.IsWasapiDevicesNoteShown = true;
        return screen;
    }

    /// <summary>The probe has not landed: every device list is still empty.</summary>
    public static FakeAudioSettingsScreen Empty()
    {
        var screen = With([], [], [], []);
        screen.SelectedWasapiOutputIndex = screen.SelectedWasapiInputIndex = screen.SelectedMidiOutputIndex = -1;
        screen.OutputPairOptions = [];
        screen.SelectedOutputPairIndex = -1;
        return screen;
    }

    /// <summary>Device names the length real drivers report, in German and Japanese, and a refused ASIO panel.</summary>
    public static FakeAudioSettingsScreen LongText()
    {
        var screen = With(
            ["Lautsprecher (High Definition Audio-Gerät mit einem sehr langen Treibernamen)",
                "スピーカー (Realtek(R) オーディオ デバイス、ハイ デフィニション オーディオ)"],
            ["Focusrite USB ASIO Universal Driver 4.119.0"],
            ["Line In (Scarlett 18i20 3rd Gen USB – Analogue 1 + 2, Instrument-Eingang)"],
            ["Standardmäßiger MIDI-Mapper", "Microsoft GS-Wavetable-Synthesizer"]);
        screen.SelectedDriver = "ASIO";
        screen.ReadoutDevice = "Focusrite USB ASIO Universal Driver 4.119.0";
        screen.ReadoutBuffer = "256 Samples";
        screen.ReadoutRate = "48 kHz";
        screen.ReadoutLatency = "5,3 ms";
        screen.AsioControlPanelError = "Der Treiber hat das Öffnen seines Bedienfelds abgelehnt: ASE_NotPresent (-1000).";
        screen.BufferLatencyDescription = "Puffer 256 Samples ≈ 5,3 ms bei 48 kHz";
        return screen;
    }

    /// <summary>ASIO end to end, two guitars on two jacks, auto-follow on and a negotiated buffer to show.
    /// The driver names its channels, so the pair and jack rows carry those names.</summary>
    public static FakeAudioSettingsScreen Full()
    {
        var screen = Default();
        screen.SelectedDriver = "ASIO";
        screen.SelectedInputDriver = "ASIO";
        screen.InputJackOptions = [new(0, "Input 1 · Mic 1"), new(1, "Input 2 · Hi-Z")];
        screen.OutputPairOptions =
            [new(0, 1, "Out 1 + 2 · Main"), new(2, 3, "Out 3 + 4 · Headphones"), new(4, 5, "Out 5 + 6 · Line 5/6")];
        screen.SelectedOutputPairIndex = 1;
        screen.IsWasapiDevicesNoteShown = false;
        screen.ReadoutDevice = "Focusrite USB ASIO";
        screen.ReadoutBuffer = "384 samples";
        screen.ReadoutLatency = "8.0 ms";
        screen.AsioInputMono = false;
        screen.InputChannelsAreSeparateGuitars = true;
        screen.AutoFollowDeviceSettings = true;
        screen.ActualBufferDescription = "Driver runs 384 frames (8.0 ms at 48 kHz)";
        screen.EnableStreamingMirror = true;
        screen.SelectedStreamingMirrorIndex = 0;
        return screen;
    }

    /// <summary>The Advanced disclosure open, so the streaming mirror and MIDI cards are on screen.</summary>
    public static FakeAudioSettingsScreen AdvancedOpen()
    {
        var screen = Default();
        screen.IsAdvancedOpen = true;
        return screen;
    }

    private static FakeAudioSettingsScreen With(
        string[] outputs, string[] asio, string[] inputs, string[] midi) => new()
        {
            WasapiDevices = outputs,
            AsioDevices = asio,
            WasapiInputDevicesList = inputs,
            AsioInputDevicesList = asio,
            StreamingMirrorDevices = outputs,
            MidiOutputDevices = midi,
            InputJackOptions = [new(0, "Input 1"), new(1, "Input 2")],
            OutputPairOptions = [new(0, 1, "Out 1 + 2"), new(2, 3, "Out 3 + 4"), new(4, 5, "Out 5 + 6")]
        };

    protected override void OnPropertyChanged(PropertyChangedEventArgs e)
    {
        base.OnPropertyChanged(e);
        if (e.PropertyName == nameof(SelectedDriver))
        {
            OnPropertyChanged(nameof(IsWasapiOutput));
            OnPropertyChanged(nameof(IsAsioOutput));
            OnPropertyChanged(nameof(IsAsioActive));
        }
        else if (e.PropertyName == nameof(SelectedInputDriver))
        {
            OnPropertyChanged(nameof(IsWasapiInput));
            OnPropertyChanged(nameof(IsAsioInput));
            OnPropertyChanged(nameof(IsAsioActive));
        }
        else if (e.PropertyName == nameof(AutoFollowDeviceSettings))
        {
            OnPropertyChanged(nameof(IsManualAudioConfig));
        }
    }
}
