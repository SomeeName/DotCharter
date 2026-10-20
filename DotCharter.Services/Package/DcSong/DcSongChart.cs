namespace DotCharter.Services.Package.DcSong;

/// <summary>
///     The chart a package carries: the project, cloned, with every FILE REFERENCE
///     pointed into the package. A project names files on its author's machine — the
///     audio, the cover art (often an absolute path under the user's profile), the
///     stems, the legacy sibling arrangement files — and none of those travel. In the
///     package they would be dead links at best and a leak of the author's folder
///     layout at worst. Everything else is the project, value for value.
/// </summary>
public static class DcSongChart
{
    /// <param name="project">The project; never modified.</param>
    /// <param name="audioPath">The package member holding the song audio.</param>
    /// <param name="coverPath">The package member holding the cover, or null.</param>
    /// <param name="stemPaths">Each packaged stem's name and member, in package order.</param>
    /// <param name="keepArrangements">Indexes into <see cref="ChartProject.Arrangements" /> the
    /// package carries; null carries every arrangement.</param>
    public static ChartProject ForPackage(
        ChartProject project, string audioPath, string? coverPath,
        IReadOnlyList<(string Name, string Path)> stemPaths, IReadOnlyCollection<int>? keepArrangements = null)
    {
        ArgumentNullException.ThrowIfNull(project);
        var packaged = ChartProjectCloner.Clone(project);
        if (keepArrangements != null)
        {
            for (var i = packaged.Arrangements.Count - 1; i >= 0; i--)
            {
                if (!keepArrangements.Contains(i)) packaged.Arrangements.RemoveAt(i);
            }
        }

        packaged.AudioFileName = audioPath;
        packaged.Chart.CoverArtPath = coverPath ?? "";

        // A stem keeps its mixer state (volume, offset, mute, solo) when the package
        // carries it, under its package path; a project stem the package does not
        // carry is dropped, and a packaged stem the project never listed is added.
        var byName = packaged.Stems.GroupBy(s => s.Name, StringComparer.Ordinal)
            .ToDictionary(g => g.Key, g => g.First(), StringComparer.Ordinal);
        packaged.Stems.Clear();
        foreach (var (name, path) in stemPaths)
        {
            var stem = byName.TryGetValue(name, out var existing) ? existing : new Stem { Name = name };
            stem.Path = path;
            stem.Local = true;
            packaged.Stems.Add(stem);
        }

        // Pre-v3 sibling arrangement files: the arrangements are embedded, and the
        // reader refuses a chart that asks to recover from files outside the package.
        packaged.ArrangementFiles.Clear();
        return packaged;
    }
}
