using System.ComponentModel;
using System.Windows.Input;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using DotCharter.Localization;
using DotCharter.Presentation.Contracts.Settings;
using DotCharter.Services.Infrastructure.Configuration;
using DotCharter.Services.Infrastructure.Input;
using DotCharter.Services.State;

namespace DotCharter.Presentation.Screens.Settings;

/// <summary>
///     Settings ▸ Keybinds over <see cref="ProjectState.Keybinds" />, the one
///     live table the registrar reads. Save persists that table and rebuilds
///     the dispatch; Revert assigns a fresh table loaded from disk — that
///     assignment IS the revert, so the rows follow the state's
///     <c>PropertyChanged</c> rather than holding the table they were built on.
///     The same rows bind the MIDI controller: in MIDI mode a row LEARNS the next
///     press from <see cref="IMidiTriggerInput" /> into the same table.
/// </summary>
public sealed class KeybindsSettingsViewModel : ObservableObject,
    IKeybindsSettingsScreen, ISettingsTab, ISettingsChangeSource, IKeybindMidiHost, IDisposable
{
    private readonly IConfigurationService _config;
    private readonly ProjectState _state;
    private readonly IKeybindDispatch _dispatch;
    private readonly ILocalizationService _localization;
    private readonly IMidiTriggerInput _midi;
    private readonly List<KeybindGroupViewModel> _groups;
    private readonly Dictionary<string, KeybindRowViewModel> _rowsByProperty;
    private readonly SettingsChangeSet _changes = new();
    private KeybindConfig? _watched;
    private KeybindRowViewModel? _learning;
    private bool _isMidiMode;

    public KeybindsSettingsViewModel(
        IConfigurationService config,
        ProjectState state,
        IKeybindDispatch dispatch,
        ILocalizationService localization,
        IMidiTriggerInput midi)
    {
        _config = config;
        _state = state;
        _dispatch = dispatch;
        _localization = localization;
        _midi = midi;

        _groups = [.. KeybindRowCatalog.Groups.Select(g => new KeybindGroupViewModel(g, localization, () => _state.Keybinds, this))];
        _rowsByProperty = _groups.SelectMany(g => g.RowModels).ToDictionary(r => r.Property, StringComparer.Ordinal);
        foreach (var row in _rowsByProperty.Values)
        {
            _changes.Track(row.Key, () => row.Chord);
            if (row.IsMidiBindable) _changes.Track(row.MidiKey, () => row.MidiTrigger);
        }

        ResetToDefaultsCommand = new RelayCommand(ResetToDefaults);
        ClearMidiBindingsCommand = new RelayCommand(() => _state.Keybinds.ClearMidiBindings());

        Watch(_state.Keybinds);
        _state.PropertyChanged += OnStateChanged;
        _localization.PropertyChanged += OnLanguageChanged;
        _midi.Pressed += OnMidiPressed;
        _midi.StatusChanged += OnMidiStatusChanged;
        _changes.Rebaseline();
    }

    public IReadOnlyList<IKeybindGroup> Groups => _groups;

    public ICommand ResetToDefaultsCommand { get; }

    public ICommand ClearMidiBindingsCommand { get; }

    public bool IsMidiMode
    {
        get => _isMidiMode;
        set
        {
            if (_isMidiMode == value) return;
            _isMidiMode = value;
            if (!value) StopLearning();
            OnPropertyChanged();
            OnPropertyChanged(nameof(IsKeyboardMode));
            foreach (var row in _rowsByProperty.Values) row.AnnounceMode();
        }
    }

    public bool IsKeyboardMode
    {
        get => !_isMidiMode;
        set => IsMidiMode = !value;
    }

    public bool IsMidiInputLive => _midi.IsAvailable && _midi.DeviceName is not null && _midi.IsConnected;

    public string MidiInputStatus
    {
        get
        {
            if (!_midi.IsAvailable) return _localization["Settings_MidiInput_NoEngine"];
            if (_midi.DeviceName is not { } device) return _localization["Settings_MidiInput_NoDevice"];
            if (_midi.IsConnected) return _localization.Format("Settings_MidiInput_Live", device);
            return _midi.Error is { Length: > 0 } error
                ? _localization.Format("Settings_MidiInput_DeviceError", device, error)
                : _localization.Format("Settings_MidiInput_DeviceWaiting", device);
        }
    }

    public string Key => SettingsTabKeys.Keybinds;

    public Type? Screen => typeof(IKeybindsSettingsScreen);

    public IReadOnlyList<string> UnsavedSettings => _changes.Differences();

    public IReadOnlyList<SettingsSearchGroup> SearchGroups =>
    [
        new(_localization["Settings_KeybindsTab"], _localization["Settings_ClickKeybindField"], []),
        new(_localization["Keybind_Mode_Midi"], _localization["Keybind_Midi_SearchSubtitle"], []),
        .. _groups.Select(g => new SettingsSearchGroup(g.Title, g.Subtitle,
            [.. g.RowModels.Select(r => new SettingsSearchRow(r.Key, r.Label))]))
    ];

    /// <summary>The table lives on the state and is loaded at startup; a load here only retakes the baseline.</summary>
    public void Load()
    {
        Rebaseline();
        AnnounceChords();
        AnnounceMidi();
        AnnounceMidiStatus();
    }

    public void Save()
    {
        StopLearning();
        _config.SaveKeybinds(_state.Keybinds);
        _dispatch.RefreshBinds();
        Rebaseline();
    }

    public void Revert()
    {
        StopLearning();
        _state.Keybinds = _config.LoadKeybinds();
        _dispatch.RefreshBinds();
        Rebaseline();
    }

    public void Reset() => Revert();

    public void Rebaseline()
    {
        _changes.Rebaseline();
        OnPropertyChanged(nameof(UnsavedSettings));
    }

    public void ApplySearch(string query)
    {
        var hasQuery = query.Length > 0;
        // A hit on "MIDI controller" lands here meaning every row, not none of them.
        var modeMatches = hasQuery && SettingsSearch.Matches(_localization["Keybind_Mode_Midi"], query);
        foreach (var group in _groups)
        {
            var groupMatches = modeMatches || (hasQuery && (SettingsSearch.Matches(group.Title, query) || SettingsSearch.Matches(group.Subtitle, query)));
            var anyVisible = false;
            foreach (var row in group.RowModels)
            {
                var visible = !hasQuery || groupMatches || SettingsSearch.Matches(row.Label, query);
                row.IsVisible = visible;
                anyVisible |= visible;
            }

            group.IsVisible = !hasQuery || anyVisible;
        }
    }

    public void Dispose()
    {
        _state.PropertyChanged -= OnStateChanged;
        _localization.PropertyChanged -= OnLanguageChanged;
        _midi.Pressed -= OnMidiPressed;
        _midi.StatusChanged -= OnMidiStatusChanged;
        Watch(null);
    }

    void IKeybindMidiHost.ToggleLearn(KeybindRowViewModel row)
    {
        var restart = _learning != row;
        StopLearning();
        if (!restart || !row.IsMidiBindable) return;
        _learning = row;
        row.IsMidiLearning = true;
    }

    private void OnMidiPressed(MidiTrigger trigger)
    {
        if (_learning is not { } row || !_isMidiMode) return;
        StopLearning();
        row.MidiTrigger = trigger;
    }

    private void StopLearning()
    {
        if (_learning is null) return;
        _learning.IsMidiLearning = false;
        _learning = null;
    }

    private void ResetToDefaults()
    {
        _state.Keybinds.ResetToDefault();
        _dispatch.RefreshBinds();
    }

    private void Watch(KeybindConfig? table)
    {
        _watched?.PropertyChanged -= OnTableChanged;
        _watched = table;
        _watched?.PropertyChanged += OnTableChanged;
    }

    private void OnStateChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName is not (null or "" or nameof(ProjectState.Keybinds))) return;
        Watch(_state.Keybinds);
        AnnounceChords();
        AnnounceMidi();
    }

    private void OnTableChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (string.IsNullOrEmpty(e.PropertyName))
        {
            AnnounceChords();
            AnnounceMidi();
            return;
        }

        if (e.PropertyName == nameof(KeybindConfig.MidiBindings)) AnnounceMidi();
        else if (_rowsByProperty.TryGetValue(e.PropertyName, out var row)) row.AnnounceChord();
        OnPropertyChanged(nameof(UnsavedSettings));
    }

    private void AnnounceChords()
    {
        foreach (var row in _rowsByProperty.Values) row.AnnounceChord();
        OnPropertyChanged(nameof(UnsavedSettings));
    }

    private void AnnounceMidi()
    {
        foreach (var row in _rowsByProperty.Values) row.AnnounceMidi();
        OnPropertyChanged(nameof(UnsavedSettings));
    }

    private void OnMidiStatusChanged() => AnnounceMidiStatus();

    private void AnnounceMidiStatus()
    {
        OnPropertyChanged(nameof(MidiInputStatus));
        OnPropertyChanged(nameof(IsMidiInputLive));
    }

    private void OnLanguageChanged(object? sender, PropertyChangedEventArgs e)
    {
        foreach (var group in _groups) group.AnnounceText();
        OnPropertyChanged(nameof(SearchGroups));
        AnnounceMidiStatus();
    }
}
