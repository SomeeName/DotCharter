using ChartProject = DotCharter.Core.Domain.Project.ChartProject;

namespace DotCharter.Services.Parsing;

public interface IChartSerializer
{
    ChartProject Load(string filePath);

    /// <summary>
    ///     Writes <paramref name="project" /> to <paramref name="filePath" />.
    /// </summary>
    /// <param name="filePath">Destination file. The format is the implementation's own.</param>
    /// <param name="project">The project graph to serialize.</param>
    /// <param name="audioLengthMs">
    ///     Length of the song's audio in milliseconds, when the caller knows it
    ///     (it lives on <c>ProjectState.TotalAudioLengthMs</c>, which
    ///     <see cref="ChartProject" /> deliberately does not carry). Formats that
    ///     stamp a song length use it for EOF's
    ///     <c>min(chart length, audio length) - 1 ms</c> rule; passing
    ///     <see langword="null" /> (no audio loaded, or a caller that only has a
    ///     file on disk) falls back to the chart's own length. Formats with no
    ///     song-length field ignore it.
    /// </param>
    void Save(string filePath, ChartProject project, double? audioLengthMs = null);
}
