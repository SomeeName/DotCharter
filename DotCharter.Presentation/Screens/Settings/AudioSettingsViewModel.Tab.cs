using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Windows.Input;
using CommunityToolkit.Mvvm.ComponentModel;
using DotCharter.Presentation.Contracts.Settings;
using DotCharter.Tone.Audio.Abstractions;
using static DotCharter.Presentation.Screens.Settings.AudioSettingsRules;

namespace DotCharter.Presentation.Screens.Settings;

/// <summary>The Settings-tab half (tracking, search), the driver predicates, the guitar-jack and output-pair pickers, and the contract.</summary>
public partial class AudioSettingsViewModel : ISettingsTab, ISettingsChangeSource
{
    /// <summary>The names the footer counts, for the round-trip and reachability lints.</summary>
    internal static IReadOnlyList<string> TrackedSettings => AudioSettingsTracking.Tracked;

    /// <summary>The picked devices' one entry in the count; see <see cref="AudioSettingsTracking" />.</summary>
    internal const string DeviceSelectionKey = AudioSettingsTracking.DeviceSelectionKey;

    private AudioSettingsTracking? _tracker;

    private AudioSettingsTracking Changes => _tracker ??= new AudioSettingsTracking(this);

    [ObservableProperty] private bool _isAdvancedOpen;

    public IReadOnlySet<string> HiddenKeys { get; private set; } = new HashSet<string>();

    public string ReadoutDevice => PickName(IsAsioOutput ? AsioDevices : WasapiDevices,
        IsAsioOutput ? SelectedAsioOutputIndex : SelectedWasapiOutputIndex) is { Length: > 0 } name
        ? name : _localization["Settings_Audio_ReadoutNoDevice"];

    /// <summary>A language switch: the lines formatted from a resx string are read again.</summary>
    internal void RaiseTextsChanged()
    {
        OnPropertyChanged(nameof(ReadoutDevice));
        OnPropertyChanged(nameof(BufferLatencyDescription));
        OnPropertyChanged(nameof(ActualBufferDescription));
        OnPropertyChanged(nameof(ReadoutBuffer));
    }

    // The readouts say what the ENGINE runs (the status bar reads the same), not the
    // request: an ASIO device fixed at 48 kHz ignores a 44.1 kHz request.
    public string ReadoutRate => ReadoutFrames > 0 ? RateLabel(ReadoutSampleRate) : "—";

    public string ReadoutBuffer => ReadoutFrames > 0
        ? _localization.Format("Settings_Audio_ReadoutFrames", ReadoutFrames) : "—";

    public string ReadoutLatency => ReadoutFrames > 0 ? LatencyLabel(ReadoutFrames, ReadoutSampleRate) : "—";

    private int ReadoutSampleRate => _devices.ActiveSampleRate > 0 ? _devices.ActiveSampleRate : SampleRate;

    private int ReadoutFrames => _devices.ActualBufferFrames > 0 ? _devices.ActualBufferFrames : BufferSizeFrames;

    public string Key => SettingsTabKeys.Audio;

    public Type? Screen => typeof(IAudioSettingsScreen);

    public IReadOnlyList<string> UnsavedSettings => Changes.Differences();

    public IReadOnlyList<SettingsSearchGroup> SearchGroups => AudioSettingsCatalog.Groups(_localization);

    void ISettingsTab.Load() => Open();

    void ISettingsTab.Save() => Save();

    /// <summary>Closes without reloading; the next <see cref="Open" /> reads the persisted values back.</summary>
    void ISettingsTab.Revert()
    {
        Cancel();
        Rebaseline();
    }

    void ISettingsTab.Reset() => Open();

    public void Rebaseline()
    {
        Changes.Rebaseline();
        OnPropertyChanged(nameof(UnsavedSettings));
    }

    public void ApplySearch(string query)
    {
        HiddenKeys = SettingsSearch.HiddenKeys(SearchGroups, query, AudioSettingsCatalog.GroupKey, out var kept);
        OnPropertyChanged(nameof(HiddenKeys));
        if (query.Length > 0 && kept.Skip(AudioSettingsCatalog.FirstAdvancedGroup).Any(k => k)) IsAdvancedOpen = true;
    }

    private void RebaselineDevices()
    {
        if (Changes.RebaselineDevices()) OnPropertyChanged(nameof(UnsavedSettings));
    }

    protected override void OnPropertyChanged(PropertyChangedEventArgs e)
    {
        base.OnPropertyChanged(e);
        if (e.PropertyName is { } name && Changes.Counts(name))
            base.OnPropertyChanged(new PropertyChangedEventArgs(nameof(UnsavedSettings)));
        if (e.PropertyName is nameof(SelectedDriver) or nameof(SelectedWasapiOutputIndex)
            or nameof(SelectedAsioOutputIndex) or nameof(SampleRate) or nameof(BufferSizeFrames))
        {
            foreach (var readout in (string[])[nameof(ReadoutDevice), nameof(ReadoutRate), nameof(ReadoutBuffer), nameof(ReadoutLatency)])
                base.OnPropertyChanged(new PropertyChangedEventArgs(readout));
        }
    }

