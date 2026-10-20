using CommunityToolkit.Mvvm.ComponentModel;
using DotCharter.Presentation.Contracts.Dialogs;
using DotCharter.Core.Domain.Project;
using DotCharter.Services.Editing.Tones;
using DotCharter.Tone.Audio.Dsp.Tones;

namespace DotCharter.Presentation.Screens.Dialogs;

/// <summary>One instrumental arrangement's tone slots, over the real arrangement they are committed to on
/// close. A slot naming a key none of <see cref="Tones" /> carries is marked undefined.</summary>
public sealed partial class ToneDesignerArrangementItem : ObservableObject, IToneArrangementRow
{
    [ObservableProperty][NotifyPropertyChangedFor(nameof(IsToneBaseUndefined))] private string _toneBase = "";
    [ObservableProperty][NotifyPropertyChangedFor(nameof(IsToneAUndefined))] private string _toneA = "";
    [ObservableProperty][NotifyPropertyChangedFor(nameof(IsToneBUndefined))] private string _toneB = "";
    [ObservableProperty][NotifyPropertyChangedFor(nameof(IsToneCUndefined))] private string _toneC = "";
    [ObservableProperty][NotifyPropertyChangedFor(nameof(IsToneDUndefined))] private string _toneD = "";

    public required string Name { get; init; }

    public required string TypeLabel { get; init; }

    public required Arrangement Arrangement { get; init; }

    /// <summary>The project's tones, as the designer lists them: the keys a slot may name.</summary>
    public IReadOnlyList<ProjectToneItem> Tones { get; init; } = [];

    public bool IsToneBaseUndefined => IsUndefined(ToneBase);
    public bool IsToneAUndefined => IsUndefined(ToneA);
    public bool IsToneBUndefined => IsUndefined(ToneB);
    public bool IsToneCUndefined => IsUndefined(ToneC);
    public bool IsToneDUndefined => IsUndefined(ToneD);

    /// <summary>The project's tones changed (an add, a rename, a delete): every mark is asked again.</summary>
    public void RefreshDefinitionMarks()
    {
        OnPropertyChanged(nameof(IsToneBaseUndefined));
        OnPropertyChanged(nameof(IsToneAUndefined));
        OnPropertyChanged(nameof(IsToneBUndefined));
        OnPropertyChanged(nameof(IsToneCUndefined));
        OnPropertyChanged(nameof(IsToneDUndefined));
    }

    /// <summary>The distinct non-empty A–D slots and the base (the first slot when unnamed): the shape the SNG mapper derives.</summary>
    public (string ToneBase, string[] Slots) ToToneSlots()
    {
        var slots = new List<string>(4);
        foreach (var slot in new[] { ToneA, ToneB, ToneC, ToneD })
        {
            var trimmed = slot.Trim();
            if (trimmed.Length > 0 && !slots.Contains(trimmed)) slots.Add(trimmed);
        }

        var toneBase = ToneBase.Trim();
        if (toneBase.Length == 0) toneBase = slots.FirstOrDefault() ?? "";
        return (toneBase, slots.ToArray());
    }

    private bool IsUndefined(string key) => !string.IsNullOrWhiteSpace(key) && !Tones.Any(t => ToneKeyEdits.Same(t.Key, key));
}

public sealed class ImportedToneItem : IImportedTone
{
    public required string Key { get; init; }

    public required string Name { get; init; }
}

/// <summary>One project-owned tone: a list row, and the editor's identity while it is selected.</summary>
public sealed partial class ProjectToneItem(string key, string name, string json) : ObservableObject, IProjectTone
{
    [ObservableProperty] private string _key = key;
    [ObservableProperty] private string _name = name;

    /// <summary>The manifest tone JSON currently committed to the project.</summary>
    public string Json { get; set; } = json;
}

/// <summary>A gear picker entry; a null <see cref="Gear" /> is the "(empty)" sentinel.</summary>
public sealed record ToneGearOption(RsGearData? Gear, string DisplayName) : ISearchableChoice
{
    public string SearchKey => Gear?.Key ?? "";

    public override string ToString() => DisplayName;
}

public sealed record ToneDescriptorChoice(ToneDescriptorOption Option) : IToneChoice
{
    public string DisplayName => Option.Name;
}

/// <summary>One knob of the selected gear, with the catalogue's range and step.</summary>
public sealed partial class ToneKnobItem : ObservableObject, IToneKnob
{
    private readonly Action _onChanged;
    private readonly bool _initialized;

    [ObservableProperty] private double _value;

    public ToneKnobItem(RsGearKnob knob, float value, Action onChanged)
    {
        Knob = knob;
        _onChanged = onChanged;
        Minimum = Math.Min(knob.MinValue, knob.MaxValue);
        Maximum = Math.Max(knob.MinValue, knob.MaxValue);
        Step = knob.ValueStep > 0 ? knob.ValueStep : 0.1;
        _value = RsToneAuthoring.ClampKnobValue(knob, value);
        _initialized = true;
    }

    public RsGearKnob Knob { get; }
    public string DisplayName => Knob.Name;
    public double Minimum { get; }
    public double Maximum { get; }
    public double Step { get; }
    public bool IsEnum => Knob.UnitType == RsKnobUnit.Enum && Knob.EnumValues is { Count: > 0 };
    public IReadOnlyList<string> EnumOptions => Knob.EnumValues ?? [];

    /// <summary>The picker index for an enum knob: value = MinValue + index.</summary>
    public int EnumIndex
    {
        get => IsEnum ? Math.Clamp((int)Math.Round(Value - Knob.MinValue), 0, EnumOptions.Count - 1) : -1;
        set
        {
            if (IsEnum && value >= 0 && value != EnumIndex) Value = Knob.MinValue + value;
        }
    }

    public string ValueText => Knob.UnitType switch
    {
        RsKnobUnit.Percent => $"{Value:0}%",
        RsKnobUnit.Db => $"{Value:0.#} dB",
        RsKnobUnit.Hz => $"{Value:0.#} Hz",
        RsKnobUnit.Khz => $"{Value:0.##} kHz",
        RsKnobUnit.Ms => $"{Value:0} ms",
        RsKnobUnit.Enum => EnumIndex >= 0 && EnumIndex < EnumOptions.Count ? EnumOptions[EnumIndex] : $"{Value:0}",
        _ => $"{Value:0.##}"
    };

    public float ClampedValue => RsToneAuthoring.ClampKnobValue(Knob, (float)Value);

    partial void OnValueChanged(double value)
    {
        OnPropertyChanged(nameof(ValueText));
        OnPropertyChanged(nameof(EnumIndex));
        if (_initialized) _onChanged();
    }
}
