using System.Text.Json;
using DotCharter.Tone.Audio.Dsp.RsToneConversion;

namespace DotCharter.Tone.Audio.Dsp.Presets;

/// <summary>Why a tone file could not be imported — the UI maps each one onto a
/// localized status line, so the reason lives here and the WORDS live there.</summary>
public enum TonePresetImportFault
{
    /// <summary>Nothing wrong; the import produced a preset.</summary>
    None,

    /// <summary>Not JSON, or JSON that is not a tone file at all.</summary>
    Malformed,

    /// <summary>Valid, but describes no modules — an empty rig is a file that
    /// would silently replace the player's tone with nothing.</summary>
    NoModules,

    /// <summary>It names a module this build's catalogue does not have (a newer
    /// app, or a hand-edited file). The offending id is the detail.</summary>
    UnknownModule
}

/// <summary>The outcome of <see cref="TonePresetJsonFile.Parse" />: a preset, or a
/// fault plus whatever detail the message should name.</summary>
public sealed record TonePresetImport(DspTonePreset? Preset, TonePresetImportFault Fault, string? Detail = null)
{
    public bool Ok => Preset is not null;
}

/// <summary>
///     Reads and writes ONE tone as a JSON file — the shareable half of the
///     preset browser ("send me your tone").
///
///     <para>
///         <b>It is the persisted shape, not a second format.</b> The document is
///         a <see cref="UserTonePresetDto" />: the same schema
///         <c>RigConfig.UserTonePresets</c> stores, so a tone exported by one
///         install and a tone saved in another are the same bytes, and the
///         ABI-v22 string params (a <c>namamp</c> capture, a cab IR) and the
///         ABI-v23 branch tag and the ABI-v34 sidechain key travel with it. A hand-rolled second serializer
///         here is exactly the drift that made two of the four snapshot loops
///         restore a MODELLESS neural amp.
///     </para>
///     <para>
///         <b>Reading is defensive by contract.</b> The file comes from outside
///         the app, so <see cref="Parse" /> never throws and never returns a rig
///         the engine cannot run: every module id is checked against
///         <see cref="DcRigTargetCatalog" />, every param is clamped to its
///         declared range (a non-finite value falls back to the catalogue
///         default), the branch tag is clamped to 0..3, and unspecified params are
///         filled with their defaults so an imported tone cannot inherit the
///         values of whatever was applied before it.
///     </para>
///     Pure: no I/O and no clocks — the caller owns the file.
/// </summary>
public static class TonePresetJsonFile
{
    /// <summary>The extension the save picker suggests. A plain <c>.json</c> on
    /// purpose: it is a readable document people mail to each other, and every OS
    /// already knows what to open it with.</summary>
    public const string FileExtension = ".json";

    private static readonly JsonSerializerOptions WriteOptions = new() { WriteIndented = true };

    private static readonly JsonSerializerOptions ReadOptions = new()
    {
        PropertyNameCaseInsensitive = true,
        AllowTrailingCommas = true,
        ReadCommentHandling = JsonCommentHandling.Skip
    };

    /// <summary>The tone as an indented JSON document. Indented because a human
    /// reads and edits these.</summary>
    public static string Serialize(DspTonePreset preset)
    {
        ArgumentNullException.ThrowIfNull(preset);
        return JsonSerializer.Serialize(UserTonePresetDto.FromPreset(preset), WriteOptions);
    }

