namespace DotCharter.Tone.Audio.Dsp.Tones;

/// <summary>Which structural family a piece of RS gear belongs to, taken from
/// the tone JSON <c>"Type"</c> field (Amps/Cabinets/Pedals/Racks). Anything the
/// parser cannot classify becomes <see cref="Unknown"/> (never an exception).</summary>
public enum RsGearType
{
    Amp,
    Cabinet,
    Pedal,
    Rack,
    Unknown
}

/// <summary>The slot role a gear item occupies in the signal chain. Chosen by
/// the slot KEY (Amp/Cabinet/PrePedalN/PostPedalN/RackN), not by JSON shape —
/// RS serializes every slot identically.</summary>
public enum RsGearRole
{
    Amp,
    Cabinet,
    PrePedal,
    PostPedal,
    Rack
}

/// <summary>
/// One parsed gear item from an RS tone's <c>GearList</c>. Knob values are keyed
/// by their FULL RS key (<c>"&lt;GearKey&gt;_&lt;Pot&gt;"</c>) and are in the RS
/// natural units (%, dB, Hz, ms, count) — NOT normalized. The Phase-1 converter
/// normalizes them against the catalogue range for the matching knob.
/// </summary>
public abstract record RsGear(
    string GearKey,
    RsGearType Type,
    string? Category,
    IReadOnlyDictionary<string, float> KnobValues)
{
    /// <summary>Looks up a knob by its single-token pot suffix by composing the
    /// full key <c>GearKey + "_" + potSuffix</c>. Case-sensitive (ordinal),
    /// matching the RS keys verbatim.</summary>
    public bool TryGetKnob(string potSuffix, out float value)
        => KnobValues.TryGetValue(GearKey + "_" + potSuffix, out value);

    /// <summary>Enumerates (pot-suffix, value) pairs, stripping the
    /// <c>GearKey_</c> prefix via the last <c>'_'</c>. Robust to a leading
    /// <c>"Bass_"</c> prefix on the gear key because pot suffixes are single
    /// tokens.</summary>
    public IEnumerable<(string Pot, float Value)> Pots()
    {
        foreach (var kv in KnobValues)
        {
            var idx = kv.Key.LastIndexOf('_');
            var pot = idx >= 0 && idx < kv.Key.Length - 1 ? kv.Key[(idx + 1)..] : kv.Key;
            yield return (pot, kv.Value);
        }
    }
}

/// <summary>The amplifier slot. Always present on a parsed tone (synthetic when
/// the JSON omits it).</summary>
public sealed record RsAmp(
    string GearKey,
    string? Category,
    IReadOnlyDictionary<string, float> KnobValues)
    : RsGear(GearKey, RsGearType.Amp, Category, KnobValues);

/// <summary>The cabinet slot. Always present (defaults to
/// <see cref="RsToneJsonParser.DefaultCabinetKey"/> when absent). Its
/// <see cref="RsGear.KnobValues"/> are usually empty — normal, not an error.</summary>
public sealed record RsCab(
    string GearKey,
    string? Category,
    IReadOnlyDictionary<string, float> KnobValues)
    : RsGear(GearKey, RsGearType.Cabinet, Category, KnobValues);

/// <summary>A pedal or rack slot. <see cref="Role"/> distinguishes
/// pre/post/rack; <see cref="SlotIndex"/> is the 1..4 slot number (for the
/// conversion report / logging).</summary>
public sealed record RsPedal(
    RsGearRole Role,
    int SlotIndex,
    string GearKey,
    RsGearType Type,
    string? Category,
    IReadOnlyDictionary<string, float> KnobValues)
    : RsGear(GearKey, Type, Category, KnobValues);

/// <summary>
/// A fully parsed Rocksmith in-game tone: its identity, cleaned volume, and the
/// gear in every occupied slot. Amp and Cabinet are mandatory (never null);
/// pedal/rack lists carry only the present slots in ascending slot order.
/// </summary>
public sealed record RsTone(
    string Key,
    string Name,
    float VolumeDb,
    IReadOnlyList<string> ToneDescriptors,
    RsAmp Amp,
    RsCab Cabinet,
    IReadOnlyList<RsPedal> PrePedals,
    IReadOnlyList<RsPedal> Racks,
    IReadOnlyList<RsPedal> PostPedals)
{
    /// <summary>The manifest <c>SortOrder</c> (in-game tone-list ordering).
    /// Null when the source body carried none — <see cref="RsToneJsonWriter"/>
    /// then omits the field, matching Rocksmith2014.NET's nullable DTO field.
    /// Kept out of the primary constructor so existing call sites stay valid.</summary>
    public float? SortOrder { get; init; }

    /// <summary>Yields the gear in RS §4.3 routing order:
    /// PrePedal1..4 → Amp → Cabinet → Rack1..4 → PostPedal1..4.</summary>
    public IEnumerable<RsGear> InSignalOrder()
    {
        foreach (var p in PrePedals) yield return p;
        yield return Amp;
        yield return Cabinet;
        foreach (var r in Racks) yield return r;
        foreach (var p in PostPedals) yield return p;
    }
}
