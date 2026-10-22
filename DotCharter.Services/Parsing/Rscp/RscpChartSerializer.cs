using System.Security.Cryptography;
using System.Text;
using System.Xml;
using System.Xml.Linq;
using ChartProject = DotCharter.Core.Domain.Project.ChartProject;
using ProjectIdentity = DotCharter.Core.Domain.Project.ProjectIdentity;

namespace DotCharter.Services.Parsing.Rscp;

/// <summary>
///     Main serializer for RSCP chart files. Coordinates parsing and writing using specialized parsers.
/// </summary>
public class RscpChartSerializer : IChartSerializer
{
    private readonly RscpArrangementParser _arrangementParser = new();
    private readonly RscpBeatsParser _beatsParser = new();
    private readonly RscpHeaderParser _headerParser = new();
    private readonly IAppLogger? _logger;
    private readonly RscpVocalsParser _vocalsParser = new();
    private readonly RscpWriter _writer = new();
    private readonly RscpMigrations _migrations;

    public RscpChartSerializer() => _migrations = new RscpMigrations();

    public RscpChartSerializer(IAppLogger logger)
    {
        _logger = logger;
        _migrations = new RscpMigrations(logger);
    }

    public ChartProject Load(string filePath)
    {
        using var _ = _logger?.BeginTimedOperation(AppLogCategory.Project, "RSCP Load", filePath);
        // Blank, not the constructor's fresh id: the header parser fills it from
        // <dcProjectId> when the file carries a well-formed one, and an empty id
        // after the parse is how "this file has none" is told apart.
        var project = new ChartProject { ProjectId = "" };
        var doc = SafeXml.LoadFromFile(filePath);

        _headerParser.ParseChartHeader(doc, filePath, project);
        if (!ProjectIdentity.IsWellFormed(project.ProjectId)) project.ProjectId = LegacyProjectId(filePath);

        // A file written by a NEWER DotCharter/RsCharter would be parsed as
        // v3 here and silently downgraded on the next save — make that loss
        // visible instead of quiet.
        if (project.Chart.ChartFormatVersion > RscpMigrations.CurrentVersion)
        {
            _logger?.Warning(AppLogCategory.Project,
                $"RSCP '{filePath}' declares chartFormatVersion {project.Chart.ChartFormatVersion} " +
                $"(this build supports {RscpMigrations.CurrentVersion}). " +
                "Data this build cannot re-attach will be dropped if the project is saved.");
        }

        // The forward-compatibility half (docs/DCPROJ-FORMAT-GUARANTEES.md): keep
        // the top-level nodes this build does not understand so the next save
        // puts them back instead of deleting a newer build's whole feature. The
        // capture is the definition of "unknown" — it is the complement of
        // RscpFormatContract.KnownRootElements, so it needs no per-feature
        // maintenance beyond registering each new root node there.
        foreach (var node in RscpFormatContract.CaptureUnknownRootNodes(doc))
            project.UnknownRscpNodes.Add(node);

        // The soft stamp. It is separate from the residue on purpose: a newer
        // build can add data this build CANNOT preserve (an attribute on a note,
        // a child of an arrangement), and that loss is invisible without it.
        var writerVersion = RscpFormatContract.ReadWriterVersion(doc);
        if (RscpFormatContract.IsNewerThanCurrent(writerVersion))
        {
            _logger?.Warning(AppLogCategory.Project,
                $"RSCP '{filePath}' was written by DotCharter {writerVersion}; this build is " +
                $"{RscpFormatContract.CurrentWriterVersion}. Its top-level extension nodes " +
                $"({project.UnknownRscpNodes.Count} unknown) are preserved across a save, but anything it " +
                "added to a note, chord or arrangement cannot be re-attached and will be lost on save.");
        }
        else if (project.UnknownRscpNodes.Count > 0)
        {
            _logger?.Info(AppLogCategory.Project,
                $"RSCP '{filePath}' carries {project.UnknownRscpNodes.Count} top-level node(s) this build " +
                "does not understand; they are preserved verbatim on save.");
        }

        _beatsParser.ParseBeats(doc, project);
        _vocalsParser.ParseVocals(doc, project);
        _arrangementParser.ParseArrangements(doc, project);

        // Older projects kept their arrangements (and vocals) in sibling files rather
        // than embedded. Parsed with the v3 readers alone they come back EMPTY — and the
        // next save would then overwrite the original with that emptiness. Upgrade after
        // the normal parse, so migration only touches what the parse could not find.
        _migrations.Upgrade(doc, filePath, project);

        // Linked measures name their arrangements by index, so they are read last — and
        // checked: a copy that does not match its group (an older build edited it) is
        // unlinked here, its content kept.
        foreach (var notice in RscpLinkGroups.Read(doc, project))
        {
            _logger?.Warning(AppLogCategory.Project,
                $"RSCP '{filePath}': linked measures {notice.Letter} ('{notice.Name}'): {notice.Unlinked} placement(s) unlinked on load ({notice.Reason}); their content is kept.");
        }

        _logger?.Trace(AppLogCategory.Project,
            $"RSCP loaded: beats={project.Beats.Count}, arrangements={project.Arrangements.Count}, vocalTracks={project.VocalTracks.Count}.");
        return project;
    }

