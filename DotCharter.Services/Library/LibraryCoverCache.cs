using System.Globalization;
using DotCharter.Services.Library.Cache;

namespace DotCharter.Services.Library;

/// <summary>
///     Where a library song's decoded cover PNG is cached, for every package source:
///     one file per package + song key under the app's writable cache. The index and
///     each <see cref="ILibraryPackageSource" /> read it back without opening the
///     package again.
/// </summary>
internal static class LibraryCoverCache
{
    internal static string AlbumArtCacheDirectory()
    {
        var localAppData = UserDataFolders.LocalAppData;
        if (string.IsNullOrWhiteSpace(localAppData)) localAppData = Path.GetTempPath();
        return Path.Combine(localAppData, "DotCharter", "AlbumArtCache");
    }

    /// <summary>
    ///     The cache-file stem for one song's cover, deriving the SAME three
    ///     components as <see cref="LibraryAudioCacheStore.DirectoryNameFor" /> —
    ///     file name, a hash of the FULL path, and the archive's last-write
    ///     ticks — plus the song key, because one archive can hold many songs.
    ///     <para>
    ///         It is not file name + key only, because both halves cost a wrong
    ///         picture. `LibraryService.LoadCoverImageAsync` returns the cached PNG the moment
    ///         `IsUsablePng` accepts it, WITHOUT opening the archive, so nothing
    ///         ever revalidated it: a re-downloaded or rebuilt CDLC kept its old
    ///         cover forever, even though the library index had correctly
    ///         invalidated its row on the same mtime the audio cache keys by.
    ///         And two archives with the same FILE NAME in different folders
    ///         carrying the same song key — the "misnamed clone of
    ///         a package" case <see cref="LibraryDedupe" />'s
    ///         own comment says real libraries contain — collided on one PNG, so
    ///         one package showed the other's art.
    ///     </para>
    /// </summary>
    internal static string AlbumArtCacheStem(string psarcPath, string songKey)
    {
        var psarcName = SafeFileName.Make(Path.GetFileNameWithoutExtension(psarcPath));
        var safeKey = SafeFileName.Make(songKey);

        string fullPath;
        try
        {
            fullPath = Path.GetFullPath(psarcPath);
        }
        catch
        {
            fullPath = psarcPath;
        }

        var pathHash = Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(
            System.Text.Encoding.UTF8.GetBytes(fullPath.ToUpperInvariant())))[..16];
        var timestamp = File.Exists(psarcPath)
            ? File.GetLastWriteTimeUtc(psarcPath).Ticks.ToString(CultureInfo.InvariantCulture)
            : "unknown";

        return $"{psarcName}_{pathHash}_{timestamp}_{safeKey}_art";
    }

    internal static string GetCachedAlbumArtPngPath(string psarcPath, string songKey) => Path.Combine(AlbumArtCacheDirectory(), AlbumArtCacheStem(psarcPath, songKey) + ".png");
}
