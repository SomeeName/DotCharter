using CommunityToolkit.Mvvm.ComponentModel;
using DotCharter.Localization;
using DotCharter.Presentation.Contracts.Dialogs;
using DotCharter.Tone.Audio.Dsp.Tones;

namespace DotCharter.Presentation.Screens.Dialogs;

/// <summary>
///     The Tone designer's editor over the selected tone: key, name, volume, three
///     descriptors and the fourteen gear slots. It buffers; the dialog commits.
///     <see cref="Changed" /> fires for a user edit, never for a load.
/// </summary>
public sealed partial class ToneEditorViewModel : ObservableObject, IToneEditor
{
    private readonly ILocalizationService _loc;
    private readonly Func<IReadOnlyList<ProjectToneItem>> _tones;
    private readonly Lazy<IReadOnlyList<ToneGearSlot>> _gearSlots;
    private bool _loading;

    [ObservableProperty] private string _editKey = "";
    [ObservableProperty] private string _editName = "";
    [ObservableProperty][NotifyPropertyChangedFor(nameof(HasKeyError))] private string _keyError = "";
    [ObservableProperty] private double _volumeDb = RsToneAuthoring.DefaultVolumeDb;
    [ObservableProperty] private ToneDescriptorChoice _descriptor1;
    [ObservableProperty] private ToneDescriptorChoice _descriptor2;
    [ObservableProperty] private ToneDescriptorChoice _descriptor3;
    [ObservableProperty] private ToneGearSlot? _selectedGearSlot;

    public ToneEditorViewModel(ILocalizationService loc, Func<IReadOnlyList<ProjectToneItem>> tones)
    {
        _loc = loc;
        _tones = tones;
        None = new ToneDescriptorChoice(new ToneDescriptorOption(loc["Dialog_Tones_DescriptorNone"], ""));
        DescriptorOptions = [None, .. RsToneAuthoring.Descriptors.Select(d => new ToneDescriptorChoice(d))];
        _descriptor1 = _descriptor2 = _descriptor3 = None;
        _gearSlots = new Lazy<IReadOnlyList<ToneGearSlot>>(BuildGearSlots);
    }

    public event EventHandler? Changed;

    /// <summary>The tone the editor holds; null with none selected.</summary>
    public ProjectToneItem? Loaded { get; private set; }

    /// <summary>A user edit since the last load or commit.</summary>
    public bool IsDirty { get; set; }

    public ToneDescriptorChoice None { get; }

    public IReadOnlyList<ToneDescriptorChoice> DescriptorOptions { get; }

    /// <summary>Built on FIRST READ: the gear catalogue is a 144 KB deserialize most sessions never need.</summary>
    public IReadOnlyList<ToneGearSlot> GearSlots => _gearSlots.Value;

    internal bool GearSlotsAreBuilt => _gearSlots.IsValueCreated;

    public bool HasKeyError => KeyError.Length > 0;

    public double VolumeMin => RsToneAuthoring.MinVolumeDb;

    public double VolumeMax => RsToneAuthoring.MaxVolumeDb;

    /// <summary>
    ///     DLC Builder's way of showing the Volume: the attenuation as a POSITIVE number (12.0 for
    ///     -12 dB). The stored value stays <see cref="VolumeDb" />; this is the same buffered edit
    ///     seen from the other sign, so it flushes as the one history action it always did.
    /// </summary>
    public double VolumeAttenuation
    {
        get => Math.Round(-VolumeDb, 1);
        set => VolumeDb = -Math.Clamp(value, VolumeAttenuationMin, VolumeAttenuationMax);
    }

    public double VolumeAttenuationMin => Math.Round(-VolumeMax, 1);

    public double VolumeAttenuationMax => Math.Round(-VolumeMin, 1);

    IReadOnlyList<IToneChoice> IToneEditor.DescriptorOptions => DescriptorOptions;
    IReadOnlyList<IToneGearSlot> IToneEditor.GearSlots => GearSlots;

