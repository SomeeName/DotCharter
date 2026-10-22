using System.Xml;
using System.Xml.Linq;

namespace DotCharter.Services.Parsing;

/// <summary>
///     Hardened XML loading for content that comes out of untrusted
///     containers (PSARC entries, Guitar Pro archives). The framework
///     defaults already prohibit DTDs and external resolution — this makes
///     that explicit so a future TFM/default change can't silently reopen
///     XXE, and adds an absolute document size ceiling as bomb protection.
/// </summary>
public static class SafeXml
{
    private static readonly XmlReaderSettings Hardened = new()
    {
        DtdProcessing = DtdProcessing.Prohibit,
        XmlResolver = null,
        MaxCharactersFromEntities = 1024,
        // Generous for genuine chart/showlights XML, fatal for bombs.
        MaxCharactersInDocument = 128L * 1024 * 1024
    };

    public static XDocument LoadFromStream(Stream stream)
    {
        using var reader = XmlReader.Create(stream, Hardened);
        return XDocument.Load(reader);
    }

    /// <summary>A STREAMING reader over the same hardened settings, for callers
    /// that must read only the HEAD of a document instead of materialising it
    /// into a DOM (<see cref="Rscp.RscpGenreReader" />). Same file, same
    /// protections — the difference is how much of it is ever touched.</summary>
    public static XmlReader CreateReader(Stream stream) => XmlReader.Create(stream, Hardened);

    /// <summary>Largest file accepted by <see cref="LoadFromFile"/> — far
    /// beyond any genuine chart/project XML, cheap insurance against
    /// materialising a multi-GB file into a DOM.</summary>
    public const long MaxUserFileBytes = 256L * 1024 * 1024;

    public static XDocument LoadFromFile(string filePath)
    {
        var length = new FileInfo(filePath).Length;
        if (length > MaxUserFileBytes)
        {
            throw new InvalidDataException(
                $"'{filePath}' is {length / (1024 * 1024)} MB — larger than any valid chart/project XML " +
                $"(limit {MaxUserFileBytes / (1024 * 1024)} MB). The file is corrupt or not a chart.");
        }

        using var stream = OpenRead(filePath);
        return LoadFromStream(stream);
    }

    /// <summary>
    ///     How every XML document this app owns is opened for reading:
    ///     <see cref="FileShare.ReadWrite" /> | <see cref="FileShare.Delete" />.
    ///     <para>
    ///         The share mode is load-bearing, not incidental. A <c>.dcproj</c> is
    ///         written through <c>AtomicFile.ReplaceWithRetry</c> — stage, fsync,
    ///         RENAME over the target — and on Windows a rename is refused by any
    ///         open handle on the destination that does not share DELETE. A plain
    ///         <c>File.OpenRead</c> denies it, so a load that overlapped a save of
    ///         the same file (the crash-recovery timer writing the snapshot the
    ///         recovery dialog is opening; an auto-backup reading a project the
    ///         user is saving) burned the writer's whole bounded retry budget and,
    ///         past it, DROPPED the write. Sharing delete lets the rename through
    ///         while this handle goes on reading the whole OLD document — the same
    ///         pairing <c>AtomicFile.ReadAllText</c> documents for the JSON stores.
    ///     </para>
    /// </summary>
    public static FileStream OpenRead(string filePath)
        => new(filePath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
}
