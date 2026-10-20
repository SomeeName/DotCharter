using System.Globalization;
using System.Text.Json;
using DotCharter.Tone.Parsing.Psarc;
using DotCharter.Tone.Infrastructure.Json;

namespace DotCharter.Tone.Audio.Dsp.Tones;

/// <summary>
/// Turns the raw tone-JSON body that the package tone importer surfaces
/// (<see cref="PsarcToneDefinition.Json"/>) into a structured <see cref="RsTone"/>.
/// Pure, deterministic, zero I/O, and throw-free: odd/dirty data is tolerated
/// (missing slots dropped, unknown types → <see cref="RsGearType.Unknown"/>,
/// dirty volume cleaned). Uses <see cref="JsonDocument"/> rather than POCO
/// binding because slot keys are dynamic and knob values are int-or-float.
/// </summary>
public static class RsToneJsonParser
{
    /// <summary>The cabinet a tone falls back to when the <c>Cabinet</c> slot is
    /// absent — the RS game default (matches Rocksmith2014.NET behaviour).</summary>
    public const string DefaultCabinetKey = "Cab_Marshall1960TV_Ribbon_Cone";

    /// <summary>Synthetic amp key used when the <c>Amp</c> slot is missing, so
    /// downstream code always has a non-null amp to reason about.</summary>
    public const string SyntheticAmpKey = "Amp_Unknown";

    private static readonly string[] PreSlots = ["PrePedal1", "PrePedal2", "PrePedal3", "PrePedal4"];
    private static readonly string[] PostSlots = ["PostPedal1", "PostPedal2", "PostPedal3", "PostPedal4"];
    private static readonly string[] RackSlots = ["Rack1", "Rack2", "Rack3", "Rack4"];

    private static readonly JsonDocumentOptions DocOptions = new() { AllowTrailingCommas = true };

    /// <summary>
    /// Parses a tone JSON body into an <see cref="RsTone"/>. Returns false only
    /// when the input is not a JSON object or has no <c>GearList</c> object;
    /// everything inside is parsed leniently.
    /// </summary>
    public static bool TryParse(string? toneJson, out RsTone? tone)
    {
        tone = null;
        if (string.IsNullOrWhiteSpace(toneJson)) return false;

        try
        {
            using var doc = JsonDocument.Parse(toneJson, DocOptions);
            var root = doc.RootElement;
            if (root.ValueKind != JsonValueKind.Object) return false;
            if (!root.TryGetProperty("GearList", out var gearList) || gearList.ValueKind != JsonValueKind.Object)
                return false;

            tone = BuildTone(root, gearList, fallbackKey: null, fallbackName: null);
            return true;
        }
        catch (JsonException)
        {
            return false;
        }
    }

    /// <summary>
    /// Convenience bridge from the package tone importer: uses the
    /// definition's Key/Name as fallbacks for the root Key/Name. Throw-free —
    /// returns a minimal (amp+cab only) tone when the JSON is unparseable.
    /// </summary>
    public static RsTone Parse(PsarcToneDefinition def)
    {
        if (!string.IsNullOrWhiteSpace(def.Json))
        {
            try
            {
                using var doc = JsonDocument.Parse(def.Json, DocOptions);
                var root = doc.RootElement;
                if (root.ValueKind == JsonValueKind.Object &&
                    root.TryGetProperty("GearList", out var gearList) &&
                    gearList.ValueKind == JsonValueKind.Object)
                {
                    return BuildTone(root, gearList, def.Key, def.Name);
                }
            }
            catch (JsonException)
            {
                // fall through to the minimal tone
            }
        }

        return MinimalTone(def.Key, def.Name);
    }

    private static RsTone BuildTone(JsonElement root, JsonElement gearList, string? fallbackKey, string? fallbackName)
    {
        var key = JsonFields.StringOrEmpty(root, "Key");
        if (key.Length == 0) key = fallbackKey ?? "";
        var name = JsonFields.StringOrEmpty(root, "Name");
        if (name.Length == 0) name = fallbackName ?? key;

        var volume = ParseVolume(GetVolumeRaw(root));
        var descriptors = ReadDescriptors(root);

        var amp = ReadAmp(gearList);
        var cab = ReadCab(gearList);
        var pre = ReadPedalSlots(gearList, PreSlots, RsGearRole.PrePedal);
        var racks = ReadPedalSlots(gearList, RackSlots, RsGearRole.Rack);
        var post = ReadPedalSlots(gearList, PostSlots, RsGearRole.PostPedal);

        return new RsTone(key, name, volume, descriptors, amp, cab, pre, racks, post)
        {
            SortOrder = ReadSortOrder(root)
        };
    }

    private static RsTone MinimalTone(string key, string name) => new(
        key,
        string.IsNullOrEmpty(name) ? key : name,
        0f,
        Array.Empty<string>(),
        new RsAmp(SyntheticAmpKey, null, EmptyKnobs),
        new RsCab(DefaultCabinetKey, null, EmptyKnobs),
        Array.Empty<RsPedal>(),
        Array.Empty<RsPedal>(),
        Array.Empty<RsPedal>());

    private static readonly IReadOnlyDictionary<string, float> EmptyKnobs =
        new Dictionary<string, float>(StringComparer.Ordinal);

    private static RsAmp ReadAmp(JsonElement gearList)
    {
        if (gearList.TryGetProperty("Amp", out var obj) && obj.ValueKind == JsonValueKind.Object)
        {
            var (_, key, category, knobs) = ReadGear(obj);
            if (key.Length == 0) key = SyntheticAmpKey;
            return new RsAmp(key, category, knobs);
        }

        return new RsAmp(SyntheticAmpKey, null, EmptyKnobs);
    }

