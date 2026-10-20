using System.Diagnostics.CodeAnalysis;
using System.Reflection;
using System.Text.Json;

namespace DotCharter.Tone.Audio.Dsp.RsToneConversion;

/// <summary>
/// Small, curated per-gear "character" table the generic catalogue-driven
/// converter can't derive: amp voicing, cab model family, the overdrive-vs-
/// distortion-vs-fuzz routing (<c>targetModule</c>), the compressor-vs-sustainer
/// split (<c>sustainerKeys</c>), and static param pins. Every key is a REAL RS
/// gear catalogue key (liveness + full amp/cab coverage are enforced by
/// <c>RsGearOverridesLivenessTests</c> and <c>RsGearOverridesTests</c>). Loaded
/// once from the embedded <c>rs_gear_overrides.json</c>; never throws
/// (missing/garbled resource → an empty override set, so conversion stays fully
/// generic).
/// </summary>
public sealed class RsGearOverrides
{
    private const string ResourceName = "DotCharter.Tone.Audio.Dsp.RsToneConversion.rs_gear_overrides.json";

    private static readonly Lazy<RsGearOverrides> SharedLazy = new(Load);

    private readonly List<KeyValuePair<string, int>> _cabModelByStem; // prefix match, longest wins
    private readonly HashSet<string> _distortionKeys;
    private readonly HashSet<string> _overdriveKeys;
    private readonly Dictionary<string, string> _targetModule;
    private readonly HashSet<string> _sustainerKeys;
    private readonly Dictionary<string, Dictionary<string, float>> _staticParams;

    private RsGearOverrides(
        List<KeyValuePair<string, int>> cabModelByStem,
        HashSet<string> distortionKeys,
        HashSet<string> overdriveKeys,
        Dictionary<string, string> targetModule,
        HashSet<string> sustainerKeys,
        Dictionary<string, Dictionary<string, float>> staticParams)
    {
        _cabModelByStem = cabModelByStem;
        _distortionKeys = distortionKeys;
        _overdriveKeys = overdriveKeys;
        _targetModule = targetModule;
        _sustainerKeys = sustainerKeys;
        _staticParams = staticParams;
    }

    public static RsGearOverrides Shared => SharedLazy.Value;

    /// <summary>An empty override set (for tests / when overrides should not apply).</summary>
    public static RsGearOverrides Empty { get; } = new(
        [],
        new HashSet<string>(StringComparer.Ordinal),
        new HashSet<string>(StringComparer.Ordinal),
        new Dictionary<string, string>(StringComparer.Ordinal),
        new HashSet<string>(StringComparer.Ordinal),
        new Dictionary<string, Dictionary<string, float>>(StringComparer.Ordinal));

    /// <summary>Longest-prefix match of the cab key against the family stems.</summary>
    public bool TryGetCabModel(string cabKey, out int model)
    {
        model = 0;
        var bestLen = -1;
        foreach (var kv in _cabModelByStem)
        {
            if (kv.Key.Length > bestLen && cabKey.StartsWith(kv.Key, StringComparison.Ordinal))
            {
                bestLen = kv.Key.Length;
                model = kv.Value;
            }
        }

        return bestLen >= 0;
    }

    public bool IsForcedDistortion(string gearKey) => _distortionKeys.Contains(gearKey);
    public bool IsForcedOverdrive(string gearKey) => _overdriveKeys.Contains(gearKey);

    /// <summary>Explicit drive-module routing (e.g. fuzz-face-likes → <c>fuzz</c>,
    /// muff-likes → <c>bigmuff</c>) for gear neither generic drive module voices well.</summary>
    public bool TryGetTargetModule(string gearKey, [MaybeNullWhen(false)] out string moduleTypeId)
        => _targetModule.TryGetValue(gearKey, out moduleTypeId);

    /// <summary>Dynamics gear steered onto the sustainer module (squishy
    /// compressor-sustainer pedals the key heuristic can't recognize).</summary>
    public bool IsForcedSustainer(string gearKey) => _sustainerKeys.Contains(gearKey);

    public IReadOnlyDictionary<string, float>? GetStaticParams(string gearKey)
        => _staticParams.TryGetValue(gearKey, out var p) ? p : null;

