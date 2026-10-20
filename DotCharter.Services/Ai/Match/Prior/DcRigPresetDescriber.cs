using System.Globalization;
using System.Text;
using DotCharter.Tone.Ai;
using DotCharter.Tone.Audio.Dsp.RsToneConversion;

namespace DotCharter.Services.Ai.Match.Prior;

/// <summary>
///     A rig as READABLE TEXT: the ordered chain in the catalogue's own words, and
///     every knob the preset sets, in the param's own unit.
///
///     <para>
///         <b>One renderer, two readers.</b> The prior source names a section's rig
///         back to the model when it asks for a follow-up, and the narrator hands the
///         MATCHED rig to the model to be explained — both need the same chain in the
///         same words, and two renderings of one rig that disagree about a knob is a
///         model told two different things about one board.
///     </para>
///     <para>
///         Display names come from <see cref="AiGearNotes" /> (the prompt's own
///         glossary), and units from <see cref="AiGearNotes.UnitOf" />, so a module
///         renders here exactly as the design prompt already describes it. A module
///         id the glossary does not carry renders as its raw id rather than being
///         dropped — a rig the describer cannot name is still a rig the player can
///         hear.
///     </para>
///     <para>
///         <b>The UI has a third copy.</b>
///         <c>ToneDesignViewModel.ChainSummary</c> builds the same arrow-joined chain
///         from the live engine's display names; <see cref="Chain" /> is the same
///         string built from the glossary and needs no open engine.
///         <b>This lane deliberately does not edit the UI</b> — swapping
///         <c>ChainSummary</c> for this is a follow-up for the UI lane, and the only
///         thing to check before doing it is that the glossary's names read as well
///         in the panel as the engine's do.
///     </para>
/// </summary>
public static class DcRigPresetDescriber
{
    private static readonly Lazy<Dictionary<string, string>> NamesById = new(
        () => AiGearNotes.All.ToDictionary(n => n.Id, n => n.Name, StringComparer.Ordinal),
        isThreadSafe: true);

    private static readonly Lazy<Dictionary<string, IReadOnlyList<string>>> ParamOrderById = new(
        () => DcRigTargetCatalog.AllModules.ToDictionary(
            m => m.ModuleTypeId,
            m => (IReadOnlyList<string>)m.Params.Select(p => p.Id).ToList(),
            StringComparer.Ordinal),
        isThreadSafe: true);

    /// <summary>The module's display name — "Noise Gate", "Tube Amp" — or its raw
    /// type id when the glossary does not carry one.</summary>
    /// <param name="moduleTypeId">A catalogue module type id.</param>
    public static string NameOf(string moduleTypeId)
        => moduleTypeId is not null && NamesById.Value.TryGetValue(moduleTypeId, out var name)
            ? name
            : moduleTypeId ?? "";

    /// <summary>The ordered chain on one line: "Noise Gate → Overdrive → Tube Amp →
    /// Cabinet". Empty for a rig with no modules.</summary>
    /// <param name="rig">The rig.</param>
    public static string Chain(DcRigPreset rig)
    {
        ArgumentNullException.ThrowIfNull(rig);
        return string.Join(" → ", rig.Modules.Select(m => NameOf(m.ModuleTypeId)));
    }

    /// <summary>
    ///     The whole rig, one line per module: position, display name, type id, and
    ///     the params the preset SETS, in catalogue order, each with its unit.
    ///     <para>
    ///         Params the preset omits are NOT listed. They take their catalogue
    ///         default when the rig is applied, and listing forty defaults for a
    ///         seven-module chain buries the handful of values that carry the tone —
    ///         a model reading it then explains the defaults.
    ///     </para>
    /// </summary>
    /// <param name="rig">The rig to describe.</param>
    public static string Describe(DcRigPreset rig)
    {
        ArgumentNullException.ThrowIfNull(rig);
        if (rig.Modules.Count == 0) return "(an empty chain — no modules)";

        var sb = new StringBuilder(1024);
        for (var i = 0; i < rig.Modules.Count; i++)
        {
            var module = rig.Modules[i];
            sb.Append(i + 1).Append(". ")
                .Append(NameOf(module.ModuleTypeId))
                .Append(" (").Append(module.ModuleTypeId).Append(')');
            if (module.Bypassed) sb.Append(" — BYPASSED");
            sb.Append(": ").AppendLine(Knobs(module));
        }

        return sb.ToString().TrimEnd();
    }

    /// <summary>The module's set params as "drive 0.6, tone 4.5 kHz", in catalogue
    /// order first and then anything the catalogue does not know (so a param from a
    /// newer build is reported rather than silently dropped). "catalogue defaults"
    /// when the module sets none.</summary>
    /// <param name="module">The module.</param>
    public static string Knobs(DcRigModule module)
    {
        ArgumentNullException.ThrowIfNull(module);
        var parts = new List<string>(module.Params.Count + 2);
        var order = ParamOrderById.Value.TryGetValue(module.ModuleTypeId, out var ids)
            ? ids
            : [];

        foreach (var id in order)
        {
            if (module.Params.TryGetValue(id, out var value))
                parts.Add(Format(id, value));
        }

        foreach (var (id, value) in module.Params)
        {
            if (!order.Contains(id, StringComparer.Ordinal))
                parts.Add(Format(id, value));
        }

        if (module.StringParams is { Count: > 0 } strings)
        {
            foreach (var (id, value) in strings)
                parts.Add($"{id} \"{ShortenPath(value)}\"");
        }

        return parts.Count == 0 ? "catalogue defaults" : string.Join(", ", parts);
    }

    private static string Format(string paramId, float value)
    {
        var unit = AiGearNotes.UnitOf(paramId);
        var text = value.ToString("0.###", CultureInfo.InvariantCulture);
        return unit is null ? $"{paramId} {text}" : $"{paramId} {text} {unit}";
    }

    /// <summary>A file-valued string param is shown by FILE NAME: a capture's full
    /// path is a hundred characters of the player's own directory layout, none of
    /// which describes the tone, and all of which would be sent to a provider.</summary>
    private static string ShortenPath(string value)
    {
        if (string.IsNullOrEmpty(value)) return "";
        var cut = value.LastIndexOfAny(['\\', '/']);
        return cut >= 0 && cut < value.Length - 1 ? value[(cut + 1)..] : value;
    }
}
