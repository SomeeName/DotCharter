using DotCharter.Tone.Audio.Dsp.RsToneConversion;

namespace DotCharter.Tone.Audio.Dsp.Presets;

/// <summary>
///     The ONE preset→rig preparation pipeline, shared by the live preset
///     apply (<see cref="DspTonePresetService" />) and the offline take re-amp
///     so both prepare a preset IDENTICALLY:
///     <list type="number">
///     <item>Fill unspecified module params with their catalogue defaults — a
///     sparse preset must sound the same no matter which tone was loaded
///     before (<see cref="DcRigApplier" /> reuses same-type nodes in place, so
///     omitted params would otherwise inherit the previous tone's values).</item>
///     <item>Loudness-normalize FACTORY presets to the same absolute reference
///     the RS tone converter targets (<see cref="DcRigLoudnessNormalizer" />);
///     USER presets apply verbatim — a player's saved trailing gain is a
///     deliberate choice.</item>
///     <item>Resolve a namamp module's bare bundled-capture file name (e.g.
///     <c>fender-deluxe-reverb-clean.nam</c>) to an absolute path under the
///     NAM capture directory — the native module's contract is absolute
///     paths.</item>
///     </list>
///     Pure: never mutates the input rig, no I/O, no clocks.
/// </summary>
public static class DcRigPresetPreparation
{
    /// <summary>The fully prepared rig for <paramref name="preset" />.
    /// <paramref name="namCaptureDirectory" /> null/blank leaves modelPath
    /// values untouched (tests / no NAM library).</summary>
    public static DcRigPreset Prepare(DspTonePreset preset, string? namCaptureDirectory)
    {
        var rig = DcRigTargetCatalog.FillDefaults(preset.Rig);
        if (preset.Origin == DspToneOrigin.BuiltIn)
            rig = DcRigLoudnessNormalizer.Normalize(rig);
        return ResolveCaptureModelPaths(rig, namCaptureDirectory);
    }

    /// <summary>Rewrites a namamp module's bare bundled-capture file name into
    /// an absolute path under <paramref name="namCaptureDirectory" />. Only
    /// touches bare file names (no directory separator, not rooted); an
    /// already-absolute path, an empty value, or a null/blank directory passes
    /// through unchanged. The input rig is never mutated.</summary>
    public static DcRigPreset ResolveCaptureModelPaths(DcRigPreset rig, string? namCaptureDirectory)
    {
        if (string.IsNullOrWhiteSpace(namCaptureDirectory)) return rig;

        List<DcRigModule>? rewritten = null;
        for (var i = 0; i < rig.Modules.Count; i++)
        {
            var mod = rig.Modules[i];
            if (mod.StringParams is not { Count: > 0 } strings ||
                !strings.TryGetValue("modelPath", out var value) ||
                string.IsNullOrWhiteSpace(value))
            {
                continue;
            }

            // Already a path (rooted or contains a separator) — leave it.
            if (Path.IsPathRooted(value) || value.IndexOfAny(['/', '\\']) >= 0)
                continue;

            var absolute = Path.Combine(namCaptureDirectory, value);
            var newStrings = new Dictionary<string, string>(strings, StringComparer.Ordinal)
            {
                ["modelPath"] = absolute
            };
            rewritten ??= [.. rig.Modules];
            rewritten[i] = mod with { StringParams = newStrings };
        }

        return rewritten is null ? rig : rig with { Modules = rewritten };
    }
}
