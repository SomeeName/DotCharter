using System.Xml;
using DotCharter.Services.Export;
using DotCharter.Services.Parsing.Xml.Showlights;
using DotCharter.Services.Parsing.Xml.Song;
using DotCharter.Services.Parsing.Xml.Vocals;

namespace DotCharter.Services.Parsing.Xml;

/// <summary>
/// RS-XML dispatcher. Detects format from the root element on Load,
/// and from project content on Save. Delegates to the appropriate
/// typed reader/writer.
/// </summary>
public class XmlChartSerializer : IChartSerializer
{
    private readonly IAppLogger? _logger;

    public XmlChartSerializer()
    {
    }

    public XmlChartSerializer(IAppLogger logger) => _logger = logger;

    /// <summary>
    ///     Save a 7-/8-string guitar's song XML as a six-string arrangement, for EOF and
    /// DLC Builder, which cannot read the spec XML's extra strings. Set by the
    ///     caller from <c>AppConfig.RsXmlSixStringProjection</c>; the serializer is a
    ///     transient, so it is per save.
    /// </summary>
    public bool SixStringProjection { get; set; }

    public ChartProject Load(string filePath)
    {
        using var _ = _logger?.BeginTimedOperation(AppLogCategory.Project, "XML Load", filePath);

        var doc = SafeXml.LoadFromFile(filePath);
        var rootName = doc.Root?.Name.LocalName ?? "";

        return rootName switch
        {
            "song" => new RsXmlSongReader { MergeSimultaneousNotes = true }.ReadDocument(doc, filePath),
            "vocals" => new RsXmlVocalsReader().ReadDocument(doc, filePath),
            "showlights" => new RsXmlShowlightsReader().ReadDocument(doc, filePath),
            _ => throw new InvalidDataException(
                $"Unknown RS-XML root element '{rootName}' in '{filePath}'. " +
                "Expected 'song', 'vocals', or 'showlights'.")
        };
    }

    /// <inheritdoc />
    /// <remarks>
    ///     Only the song document uses <c>audioLengthMs</c> — vocals and showlights
    ///     have no <c>songLength</c> element.
    /// </remarks>
    public void Save(string filePath, ChartProject project, double? audioLengthMs = null)
    {
        // Rocksmith XML of play-only content is never exported.
        PlayOnlyExportGuard.ThrowIfPlayOnly(project, "Rocksmith XML export");
        using var _ = _logger?.BeginTimedOperation(AppLogCategory.Project, "XML Save", filePath);

        var ext = Path.GetExtension(filePath).ToLowerInvariant();

        if (ext == ".xml")
        {
            // Detect by filename convention or content
            var fileName = Path.GetFileNameWithoutExtension(filePath).ToLowerInvariant();
            if (fileName.EndsWith("_showlights") || fileName.StartsWith("showlights"))
            {
                SaveShowlights(filePath, project);
                return;
            }

            if (fileName.EndsWith("_vocals") || fileName.StartsWith("vocals") ||
                project.VocalTracks.Count > 0 && project.Arrangements.Count == 0)
            {
                SaveVocals(filePath, project);
                return;
            }
        }

        // Default: song XML
        SaveSong(filePath, project, audioLengthMs);
    }

    /// <summary>Writes the RS2014 <c>&lt;song&gt;</c> document.</summary>
    /// <param name="filePath">Destination file.</param>
    /// <param name="project">The project to write.</param>
    /// <param name="audioLengthMs">
    ///     Audio length in milliseconds, or <see langword="null" /> when the caller
    ///     has none — see <see cref="RsXmlSongWriter.Write(ChartProject, double?)" />.
    /// </param>
    private void SaveSong(string filePath, ChartProject project, double? audioLengthMs)
    {
        var writer = new RsXmlSongWriter { SixStringProjection = SixStringProjection };
        var doc = writer.Write(project, audioLengthMs);
        using var xw = XmlWriter.Create(filePath, Fmt.GetWriterSettings());
        doc.WriteTo(xw);
        if (writer.LastLoss is { IsEmpty: false } loss && project.Arrangements.Count > 0)
            _logger?.Warning(AppLogCategory.Project, loss.Describe(project.Arrangements[0]));
    }

    private static void SaveVocals(string filePath, ChartProject project) => new RsXmlVocalsWriter().WriteTo(project, filePath);

    private static void SaveShowlights(string filePath, ChartProject project) => new RsXmlShowlightsWriter().WriteTo(project, filePath);
}
