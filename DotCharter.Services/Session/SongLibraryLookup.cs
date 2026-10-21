using System.Collections.Concurrent;
using DotCharter.Services.Library.Index;
using DotCharter.Services.Parsing.Rscp;
using DotCharter.Services.Library;

namespace DotCharter.Services.Session;

/// <summary>
///     <see cref="ISongLibraryLookup" /> over the library index and the project
///     files. A project's cover is its <c>dcCoverArt</c> (relative to the
///     project folder), else <c>cover.png</c> beside it, else the library's
///     cover for the same artist and title. Only the project HEADER is read
///     (<see cref="RscpCoverReader" />), once per file version: the result is
///     cached per path against the file's modification time and length.
/// </summary>
public sealed class SongLibraryLookup(ILibraryIndex index) : ISongLibraryLookup
{
    private const string DefaultCoverName = "cover.png";

    private readonly ConcurrentDictionary<string, (long Ticks, long Length, ProjectCoverFacts Facts)> _headers =
        new(StringComparer.OrdinalIgnoreCase);

    public LibraryIndexFacts ReadFacts() => index.ReadFacts();

    public string FindCover(string statsKey)
    {
        if (string.IsNullOrWhiteSpace(statsKey)) return "";
        if (LibrarySongIdentity.TryGetSongKey(statsKey, out var songKey))
            return index.FindCoverImage(songKey);

        try
        {
            return FindProjectCover(statsKey);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or System.Xml.XmlException
                                       or ArgumentException or NotSupportedException)
        {
            return "";
        }
    }

    private string FindProjectCover(string projectPath)
    {
        var info = new FileInfo(projectPath);
        if (!info.Exists) return "";

        var directory = info.DirectoryName ?? "";
        var facts = HeaderOf(info);

        if (facts.CoverArtPath.Length > 0)
        {
            // Data from the file: a rooted, UNC or escaping name is never probed.
            var declared = ProjectRelativePath.TryResolve(directory, facts.CoverArtPath);
            if (declared != null && File.Exists(declared)) return declared;
        }

        var beside = Path.Combine(directory, DefaultCoverName);
        if (File.Exists(beside)) return beside;

        return facts.Title.Length == 0 ? "" : index.FindCoverImageByTitle(facts.Artist, facts.Title);
    }

    private ProjectCoverFacts HeaderOf(FileInfo info)
    {
        var ticks = info.LastWriteTimeUtc.Ticks;
        if (_headers.TryGetValue(info.FullName, out var cached) && cached.Ticks == ticks && cached.Length == info.Length)
            return cached.Facts;

        var facts = RscpCoverReader.Read(info.FullName);
        _headers[info.FullName] = (ticks, info.Length, facts);
        return facts;
    }
}
