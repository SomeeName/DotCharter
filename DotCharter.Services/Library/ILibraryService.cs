namespace DotCharter.Services.Library;

public interface ILibraryService
{
    Task<IReadOnlyList<LibrarySongEntry>> ScanAsync(string rootPath);

    /// <summary>
    /// Streaming scan of a PSARC library tree. Reports incremental progress
    /// via <paramref name="progress"/> and honors <paramref name="cancellationToken"/>
    /// so the UI can display a progress bar AND let the user abort a long
    /// scan over network shares or huge libraries.
    /// </summary>
    Task<IReadOnlyList<LibrarySongEntry>> ScanAsync(
        string rootPath,
        IProgress<LibraryScanProgress>? progress,
        CancellationToken cancellationToken);

    /// <summary>
    /// Streaming scan that also reports each batch of songs the moment its
    /// containing PSARC has been parsed via <paramref name="songFound"/>. The
    /// UI can render those songs immediately instead of waiting for the whole
    /// library to finish. The final returned list is fully sorted; callers
    /// that displayed incremental results should re-sort/replace the bound
    /// collection once the task completes.
    /// </summary>
    Task<IReadOnlyList<LibrarySongEntry>> ScanAsync(
        string rootPath,
        IProgress<LibraryScanProgress>? progress,
        IProgress<IReadOnlyList<LibrarySongEntry>>? songFound,
        CancellationToken cancellationToken);

    Task<LibraryLoadedSong> LoadSongAsync(LibrarySongEntry song);
    Task<string> LoadCoverImageAsync(LibrarySongEntry song);
}