    IToneChoice IToneEditor.Descriptor1 { get => Descriptor1; set => Descriptor1 = value as ToneDescriptorChoice ?? None; }
    IToneChoice IToneEditor.Descriptor2 { get => Descriptor2; set => Descriptor2 = value as ToneDescriptorChoice ?? None; }
    IToneChoice IToneEditor.Descriptor3 { get => Descriptor3; set => Descriptor3 = value as ToneDescriptorChoice ?? None; }

    IToneGearSlot? IToneEditor.SelectedGearSlot
    {
        get => SelectedGearSlot;
        set => SelectedGearSlot = value as ToneGearSlot;
    }

    /// <summary>Loads a tone into the fields without marking anything dirty; null clears the editor.</summary>
    public void Load(ProjectToneItem? item)
    {
        Loaded = item;
        IsDirty = false;
        if (item is null)
        {
            KeyError = "";
            return;
        }

        var tone = RsToneJsonParser.TryParse(item.Json, out var parsed) && parsed is not null
            ? parsed with { Key = item.Key, Name = item.Name }
            : RsToneAuthoring.CreateDefaultTone(item.Key, item.Name, RsGearCatalog.Shared);
        _loading = true;
        try
        {
            EditKey = tone.Key;
            EditName = tone.Name;
            VolumeDb = Math.Round(RsToneAuthoring.ClampVolumeDb(tone.VolumeDb), 1);
            Descriptor1 = DescriptorAt(tone.ToneDescriptors, 0);
            Descriptor2 = DescriptorAt(tone.ToneDescriptors, 1);
            Descriptor3 = DescriptorAt(tone.ToneDescriptors, 2);
            GearSlots[0].Load(tone.Amp);
            GearSlots[1].Load(tone.Cabinet);
            LoadPedals(2, tone.PrePedals);
            LoadPedals(6, tone.PostPedals);
            LoadPedals(10, tone.Racks);
            SelectedGearSlot = GearSlots[0];
        }
        finally
        {
            _loading = false;
        }

        ValidateKey();
    }

    /// <summary>The edited key when valid and unique, else the committed one: invalid input never lands.</summary>
    public string EffectiveKey()
    {
        var key = EditKey.Trim();
        return RsToneAuthoring.IsValidKey(key) && !IsTakenByAnother(key) ? key : Loaded?.Key ?? key;
    }

    /// <summary>Shows <paramref name="key" /> without it counting as an edit: the committed key after an invalid one reverted.</summary>
    public void ShowKey(string key)
    {
        _loading = true;
        EditKey = key;
        _loading = false;
        ValidateKey();
    }

    public void ValidateKey()
    {
        var key = EditKey.Trim();
        KeyError = !RsToneAuthoring.IsValidKey(key) ? _loc["Dialog_Tones_KeyInvalid"]
            : IsTakenByAnother(key) ? _loc["Dialog_Tones_KeyDuplicate"]
            : "";
    }

    /// <summary>The edited tone, pure: the commit and the preview both read it. Null-tolerant, as pickers write null in passing.</summary>
    public RsTone BuildTone(string key)
    {
        var name = EditName.Trim();
        if (name.Length == 0) name = key;
        var descriptors = new List<string>(3);
        foreach (var d in new[] { Descriptor1, Descriptor2, Descriptor3 })
        {
            if (d?.Option.UIName is { Length: > 0 } uiName && !descriptors.Contains(uiName, StringComparer.Ordinal))
                descriptors.Add(uiName);
        }

        if (descriptors.Count == 0) descriptors.Add(RsToneAuthoring.CleanDescriptorUIName);

        var ampSlot = GearSlots[0];
        var amp = ampSlot.SelectedOption?.Gear is { } ampGear
            ? new RsAmp(ampGear.Key, ampGear.Category, ampSlot.BuildKnobValues())
            : new RsAmp(RsToneJsonParser.SyntheticAmpKey, null, new Dictionary<string, float>(StringComparer.Ordinal));
        var cabSlot = GearSlots[1];
        var cab = cabSlot.SelectedOption?.Gear is { } cabGear
            ? new RsCab(cabGear.Key, cabGear.Category, cabSlot.BuildKnobValues())
            : new RsCab(RsToneJsonParser.DefaultCabinetKey, null, new Dictionary<string, float>(StringComparer.Ordinal));
        return new RsTone(key, name, RsToneAuthoring.ClampVolumeDb((float)VolumeDb), descriptors, amp, cab,
            Pedals(2), Pedals(10), Pedals(6));
    }