    private static RsCab ReadCab(JsonElement gearList)
    {
        if (gearList.TryGetProperty("Cabinet", out var obj) && obj.ValueKind == JsonValueKind.Object)
        {
            var (_, key, category, knobs) = ReadGear(obj);
            if (key.Length == 0) key = DefaultCabinetKey;
            return new RsCab(key, category, knobs);
        }

        return new RsCab(DefaultCabinetKey, null, EmptyKnobs);
    }

    private static IReadOnlyList<RsPedal> ReadPedalSlots(JsonElement gearList, string[] slots, RsGearRole role)
    {
        List<RsPedal>? result = null;
        for (var i = 0; i < slots.Length; i++)
        {
            if (!gearList.TryGetProperty(slots[i], out var obj) || obj.ValueKind != JsonValueKind.Object)
                continue;

            var (type, key, category, knobs) = ReadGear(obj);
            if (key.Length == 0) continue; // an empty slot key carries nothing usable

            (result ??= []).Add(new RsPedal(role, i + 1, key, type, category, knobs));
        }

        return (IReadOnlyList<RsPedal>?)result ?? Array.Empty<RsPedal>();
    }

    private static (RsGearType Type, string Key, string? Category, IReadOnlyDictionary<string, float> Knobs)
        ReadGear(JsonElement gearObj)
    {
        var type = ParseType(JsonFields.StringOrEmpty(gearObj, "Type"));
        var key = JsonFields.StringOrEmpty(gearObj, "Key");
        var category = gearObj.TryGetProperty("Category", out var catEl) &&
                       catEl.ValueKind == JsonValueKind.String
            ? catEl.GetString()
            : null;

        var knobs = ReadKnobValues(gearObj);
        return (type, key, category, knobs);
    }

    private static IReadOnlyDictionary<string, float> ReadKnobValues(JsonElement gearObj)
    {
        if (!gearObj.TryGetProperty("KnobValues", out var kv) || kv.ValueKind != JsonValueKind.Object)
            return EmptyKnobs;

        Dictionary<string, float>? map = null;
        foreach (var prop in kv.EnumerateObject())
        {
            if (prop.Value.ValueKind != JsonValueKind.Number) continue;
            // Tolerates both ints (360) and floats (0.5).
            if (!prop.Value.TryGetDouble(out var d)) continue;
            (map ??= new Dictionary<string, float>(StringComparer.Ordinal))[prop.Name] = (float)d;
        }

        return (IReadOnlyDictionary<string, float>?)map ?? EmptyKnobs;
    }

    private static IReadOnlyList<string> ReadDescriptors(JsonElement root)
    {
        if (!root.TryGetProperty("ToneDescriptors", out var arr) || arr.ValueKind != JsonValueKind.Array)
            return Array.Empty<string>();

        List<string>? result = null;
        foreach (var item in arr.EnumerateArray())
        {
            if (item.ValueKind == JsonValueKind.String)
                (result ??= []).Add(item.GetString() ?? "");
        }

        return (IReadOnlyList<string>?)result ?? Array.Empty<string>();
    }

    private static RsGearType ParseType(string type) => type switch
    {
        "Amps" => RsGearType.Amp,
        "Cabinets" => RsGearType.Cabinet,
        "Pedals" => RsGearType.Pedal,
        "Racks" => RsGearType.Rack,
        _ => RsGearType.Unknown
    };

    /// <summary>Reads the optional <c>SortOrder</c> field. Tolerates a number or
    /// a numeric string (some toolkit exports quote it); anything else → null.</summary>
    private static float? ReadSortOrder(JsonElement root)
    {
        if (!root.TryGetProperty("SortOrder", out var so)) return null;
        return so.ValueKind switch
        {
            JsonValueKind.Number when so.TryGetDouble(out var d) => (float)d,
            JsonValueKind.String when float.TryParse(so.GetString(), NumberStyles.Float,
                CultureInfo.InvariantCulture, out var f) => f,
            _ => null
        };
    }

    private static string? GetVolumeRaw(JsonElement root)
    {
        if (!root.TryGetProperty("Volume", out var v)) return null;
        return v.ValueKind switch
        {
            JsonValueKind.String => v.GetString(),
            JsonValueKind.Number => v.GetRawText(),
            _ => null
        };
    }

    /// <summary>
    /// Ports Rocksmith2014.NET's <c>volumeFromString</c>: normalise unicode
    /// dashes and decimal comma, strip everything except digits/dot/minus, parse
    /// invariant, round to one decimal. Returns 0 (unity) when absent/unparseable.
    /// Volume ships as a possibly-dirty STRING (commas, stray letters), never a
    /// clean number.
    /// </summary>
    public static float ParseVolume(string? raw)
    {
        if (string.IsNullOrWhiteSpace(raw)) return 0f;

        var buffer = raw.Length <= 64 ? stackalloc char[raw.Length] : new char[raw.Length];
        var n = 0;
        foreach (var ch0 in raw)
        {
            var ch = ch0 switch
            {
                ',' => '.',
                // normalise the common unicode hyphens/minus to ASCII '-'
                '‐' or '‑' or '‒' or '–' or '—' or '―' or '−' => '-',
                _ => ch0
            };
            if (ch is (>= '0' and <= '9') or '.' or '-')
                buffer[n++] = ch;
        }

        if (n == 0) return 0f;
        if (!float.TryParse(buffer[..n], NumberStyles.Float, CultureInfo.InvariantCulture, out var value))
            return 0f;

        return MathF.Round(value, 1, MidpointRounding.AwayFromZero);
    }
}
