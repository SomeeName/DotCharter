using DotCharter.Localization;
using DotCharter.Services.Audio.Lyrics;

namespace DotCharter.Services.Ai.Match.Model;

/// <summary>Downloads the trained rig-estimator model on demand.</summary>
public interface IRigEstimatorModelDownloadService
{
    /// <summary>
    ///     The folder the estimator loads from — the same path
    ///     <see cref="RigEstimatorOnnx.DefaultModelDirectory" /> names. Exposed
    ///     so the settings page can offer an "open folder" link that points at
    ///     the place a hand-placed export also has to go.
    /// </summary>
    string ModelDirectory { get; }

    /// <summary>True when the model files are on disk.</summary>
    /// <remarks>
    ///     A file-presence check only. Whether the model LOADS is
    ///     <c>IRigEstimator.IsAvailable</c>, and the two can legitimately
    ///     disagree — a sidecar from a different label space is installed and
    ///     unusable. The settings page shows both.
    /// </remarks>
    bool IsInstalled { get; }

    /// <summary>What the installed model says about itself, or null when none is.</summary>
    RigEstimatorModelInfo? InstalledModel { get; }

    /// <summary>Approximate download size in MB, for the offer text.</summary>
    int ApproximateMegabytes { get; }

    /// <summary>
    ///     Ensures the model is present, fetching it if it is not. Returns
    ///     whether it is usable afterwards and, when it is not, why.
    /// </summary>
    /// <param name="progress">Optional progress text for the UI.</param>
    /// <param name="ct">Cancels the download.</param>
    /// <returns>The outcome.</returns>
    Task<RigEstimatorModelInstallResult> EnsureInstalledAsync(
        IProgress<string>? progress = null, CancellationToken ct = default);

    // ---- the tone library -----------------------------------------
    // The pre-rendered retrieval library for the bundled DI: the same shape as the
    // model — one release asset, one folder, fetched on demand — and the reason the
    // first "Match from audio" does not render 4,000 rigs in front of the user.

    /// <summary>The installed library FILE (present or not). The matcher's
    /// provider reads this path on every match, so a download that lands mid-session
    /// is picked up by the next one.</summary>
    string LibraryPath { get; }

    /// <summary>True when the library file is on disk.</summary>
    bool IsLibraryInstalled { get; }

    /// <summary>Approximate download size in MB, for the offer text.</summary>
    int LibraryApproximateMegabytes { get; }

    /// <summary>The installed library's size in MB, 0 when it is absent or
    /// unreadable. The size is decoration on a status row, so an I/O failure
    /// reports zero rather than throwing at whoever is drawing it — and the read
    /// lives here because a presentation layer may not touch the disk.</summary>
    int InstalledLibraryMegabytes { get; }

    /// <summary>Ensures the library is present, fetching it if it is not.</summary>
    /// <param name="progress">Optional progress text for the UI.</param>
    /// <param name="ct">Cancels the download.</param>
    /// <returns>The outcome.</returns>
    Task<RigEstimatorModelInstallResult> EnsureLibraryInstalledAsync(
        IProgress<string>? progress = null, CancellationToken ct = default);
}

/// <summary>
///     Ties the release-asset fetcher to <see cref="RigEstimatorModelInstaller" />.
///     <para>
///         <b>The fetcher is the lyric lane's, reused rather than forked.</b>
///         <see cref="LyricModelAssetFetcher" /> is a GitHub-release asset
///         reader with no lyric-specific behaviour in it, and the one piece of
///         hard-won knowledge it carries — that the scan must PAGE (5 x 100)
///         because a model asset sits on a release a hundred behind HEAD —
///         would be silently missing from any copy. A second copy would have
///         re-broken the download the same way and read as "the model is not
///         available". Its name is the only thing lyric-specific about it; if
///         it is ever renamed, rename it rather than duplicating it.
///     </para>
///     <para>
///         There is exactly ONE model, so this is a much thinner service than
///         the lyric one: no per-sheet routing, no required/optional asymmetry.
///         It exists at all because the settings page and the tone panel both
///         need the same "is it there, can I get it, how big is it" surface,
///         and neither should be constructing an installer of its own.
///     </para>
/// </summary>
/// <param name="fetcher">Resolves and streams the release asset. Owned: disposed with this service.</param>
/// <param name="localization">Resolves the status and progress text the user reads.</param>
/// <param name="logger">Optional log sink.</param>
/// <param name="modelRoot">Overrides the catalogue root; tests pass a temp folder.</param>
/// <param name="download">
///     Overrides the catalogue entry. Null (the product) means
///     <see cref="RigEstimatorModelCatalog.Download" />, whose SHA-256 is the
///     shipped archive's — so a test that serves a synthetic archive passes an
///     entry with an empty hash (the size-checked path) or the archive's own
///     digest, instead of asking the installer to accept a fake as the real thing.
/// </param>
/// <param name="library">Overrides the library entry the same way; null means
/// <see cref="RigEstimatorModelCatalog.Library" />.</param>
public sealed class RigEstimatorModelDownloadService(
    LyricModelAssetFetcher fetcher,
    ILocalizationService localization,
    IAppLogger? logger = null,
    string? modelRoot = null,
    RigEstimatorModelDownload? download = null,
    RigEstimatorModelDownload? library = null) : IRigEstimatorModelDownloadService, IDisposable
{
    private readonly RigEstimatorModelInstaller _installer = new(fetcher.FetchAsync, localization, logger, modelRoot);
    private readonly RigEstimatorModelDownload _download = download ?? RigEstimatorModelCatalog.Download;
    private readonly RigEstimatorModelDownload _library = library ?? RigEstimatorModelCatalog.Library;

    /// <inheritdoc />
    public string ModelDirectory => _installer.TargetDirectory(RigEstimatorModelCatalog.RigEstimatorId);

    /// <inheritdoc />
    public bool IsInstalled => _installer.IsInstalled(RigEstimatorModelCatalog.RigEstimatorId);

    /// <inheritdoc />
    public RigEstimatorModelInfo? InstalledModel =>
        _installer.DescribeInstalled(RigEstimatorModelCatalog.RigEstimatorId);

    /// <inheritdoc />
    public int ApproximateMegabytes =>
        (int)(_download.ApproximateBytes / (1024 * 1024));

    /// <inheritdoc />
    public Task<RigEstimatorModelInstallResult> EnsureInstalledAsync(
        IProgress<string>? progress = null, CancellationToken ct = default) =>
        _installer.EnsureInstalledAsync(_download, progress, ct);

    /// <inheritdoc />
    public string LibraryPath => Path.Combine(_installer.TargetDirectory(_library.Id), _library.Files[0]);

    /// <inheritdoc />
    public bool IsLibraryInstalled => _installer.IsInstalled(_library.Id, _library.Files);

    /// <inheritdoc />
    public int LibraryApproximateMegabytes => (int)(_library.ApproximateBytes / (1024 * 1024));

    /// <inheritdoc />
    public int InstalledLibraryMegabytes
    {
        get
        {
            try { return (int)(new FileInfo(LibraryPath).Length / (1024 * 1024)); }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { return 0; }
        }
    }

    /// <inheritdoc />
    public Task<RigEstimatorModelInstallResult> EnsureLibraryInstalledAsync(
        IProgress<string>? progress = null, CancellationToken ct = default) =>
        _installer.EnsureInstalledAsync(_library, progress, ct);

    /// <summary>Disposes the owned fetcher.</summary>
    public void Dispose() => fetcher.Dispose();
}
