using System.Text.Json;
using DotCharter.Tone.Audio.Dsp.Presets;
using DotCharter.Tone.Audio.Dsp.RsToneConversion;

namespace DotCharter.Tone.Ai;

/// <summary>What the validator made of a reply: the preset (null when nothing
/// survived), the draft it came from, and every correction it had to make.</summary>
public sealed record ToneDraftValidation(
    DspTonePreset? Preset,
    ToneDraft? Draft,
    IReadOnlyList<string> Warnings);

/// <summary>
///     Turns a model's reply into a preset this build can actually apply, or says
///     why it cannot.
///
///     <para>
///         <b>It never trusts the reply and never throws on its content.</b> The
///         shape is the same contract <c>TonePresetJsonFile.Parse</c> honours for a
///         tone file a stranger mailed you — the source is untrusted either way —
///         with one deliberate difference: a file naming an unknown module is
///         REJECTED whole (the user asked for that exact file), while a model's
///         unknown module is DROPPED with a warning (the user asked for a sound,
///         and eleven good modules plus a note beats nothing).
///     </para>
///     <para>
///         Every correction is reported. A silent clamp is how a tone comes out
///         wrong with nothing on screen saying so.
///     </para>
/// </summary>
public static class ToneDraftValidator
{
    /// <summary>Hard cap on chain length. A longer chain is truncated rather than
    /// rejected: the head of a signal chain is the part that defines the sound.
    /// <para>Enforced HERE and nowhere else. The schema states the bound in its
    /// <c>description</c> only — <c>maxItems</c> is outside both decoders' strict
    /// subsets, see <see cref="ToneDraftJson" /> — so this is the one thing
    /// between a thirty-module reply and a rig nobody asked for.</para></summary>
    public const int MaxModules = 12;

    /// <summary>Parses <paramref name="json" /> (already extracted from any prose)
    /// and validates it. Returns a validation with a null preset when the JSON is
    /// unusable or nothing survived; the caller turns that into
    /// <see cref="ToneDesignFault.InvalidOutput" />.</summary>
    /// <param name="json">The model's reply.</param>
    /// <param name="captures">The closed capture list the prompt offered. A
    /// <c>namamp</c> module is accepted ONLY when it names a capture in it — with no
    /// catalogue (the default, and a player with no captures) every <c>namamp</c> is
    /// dropped, which is exactly what the prompt told the model in that case.</param>
    public static ToneDraftValidation Validate(string? json, NamCaptureCatalog? captures = null)
    {
        var warnings = new List<string>();

        ToneDraft? draft;
        try
        {
            draft = JsonSerializer.Deserialize<ToneDraft>(json ?? "", ToneDraftJson.Options);
        }
        catch (JsonException ex)
        {
            warnings.Add($"The reply was not valid JSON: {ex.Message}");
            return new ToneDraftValidation(null, null, warnings);
        }
        catch (NotSupportedException)
        {
            warnings.Add("The reply was JSON, but not a tone at all.");
            return new ToneDraftValidation(null, null, warnings);
        }

        if (draft is null)
        {
            warnings.Add("The reply was empty.");
            return new ToneDraftValidation(null, null, warnings);
        }

        var modules = ValidateModules(draft.Modules, captures, warnings);
        if (modules.Count == 0)
        {
            warnings.Add("None of the modules in the reply exist in this build's catalogue.");
            return new ToneDraftValidation(null, draft, warnings);
        }

        // A name and a reason are a model's words: one capped line each, because both reach the
        // tone list, the status rail and the log.
        var name = AiUntrustedText.Clean(draft.Name, AiUntrustedText.MaxNameLength) is { Length: > 0 } named
            ? named
            : "AI Tone";
        var rationale = AiUntrustedText.Clean(draft.Rationale, AiUntrustedText.MaxRationaleLength);

        var preset = new DspTonePreset(
            $"ai-{Guid.NewGuid():N}",
            name,
            CategoryFor(draft),
            rationale,
            new DcRigPreset(modules),
            DspToneOrigin.User);

        return new ToneDraftValidation(preset, draft, warnings);
    }

