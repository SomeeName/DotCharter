namespace DotCharter.Services.Library.Prefetch;

/// <summary>
/// Convert-on-select: the library tells it where the selection is, and
///     once the selection settles the song's audio is extracted and converted in
///     the background, so the Open that usually follows is a cache hit.
/// </summary>
public interface ILibraryAudioPrefetcher
{
    /// <summary>The selection moved to <paramref name="song" /> (null: nothing
    /// loadable is selected). Returns at once; call it from any thread.</summary>
    void SelectionChanged(LibrarySongEntry? song);
}
