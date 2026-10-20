using DotCharter.Services.Library;
using DotCharter.Services.Library.Cache;
using DotCharter.Services.Library.Index;
using DotCharter.Services.Package.DcSong;

namespace DotCharter.Tests.Services.Library;

/// <summary>
///     The library the public app composes (#886, #889): one <see cref="LibraryService" />
///     over the <c>.dcsong</c> source, with its audio cache and index — the shape
///     <c>App.EditingServices</c> registers when no plugin adds a package source ahead of it.
/// </summary>
internal static class TestLibraries
{
    public static LibraryService Create(
        IAppLogger logger,
        ILibraryIndex? libraryIndex = null,
        LibraryAudioCacheStore? audioCache = null,
        DcSongLibrarySource? packages = null) =>
        new(logger, Sources(logger, audioCache, packages), libraryIndex);

    /// <summary>The public app's sources: <c>.dcsong</c>.</summary>
    public static ILibraryPackageSource[] Sources(
        IAppLogger logger,
        LibraryAudioCacheStore? audioCache = null,
        DcSongLibrarySource? packages = null) =>
    [
        // The fallback store never sweeps on its own, as the app's own fallback.
        packages ?? new DcSongLibrarySource(logger, audioCache ?? new LibraryAudioCacheStore(logger),
            configuredFfmpegPath: static () => null)
    ];
}
