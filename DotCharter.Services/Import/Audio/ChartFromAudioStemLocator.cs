using System.Buffers.Binary;
using DotCharter.Services.Ai.Match;
using DotCharter.Services.Audio.Lyrics;
using DotCharter.Services.Audio.StemSeparation.Cache;

namespace DotCharter.Services.Import.Audio;

/// <summary>
///     An already-separated, LOSSLESS stem of the arrangement's instrument, which
///     chart-from-audio listens to instead of the mix. <see cref="ArchiveEntry" /> is
///     set when the stem is read straight out of a <c>.dcstems</c> archive at
///     <see cref="Path" />; otherwise <see cref="Path" /> is a loose WAV.
/// </summary>
/// <param name="StemName">The separated source's name (<c>guitar</c>, <c>bass</c>, …).</param>
/// <param name="Path">The WAV file, or the archive holding it.</param>
/// <param name="ArchiveEntry">The entry inside the archive, or <c>null</c> for a loose file.</param>
public sealed record ChartFromAudioStem(string StemName, string Path, string? ArchiveEntry = null)
{
    /// <summary>The stem's WAV bytes. Writes nothing to disk, also for an archive entry.</summary>
    public byte[] ReadWavBytes() => ArchiveEntry == null
        ? File.ReadAllBytes(Path)
        : StemArchive.ReadEntryBytes(Path, ArchiveEntry)
          ?? throw new FileNotFoundException($"Stem archive '{Path}' no longer holds '{ArchiveEntry}'.");
}

/// <summary>Finds the stem chart-from-audio should listen to, or <c>null</c> for the mix.</summary>
public interface IChartFromAudioStemLocator
{
    /// <summary>
    ///     The lossless stem of <paramref name="arrangementType" />'s instrument that
    ///     was ALREADY separated from <paramref name="mixWavPath" />, or <c>null</c>.
    ///     Never separates, never writes, never throws.
    /// </summary>
    ChartFromAudioStem? TryLocate(string mixWavPath, ArrangementType? arrangementType);
}

/// <summary>
///     Hands chart-from-audio the arrangement instrument's separated stem when one
/// already exists for the audio it was given. The detector is Live Play's,
///     built for a clean DI guitar; on a full mix it hears vocals, drums and the
///     other guitar, which is what the corpus score says limits the draft.
///     <para>
///         <b>Stems are NEVER separated here and nothing is written.</b> Separating a
///         song is minutes of GPU and the user asked for a draft, not a separation —
///         the same restraint as <see cref="VocalStemLocator" />. Only what the app
///         already produced is read.
///     </para>
///     <para>
///         <b>Lossless only; an Opus stem is playback-only and is never fed to note
///         detection.</b> The durable cache stores Opus, and the WAVs the app expands
///         from it are DECODED Opus — so a folder carrying the expansion marker
///         (<see cref="StemCacheStore.ExpandedMarkerFileName" />) is accepted only when
///         the archive it names stores WAV. A folder without the marker holds the
///         separator's own output, which is lossless.
///     </para>
///     <para>
///         Where it looks, all keyed to this exact audio, first hit wins:
///         <list type="number">
///         <item>the cache archive for the audio's own-chart key, when its codec is
///         WAV (read in memory, never extracted);</item>
///         <item>the separator's raw output the tone matcher left under the cache
///         root (<c>match-eval/&lt;token&gt;/raw</c>), and its expansion beside it;</item>
///         <item>the <c>stems/</c> folder beside the audio, where separation writes —
///         not keyed by content, so a stem there must also be as long as the mix.</item>
///         </list>
///         A library song is keyed on its source WEM, which this WAV-only entry point
///         cannot see, so a PSARC session's archive is not found here.
///     </para>
/// </summary>
public sealed class ChartFromAudioStemLocator : IChartFromAudioStemLocator
{
    /// <summary>How far a stem's length may differ from the mix's and still be its stem.</summary>
    public const double MaxLengthMismatchSeconds = 0.25;

    private readonly string _cacheRoot;

    /// <summary>Reads the app's own stem cache.</summary>
    public ChartFromAudioStemLocator() : this(StemCacheStore.DefaultRoot())
    {
    }

    /// <summary>Reads the stem cache rooted at <paramref name="cacheRoot" /> (a test's own directory).</summary>
    public ChartFromAudioStemLocator(string cacheRoot) =>
        _cacheRoot = cacheRoot ?? throw new ArgumentNullException(nameof(cacheRoot));

    /// <summary>
    ///     The separated sources that may stand in for the mix, best first. A guitar
    ///     arrangement takes <c>guitar</c>, then <c>other</c> (a four-source model has
    ///     no guitar target) — the one table <see cref="StemIsolator.AcceptableSourcesFor" />
    ///     keeps; a bass arrangement takes <c>bass</c> only. No arrangement drafts as a
    ///     guitar (the analyzer's standard map); a vocal arrangement has no stem to take.
    /// </summary>
    public static IReadOnlyList<string> SourcesFor(ArrangementType? arrangementType) => arrangementType switch
    {
        ArrangementType.Vocal => [],
        ArrangementType.Bass => StemIsolator.AcceptableSourcesFor(ToneStems.Bass),
        _ => StemIsolator.AcceptableSourcesFor(ToneStems.Guitar)
    };

