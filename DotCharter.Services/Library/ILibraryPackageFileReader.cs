namespace DotCharter.Services.Library;

/// <summary>
///     Reads ONE package file that is not (or not yet) in the Library folder — a
///     dropped or File-opened <c>.psarc</c> — into the same rows a Library scan
///     would build for it, through the source that owns its extension. Nothing is
///     copied, indexed or added to the Library.
/// </summary>
public interface ILibraryPackageFileReader
{
    /// <summary>The package's songs, read off the calling thread. Faults with the
    /// source's own error for a package it cannot read, and with
    /// <see cref="NotSupportedException" /> when no source reads the extension.</summary>
    Task<IReadOnlyList<LibrarySongEntry>> ReadPackageAsync(string path, CancellationToken cancellationToken = default);
}
