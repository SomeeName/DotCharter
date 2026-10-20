using System.Xml;

namespace DotCharter.Services.Parsing.Rscp;

/// <summary>
///     Reads the authored genre (<c>dcGenre</c>) out of an <c>.rscp</c> project
///     HEADER without materialising the chart.
///     <para>
///         This exists because the practice planner's genre preference has
///         exactly one real backing source in this app: the user's own charts,
///         where <c>SongMetadataViewModel</c> authors the field and
///         <see cref="RscpWriter" /> persists it as <c>dcGenre</c>. A Rocksmith
///         package carries no genre at all (neither <c>LibrarySongEntry</c> nor the
///         RS2014 song manifest has the concept), so those songs stay unknown —
///         and unknown never FAILS a genre preference.
///     </para>
///     <para>
///         <b>Header only, by construction.</b> <c>RscpChartSerializer.Load</c>
///         goes through <c>SafeXml.LoadFromFile</c>, i.e. a full DOM of a file
///         that is routinely megabytes of notes — far too much to pay per song
///         in a library scan. This walks the ROOT's direct children with a
///         streaming reader and stops dead at the first bulk node
///         (<see cref="BulkElements" />), so the note data is never even read off
///         the disk. <c>dcGenre</c> is written between <c>albumYear</c> and
///         <c>musicFileName</c> (<c>RscpWriter.BuildChartHeader</c>), so the walk
///         is a couple of dozen shallow nodes.
///     </para>
///     <para>
///         Faults are the CALLER's to absorb: this throws whatever the file
///         system or the XML reader throws, and <see cref="ChartGenreIndex" />
///         catches + logs + records "unknown". A missing or malformed chart must
///         never take a library scan down.
///     </para>
/// </summary>
public static class RscpGenreReader
{
    /// <summary>The DotCharter-extension element holding the authored genre.</summary>
    public const string GenreElement = "dcGenre";

    /// <summary>Root children that mean the header is over. Reaching one of
    /// these is the stop condition — everything past it is chart bulk (notes,
    /// beats, vocals, arrangements) and this reader must never walk it. Names
    /// mirror <c>RscpWriter</c>'s node builders.</summary>
    private static readonly HashSet<string> BulkElements = new(StringComparer.Ordinal)
    {
        "beats", "vocals", "showlights", "bookmarks", "arrangements",
        "dcAnnotations", "dcToneDefinitions", "dcNotationVoices", "text"
    };

    /// <summary>The genre authored in <paramref name="projectPath" />'s header,
    /// or null when the file carries none. Throws on a missing/unreadable/
    /// malformed file.</summary>
    public static string? ReadGenre(string projectPath)
    {
        using var stream = SafeXml.OpenRead(projectPath);
        return ReadGenre(stream);
    }

    /// <inheritdoc cref="ReadGenre(string)" />
    public static string? ReadGenre(Stream stream)
    {
        using var reader = SafeXml.CreateReader(stream);

        // Position on the root element (<project>).
        while (reader.Read() && reader.NodeType != XmlNodeType.Element)
        {
        }

        if (reader.NodeType != XmlNodeType.Element || reader.IsEmptyElement) return null;
        var childDepth = reader.Depth + 1;

        while (reader.Read())
        {
            // Deliberately NOT XmlReader.Skip(): it leaves the reader ON the node
            // after the skipped element, so the loop's next Read() would step
            // over a SIBLING. The header's non-genre children are a handful of
            // shallow scalars plus <stems>/<arrangementFiles>, so simply letting
            // the walk pass over them (filtered by depth) is both cheaper to
            // reason about and correct.
            if (reader.NodeType != XmlNodeType.Element || reader.Depth != childDepth) continue;

            if (string.Equals(reader.Name, GenreElement, StringComparison.Ordinal))
            {
                var value = reader.ReadElementContentAsString();
                return string.IsNullOrWhiteSpace(value) ? null : value.Trim();
            }

            if (BulkElements.Contains(reader.Name)) return null;
        }

        return null;
    }
}
