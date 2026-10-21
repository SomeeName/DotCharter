using System.Globalization;
using System.Xml;
using System.IO.Compression;
using System.Xml.Linq;
using DotCharter.Services.Infrastructure;
using Arrangement = DotCharter.Core.Domain.Project.Arrangement;

namespace DotCharter.Services.Parsing.GuitarPro;

/// <summary>
///     Imports Guitar Pro score files. Handles the modern XML-based containers — GP7+
///     <c>.gp</c> (a zip) and GP6 <c>.gpx</c> (BCFZ/BCFS, <see cref="GpxContainer"/>) —
///     directly and dispatches the legacy
///     binary formats (<c>.gp3</c>, <c>.gp4</c>, <c>.gp5</c>) to
///     <see cref="GpLegacyImporter"/>. Either path produces the same
///     <see cref="GpImportResult"/> shape, after which the shared
///     post-processing collaborators run:
///     <list type="bullet">
///         <item><see cref="GpMetadataParser"/> — time signatures, key, tempo map (XML path)</item>
///         <item><see cref="GpArrangementParser"/> — per-track bars / voices / notes (XML path)</item>
///         <item><see cref="GpLegacyImporter"/> — full parse for binary GP3/4/5</item>
///         <item><see cref="GpPostProcessor"/> — tied-note merging, slide linking, sustain adjustments</item>
///         <item><see cref="DotCharter.Services.Editing.FretAnchors.FretAnchorGenerationService"/> — infers anchors from left-hand fingers</item>
///     </list>
/// </summary>
public sealed class GpImporter
{
    public List<GpTrackInfo> GetAvailableTracks(string gpFilePath)
    {
        if (IsLegacyBinaryFormat(gpFilePath))
            return new GpLegacyImporter().GetTracks(gpFilePath);

        if (GpxContainer.IsGpx(gpFilePath))
        {
            var score = GpxContainer.ReadScore(gpFilePath);
            if (score == null) return [];
            using var gpxStream = new MemoryStream(score, writable: false);
            return ReadTracks(gpxStream);
        }

        using var archive = OpenArchive(gpFilePath, ImportSizeLimits.GpArchiveMaxEntries);
        var scoreEntry = FindScoreEntry(archive);
        if (scoreEntry == null) return [];

        using var stream = OpenScoreEntry(scoreEntry, ImportSizeLimits.GpScoreEntryBytes);
        return ReadTracks(stream);
    }

    /// <summary>
    ///     The track list out of a <c>score.gpif</c> STREAM, without
    ///     materialising the document.
    /// </summary>
    /// <remarks>
    ///     This populates the import dialog's track picker, and it must not build
    ///     the whole DOM — every bar, voice, beat, note and rhythm of the score —
    ///     to read a handful of <c>&lt;Track&gt;&lt;Name&gt;</c> elements, and
    ///     then throw it away so that <see cref="ImportTracksWithMetadata" />
    ///     would inflate and parse the same document again when the user presses
    ///     OK. GPIF writes <c>&lt;Tracks&gt;</c> BEFORE the bulk sections, so a
    ///     streaming read stops within the first fraction of the file and the
    ///     rest is never inflated at all. Same hardened reader settings as the
    ///     DOM path (<see cref="SafeXml" />) — the difference is how much of the
    ///     file is touched, exactly as <c>RscpGenreReader</c> does for the
    ///     project header.
    /// </remarks>
    internal static List<GpTrackInfo> ReadTracks(Stream stream)
    {
        var tracks = new List<GpTrackInfo>();
        using var reader = SafeXml.CreateReader(stream);

        while (reader.Read())
        {
            // Depth 1 = a DIRECT child of the root, which is what the DOM path's
            // `doc.Root.Element("Tracks")` meant.
            if (reader.NodeType != XmlNodeType.Element || reader.Depth != 1 ||
                !string.Equals(reader.Name, "Tracks", StringComparison.Ordinal))
            {
                continue;
            }

            if (reader.IsEmptyElement) return tracks;

            var trackDepth = reader.Depth + 1;
            while (reader.Read())
            {
                if (reader.NodeType == XmlNodeType.EndElement && reader.Depth < trackDepth) break;
                if (reader.NodeType != XmlNodeType.Element || reader.Depth != trackDepth ||
                    !string.Equals(reader.Name, "Track", StringComparison.Ordinal))
                {
                    continue;
                }

                var idText = reader.GetAttribute("id");
                var isEmpty = reader.IsEmptyElement;
                if (!int.TryParse(idText, CultureInfo.InvariantCulture, out var id))
                {
                    if (!isEmpty) reader.Skip();
                    continue;
                }

                // Normalized at the read, like GpArrangementParser — real GP
                // files carry doubled spaces in their bytes, and the import
                // dialog matches tracks to arrangements BY NAME.
                var name = GpTrackNames.Normalize(isEmpty ? null : ReadFirstChildName(reader, trackDepth + 1));
                tracks.Add(new GpTrackInfo
                {
                    Id = id,
                    Name = name.Length > 0 ? name : $"Track {id}"
                });
            }

            // <Tracks> is written once; everything past it is score bulk.
            return tracks;
        }

        return tracks;
    }

