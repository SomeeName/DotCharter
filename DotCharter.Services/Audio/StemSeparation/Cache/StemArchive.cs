using System.IO.Compression;
using System.Security.Cryptography;

namespace DotCharter.Services.Audio.StemSeparation.Cache;

/// <summary>
/// Reads/writes a single <c>.dcstems</c> archive per song: a ZIP containing a
/// <c>manifest.json</c> plus one encoded file per stem. Keeping the whole stem
/// set in one compressed container (instead of a folder of loose WAVs) is the
/// "package the stems, unzip while playing" storage model — the archive is the
/// durable artifact; stems are expanded on demand at playback.
/// </summary>
public static class StemArchive
{
    public const string ManifestEntryName = "manifest.json";
    public const string ArchiveExtension = ".dcstems";

    /// <summary>
    /// Writes <paramref name="archivePath"/> from a manifest and a set of
    /// already-encoded stem files. Opus/FLAC payloads are already compressed so
    /// the ZIP layer uses <see cref="CompressionLevel.NoCompression"/> for them;
    /// WAV payloads are deflated.
    /// </summary>
    public static void Write(
        string archivePath,
        StemCacheManifest manifest,
        IReadOnlyList<(string EntryName, string SourceFile)> stemFiles)
    {
        ArgumentNullException.ThrowIfNull(manifest);
        ArgumentNullException.ThrowIfNull(stemFiles);

        var dir = Path.GetDirectoryName(archivePath);
        if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);
        if (File.Exists(archivePath)) File.Delete(archivePath);

        var level = manifest.Codec == StemCodec.Wav
            ? CompressionLevel.Optimal
            : CompressionLevel.NoCompression;

        using (var zip = ZipFile.Open(archivePath, ZipArchiveMode.Create))
        {
            var manifestEntry = zip.CreateEntry(ManifestEntryName, CompressionLevel.Optimal);
            using (var writer = new StreamWriter(manifestEntry.Open()))
                writer.Write(manifest.ToJson());

            foreach (var (entryName, sourceFile) in stemFiles)
                zip.CreateEntryFromFile(sourceFile, entryName, level);
        }