    // --- test seams (the RsGearOverridesLivenessTests liveness/coverage gate
    // and RsGearOverridesTests catalogue-resolution gate) ---

    internal IReadOnlyList<KeyValuePair<string, int>> CabModelStems => _cabModelByStem;
    internal IReadOnlyCollection<string> ForcedDistortionKeys => _distortionKeys;
    internal IReadOnlyCollection<string> ForcedOverdriveKeys => _overdriveKeys;
    internal IReadOnlyDictionary<string, string> TargetModuleMap => _targetModule;
    internal IReadOnlyCollection<string> SustainerKeys => _sustainerKeys;
    internal IEnumerable<string> StaticParamGearKeys => _staticParams.Keys;

    private static RsGearOverrides Load()
    {
        try
        {
            using var stream = Assembly.GetExecutingAssembly().GetManifestResourceStream(ResourceName);
            if (stream is null) return Empty;

            using var doc = JsonDocument.Parse(stream, new JsonDocumentOptions { AllowTrailingCommas = true });
            var root = doc.RootElement;
            if (root.ValueKind != JsonValueKind.Object) return Empty;

            return new RsGearOverrides(
                ReadStemList(root, "cabModel"),
                ReadStringSet(root, "distortionKeys"),
                ReadStringSet(root, "overdriveKeys"),
                ReadStringMap(root, "targetModule"),
                ReadStringSet(root, "sustainerKeys"),
                ReadStaticParams(root));
        }
        catch (JsonException)
        {
            return Empty;
        }
    }

    private static List<KeyValuePair<string, int>> ReadStemList(JsonElement root, string name)
    {
        var list = new List<KeyValuePair<string, int>>();
        if (root.TryGetProperty(name, out var obj) && obj.ValueKind == JsonValueKind.Object)
        {
            foreach (var p in obj.EnumerateObject())
            {
                if (p.Value.ValueKind == JsonValueKind.Number && p.Value.TryGetInt32(out var v))
                    list.Add(new KeyValuePair<string, int>(p.Name, v));
            }
        }

        return list;
    }

    private static Dictionary<string, string> ReadStringMap(JsonElement root, string name)
    {
        // Unlike the int maps (where a "_comment" string value is naturally
        // skipped by the value-type check), string values ARE the payload here —
        // skip '_'-prefixed property names explicitly so comments stay possible.
        var map = new Dictionary<string, string>(StringComparer.Ordinal);
        if (root.TryGetProperty(name, out var obj) && obj.ValueKind == JsonValueKind.Object)
        {
            foreach (var p in obj.EnumerateObject())
            {
                if (!p.Name.StartsWith('_') &&
                    p.Value.ValueKind == JsonValueKind.String &&
                    p.Value.GetString() is { Length: > 0 } s)
                {
                    map[p.Name] = s;
                }
            }
        }

        return map;
    }

    private static HashSet<string> ReadStringSet(JsonElement root, string name)
    {
        var set = new HashSet<string>(StringComparer.Ordinal);
        if (root.TryGetProperty(name, out var arr) && arr.ValueKind == JsonValueKind.Array)
        {
            foreach (var item in arr.EnumerateArray())
            {
                if (item.ValueKind == JsonValueKind.String && item.GetString() is { Length: > 0 } s)
                    set.Add(s);
            }
        }

        return set;
    }

    private static Dictionary<string, Dictionary<string, float>> ReadStaticParams(JsonElement root)
    {
        var result = new Dictionary<string, Dictionary<string, float>>(StringComparer.Ordinal);
        if (!root.TryGetProperty("staticParams", out var obj) || obj.ValueKind != JsonValueKind.Object)
            return result;

        foreach (var gear in obj.EnumerateObject())
        {
            if (gear.Value.ValueKind != JsonValueKind.Object) continue;
            Dictionary<string, float>? pins = null;
            foreach (var p in gear.Value.EnumerateObject())
            {
                if (p.Value.ValueKind == JsonValueKind.Number && p.Value.TryGetDouble(out var d))
                    (pins ??= new Dictionary<string, float>(StringComparer.Ordinal))[p.Name] = (float)d;
            }

            if (pins is not null) result[gear.Name] = pins;
        }

        return result;
    }
}
