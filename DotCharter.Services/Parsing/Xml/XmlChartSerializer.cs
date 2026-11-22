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


    public Action<string>? Warning { get; set; }

    /// <summary>
    ///     Write a phrase that starts on a note between two beats as moveR&lt;N&gt; on the beat
    ///     before it. Set by the caller from <c>AppConfig.RsXmlMoverPhrases</c>.
    /// </summary>
    public bool MoverPhrases { get; set; }

    /// <summary>The caller's double-stop naming policy for the song XML.</summary>
    public DoubleStopNameOptions ChordNameOptions { get; set; } = DoubleStopNameOptions.Default;

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
        => Save(filePath, project, GetDocumentKind(filePath, project), audioLengthMs);

    /// <summary>The XML document selected from the destination before staging.</summary>
    public enum DocumentKind { Song, Vocals, Showlights }

    /// <summary>Resolves the destination's filename convention or project content.</summary>
    public static DocumentKind GetDocumentKind(string destinationPath, ChartProject project)
    {
        var ext = Path.GetExtension(destinationPath).ToLowerInvariant();

        if (ext == ".xml")
        {
            // Detect by filename convention or content
            var fileName = Path.GetFileNameWithoutExtension(destinationPath).ToLowerInvariant();
            if (fileName.EndsWith("_showlights") || fileName.StartsWith("showlights"))
                return DocumentKind.Showlights;

            if (fileName.EndsWith("_vocals") || fileName.StartsWith("vocals") ||
                project.VocalTracks.Count > 0 && project.Arrangements.Count == 0)
            {
                return DocumentKind.Vocals;
            }
        }

        return DocumentKind.Song;
    }

    /// <summary>Writes the selected document; a main document must fail when its arrangement is refused.</summary>
    public void Save(string filePath, ChartProject project, DocumentKind kind, double? audioLengthMs = null,
        bool failOnRefusal = false)
    {
        PlayOnlyExportGuard.ThrowIfPlayOnly(project, "Rocksmith XML export");
        using var _ = _logger?.BeginTimedOperation(AppLogCategory.Project, "XML Save", filePath);
        if (kind == DocumentKind.Showlights) SaveShowlights(filePath, project);
        else if (kind == DocumentKind.Vocals) SaveVocals(filePath, project);
        else SaveSong(filePath, project, audioLengthMs, failOnRefusal);
    }

    /// <summary>Writes the RS2014 <c>&lt;song&gt;</c> document.</summary>
    /// <param name="filePath">Destination file.</param>
    /// <param name="project">The project to write.</param>
    /// <param name="audioLengthMs">
    ///     Audio length in milliseconds, or <see langword="null" /> when the caller
    ///     has none — see <see cref="RsXmlSongWriter.Write(ChartProject, double?)" />.
    /// </param>
    /// <param name="failOnRefusal">Throw for the main document; companion exports skip a refused arrangement.</param>
    private void SaveSong(string filePath, ChartProject project, double? audioLengthMs, bool failOnRefusal)
    {
        var writer = new RsXmlSongWriter
        {
            SixStringProjection = SixStringProjection, MoverPhrases = MoverPhrases, ChordNameOptions = ChordNameOptions,
            Warning = Warning ?? (message => _logger?.Warning(AppLogCategory.Project, message))
        };
        var doc = failOnRefusal ? writer.Write(project, audioLengthMs) : writer.TryWrite(project, audioLengthMs);
        if (doc == null) return;
        using var xw = XmlWriter.Create(filePath, Fmt.GetWriterSettings());
        doc.WriteTo(xw);
        if (writer.LastLoss is { IsEmpty: false } loss && project.Arrangements.Count > 0)
            _logger?.Warning(AppLogCategory.Project, loss.Describe(project.Arrangements[0]));
    }

    private static void SaveVocals(string filePath, ChartProject project) => new RsXmlVocalsWriter().WriteTo(project, filePath);

    private static void SaveShowlights(string filePath, ChartProject project) => new RsXmlShowlightsWriter().WriteTo(project, filePath);
}
