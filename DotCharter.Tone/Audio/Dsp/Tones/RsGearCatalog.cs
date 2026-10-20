using System.Diagnostics;
using System.Diagnostics.CodeAnalysis;
using System.Reflection;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace DotCharter.Tone.Audio.Dsp.Tones;

/// <summary>The declared unit of an RS gear knob (from <c>UnitType</c> in
/// tone_gear_data.json). "%" is not a valid enum identifier, so parsing goes
/// through <see cref="RsKnobUnitParser"/> rather than a string-enum converter.</summary>
public enum RsKnobUnit
{
    Number,
    Percent,
    Db,
    Hz,
    Khz,
    Ms,
    Enum,
    Unknown
}

internal static class RsKnobUnitParser
{
    public static RsKnobUnit From(string? unitType) => unitType switch
    {
        "number" => RsKnobUnit.Number,
        "%" => RsKnobUnit.Percent,
        "db" => RsKnobUnit.Db,
        "hz" => RsKnobUnit.Hz,
        "khz" => RsKnobUnit.Khz,
        "ms" => RsKnobUnit.Ms,
        "enum" => RsKnobUnit.Enum,
        _ => RsKnobUnit.Unknown
    };
}

/// <summary>One knob of a gear item, with its declared unit and RAW range in the
/// DECLARED unit (khz min/max stay in kHz — the Phase-1 converter multiplies by
/// 1000 when feeding an Hz param).</summary>
public sealed record RsGearKnob(
    string Name,
    string Key,
    RsKnobUnit UnitType,
    float MinValue,
    float MaxValue,
    float ValueStep,
    float DefaultValue,
    IReadOnlyList<string>? EnumValues)
{
    /// <summary>The pot suffix (token after the last <c>'_'</c>).</summary>
    public string Pot => Key.LastIndexOf('_') is var i && i >= 0 && i < Key.Length - 1
        ? Key[(i + 1)..]
        : Key;
}

/// <summary>One catalogue gear entry. Cabinets have a null <see cref="Knobs"/>
/// array.</summary>
public sealed record RsGearData(
    string Name,
    string Type,
    string Category,
    string Key,
    IReadOnlyList<RsGearKnob>? Knobs);

// --- deserialization DTOs (UnitType stays a raw string; mapped after load) ---

internal sealed class GearKnobDto
{
    public string Name { get; set; } = "";
    public string Key { get; set; } = "";
    public string UnitType { get; set; } = "";
    public float MinValue { get; set; }
    public float MaxValue { get; set; }
    public float ValueStep { get; set; }
    public float DefaultValue { get; set; }
    public string[]? EnumValues { get; set; }
}

internal sealed class GearDataDto
{
    public string Name { get; set; } = "";
    public string Type { get; set; } = "";
    public string Category { get; set; } = "";
    public string Key { get; set; } = "";
    public GearKnobDto[]? Knobs { get; set; }
}

[JsonSourceGenerationOptions(PropertyNameCaseInsensitive = false)]
[JsonSerializable(typeof(GearDataDto[]))]
internal partial class RsGearJsonContext : JsonSerializerContext;

/// <summary>
/// The embedded snapshot of Rocksmith's <c>tone_gear_data.json</c> (63 amps /
/// 450 cabinets / 83 pedals / 17 racks), indexed for the Phase-1 converter to
/// resolve each RS knob's UnitType/Min/Max/Default and each gear's authoritative
/// routing category. Immutable static data, loaded once.
/// </summary>
public sealed class RsGearCatalog
{
    // Expected counts by Type — a load-time sanity check that the embedded
    // snapshot is the full file, not a truncated copy.
    internal const int ExpectedAmps = 63;
    internal const int ExpectedCabinets = 450;
    internal const int ExpectedPedals = 83;
    internal const int ExpectedRacks = 17;

    private const string ResourceName = "DotCharter.Tone.Audio.Dsp.Tones.tone_gear_data.json";

    private static readonly Lazy<RsGearCatalog> SharedLazy = new(Load);

    private readonly Dictionary<string, RsGearData> _byKey;
    private readonly Dictionary<string, RsGearKnob> _byFullKnobKey;
    private readonly Dictionary<RsGearType, int> _countsByType;

    private RsGearCatalog(
        Dictionary<string, RsGearData> byKey,
        Dictionary<string, RsGearKnob> byFullKnobKey,
        Dictionary<RsGearType, int> countsByType)
    {
        _byKey = byKey;
        _byFullKnobKey = byFullKnobKey;
        _countsByType = countsByType;
    }

    /// <summary>The process-wide catalogue (load-once, immutable).</summary>
    public static RsGearCatalog Shared => SharedLazy.Value;

    /// <summary>Number of gear entries of the given type (Amp/Cabinet/Pedal/Rack).</summary>
    public int Count(RsGearType type) => _countsByType.GetValueOrDefault(type);

