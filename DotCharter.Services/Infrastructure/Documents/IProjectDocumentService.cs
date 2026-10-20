namespace DotCharter.Services.Infrastructure.Documents;

public interface IProjectDocumentService
{
    ChartProject LoadChart(string filePath);

    /// <summary>Serializes <paramref name="project" /> to <paramref name="filePath" />.</summary>
    /// <param name="filePath">Destination file; the serializer is chosen by its extension.</param>
    /// <param name="project">The project graph to write.</param>
    /// <param name="audioLengthMs">
    ///     Length of the loaded audio in milliseconds, or <see langword="null" /> when
    ///     the caller has no audio (or does not know its length). Only formats with a
    ///     song-length field consume it — see
    ///     <see cref="DotCharter.Services.Parsing.IChartSerializer.Save" />.
    /// </param>
    void SaveChart(string filePath, ChartProject project, double? audioLengthMs = null);

    void RefreshArrangementFileManifest(ChartProject project);

    /// <summary>Writes the DLC-Builder-importable <c>DTRS XML</c> bundle next to the project.</summary>
    /// <param name="projectPath">The project file the bundle is written beside.</param>
    /// <param name="project">The project graph to export.</param>
    /// <param name="audioLengthMs">
    ///     Length of the loaded audio in milliseconds, or <see langword="null" /> when
    ///     the caller has none. Sets each arrangement's <c>songLength</c> to EOF's
    ///     <c>min(chart length, audio length) - 1 ms</c>; without it the chart's own
    ///     length is used (EOF's no-audio-loaded branch).
    /// </param>
    void SaveDlcBuilderXmlBundle(string projectPath, ChartProject project, double? audioLengthMs = null);

    ChartProject AssembleProject(ChartProject? baseProject, double bpm, string audioFileName,
        IEnumerable<LyricPhrase> lyrics);

    ChartProject CreateNewProject(string title, string artist, string audioFileName);
}
