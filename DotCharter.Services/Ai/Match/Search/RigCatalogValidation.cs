using DotCharter.Tone.Audio.Dsp.RsToneConversion;

namespace DotCharter.Services.Ai.Match.Search;

/// <summary>One reason a rig is not something this app may hand a player.</summary>
/// <param name="ModuleIndex">Where in the chain, or -1 for a whole-rig fault.</param>
/// <param name="ModuleTypeId">The offending module's catalogue type id.</param>
/// <param name="ParamId">The offending param, or null when the MODULE is the fault.</param>
/// <param name="Message">One sentence naming the module, the value and the range.</param>
public sealed record RigCatalogFault(int ModuleIndex, string ModuleTypeId, string? ParamId, string Message);

/// <summary>
///     The gate every rig this lane hands out passes: only modules the DSP module
///     catalogue knows, only knob values inside the range that catalogue declares.
///     <para>
///         <b>Reject, never repair.</b> A rig that fails comes back with a fault
///         naming the module or the value — it is not clamped into range and it is
///         not quietly dropped, because both of those turn "the matcher produced
///         something this app cannot play" into a tone that merely sounds wrong. The
///         same rule the tone catalogue has everywhere else: what leaves here has to
///         be something the gear actually supports.
///     </para>
///     <para>
///         <b>Retrieval cannot trip this and that is the point.</b> Every library row
///         is a <see cref="IRigSampler" /> draw, and a draw is built out of
///         <see cref="DcRigTargetCatalog" />'s own specs — measured over
///         the shipped 4,000-row library: 0 unknown module types, 0 unknown params,
///         0 out-of-range values. The rigs that can fail are the ones that came from
///         OUTSIDE the sampler — the caller's prior (a language model's guess, a
///         user's own board, a tone file from another install) and the ONNX
///         estimator's decoded chain — and neither of those is checked anywhere
///         else before it is rendered, scored and offered.
///     </para>
///     <para>
///         Only FINITE values are legal: a NaN compares false against every bound,
///         so an explicit check is the difference between rejecting one and passing
///         it through as "not out of range".
///     </para>
/// </summary>
public static class RigCatalogValidation
{
    /// <summary>The catalogue, indexed once. Read from
    /// <see cref="DcRigTargetCatalog.AllModules" /> rather than restated — a second
    /// copy of the module list is a second thing to keep correct, and it would rot
    /// silently (it would go on producing plausible verdicts).</summary>
    private static readonly Dictionary<string, IReadOnlyList<DcRigTargetCatalog.ParamSpec>> Catalogue =
        DcRigTargetCatalog.AllModules.ToDictionary(m => m.ModuleTypeId, m => m.Params, StringComparer.Ordinal);

    /// <summary>The first fault in <paramref name="rig" />, or null when every module
    /// and every knob is one the catalogue declares.</summary>
    public static RigCatalogFault? FirstFault(DcRigPreset? rig)
    {
        if (rig is null) return new RigCatalogFault(-1, string.Empty, null, "the rig is null");

        for (var i = 0; i < rig.Modules.Count; i++)
        {
            var module = rig.Modules[i];
            var type = module.ModuleTypeId;
            if (string.IsNullOrWhiteSpace(type))
            {
                return new RigCatalogFault(i, string.Empty, null,
                    $"module {i + 1} has no module type id");
            }

            if (!Catalogue.TryGetValue(type, out var specs))
            {
                return new RigCatalogFault(i, type, null,
                    $"module {i + 1} is '{type}', which is not in the DSP module catalogue");
            }

            foreach (var (paramId, value) in module.Params)
            {
                var spec = Spec(specs, paramId);
                if (spec is null)
                {
                    return new RigCatalogFault(i, type, paramId,
                        $"'{type}.{paramId}' (module {i + 1}) is not a param this module has");
                }

                if (!float.IsFinite(value))
                {
                    return new RigCatalogFault(i, type, paramId,
                        $"'{type}.{paramId}' (module {i + 1}) is {value}, which is not a number");
                }

                if (value < spec.Value.Min || value > spec.Value.Max)
                {
                    return new RigCatalogFault(i, type, paramId,
                        $"'{type}.{paramId}' (module {i + 1}) is {value.ToString("0.###",
                            System.Globalization.CultureInfo.InvariantCulture)}, outside the catalogue range " +
                        $"[{spec.Value.Min.ToString("0.###", System.Globalization.CultureInfo.InvariantCulture)}, " +
                        $"{spec.Value.Max.ToString("0.###", System.Globalization.CultureInfo.InvariantCulture)}]");
                }
            }
        }

        return null;
    }

    /// <summary>True when the rig is one this app may build and a player may hear.</summary>
    public static bool IsValid(DcRigPreset? rig) => FirstFault(rig) is null;

    private static DcRigTargetCatalog.ParamSpec? Spec(IReadOnlyList<DcRigTargetCatalog.ParamSpec> specs, string paramId)
    {
        foreach (var spec in specs)
        {
            if (string.Equals(spec.Id, paramId, StringComparison.Ordinal))
                return spec;
        }

        return null;
    }
}
