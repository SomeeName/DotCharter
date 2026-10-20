namespace DotCharter.Services.Audio.StemSeparation.Cache;

/// <summary>
/// Codec a stem is stored as inside the cache archive. Recorded in the manifest
/// so a cache written by one machine (e.g. with ffmpeg → Opus) is readable by
/// another (which knows how to decode it back to WAV for JUCE).
/// </summary>
public enum StemCodec
{
    /// <summary>Uncompressed PCM WAV — always readable, the no-ffmpeg fallback.</summary>
    Wav = 0,

    /// <summary>FLAC — lossless, ~50% of WAV. Needs ffmpeg.</summary>
    Flac = 1,

    /// <summary>Opus in an Ogg container — lossy, best size/quality. Needs ffmpeg.</summary>
    Opus = 2,
}

public static class StemCodecExtensions
{
    /// <summary>File extension (no dot) used for a stem stored with this codec.</summary>
    public static string FileExtension(this StemCodec codec) => codec switch
    {
        StemCodec.Opus => "opus",
        StemCodec.Flac => "flac",
        _ => "wav",
    };

    public static bool TryParse(string? value, out StemCodec codec)
    {
        switch (value?.Trim().ToLowerInvariant())
        {
            case "opus": codec = StemCodec.Opus; return true;
            case "flac": codec = StemCodec.Flac; return true;
            case "wav": codec = StemCodec.Wav; return true;
            default: codec = StemCodec.Wav; return false;
        }
    }
}