    /// <summary>
    ///     The id of a project file written before <c>&lt;dcProjectId&gt;</c> existed
    ///     (or carrying a malformed one): the first 128 bits of the SHA-256 of its
    ///     BYTES, in <see cref="ProjectIdentity" />'s shape.
    ///     <para>
    ///         Derived, not random, on purpose. The id is only written by the next
    ///         SAVE, so a random one would give a legacy project a new identity on
    /// every open until then — and any AI chat started in between
    ///         would be orphaned the moment the project was closed unsaved. The
    ///         file's bytes do not change until it is saved, and that save writes
    ///         this very id, so the identity is stable across both. Two
    ///         byte-identical files share it, which is right: they are copies of
    ///         one project, and each copy keeps its chats in its own folder.
    ///     </para>
    /// </summary>
    internal static string LegacyProjectId(string filePath)
    {
        try
        {
            using var stream = new FileStream(filePath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
            var hash = SHA256.HashData(stream);
            return Convert.ToHexStringLower(hash, 0, ProjectIdentity.Length / 2);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // The document was readable a moment ago; if its bytes are not now,
            // a fresh id is the honest fallback (written by the next save).
            return ProjectIdentity.NewId();
        }
    }

    /// <inheritdoc />
    /// <remarks>
    ///     <paramref name="audioLengthMs" /> is ignored: the RSCP project format has
    ///     no song-length field — the chart IS the document, and the audio length is
    ///     re-derived from the decoded file on load.
    /// </remarks>
    public void Save(string filePath, ChartProject project, double? audioLengthMs = null)
    {
        using var _ = _logger?.BeginTimedOperation(AppLogCategory.Project, "RSCP Save", filePath);
        var doc = new XDocument();
        _writer.WriteDocument(doc, project);

        using (var stream = new FileStream(filePath, FileMode.Create, FileAccess.Write, FileShare.Read))
        {
            WriteDocument(stream, doc);

            // Durability, not just atomicity: this file is the staging half of a
            // temp-write → File.Move swap (ProjectSessionService writes
            // "<path>.saving" and renames over the real project; the recovery
            // snapshot store does the same). Without an fsync the rename can
            // become durable BEFORE the data, so a power cut swaps a truncated
            // file over the previous good save — losing both (AtomicFile
            // documents this exact trap). Flush(true) forces the
            // bytes to the platter before the caller's rename can retire.
            stream.Flush(flushToDisk: true);
        }
    }

    /// <summary>
    ///     Writes <paramref name="doc" /> the way RsCharter (XStream) does:
    ///     <c>&lt;?xml version='1.0' encoding='UTF-8'?&gt;</c> with single quotes and
    ///     uppercase UTF-8, then the body indented by two spaces. XmlWriter only emits the
    ///     double-quoted, lowercase form, so the prologue is written by hand and the body
    ///     follows without its own declaration. Shared by the project file and its Charter companion.
    /// </summary>
    public static void WriteDocument(Stream stream, XDocument doc)
    {
        var settings = new XmlWriterSettings
        {
            OmitXmlDeclaration = true,
            Indent = true,
            IndentChars = "  ",
            Encoding = new UTF8Encoding(false)
        };

        var prologue = Encoding.UTF8.GetBytes("<?xml version='1.0' encoding='UTF-8'?>\n");
        stream.Write(prologue, 0, prologue.Length);
        using var writer = XmlWriter.Create(stream, settings);
        doc.Save(writer);
    }
}
