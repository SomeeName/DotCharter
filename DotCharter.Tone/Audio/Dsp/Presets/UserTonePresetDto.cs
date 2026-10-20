using System.Text.Json.Serialization;
using DotCharter.Tone.Audio.Dsp.RsToneConversion;

namespace DotCharter.Tone.Audio.Dsp.Presets;

/// <summary>
/// JSON-friendly persistence shape for a player-saved tone profile, stored
/// verbatim in <c>RigConfig.UserTonePresets</c> (System.Text.Json needs the
/// mutable auto-props + parameterless ctor and a concrete
/// <see cref="System.Collections.Generic.Dictionary{TKey,TValue}"/> — never the
/// domain <see cref="DcRigPreset"/>, whose <c>IReadOnlyDictionary</c> does not
/// round-trip). All conversion lives in <see cref="ToPreset"/> /
/// <see cref="FromPreset"/> so the store stays trivial and unit-testable.
/// </summary>
public sealed class UserTonePresetDto
{
    public string Id { get; set; } = string.Empty;
    public string Name { get; set; } = string.Empty;
    public DspToneCategory Category { get; set; } = DspToneCategory.Clean;
    public string Description { get; set; } = string.Empty;
    public List<UserToneModuleDto> Modules { get; set; } = [];

    /// <summary>The rig's scenes (A–H), or null (and no JSON key) for a tone that
    /// has none — every tone saved before scenes existed reads back unchanged.</summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public RigSceneSetDto? Scenes { get; set; }

    public UserTonePresetDto Clone() => new()
    {
        Id = Id,
        Name = Name,
        Category = Category,
        Description = Description,
        Modules = Modules.Select(m => m.Clone()).ToList(),
        Scenes = Scenes?.Clone()
    };

    /// <summary>Materializes the persisted shape into a <see cref="DspToneOrigin.User"/>
    /// <see cref="DspTonePreset"/>. Never throws — a null module list reads as an
    /// empty rig.</summary>
    public DspTonePreset ToPreset()
    {
        var modules = (Modules ?? [])
            .Select(m => m.ToModule())
            .ToList();
        return new DspTonePreset(
            Id ?? string.Empty,
            Name ?? string.Empty,
            Category,
            Description ?? string.Empty,
            new DcRigPreset(modules) { Scenes = Scenes?.ToSet() },
            DspToneOrigin.User);
    }

    /// <summary>Maps a preset (its id/name/category/description + rig) into the
    /// persistence shape. Origin is dropped — everything in the store is a user
    /// preset by definition.</summary>
    public static UserTonePresetDto FromPreset(DspTonePreset preset) => new()
    {
        Id = preset.Id,
        Name = preset.Name,
        Category = preset.Category,
        Description = preset.Description,
        Modules = preset.Rig.Modules.Select(UserToneModuleDto.FromModule).ToList(),
        Scenes = RigSceneSetDto.From(preset.Rig.Scenes)
    };
}

/// <summary>One module inside a persisted <see cref="UserTonePresetDto"/>: the
/// catalogue type id, its explicit param values, the bypass flag, the ABI-v22
/// string params, the ABI-v23 branch tag and the ABI-v34 sidechain key.</summary>
public sealed class UserToneModuleDto
{
    public string ModuleTypeId { get; set; } = string.Empty;
    public Dictionary<string, float> Params { get; set; } = new(StringComparer.Ordinal);
    public bool Bypassed { get; set; }

    /// <summary>The module's string-valued params (ABI v22): a <c>namamp</c>'s
    /// <c>modelPath</c> capture, a <c>cab</c>/<c>acousticsim</c> <c>irPath</c>, the
    /// <c>miku</c> <c>phrases</c>. Null (and omitted from the JSON) for the common
    /// module. **Load-bearing:** without it a "Save as new" of a rig with a loaded
    /// NAM capture persisted a MODELLESS namamp, and re-applying that tone brought
    /// back a silent amp — <see cref="DcRigApplier"/> only re-pushes the strings a
    /// preset actually CARRIES when it (re-)adds the node.</summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public Dictionary<string, string>? StringParams { get; set; }

