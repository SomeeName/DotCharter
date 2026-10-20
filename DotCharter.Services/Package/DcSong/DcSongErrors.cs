using DotCharter.Services.Library;

namespace DotCharter.Services.Package.DcSong;

/// <summary>
///     Why a <c>.dcsong</c> could not be read. Each member names a resx key through
///     <see cref="DcSongReadException.ResourceKey" />, so the reason reaches the user
///     in their language; the exception's <see cref="Exception.Message" /> is the
///     English detail for the log.
/// </summary>
public enum DcSongReadFailure
{
    /// <summary>The file is missing, unreadable, or not a zip (a truncated download lands here).</summary>
    Unreadable,

    /// <summary>The zip structure is damaged, or uses zip64 / several volumes.</summary>
    Malformed,

    /// <summary>A member name is absolute, climbs out with <c>..</c>, or is otherwise not a safe path.</summary>
    UnsafePath,

    /// <summary>More members than a package can have.</summary>
    TooManyEntries,

    /// <summary>A member is larger than the format allows (the zip-bomb guard).</summary>
    EntryTooLarge,

    /// <summary>No <c>manifest.json</c>.</summary>
    ManifestMissing,

    /// <summary>The manifest is not valid JSON, not a <c>.dcsong</c> manifest, or breaks a rule of the format.</summary>
    ManifestInvalid,

    /// <summary>The package was written by a newer major version of the format.</summary>
    NewerVersion,

    /// <summary>The manifest lists a member the zip does not have.</summary>
    EntryMissing,

    /// <summary>The zip has a member the manifest does not list.</summary>
    EntryUnlisted,

    /// <summary>A member's bytes do not match the size or SHA-256 the manifest records.</summary>
    ChecksumMismatch,

    /// <summary>The chart verified but is not a chart this build can open.</summary>
    ChartInvalid,

    /// <summary>Opening the package as a project needs its Opus audio decoded, and no
    /// ffmpeg was found (the audio engine does not decode Opus).</summary>
    DecoderMissing,

    /// <summary>ffmpeg ran on the package's audio and produced nothing usable.</summary>
    DecodeFailed
}

/// <summary>A <c>.dcsong</c> refused, with the reason.</summary>
public sealed class DcSongReadException(DcSongReadFailure reason, string detail, Exception? inner = null)
    : Exception(detail, inner), ILibraryReadError
{
    public DcSongReadFailure Reason { get; } = reason;

    /// <summary>
    ///     The resx key naming <see cref="Reason" />. A COMPUTED key family, so it
    ///     is registered in
    ///     <c>LocalizationSweepTests.EveryComputedKeyFamily_IsCompleteInEveryLanguage</c>.
    /// </summary>
    public string ResourceKey => KeyFor(Reason);

    public static string KeyFor(DcSongReadFailure reason) => $"DcSong_ReadFailure_{reason}";
}

/// <summary>Why a <c>.dcsong</c> could not be written.</summary>
public enum DcSongWriteFailure
{
    /// <summary>The song audio file does not exist.</summary>
    AudioMissing,

    /// <summary>No ffmpeg was found; it encodes both Opus and FLAC.</summary>
    FfmpegMissing,

    /// <summary>ffmpeg ran and produced no usable file.</summary>
    EncodeFailed,

    /// <summary>A cover or banner that was asked for is missing, too large, or not a PNG or JPEG.</summary>
    ImageUnsupported,

    /// <summary>A stem has a bad or duplicate name, a missing file, or there are too many.</summary>
    InvalidStem
}

/// <summary>A <c>.dcsong</c> write refused, with the reason. Nothing is left at the destination.</summary>
public sealed class DcSongWriteException(DcSongWriteFailure reason, string detail, Exception? inner = null)
    : Exception(detail, inner)
{
    public DcSongWriteFailure Reason { get; } = reason;

    /// <summary>The resx key naming <see cref="Reason" /> (a computed key family).</summary>
    public string ResourceKey => KeyFor(Reason);

    public static string KeyFor(DcSongWriteFailure reason) => $"DcSong_WriteFailure_{reason}";
}

/// <summary>Something the writer did that the user should know about. The package was written.</summary>
public enum DcSongWriteWarning
{
    /// <summary>The project's cover art is missing or not a PNG or JPEG, so the package has none.</summary>
    CoverSkipped,

    /// <summary>A lossless package was asked for, but the source is lossy: the package
    /// holds its decode, losslessly.</summary>
    LossySourceInLosslessPackage,

    /// <summary>The source could not be copied into FLAC as it is, so its 48 kHz
    /// 16-bit decode was stored instead.</summary>
    LosslessFromDecode
}

public static class DcSongWriteWarnings
{
    /// <summary>The resx key naming <paramref name="warning" /> (a computed key family).</summary>
    public static string ResourceKey(DcSongWriteWarning warning) => $"DcSong_WriteWarning_{warning}";
}