    public int InputDeviceJackCount => _jacks.JackCount;

    /// <summary>
    ///     WASAPI lists some interfaces as several capture devices, one per input
    ///     pair, so a WASAPI device with two inputs or fewer (or a count not known
    ///     yet) says to pick the device that carries the guitar before the jack.
    /// </summary>
    public bool IsWasapiDevicesNoteShown => EnableInput && SelectedDriver != "ASIO" && _jacks.JackCount <= 2;

    public int SelectedInputJackIndex { get => _jacks.SelectedIndex; set => _jacks.SelectedIndex = value; }

    /// <summary>The guitar's jack rows the rig footer and the first-run wizard show; see <see cref="InputJackPicker" />.</summary>
    public ObservableCollection<InputJackOption> InputJackOptions => _jacks.Options;

    public bool IsAsioActive => SelectedDriver == "ASIO" || SelectedInputDriver == "ASIO";

    /// <summary>Play Mode's modal swaps one row's list behind these; a compiled binding has no <c>==</c>.</summary>
    public bool IsWasapiOutput => SelectedDriver != "ASIO";

    public bool IsAsioOutput => SelectedDriver == "ASIO";
    public bool IsWasapiInput => SelectedInputDriver != "ASIO";
    public bool IsAsioInput => SelectedInputDriver == "ASIO";

    /// <summary>Rebuilds the jack rows for a device with that many inputs (0 = unknown, the generic list).</summary>
    public void RefreshInputJackOptions(int deviceJackCount, IReadOnlyList<string>? channelNames = null) =>
        _jacks.Refresh(deviceJackCount, channelNames);

    /// <summary>The latest capture device's jack-count read; tests await it.</summary>
    internal Task JackCountProbe => _jacks.Probe;

    private void PickInputJack(int jack)
    {
        if (jack == AsioInputChannelLeft) return;
        // Two open channels are two DIFFERENT jacks: taking the right box's jack swaps them.
        if (!AsioInputMono && AsioInputChannelRight == jack) AsioInputChannelRight = AsioInputChannelLeft;
        AsioInputChannelLeft = jack;
    }

    partial void OnAsioInputChannelLeftChanged(int value) => _jacks.Sync(value);

    /// <summary>Another capture device means another jack count, enumerated off the UI thread; a device of the other driver re-reads nothing.</summary>
    partial void OnSelectedAsioInputIndexChanged(int value)
    {
        if (IsAsioInput) _jacks.ProbeDevice("ASIO", AsioInputDevicesList, value);
    }

    /// <summary>The WASAPI capture device's jack count, read the same way: the engine masks its jacks too.</summary>
    partial void OnSelectedWasapiInputIndexChanged(int value)
    {
        if (IsWasapiInput) _jacks.ProbeDevice("WASAPI", WasapiInputDevicesList, value);
    }

    /// <summary>The playback device's output count behind <see cref="OutputPairOptions" />; 0 = not known yet.</summary>
    public int OutputDeviceChannelCount => _pairs.DeviceChannelCount;

    public int SelectedOutputPairIndex { get => _pairs.SelectedIndex; set => _pairs.SelectedIndex = value; }

    /// <summary>The master mix's pair rows the first-run wizard shows too; see <see cref="OutputPairPicker" />.</summary>
    public ObservableCollection<OutputPairOption> OutputPairOptions => _pairs.Options;

    /// <summary>Rebuilds the pair rows for a device with that many outputs (0 = unknown, the generic list).</summary>
    public void RefreshOutputPairOptions(int deviceOutputCount, IReadOnlyList<string>? channelNames = null) =>
        _pairs.Refresh(deviceOutputCount, channelNames);

    partial void OnAsioOutputChannelLeftChanged(int value) => _pairs.Sync();

    partial void OnAsioOutputChannelRightChanged(int value) => _pairs.Sync();

    /// <summary>Another playback device means another output count; a device of the other driver re-reads nothing.</summary>
    partial void OnSelectedWasapiOutputIndexChanged(int value) => ProbePairCount();

    partial void OnSelectedAsioOutputIndexChanged(int value) => ProbePairCount();

    /// <summary>The latest playback device's output-count read; tests await it.</summary>
    internal Task OutputCountProbe => _pairs.Probe;

    /// <summary>Reads the selected playback device's outputs, the first listed one (the engine's fallback) while none is.</summary>
    private void ProbePairCount()
    {
        var asio = SelectedDriver == "ASIO";
        var devices = asio ? AsioDevices : WasapiDevices;
        var index = Math.Max(0, asio ? SelectedAsioOutputIndex : SelectedWasapiOutputIndex);
        if (index < devices.Count) _pairs.ProbeDevice(asio ? "ASIO" : "WASAPI", devices[index]);
    }

