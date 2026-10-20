using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using DotCharter.Tone.Audio.Midi;

namespace DotCharter.Tone.UI.ViewModels.PlayMode;

/// <summary>One MIDI channel choice for the rig's program changes and scene CC (0 = omni).</summary>
public sealed record RigMidiChannelOption(int Channel, string Label)
{
    public override string ToString() => Label;
}

/// <summary>One MIDI connection for the shared rig, with bounded UI delivery.</summary>
public sealed partial class RigMidiInputViewModel : ObservableObject, IDisposable
{
    private readonly IRigMidiInputSource? _source;
    private readonly IRigConfigService? _config;
    private readonly ILocalizationService _loc;
    private readonly Action<Action> _post;
    private readonly Action<int, int, int> _consume;
    private readonly Action<int, int>? _consumeProgram;
    private readonly object _pendingGate = new();
    private readonly Dictionary<int, (int Generation, int Value)> _pending = [];
    // ONE slot: a burst of program changes is "go to the last one", never a queue
    // of tone switches the rig would then play through one by one.
    private (int Generation, int Channel, int Program)? _pendingProgram;
    private readonly SemaphoreSlim _connectionGate = new(1, 1);
    private bool _posted;
    private bool _connecting;
    private volatile bool _accepting = true;
    private volatile bool _disposed;
    private int _connectionGeneration;
    private PedalboardViewModel? _rigScope;

    public RigMidiInputViewModel(IRigMidiInputSource? source, IRigConfigService? config,
        ILocalizationService localization, Action<Action> post, Action<int, int, int> consume, Action<int, int>? consumeProgram = null)
    {
        _source = source;
        _config = config;
        _loc = localization;
        _post = post;
        _consume = consume;
        _consumeProgram = consumeProgram;
        ChannelOptions = [new RigMidiChannelOption(0, _loc["Rig_Midi_ChannelOmni"]),
            .. Enumerable.Range(1, 16).Select(c => new RigMidiChannelOption(c, c.ToString(System.Globalization.CultureInfo.CurrentCulture)))];
        LoadRouting();
        if (source is null) return;
        source.RigMidiControlReceived += OnControl;
        source.RigMidiProgramReceived += OnProgram;
        source.RigMidiInputStatusChanged += OnStatus;
        try
        {
            var saved = config?.LoadRigConfig().RigMidiInputDevice;
            if (!string.IsNullOrWhiteSpace(saved)) Devices.Add(saved);
            SelectedDevice = saved;
        }
        catch (Exception ex) { Status = ex.Message; }
    }

    public ObservableCollection<string> Devices { get; } = [];
    public bool IsAvailable => _source is not null;
    public string ConnectionHint => _loc[_connecting ? "Rig_MidiConnecting" :
        SelectedDevice is null ? "Rig_MidiDisconnected" : _source?.RigMidiInputConnected == true ? "Rig_MidiConnected" : "Rig_MidiWaiting"];
    [ObservableProperty] private string? _selectedDevice;
    [ObservableProperty] private string? _status;

    partial void OnSelectedDeviceChanged(string? value)
    {
        _accepting = false;
        _connecting = true;
        OnPropertyChanged(nameof(ConnectionHint));
        ClearPending();
        try { _config?.UpdateRigConfig(c => c.RigMidiInputDevice = value); }
        catch (Exception ex) { Status = ex.Message; }
        _ = ConnectAsync(value, Interlocked.Increment(ref _connectionGeneration));
    }

    private async Task ConnectAsync(string? device, int generation)
    {
        if (_source is null) return;
        await _connectionGate.WaitAsync().ConfigureAwait(false);
        try
        {
            if (_disposed || generation != Volatile.Read(ref _connectionGeneration)) return;
            await Task.Run(() => _source.SelectRigMidiInput(device)).ConfigureAwait(false);
            _post(() =>
            {
                if (!_disposed && generation == Volatile.Read(ref _connectionGeneration))
                {
                    ClearPending();
                    _connecting = false;
                    // An engine rebuild may have opened the selected device after
                    // SelectRigMidiInput returned, but before this UI continuation.
                    _accepting = device is not null && _source.RigMidiInputConnected;
                    Status = _source.RigMidiInputError;
                    OnPropertyChanged(nameof(ConnectionHint));
                }
            });
        }
        catch (Exception ex)
        {
            _post(() =>
            {
                if (_disposed || generation != Volatile.Read(ref _connectionGeneration)) return;
                _connecting = false;
                Status = ex.Message;
                OnPropertyChanged(nameof(ConnectionHint));
            });
        }
        finally { _connectionGate.Release(); }
    }

