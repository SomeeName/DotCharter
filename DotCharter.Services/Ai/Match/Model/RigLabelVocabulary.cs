using System.Text.Json;
using System.Text.Json.Serialization;
using DotCharter.Tone.Audio.Dsp.RsToneConversion;

namespace DotCharter.Services.Ai.Match.Model;

/// <summary>
///     The label space the rig estimator is trained against, derived from
///     <c>DcRigTargetCatalog</c> so python and C# agree by CONSTRUCTION rather
///     than from two hand-maintained copies.
///     <para>
///         The trainer normalises every knob into <c>[0,1]</c> with the rule
///         <see cref="Normalise" /> states and the estimator reads it back with
///         <see cref="Denormalise" />. Both sides load the SAME JSON sidecar
///         (<see cref="ToJson" />, written next to the ONNX by
///         <c>tools/rig-model/export_onnx.py</c>). A SHIPPED model keeps the
///         sidecar it was exported with, and the estimator decodes through THAT,
///         then maps each predicted knob onto today's catalogue by NAME
///         (<c>RigEstimatorOnnx.ConformToCatalogue</c>) — so a catalogue that grows
///         or moves a range after a model ships never shifts its knobs and never
///         needs a retrain.
///     </para>
///     <para>
///         Ordering is deterministic and is part of the contract: modules are
///         sorted ORDINAL by type id, and each module's params keep their
///         catalogue declaration order. The flat knob vector is those params
///         concatenated in that order, and <see cref="ModuleEntry.ParamOffset" />
///         is a module's first index in it.
///     </para>
/// </summary>
public static class RigLabelVocabulary
{
    /// <summary>Sidecar schema version. Bump on any layout change; the estimator
    /// refuses a sidecar whose version it does not know.</summary>
    public const int SchemaVersion = 1;

    /// <summary>The clip length, in seconds, the model is exported for.</summary>
    public const float ClipSeconds = 5f;

    /// <summary>The sample rate the model is exported for.</summary>
    public const int SampleRate = 48000;

    /// <summary>Chain slots the slot head decodes, in canonical signal order.
    /// Matches <c>RigSamplingProfile.MaxModules</c>'s default of 7.</summary>
    public const int Slots = 7;

    /// <summary>The label for an empty slot — index 0 of the slot softmax. It is
    /// not a module type id and cannot collide with one, because every catalogue
    /// id is lowercase alphanumeric.</summary>
    public const string NoneLabel = "<none>";

