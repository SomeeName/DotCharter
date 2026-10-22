using System.Buffers.Binary;
using System.IO.Compression;
using System.Security.Cryptography;
using System.Text;
using System.Xml;
using DotCharter.Services.Parsing;
using DotCharter.Services.Parsing.Rscp;
using DotCharter.Services.Update;

namespace DotCharter.Services.Package.DcSong;

/// <summary>A cover or banner image, checked to be what its media type says.</summary>
public sealed record DcSongImage(string MediaType, byte[] Bytes);

/// <summary>
///     Reads a <c>.dcsong</c> package (format: <c>docs/agents/services/dcsong-format.md</c>).
///     <para>
///         <b>A package is UNTRUSTED input</b>, so nothing here asks
///         <see cref="ZipArchive" /> anything. The structure is read by
///         <see cref="ZipCentralDirectory" /> — the same hardened, allocation-bounded
///         parser the partial updater trusts with bytes a server wrote: zip64 and
///         multi-volume refused, every offset bounded against the file, every name
///         through <see cref="ZipCentralDirectory.NormalizeEntryName" /> (no
///         <c>..</c>, no rooted path). Every read is BOUNDED: the member count, the
///         manifest, the chart and each image have a cap checked against the
///         declared size first and against the bytes the inflater really produces
///         second, and the media members are STORED, so their bytes are a plain
///         window of the file that cannot inflate at all. Nothing is extracted to
///         disk except the chart, into a temporary folder, because the <c>.dcproj</c>
///         reader takes a path.
///     </para>
///     <para>
///         Every member is checked against the size and SHA-256 its manifest
///         records before a byte of it is handed out. The package file is held open
///         with <see cref="FileShare.Read" /> for the life of this object, so a
///         member verified once cannot change underneath a later read.
///     </para>
/// </summary>
public sealed class DcSongPackage : IDisposable
{
    private readonly FileStream _hold;
    private readonly Dictionary<string, Member> _members;
    private readonly HashSet<string> _verified = new(StringComparer.Ordinal);
    private readonly Lock _gate = new();

    private DcSongPackage(string path, FileStream hold, Dictionary<string, Member> members, DcSongManifest manifest)
    {
        FilePath = path;
        _hold = hold;
        _members = members;
        Manifest = manifest;
    }

    public string FilePath { get; }

    public DcSongManifest Manifest { get; }

    /// <summary>True for a FLAC package. Chart-from-audio and stem separation need
    /// this; an Opus package is playback-only.</summary>
    public bool IsLossless => Manifest.Audio.Lossless;

    /// <summary>The stems' names, in package order.</summary>
    public IReadOnlyList<string> StemNames => Manifest.Stems.Select(s => s.StemName!).ToList();

