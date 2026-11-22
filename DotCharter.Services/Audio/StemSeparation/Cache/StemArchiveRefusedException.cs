namespace DotCharter.Services.Audio.StemSeparation.Cache;

/// <summary>
/// A <c>.dcstems</c> archive was refused because expanding it would be unsafe
/// (an entry escapes the destination, too many entries, or too many inflated
/// bytes). Deliberately NOT a <see cref="StemArchiveCorruptException"/>: stems are
/// never regenerated, so the coordinator keeps the archive and reports a miss.
/// </summary>
public sealed class StemArchiveRefusedException : Exception
{
    public StemArchiveRefusedException(string message) : base(message) { }
}
