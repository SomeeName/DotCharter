namespace DotCharter.Services.Library;

/// <summary>Why a library song the scan read cannot be played on this computer.</summary>
public enum LibrarySongBlock
{
    /// <summary>Nothing stops it.</summary>
    None,

    /// <summary>A <c>.dcsong</c> with Opus audio, and no ffmpeg to decode it: the audio
    /// engine does not decode Opus.</summary>
    AudioDecoderMissing
}

/// <summary>
///     What the Library asks before it offers a song: a capability the app cannot honour
///     must never look live, so a row it cannot play is shown disabled with its reason.
///     Answers from state probed OFF the UI thread (each scan probes again), so a row
///     can ask while it is built.
/// </summary>
public interface ILibrarySongPlayability
{
    LibrarySongBlock BlockFor(LibrarySongEntry song);
}
