using System.Globalization;

namespace DotCharter.Services.Infrastructure;

/// <summary>
///     The size ceilings for files the app is handed and then reads whole:
///     score containers, MIDI, lyric text, WAV analysis input. Every number is
///     set generously past anything an author produces and exists only so that
///     a crafted or corrupt file is refused with a "file too large" message
///     BEFORE it is read, instead of exhausting memory.
///     <para>
///         They live together so a reader that grows a cap and its sibling that
///         does not are visible in one place; the checks themselves are the
///         one-line <see cref="Exceeds" /> at each call site, made against the
///         length the file system reports and never against bytes already read.
///     </para>
/// </summary>
public static class ImportSizeLimits
{
    private const long Mebibyte = 1024L * 1024L;

    /// <summary>A standard MIDI file is tens of KB; a long, dense score is a few MB.</summary>
    public const long MidiFileBytes = 64 * Mebibyte;

    /// <summary>An UltraStar or LRC text file is well under 1 MB.</summary>
    public const long LyricsTextFileBytes = 16 * Mebibyte;

    /// <summary>The WAV an analysis run decodes whole. 512 MiB is about 50
    /// minutes of 44.1 kHz 16-bit stereo; a song is 3 to 10.</summary>
    public const long WavFileBytes = 512 * Mebibyte;

    /// <summary>A Guitar Pro 6 <c>.gpx</c> container, read whole before it is
    /// inflated. Real scores are a few hundred KB.</summary>
    public const long GpxContainerBytes = 128 * Mebibyte;

    /// <summary>The uncompressed <c>score.gpif</c> of a Guitar Pro 7+ file. A
    /// large orchestral score is a few MB of XML; GP archives may embed audio,
    /// so the archive itself is NOT capped, only the score entry.</summary>
    public const long GpScoreEntryBytes = 128 * Mebibyte;

    /// <summary>Members a Guitar Pro 7+ archive may declare. A real one holds
    /// about ten (score, preview, track images, embedded audio).</summary>
    public const int GpArchiveMaxEntries = 1024;

    /// <summary>True when <paramref name="length" /> is past <paramref name="cap" />.</summary>
    public static bool Exceeds(long length, long cap) => length > cap;

    /// <summary>Whole mebibytes, rounded up, for a message ("12 MB").</summary>
    public static long ToMegabytes(long bytes) => (bytes + Mebibyte - 1) / Mebibyte;

    /// <summary>Throws <see cref="ImportFileTooLargeException" /> when
    /// <paramref name="length" /> is past <paramref name="cap" />.</summary>
    public static void RequireWithin(long length, long cap, string what)
    {
        if (Exceeds(length, cap)) throw new ImportFileTooLargeException(what, length, cap);
    }
}

/// <summary>
///     A file or archive member is longer than the import allows. An
///     <see cref="IOException" />, so every handler that already reports an
///     unreadable file reports this too; a launcher that wants the dedicated
///     localized message catches it first and reads the sizes off it.
/// </summary>
public sealed class ImportFileTooLargeException(string what, long length, long cap)
    : IOException(string.Create(CultureInfo.InvariantCulture,
        $"{what} is too large ({ImportSizeLimits.ToMegabytes(length)} MB; the limit is {ImportSizeLimits.ToMegabytes(cap)} MB)."))
{
    /// <summary>What was refused: "The WAV file".</summary>
    public string What { get; } = what;

    /// <summary>The size found, in bytes (for a stream, the bytes read when it passed the cap).</summary>
    public long Length { get; } = length;

    /// <summary>The limit that was passed, in bytes.</summary>
    public long Cap { get; } = cap;
}