    // The native palette category per module type (ABI v33, the fixed set in
    // JuceSharp/src/JuceSharp.Native/src/dsp/GearEntry.h). DcRigTargetCatalog
    // mirrors the param RANGES but not the category, so this table is the second
    // half of that mirror — every row was read OUT of the native catalogue table
    // (DspModuleFactory.cpp + src/dsp/gear/*.cpp), never invented.
    // RigLabelVocabularyTests re-checks that every catalogue module has a row
    // here and that every value is one of Families.
    private static readonly Dictionary<string, string> CategoryByModule = new(StringComparer.Ordinal)
    {
        ["acousticsim"] = "cab",
        ["acpre"] = "amp",
        ["amp"] = "amp",
        ["autopan"] = "modulation",
        ["autowah"] = "filter",
        ["barberpole"] = "modulation",
        ["bassamp"] = "amp",
        ["rsamp"] = "amp",
        ["bassod"] = "drive",
        ["basssynth"] = "pitch",
        ["bbddelay"] = "delay",
        ["bigmuff"] = "drive",
        ["bitcrusher"] = "drive",
        ["boost"] = "drive",
        ["cab"] = "cab",
        ["chorus"] = "modulation",
        ["comb"] = "filter",
        ["compressor"] = "dynamics",
        ["convreverb"] = "reverb",
        ["delay"] = "delay",
        ["detune"] = "pitch",
        ["dimension"] = "modulation",
        ["distortion"] = "drive",
        ["doubler"] = "modulation",
        ["ds1"] = "drive",
        ["duckdelay"] = "delay",
        ["ducker"] = "dynamics",
        ["ensemble"] = "modulation",
        ["envfilter"] = "filter",
        ["eq"] = "eq",
        ["expander"] = "dynamics",
        ["feedbacker"] = "synth",
        ["filterseq"] = "filter",
        ["flanger"] = "modulation",
        ["formant"] = "filter",
        ["freeze"] = "synth",
        ["freqshift"] = "modulation",
        ["fuzz"] = "drive",
        ["gain"] = "utility",
        ["gate"] = "dynamics",
        ["gatedreverb"] = "reverb",
        ["glitch"] = "utility",
        ["granular"] = "delay",
        ["graphiceq"] = "eq",
        ["hallreverb"] = "reverb",
        ["harmonizer"] = "pitch",
        ["harmtremolo"] = "modulation",
        ["hm2"] = "drive",
        ["klon"] = "drive",
        ["limiter"] = "dynamics",
        ["looper"] = "utility",
        ["merger"] = "routing",
        ["micpos"] = "cab",
        ["midside"] = "utility",
        ["miku"] = "synth",
        ["mikustomp"] = "synth",
        ["multiband"] = "dynamics",
        ["multitap"] = "delay",
        ["namamp"] = "amp",
        ["noisereducer"] = "dynamics",
        ["octaver"] = "pitch",
        ["octfuzz"] = "drive",
        ["optocomp"] = "dynamics",
        ["overdrive"] = "drive",
        ["phaser"] = "modulation",
        ["pitchcorrect"] = "pitch",
        ["platereverb"] = "reverb",
        ["poweramp"] = "amp",
        ["rat"] = "drive",
        ["resonator"] = "synth",
        ["reverb"] = "reverb",
        ["reversedelay"] = "delay",
        ["reversereverb"] = "reverb",
        ["rhythmdelay"] = "delay",
        ["ringmod"] = "modulation",
        ["rotary"] = "modulation",
        ["shimmer"] = "reverb",
        ["slapback"] = "delay",
        ["splitter"] = "routing",
        ["springreverb"] = "reverb",
        ["subharmonic"] = "pitch",
        ["sustainer"] = "dynamics",
        ["svf"] = "filter",
        ["swell"] = "dynamics",
        ["talkbox"] = "filter",
        ["tapedelay"] = "delay",
        ["tapesat"] = "drive",
        ["tapestop"] = "pitch",
        ["tilt"] = "eq",
        ["transient"] = "dynamics",
        ["tremolo"] = "modulation",
        ["tubepre"] = "drive",
        ["twelvestring"] = "pitch",
        ["tzflanger"] = "modulation",
        ["univibe"] = "modulation",
        ["vibrato"] = "modulation",
        ["vocoder"] = "filter",
        ["volume"] = "utility",
        ["wah"] = "filter",
        ["whammy"] = "pitch",
        ["widener"] = "utility",
    };

    /// <summary>
    ///     The native palette categories, sorted ordinal. The family-presence
    ///     head is one sigmoid per entry, in this order.
    ///     <para>
    ///         This is THIRTEEN, not the nine a reader may expect from the nine
    ///         <c>src/dsp/gear/*.cpp</c> files — those are source files, not
    ///         categories. The engine's vocabulary is the fixed set in
    ///         <c>GearEntry.h</c>, in which cab, eq, routing and synth have no
    ///         gear file of their own.
    ///     </para>
    /// </summary>
    public static IReadOnlyList<string> Families { get; } =
        CategoryByModule.Values.Distinct(StringComparer.Ordinal)
            .OrderBy(v => v, StringComparer.Ordinal).ToArray();

    /// <summary>Every module the estimator can predict, sorted ordinal by type
    /// id. The slot softmax is <c>1 + Modules.Count</c> wide: index 0 is
    /// <see cref="NoneLabel" />, index <c>i + 1</c> is <c>Modules[i]</c>.</summary>
    public static IReadOnlyList<ModuleEntry> Modules { get; } = BuildModules();

