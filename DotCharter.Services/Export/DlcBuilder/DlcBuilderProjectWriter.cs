using DotCharter.Services.Infrastructure.Documents;
using DotCharter.Services.Parsing.Rscp;

namespace DotCharter.Services.Export.DlcBuilder;

/// <summary>
///     What a DLC Builder project references that is not in the chart: the audio, the cover art and the audio's
///     length; and the export dialog's low bass tuning box, which the bundle's XML and the project both follow.
/// </summary>
public sealed record DlcBuilderProjectSources(string AudioPath, string CoverArtPath, double? AudioLengthMs, bool FixLowBassTuning);

/// <summary>
///     The project files an export wrote: the one a stock DLC Builder builds, and the RS Extended Range one
///     (<c>_rsxr</c>) when the chart has 7-/8-string guitar or 5-/6-string bass paths, null otherwise.
/// </summary>
public sealed record DlcBuilderProjectPaths(string Project, string? ExtendedRangeProject);

/// <summary>
///     The export dialog's "Export DLC Builder project": the Rocksmith XML bundle and the
///     <c>.rs2dlc</c> that names it, written on request into a folder the user picked.
/// </summary>
public interface IDlcBuilderProjectWriter
{
    /// <summary>
    ///     Writes <c>DTRS XML/</c> and the <c>.rs2dlc</c> into <paramref name="folder" />, replacing the ones
    ///     already there, and returns the <c>.rs2dlc</c> paths: a chart with RS Extended Range paths gets a
    ///     standard project and an <c>_rsxr</c> one (<see cref="Rs2DlcEditions" />). <paramref name="snapshot" /> is a private
    ///     copy of the chart: its arrangement-file manifest is refreshed. Throws
    ///     <see cref="PlayOnlyContentException" /> for a play-only chart.
    /// </summary>
    DlcBuilderProjectPaths Write(string folder, ChartProject snapshot, DlcBuilderProjectSources sources);
}

/// <summary>
///     Unlike the save companion, this is a request: it ignores the companion setting and replaces an
///     existing project file instead of leaving one the charter edited in DLC Builder alone.
/// </summary>
public sealed class DlcBuilderProjectWriter(IProjectDocumentService documents, IConfigurationService config, IAppLogger logger)
    : IDlcBuilderProjectWriter
{
    public DlcBuilderProjectPaths Write(string folder, ChartProject snapshot, DlcBuilderProjectSources sources)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(folder);
        ArgumentNullException.ThrowIfNull(snapshot);
        ArgumentNullException.ThrowIfNull(sources);
        PlayOnlyExportGuard.ThrowIfPlayOnly(snapshot, nameof(DlcBuilderProjectWriter));

        var projectPath = ProjectFiles.Beside(DlcBuilderProjectFile.PathIn(folder, snapshot));
        var path = DlcBuilderProjectFile.PathIn(folder, snapshot);
        // The bundle writer names its folder after the project file it is given.
        var bundle = Path.Combine(folder, DlcBuilderBundle.DirectoryName);
        documents.SaveDlcBuilderXmlBundleToDirectory(bundle, snapshot, sources.AudioLengthMs, sources.FixLowBassTuning);
        if (!Directory.Exists(bundle))
            throw new IOException($"The '{DlcBuilderBundle.DirectoryName}' folder was not written in '{folder}'.");

        var settings = config.LoadAppConfig();
        var media = new ExportCompanionMedia(logger).Ensure(projectPath, snapshot, sources.AudioLengthMs, settings,
            sources.AudioPath, sources.CoverArtPath, outputDirectory: folder);
        string? extendedRange = null;
        foreach (var edition in Rs2DlcEditions.For(snapshot))
        {
            var target = Rs2DlcEditions.PathFor(path, edition);
            Rs2DlcWriter.WriteTo(target, DlcBuilderProjectFile.Build(settings, snapshot, folder, media.Audio,
                media.Cover, sources.AudioLengthMs, sources.FixLowBassTuning, edition));
            if (edition == Rs2DlcEdition.ExtendedRange) extendedRange = target;
            logger.Info(AppLogCategory.Project, $"DLC Builder project exported. Edition={edition}, Path='{target}'.");
        }
        if (Rs2DlcEditions.RemoveStaleExtendedRangeProject(snapshot, path, RecycleBin.Remove))
            logger.Info(AppLogCategory.Project, $"Removed the stale RS Extended Range .rs2dlc beside '{path}'.");
        return new DlcBuilderProjectPaths(path, extendedRange);
    }
}