    [RelayCommand]
    private async Task RefreshAsync()
    {
        if (_source is null) return;
        try
        {
            var devices = await Task.Run(_source.GetRigMidiInputs);
            if (_disposed) return;
            // Keep the selected item while refreshing: clearing ItemsSource can
            // write a null selection back through the picker and disconnect it.
            foreach (var stale in Devices.Where(d => d != SelectedDevice && !devices.Contains(d)).ToArray()) Devices.Remove(stale);
            foreach (var device in devices) if (!Devices.Contains(device)) Devices.Add(device);
            Status = _source.RigMidiInputError;
        }
        catch (Exception ex) { if (!_disposed) Status = ex.Message; }
    }

    [RelayCommand]
    private void Disconnect() => SelectedDevice = null;

    private void OnStatus() => _post(() =>
    {
        if (_disposed) return;
        if (!_connecting) _accepting = SelectedDevice is not null && _source?.RigMidiInputConnected == true;
        Status = _source?.RigMidiInputError;
        OnPropertyChanged(nameof(ConnectionHint));
    });

    private void OnControl(int generation, int channel, int controller, int value)
    {
        if ((uint)channel > 15 || (uint)controller > 127 || (uint)value > 127) return;
        lock (_pendingGate)
        {
            if (_disposed || !_accepting || generation != _source?.RigMidiInputGeneration) return;
            _pending[channel * 128 + controller] = (generation, value); // at most 2,048 keys
            if (_posted) return;
            _posted = true;
        }
        _post(Drain);
    }

    private void OnProgram(int generation, int channel, int program)
    {
        if ((uint)channel > 15 || (uint)program > 127) return;
        lock (_pendingGate)
        {
            if (_disposed || !_accepting || generation != _source?.RigMidiInputGeneration) return;
            _pendingProgram = (generation, channel, program);
            if (_posted) return;
            _posted = true;
        }
        _post(Drain);
    }

    private void Drain()
    {
        KeyValuePair<int, (int Generation, int Value)>[] values;
        (int Generation, int Channel, int Program)? program;
        lock (_pendingGate)
        {
            _posted = false;
            if (_disposed) return;
            values = _pending.ToArray();
            _pending.Clear();
            program = _pendingProgram;
            _pendingProgram = null;
        }
        foreach (var (key, message) in values)
        {
            if (!_disposed && _accepting && message.Generation == _source?.RigMidiInputGeneration)
            {
                LastMessage = string.Format(System.Globalization.CultureInfo.CurrentCulture,
                    _loc["Rig_Midi_LastControl"], key % 128, message.Value, key / 128 + 1);
                _consume(key / 128, key % 128, message.Value);
            }
        }

        if (program is { } pc && !_disposed && _accepting && pc.Generation == _source?.RigMidiInputGeneration)
        {
            LastMessage = string.Format(System.Globalization.CultureInfo.CurrentCulture,
                _loc["Rig_Midi_LastProgram"], pc.Program, pc.Channel + 1);
            _consumeProgram?.Invoke(pc.Channel, pc.Program);
        }
    }

    // ── what a program change and the scene CC do (RigConfig) ────────────────

    /// <summary>The last message the controller sent (drained on the UI thread),
    /// so a player can see the pedal works before trusting it on a stage.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(LastMessageText))]
    private string? _lastMessage;

    /// <summary><see cref="LastMessage" />, or "nothing received yet".</summary>
    public string LastMessageText => LastMessage ?? _loc["Rig_Midi_NothingYet"];

    /// <summary>Omni, then channels 1..16.</summary>
    public IReadOnlyList<RigMidiChannelOption> ChannelOptions { get; }

