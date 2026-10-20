using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using DotCharter.Localization;
using DotCharter.Tone.Audio.Abstractions;
using DotCharter.Tone.Diagnostics;
using DotCharter.Tone.UI.ViewModels.Settings;

namespace DotCharter.RigLab;

/// <summary>One choice in a picker: the value written to the config and the
/// words shown for it.</summary>
public sealed record SettingOption(string Value, string Label)
{
    public override string ToString() => Label;
}

/// <summary>
///     RigLab's Audio tab: driver, devices, buffer, sample rate, the guitar's
///     input jack and the language. Every change is written to
///     <c>riglab.json</c> and re-applied to the engine at once — off the UI thread,
///     because opening an ASIO device blocks for as long as the driver likes.
/// </summary>
public sealed partial class AudioSettingsViewModel : ObservableObject
{
    public const string Wasapi = "WASAPI";
    public const string Asio = "ASIO";

    private static readonly int[] BufferChoices = [64, 128, 256, 512, 1024];
    private static readonly int[] RateChoices = [44100, 48000, 96000];

    private readonly IAudioDeviceControl _audio;
    private readonly RigLabConfigService _config;
    private readonly IUiDispatcher _ui;
    private readonly ILocalizationService _localization;
    private readonly Func<Func<Task>, Task> _worker;
    private int _applyGeneration;
    private bool _loading;

    public AudioSettingsViewModel(
        IAudioDeviceControl audio, RigLabConfigService config, IUiDispatcher ui,
        LiveInputChannelViewModel inputChannel, ILocalizationService localization, Func<Func<Task>, Task>? worker = null)
    {
        _audio = audio;
        _config = config;
        _ui = ui;
        _worker = worker ?? (static work => Task.Run(work));
        InputChannel = inputChannel;
        _localization = localization;

        Drivers = [new SettingOption(Wasapi, "WASAPI"), new SettingOption(Asio, "ASIO")];
        BufferSizes = [.. BufferChoices.Select(b => new SettingOption(b.ToString(), b.ToString()))];
        SampleRates = [.. RateChoices.Select(r => new SettingOption(r.ToString(), r.ToString()))];
        Languages =
        [
            new SettingOption("system", Loc("Language_System")),
            new SettingOption("en-US", Loc("Language_English")),
            new SettingOption("de-DE", Loc("Language_German")),
            new SettingOption("ja-JP", Loc("Language_Japanese"))
        ];

        _loading = true;
        var cfg = _config.Load();
        _selectedDriver = Drivers.FirstOrDefault(d => d.Value == cfg.AudioDriver) ?? Drivers[0];
        _selectedBufferSize = Nearest(BufferSizes, cfg.AudioBufferFrames);
        _selectedSampleRate = Nearest(SampleRates, cfg.AudioSampleRate);
        _enableInput = cfg.EnableInput;
        _selectedLanguage = Languages.FirstOrDefault(l => l.Value == cfg.LanguageCulture) ?? Languages[0];
        _loading = false;
    }

    public IReadOnlyList<SettingOption> Drivers { get; }
    public IReadOnlyList<SettingOption> BufferSizes { get; }
    public IReadOnlyList<SettingOption> SampleRates { get; }
    public IReadOnlyList<SettingOption> Languages { get; }
    public ObservableCollection<string> OutputDevices { get; } = [];
    public ObservableCollection<string> InputDevices { get; } = [];
    public LiveInputChannelViewModel InputChannel { get; }

    [ObservableProperty] private SettingOption _selectedDriver;
    [ObservableProperty] private string? _selectedOutputDevice;
    [ObservableProperty] private string? _selectedInputDevice;
    [ObservableProperty] private SettingOption _selectedBufferSize;
    [ObservableProperty] private SettingOption _selectedSampleRate;
    [ObservableProperty] private bool _enableInput;
    [ObservableProperty] private SettingOption _selectedLanguage;
    [ObservableProperty] private string _statusText = string.Empty;
    [ObservableProperty] private bool _isEngineRunning;
    [ObservableProperty] private bool _isBusy;

