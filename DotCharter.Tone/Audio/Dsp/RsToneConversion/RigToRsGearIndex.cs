using DotCharter.Tone.Audio.Dsp.Tones;

namespace DotCharter.Tone.Audio.Dsp.RsToneConversion;

/// <summary>One catalogue gear as the forward converter sees it: what it becomes at its
/// default knobs, and which of its knobs move anything (the rest fold and stay at default).</summary>
internal sealed record RigToRsGearEntry(RsGearData Gear, IReadOnlyList<DcRigModule> Output, IReadOnlyList<RsGearKnob> LandingKnobs);

/// <summary>
///     Every catalogue gear indexed by the module types its FORWARD conversion
///     (<see cref="RsToneConverter.ConvertGear" />) produces, so the inverse never keeps a
///     second mapping: a rig module is answered by the gear that already lands on it. Built
///     once per catalogue, override table and game-table set (the amps convert differently
///     with a Rocksmith install's tables).
/// </summary>
internal sealed class RigToRsGearIndex
{
    private static readonly object CacheGate = new();
    private static (RsGearCatalog Catalog, RsGearOverrides Overrides, RsGameTables Tables, RigToRsGearIndex Index)? _cache;

    private readonly Dictionary<string, List<RigToRsGearEntry>> _byFirstType = new(StringComparer.Ordinal);

    private RigToRsGearIndex(RsGearCatalog catalog, RsGearOverrides overrides)
    {
        Catalog = catalog;
        Overrides = overrides;
        foreach (var gear in catalog.AllGear.OrderBy(g => g.Key, StringComparer.Ordinal))
        {
            var output = Forward(gear, RsToneAuthoring.DefaultKnobValues(gear));
            if (output.Count == 0) continue;
            var entry = new RigToRsGearEntry(gear, output, LandingKnobs(gear));
            if (!_byFirstType.TryGetValue(output[0].ModuleTypeId, out var list)) _byFirstType[output[0].ModuleTypeId] = list = [];
            list.Add(entry);
        }
    }

    public RsGearCatalog Catalog { get; }

    public RsGearOverrides Overrides { get; }

    /// <summary>The index for this catalogue and override table under the current game tables.</summary>
    public static RigToRsGearIndex For(RsGearCatalog catalog, RsGearOverrides overrides)
    {
        var tables = RsGameTables.Current;
        lock (CacheGate)
        {
            if (_cache is { } c && ReferenceEquals(c.Catalog, catalog) && ReferenceEquals(c.Overrides, overrides) &&
                ReferenceEquals(c.Tables, tables))
            {
                return c.Index;
            }
        }

        var index = new RigToRsGearIndex(catalog, overrides);
        lock (CacheGate) _cache = (catalog, overrides, tables, index);
        return index;
    }

    /// <summary>Every gear whose conversion starts with <paramref name="moduleTypeId" />.</summary>
    public IReadOnlyList<RigToRsGearEntry> StartingWith(string moduleTypeId)
        => _byFirstType.TryGetValue(moduleTypeId, out var list) ? list : [];

    /// <summary>The forward conversion of <paramref name="gear" /> at <paramref name="knobs" />.</summary>
    public IReadOnlyList<DcRigModule> Forward(RsGearData gear, IReadOnlyDictionary<string, float> knobs)
        => RsToneConverter.ConvertGear(MakeGear(gear, knobs), Catalog, Overrides, new ConversionReport());

    /// <summary>The parsed-gear shape of a catalogue entry at the given knob values.</summary>
    public static RsGear MakeGear(RsGearData gear, IReadOnlyDictionary<string, float> knobs) => gear.Type switch
    {
        "Amps" => new RsAmp(gear.Key, gear.Category, knobs),
        "Cabinets" => new RsCab(gear.Key, gear.Category, knobs),
        "Racks" => new RsPedal(RsGearRole.Rack, 1, gear.Key, RsGearType.Rack, gear.Category, knobs),
        _ => new RsPedal(RsGearRole.PrePedal, 1, gear.Key, RsGearType.Pedal, gear.Category, knobs)
    };

    private List<RsGearKnob> LandingKnobs(RsGearData gear)
    {
        var landing = new List<RsGearKnob>();
        if (gear.Knobs is not { Count: > 0 } knobs) return landing;

        var defaults = RsToneAuthoring.DefaultKnobValues(gear);
        foreach (var knob in knobs)
        {
            var lo = new Dictionary<string, float>(defaults, StringComparer.Ordinal) { [knob.Key] = knob.MinValue };
            var hi = new Dictionary<string, float>(defaults, StringComparer.Ordinal) { [knob.Key] = knob.MaxValue };
            if (RigToRsKnobSearch.Distance(Forward(gear, lo), Forward(gear, hi)) > 0) landing.Add(knob);
        }

        return landing;
    }
}
