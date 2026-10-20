using System.Xml;

namespace DotCharter.Services.Parsing.Rscp;

/// <summary>What a project header says about its song's cover.</summary>
/// <param name="Title">The song title.</param>
/// <param name="Artist">The artist name.</param>
/// <param name="CoverArtPath">The <c>dcCoverArt</c> value: a file name relative to the project folder, or absolute.</param>
public readonly record struct ProjectCoverFacts(string Title, string Artist, string CoverArtPath);

/// <summary>
///     Reads the title, artist and cover art path out of an <c>.rscp</c>
///     project HEADER with a streaming reader, stopping at the first bulk node
///     (the same walk <see cref="RscpGenreReader" /> makes), so Home never
///     materialises a chart to find a cover. Throws whatever the file system
///     or the XML reader throws; the caller absorbs it.
/// </summary>
public static class RscpCoverReader
{
    private static readonly HashSet<string> BulkElements = new(StringComparer.Ordinal)
    {
        "beats", "vocals", "showlights", "bookmarks", "arrangements",
        "dcAnnotations", "dcToneDefinitions", "dcNotationVoices", "text"
    };

    public static ProjectCoverFacts Read(string projectPath)
    {
        using var stream = SafeXml.OpenRead(projectPath);
        using var reader = SafeXml.CreateReader(stream);

        string title = "", artist = "", cover = "";
        while (reader.Read() && reader.NodeType != XmlNodeType.Element)
        {
        }

        if (reader.NodeType != XmlNodeType.Element || reader.IsEmptyElement) return default;
        var childDepth = reader.Depth + 1;

        // ReadElementContentAsString leaves the reader ON the next node, so a
        // consumed element must not be followed by another Read().
        var more = reader.Read();
        while (more)
        {
            if (reader.NodeType != XmlNodeType.Element || reader.Depth != childDepth)
            {
                more = reader.Read();
                continue;
            }

            switch (reader.Name)
            {
                case "title":
                    title = reader.ReadElementContentAsString().Trim();
                    break;
                case "artistName":
                    artist = reader.ReadElementContentAsString().Trim();
                    break;
                case "dcCoverArt":
                    cover = reader.ReadElementContentAsString().Trim();
                    break;
                default:
                    if (BulkElements.Contains(reader.Name)) return new ProjectCoverFacts(title, artist, cover);
                    more = reader.Read();
                    break;
            }

            more = more && !reader.EOF;
        }

        return new ProjectCoverFacts(title, artist, cover);
    }
}
