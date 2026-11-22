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

    /// <summary>The string-param keys that name a file in the capture / IR library.</summary>
    private static readonly string[] FileParamKeys = ["modelPath", "irPath", "irPathB", "irFile"];

    /// <summary>Rewrites a module's bare library file name (a namamp
    /// <c>modelPath</c>, cab / acousticsim <c>irPath</c>, cab <c>irPathB</c>, or
    /// convolution-reverb <c>irFile</c>) into an absolute
    /// path under <paramref name="namCaptureDirectory" />. Touches bare names and
    /// library-relative paths with forward slashes (<c>Amps/foo.nam</c>); a rooted
    /// path, a path with a <c>..</c> segment, an empty value, or a null/blank
    /// directory passes through unchanged. The input rig is never mutated.</summary>
    public static DcRigPreset ResolveCaptureModelPaths(DcRigPreset rig, string? namCaptureDirectory)
        => MapFileParams(rig, namCaptureDirectory, (dir, value) =>
            IsPortableRelative(value) ? Path.Combine(dir, value.Replace('/', Path.DirectorySeparatorChar)) : null);

    /// <summary>A library-relative reference: not rooted, no <c>..</c> segment,
    /// so it can never leave the library folder.</summary>
    private static bool IsPortableRelative(string value)
        => !Path.IsPathRooted(value) && !value.Split('/', '\\').Any(segment => segment == "..");

    /// <summary>The inverse, for export and save: a file param that points at a
    /// file inside <paramref name="libraryDirectory" /> (directly or in a subfolder)
    /// becomes a relative path with forward slashes (<c>amp.nam</c>,
    /// <c>Amps/amp.nam</c>), so the tone carries no machine-specific path and still
    /// resolves on another install. A path outside the library is left as it is
    /// (see <see cref="FindNonPortableFiles" />). Never writes a <c>..</c> path.
    /// The input rig is never mutated.</summary>
    public static DcRigPreset MakeLibraryPathsPortable(DcRigPreset rig, string? libraryDirectory)
        => MapFileParams(rig, libraryDirectory, RelativeToLibrary);

    /// <summary>The file names whose params stay machine-specific after
    /// <see cref="MakeLibraryPathsPortable" />: rooted paths outside the library.</summary>
    public static IReadOnlyList<string> FindNonPortableFiles(DcRigPreset rig, string? libraryDirectory)
    {
        if (string.IsNullOrWhiteSpace(libraryDirectory)) return [];
        var found = new List<string>();
        foreach (var mod in rig.Modules)
        {
            if (mod.StringParams is not { Count: > 0 } strings) continue;
            foreach (var key in FileParamKeys)
            {
                if (!strings.TryGetValue(key, out var value) || string.IsNullOrWhiteSpace(value)) continue;
                if (Path.IsPathRooted(value) && RelativeToLibrary(libraryDirectory, value) is null)
                    found.Add(Path.GetFileName(value));
            }
        }

        return found;
    }

    private static string? RelativeToLibrary(string dir, string value)
    {
        if (!Path.IsPathRooted(value)) return null;
        var relative = Path.GetRelativePath(Path.GetFullPath(dir), Path.GetFullPath(value));
        if (Path.IsPathRooted(relative)) return null;
        var parts = relative.Split(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        return parts.Any(part => part == "..") ? null : string.Join('/', parts);
    }

    private static DcRigPreset MapFileParams(DcRigPreset rig, string? directory, Func<string, string, string?> map)
    {
        if (string.IsNullOrWhiteSpace(directory)) return rig;

        List<DcRigModule>? rewritten = null;
        for (var i = 0; i < rig.Modules.Count; i++)
        {
            var mod = rig.Modules[i];
            if (mod.StringParams is not { Count: > 0 } strings) continue;

            Dictionary<string, string>? changed = null;
            foreach (var key in FileParamKeys)
            {
                if (!strings.TryGetValue(key, out var value) || string.IsNullOrWhiteSpace(value)) continue;
                if (map(directory, value) is not { } mapped) continue;
                changed ??= new Dictionary<string, string>(strings, StringComparer.Ordinal);
                changed[key] = mapped;
            }

            if (changed is null) continue;
            rewritten ??= [.. rig.Modules];
            rewritten[i] = mod with { StringParams = changed };
        }

        return rewritten is null ? rig : rig with { Modules = rewritten };
    }
}
