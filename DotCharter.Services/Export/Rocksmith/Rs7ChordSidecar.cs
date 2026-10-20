namespace DotCharter.Services.Export.Rocksmith;

/// <summary>
/// The <c>&lt;package&gt;.chords-ext.json</c> sidecar: chord notes on the 7th/8th
///     string, which the six-wide SNG chord records cannot hold, in <c>rs7chart</c>'s
///     format (<c>format: rs7chart.chords-ext</c>, version 1): the SNG body's SHA-256, the
///     templates with every column, and one event per projected chord keyed by (level,
///     note index).
///     <para>
///         <b>Read only, for packages built before the RS7X block</b>
///         (<see cref="Sng.Model.RsExtensionBlock" />), which now carries those columns
/// inside the SNG and is what the game reads. This half names the files and
///         lets an export remove the ones an older build of the same package left
///         (<see cref="RemoveStale" />); reading them is the package reader's:
///         a sidecar is applied ONLY when its
///         digest matches the SNG it sits beside, and only when the SNG has no valid block.
///     </para>
///     <para>
///         One arrangement per file: <c>&lt;base&gt;.chords-ext.json</c> (what <c>rs7chart</c>
///         names it), or with several, <c>&lt;base&gt;.&lt;partition&gt;.chords-ext.json</c>.
///         The reader takes both and matches each by its <c>sng</c> entry name.
///     </para>
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
    /// (the RS7X block replaced them) — only the exact names DotCharter
    ///     wrote for this package and its <paramref name="partitions" />, nothing else.
    /// </summary>
    public static void RemoveStale(string packagePath, IEnumerable<string> partitions)
    {
        IEnumerable<string> ours = [PathFor(packagePath, null), .. partitions.Select(p => PathFor(packagePath, p))];
        foreach (var stale in ours.Distinct(StringComparer.OrdinalIgnoreCase).Where(File.Exists))
            BestEffortDelete.File(stale);
    }
}