        // Force the archive to the platter BEFORE the caller renames it into
        // place. Closing the ZipArchive/FileStream only gets the bytes as far as
        // the OS page cache, so a power cut could persist the RENAME while the
        // contents were still in flight — an atomically-swapped-in truncated
        // .dcstems, i.e. losing a never-regenerated artifact to a crash. The
        // staged-then-swap half of the pattern was already right; this is the
        // durability half AtomicFile's own doc says the hand-rolled copies of
        // this pattern all miss.
        AtomicFile.FlushFile(archivePath);
    }

    /// <summary>
    /// Writes a copy of <paramref name="sourceArchive"/> to <paramref name="destArchive"/>
    /// with <paramref name="manifest"/> in place of its own: every other entry is
    /// copied byte for byte (decompressed and stored again at the level
    /// <see cref="Write"/> uses for the codec), then the file is flushed to disk.
    /// The source is only read. For re-keying an archive; the caller verifies
    /// the copy with <see cref="HasSamePayload"/> before it replaces anything.
    /// </summary>
    public static void Rewrite(string sourceArchive, string destArchive, StemCacheManifest manifest)
    {
        ArgumentNullException.ThrowIfNull(manifest);
        var level = manifest.Codec == StemCodec.Wav
            ? CompressionLevel.Optimal
            : CompressionLevel.NoCompression;

        using (var source = OpenForRead(sourceArchive))
        using (var dest = ZipFile.Open(destArchive, ZipArchiveMode.Create))
        {
            var manifestEntry = dest.CreateEntry(ManifestEntryName, CompressionLevel.Optimal);
            using (var writer = new StreamWriter(manifestEntry.Open()))
                writer.Write(manifest.ToJson());

            foreach (var entry in source.Entries)
            {
                if (string.Equals(entry.FullName, ManifestEntryName, StringComparison.OrdinalIgnoreCase)) continue;
                var copy = dest.CreateEntry(entry.FullName, level);
                copy.LastWriteTime = entry.LastWriteTime;
                using var from = entry.Open();
                using var to = copy.Open();
                from.CopyTo(to);
            }
        }

        AtomicFile.FlushFile(destArchive);
    }

    /// <summary>
    /// True when both archives hold the same entries besides the manifest, each with
    /// identical decompressed bytes (length and SHA-256). Reading either one fully is
    /// the point: a copy that cannot be read back is not a copy.
    /// </summary>
    public static bool HasSamePayload(string archiveA, string archiveB)
    {
        var a = PayloadDigests(archiveA);
        var b = PayloadDigests(archiveB);
        return a.Count == b.Count &&
               a.All(kv => b.TryGetValue(kv.Key, out var other) && string.Equals(kv.Value, other, StringComparison.Ordinal));
    }

    private static Dictionary<string, string> PayloadDigests(string archivePath)
    {
        var digests = new Dictionary<string, string>(StringComparer.Ordinal);
        using var zip = OpenForRead(archivePath);
        foreach (var entry in zip.Entries)
        {
            if (string.Equals(entry.FullName, ManifestEntryName, StringComparison.OrdinalIgnoreCase)) continue;
            using var stream = entry.Open();
            using var sha = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
            var buffer = new byte[1 << 16];
            long length = 0;
            int read;
            while ((read = stream.Read(buffer, 0, buffer.Length)) > 0)
            {
                sha.AppendData(buffer, 0, read);
                length += read;
            }

            digests[entry.FullName] = length + ":" + Convert.ToHexString(sha.GetHashAndReset());
        }

        return digests;
    }

    /// <summary>
    /// Opens an archive for reading with a 64 KB sequential-scan stream.
    /// Bounds the declared entry count on that same stream before ZIP entries
    /// can be materialized. A refusal keeps the archive intact.
    /// <para><see cref="ZipFile.OpenRead(string)" /> gives no control over either,
    /// so it reads a tens-of-MB <c>.dcstems</c> through the BCL's 4 KB default
    /// with no access hint — the same mistake <c>WaveformDiskCache</c> and
    /// <c>StemAudioSignature</c> both already avoid.</para>
    /// <para><b>The try/catch is not defensive tidying.</b> The
    /// <see cref="ZipArchive" /> ctor THROWS on a file that is not a zip, and it
    /// adopts the stream only once it has succeeded — so handing it a stream we
    /// opened ourselves leaks the file HANDLE on exactly the corrupt-archive
    /// path, and on Windows an open handle makes that file undeletable. That is
    /// <see cref="ReadManifest" /> reporting "corrupt" while the coordinator's
    /// delete silently fails, i.e. a truncated <c>.dcstems</c> wedging the song
    /// out of its stems forever (<c>StemCacheCoordinatorTests
    /// .Corrupt_archive_is_deleted_and_reported_as_a_miss</c>).
    /// <see cref="ZipFile.OpenRead(string)" /> does this same dance internally;
    /// replacing it means taking the dance with it.</para>
    /// </summary>
    private static ZipArchive OpenForRead(string archivePath, int maxEntries = MaxEntries)
    {
        var stream = new FileStream(archivePath, FileMode.Open, FileAccess.Read, FileShare.Read,
            1 << 16, FileOptions.SequentialScan);
        try
        {
            // No ZIP (including ZIP64) can be shorter than its 22-byte end record.
            if (stream.Length < 22) throw new InvalidDataException("Stem archive is truncated before its end record.");
            var check = ZipDirectoryBudget.Check(stream, maxEntries);
            if (check == ZipExtractionRefusal.TooManyEntries)
            {
                throw new StemArchiveRefusedException(
                    $"Stem archive '{Path.GetFileName(archivePath)}' contains more than {maxEntries} entries; refusing to read it.");
            }
            if (check != ZipExtractionRefusal.None) throw new StemArchiveRefusedException("Stem archive directory could not be bounded; keeping the archive.");

            stream.Position = 0;
            return new ZipArchive(stream, ZipArchiveMode.Read);
        }
        catch (IOException ex)
        {
            stream.Dispose();
            throw new StemArchiveRefusedException($"Stem archive could not be read safely: {ex.Message}");
        }
        catch
        {
            stream.Dispose();
            throw;
        }
    }

    /// <summary>Reads just the manifest from an archive; null if missing/corrupt.
    /// Throws <see cref="StemArchiveRefusedException"/> for an entry-count or
    /// manifest-byte limit, so the coordinator keeps the archive.</summary>
    public static StemCacheManifest? ReadManifest(string archivePath)
    {
        try
        {
            using var zip = OpenForRead(archivePath);
            var entry = zip.GetEntry(ManifestEntryName);
            if (entry == null) return null;
            var text = BoundedZipExtractor.ReadEntryText(entry, MaxManifestBytes)
                ?? throw new StemArchiveRefusedException(
                    $"Stem archive '{Path.GetFileName(archivePath)}' manifest exceeds {MaxManifestBytes} inflated bytes.");

            return StemCacheManifest.FromJson(text);
        }
        catch (InvalidDataException) { return null; }
        catch (IOException) { return null; }
    }

    /// <summary>
    /// Reads one stem entry's bytes straight out of the archive, writing nothing to
    /// disk; <c>null</c> when the archive has no such entry. For a reader that only
    /// needs one stem in memory, where <see cref="ExtractStems(string, string)"/>
    /// would unpack the whole set into a directory.
    /// </summary>
    public static byte[]? ReadEntryBytes(string archivePath, string entryName)
    {
        using var zip = OpenForRead(archivePath);
        var entry = zip.GetEntry(entryName);
        if (entry == null) return null;
        using var stream = entry.Open();
        using var buffer = new MemoryStream(entry.Length is > 0 and < int.MaxValue ? (int)entry.Length : 0);
        stream.CopyTo(buffer);
        return buffer.ToArray();
    }

    /// <summary>Members a stem archive may hold: the manifest plus a handful of stems.</summary>
    internal const int MaxEntries = 64;

    /// <summary>Actual inflated manifest bytes accepted before parsing JSON.</summary>
    internal const int MaxManifestBytes = 1024 * 1024;

    /// <summary>Inflated-byte ceiling for one archive. Far above any real stem set
    /// (hours of lossless audio) and far below "fill the disk".</summary>
    internal const long MaxInflatedBytes = 16L * 1024 * 1024 * 1024;

    /// <summary>
    /// Extracts every stem file (not the manifest) into <paramref name="destDir"/>,
    /// keyed by the entry's file name, and returns the extracted absolute paths.
    /// Goes through <see cref="BoundedZipExtractor"/>: a member whose name carries a
    /// path separator, drive colon or a bare <c>..</c>/<c>.</c> name, too many members, or more inflated
    /// bytes than the budget makes it throw <see cref="StemArchiveRefusedException"/>.
    /// That is NOT corruption: the coordinator keeps the archive (stems are never
    /// regenerated) and reports a miss.
    /// </summary>
    public static IReadOnlyList<string> ExtractStems(string archivePath, string destDir)
        => ExtractStems(archivePath, destDir, MaxEntries, MaxInflatedBytes);

    internal static IReadOnlyList<string> ExtractStems(
        string archivePath, string destDir, int maxEntries, long maxInflatedBytes)
    {
        Directory.CreateDirectory(destDir);
        var count = BoundedZipExtractor.CheckDeclaredEntryCount(archivePath, maxEntries);
        if (count == ZipExtractionRefusal.TooManyEntries)
        {
            throw new StemArchiveRefusedException(
                $"Stem archive '{Path.GetFileName(archivePath)}' declares more than {maxEntries} entries; refusing to expand it.");
        }

        var extracted = new List<string>();
        using var zip = OpenForRead(archivePath, maxEntries);
        var result = BoundedZipExtractor.ExtractBounded(zip, destDir, maxInflatedBytes, maxEntries, entry =>
        {
            if (string.Equals(entry.FullName, ManifestEntryName, StringComparison.OrdinalIgnoreCase))
                return null;
            if (string.IsNullOrEmpty(entry.Name)) return null; // directory entry
            var name = entry.FullName;
            if (name.Contains('\\') || name.Contains('/') || name.Contains(':') || name == ".." || name == ".")
            {
                throw new StemArchiveRefusedException(
                    $"Stem archive '{Path.GetFileName(archivePath)}' has an entry name that leaves the destination ('{name}'); refusing to expand it.");
            }

            extracted.Add(Path.Combine(destDir, name));
            return name;
        });
        if (!result.Succeeded)
        {
            throw new StemArchiveRefusedException(
                $"Stem archive '{Path.GetFileName(archivePath)}' refused: {result.Refusal}.");
        }

        return extracted;
    }
}
