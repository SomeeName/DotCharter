using DotCharter.Services.Audio.StemSeparation.GuitarRoles;

namespace DotCharter.Services.Audio.StemSeparation;

/// <summary>Result of <see cref="StemFolderProbe.Probe" />.</summary>
/// <param name="Recovered">Stems adopted from a `stems/` folder the project
/// does not reference (name + project-relative path), empty otherwise.</param>
/// <param name="Existing">Absolute paths that were found on disk.</param>
/// <param name="Resolved">Stored path → the absolute path it resolved to,
/// decided on the worker so the UI thread never has to touch the disk.</param>
/// <param name="GuitarPath">The guitar stem's file, when one is declared and on disk.</param>
/// <param name="RolePair">The current derived pair, when Guitar roles is on and it exists.</param>
public readonly record struct StemProbe(
    List<(string Name, string RelativePath)> Recovered,
    HashSet<string> Existing,
    Dictionary<string, string> Resolved,
    string? GuitarPath,
    GuitarRolePair? RolePair);

/// <summary>
///     The disk half of the stem mixer's rebuild: which declared stems exist,
///     where each stored path resolves to, and which WAVs an unreferenced
///     <c>stems/</c> folder holds. The mixer keeps the snapshot and the strips;
///     this does the touching, on the worker the mixer hands it.
/// </summary>
public static class StemFolderProbe
{
    /// <summary>
    ///     Pure filesystem probing. Runs on a worker thread — it touches nothing
    ///     but the snapshot it is handed.
    /// </summary>
    public static StemProbe Probe(string projectDir, string legacyDir, IReadOnlyList<Stem> declared,
        bool rolesWanted, IGuitarRoleStems? guitarRoleStems)
    {
        var recovered = new List<(string, string)>();
        var existing = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var resolved = new Dictionary<string, string>(StringComparer.Ordinal);

        // A `stems/` folder the project does not reference. It can sit beside
        // EITHER base — a saved project puts it beside the project, a PSARC or
        // rooted-audio session beside the audio — so look in both, project first.
        if (declared.Count == 0)
        {
            foreach (var baseDir in Bases(projectDir, legacyDir))
            {
                var stemsDir = Path.Combine(baseDir, "stems");
                if (!Directory.Exists(stemsDir)) continue;

                foreach (var wav in Directory.EnumerateFiles(stemsDir, "*.wav", SearchOption.TopDirectoryOnly)
                             .OrderBy(p => p, StringComparer.OrdinalIgnoreCase))
                {
                    var name = Path.GetFileNameWithoutExtension(wav);
                    if (string.IsNullOrWhiteSpace(name)) continue;

                    // Recovered names are always written PROJECT-relative, which
                    // is what the persist side does too — recovery must not
                    // reintroduce the base it is here to compensate for.
                    var stored = string.IsNullOrEmpty(projectDir)
                        ? wav
                        : Path.GetRelativePath(projectDir, wav);
                    recovered.Add((name, stored));
                    existing.Add(wav);
                    resolved[stored] = wav;
                }

                if (recovered.Count > 0) break;
            }
        }

        string? guitarPath = null;
        foreach (var s in declared)
        {
            var abs = ResolveStemPath(projectDir, legacyDir, s.Path);
            resolved[s.Path] = abs;
            if (string.IsNullOrWhiteSpace(abs) || !File.Exists(abs)) continue;
            existing.Add(abs);
            if (guitarPath == null && GuitarRoleStems.IsGuitarStem(s.Name)) guitarPath = abs;
        }

        // Recovered stems are named after their files, so a recovered "guitar.wav" counts too.
        guitarPath ??= recovered.Where(r => GuitarRoleStems.IsGuitarStem(r.Item1))
            .Select(r => resolved[r.Item2]).FirstOrDefault();

        var pair = rolesWanted && guitarPath != null ? guitarRoleStems?.TryGetDerived(guitarPath) : null;
        return new StemProbe(recovered, existing, resolved, guitarPath, pair);
    }

    /// <summary>
    ///     A stored stem path made absolute. The PROJECT directory is the base —
    ///     the one every relative name in the <c>.dcproj</c> uses — and the audio
    ///     directory is a read-only FALLBACK for files written against the audio
    ///     directory by older builds. Touches the disk (to pick between the two),
    ///     so it runs on the probe's worker and never on the UI thread. With
    ///     neither present it returns the CANONICAL location, so a "missing stem"
    ///     is reported where the file is supposed to be.
    /// </summary>
    public static string ResolveStemPath(string projectDir, string legacyDir, string path)
    {
        // Data from the .dcproj: a UNC, device or network-drive path is refused unprobed.
        if (!ProjectRelativePath.IsReadableMediaReference(path)) return string.Empty;
        if (Path.IsPathRooted(path)) return path;

        var canonical = ResolveAbsolute(projectDir, path);
        if (!string.IsNullOrWhiteSpace(canonical) && File.Exists(canonical)) return canonical;

        var legacy = ResolveAbsolute(legacyDir, path);
        if (!string.IsNullOrWhiteSpace(legacy) && File.Exists(legacy)) return legacy;

        return canonical;
    }

    private static IEnumerable<string> Bases(string projectDir, string legacyDir)
    {
        if (!string.IsNullOrEmpty(projectDir)) yield return projectDir;
        if (!string.IsNullOrEmpty(legacyDir) &&
            !string.Equals(legacyDir, projectDir, StringComparison.OrdinalIgnoreCase))
        {
            yield return legacyDir;
        }
    }

    private static string ResolveAbsolute(string baseDir, string path)
    {
        if (string.IsNullOrWhiteSpace(path)) return string.Empty;
        if (Path.IsPathRooted(path)) return path;
        return string.IsNullOrEmpty(baseDir) ? path : Path.Combine(baseDir, path);
    }
}