    /// <summary>Length of the flat knob vector — every module's params
    /// concatenated in <see cref="Modules" /> order.</summary>
    public static int KnobDimension { get; } = Modules.Count == 0
        ? 0
        : Modules[^1].ParamOffset + Modules[^1].Params.Count;

    private static readonly Dictionary<string, ModuleEntry> ByTypeId =
        Modules.ToDictionary(m => m.TypeId, StringComparer.Ordinal);

    /// <summary>Finds a module entry by its catalogue type id.</summary>
    /// <param name="moduleTypeId">The catalogue type id, e.g. <c>"overdrive"</c>.</param>
    /// <param name="entry">The entry when found; otherwise null.</param>
    /// <returns>True when the module is in the vocabulary.</returns>
    public static bool TryGetModule(string moduleTypeId, out ModuleEntry? entry)
        => ByTypeId.TryGetValue(moduleTypeId, out entry);

    /// <summary>The native palette category of a module type.</summary>
    /// <param name="moduleTypeId">The catalogue type id.</param>
    /// <returns>One of <see cref="Families" />, or null when the type is unknown.</returns>
    public static string? CategoryOf(string moduleTypeId)
        => CategoryByModule.TryGetValue(moduleTypeId, out var c) ? c : null;

    /// <summary>
    ///     Maps a raw param value onto <c>[0,1]</c> by its catalogue range. This
    ///     is THE trainer-side normalisation, reproduced exactly: a non-finite
    ///     value reads as the catalogue DEFAULT, a degenerate range
    ///     (<c>max &lt;= min</c>) yields 0, and the result is clamped.
    /// </summary>
    /// <param name="spec">The param's catalogue range.</param>
    /// <param name="value">The raw value, in catalogue units.</param>
    /// <returns>The normalised value, in <c>[0,1]</c>.</returns>
    public static float Normalise(ParamRange spec, float value)
    {
        if (!float.IsFinite(value)) value = spec.Default;
        var span = spec.Max - spec.Min;
        if (!(span > 0f)) return 0f;
        return Math.Clamp((value - spec.Min) / span, 0f, 1f);
    }

    /// <summary>The inverse of <see cref="Normalise" />: maps <c>[0,1]</c> back
    /// into catalogue units, clamped to the declared range.</summary>
    /// <param name="spec">The param's catalogue range.</param>
    /// <param name="unit">The normalised value; non-finite reads as 0.</param>
    /// <returns>The raw value, in catalogue units.</returns>
    public static float Denormalise(ParamRange spec, float unit)
    {
        if (!float.IsFinite(unit)) unit = 0f;
        var span = spec.Max - spec.Min;
        if (!(span > 0f)) return spec.Min;
        return Math.Clamp(spec.Min + Math.Clamp(unit, 0f, 1f) * span, spec.Min, spec.Max);
    }

    /// <summary>Serialises the vocabulary as the ONNX sidecar both lanes read.</summary>
    /// <param name="indented">Whether to pretty-print the JSON.</param>
    /// <returns>The sidecar JSON.</returns>
    public static string ToJson(bool indented = true)
        => JsonSerializer.Serialize(Snapshot(), new JsonSerializerOptions { WriteIndented = indented });

    /// <summary>The sidecar's data shape, without writing a file.</summary>
    /// <returns>A serialisable snapshot of the vocabulary.</returns>
    public static RigLabelVocabularySnapshot Snapshot() => new(
        SchemaVersion, SampleRate, ClipSeconds, Slots, NoneLabel, KnobDimension,
        Families,
        Modules.Select(m => new ModuleSnapshot(
            m.TypeId, m.Category, m.ParamOffset,
            m.Params.Select(p => new ParamSnapshot(p.Id, p.Min, p.Max, p.Default)).ToArray())).ToArray());

