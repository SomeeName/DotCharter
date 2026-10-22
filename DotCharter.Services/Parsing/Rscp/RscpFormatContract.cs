using System.Xml.Linq;

namespace DotCharter.Services.Parsing.Rscp;

/// <summary>
///     The `.dcproj` forward-compatibility contract, in code:
///     <list type="number">
///         <item>
///             which element names the ROOT of a project document may carry
///             (<see cref="KnownRootElements" />) — everything else is a node a
///             NEWER build wrote,
///         </item>
///         <item>
///             capturing those unknown nodes on load
///             (<see cref="CaptureUnknownRootNodes" />) and putting them back on
///             save (<see cref="EmitUnknownRootNodes" />) so an older build
///             opening a newer build's project does not delete what it could
///             not read, and
///         </item>
///         <item>
///             the soft writer stamp (<see cref="WriterVersionElement" />) that
///             lets a build say "this file came from a version after me" out
///             loud instead of silently downgrading it.
///         </item>
///     </list>
///     <para>
///         The prose half is <c>docs/DCPROJ-FORMAT-GUARANTEES.md</c>; every clause
///         of it that can be checked mechanically is checked by
///         <c>RscpForwardCompatibilityTests</c>. If you add a root element to
///         <see cref="RscpWriter" /> or teach a parser to read one, add it here
///         in the same change — the writer-side lint in that suite fails
///         otherwise, and the failure mode it prevents is real: an element that
///         is known to the READER but missing from this set is captured as
///         residue AND written from the model, i.e. the same node twice.
///     </para>
/// </summary>
public static class RscpFormatContract
{
    /// <summary>
    ///     The build stamp, written on EVERY save (see the doc's "when absence
    ///     carries meaning" rule — a stamp that appears only sometimes cannot
    ///     answer "is this file newer than me?", because absence has to mean
    ///     "written before the stamp existed").
    /// </summary>
    public const string WriterVersionElement = "dcWriterVersion";

    /// <summary>
    /// The project's stable identity (<c>ChartProject.ProjectId</c>),
    ///     written on EVERY save like the stamp: absence means "written before the
    ///     id existed", and the reader derives one for such a file.
    /// </summary>
    public const string ProjectIdElement = "dcProjectId";

    /// <summary>
    ///     Every element name that may appear as a direct child of
    ///     <c>&lt;project&gt;</c> in a document THIS build writes or reads. The
    ///     complement of this set is, by definition, the residue.
    ///     <para>
    ///         Ordinal comparison and namespace-free: the format has never used
    ///         XML namespaces, so a namespaced element is unknown even if its
    ///         local name is listed.
    ///     </para>
    /// </summary>
    public static readonly IReadOnlySet<string> KnownRootElements = new HashSet<string>(StringComparer.Ordinal)
    {
        // --- rscharter's own header field order ---
        "chartFormatVersion", "editMode", "arrangement", "level", "time",
        "artistName", "artistNameSort", "title", "albumName", "albumYear",
        "musicFileName", "stems", "selectedStem", "arrangementFiles",
        "beats", "mainAudioOffsetAdded", "barsAdded", "showlights",
        "vocalPaths", "arrangements", "bookmarks", "text",

        // --- DotCharter additive extensions (dc*) ---
        WriterVersionElement, ProjectIdElement,
        "dcJapaneseTitle", "dcJapaneseArtist", "dcCharter", "dcGenre",
        "dcReleaseDate", "dcCoverArt", "dcVersion", "dcBeatMapLocked",
        "dcKeyAccidentals", "dcKeyIsMinor",
        "dcAnnotations", "dcToneDefinitions", "dcNotationVoices", "dcSynthPlayStyle",
        RscpProvenance.ProjectElement, RscpLinkGroups.RootElement
    };

    /// <summary>
    ///     This build's version, as written into <see cref="WriterVersionElement" />.
    ///     Resolved from the ENTRY assembly (the SDK derives every version from
    ///     <c>DotCharter.UI.csproj</c>'s single <c>&lt;Version&gt;</c>; the class
    ///     libraries carry the default 1.0.0 and would be a lie here).
    /// </summary>
    public static string CurrentWriterVersion { get; } = ResolveWriterVersion();

    private static string ResolveWriterVersion() => DotCharter.Services.Infrastructure.AppVersionInfo.Current;

    /// <summary>The <c>dcWriterVersion</c> a document declares, or null.</summary>
    public static string? ReadWriterVersion(XDocument doc)
    {
        ArgumentNullException.ThrowIfNull(doc);
        var value = doc.Root?.Element(WriterVersionElement)?.Value?.Trim();
        return string.IsNullOrEmpty(value) ? null : value;
    }