    /// <summary>Reads a positioned <c>&lt;Track&gt;</c> to its end, returning its
    /// first direct <c>&lt;Name&gt;</c> child's text (the DOM path's
    /// <c>Element("Name")</c>), or null.</summary>
    private static string? ReadFirstChildName(XmlReader reader, int childDepth)
    {
        string? name = null;
        while (reader.Read())
        {
            if (reader.NodeType == XmlNodeType.EndElement && reader.Depth < childDepth) return name;
            if (reader.NodeType != XmlNodeType.Element || reader.Depth != childDepth ||
                !string.Equals(reader.Name, "Name", StringComparison.Ordinal))
            {
                continue;
            }

            if (reader.IsEmptyElement)
            {
                name ??= "";
                continue;
            }

            // ReadElementContentAsString consumes the element and leaves the
            // reader on the node AFTER it, so the depth guards above still hold.
            var text = reader.ReadElementContentAsString();
            name ??= text;
            if (reader.NodeType == XmlNodeType.EndElement && reader.Depth < childDepth) return name;
        }

        return name;
    }

    public List<Arrangement> ImportTracks(string gpFilePath, List<int> trackIdsToImport,
        GpImportSettings? settings = null) => ImportTracksWithMetadata(gpFilePath, trackIdsToImport, settings).Arrangements;

    public GpImportResult ImportTracksWithMetadata(string gpFilePath, List<int> trackIdsToImport,
        GpImportSettings? settings = null)
    {
        settings ??= GpImportSettings.Default;
        if (IsLegacyBinaryFormat(gpFilePath))
        {
            var legacy = new GpLegacyImporter().Import(gpFilePath, trackIdsToImport, settings);
            GpMeterGrid.ToBeatGrid(legacy, settings.ImportTimeSignatures);
            return legacy;
        }

        var doc = LoadScoreDocument(gpFilePath);
        if (doc == null) return new GpImportResult();

        var result = new GpImportResult();
        var arrangementParser = new GpArrangementParser();

        result.Arrangements.AddRange(arrangementParser.Parse(doc, trackIdsToImport, settings));
        foreach (var (arrangement, slideIns) in arrangementParser.SlideInsMaterialized)
            result.SlideInsMaterialized[arrangement] = slideIns;
        GpMetadataParser.Populate(doc, result.Metadata);
        GpMeterGrid.ToBeatGrid(result, settings.ImportTimeSignatures);
        return result;
    }