    private static List<DcRigModule> ValidateModules(
        IReadOnlyList<ToneDraftModule>? drafted, NamCaptureCatalog? captures, List<string> warnings)
    {
        var modules = new List<DcRigModule>();
        if (drafted is null) return modules;

        foreach (var module in drafted)
        {
            if (modules.Count == MaxModules)
            {
                warnings.Add(
                    $"The chain had more than {MaxModules} modules; everything after the " +
                    $"{MaxModules}th was dropped.");
                break;
            }

            var type = module?.Type?.Trim();
            if (module is null || string.IsNullOrWhiteSpace(type))
            {
                warnings.Add("A module with no type was dropped.");
                continue;
            }

            if (!DcRigTargetCatalog.ModuleTypeIds.Contains(type))
            {
                warnings.Add($"\"{type}\" is not a module DotCharter has; it was dropped.");
                continue;
            }

            if (AiGearNotes.NotDesignable.Contains(type))
            {
                warnings.Add($"\"{type}\" is Rocksmith's own amp chain, which only the tone converter " +
                             "writes; it was dropped.");
                continue;
            }

            if (AiGearNotes.SeriesExcluded.Contains(type))
            {
                warnings.Add($"\"{type}\" only works inside a parallel split, which this " +
                             "designer does not build yet; it was dropped.");
                continue;
            }

            // The capture is the one field whose legal values live outside this
            // build: they are the player's own library. Resolve it to a FILE NAME
            // here and let DcRigPresetPreparation turn that into the absolute path
            // the native module needs — the same two steps a factory capture preset
            // goes through, rather than a second path-resolution rule.
            var requested = module.Capture?.Trim() ?? "";
            IReadOnlyDictionary<string, string>? stringParams = null;

            if (string.Equals(type, AiGearNotes.NamAmpId, StringComparison.Ordinal))
            {
                if (requested.Length == 0)
                {
                    warnings.Add($"\"{type}\" plays a neural capture and the reply named none; " +
                                 "it was dropped.");
                    continue;
                }

                var option = captures?.Find(requested);
                if (option is null)
                {
                    warnings.Add($"\"{requested}\" is not a capture in this library, so " +
                                 $"\"{type}\" was dropped.");
                    continue;
                }

                stringParams = new Dictionary<string, string>(StringComparer.Ordinal)
                {
                    ["modelPath"] = option.FileName
                };
            }
            else if (requested.Length > 0)
            {
                warnings.Add($"\"{type}\" plays no capture; \"{requested}\" was ignored.");
            }

            modules.Add(new DcRigModule(type, ValidateParams(type, module.Params, warnings), module.Bypassed)
            {
                StringParams = stringParams
            });
        }

        return modules;
    }

    private static Dictionary<string, float> ValidateParams(
        string moduleTypeId, IReadOnlyDictionary<string, float>? drafted, List<string> warnings)
    {
        var values = new Dictionary<string, float>(StringComparer.Ordinal);
        if (drafted is null) return values;

        foreach (var (paramId, raw) in drafted)
        {
            if (string.IsNullOrWhiteSpace(paramId)) continue;

            var id = paramId.Trim();
            if (!DcRigTargetCatalog.TryGetParam(moduleTypeId, id, out var spec))
            {
                warnings.Add($"{moduleTypeId} has no \"{id}\" knob; it was ignored.");
                continue;
            }

            if (!float.IsFinite(raw))
            {
                warnings.Add($"{moduleTypeId}.{id} was not a number; the default " +
                             $"{ToneDesignNumber.Format(spec.Default)} was used.");
                values[id] = spec.Default;
                continue;
            }

            var clamped = Math.Clamp(raw, spec.Min, spec.Max);
            if (clamped != raw)
            {
                warnings.Add($"{moduleTypeId}.{id} {ToneDesignNumber.Format(raw)} is outside " +
                             $"{ToneDesignNumber.Format(spec.Min)}..{ToneDesignNumber.Format(spec.Max)}; it was set to {ToneDesignNumber.Format(clamped)}.");
            }

            values[id] = clamped;
        }

        return values;
    }

    /// <summary>
    ///     The closest <see cref="DspToneCategory" /> for a draft, matched on
    ///     keywords in its name and rationale and then on what the chain contains.
    ///     Falls back to <see cref="DspToneCategory.Clean" /> — a neutral bucket,
    ///     and the first tab in the picker, so a mis-categorised tone is still the
    ///     one the user is looking at.
    /// </summary>
    internal static DspToneCategory CategoryFor(ToneDraft draft)
    {
        var text = $"{draft.Name} {draft.Rationale}".ToLowerInvariant();
        var types = new HashSet<string>(
            (draft.Modules ?? []).Where(m => m?.Type is not null).Select(m => m.Type),
            StringComparer.Ordinal);

        // The chain is checked FIRST for bass and acoustic because those two are
        // structural — a bassamp rig is a bass tone whatever the prose calls it.
        if (types.Contains("bassamp") || types.Contains("bassod") || types.Contains("basssynth"))
            return DspToneCategory.Bass;
        if (types.Contains("acpre") || types.Contains("acousticsim"))
            return DspToneCategory.Acoustic;

        if (Mentions(text, "bass")) return DspToneCategory.Bass;
        if (Mentions(text, "acoustic", "folk", "fingerstyle")) return DspToneCategory.Acoustic;
        if (Mentions(text, "metal", "djent", "thrash", "death", "high gain", "high-gain", "brutal", "chug"))
            return DspToneCategory.HighGain;
        if (Mentions(text, "lead", "solo", "shred", "legato", "sustain")) return DspToneCategory.Lead;
        if (Mentions(text, "ambient", "pad", "shimmer", "wash", "atmospher", "cinematic"))
            return DspToneCategory.Ambient;
        if (Mentions(text, "crunch", "overdriv", "blues", "rock", "grit", "breakup"))
            return DspToneCategory.Crunch;
        if (Mentions(text, "clean", "sparkle", "chime", "jazz", "funk")) return DspToneCategory.Clean;
        if (Mentions(text, "experiment", "glitch", "weird", "broken", "chaos"))
            return DspToneCategory.Experimental;

        if (types.Contains("distortion") || types.Contains("hm2") || types.Contains("bigmuff"))
            return DspToneCategory.HighGain;
        if (types.Contains("overdrive") || types.Contains("rat") || types.Contains("ds1") ||
            types.Contains("fuzz") || types.Contains("klon"))
        {
            return DspToneCategory.Crunch;
        }

        return DspToneCategory.Clean;
    }

    private static bool Mentions(string text, params string[] needles)
        => needles.Any(n => text.Contains(n, StringComparison.Ordinal));
}