    /// <summary>Every gear entry in the catalogue — the test seam for the
    /// rs_gear_overrides liveness/coverage gate.</summary>
    internal IEnumerable<RsGearData> AllGear => _byKey.Values;

    public bool TryGetGear(string gearKey, [MaybeNullWhen(false)] out RsGearData gear)
        => _byKey.TryGetValue(gearKey, out gear);

    /// <summary>The AUTHORITATIVE routing category for a gear key (the tone
    /// JSON's own Category is not trusted — it is sometimes a loc-id). Null when
    /// the key is unknown.</summary>
    public string? GetCategory(string gearKey)
        => _byKey.TryGetValue(gearKey, out var gear) ? gear.Category : null;

    /// <summary>Resolves a knob by gear key + single-token pot suffix.</summary>
    public bool TryGetKnob(string gearKey, string potSuffix, [MaybeNullWhen(false)] out RsGearKnob knob)
        => _byFullKnobKey.TryGetValue(gearKey + "_" + potSuffix, out knob);

    /// <summary>Resolves a knob directly from a tone <c>KnobValues</c> key (the
    /// full <c>&lt;GearKey&gt;_&lt;Pot&gt;</c> string) in one hop.</summary>
    public bool TryGetKnobByFullKey(string fullKnobKey, [MaybeNullWhen(false)] out RsGearKnob knob)
        => _byFullKnobKey.TryGetValue(fullKnobKey, out knob);

    /// <summary>An empty catalogue used as the degraded fallback so a missing
    /// or unreadable embedded resource never throws through <see cref="Shared"/>
    /// (the converter's "never throws" contract): every lookup misses and gear
    /// is reported as unmapped rather than crashing the tone conversion.</summary>
    public static RsGearCatalog Empty { get; } = new(
        new Dictionary<string, RsGearData>(StringComparer.Ordinal),
        new Dictionary<string, RsGearKnob>(StringComparer.Ordinal),
        []);

    private static RsGearCatalog Load()
    {
        // Degraded, not fatal: a missing/corrupt resource returns the Empty
        // catalogue. A default Lazy caches this factory's result once, so we
        // must not let it throw (a cached exception would re-throw on every
        // access for the process lifetime — the confirmed audit finding).
        try
        {
            return LoadCore();
        }
        catch
        {
            return Empty;
        }
    }

    private static RsGearCatalog LoadCore()
    {
        using var stream = Assembly.GetExecutingAssembly().GetManifestResourceStream(ResourceName);
        if (stream == null) return Empty;

        var dtos = JsonSerializer.Deserialize(stream, RsGearJsonContext.Default.GearDataDtoArray)
                   ?? [];

        var byKey = new Dictionary<string, RsGearData>(dtos.Length, StringComparer.Ordinal);
        var byFullKnobKey = new Dictionary<string, RsGearKnob>(StringComparer.Ordinal);
        var counts = new Dictionary<RsGearType, int>();

        foreach (var dto in dtos)
        {
            IReadOnlyList<RsGearKnob>? knobs = null;
            if (dto.Knobs is { Length: > 0 })
            {
                var list = new List<RsGearKnob>(dto.Knobs.Length);
                foreach (var k in dto.Knobs)
                {
                    var knob = new RsGearKnob(
                        k.Name,
                        k.Key,
                        RsKnobUnitParser.From(k.UnitType),
                        k.MinValue,
                        k.MaxValue,
                        k.ValueStep,
                        k.DefaultValue,
                        k.EnumValues);
                    list.Add(knob);
                    byFullKnobKey[k.Key] = knob; // index every knob by its own full key
                }

                knobs = list;
            }

            var gear = new RsGearData(dto.Name, dto.Type, dto.Category, dto.Key, knobs);
            if (dto.Key.Length != 0) byKey[dto.Key] = gear;

            var type = dto.Type switch
            {
                "Amps" => RsGearType.Amp,
                "Cabinets" => RsGearType.Cabinet,
                "Pedals" => RsGearType.Pedal,
                "Racks" => RsGearType.Rack,
                _ => RsGearType.Unknown
            };
            counts[type] = counts.GetValueOrDefault(type) + 1;
        }

        Debug.Assert(counts.GetValueOrDefault(RsGearType.Amp) == ExpectedAmps, "amp count drift");
        Debug.Assert(counts.GetValueOrDefault(RsGearType.Cabinet) == ExpectedCabinets, "cabinet count drift");
        Debug.Assert(counts.GetValueOrDefault(RsGearType.Pedal) == ExpectedPedals, "pedal count drift");
        Debug.Assert(counts.GetValueOrDefault(RsGearType.Rack) == ExpectedRacks, "rack count drift");

        return new RsGearCatalog(byKey, byFullKnobKey, counts);
    }
}
