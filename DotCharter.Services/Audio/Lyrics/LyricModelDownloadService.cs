using DotCharter.Analysis.Lyrics;
using DotCharter.Localization;

namespace DotCharter.Services.Audio.Lyrics;

/// <summary>Downloads the acoustic models lyric auto-sync needs, on demand.</summary>
public interface ILyricModelDownloadService
{
    /// <summary>True when the multilingual model — the one auto-sync cannot run without — is present.</summary>
    bool IsRequiredModelInstalled { get; }

    /// <summary>Approximate download size, MB, for what <see cref="EnsureModelsForAsync" /> would fetch.</summary>
    int PendingMegabytesFor(LyricSheet sheet);

    /// <summary>
    ///     Ensures the models this SHEET needs are present, fetching what is
    ///     missing. Returns true when auto-sync can run afterwards.
    /// </summary>
    Task<bool> EnsureModelsForAsync(
        LyricSheet sheet, IProgress<string>? progress = null, CancellationToken ct = default);
}

/// <summary>
///     Ties <see cref="LyricModelAssetFetcher" /> to <see cref="LyricModelInstaller" />.
///     <para>
///         <b>What gets fetched depends on the sheet, and the two models are not
///         equal.</b> The multilingual model is REQUIRED — without it auto-sync
///         cannot run at all. The English head is an OPTIONAL improvement, so it is
///         only fetched when the router says this sheet is English, and a failure
///         to get it is not a failure of the operation: alignment proceeds on the
///         multilingual model, a little less accurately. That asymmetry is the
///         whole reason this is a service and not one <c>EnsureInstalled</c> call.
///     </para>
/// </summary>
public sealed class LyricModelDownloadService(
    LyricModelAssetFetcher fetcher,
    ILocalizationService localization,
    IAppLogger? logger = null,
    IReadOnlyList<string>? modelRoots = null) : ILyricModelDownloadService, IDisposable
{
    private readonly LyricModelInstaller _installer = new(fetcher.FetchAsync, localization, logger, modelRoots);

    public bool IsRequiredModelInstalled =>
        _installer.IsInstalled(LyricModelCatalog.MultilingualId);

    public int PendingMegabytesFor(LyricSheet sheet)
    {
        ArgumentNullException.ThrowIfNull(sheet);

        var bytes = 0L;
        if (!IsRequiredModelInstalled) bytes += LyricModelCatalog.MultilingualDownload.ApproximateBytes;
        if (WantsEnglish(sheet) && !_installer.IsInstalled(LyricModelCatalog.EnglishId))
            bytes += LyricModelCatalog.EnglishDownload.ApproximateBytes;

        return (int)(bytes / (1024 * 1024));
    }

    public async Task<bool> EnsureModelsForAsync(
        LyricSheet sheet, IProgress<string>? progress = null, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(sheet);

        if (!IsRequiredModelInstalled &&
            !await _installer.EnsureInstalledAsync(
                LyricModelCatalog.MultilingualDownload, progress, ct).ConfigureAwait(false))
        {
            return false;
        }

        // Optional: a failure here costs accuracy, not the operation.
        if (WantsEnglish(sheet) && !_installer.IsInstalled(LyricModelCatalog.EnglishId))
        {
            await _installer.EnsureInstalledAsync(
                LyricModelCatalog.EnglishDownload, progress, ct).ConfigureAwait(false);
        }

        return IsRequiredModelInstalled;
    }

    private static bool WantsEnglish(LyricSheet sheet) => LyricScriptDetector.IsLikelyEnglish(sheet);

    public void Dispose() => fetcher.Dispose();
}
