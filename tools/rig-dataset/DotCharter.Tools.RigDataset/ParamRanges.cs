using JuceSharp.Dsp;

namespace DotCharter.Tools.RigDataset;

/// <summary>
///     "Does this module have this knob, and what are its ends?"
///     <para>
///         <b>It is an interface because the answer's only source needs the native
///         DLL.</b> <c>DcRigTargetCatalog</c> — the managed mirror the converter and
///         the sampler clamp against — is <c>internal</c> to
///         <c>DotCharter.Services</c>, so this tool cannot read it at all; the
///         reachable source of truth is <see cref="JuceDspModuleCatalog" />, which
///         needs no engine but does need <c>juce_sharp.dll</c>. Taking the ranges as
///         a dependency keeps <see cref="Beds.BedPlanner" /> pure, so the whole
///         musical half of the bed generator stays in the default gate with no
///         native anything.
///     </para>
///     <para>
///         <b>Do not add a third table.</b> Copying ranges into this tool is what the
///         sampler's guide forbids for exactly the reason it forbids it: the copy
///         goes stale on the next ABI, and a stale range is a bed rig whose sidecar
///         claims a knob the engine clamped somewhere else.
///     </para>
/// </summary>
public interface IParamRanges
{
    /// <summary>True when the catalogue has this knob, with its ends.</summary>
    /// <param name="moduleTypeId">Engine module type id.</param>
    /// <param name="paramId">Param id.</param>
    /// <param name="min">The low end.</param>
    /// <param name="max">The high end.</param>
    bool TryGet(string moduleTypeId, string paramId, out float min, out float max);
}

/// <summary>Range helpers every implementation shares.</summary>
public static class ParamRanges
{
    /// <summary>Maps a 0…1 unit value onto the knob's range, or null when there is no
    /// such knob — the caller then DROPS it rather than pushing a param the engine
    /// will refuse while the sidecar claims it was set.</summary>
    /// <param name="ranges">The catalogue.</param>
    /// <param name="moduleTypeId">Engine module type id.</param>
    /// <param name="paramId">Param id.</param>
    /// <param name="unit">0…1.</param>
    public static float? FromUnit(this IParamRanges ranges, string moduleTypeId, string paramId, double unit)
        => ranges.TryGet(moduleTypeId, paramId, out var min, out var max)
            ? min + (float)Math.Clamp(unit, 0d, 1d) * (max - min)
            : null;
}

/// <summary>The real catalogue, read out of the native DLL. No engine and no audio
/// device — <see cref="JuceDspModuleCatalog" /> is a compile-time table on the other
/// side, read once per process.</summary>
public sealed class EngineParamRanges : IParamRanges
{
    /// <summary>The shared instance. The catalogue is immutable and cached natively,
    /// so there is no reason for a second.</summary>
    public static EngineParamRanges Instance { get; } = new();

    /// <inheritdoc />
    public bool TryGet(string moduleTypeId, string paramId, out float min, out float max)
    {
        min = 0f;
        max = 0f;
        var spec = JuceDspModuleCatalog.Find(moduleTypeId)?.Params
            .FirstOrDefault(p => string.Equals(p.Id, paramId, StringComparison.Ordinal));
        if (spec is null) return false;
        min = spec.Min;
        max = spec.Max;
        return true;
    }
}

/// <summary>A fixed table, for a test that must not load the native DLL. Also what a
/// <c>--dry-run</c> plan falls back to when the DLL is absent.</summary>
public sealed class FixedParamRanges : IParamRanges
{
    private readonly IReadOnlyDictionary<(string Module, string Param), (float Min, float Max)> _table;

    /// <summary>Creates the table.</summary>
    /// <param name="table">module id + param id → range.</param>
    public FixedParamRanges(IReadOnlyDictionary<(string Module, string Param), (float Min, float Max)> table)
        => _table = table;

    /// <summary>Every knob this tool's bed rigs ask for, at 0…1, plus the handful
    /// with a wider native range. Enough for a plan test; NOT a mirror of the
    /// catalogue and never to be used by the renderer.</summary>
    public static FixedParamRanges UnitDefaults(IEnumerable<(string Module, string Param)> knobs)
        => new(knobs.ToDictionary(k => k, _ => (0f, 1f)));

    /// <inheritdoc />
    public bool TryGet(string moduleTypeId, string paramId, out float min, out float max)
    {
        if (_table.TryGetValue((moduleTypeId, paramId), out var r))
        {
            (min, max) = r;
            return true;
        }

        min = 0f;
        max = 0f;
        return false;
    }
}
