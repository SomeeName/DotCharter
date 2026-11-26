using DotCharter.Services.Library;

namespace DotCharter.Tests.UI.ViewModels.Panels;

/// <summary>A package reader for song-browser tests that never open a package file: it reports no songs.</summary>
internal sealed class StubPackageFileReader : ILibraryPackageFileReader
{
    public Task<IReadOnlyList<LibrarySongEntry>> ReadPackageAsync(string path, CancellationToken cancellationToken = default)
        => Task.FromResult<IReadOnlyList<LibrarySongEntry>>([]);
}