    public bool IsAsio => SelectedDriver.Value == Asio;

    /// <summary>ASIO has one device for both directions; WASAPI picks them
    /// separately.</summary>
    public bool HasSeparateInputDevice => !IsAsio;

    /// <summary>Supplies the window handle the ASIO control panel parents to.
    /// Set by the view.</summary>
    public Func<IntPtr>? WindowHandle { get; set; }

    /// <summary>Enumerates the devices for the chosen driver (off the UI
    /// thread) and re-selects the configured ones. Called when the tab opens and
    /// when the driver changes.</summary>
    public Task RefreshDevicesAsync()
    {
        var driver = SelectedDriver.Value;
        IsBusy = true;
        return _worker(() =>
        {
            var (outputs, inputs) = EnumerateDevices(driver);
            _ui.Post(() => ApplyDeviceLists(driver, outputs, inputs));
            return Task.CompletedTask;
        });
    }

    /// <summary>
    ///     The devices come from the LIVE engine, so they may only be read once
    ///     the engine has been re-opened for the driver being listed. Reading
    ///     them alongside the re-open (the first cut ran both at once) caught
    ///     the engine mid-rebuild and showed an empty ASIO list until the tab was
    ///     entered again.
    /// </summary>
    private (List<string> Outputs, List<string> Inputs) EnumerateDevices(string driver)
    {
        try
        {
            var cfg = _config.Load();
            if (driver == Asio)
            {
                _audio.EnsureAsioDevicesProbed(cfg);
                var asio = _audio.GetAsioDevices();
                return (asio, asio);
            }

            _audio.EnsureWasapiDevicesProbed(cfg);
            return (_audio.GetWasapiOutputDevices(), _audio.GetWasapiInputDevices());
        }
        catch (Exception)
        {
            // A driver that throws on enumeration shows empty lists; the status
            // line says the engine is down.
            return ([], []);
        }
    }

    private void ApplyDeviceLists(string driver, List<string> outputs, List<string> inputs)
    {
        if (driver != SelectedDriver.Value) return; // a newer refresh is on its way
        var cfg = _config.Load();
        var wantedOut = driver == Asio ? cfg.AsioOutputDeviceName : cfg.WasapiOutputDeviceName;
        var wantedIn = driver == Asio ? cfg.AsioInputDeviceName : cfg.WasapiInputDeviceName;
        _loading = true;
        try
        {
            Replace(OutputDevices, outputs);
            Replace(InputDevices, inputs);
            SelectedOutputDevice = Pick(OutputDevices, wantedOut);
            SelectedInputDevice = Pick(InputDevices, wantedIn);
        }
        finally
        {
            _loading = false;
            IsBusy = false;
        }

        // The configured device is gone (or none was ever chosen, the first
        // switch to ASIO): the picker shows the first one, so the engine must
        // run on it too — otherwise the screen names a device nobody opened.
        var outChanged = SelectedOutputDevice is { } o &&
                         !string.Equals(o, wantedOut, StringComparison.OrdinalIgnoreCase);
        var inChanged = driver != Asio && SelectedInputDevice is { } i &&
                        !string.Equals(i, wantedIn, StringComparison.OrdinalIgnoreCase);
        if (outChanged || inChanged)
        {
            var output = SelectedOutputDevice;
            var input = SelectedInputDevice;
            _config.Update(c =>
            {
                if (driver == Asio)
                {
                    c.AsioOutputDeviceName = output ?? string.Empty;
                    c.AsioInputDeviceName = output ?? string.Empty;
                }
                else
                {
                    if (output != null) c.WasapiOutputDeviceName = output;
                    if (input != null) c.WasapiInputDeviceName = input;
                }
            });
            _ = ApplyAsync();
            return;
        }

        RefreshStatus();
        InputChannel.Refresh();
    }

    partial void OnSelectedDriverChanged(SettingOption value)
    {
        OnPropertyChanged(nameof(IsAsio));
        OnPropertyChanged(nameof(HasSeparateInputDevice));
        if (_loading) return;
        _config.Update(c => c.AudioDriver = value.Value);
        // Re-open FIRST, list AFTER: the device lists are read off the engine.
        _ = ApplyAsync(refreshDevices: true);
    }

