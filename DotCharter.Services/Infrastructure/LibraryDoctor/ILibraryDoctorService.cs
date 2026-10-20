using DotCharter.Services.Library;


namespace DotCharter.Services.Infrastructure.LibraryDoctor;

/// <summary>
/// Batch health check over an already-scanned PSARC library. Consumes the
/// song-browser data source (the list the library index produced) read-only
/// and returns a structured <see cref="LibraryDoctorReport"/>.
/// </summary>
public interface ILibraryDoctorService
{
    /// <summary>
    /// Runs every rule over <paramref name="songs"/>. Each distinct package is
    /// probed once (readability + audio presence) on a background thread;
    /// <paramref name="progress"/> ticks per package and
    /// <paramref name="cancellationToken"/> aborts a long scan.
    /// </summary>
    Task<LibraryDoctorReport> RunAsync(
        IReadOnlyList<LibrarySongEntry> songs,
        IProgress<LibraryDoctorProgress>? progress = null,
        CancellationToken cancellationToken = default);
}
