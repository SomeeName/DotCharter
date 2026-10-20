namespace DotCharter.Services.Library.Prefetch;

/// <summary>
/// Produces a library song's playable audio ahead of Open — the same
///     extract + convert Open runs, under the same per-folder gate, so the two
///     never write one cache folder at once. <see cref="LibraryService" />
///     implements it through the source that owns the song
///     (<see cref="ILibraryPackageSource.PrepareAudio" />).
/// </summary>
public interface ILibraryAudioPreparer
{
    /// <summary>Blocking; call it off the UI thread. Throws
    /// <see cref="OperationCanceledException" /> when <paramref name="cancellationToken" />
    /// fires between stages, leaving nothing half-written.</summary>
    LibraryAudioPrepareOutcome PrepareAudio(LibrarySongEntry song, CancellationToken cancellationToken);
}

public enum LibraryAudioPrepareOutcome
{
    /// <summary>This session already produced the song's audio and it is still fresh; nothing was opened.</summary>
    AlreadyPrepared,

    /// <summary>The archive was opened and the song's playable file is now on disk (found fresh or converted).</summary>
    Prepared,

    /// <summary>The song has no resolvable audio, or every decoder failed.</summary>
    NoAudio
}
