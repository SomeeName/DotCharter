using DotCharter.Services.Infrastructure.Documents;
using DotCharter.Services.Parsing.Rscp;

namespace DotCharter.Services.Export.DlcBuilder;

/// <summary>What a DLC Builder project references that is not in the chart: the audio, the cover art and the audio's length.</summary>
public sealed record DlcBuilderProjectSources(string AudioPath, string CoverArtPath, double? AudioLengthMs);

/// <summary>
///     The export dialog's "Export DLC Builder project": the Rocksmith XML bundle and the
///     <c>.rs2dlc</c> that names it, written on request into a folder the user picked.
/// </summary>
public interface IDlcBuilderProjectWriter
{
    /// <summary>
    ///     Writes <c>DTRS XML/</c> and the <c>.rs2dlc</c> into <paramref name="folder" />, replacing the ones
    ///     already there, and returns the <c>.rs2dlc</c> path. <paramref name="snapshot" /> is a private
    ///     copy of the chart: its arrangement-file manifest is refreshed. Throws
    ///     <see cref="PlayOnlyContentException" /> for a play-only chart.
    /// </summary>
    string Write(string folder, ChartProject snapshot, DlcBuilderProjectSources sources);
}

/// <summary>
///     Unlike the save companion, this is a request: it ignores the companion setting and replaces an
///     existing project file instead of leaving one the charter edited in DLC Builder alone.
/// </summary>
public sealed class DlcBuilderProjectWriter(IProjectDocumentService documents, IConfigurationService config, IAppLogger logger)
    : IDlcBuilderProjectWriter
{
    public string Write(string folder, ChartProject snapshot, DlcBuilderProjectSources sources)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(folder);
        ArgumentNullException.ThrowIfNull(snapshot);
        ArgumentNullException.ThrowIfNull(sources);
        PlayOnlyExportGuard.ThrowIfPlayOnly(snapshot, nameof(DlcBuilderProjectWriter));

        var projectPath = ProjectFiles.Beside(DlcBuilderProjectFile.PathIn(folder, snapshot));
        var path = DlcBuilderProjectFile.PathIn(folder, snapshot);
        // The bundle writer names its folder after the project file it is given.
        var bundle = Path.Combine(folder, DlcBuilderBundle.DirectoryName);
        documents.SaveDlcBuilderXmlBundleToDirectory(bundle, snapshot, sources.AudioLengthMs);
        if (!Directory.Exists(bundle))
            throw new IOException($"The '{DlcBuilderBundle.DirectoryName}' folder was not written in '{folder}'.");

        var settings = config.LoadAppConfig();
        var media = new ExportCompanionMedia(logger).Ensure(projectPath, snapshot, sources.AudioLengthMs, settings,
            sources.AudioPath, sources.CoverArtPath, outputDirectory: folder);
        Rs2DlcWriter.WriteTo(path, DlcBuilderProjectFile.Build(settings, snapshot, folder, media.Audio,
            media.Cover, sources.AudioLengthMs));
        logger.Info(AppLogCategory.Project, $"DLC Builder project exported. Path='{path}'.");
        return path;
    }
}
