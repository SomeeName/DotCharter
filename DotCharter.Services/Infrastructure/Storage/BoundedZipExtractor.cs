using System.Buffers;
using System.Buffers.Binary;
using System.IO.Compression;

namespace DotCharter.Services.Infrastructure.Storage;

/// <summary>Why a bounded extraction refused an archive.</summary>
public enum ZipExtractionRefusal
{
    /// <summary>Everything was extracted inside the budget.</summary>
    None,

    /// <summary>The archive declares more members than the caller allows.</summary>
    TooManyEntries,

    /// <summary>Inflating the members passed the caller's byte budget. Measured
    /// on the bytes actually PRODUCED, never on a declared size.</summary>
    BudgetExceeded,

    /// <summary>A member resolved outside the destination directory (zip slip).</summary>
    UnsafePath,

    /// <summary>The file is not a readable zip (no end-of-central-directory record).</summary>
    Unreadable
}

/// <summary>Outcome of one bounded extraction. <see cref="BytesWritten" /> is
/// what actually landed on disk, so a refusal still reports what has to be
/// cleaned up.</summary>
public sealed record ZipExtractionResult(ZipExtractionRefusal Refusal, long BytesWritten, int FilesWritten)
{
    public bool Succeeded => Refusal == ZipExtractionRefusal.None;
}

/// <summary>
///     Extracts a zip that arrived from somewhere we do not trust — a LAN peer,
///     a roamed profile bundle — while counting the bytes it really produces.
///     <para>
///         <b>Why this exists.</b> The guard it replaces was
///         <c>Entries.Sum(e =&gt; e.Length)</c> against a byte cap, i.e. a
///         decision taken on numbers the SENDER wrote into the central
///         directory. Three things are wrong with that, and only the last one
///         is what most write-ups claim:
///         <list type="number">
///             <item>
///                 <see cref="Enumerable.Sum(IEnumerable{long})" /> is CHECKED.
///                 A zip64 archive can declare member sizes that overflow it,
///                 and the resulting <see cref="OverflowException" /> is in
///                 neither call site's catch filter — so the "guard" is itself
///                 a remote crash.
///             </item>
///             <item>
///                 Reaching <c>Entries</c> at all materialises one
///                 <see cref="ZipArchiveEntry" /> per member, so a 200 MB
///                 archive of empty members costs millions of live objects
///                 BEFORE any per-entry check could run. That is why the count
///                 is bounded from the end-of-central-directory record instead
///                 (<see cref="CheckDeclaredEntryCount" />), before
///                 <see cref="ZipFile.OpenRead(string)" /> is called.
///             </item>
///             <item>
///                 An UNDER-declared member does not actually buy expansion on
///                 .NET: the runtime passes the declared uncompressed size to
///                 <c>DeflateStream</c>, so the inflater stops there and the
///                 member extracts truncated. Measured, and pinned by
///                 <c>BoundedZipExtractorTests.AnUnderDeclaredMemberIsTruncatedByTheRuntime</c>
///                 — do not "restore" a declared-size guard on the strength of
///                 that theory.
///             </item>
///         </list>
///         Counting the bytes as they come out of the inflater is immune to all
///         three, and aborts mid-member the moment the budget is passed.
///     </para>
///     Pure apart from the filesystem writes, so it is unit-testable.
/// </summary>
public static class BoundedZipExtractor
{
    /// <summary>Members accepted by default. Chart projects and profile
    /// bundles are a handful of files; anything near this is not one.</summary>
    public const int DefaultMaxEntries = 4096;

    private const int CopyBufferBytes = 81920;

    /// <summary>
    ///     Reads how many members the archive DECLARES, straight out of the
    ///     end-of-central-directory record, without materialising any of them.
    ///     Null when there is no readable record (not a zip).
    ///     <para>
    ///         A zip64 archive stores <c>0xFFFF</c> here and its real count in a
    ///         separate record; that sentinel is returned verbatim, so it fails
    ///         every sane <c>maxEntries</c> and the archive is refused. That is
    ///         the wanted answer — nothing this app receives over the LAN has
    ///         65,535 members.
    ///     </para>
    /// </summary>
    public static int? TryReadDeclaredEntryCount(Stream stream)
    {
        const int recordBytes = 22; // EOCD with an empty comment
        const uint signature = 0x06054b50;

        var length = stream.Length;
        if (length < recordBytes) return null;

        // The record sits at the very end, behind an optional comment of up to
        // 64 KB — so that window is all that has to be read.
        var window = (int)Math.Min(length, recordBytes + ushort.MaxValue);
        var buffer = new byte[window];
        stream.Seek(length - window, SeekOrigin.Begin);
        stream.ReadExactly(buffer);

        for (var i = window - recordBytes; i >= 0; i--)
        {
            if (BinaryPrimitives.ReadUInt32LittleEndian(buffer.AsSpan(i)) != signature) continue;
            return BinaryPrimitives.ReadUInt16LittleEndian(buffer.AsSpan(i + 10));
        }

        return null;
    }