    private static ModuleEntry[] BuildModules()
    {
        var offset = 0;
        var list = new List<ModuleEntry>();
        foreach (var (typeId, specs) in DcRigTargetCatalog.AllModules
                     .OrderBy(m => m.ModuleTypeId, StringComparer.Ordinal))
        {
            var ranges = specs.Select(s => new ParamRange(s.Id, s.Min, s.Max, s.Default)).ToArray();
            list.Add(new ModuleEntry(typeId, CategoryByModule.GetValueOrDefault(typeId, "utility"), offset, ranges));
            offset += ranges.Length;
        }

        return list.ToArray();
    }
}

/// <summary>One param's catalogue range, mirrored out of <c>DcRigTargetCatalog</c>.</summary>
/// <param name="Id">The param id.</param>
/// <param name="Min">Inclusive lower bound, in catalogue units.</param>
/// <param name="Max">Inclusive upper bound, in catalogue units.</param>
/// <param name="Default">The catalogue default, in catalogue units.</param>
public sealed record ParamRange(string Id, float Min, float Max, float Default);

/// <summary>One module in the label space.</summary>
/// <param name="TypeId">The catalogue type id.</param>
/// <param name="Category">Its native palette category, one of <c>RigLabelVocabulary.Families</c>.</param>
/// <param name="ParamOffset">This module's first index in the flat knob vector.</param>
/// <param name="Params">Its params, in catalogue declaration order.</param>
public sealed record ModuleEntry(string TypeId, string Category, int ParamOffset, IReadOnlyList<ParamRange> Params);

/// <summary>The sidecar's serialisable shape.</summary>
/// <param name="SchemaVersion">Sidecar schema version.</param>
/// <param name="SampleRate">The sample rate the model expects.</param>
/// <param name="ClipSeconds">The clip length the model expects.</param>
/// <param name="Slots">Number of chain slots the slot head decodes.</param>
/// <param name="NoneLabel">The empty-slot label.</param>
/// <param name="KnobDimension">Length of the flat knob vector.</param>
/// <param name="Families">Family-presence head labels, in order.</param>
/// <param name="Modules">Modules in slot-softmax order, offset by one for none.</param>
public sealed record RigLabelVocabularySnapshot(
    [property: JsonPropertyName("schemaVersion")] int SchemaVersion,
    [property: JsonPropertyName("sampleRate")] int SampleRate,
    [property: JsonPropertyName("clipSeconds")] float ClipSeconds,
    [property: JsonPropertyName("slots")] int Slots,
    [property: JsonPropertyName("noneLabel")] string NoneLabel,
    [property: JsonPropertyName("knobDim")] int KnobDimension,
    [property: JsonPropertyName("families")] IReadOnlyList<string> Families,
    [property: JsonPropertyName("modules")] IReadOnlyList<ModuleSnapshot> Modules);

/// <summary>A module row in the sidecar.</summary>
/// <param name="TypeId">The catalogue type id.</param>
/// <param name="Category">Its native palette category.</param>
/// <param name="ParamOffset">First index in the flat knob vector.</param>
/// <param name="Params">Its params, in order.</param>
public sealed record ModuleSnapshot(
    [property: JsonPropertyName("id")] string TypeId,
    [property: JsonPropertyName("category")] string Category,
    [property: JsonPropertyName("paramOffset")] int ParamOffset,
    [property: JsonPropertyName("params")] IReadOnlyList<ParamSnapshot> Params);

/// <summary>A param row in the sidecar.</summary>
/// <param name="Id">The param id.</param>
/// <param name="Min">Inclusive lower bound.</param>
/// <param name="Max">Inclusive upper bound.</param>
/// <param name="Default">The catalogue default.</param>
public sealed record ParamSnapshot(
    [property: JsonPropertyName("id")] string Id,
    [property: JsonPropertyName("min")] float Min,
    [property: JsonPropertyName("max")] float Max,
    [property: JsonPropertyName("default")] float Default);
