using DotCharter.Tone.Audio.Dsp.Tones;

namespace DotCharter.Services.Ai.Generate;

/// <summary>
///     The knob search's space for ONE Rocksmith tone: every continuous knob the tone sets, each mapped to <c>[0,1]</c>
///     over the catalogue's own range. The gear stays fixed and so does the knob set; enum knobs (a selector) stay where
///     the model put them. A point of the unit box decodes inside the catalogue's ranges BY CONSTRUCTION, snapped to the
///     knob's step, so nothing is clamped into range after the fact, and every decoded tone still goes through
///     <see cref="RsToneCatalogValidation.Require" />. An axis is a knob OF ONE SLOT: two instances of the same gear on a
///     rail share knob keys, so each axis also carries its gear's position in <see cref="RsTone.InSignalOrder" /> and the
///     two move independently.
/// </summary>
public sealed class RsToneKnobSpace
{
    private readonly RsGearCatalog _catalog;
    private readonly List<(int Slot, string Knob, RsGearKnob Spec)> _axes;

    private RsToneKnobSpace(RsGearCatalog catalog, List<(int Slot, string Knob, RsGearKnob Spec)> axes)
    {
        _catalog = catalog;
        _axes = axes;
    }

    /// <summary>How many knobs the search moves.</summary>
    public int Count => _axes.Count;

    /// <summary>The full knob keys, in axis order (a key appears once per slot that sets it).</summary>
    public IReadOnlyList<string> Keys => _axes.Select(a => a.Knob).ToList();

    /// <summary>The space of <paramref name="tone" />'s continuous knobs.</summary>
    public static RsToneKnobSpace Of(RsTone tone, RsGearCatalog catalog)
    {
        ArgumentNullException.ThrowIfNull(tone);
        ArgumentNullException.ThrowIfNull(catalog);
        var axes = new List<(int, string, RsGearKnob)>();
        var slot = 0;
        foreach (var gear in tone.InSignalOrder())
        {
            foreach (var key in gear.KnobValues.Keys.Order(StringComparer.Ordinal))
            {
                if (catalog.TryGetKnobByFullKey(key, out var spec) && spec.UnitType != RsKnobUnit.Enum && spec.MaxValue != spec.MinValue)
                    axes.Add((slot, key, spec));
            }

            slot++;
        }

        return new RsToneKnobSpace(catalog, axes);
    }

    /// <summary>Where <paramref name="tone" /> sits in the unit box.</summary>
    public double[] Encode(RsTone tone)
    {
        ArgumentNullException.ThrowIfNull(tone);
        var gears = tone.InSignalOrder().ToList();
        return _axes.Select(a =>
        {
            var (min, max) = Range(a.Spec);
            return a.Slot < gears.Count && gears[a.Slot].KnobValues.TryGetValue(a.Knob, out var v)
                ? Math.Clamp((v - min) / (max - min), 0.0, 1.0)
                : 0.5;
        }).ToArray();
    }

    /// <summary>
    ///     <paramref name="tone" /> with its continuous knobs at <paramref name="point" />: <c>min + u·(max − min)</c>, snapped
    ///     to the knob's step counted from its minimum. Validated against the catalogue before it is returned.
    /// </summary>
    public RsTone Decode(RsTone tone, IReadOnlyList<double> point)
    {
        ArgumentNullException.ThrowIfNull(tone);
        ArgumentNullException.ThrowIfNull(point);
        if (point.Count != _axes.Count) throw new ArgumentException($"expected {_axes.Count} values, got {point.Count}", nameof(point));
        var set = new Dictionary<(int Slot, string Knob), float>();
        for (var i = 0; i < _axes.Count; i++) set[(_axes[i].Slot, _axes[i].Knob)] = Value(_axes[i].Spec, point[i]);

        // The slots in InSignalOrder's order: pre-pedals, amp, cabinet, racks, post-pedals.
        var amp = tone.PrePedals.Count;
        var racks = amp + 2;
        var post = racks + tone.Racks.Count;
        var decoded = tone with
        {
            Amp = tone.Amp with { KnobValues = Moved(tone.Amp, amp, set) },
            Cabinet = tone.Cabinet with { KnobValues = Moved(tone.Cabinet, amp + 1, set) },
            PrePedals = tone.PrePedals.Select((p, i) => p with { KnobValues = Moved(p, i, set) }).ToList(),
            Racks = tone.Racks.Select((p, i) => p with { KnobValues = Moved(p, racks + i, set) }).ToList(),
            PostPedals = tone.PostPedals.Select((p, i) => p with { KnobValues = Moved(p, post + i, set) }).ToList()
        };
        return RsToneCatalogValidation.Require(decoded, _catalog, $"the knob search's '{tone.Name}'");
    }

    private static float Value(RsGearKnob spec, double unit)
    {
        var (min, max) = Range(spec);
        var raw = min + (Math.Clamp(unit, 0.0, 1.0) * (max - min));
        if (spec.ValueStep > 0) raw = min + (Math.Round((raw - min) / spec.ValueStep) * spec.ValueStep);
        // A step that does not divide the range rounds the top end past it: one step back keeps it inside. Within
        // floating-point noise of the end it IS the end.
        if (raw > max + 1e-6) raw -= spec.ValueStep;
        return (float)Math.Min(raw, max);
    }

    private static (double Min, double Max) Range(RsGearKnob spec) =>
        (Math.Min(spec.MinValue, spec.MaxValue), Math.Max(spec.MinValue, spec.MaxValue));

    private static IReadOnlyDictionary<string, float> Moved(RsGear gear, int slot, Dictionary<(int Slot, string Knob), float> set) =>
        gear.KnobValues.ToDictionary(kv => kv.Key, kv => set.TryGetValue((slot, kv.Key), out var v) ? v : kv.Value, StringComparer.Ordinal);
}
