using DotCharter.Tone.Audio.Dsp.RsToneConversion;

namespace DotCharter.Services.Ai.Match.Search;

/// <summary>One searchable knob: where it sits in the chain, and how its value
/// maps onto <c>[0,1]</c>. Immutable; produced by <see cref="KnobLayout" />.</summary>
public sealed record KnobCoordinate(
    int ModuleIndex,
    string ModuleTypeId,
    string ParamId,
    float Min,
    float Max,
    bool Logarithmic)
{
    /// <summary>The param value as a normalised coordinate, clamped to
    /// <c>[0,1]</c>.</summary>
    public float ToUnit(float value)
    {
        if (Max <= Min) return 0f;
        if (Logarithmic)
        {
            var v = Math.Clamp(value, Min, Max);
            return (float)Math.Clamp(Math.Log(v / Min) / Math.Log(Max / (double)Min), 0.0, 1.0);
        }

        return (float)Math.Clamp((value - Min) / (double)(Max - Min), 0.0, 1.0);
    }

    /// <summary>The value for a normalised coordinate; the coordinate is clamped
    /// to <c>[0,1]</c> first, so an optimiser that steps out of bounds gets the
    /// nearest legal rig instead of an out-of-range param the engine rejects.</summary>
    public float ToValue(float unit)
    {
        var t = Math.Clamp(unit, 0f, 1f);
        if (Max <= Min) return Min;
        var v = Logarithmic
            ? Min * Math.Pow(Max / (double)Min, t)
            : Min + t * (double)(Max - Min);
        return (float)Math.Clamp(v, Min, Max);
    }
}

/// <summary>
///     The bijection between a rig with a FIXED chain and a point in
///     <c>[0,1]^n</c>: one coordinate per continuous param of every module in the
///     chain, in chain order then catalogue param order, so the layout is
///     deterministic and two rigs with the same module type sequence share it.
///     <para>
///         What is NOT in the vector, and why. (1) The chain STRUCTURE — which
///         modules, in what order — is retrieval's job, not the optimiser's.
///         (2) DISCRETE params (see <see cref="KnobParamKinds" />): a staircase
///         loss breaks CMA-ES's covariance estimate. (3) STRING params (a
///         <c>namamp</c> capture, a <c>cab</c> IR), branch tags, sidechain keys
///         and bypass flags: all identity, none continuous. Everything in (1)–(3)
///         is carried through <see cref="KnobLayout.Decode" /> from the TEMPLATE
///         rig untouched, which is what makes the round trip lossless.
///     </para>
///     <para>
///         A module type the catalogue does not know contributes no coordinates
///         and is passed through as-is, rather than being dropped — a rig the
///         matcher cannot tune is still a rig the player can hear.
///     </para>
/// </summary>
public sealed class KnobLayout
{
    private readonly DcRigPreset _template;

    private KnobLayout(DcRigPreset template, IReadOnlyList<KnobCoordinate> coordinates)
    {
        _template = template;
        Coordinates = coordinates;
    }

    /// <summary>The coordinates, in chain order then catalogue param order.</summary>
    public IReadOnlyList<KnobCoordinate> Coordinates { get; }

    /// <summary>Dimensionality of the search space.</summary>
    public int Count => Coordinates.Count;

    /// <summary>The rig this layout was built from: every non-searchable field of
    /// the decoded rig comes from here.</summary>
    public DcRigPreset Template => _template;

    /// <summary>Builds the layout for a rig's chain.</summary>
    public static KnobLayout For(DcRigPreset preset)
    {
        ArgumentNullException.ThrowIfNull(preset);
        var coords = new List<KnobCoordinate>();
        for (var i = 0; i < preset.Modules.Count; i++)
        {
            var module = preset.Modules[i];
            if (!KnobParamKinds.TryGetParams(module.ModuleTypeId, out var specs)) continue;
            foreach (var spec in specs)
            {
                if (KnobParamKinds.IsDiscrete(module.ModuleTypeId, spec)) continue;
                if (spec.Max <= spec.Min) continue;
                coords.Add(new KnobCoordinate(
                    i, module.ModuleTypeId, spec.Id, spec.Min, spec.Max,
                    KnobParamKinds.IsLogarithmic(spec)));
            }
        }

        return new KnobLayout(preset, coords);
    }

    /// <summary>The template rig as a normalised vector. A param the rig leaves
    /// unspecified reads as the catalogue DEFAULT, exactly as
    /// <see cref="DcRigTargetCatalog.FillDefaults" /> would fill it — so encoding
    /// a sparse preset and decoding it back yields the same rendered audio.</summary>
    public float[] Encode() => Encode(_template);

    /// <summary>The given rig as a normalised vector against this layout. The rig
    /// must have the same module type sequence as the template.</summary>
    public float[] Encode(DcRigPreset preset)
    {
        ArgumentNullException.ThrowIfNull(preset);
        var v = new float[Coordinates.Count];
        for (var i = 0; i < Coordinates.Count; i++)
        {
            var c = Coordinates[i];
            var module = preset.Modules[c.ModuleIndex];
            var value = module.Params.TryGetValue(c.ParamId, out var raw)
                ? raw
                : DefaultOf(c.ModuleTypeId, c.ParamId, c.Min);
            v[i] = c.ToUnit(value);
        }

        return v;
    }

    /// <summary>The rig for a normalised vector: the template with every
    /// searchable param replaced, everything else carried through. Out-of-range
    /// coordinates are clamped, never rejected.</summary>
    public DcRigPreset Decode(ReadOnlySpan<float> unit)
    {
        if (unit.Length != Coordinates.Count)
        {
            throw new ArgumentException(
                $"Expected {Coordinates.Count} coordinates, got {unit.Length}.", nameof(unit));
        }

        var modules = new List<DcRigModule>(_template.Modules);
        var edits = new Dictionary<string, float>?[modules.Count];
        for (var i = 0; i < Coordinates.Count; i++)
        {
            var c = Coordinates[i];
            var target = edits[c.ModuleIndex] ??=
                new Dictionary<string, float>(modules[c.ModuleIndex].Params, StringComparer.Ordinal);
            target[c.ParamId] = c.ToValue(unit[i]);
        }

        for (var i = 0; i < modules.Count; i++)
        {
            if (edits[i] is { } p)
                modules[i] = modules[i] with { Params = p };
        }

        return new DcRigPreset(modules);
    }

    private static float DefaultOf(string moduleTypeId, string paramId, float fallback)
        => DcRigTargetCatalog.TryGetParam(moduleTypeId, paramId, out var spec) ? spec.Default : fallback;
}
