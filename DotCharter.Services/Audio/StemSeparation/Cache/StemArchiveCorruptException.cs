namespace DotCharter.Services.Audio.StemSeparation.Cache;

/// <summary>
/// Thrown when the <c>.dcstems</c> ARCHIVE ITSELF is bad — a truncated write, a
/// half-copied cache directory, a manifest that read once and now fails.
///
/// This is the OPPOSITE pole from <see cref="StemDecodeUnavailableException"/>,
/// and the distinction is the whole point: stems are never regenerated, so
/// <see cref="StemCacheCoordinator.TryLoadCachedAsync"/> may only delete an
/// archive it can PROVE is unusable. Anything environmental (disk full, an ACL,
/// a destination WAV still open in the mixer) is a plain miss that KEEPS the
/// archive, because the same bytes expand fine once the environment recovers.
/// </summary>
public sealed class StemArchiveCorruptException : Exception
{
    public StemArchiveCorruptException(string message) : base(message) { }

    public StemArchiveCorruptException(string message, Exception innerException)
        : base(message, innerException) { }
}