    /// <summary>
    ///     Parses a tone document into an applicable USER preset, or reports why
    ///     it could not. Never throws.
    ///     <para>
    ///         The returned preset keeps the file's id, name, category and
    ///         description; the CALLER decides what to do with them (the browser
    ///         saves it as a new user preset, which mints a fresh id — an imported
    ///         tone must not be able to overwrite one of the player's).
    ///     </para>
    /// </summary>
    public static TonePresetImport Parse(string? json)
    {
        if (string.IsNullOrWhiteSpace(json))
            return new TonePresetImport(null, TonePresetImportFault.Malformed);

        UserTonePresetDto? dto;
        try
        {
            dto = JsonSerializer.Deserialize<UserTonePresetDto>(json, ReadOptions);
        }
        catch (JsonException)
        {
            return new TonePresetImport(null, TonePresetImportFault.Malformed);
        }
        catch (NotSupportedException)
        {
            // A document whose shape cannot map onto the DTO at all (a bare array,
            // a number) — the same verdict as malformed to the user.
            return new TonePresetImport(null, TonePresetImportFault.Malformed);
        }

        if (dto is null)
            return new TonePresetImport(null, TonePresetImportFault.Malformed);

        var modules = dto.Modules ?? [];
        if (modules.Count == 0)
            return new TonePresetImport(null, TonePresetImportFault.NoModules);

        var sanitized = new List<DcRigModule>(modules.Count);
        foreach (var module in modules)
        {
            var id = module?.ModuleTypeId;
            if (module is null || string.IsNullOrWhiteSpace(id) ||
                !DcRigTargetCatalog.ModuleTypeIds.Contains(id))
            {
                return new TonePresetImport(null, TonePresetImportFault.UnknownModule,
                    string.IsNullOrWhiteSpace(id) ? "?" : id);
            }

            sanitized.Add(Sanitize(module, id));
        }

        // Fill what the document left out, so an imported tone sounds the same
        // whatever was on the chain before it (the DcRigApplier reuse rule).
        var rig = DcRigTargetCatalog.FillDefaults(new DcRigPreset(sanitized) { Scenes = SanitizeScenes(dto.Scenes) });

        return new TonePresetImport(
            new DspTonePreset(
                dto.Id ?? string.Empty,
                dto.Name ?? string.Empty,
                dto.Category,
                dto.Description ?? string.Empty,
                rig,
                DspToneOrigin.User),
            TonePresetImportFault.None);
    }

    /// <summary>One module with every value forced into range: params clamped to
    /// their declared min/max (a non-finite value taken as the default), the branch
    /// tag clamped to 0..3, the sidechain key folded to a source this build knows,
    /// string params copied verbatim (a path is the file
    /// system's business, and the native module refuses one it cannot load).</summary>
    private static DcRigModule Sanitize(UserToneModuleDto module, string moduleTypeId)
    {
        var values = new Dictionary<string, float>(StringComparer.Ordinal);
        foreach (var (paramId, value) in module.Params ?? [])
        {
            if (string.IsNullOrWhiteSpace(paramId)) continue;
            var known = DcRigTargetCatalog.TryGetParam(moduleTypeId, paramId, out var spec);
            values[paramId] = float.IsFinite(value)
                ? DcRigTargetCatalog.Clamp(moduleTypeId, paramId, value)
                : known ? spec.Default : 0f;
        }

        var strings = module.StringParams is { Count: > 0 } s
            ? new Dictionary<string, string>(s, StringComparer.Ordinal)
            : null;

        return new DcRigModule(moduleTypeId, values, module.Bypassed)
        {
            StringParams = strings,
            Branch = Math.Clamp(module.Branch, 0, 3),
            // An unknown key number reads as None (UserToneModuleDto.SidechainOf)
            // - never cast blindly, because an undefined value reaches the engine
            // and the rejection costs the module its audibility.
            Sidechain = UserToneModuleDto.SidechainOf(module.Sidechain),
            ControlAssignments = SafeAssignments(module)
        };
    }

    /// <summary>
    ///     The document's scenes with every value forced into range and every
    ///     module of a type this build does not know dropped from the scene (the
    ///     module itself is already refused above). A scene is a set of knob
    ///     positions, so a bad one costs that module's values, never the tone.
    /// </summary>
    private static Scenes.RigSceneSet? SanitizeScenes(RigSceneSetDto? scenes)
    {
        if (scenes is null) return null;
        var set = scenes.ToSet();
        var slots = set.Slots
            .Select(s => s.Modules is { } modules
                ? s with { Modules = modules.Select(RigSceneModuleDto.Sanitize).OfType<Scenes.RigSceneModule>().ToList() }
                : s)
            .ToList();
        return new Scenes.RigSceneSet(slots, set.Active);
    }

    /// <summary>An imported control map that does not validate is DROPPED, not a
    /// reason to reject the tone — the sound is the point, and a bad MIDI mapping
    /// in somebody else's file must not cost the player the whole rig.</summary>
    private static IReadOnlyList<RigControlAssignment> SafeAssignments(UserToneModuleDto module)
    {
        try { return RigControlAssignments.Copy(module.ControlAssignments); }
        catch (ArgumentException) { return []; }
    }
}
