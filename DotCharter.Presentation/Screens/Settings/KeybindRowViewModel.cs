using System.Reflection;
using System.Windows.Input;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using DotCharter.Localization;
using DotCharter.Presentation.Contracts.Settings;
using DotCharter.Services.Infrastructure.Configuration;

namespace DotCharter.Presentation.Screens.Settings;

/// <summary>What a keybind row asks of its tab about MIDI: the mode, and learning.</summary>
internal interface IKeybindMidiHost
{
    bool IsMidiMode { get; }

    void ToggleLearn(KeybindRowViewModel row);
}

/// <summary>
///     One keybind row over the LIVE table. The table is replaced rather than
///     mutated by a revert and by a backup import, so the row reads it through
///     a supplier and never holds an instance; the tab re-announces every chord
///     and every MIDI trigger when the table changes.
/// </summary>
public sealed class KeybindRowViewModel : ObservableObject, IKeybindRow
{
    private static readonly Dictionary<string, PropertyInfo> Properties = typeof(KeybindConfig)
        .GetProperties(BindingFlags.Public | BindingFlags.Instance)
        .Where(p => p.PropertyType == typeof(KeyChord) && p.CanRead && p.CanWrite)
        .ToDictionary(p => p.Name, StringComparer.Ordinal);

    private readonly KeybindRowSpec _spec;
    private readonly ILocalizationService _localization;
    private readonly Func<KeybindConfig> _table;
    private readonly IKeybindMidiHost _host;
    private readonly PropertyInfo _property;
    private bool _isVisible = true;
    private bool _isMidiLearning;

    internal KeybindRowViewModel(KeybindRowSpec spec, ILocalizationService localization, Func<KeybindConfig> table,
        IKeybindMidiHost host)
    {
        _spec = spec;
        _localization = localization;
        _table = table;
        _host = host;
        _property = Properties.TryGetValue(spec.Property, out var property)
            ? property
            : throw new ArgumentException($"KeybindConfig has no KeyChord property '{spec.Property}'.", nameof(spec));
        IsMidiBindable = KeybindConfig.IsMidiBindable(spec.Property);
        LearnMidiCommand = new RelayCommand(() => _host.ToggleLearn(this), () => IsMidiBindable);
        ClearMidiCommand = new RelayCommand(() => MidiTrigger = MidiTrigger.None, () => IsMidiBindable);
    }

    /// <summary>The change-set key: <c>keybinds.PlayPause</c>.</summary>
    public string Key => "keybinds." + _spec.Property;

    /// <summary>The change-set key of the row's MIDI trigger: <c>midi.PlayPause</c>.</summary>
    public string MidiKey => "midi." + _spec.Property;

    public string Property => _spec.Property;

    public string Label => _localization[_spec.LabelKey];

    public string Tooltip => _localization[_spec.TooltipKey ?? _spec.LabelKey];

    /// <summary>Written straight into the table, which announces the change; the tab forwards it here.</summary>
    public KeyChord Chord
    {
        get => (KeyChord)_property.GetValue(_table())!;
        set => _property.SetValue(_table(), value);
    }

    public bool IsVisible
    {
        get => _isVisible;
        internal set => SetProperty(ref _isVisible, value);
    }

    public bool ShowsMidi => _host.IsMidiMode;

    public bool IsMidiBindable { get; }

    /// <summary>Written straight into the table, which announces the change; the tab forwards it here.</summary>
    public MidiTrigger MidiTrigger
    {
        get => IsMidiBindable ? _table().GetMidiTrigger(_spec.Property) : MidiTrigger.None;
        set
        {
            if (IsMidiBindable) _table().SetMidiTrigger(_spec.Property, value);
        }
    }

    public bool IsMidiBound => MidiTrigger.IsBound;

    public string MidiText => IsMidiLearning ? _localization["Keybind_Midi_Listening"]
        : !IsMidiBindable ? _localization["Keybind_Midi_KeyboardOnly"]
        : MidiTrigger.ToDisplayString(_localization);

    public bool IsMidiLearning
    {
        get => _isMidiLearning;
        internal set
        {
            if (SetProperty(ref _isMidiLearning, value)) OnPropertyChanged(nameof(MidiText));
        }
    }

    public ICommand LearnMidiCommand { get; }

    public ICommand ClearMidiCommand { get; }

    internal void AnnounceChord() => OnPropertyChanged(nameof(Chord));

    internal void AnnounceMidi()
    {
        OnPropertyChanged(nameof(MidiTrigger));
        OnPropertyChanged(nameof(IsMidiBound));
        OnPropertyChanged(nameof(MidiText));
    }

    internal void AnnounceMode() => OnPropertyChanged(nameof(ShowsMidi));

    internal void AnnounceText()
    {
        OnPropertyChanged(nameof(Label));
        OnPropertyChanged(nameof(Tooltip));
        OnPropertyChanged(nameof(MidiText));
    }
}

/// <summary>One keybind group: a title, a subtitle, a glyph and its rows.</summary>
public sealed class KeybindGroupViewModel : ObservableObject, IKeybindGroup
{
    private readonly KeybindGroupSpec _spec;
    private readonly ILocalizationService _localization;
    private bool _isVisible = true;

    internal KeybindGroupViewModel(KeybindGroupSpec spec, ILocalizationService localization, Func<KeybindConfig> table,
        IKeybindMidiHost host)
    {
        _spec = spec;
        _localization = localization;
        RowModels = [.. spec.Rows.Select(r => new KeybindRowViewModel(r, localization, table, host))];
    }

    public IReadOnlyList<KeybindRowViewModel> RowModels { get; }

    public string Title => _localization[_spec.TitleKey];

    public string Subtitle => _localization[_spec.SubtitleKey];

    public string IconKey => _spec.IconKey;

    public IReadOnlyList<IKeybindRow> Rows => RowModels;

    public bool IsVisible
    {
        get => _isVisible;
        internal set => SetProperty(ref _isVisible, value);
    }

    internal void AnnounceText()
    {
        OnPropertyChanged(nameof(Title));
        OnPropertyChanged(nameof(Subtitle));
        foreach (var row in RowModels) row.AnnounceText();
    }
}