    /// <summary>Bounds the member count of an archive on disk BEFORE anyone
    /// opens it. <see cref="ZipExtractionRefusal.None" /> means it is safe to
    /// open.</summary>
    public static ZipExtractionRefusal CheckDeclaredEntryCount(string archivePath, int maxEntries = DefaultMaxEntries)
    {
        try
        {
            using var stream = new FileStream(archivePath, FileMode.Open, FileAccess.Read, FileShare.Read);
            var declared = TryReadDeclaredEntryCount(stream);
            if (declared == null) return ZipExtractionRefusal.Unreadable;
            return declared > maxEntries ? ZipExtractionRefusal.TooManyEntries : ZipExtractionRefusal.None;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return ZipExtractionRefusal.Unreadable;
        }
    }

    /// <summary>
    ///     Opens <paramref name="archivePath" /> (after the count pre-check) and
    ///     extracts it into <paramref name="destinationDirectory" /> under a
    ///     hard budget of really-inflated bytes.
    /// </summary>
    public static ZipExtractionResult ExtractBounded(
        string archivePath, string destinationDirectory, long maxTotalBytes,
        int maxEntries = DefaultMaxEntries,
        Func<ZipArchiveEntry, string?>? selectRelativePath = null)
    {
        var precheck = CheckDeclaredEntryCount(archivePath, maxEntries);
        if (precheck != ZipExtractionRefusal.None) return new ZipExtractionResult(precheck, 0, 0);

        using var archive = ZipFile.OpenRead(archivePath);
        return ExtractBounded(archive, destinationDirectory, maxTotalBytes, maxEntries, selectRelativePath);
    }

    /// <summary>
    ///     Extracts an already-open archive under the same budget. Callers that
    ///     read a member first (a manifest, say) use this overload — they are
    ///     then responsible for having run <see cref="CheckDeclaredEntryCount" />
    ///     before they opened it.
    ///     <para>
    ///         <paramref name="selectRelativePath" /> maps a member to its path
    ///         under the destination, or null to skip it entirely (that is how
    ///         a caller extracts only one subtree). Defaults to the member's own
    ///         <see cref="ZipArchiveEntry.FullName" />.
    ///     </para>
    ///     Throws the same <see cref="IOException" /> /
    ///     <see cref="InvalidDataException" /> family <see cref="ZipFile" /> does;
    ///     partial output is reported in the result so the caller can clean up.
    /// </summary>
    public static ZipExtractionResult ExtractBounded(
        ZipArchive archive, string destinationDirectory, long maxTotalBytes,
        int maxEntries = DefaultMaxEntries,
        Func<ZipArchiveEntry, string?>? selectRelativePath = null)
    {
        var root = Path.GetFullPath(destinationDirectory);
        var prefix = root.EndsWith(Path.DirectorySeparatorChar) ? root : root + Path.DirectorySeparatorChar;

        long written = 0;
        var files = 0;
        var buffer = ArrayPool<byte>.Shared.Rent(CopyBufferBytes);
        try
        {
            foreach (var entry in archive.Entries)
            {
                var relative = selectRelativePath == null ? entry.FullName : selectRelativePath(entry);
                if (string.IsNullOrEmpty(relative)) continue;
                if (relative[^1] is '/' or '\\') continue; // a directory member carries no bytes

                if (files >= maxEntries)
                    return new ZipExtractionResult(ZipExtractionRefusal.TooManyEntries, written, files);

                string target;
                try
                {
                    target = Path.GetFullPath(Path.Combine(root, relative));
                }
                catch (Exception ex) when (ex is ArgumentException or PathTooLongException or NotSupportedException)
                {
                    return new ZipExtractionResult(ZipExtractionRefusal.UnsafePath, written, files);
                }

                if (!target.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
                    return new ZipExtractionResult(ZipExtractionRefusal.UnsafePath, written, files);

                Directory.CreateDirectory(Path.GetDirectoryName(target)!);
                files++;

                using var source = entry.Open();
                using var file = new FileStream(target, FileMode.Create, FileAccess.Write,
                    FileShare.None, CopyBufferBytes);

                int read;
                while ((read = source.Read(buffer, 0, CopyBufferBytes)) > 0)
                {
                    // Count what came OUT of the inflater, not what the header
                    // promised — the header is the attacker's to write.
                    // Count what came OUT of the inflater, not what the header
                    // promised — the header is the attacker's to write.
                    written += read;
                    if (written > maxTotalBytes)
                        return new ZipExtractionResult(ZipExtractionRefusal.BudgetExceeded, written, files);
                    file.Write(buffer, 0, read);
                }
            }
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(buffer);
        }

        return new ZipExtractionResult(ZipExtractionRefusal.None, written, files);
    }
}
