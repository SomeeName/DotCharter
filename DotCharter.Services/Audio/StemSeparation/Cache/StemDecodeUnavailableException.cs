namespace DotCharter.Services.Audio.StemSeparation.Cache;

/// <summary>
/// Thrown when a cached stem cannot be decoded for ENVIRONMENTAL reasons —
/// ffmpeg missing from PATH/tools, the decode process failing (locked output,
/// disk full), etc. The archive itself is presumed valid.
///
/// <see cref="StemCacheCoordinator.TryLoadCachedAsync"/> treats this as a plain
/// cache miss and — unlike genuine corruption — must NOT delete the durable
/// never-regenerate archive: the same archive decodes fine once the tool is back.
/// </summary>
public sealed class StemDecodeUnavailableException : Exception
{
    public StemDecodeUnavailableException(string message) : base(message) { }

    public StemDecodeUnavailableException(string message, Exception innerException)
        : base(message, innerException) { }
}