    /// <summary>
    ///     Opens <paramref name="path" />: reads the zip directory, reads and checks
    ///     the manifest (version first), and checks that the zip and the manifest
    ///     name exactly the same members with the same sizes. Member CONTENTS are
    ///     verified when they are read (or all at once by <see cref="VerifyAll" />).
    ///     Throws <see cref="DcSongReadException" />.
    /// </summary>
    public static DcSongPackage Open(string path)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        FileStream file;
        try
        {
            file = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read, 4096, FileOptions.RandomAccess);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException)
        {
            throw new DcSongReadException(DcSongReadFailure.Unreadable, $"'{path}' could not be opened: {ex.Message}", ex);
        }

        try
        {
            var members = ReadDirectory(file);
            if (!members.TryGetValue(DcSongFormat.ManifestPath, out var manifestMember))
                throw new DcSongReadException(DcSongReadFailure.ManifestMissing, "the package has no manifest.json.");

            var manifest = DcSongManifestJson.Parse(Inflate(path, manifestMember, DcSongFormat.MaxManifestBytes));
            CrossCheck(manifest, members);
            return new DcSongPackage(path, file, members, manifest);
        }
        catch (IOException ex)
        {
            file.Dispose();
            throw new DcSongReadException(DcSongReadFailure.Unreadable, $"'{path}' could not be read: {ex.Message}", ex);
        }
        catch
        {
            file.Dispose();
            throw;
        }
    }

    /// <summary>
    ///     The chart, as a fresh <see cref="ChartProject" />. Its file references
    ///     (<see cref="ChartProject.AudioFileName" />, the cover, the stems) name
    ///     package members, never files on the author's machine.
    /// </summary>
    public ChartProject LoadChart(IAppLogger? logger = null)
    {
        var bytes = ReadVerified(Manifest.Find(DcSongFileRole.Chart)!, DcSongFormat.MaxChartBytes);

        // A pre-v3 project names sibling files to recover its arrangements from, and
        // the project reader would resolve those against the folder the chart sits in —
        // or, for a rooted name, anywhere on the disk. A package's chart is always
        // current (the writer saves it), so one that asks for recovery is refused
        // before the reader can follow it out of the package.
        try
        {
            using var probe = new MemoryStream(bytes, writable: false);
            if (RscpMigrations.NeedsArrangementRecovery(SafeXml.LoadFromStream(probe)))
                throw new DcSongReadException(DcSongReadFailure.ChartInvalid, "the chart names files outside the package.");
        }
        catch (XmlException ex)
        {
            throw new DcSongReadException(DcSongReadFailure.ChartInvalid, $"The chart is not valid XML: {ex.Message}", ex);
        }

        var folder = Directory.CreateTempSubdirectory("dotcharter_dcsong_chart_").FullName;
        try
        {
            var chartPath = Path.Combine(folder, DcSongFormat.ChartPath);
            File.WriteAllBytes(chartPath, bytes);
            var serializer = logger == null ? new RscpChartSerializer() : new RscpChartSerializer(logger);
            return serializer.Load(chartPath);
        }
        catch (Exception ex) when (ex is XmlException or InvalidDataException or FormatException)
        {
            throw new DcSongReadException(DcSongReadFailure.ChartInvalid, $"The chart could not be read: {ex.Message}", ex);
        }
        finally
        {
            BestEffortDelete.Directory(folder);
        }
    }

    /// <summary>The song audio as a seekable stream (Opus or FLAC, per
    /// <see cref="DcSongManifest.Audio" />), verified first. The caller disposes it.</summary>
    public Stream OpenAudio() => OpenStored(Manifest.Find(DcSongFileRole.Audio)!);

    /// <summary>The preview clip (Opus), or null when the package has none.</summary>
    public Stream? OpenPreview() =>
        Manifest.Find(DcSongFileRole.Preview) is { } preview ? OpenStored(preview) : null;

    /// <summary>The stem named <paramref name="name" /> (Opus, playback-only).</summary>
    public Stream OpenStem(string name)
    {
        var stem = Manifest.FindStem(name)
                   ?? throw new DcSongReadException(DcSongReadFailure.EntryMissing, $"the package has no stem '{name}'.");
        return OpenStored(stem);
    }

    public DcSongImage? ReadCover() => ReadImage(DcSongFileRole.Cover);

    public DcSongImage? ReadBanner() => ReadImage(DcSongFileRole.Banner);

    /// <summary>Checks every member against its manifest checksum, including members
    /// a newer minor version added that this build never reads.</summary>
    public void VerifyAll()
    {
        foreach (var file in Manifest.Files)
        {
            switch (file.Role)
            {
                case DcSongFileRole.Chart:
                    ReadVerified(file, DcSongFormat.MaxChartBytes);
                    break;
                case DcSongFileRole.Cover or DcSongFileRole.Banner:
                    ReadVerified(file, DcSongFormat.MaxImageBytes);
                    break;
                default:
                    OpenStored(file).Dispose();
                    break;
            }
        }
    }

    public void Dispose() => _hold.Dispose();

    private DcSongImage? ReadImage(DcSongFileRole role)
    {
        if (Manifest.Find(role) is not { } file) return null;
        var bytes = ReadVerified(file, DcSongFormat.MaxImageBytes);
        if (DcSongImages.Sniff(bytes) != file.MediaType)
            throw new DcSongReadException(DcSongReadFailure.ManifestInvalid, $"'{file.Path}' is not the {file.MediaType} its manifest says.");
        return new DcSongImage(file.MediaType!, bytes);
    }

    private byte[] ReadVerified(DcSongFileEntry file, long cap)
    {
        var bytes = Inflate(FilePath, _members[file.Path], cap);
        CheckHash(file, SHA256.HashData(bytes));
        return bytes;
    }

    private Stream OpenStored(DcSongFileEntry file)
    {
        var member = _members[file.Path];
        lock (_gate)
        {
            if (!_verified.Contains(file.Path))
            {
                using (var check = new DcSongEntryStream(FilePath, member.DataOffset, member.CompressedSize))
                    CheckHash(file, SHA256.HashData(check));
                _verified.Add(file.Path);
            }
        }

        return new DcSongEntryStream(FilePath, member.DataOffset, member.CompressedSize);
    }

    private static void CheckHash(DcSongFileEntry file, byte[] hash)
    {
        if (!string.Equals(Convert.ToHexStringLower(hash), file.Sha256, StringComparison.Ordinal))
            throw new DcSongReadException(DcSongReadFailure.ChecksumMismatch, $"'{file.Path}' does not match its SHA-256.");
    }

    /// <summary>The member's bytes, inflated under <paramref name="cap" />. The cap is
    /// checked against the DECLARED size first, then the inflater is stopped one byte
    /// past it — a member that produces more than it declared is refused.</summary>
    private static byte[] Inflate(string path, Member member, long cap)
    {
        if (member.UncompressedSize > cap)
        {
            throw new DcSongReadException(DcSongReadFailure.EntryTooLarge,
                $"'{member.Name}' declares {member.UncompressedSize} bytes; the limit is {cap}.");
        }

        var declared = (int)member.UncompressedSize;
        var buffer = new byte[declared];
        using var raw = new DcSongEntryStream(path, member.DataOffset, member.CompressedSize);
        try
        {
            using var source = member.Method == ZipDirectoryEntry.Deflate
                ? new DeflateStream(raw, CompressionMode.Decompress, leaveOpen: true)
                : (Stream)raw;
            var filled = 0;
            int read;
            while (filled < declared && (read = source.Read(buffer, filled, declared - filled)) > 0)
                filled += read;
            if (filled < declared)
                throw new DcSongReadException(DcSongReadFailure.Malformed, $"'{member.Name}' ends before its declared size.");

            Span<byte> probe = stackalloc byte[1];
            if (source.Read(probe) > 0)
                throw new DcSongReadException(DcSongReadFailure.EntryTooLarge, $"'{member.Name}' inflates past its declared size.");
        }
        catch (InvalidDataException ex)
        {
            throw new DcSongReadException(DcSongReadFailure.Malformed, $"'{member.Name}' does not inflate: {ex.Message}", ex);
        }

        return buffer;
    }

    private static void CrossCheck(DcSongManifest manifest, Dictionary<string, Member> members)
    {
        foreach (var file in manifest.Files)
        {
            if (!members.TryGetValue(file.Path, out var member))
                throw new DcSongReadException(DcSongReadFailure.EntryMissing, $"the manifest lists '{file.Path}', which the package lacks.");
            if (member.UncompressedSize != file.Size)
                throw new DcSongReadException(DcSongReadFailure.ChecksumMismatch, $"'{file.Path}' is not the size its manifest records.");

            // Only the chart and the images may be deflated; everything else is a
            // window of the file, which is what makes the audio seekable and what
            // keeps a member the reader never inflates from being a bomb.
            if (file.Role is not (DcSongFileRole.Chart or DcSongFileRole.Cover or DcSongFileRole.Banner)
                && member.Method != ZipDirectoryEntry.Stored)
            {
                throw new DcSongReadException(DcSongReadFailure.Malformed, $"'{file.Path}' must be stored, not compressed.");
            }
        }

        var listed = manifest.Files.Select(f => f.Path).ToHashSet(StringComparer.Ordinal);
        var unlisted = members.Keys.FirstOrDefault(name => name != DcSongFormat.ManifestPath && !listed.Contains(name));
        if (unlisted != null)
            throw new DcSongReadException(DcSongReadFailure.EntryUnlisted, $"the package holds '{unlisted}', which its manifest does not list.");
    }

    private static Dictionary<string, Member> ReadDirectory(FileStream file)
    {
        var length = file.Length;
        var tail = new byte[(int)Math.Min(length, 22 + ushort.MaxValue)];
        if (tail.Length < 22) throw new DcSongReadException(DcSongReadFailure.Unreadable, "the file is too short to be a package.");
        ReadAt(file, length - tail.Length, tail);

        var eocd = ZipCentralDirectory.TryReadEndOfCentralDirectory(tail, length);
        if (!eocd.Succeeded) throw Refusal(eocd.Refusal, "end of central directory");
        if (eocd.Record!.EntryCount > DcSongFormat.MaxEntries)
            throw new DcSongReadException(DcSongReadFailure.TooManyEntries, $"the package declares {eocd.Record.EntryCount} members.");

        var directory = new byte[eocd.Record.DirectorySize];
        ReadAt(file, eocd.Record.DirectoryOffset, directory);
        var parsed = ZipCentralDirectory.Parse(directory, eocd.Record, length, DcSongFormat.MaxEntries);
        if (!parsed.Succeeded) throw Refusal(parsed.Refusal, "central directory");

        var members = new Dictionary<string, Member>(StringComparer.Ordinal);
        var folded = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var entry in parsed.Entries)
        {
            if (entry.IsDirectory) continue;
            if (!folded.Add(entry.Name))
                throw new DcSongReadException(DcSongReadFailure.Malformed, $"the package holds '{entry.Name}' twice.");
            if (entry.CompressionMethod is not (ZipDirectoryEntry.Stored or ZipDirectoryEntry.Deflate) ||
                (entry.CompressionMethod == ZipDirectoryEntry.Stored && entry.CompressedSize != entry.UncompressedSize))
            {
                throw new DcSongReadException(DcSongReadFailure.Malformed, $"'{entry.Name}' uses an unsupported compression.");
            }

            members[entry.Name] = new Member(entry.Name, LocateData(file, entry, eocd.Record.DirectoryOffset),
                entry.CompressedSize, entry.UncompressedSize, entry.CompressionMethod);
        }

        return members;
    }

    /// <summary>Where a member's bytes start, read from its LOCAL header, which has to
    /// agree with the central record on the name and must not be encrypted.</summary>
    private static long LocateData(FileStream file, ZipDirectoryEntry entry, long directoryOffset)
    {
        var fixedPart = new byte[ZipCentralDirectory.LocalHeaderFixedBytes];
        ReadAt(file, entry.LocalHeaderOffset, fixedPart);
        var nameLength = BinaryPrimitives.ReadUInt16LittleEndian(fixedPart.AsSpan(26));
        var extraLength = BinaryPrimitives.ReadUInt16LittleEndian(fixedPart.AsSpan(28));
        var header = new byte[fixedPart.Length + nameLength + extraLength];
        if (entry.LocalHeaderOffset + header.Length > directoryOffset)
            throw new DcSongReadException(DcSongReadFailure.Malformed, $"'{entry.Name}' has a local header past its data.");
        ReadAt(file, entry.LocalHeaderOffset, header);

        var offset = ZipCentralDirectory.TryReadLocalDataOffset(header, 0)
                     ?? throw new DcSongReadException(DcSongReadFailure.Malformed, $"'{entry.Name}' has no local header.");
        if ((BinaryPrimitives.ReadUInt16LittleEndian(header.AsSpan(6)) & 1) != 0)
            throw new DcSongReadException(DcSongReadFailure.Malformed, $"'{entry.Name}' is encrypted.");

        var localName = ZipCentralDirectory.NormalizeEntryName(
            Encoding.UTF8.GetString(header, ZipCentralDirectory.LocalHeaderFixedBytes, nameLength));
        if (localName != entry.Name)
            throw new DcSongReadException(DcSongReadFailure.Malformed, $"'{entry.Name}' is named differently in its local header.");

        var dataOffset = entry.LocalHeaderOffset + offset;
        if (dataOffset + entry.CompressedSize > directoryOffset)
            throw new DcSongReadException(DcSongReadFailure.Malformed, $"'{entry.Name}' runs into the central directory.");
        return dataOffset;
    }

    private static void ReadAt(FileStream file, long offset, byte[] buffer)
    {
        file.Position = offset;
        try
        {
            file.ReadExactly(buffer);
        }
        catch (EndOfStreamException ex)
        {
            throw new DcSongReadException(DcSongReadFailure.Unreadable, "the file ends early: the download is truncated.", ex);
        }
    }

    private static DcSongReadException Refusal(ZipDirectoryRefusal refusal, string what) => refusal switch
    {
        ZipDirectoryRefusal.NoEndOfCentralDirectory =>
            new DcSongReadException(DcSongReadFailure.Unreadable, "the file is not a zip, or the download is truncated."),
        ZipDirectoryRefusal.TooManyEntries =>
            new DcSongReadException(DcSongReadFailure.TooManyEntries, "the package declares too many members."),
        ZipDirectoryRefusal.UnsafeEntryName =>
            new DcSongReadException(DcSongReadFailure.UnsafePath, "a member name is absolute or climbs out of the package."),
        _ => new DcSongReadException(DcSongReadFailure.Malformed, $"the {what} is unreadable ({refusal}).")
    };

    private sealed record Member(string Name, long DataOffset, long CompressedSize, long UncompressedSize, ushort Method);
}