    /// <summary>The module's split/parallel branch tag (ABI v23), <c>0..3</c>.
    /// Omitted from the JSON when 0 (the common case) so an old store stays
    /// schema-valid and a linear rig writes no noise.</summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)]
    public int Branch { get; set; }

    /// <summary>
    ///     The module's ABI-v34 detector key, as the RAW ABI NUMBER
    ///     (0 = None, 1 = SeatInput, 2 = SongMix). Omitted from the JSON when 0
    ///     (the common case) so an old store stays schema-valid and an unkeyed
    ///     rig writes no noise.
    ///     <para>
    ///         <b>An <c>int</c> and not the enum, deliberately.</b> These numbers
    ///         are part of the ABI and are what a shared tone file carries
    ///         between installs; storing the enum would make the document's
    ///         meaning depend on this project's serializer options, and one
    ///         <c>JsonStringEnumConverter</c> added anywhere upstream would
    ///         silently rewrite every saved tone's key as a NAME. The conversion
    ///         is at this boundary and nowhere else.
    ///     </para>
    /// </summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)]
    public int Sidechain { get; set; }

    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public List<RigControlAssignment>? ControlAssignments { get; set; }

    public UserToneModuleDto Clone() => new()
    {
        ModuleTypeId = ModuleTypeId,
        Params = new Dictionary<string, float>(Params ?? [], StringComparer.Ordinal),
        Bypassed = Bypassed,
        StringParams = CopyStrings(StringParams),
        Branch = Branch,
        Sidechain = Sidechain,
        ControlAssignments = CopyAssignments(ControlAssignments)
    };

    public DcRigModule ToModule() => new(
        ModuleTypeId ?? string.Empty,
        new Dictionary<string, float>(Params ?? [], StringComparer.Ordinal),
        Bypassed)
    {
        StringParams = CopyStrings(StringParams),
        Branch = Branch,
        Sidechain = SidechainOf(Sidechain),
        ControlAssignments = CopyAssignments(ControlAssignments) ?? []
    };

    public static UserToneModuleDto FromModule(DcRigModule module) => new()
    {
        ModuleTypeId = module.ModuleTypeId,
        Params = new Dictionary<string, float>(module.Params, StringComparer.Ordinal),
        Bypassed = module.Bypassed,
        StringParams = CopyStrings(module.StringParams),
        Branch = module.Branch,
        Sidechain = (int)module.Sidechain,
        ControlAssignments = module.ControlAssignments.Count == 0 ? null : module.ControlAssignments.ToList()
    };

    /// <summary>
    ///     A persisted key NUMBER as a source. A number this build has never
    ///     heard of - a tone written by a NEWER app, or a hand-edited file -
    ///     reads as <c>None</c> rather than being cast blindly: an undefined enum
    ///     value would travel all the way to
    ///     <c>dca_engine_dsp_set_node_sidechain</c> and be rejected there, and a
    ///     rejected configuration costs the module its audibility.
    /// </summary>
    internal static JuceSharp.Dsp.JuceDspSidechainSource SidechainOf(int value)
        => value is (int)JuceSharp.Dsp.JuceDspSidechainSource.SeatInput
                 or (int)JuceSharp.Dsp.JuceDspSidechainSource.SongMix
            ? (JuceSharp.Dsp.JuceDspSidechainSource)value
            : JuceSharp.Dsp.JuceDspSidechainSource.None;

    // Invalid imported mappings do not discard an otherwise usable saved tone.
    private static List<RigControlAssignment>? CopyAssignments(IReadOnlyList<RigControlAssignment>? assignments)
    {
        try { var copy = RigControlAssignments.Copy(assignments); return copy.Count == 0 ? null : copy.ToList(); }
        catch (ArgumentException) { return null; }
    }

    /// <summary>Defensive copy; an empty map reads as "none" so a round-trip never
    /// turns a null into an empty dictionary (or vice versa).</summary>
    private static Dictionary<string, string>? CopyStrings(IReadOnlyDictionary<string, string>? strings)
        => strings is { Count: > 0 }
            ? new Dictionary<string, string>(strings, StringComparer.Ordinal)
            : null;
}
