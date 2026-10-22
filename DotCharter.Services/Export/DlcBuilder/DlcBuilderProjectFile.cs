namespace DotCharter.Services.Export.DlcBuilder;

/// <summary>
///     The <c>.rs2dlc</c> a folder gets for a chart, shared by the two writers of it: the save
///     companion (beside the <c>.dcproj</c>, only when its setting is on) and the export dialog's
///     "Export DLC Builder project" (a folder the user picked). One place decides the file name,
///     the vocals and charter fallbacks and how the audio and cover art are referenced.
/// </summary>
internal static class DlcBuilderProjectFile
{
    /// <summary>Where the project file of <paramref name="snapshot" /> goes inside <paramref name="directory" />.</summary>
    public static string PathIn(string directory, ChartProject snapshot) =>
        Path.Combine(directory, Rs2DlcProjectBuilder.FileNameFor(snapshot));

    /// <summary>
    ///     The project for <paramref name="snapshot" />, whose <c>ArrangementFiles</c> manifest the bundle
    ///     writer has already refreshed. The audio and cover art are named relative to
    ///     <paramref name="directory" /> when they sit inside it, absolute otherwise.
    /// </summary>
    public static Rs2DlcProjectDto Build(AppConfig config, ChartProject snapshot, string directory, string? audioPath,
        string? coverArtPath, double? audioLengthMs) =>
        Rs2DlcProjectBuilder.Build(snapshot, new Rs2DlcProjectBuilder.Rs2DlcBuildOptions(
            AudioFileName: RelativeToProject(directory, audioPath),
            CoverArtFileName: RelativeToProject(directory, coverArtPath),
            AudioLengthMs: audioLengthMs,
            FallbackCharter: config.DefaultCharterName,
            IncludeVocals: VocalsExportPolicy.IncludeVocals(config),
            SixStringProjection: config.RsXmlSixStringProjection));

    /// <summary>A resolved absolute path expressed the way the project stores it:
    /// a bare file name when it is inside the project folder, otherwise absolute.</summary>
    private static string RelativeToProject(string directory, string? absolutePath)
    {
        if (string.IsNullOrWhiteSpace(absolutePath)) return "";

        var parent = Path.GetDirectoryName(Path.GetFullPath(absolutePath));
        return string.Equals(parent, Path.GetFullPath(directory).TrimEnd(Path.DirectorySeparatorChar),
            StringComparison.OrdinalIgnoreCase)
            ? Path.GetFileName(absolutePath)
            : absolutePath;
    }
}