    [ObservableProperty] private RigMidiChannelOption? _selectedChannel;
    [ObservableProperty] private bool _isProgramChangeEnabled = true;
    [ObservableProperty] private bool _isSceneCcEnabled = true;
    [ObservableProperty] private decimal? _sceneCc = RigMidiRouting.DefaultSceneCc;
    private bool _loadingRouting;

    /// <summary>What a program change and the scene CC do, as the settings say now.</summary>
    public RigMidiRouting Routing => new(
        SelectedChannel?.Channel ?? 0,
        IsProgramChangeEnabled,
        IsSceneCcEnabled && SceneCc is { } cc ? (int)Math.Clamp(cc, 0, 127) : -1);

    private void LoadRouting()
    {
        var routing = RigMidiRouting.Default;
        try { if (_config is not null) routing = RigMidiRouting.From(_config.LoadRigConfig()); }
        catch (Exception ex) { Status = ex.Message; }
        _loadingRouting = true;
        SelectedChannel = ChannelOptions.FirstOrDefault(o => o.Channel == routing.Channel) ?? ChannelOptions[0];
        IsProgramChangeEnabled = routing.ProgramChange;
        IsSceneCcEnabled = routing.SceneCc >= 0;
        SceneCc = routing.SceneCc >= 0 ? routing.SceneCc : RigMidiRouting.DefaultSceneCc;
        _loadingRouting = false;
    }

    partial void OnSelectedChannelChanged(RigMidiChannelOption? value) => PersistRouting();
    partial void OnIsProgramChangeEnabledChanged(bool value) => PersistRouting();
    partial void OnIsSceneCcEnabledChanged(bool value) => PersistRouting();
    partial void OnSceneCcChanged(decimal? value) => PersistRouting();

    private void PersistRouting()
    {
        if (_loadingRouting || _config is null) return;
        var routing = Routing;
        try
        {
            _config.UpdateRigConfig(c =>
            {
                c.RigMidiChannel = routing.Channel;
                c.RigMidiProgramChange = routing.ProgramChange;
                c.RigMidiSceneCc = routing.SceneCc;
            });
        }
        catch (Exception ex) { Status = ex.Message; }
    }

    internal void BindRigScope(PedalboardViewModel rig)
    {
        UnbindRigScope();
        _rigScope = rig;
        rig.PropertyChanged += OnRigScopeChanged;
        rig.Nodes.CollectionChanged += OnRigNodesChanged;
        rig.Browser.PropertyChanged += OnPresetChanged;
        ClearPending();
    }
    private void OnRigScopeChanged(object? sender, System.ComponentModel.PropertyChangedEventArgs e)
    {
        if (e.PropertyName is nameof(PedalboardViewModel.SelectedSeat) or nameof(PedalboardViewModel.SelectedNode)) ClearPending();
    }
    private void OnRigNodesChanged(object? sender, System.Collections.Specialized.NotifyCollectionChangedEventArgs e) => ClearPending();
    private void OnPresetChanged(object? sender, System.ComponentModel.PropertyChangedEventArgs e)
    {
        if (e.PropertyName == "ActivePreset") ClearPending();
    }
    private void UnbindRigScope()
    {
        if (_rigScope is not { } rig) return;
        rig.PropertyChanged -= OnRigScopeChanged;
        rig.Nodes.CollectionChanged -= OnRigNodesChanged;
        rig.Browser.PropertyChanged -= OnPresetChanged;
        _rigScope = null;
    }

    /// <summary>Learning starts with the next incoming CC, not a queued old value.</summary>
    public void ClearPending()
    {
        lock (_pendingGate)
        {
            _pending.Clear();
            _pendingProgram = null;
        }
    }

    public void Dispose()
    {
        lock (_pendingGate) { _disposed = true; _pending.Clear(); _pendingProgram = null; }
        Interlocked.Increment(ref _connectionGeneration);
        UnbindRigScope();
        if (_source is not null)
        {
            _source.RigMidiControlReceived -= OnControl;
            _source.RigMidiProgramReceived -= OnProgram;
            _source.RigMidiInputStatusChanged -= OnStatus;
        }
    }
}