    /// <summary>
    ///     True when <paramref name="stamp" /> names a build strictly newer than
    ///     this one. Deliberately conservative: an absent, malformed or equal
    ///     stamp is NOT newer, because the point of the stamp is a warning the
    ///     user can trust, and a warning that fires on every second file is one
    ///     nobody reads.
    /// </summary>
    public static bool IsNewerThanCurrent(string? stamp)
    {
        var declared = DotCharter.Services.Update.GitHubReleaseRules.TryParseVersionFromTag(stamp);
        var current = DotCharter.Services.Update.GitHubReleaseRules.TryParseVersionFromTag(CurrentWriterVersion);
        return declared != null && current != null && DotCharter.Services.Update.GitHubReleaseRules.CompareVersions(declared, current) > 0;
    }

    /// <summary>
    ///     Every direct child of <c>&lt;project&gt;</c> whose name this build
    ///     does not know, normalized to one XML fragment each, in document
    ///     order. Never throws — a document with no root yields nothing.
    /// </summary>
    public static List<string> CaptureUnknownRootNodes(XDocument doc)
    {
        ArgumentNullException.ThrowIfNull(doc);

        var captured = new List<string>();
        if (doc.Root is null) return captured;

        foreach (var element in doc.Root.Elements())
        {
            if (element.Name.Namespace == XNamespace.None &&
                KnownRootElements.Contains(element.Name.LocalName))
            {
                continue;
            }

            captured.Add(Normalize(element));
        }

        return captured;
    }

    /// <summary>
    ///     One fragment, in a spelling that does not depend on how the file
    ///     happened to be indented.
    ///     <para>
    ///         The re-parse is the point. <see cref="SafeXml" /> loads through an
    ///         <c>XmlReader</c> that does NOT ignore whitespace, so an element
    ///         read from a pretty-printed file arrives carrying the file's
    ///         indentation as whitespace-only text nodes — and those would then
    ///         be part of the stored string, making the same node compare
    ///         unequal to itself across an indent change. Re-parsing with
    ///         <see cref="LoadOptions.None" /> drops whitespace-only nodes (the
    ///         ones <see cref="System.Xml.XmlWriter" /> regenerates on the way
    ///         out anyway) and leaves every text node that carries content.
    ///     </para>
    /// </summary>
    private static string Normalize(XElement element)
    {
        var raw = element.ToString(SaveOptions.DisableFormatting);
        try
        {
            return XElement.Parse(raw, LoadOptions.None).ToString(SaveOptions.DisableFormatting);
        }
        catch (System.Xml.XmlException)
        {
            // Unreachable: `raw` came from a parsed document. Keeping the bytes
            // is still the right answer if it ever isn't.
            return raw;
        }
    }

    /// <summary>
    ///     Puts captured residue back, LAST — after every node this build wrote.
    ///     <para>
    ///         Order is load-bearing, not cosmetic: the header/beats/vocals
    ///         parsers look their nodes up with <c>Descendants(name)</c> +
    ///         <c>FirstOrDefault()</c>, which is DOCUMENT ORDER. Appending means
    ///         a preserved node that happens to contain a <c>&lt;title&gt;</c>
    ///         can never shadow the real one.
    ///     </para>
    ///     <para>
    ///         A fragment whose name the writer already emitted is DROPPED rather
    ///         than appended. That makes duplication structurally impossible even
    ///         if <see cref="KnownRootElements" /> ever falls behind the writer:
    ///         without it, a node that is known to the reader but missing from
    ///         the set would be written from the model AND re-appended as
    ///         residue, and the file would grow a copy on every single save.
    ///     </para>
    ///     <para>
    ///         That test is against the WRITER's names only — captured before the
    ///         loop and never extended. Residue nodes that share a name with each
    ///         OTHER are all preserved: a future build's
    ///         <c>&lt;dcSection id="1"/&gt;&lt;dcSection id="2"/&gt;</c> is two
    ///         nodes, and keeping only the first silently deleted the rest on the
    ///         first re-save.
    ///     </para>
    /// </summary>
    public static void EmitUnknownRootNodes(XElement root, IEnumerable<string>? nodes)
    {
        ArgumentNullException.ThrowIfNull(root);
        if (nodes is null) return;

        // Captured ONCE, from what the WRITER emitted, and never added to: the
        // guard exists to stop a stale KnownRootElements duplicating a node the
        // writer already wrote, not to make residue names unique. Adding each
        // appended fragment's name back in dropped the second and later unknown
        // roots that SHARE a name — `<dcSection id="1"/><dcSection id="2"/>`
        // came back as one after a single re-save, and the rest were gone for
        // good.
        var writerEmitted = root.Elements().Select(e => e.Name).ToHashSet();

        foreach (var fragment in nodes)
        {
            if (string.IsNullOrWhiteSpace(fragment)) continue;

            XElement parsed;
            try
            {
                parsed = XElement.Parse(fragment, LoadOptions.None);
            }
            catch (System.Xml.XmlException)
            {
                // Residue only ever originates from a document we parsed, so this
                // is unreachable from the file path — it guards a caller that put
                // something else in the collection. Dropping is right: an
                // unparseable fragment cannot be written without corrupting the
                // whole document.
                continue;
            }

            if (writerEmitted.Contains(parsed.Name)) continue;
            root.Add(parsed);
        }
    }
}