    // The pickers' own notifications, under the names the screen binds.
    private void OnPickerChanged(object? sender, PropertyChangedEventArgs e)
    {
        string[] relayed = (sender == _jacks, e.PropertyName) switch
        {
            (true, nameof(InputJackPicker.SelectedIndex)) => [nameof(SelectedInputJackIndex)],
            (true, nameof(InputJackPicker.JackCount)) => [nameof(InputDeviceJackCount), nameof(IsWasapiDevicesNoteShown)],
            (false, nameof(OutputPairPicker.SelectedIndex)) => [nameof(SelectedOutputPairIndex)],
            (false, nameof(OutputPairPicker.DeviceChannelCount)) => [nameof(OutputDeviceChannelCount)],
            _ => []
        };
        foreach (var name in relayed) OnPropertyChanged(name);
    }

    partial void OnSelectedDriverChanged(string value)
    {
        OnPropertyChanged(nameof(IsAsioActive));
        OnPropertyChanged(nameof(IsWasapiOutput));
        OnPropertyChanged(nameof(IsAsioOutput));
        OnPropertyChanged(nameof(IsWasapiDevicesNoteShown));
        ProbePairCount();
    }

    partial void OnEnableInputChanged(bool value) => OnPropertyChanged(nameof(IsWasapiDevicesNoteShown));

    partial void OnSelectedInputDriverChanged(string value)
    {
        OnPropertyChanged(nameof(IsAsioActive));
        OnPropertyChanged(nameof(IsWasapiInput));
        OnPropertyChanged(nameof(IsAsioInput));
        // The new driver's device: a read of the old driver's still running must not land for it.
        if (IsAsioInput) _jacks.ProbeDevice("ASIO", AsioInputDevicesList, SelectedAsioInputIndex);
        else _jacks.ProbeDevice("WASAPI", WasapiInputDevicesList, SelectedWasapiInputIndex);
    }

    partial void OnBufferSizeFramesChanged(int value) => OnPropertyChanged(nameof(BufferLatencyDescription));

    partial void OnSampleRateChanged(int value) => OnPropertyChanged(nameof(BufferLatencyDescription));

    partial void OnAutoFollowDeviceSettingsChanged(bool value)
    {
        OnPropertyChanged(nameof(IsManualAudioConfig));
        if (!value) return;
        if (_devices.ActualBufferFrames > 0) BufferSizeFrames = NormalizeBufferSize(_devices.ActualBufferFrames);
        if (_devices.ActiveSampleRate > 0) SampleRate = NormalizeSampleRate(_devices.ActiveSampleRate);
        OnPropertyChanged(nameof(ActualBufferDescription));
    }

    /// <summary>
    ///     Shows what the driver negotiated, in manual mode too: a driver can
    ///     refuse the requested size, and showing the request would be a lie.
    /// </summary>
    private void OnDeviceBufferFramesChanged(object? sender, int frames)
    {
        if (!IsOpen) return;
        BufferSizeFrames = NormalizeBufferSize(frames);
        OnPropertyChanged(nameof(ActualBufferDescription));
    }

    IReadOnlyList<string> IAudioSettingsScreen.DriverOptions => DriverOptions;
    IReadOnlyList<string> IAudioSettingsScreen.WasapiDevices => WasapiDevices;
    IReadOnlyList<string> IAudioSettingsScreen.AsioDevices => AsioDevices;
    IReadOnlyList<string> IAudioSettingsScreen.WasapiInputDevicesList => WasapiInputDevicesList;
    IReadOnlyList<string> IAudioSettingsScreen.AsioInputDevicesList => AsioInputDevicesList;
    IReadOnlyList<string> IAudioSettingsScreen.StreamingMirrorDevices => StreamingMirrorDevices;
    IReadOnlyList<string> IAudioSettingsScreen.MidiOutputDevices => MidiOutputDevices;
    IReadOnlyList<string> IAudioSettingsScreen.DropTunerEngineOptions => DropTunerEngineOptions;
    IReadOnlyList<int> IAudioSettingsScreen.SampleRateOptions => SampleRateOptions;
    IReadOnlyList<int> IAudioSettingsScreen.BufferSizeOptions => BufferSizeOptions;
    IReadOnlyList<InputJackOption> IAudioSettingsScreen.InputJackOptions => InputJackOptions;
    IReadOnlyList<OutputPairOption> IAudioSettingsScreen.OutputPairOptions => OutputPairOptions;
    ICommand IAudioSettingsScreen.MeasureLatencyCommand => MeasureLatencyCommand;
    ICommand IAudioSettingsScreen.OpenLatencyWizardCommand => OpenLatencyWizardCommand;
    ICommand IAudioSettingsScreen.OpenAsioControlPanelCommand => OpenAsioControlPanelCommand;
}
