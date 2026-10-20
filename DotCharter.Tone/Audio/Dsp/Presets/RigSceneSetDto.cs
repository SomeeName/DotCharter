using System.Text.Json.Serialization;
using DotCharter.Tone.Audio.Dsp.RsToneConversion;
using DotCharter.Tone.Audio.Dsp.Scenes;

namespace DotCharter.Tone.Audio.Dsp.Presets;

/// <summary>
///     The persisted shape of a rig's scenes (<see cref="RigSceneSet" />), inside
///     a <see cref="UserTonePresetDto" /> — so they save with a user tone, travel in
///     an exported tone file and come back with the persisted live rig. Additive:
///     a tone without scenes writes no <c>Scenes</c> key at all, and a document
///     that has none reads back as a rig without scenes.
/// </summary>
public sealed class RigSceneSetDto
{
    /// <summary>Which scene is playing, 0..7 (A..H).</summary>
    public int Active { get; set; }

    /// <summary>The eight slots in order; a short or long list is normalized on read.</summary>
    public List<RigSceneDto?> Slots { get; set; } = [];

    public RigSceneSetDto Clone() => new()
    {
        Active = Active,
        Slots = (Slots ?? []).Select(s => s?.Clone()).ToList()
    };

    /// <summary>The domain set, normalized (eight slots, active in range, names
    /// cleaned, non-finite values dropped). Never throws.</summary>
    public RigSceneSet ToSet()
    {
        var slots = (Slots ?? [])
            .Select(s => s?.ToScene() ?? RigScene.Empty)
            .ToList();
        return RigScenes.Normalize(new RigSceneSet(slots, Active));
    }

    public static RigSceneSetDto? From(RigSceneSet? set)
    {
        if (set is null) return null;
        set = RigScenes.Normalize(set);
        return new RigSceneSetDto
        {
            Active = set.Active,
            Slots = set.Slots.Select(RigSceneDto.From).ToList()
        };
    }
}

/// <summary>One persisted scene: its name and, once used, every module's values.</summary>
public sealed class RigSceneDto
{
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? Name { get; set; }

    /// <summary>Null = the slot has never been used.</summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public List<RigSceneModuleDto>? Modules { get; set; }

    public RigSceneDto Clone() => new()
    {
        Name = Name,
        Modules = Modules?.Select(m => m.Clone()).ToList()
    };

    public RigScene ToScene() => new(
        RigScenes.CleanName(Name),
        Modules?.Where(m => m is not null && !string.IsNullOrWhiteSpace(m.ModuleTypeId))
            .Select(m => m.ToModule())
            .ToList());

    public static RigSceneDto? From(RigScene scene) =>
        scene.Name is null && scene.Modules is null
            ? null
            : new RigSceneDto
            {
                Name = scene.Name,
                Modules = scene.Modules?.Select(RigSceneModuleDto.From).ToList()
            };
}

/// <summary>One module's values inside a persisted scene.</summary>
public sealed class RigSceneModuleDto
{
    public string ModuleTypeId { get; set; } = string.Empty;
    public Dictionary<string, float> Params { get; set; } = new(StringComparer.Ordinal);

    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)]
    public bool Bypassed { get; set; }

    public RigSceneModuleDto Clone() => new()
    {
        ModuleTypeId = ModuleTypeId,
        Params = new Dictionary<string, float>(Params ?? [], StringComparer.Ordinal),
        Bypassed = Bypassed
    };

    public RigSceneModule ToModule() => new(
        ModuleTypeId,
        (Params ?? [])
            .Where(p => !string.IsNullOrWhiteSpace(p.Key) && float.IsFinite(p.Value))
            .ToDictionary(p => p.Key, p => p.Value, StringComparer.Ordinal),
        Bypassed);

    public static RigSceneModuleDto From(RigSceneModule module) => new()
    {
        ModuleTypeId = module.ModuleTypeId,
        Params = new Dictionary<string, float>(module.Params, StringComparer.Ordinal),
        Bypassed = module.Bypassed
    };

    /// <summary>An imported scene module, every value forced into its declared
    /// range (the tone file's import rule), or null for a type this build does
    /// not know.</summary>
    internal static RigSceneModule? Sanitize(RigSceneModule module)
    {
        if (!DcRigTargetCatalog.ModuleTypeIds.Contains(module.ModuleTypeId)) return null;
        var values = new Dictionary<string, float>(StringComparer.Ordinal);
        foreach (var (id, value) in module.Params)
            values[id] = DcRigTargetCatalog.Clamp(module.ModuleTypeId, id, value);
        return module with { Params = values };
    }
}