    /// <inheritdoc />
    public ChartFromAudioStem? TryLocate(string mixWavPath, ArrangementType? arrangementType)
    {
        var wanted = SourcesFor(arrangementType);
        if (wanted.Count == 0 || string.IsNullOrWhiteSpace(mixWavPath)) return null;
        try
        {
            if (!File.Exists(mixWavPath)) return null;
            var key = StemCacheSource.TryResolve(StemCacheIdentity.ForAudioFile(mixWavPath), mixWavPath)?.Key;
            if (key != null)
            {
                if (FromArchive(Path.Combine(_cacheRoot, key.DirectoryToken + StemArchive.ArchiveExtension), key,
                        mixWavPath, wanted) is { } archived)
                {
                    return archived;
                }

                var playback = Path.Combine(_cacheRoot, StemIsolator.PlaybackFolderName, key.DirectoryToken);
                if (FromFolder(Path.Combine(playback, StemIsolator.RawFolderName), wanted, null) is { } raw) return raw;
                if (FromFolder(playback, wanted, null) is { } expanded) return expanded;
            }

            var directory = Path.GetDirectoryName(Path.GetFullPath(mixWavPath));
            if (string.IsNullOrEmpty(directory) || TryReadWavSeconds(mixWavPath) is not { } mixSeconds) return null;
            return FromFolder(Path.Combine(directory, VocalStemLocator.StemsFolderName), wanted, mixSeconds);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidDataException)
        {
            // A missing folder, an ACL or a racing delete is a plain "no stem": the
            // draft falls back to the mix, which is what it did before stems existed.
            return null;
        }
    }

    /// <summary>
    ///     The wanted stem out of the audio's own archive, when that archive stores WAV.
    ///     The same two checks <see cref="StemCacheStore" /> makes before serving a hit:
    ///     the archive records THIS key, and — when it recorded the whole-file hash
    /// — the audio still hashes the same.
    /// </summary>
    private static ChartFromAudioStem? FromArchive(string archivePath, StemCacheKey key, string mixWavPath,
        IReadOnlyList<string> wanted)
    {
        if (!File.Exists(archivePath)) return null;
        var manifest = StemArchive.ReadManifest(archivePath);
        if (manifest is not { Codec: StemCodec.Wav } || !key.AcceptsRecordedKey(manifest.Key)) return null;
        if (!string.IsNullOrEmpty(manifest.SourceFullHash) &&
            !string.Equals(StemAudioSignature.ComputeFullFileHash(mixWavPath), manifest.SourceFullHash,
                StringComparison.OrdinalIgnoreCase))
        {
            return null;
        }
        foreach (var name in wanted)
        {
            var entry = manifest.Stems.FirstOrDefault(s => string.Equals(s.Name, name, StringComparison.OrdinalIgnoreCase));
            if (entry != null && entry.FileName.EndsWith(".wav", StringComparison.OrdinalIgnoreCase))
                return new ChartFromAudioStem(name, archivePath, entry.FileName);
        }

        return null;
    }

    /// <summary>The first wanted stem in <paramref name="folder" /> whose WAVs are lossless.</summary>
    /// <param name="folder">The folder to look in.</param>
    /// <param name="wanted">Stem names, best first.</param>
    /// <param name="mixSeconds">When the folder is not keyed to the audio's content, the
    /// mix's length a stem must match; <c>null</c> when the folder IS keyed.</param>
    private ChartFromAudioStem? FromFolder(string folder, IReadOnlyList<string> wanted, double? mixSeconds)
    {
        if (!Directory.Exists(folder) || !IsLossless(folder)) return null;
        foreach (var name in wanted)
        {
            var path = Path.Combine(folder, name + ".wav");
            if (!File.Exists(path)) continue;
            if (mixSeconds is { } expected &&
                !(TryReadWavSeconds(path) is { } actual && Math.Abs(actual - expected) <= MaxLengthMismatchSeconds))
            {
                continue;
            }

            return new ChartFromAudioStem(name, path);
        }

        return null;
    }

    /// <summary>
    ///     True when the WAVs in <paramref name="folder" /> are not decoded from a lossy
    ///     archive: no expansion marker (the separator's own output), or a marker naming
    ///     an archive that stores WAV. A marker naming an archive that is gone or
    ///     unreadable is refused — what it was decoded from cannot be told.
    /// </summary>
    private bool IsLossless(string folder)
    {
        var marker = Path.Combine(folder, StemCacheStore.ExpandedMarkerFileName);
        if (!File.Exists(marker)) return true;
        var token = File.ReadAllText(marker).Trim();
        if (token.Length == 0 || token.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0) return false;
        var archive = Path.Combine(_cacheRoot, token + StemArchive.ArchiveExtension);
        return File.Exists(archive) && StemArchive.ReadManifest(archive) is { Codec: StemCodec.Wav };
    }

    /// <summary>A WAV file's length in seconds from its header alone, or <c>null</c>.</summary>
    internal static double? TryReadWavSeconds(string path)
    {
        using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
        Span<byte> header = stackalloc byte[12];
        if (stream.Read(header) != 12 || !header[..4].SequenceEqual("RIFF"u8) || !header.Slice(8, 4).SequenceEqual("WAVE"u8))
            return null;

        Span<byte> chunk = stackalloc byte[8];
        Span<byte> fmt = stackalloc byte[16];
        var byteRate = 0;
        while (stream.Read(chunk) == 8)
        {
            var size = BinaryPrimitives.ReadUInt32LittleEndian(chunk[4..]);
            if (chunk[..4].SequenceEqual("fmt "u8) && size >= 16)
            {
                if (stream.Read(fmt) != 16) return null;
                byteRate = BinaryPrimitives.ReadInt32LittleEndian(fmt[8..]);
                stream.Seek(size - 16 + (size & 1), SeekOrigin.Current);
            }
            else if (chunk[..4].SequenceEqual("data"u8))
            {
                var available = stream.Length - stream.Position;
                var bytes = Math.Min(size, available);
                return byteRate > 0 ? bytes / (double)byteRate : null;
            }
            else
            {
                stream.Seek(size + (size & 1), SeekOrigin.Current);
            }
        }

        return null;
    }
}
