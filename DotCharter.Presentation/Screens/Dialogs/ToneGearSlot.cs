using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using DotCharter.Presentation.Contracts.Dialogs;
using DotCharter.Tone.Audio.Dsp.Tones;

namespace DotCharter.Presentation.Screens.Dialogs;

/// <summary>One gear slot of the tone editor: the options of its gear family and the knob rows of the gear picked.</summary>
public sealed partial class ToneGearSlot : ObservableObject, IToneGearSlot
{
    private readonly Action _onChanged;
    private bool _loading;

    /// <summary>Knob values carried verbatim for gear with no catalogue knob metadata; reset when other gear is picked.</summary>
    private IReadOnlyDictionary<string, float> _rawValues = new Dictionary<string, float>(StringComparer.Ordinal);

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasGear))]
    [NotifyPropertyChangedFor(nameof(GearDisplayName))]
    private ToneGearOption _selectedOption;

    public ToneGearSlot(string label, RsGearRole role, int slotIndex, IReadOnlyList<ToneGearOption> options, Action onChanged)
    {
        Label = label;
        Role = role;
        SlotIndex = slotIndex;
        Options = options;
        _onChanged = onChanged;
        _selectedOption = options[0];
    }

    public string Label { get; }

    public RsGearRole Role { get; }

    /// <summary>1..4 for pedal and rack slots; 0 for the amp and the cabinet.</summary>
    public int SlotIndex { get; }

    public IReadOnlyList<ToneGearOption> Options { get; }

    public ObservableCollection<ToneKnobItem> Knobs { get; } = [];

    public bool HasGear => SelectedOption?.Gear is not null;

    public string GearDisplayName => SelectedOption?.DisplayName ?? "";

    public bool HasKnobs => Knobs.Count > 0;

    public bool ShowNoKnobsHint => HasGear && Knobs.Count == 0;

    IReadOnlyList<IToneChoice> IToneGearSlot.Options => Options;

    IReadOnlyList<IToneKnob> IToneGearSlot.Knobs => Knobs;

    IToneChoice IToneGearSlot.SelectedOption
    {
        get => SelectedOption;
        set => SelectedOption = value as ToneGearOption ?? null!;
    }

    /// <summary>Loads a parsed gear item without marking the editor dirty; an unknown key degrades to the first option.</summary>
    internal void Load(RsGear? gear)
    {
        _loading = true;
        try
        {
            if (gear is null)
            {
                _rawValues = new Dictionary<string, float>(StringComparer.Ordinal);
                SelectedOption = Options[0];
                Knobs.Clear();
                OnPropertyChanged(nameof(HasKnobs));
                OnPropertyChanged(nameof(ShowNoKnobsHint));
                return;
            }

            var option = Options.FirstOrDefault(o => o.Gear is { } g && string.Equals(g.Key, gear.GearKey, StringComparison.Ordinal))
                         ?? Options[0];
            _rawValues = new Dictionary<string, float>(gear.KnobValues, StringComparer.Ordinal);
            SelectedOption = option;
            RebuildKnobs(option.Gear, _rawValues);
        }
        finally
        {
            _loading = false;
        }
    }

    /// <summary>The values to serialize: the knob rows (clamped) when the gear has catalogue knobs, else the raw values verbatim.</summary>
    internal IReadOnlyDictionary<string, float> BuildKnobValues()
    {
        if (SelectedOption?.Gear?.Knobs is not { Count: > 0 }) return _rawValues;
        var values = new Dictionary<string, float>(Knobs.Count, StringComparer.Ordinal);
        foreach (var knob in Knobs) values[knob.Knob.Key] = knob.ClampedValue;
        return values;
    }

    /// <summary>The pedal or rack record for this slot; null when it is empty.</summary>
    internal RsPedal? BuildPedal()
    {
        if (SelectedOption?.Gear is not { } gear) return null;
        var type = Role == RsGearRole.Rack ? RsGearType.Rack : RsGearType.Pedal;
        return new RsPedal(Role, SlotIndex, gear.Key, type, gear.Category, BuildKnobValues());
    }

    partial void OnSelectedOptionChanged(ToneGearOption? oldValue, ToneGearOption newValue)
    {
        if (newValue is null)
        {
            // The shared gear picker writes null through while its items swap to another slot's options.
            // It must never stick: a stale display, then a crash on close. Restored under _loading so knob values survive.
            _loading = true;
            try
            {
                SelectedOption = oldValue ?? Options[0];
            }
            finally
            {
                _loading = false;
            }

            return;
        }

        if (_loading) return;
        _rawValues = newValue.Gear is { } gear
            ? RsToneAuthoring.DefaultKnobValues(gear)
            : new Dictionary<string, float>(StringComparer.Ordinal);
        RebuildKnobs(newValue.Gear, _rawValues);
        _onChanged();
    }

    private void RebuildKnobs(RsGearData? gear, IReadOnlyDictionary<string, float> values)
    {
        Knobs.Clear();
        foreach (var knob in gear?.Knobs ?? [])
            Knobs.Add(new ToneKnobItem(knob, values.TryGetValue(knob.Key, out var v) ? v : knob.DefaultValue, _onChanged));
        OnPropertyChanged(nameof(HasKnobs));
        OnPropertyChanged(nameof(ShowNoKnobsHint));
    }
}