    partial void OnEditKeyChanged(string value)
    {
        if (_loading) return;
        ValidateKey();
        MarkChanged();
    }

    partial void OnEditNameChanged(string value) => MarkChanged();
    partial void OnVolumeDbChanged(double value)
    {
        OnPropertyChanged(nameof(VolumeAttenuation));
        MarkChanged();
    }
    partial void OnDescriptor1Changed(ToneDescriptorChoice value) => MarkChanged();
    partial void OnDescriptor2Changed(ToneDescriptorChoice value) => MarkChanged();
    partial void OnDescriptor3Changed(ToneDescriptorChoice value) => MarkChanged();

    private void MarkChanged()
    {
        if (_loading || Loaded is null) return;
        IsDirty = true;
        Changed?.Invoke(this, EventArgs.Empty);
    }

    private bool IsTakenByAnother(string key) =>
        _tones().Any(t => !ReferenceEquals(t, Loaded) && string.Equals(t.Key.Trim(), key, StringComparison.OrdinalIgnoreCase));

    private ToneDescriptorChoice DescriptorAt(IReadOnlyList<string> descriptors, int index) =>
        index < descriptors.Count && RsToneAuthoring.FindDescriptor(descriptors[index]) is { } found
            ? DescriptorOptions.FirstOrDefault(d => d.Option == found) ?? None
            : None;

    private void LoadPedals(int first, IReadOnlyList<RsPedal> pedals)
    {
        for (var i = 0; i < 4; i++) GearSlots[first + i].Load(pedals.FirstOrDefault(p => p.SlotIndex == i + 1));
    }

    private List<RsPedal> Pedals(int first)
    {
        var pedals = new List<RsPedal>(4);
        for (var i = 0; i < 4; i++)
        {
            if (GearSlots[first + i].BuildPedal() is { } pedal)
                pedals.Add(pedal);
        }

        return pedals;
    }

    private IReadOnlyList<ToneGearSlot> BuildGearSlots()
    {
        var catalog = RsGearCatalog.Shared;
        var empty = _loc["Dialog_Tones_GearNone"];

        static List<ToneGearOption> Options(IReadOnlyList<RsGearData> gear, string? noneLabel)
        {
            var options = new List<ToneGearOption>(gear.Count + 1);
            if (noneLabel != null) options.Add(new ToneGearOption(null, noneLabel));
            options.AddRange(gear.Select(g => new ToneGearOption(g, g.Name)));
            return options;
        }

        // The amp and the cabinet are mandatory in Rocksmith: no "(empty)" option.
        var amps = Options(RsToneAuthoring.GearOptions(catalog, RsGearType.Amp), null);
        var cabs = Options(RsToneAuthoring.GearOptions(catalog, RsGearType.Cabinet), null);
        var pedals = Options(RsToneAuthoring.GearOptions(catalog, RsGearType.Pedal), empty);
        var racks = Options(RsToneAuthoring.GearOptions(catalog, RsGearType.Rack), empty);
        if (amps.Count == 0) amps.Add(new ToneGearOption(null, empty));
        if (cabs.Count == 0) cabs.Add(new ToneGearOption(null, empty));

        var slots = new List<ToneGearSlot>(14)
        {
            new(_loc["Dialog_Tones_SlotAmp"], RsGearRole.Amp, 0, amps, MarkChanged),
            new(_loc["Dialog_Tones_SlotCabinet"], RsGearRole.Cabinet, 0, cabs, MarkChanged)
        };
        for (var i = 1; i <= 4; i++) slots.Add(new(_loc.Format("Dialog_Tones_SlotPre", i), RsGearRole.PrePedal, i, pedals, MarkChanged));
        for (var i = 1; i <= 4; i++) slots.Add(new(_loc.Format("Dialog_Tones_SlotLoop", i), RsGearRole.PostPedal, i, pedals, MarkChanged));
        for (var i = 1; i <= 4; i++) slots.Add(new(_loc.Format("Dialog_Tones_SlotRack", i), RsGearRole.Rack, i, racks, MarkChanged));
        return slots;
    }
}