    private static bool IsLegacyBinaryFormat(string gpFilePath)
    {
        var ext = Path.GetExtension(gpFilePath);
        if (string.Equals(ext, ".gp3", StringComparison.OrdinalIgnoreCase) ||
            string.Equals(ext, ".gp4", StringComparison.OrdinalIgnoreCase) ||
            string.Equals(ext, ".gp5", StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

        // .gp/.gpx files are zip archives; legacy binaries are not. Sniff the
        // first byte (which encodes the version-string length, ~22-25 for the
        // supported GP3/4/5 versions) and confirm with the "FICHIER" prefix.
        try
        {
            using var stream = File.OpenRead(gpFilePath);
            Span<byte> head = stackalloc byte[8];
            var read = stream.Read(head);
            if (read < 8) return false;
            return head[0] >= 20 && head[0] <= 30 &&
                   head[1] == (byte)'F' && head[2] == (byte)'I' && head[3] == (byte)'C' &&
                   head[4] == (byte)'H' && head[5] == (byte)'I' && head[6] == (byte)'E' && head[7] == (byte)'R';
        }
        catch
        {
            return false;
        }
    }

    private static ZipArchiveEntry? FindScoreEntry(ZipArchive archive) =>
        archive.GetEntry("content/score.gpif") ??
        archive.Entries.FirstOrDefault(e =>
            e.FullName.EndsWith("score.gpif", StringComparison.OrdinalIgnoreCase));

    /// <summary>The <c>score.gpif</c> of a GP6 (<c>.gpx</c>) or GP7+ (<c>.gp</c>) file, or
    /// null when the container holds none. Throws <see cref="InvalidDataException" /> for
    /// a file that is neither container.</summary>
    internal static XDocument? LoadScoreDocument(string gpFilePath)
        => LoadScoreDocument(gpFilePath, ImportSizeLimits.GpScoreEntryBytes, ImportSizeLimits.GpArchiveMaxEntries);

    /// <summary>The same, with the limits a caller (a test) chooses.</summary>
    internal static XDocument? LoadScoreDocument(string gpFilePath, long maxScoreBytes, int maxEntries)
    {
        if (GpxContainer.IsGpx(gpFilePath))
        {
            var score = GpxContainer.ReadScore(gpFilePath);
            if (score == null) return null;
            using var gpxStream = new MemoryStream(score, writable: false);
            return SafeXml.LoadFromStream(gpxStream);
        }

        using var archive = OpenArchive(gpFilePath, maxEntries);
        var scoreEntry = FindScoreEntry(archive);
        if (scoreEntry == null) return null;

        using var stream = OpenScoreEntry(scoreEntry, maxScoreBytes);
        return SafeXml.LoadFromStream(stream);
    }

    /// <summary>
    ///     Opens a <c>.gp</c> after bounding its member count from the
    ///     end-of-central-directory record, so a hostile archive of millions of
    ///     empty members is refused BEFORE one entry object is built. A file with
    ///     no readable record falls through to <see cref="ZipFile" />, which
    ///     reports "not a zip" in its own words.
    /// </summary>
    internal static ZipArchive OpenArchive(string gpFilePath, int maxEntries)
    {
        if (BoundedZipExtractor.CheckDeclaredEntryCount(gpFilePath, maxEntries) == ZipExtractionRefusal.TooManyEntries)
            throw new InvalidDataException("The Guitar Pro file declares too many archive entries to be a score.");
        return ZipFile.OpenRead(gpFilePath);
    }

    /// <summary>
    ///     The score entry as a stream that refuses more than
    ///     <paramref name="maxBytes" />: the declared size is checked first (the
    ///     runtime inflates no further than that), and the bytes actually
    ///     produced are counted as a second line.
    /// </summary>
    private static Stream OpenScoreEntry(ZipArchiveEntry entry, long maxBytes)
    {
        const string what = "The Guitar Pro score";
        ImportSizeLimits.RequireWithin(entry.Length, maxBytes, what);
        return new LimitedReadStream(entry.Open(), maxBytes, what);
    }
}
