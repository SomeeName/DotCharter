namespace DotCharter.Services.Export.Rocksmith;

/// <summary>
///     Names legacy chord sidecars for package readers and lets an export remove
///     the exact stale names belonging to its package and arrangement partitions.
///     RS Extended Range carries every chord column in its positional chart records.
/// </summary>
internal static class Rs7ChordSidecar
{
    public const string Format = "rs7chart.chords-ext";
    public const int Version = 1;
    public const string FileSuffix = ".chords-ext.json";

    /// <summary>The sidecar path for a package (and, when several arrangements need one,
    /// the arrangement's partition name).</summary>
    public static string PathFor(string packagePath, string? partition)
    {
        var directory = Path.GetDirectoryName(Path.GetFullPath(packagePath)) ?? "";
        var name = BaseName(packagePath);
        return Path.Combine(directory, partition == null ? name + FileSuffix : $"{name}.{partition}{FileSuffix}");
    }

    internal static string BaseName(string packagePath)
    {
        var name = Path.GetFileName(packagePath);
        return name.EndsWith(".psarc", StringComparison.OrdinalIgnoreCase) ? name[..^".psarc".Length] : name;
    }

    /// <summary>
    ///     Deletes the sidecars an earlier build of <paramref name="packagePath" /> left
    /// — only the exact names DotCharter
    ///     wrote for this package and its <paramref name="partitions" />, nothing else.
    /// </summary>
    public static void RemoveStale(string packagePath, IEnumerable<string> partitions)
    {
        IEnumerable<string> ours = [PathFor(packagePath, null), .. partitions.Select(p => PathFor(packagePath, p))];
        foreach (var stale in ours.Distinct(StringComparer.OrdinalIgnoreCase).Where(File.Exists))
            BestEffortDelete.File(stale);
    }
}