    partial void OnSelectedOutputDeviceChanged(string? value)
    {
        if (_loading || value is null) return;
        var asio = IsAsio;
        _config.Update(c =>
        {
            if (asio)
            {
                c.AsioOutputDeviceName = value;
                // One ASIO driver serves both directions.
                c.AsioInputDeviceName = value;
            }
            else c.WasapiOutputDeviceName = value;
        });
        _ = ApplyAsync();
    }

    partial void OnSelectedInputDeviceChanged(string? value)
    {
        if (_loading || value is null || IsAsio) return;
        _config.Update(c => c.WasapiInputDeviceName = value);
        _ = ApplyAsync();
    }

    partial void OnSelectedBufferSizeChanged(SettingOption value)
    {
        if (_loading) return;
        _config.Update(c => c.AudioBufferFrames = int.Parse(value.Value));
        _ = ApplyAsync();
    }

    partial void OnSelectedSampleRateChanged(SettingOption value)
    {
        if (_loading) return;
        _config.Update(c => c.AudioSampleRate = int.Parse(value.Value));
        _ = ApplyAsync();
    }

    partial void OnEnableInputChanged(bool value)
    {
        if (_loading) return;
        _config.Update(c => c.EnableInput = value);
        _ = ApplyAsync();
    }

    partial void OnSelectedLanguageChanged(SettingOption value)
    {
        if (_loading) return;
        _config.Update(c => c.LanguageCulture = value.Value);
        _localization.SetConfiguredCulture(value.Value);
        RefreshStatus();
    }

    /// <summary>Re-opens the engine with the saved settings. Only the LATEST
    /// request reports back: two quick picks must not end with the first one's
    /// status over the second one's device.</summary>
    public Task ApplyAsync(bool refreshDevices = false)
    {
        var generation = Interlocked.Increment(ref _applyGeneration);
        var driver = SelectedDriver.Value;
        IsBusy = true;
        return _worker(() =>
        {
            try { _audio.ApplyAudioConfiguration(_config.Load()); }
            catch (Exception)
            {
                // Reported through the status line: the engine is simply down.
            }

            var devices = refreshDevices ? EnumerateDevices(driver) : default;
            _ui.Post(() =>
            {
                if (generation != Volatile.Read(ref _applyGeneration)) return;
                if (refreshDevices) ApplyDeviceLists(driver, devices.Outputs, devices.Inputs);
                IsBusy = false;
                RefreshStatus();
                InputChannel.Refresh();
            });
            return Task.CompletedTask;
        });
    }

    [RelayCommand]
    private void OpenAsioControlPanel()
    {
        var handle = WindowHandle?.Invoke() ?? IntPtr.Zero;
        if (!_audio.TryOpenAsioControlPanel(handle, out var error) && !string.IsNullOrWhiteSpace(error))
            StatusText = error;
        else
            RefreshStatus();
    }

    public void RefreshStatus()
    {
        var rate = _audio.ActiveSampleRate;
        var frames = _audio.ActualBufferFrames;
        IsEngineRunning = rate > 0 && frames > 0;
        StatusText = IsEngineRunning
            ? string.Format(Loc("Settings_BufferLatencyDescription"), frames, frames * 1000.0 / rate, rate / 1000.0)
            : Loc("RigLab_Audio_EngineDown");
    }

    private static SettingOption Nearest(IReadOnlyList<SettingOption> options, int value) =>
        options.OrderBy(o => Math.Abs(int.Parse(o.Value) - value)).First();

    private static string? Pick(IReadOnlyList<string> devices, string? wanted) =>
        devices.FirstOrDefault(d => string.Equals(d, wanted, StringComparison.OrdinalIgnoreCase))
        ?? devices.FirstOrDefault();

    private static void Replace(ObservableCollection<string> target, IReadOnlyList<string> items)
    {
        target.Clear();
        foreach (var item in items) target.Add(item);
    }

    private string Loc(string key) => _localization[key];
}
