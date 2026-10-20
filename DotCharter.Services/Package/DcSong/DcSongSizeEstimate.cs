using DotCharter.Services.Export.Audio;

namespace DotCharter.Services.Package.DcSong;

/// <summary>
///     About how big a package will be, before it is written: the song audio, the
///     preview clip and the stems, plus a fixed allowance for the manifest, the chart and
///     the zip records. Opus is a constant bit rate; FLAC is guessed from the source
///     (<see cref="FlacRatio" /> of 16-bit PCM). Images are not counted.
/// </summary>
public static class DcSongSizeEstimate
{
    /// <summary>FLAC of typical music, as a share of its 16-bit PCM. The format doc
    /// measured 0.77 on noise, the worst case; most songs land between 0.5 and 0.7.</summary>
    public const double FlacRatio = 0.65;

    /// <summary>The manifest, a deflated chart and the zip's own records.</summary>
    public const long OverheadBytes = 128 * 1024;

    /// <summary>The editor's decode: 48 kHz, 16-bit, stereo.</summary>
    private const double DecodedPcmBytesPerSecond = 48_000 * 2 * 2;

    private static readonly HashSet<string> PcmContainers = new(StringComparer.OrdinalIgnoreCase) { ".wav", ".aif", ".aiff" };

    public static double OpusBytesPerSecond => DcSongFormat.OpusBitrateKbps * 1000 / 8.0;

    /// <param name="lengthMs">The song's length.</param>
    /// <param name="lossless">FLAC rather than Opus.</param>
    /// <param name="audioPath">The source audio, for its container.</param>
    /// <param name="audioBytes">The source audio's size.</param>
    /// <param name="stems">The stems going in; an <c>.opus</c> one is stored as it is.</param>
    public static long Bytes(double lengthMs, bool lossless, string audioPath, long audioBytes,
        IEnumerable<DcSongStemCandidate> stems)
    {
        ArgumentNullException.ThrowIfNull(stems);
        var seconds = Math.Max(0, lengthMs) / 1000;
        var song = lossless ? LosslessBytes(seconds, audioPath, audioBytes) : seconds * OpusBytesPerSecond;
        var preview = Math.Min(seconds, PreviewClip.DefaultLengthMs / 1000) * OpusBytesPerSecond;
        var stemBytes = stems.Sum(s => IsOpus(s.SourcePath) ? s.Bytes : seconds * OpusBytesPerSecond);
        return (long)Math.Round(song + preview + stemBytes) + OverheadBytes;
    }

    /// <summary>The stems alone, as <see cref="Bytes" /> counts them.</summary>
    public static long StemBytes(double lengthMs, IEnumerable<DcSongStemCandidate> stems)
    {
        ArgumentNullException.ThrowIfNull(stems);
        var seconds = Math.Max(0, lengthMs) / 1000;
        return (long)Math.Round(stems.Sum(s => IsOpus(s.SourcePath) ? s.Bytes : seconds * OpusBytesPerSecond));
    }

    private static double LosslessBytes(double seconds, string audioPath, long audioBytes)
    {
        var extension = Path.GetExtension(audioPath);
        if (audioBytes > 0 && extension.Equals(".flac", StringComparison.OrdinalIgnoreCase)) return audioBytes;
        if (audioBytes > 0 && PcmContainers.Contains(extension)) return audioBytes * FlacRatio;
        return seconds * DecodedPcmBytesPerSecond * FlacRatio;
    }

    private static bool IsOpus(string path) => Path.GetExtension(path).Equals(".opus", StringComparison.OrdinalIgnoreCase);
}
