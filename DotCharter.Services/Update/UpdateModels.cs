namespace DotCharter.Services.Update;

/// <summary>
/// A newer release discovered on GitHub. <see cref="AssetApiUrl"/> is the
/// api.github.com asset endpoint (works for private repos with a token via
/// <c>Accept: application/octet-stream</c>); <see cref="AssetBrowserUrl"/>
/// is the public browser_download_url kept as a fallback.
/// </summary>
/// <param name="Version">The release's version, parsed from its tag.</param>
/// <param name="TagName">The release tag, e.g. "v0.41.0".</param>
/// <param name="ReleaseNotes">The release body (markdown).</param>
/// <param name="ReleasePageUrl">Browser URL of the release page.</param>
/// <param name="AssetName">File name of the asset to download.</param>
/// <param name="AssetApiUrl">api.github.com asset endpoint (works for private repos).</param>
/// <param name="AssetBrowserUrl">Public browser_download_url, kept as a fallback.</param>
/// <param name="AssetSizeBytes">Size of the FULL asset, and the length a range fetch addresses.</param>
/// <param name="ManifestAssetApiUrl">
///     api.github.com url of the release's <c>payload-manifest.json</c> asset, or
///     null when the release publishes none.
///     <para>
///         <b>This is what makes a partial download possible.</b> The manifest is a
///         SHA-256 per payload file (66 KB), so the updater can work out which
///         files this install is missing and range-fetch only those out of the one
///         full zip — no patch assets, and it works from ANY prior version. A
///         release without one (older releases) simply takes the full
///         download; see <see cref="PartialUpdateDownloader" />.
///     </para>
/// </param>
public sealed record UpdateInfo(
    Version Version,
    string TagName,
    string ReleaseNotes,
    string ReleasePageUrl,
    string AssetName,
    string AssetApiUrl,
    string AssetBrowserUrl,
    long AssetSizeBytes,
    string? ManifestAssetApiUrl = null);

public enum UpdateCheckStatus
{
    UpdateAvailable,
    UpToDate,
    Failed,

    /// <summary>The feed is private (<see cref="IUpdateFeed.RequiresSignIn" />) and no GitHub
    /// sign-in is stored, so it was not asked. The user is told to sign in.</summary>
    SignInRequired
}

public sealed record UpdateCheckResult(
    UpdateCheckStatus Status,
    UpdateInfo? Update = null,
    string? Error = null);

/// <summary>
/// Why an in-place apply is not possible. The UI maps these to localized
/// messages; the service itself never attempts elevation.
/// </summary>
public enum UpdateApplyBlockReason
{
    None,

    /// <summary>The running layout doesn't look like a release install
    /// (no DotCharter.exe next to the app base directory — e.g. running
    /// from `dotnet run` / bin/Debug with a different host).</summary>
    NotAReleaseInstall,

    /// <summary>The install directory is not writable (Program Files
    /// without elevation, network share, …).</summary>
    InstallDirNotWritable,

    /// <summary>DotCharter.runtimeconfig.json is missing or doesn't describe a
    /// known runtime layout, so we cannot tell which payloads are safe to copy
    /// over this install. Applying one anyway risks an unbootable mix of
    /// runtime files (see <see cref="AppPayload" />).</summary>
    UnknownInstallLayout
}
